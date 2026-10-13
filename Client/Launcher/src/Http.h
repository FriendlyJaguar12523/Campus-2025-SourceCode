#pragma once
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <utility>
#include <vector>

// Minimal HTTPS client on WinHTTP (built into Windows - no extra DLLs).
struct HttpResult
{
    int status = 0;
    std::string body;
    std::string error;                     // transport error (no connection, TLS...)
    bool Ok() const { return error.empty() && status >= 200 && status < 300; }
    std::string Message() const;           // the server's {"error": "..."} or a readable fallback
};

struct HttpFile { std::string field, filename, path; };

HttpResult HttpGet(const std::string& url, const std::string& token);
// GET straight to a file (a 200 body only; an error body stays in `body`). `got` / `total` = progress.
HttpResult HttpDownload(const std::string& url, const std::string& token, const std::string& path,
                        std::atomic<uint64_t>* got = nullptr, std::atomic<uint64_t>* total = nullptr);
// Like HttpDownload, but continues a partial file at `path` (Range request) and can be cancelled.
HttpResult HttpDownloadResume(const std::string& url, const std::string& token, const std::string& path,
                              std::atomic<uint64_t>* got, std::atomic<uint64_t>* total, std::atomic<bool>* cancel);
HttpResult HttpPostJson(const std::string& url, const std::string& token, const std::string& json);
// multipart/form-data; `sent` / `total` are updated while uploading (for a progress bar).
HttpResult HttpPostMultipart(const std::string& url, const std::string& token,
                             const std::vector<std::pair<std::string, std::string>>& fields,
                             const std::vector<HttpFile>& files,
                             std::atomic<uint64_t>* sent = nullptr, std::atomic<uint64_t>* total = nullptr);

// One request on a background thread, polled by the UI.
struct NetJob
{
    std::atomic<bool> done{ false };
    HttpResult result;
    std::atomic<uint64_t> sent{ 0 }, total{ 0 };
};
std::shared_ptr<NetJob> RunNet(std::function<HttpResult(NetJob&)> fn);
