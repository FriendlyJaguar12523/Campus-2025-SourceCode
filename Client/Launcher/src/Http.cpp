#include "Http.h"
#include "Util.h"
#include "json.hpp"
#include <windows.h>
#include <winhttp.h>
#include <filesystem>
#include <fstream>
#include <random>
#include <thread>

std::string HttpResult::Message() const
{
    if (!error.empty()) return error;
    try
    {
        auto j = nlohmann::json::parse(body);
        if (j.contains("error") && j["error"].is_string()) return j["error"].get<std::string>();
    }
    catch (...) {}
    if (status == 401) return "Sign in again.";
    if (status == 403) return "Not allowed.";
    if (status == 413) return "Too big to upload.";
    return "The server answered " + std::to_string(status) + ".";
}

namespace
{
struct Handle
{
    HINTERNET h = nullptr;
    Handle() = default;
    explicit Handle(HINTERNET x) : h(x) {}
    ~Handle() { if (h) WinHttpCloseHandle(h); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    operator HINTERNET() const { return h; }
};

// WinHTTP tells us WHY it refused a certificate (a status callback with flags) - the bare "secure failure"
// error says nothing, and the usual causes are on the player's PC, not ours.
void CALLBACK SecureFailureCb(HINTERNET, DWORD_PTR ctx, DWORD status, LPVOID info, DWORD len)
{
    if (status == WINHTTP_CALLBACK_STATUS_SECURE_FAILURE && ctx && info && len >= sizeof(DWORD))
        *reinterpret_cast<DWORD*>(ctx) = *static_cast<DWORD*>(info);
}

std::string CertReason(DWORD f)
{
    char hex[16]; snprintf(hex, sizeof(hex), "0x%X", (unsigned)f);
    std::string code = std::string(" (") + hex + ")";
    if (f & WINHTTP_CALLBACK_STATUS_FLAG_CERT_DATE_INVALID)
        return "The server's certificate looks expired or not valid yet - check that this PC's date, time and time zone are right." + code;
    if (f & WINHTTP_CALLBACK_STATUS_FLAG_CERT_CN_INVALID)
        return "The certificate is for a different address - a VPN, proxy or antivirus is probably intercepting the connection." + code;
    if (f & WINHTTP_CALLBACK_STATUS_FLAG_INVALID_CA)
        return "This PC doesn't trust the certificate's authority - run Windows Update, or turn off antivirus HTTPS scanning." + code;
    if (f & WINHTTP_CALLBACK_STATUS_FLAG_CERT_REV_FAILED)
        return "This PC couldn't check whether the certificate was revoked - a firewall may be blocking it." + code;
    if (f & WINHTTP_CALLBACK_STATUS_FLAG_CERT_REVOKED)
        return "The server's certificate was revoked." + code;
    if (f & WINHTTP_CALLBACK_STATUS_FLAG_SECURITY_CHANNEL_ERROR)
        return "The secure connection failed - an old Windows version, antivirus or a proxy may be getting in the way." + code;
    return "The server's security certificate was refused." + code;
}

std::string LastError(const char* what, DWORD secFlags = 0)
{
    DWORD e = GetLastError();
    if (e == ERROR_WINHTTP_CANNOT_CONNECT || e == ERROR_WINHTTP_NAME_NOT_RESOLVED)
        return "Can't reach the server - check your internet connection.";
    if (e == ERROR_WINHTTP_TIMEOUT) return "The server took too long to answer.";
    if (e == ERROR_WINHTTP_SECURE_FAILURE) return CertReason(secFlags);
    return std::string(what) + " failed (" + std::to_string(e) + ").";
}

// body: either `data` in memory, or streamed from `parts` (memory chunks and files) for uploads
struct Part { std::string mem; std::string file; uint64_t size = 0; };

// Downloads go straight to a file. resumeFrom > 0 asks for the rest (Range) and appends when the
// server answers 206; a 200 starts the file over. cancel stops the transfer (the partial file stays).
struct FileSink { std::string path; uint64_t resumeFrom = 0; std::atomic<bool>* cancel = nullptr; };

HttpResult Send(const std::string& method, const std::string& url, const std::string& token, const std::string& contentType,
                const std::vector<Part>& parts, std::atomic<uint64_t>* sent, std::atomic<uint64_t>* total,
                const FileSink* sink = nullptr)
{
    HttpResult r;
    std::wstring wurl = Widen(url);
    URL_COMPONENTS uc{ sizeof(uc) };
    wchar_t host[256] = {}, path[2048] = {};
    uc.lpszHostName = host; uc.dwHostNameLength = 256;
    uc.lpszUrlPath = path; uc.dwUrlPathLength = 2048;
    wchar_t extra[1024] = {};
    uc.lpszExtraInfo = extra; uc.dwExtraInfoLength = 1024;
    if (!WinHttpCrackUrl(wurl.c_str(), 0, 0, &uc)) { r.error = "Bad server address: " + url; return r; }
    std::wstring fullPath = std::wstring(path) + extra;

    Handle session(WinHttpOpen(L"CampusLauncher/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
    if (!session) { r.error = LastError("WinHttpOpen"); return r; }
    WinHttpSetTimeouts(session, 15000, 15000, 300000, 300000);
    Handle conn(WinHttpConnect(session, host, uc.nPort, 0));
    if (!conn) { r.error = LastError("Connect"); return r; }
    DWORD flags = uc.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    Handle req(WinHttpOpenRequest(conn, Widen(method).c_str(), fullPath.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags));
    if (!req) { r.error = LastError("OpenRequest"); return r; }
    DWORD secFlags = 0;
    WinHttpSetStatusCallback(req, SecureFailureCb, WINHTTP_CALLBACK_FLAG_SECURE_FAILURE, 0);

    std::wstring headers;
    if (!token.empty()) headers += L"Authorization: Bearer " + Widen(token) + L"\r\n";
    if (!contentType.empty()) headers += L"Content-Type: " + Widen(contentType) + L"\r\n";
    if (sink && sink->resumeFrom > 0) headers += L"Range: bytes=" + std::to_wstring(sink->resumeFrom) + L"-\r\n";
    uint64_t len = 0;
    for (auto& p : parts) len += p.size;
    if (total) *total = len;
    if (sent) *sent = 0;

    if (!WinHttpSendRequest(req, headers.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : headers.c_str(), (DWORD)-1L,
                            WINHTTP_NO_REQUEST_DATA, 0, (DWORD)len, (DWORD_PTR)&secFlags))
    { r.error = LastError("Send", secFlags); return r; }

    std::vector<char> buf(256 * 1024);
    for (auto& p : parts)
    {
        if (p.file.empty())
        {
            size_t off = 0;
            while (off < p.mem.size())
            {
                DWORD n = (DWORD)std::min<size_t>(buf.size(), p.mem.size() - off), w = 0;
                if (!WinHttpWriteData(req, p.mem.data() + off, n, &w)) { r.error = LastError("Upload", secFlags); return r; }
                off += w; if (sent) *sent += w;
            }
        }
        else
        {
            std::ifstream in(U8Path(p.file), std::ios::binary);
            if (!in) { r.error = "Can't read " + p.file; return r; }
            while (in)
            {
                in.read(buf.data(), buf.size());
                DWORD n = (DWORD)in.gcount(), w = 0;
                if (n == 0) break;
                if (!WinHttpWriteData(req, buf.data(), n, &w)) { r.error = LastError("Upload", secFlags); return r; }
                if (sent) *sent += w;
            }
        }
    }
    if (!WinHttpReceiveResponse(req, nullptr)) { r.error = LastError("Receive", secFlags); return r; }
    DWORD status = 0, sz = sizeof(status);
    WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &sz, WINHTTP_NO_HEADER_INDEX);
    r.status = (int)status;
    // Downloads (sink): a 200 (or a 206 continuing a partial file) goes to the file, with `sent` /
    // `total` as the progress over the WHOLE file.
    std::ofstream file;
    bool resumed = sink && status == 206 && sink->resumeFrom > 0;
    bool toSink = sink && (status == 200 || resumed);
    if (toSink)
    {
        uint64_t base = resumed ? sink->resumeFrom : 0;
        file.open(U8Path(sink->path), std::ios::binary | (resumed ? std::ios::app : std::ios::trunc));
        if (!file) { r.error = "Can't write " + sink->path; return r; }
        wchar_t lenBuf[32] = {}; DWORD lenSz = sizeof(lenBuf);
        if (total && WinHttpQueryHeaders(req, WINHTTP_QUERY_CONTENT_LENGTH, WINHTTP_HEADER_NAME_BY_INDEX, lenBuf, &lenSz, WINHTTP_NO_HEADER_INDEX))
            *total = base + _wcstoui64(lenBuf, nullptr, 10);
        if (sent) *sent = base;
    }
    for (;;)
    {
        if (toSink && sink->cancel && sink->cancel->load()) { r.error = "cancelled"; break; }
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(req, &avail)) { if (toSink) r.error = LastError("Download", secFlags); break; }
        if (avail == 0) break;
        std::string chunk(avail, '\0');
        DWORD got = 0;
        if (!WinHttpReadData(req, chunk.data(), avail, &got)) { if (toSink) r.error = LastError("Download", secFlags); break; }
        if (got == 0) break;
        if (toSink)
        {
            file.write(chunk.data(), got);
            if (!file) { r.error = "Couldn't save the download - is the disk full?"; break; }
            if (sent) *sent += got;
            continue;
        }
        r.body.append(chunk.data(), got);
        if (r.body.size() > 64u * 1024 * 1024) break;
    }
    if (resumed) r.status = 200;     // to the caller a finished resume is a whole file
    return r;
}
}

HttpResult HttpDownload(const std::string& url, const std::string& token, const std::string& path,
                        std::atomic<uint64_t>* got, std::atomic<uint64_t>* total)
{
    FileSink f; f.path = path;
    return Send("GET", url, token, "", {}, got, total, &f);
}

HttpResult HttpDownloadResume(const std::string& url, const std::string& token, const std::string& path,
                              std::atomic<uint64_t>* got, std::atomic<uint64_t>* total, std::atomic<bool>* cancel)
{
    FileSink f; f.path = path; f.cancel = cancel;
    std::error_code ec;
    if (std::filesystem::is_regular_file(U8Path(path), ec)) f.resumeFrom = std::filesystem::file_size(U8Path(path), ec);
    HttpResult r = Send("GET", url, token, "", {}, got, total, &f);
    if (r.status == 416 && f.resumeFrom > 0)        // already have it all (or a stale bigger file): the caller's checksum decides
    { r.status = 200; r.error.clear(); if (total) *total = f.resumeFrom; if (got) *got = f.resumeFrom; }
    return r;
}

HttpResult HttpGet(const std::string& url, const std::string& token)
{
    return Send("GET", url, token, "", {}, nullptr, nullptr);
}

HttpResult HttpPostJson(const std::string& url, const std::string& token, const std::string& json)
{
    Part p; p.mem = json; p.size = json.size();
    return Send("POST", url, token, "application/json", { p }, nullptr, nullptr);
}

HttpResult HttpPostMultipart(const std::string& url, const std::string& token,
                             const std::vector<std::pair<std::string, std::string>>& fields,
                             const std::vector<HttpFile>& files, std::atomic<uint64_t>* sent, std::atomic<uint64_t>* total)
{
    std::mt19937_64 rng{ std::random_device{}() };
    std::string boundary = "----CampusStudio" + std::to_string(rng());
    std::vector<Part> parts;
    auto mem = [&](std::string s) { Part p; p.size = s.size(); p.mem = std::move(s); parts.push_back(std::move(p)); };
    for (auto& [k, v] : fields)
        mem("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + k + "\"\r\n\r\n" + v + "\r\n");
    for (auto& f : files)
    {
        std::error_code ec;
        uint64_t size = std::filesystem::file_size(U8Path(f.path), ec);
        if (ec) { HttpResult r; r.error = "Missing file: " + f.path; return r; }
        mem("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + f.field + "\"; filename=\"" + f.filename +
            "\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        Part p; p.file = f.path; p.size = size; parts.push_back(p);
        mem("\r\n");
    }
    mem("--" + boundary + "--\r\n");
    return Send("POST", url, token, "multipart/form-data; boundary=" + boundary, parts, sent, total);
}

std::shared_ptr<NetJob> RunNet(std::function<HttpResult(NetJob&)> fn)
{
    auto job = std::make_shared<NetJob>();
    std::thread([job, fn]() {
        HttpResult r;
        try { r = fn(*job); }
        catch (std::exception& e) { r.error = e.what(); }
        job->result = std::move(r);
        job->done = true;
    }).detach();
    return job;
}
