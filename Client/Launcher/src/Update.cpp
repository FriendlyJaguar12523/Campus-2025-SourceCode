#include "Update.h"
#include "Util.h"
#include <windows.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <vector>

namespace fs = std::filesystem;

std::string ExePath()
{
    std::wstring buf(MAX_PATH, L'\0');
    for (;;)
    {
        DWORD n = GetModuleFileNameW(nullptr, buf.data(), (DWORD)buf.size());
        if (n < buf.size()) { buf.resize(n); break; }
        buf.resize(buf.size() * 2);
    }
    return Narrow(buf);
}

static fs::path OldName(const fs::path& exe, int n)
{
    std::wstring stem = exe.stem().wstring();
    return exe.parent_path() / (stem + (n ? L".old" + std::to_wstring(n) : L".old") + L".exe");
}

bool RemoveOldExe()
{
    fs::path exe = U8Path(ExePath());
    bool all = true;
    for (int n = 0; n < 10; n++)
    {
        std::error_code ec;
        fs::path p = OldName(exe, n);
        if (!fs::exists(p, ec)) continue;
        if (!DeleteFileW(p.c_str())) all = false;       // still running (the update is a moment old)
    }
    return all;
}

std::string Sha256File(const std::string& path)
{
    std::ifstream in(U8Path(path), std::ios::binary);
    if (!in) return "";
    std::vector<unsigned char> data((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    unsigned char hash[32] = {};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, data.data(), (ULONG)data.size(), hash, sizeof hash) != 0) return "";
    static const char* hex = "0123456789abcdef";
    std::string s;
    for (unsigned char b : hash) { s += hex[b >> 4]; s += hex[b & 15]; }
    return s;
}

bool ApplyUpdate(const std::string& newExe, std::string& error)
{
    fs::path exe = U8Path(ExePath());
    // a free .old name (an older one may still be in use)
    fs::path old;
    for (int n = 0; n < 10 && old.empty(); n++)
    {
        fs::path p = OldName(exe, n);
        std::error_code ec;
        if (!fs::exists(p, ec) || DeleteFileW(p.c_str())) old = p;
    }
    if (old.empty()) { error = "old copies of the launcher are still running - close them and try again"; return false; }
    if (!MoveFileExW(exe.c_str(), old.c_str(), MOVEFILE_REPLACE_EXISTING))
    {
        error = "can't replace " + PathU8(exe) + " (error " + std::to_string(GetLastError()) + ") - is the folder read-only?";
        return false;
    }
    if (!CopyFileW(U8Path(newExe).c_str(), exe.c_str(), FALSE))
    {
        error = "can't write the new version (error " + std::to_string(GetLastError()) + ")";
        MoveFileExW(old.c_str(), exe.c_str(), MOVEFILE_REPLACE_EXISTING);
        return false;
    }
    std::wstring cmd = GetCommandLineW();
    STARTUPINFOW si{ sizeof si };
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
    {
        error = "updated, but couldn't start it (error " + std::to_string(GetLastError()) + ") - open the launcher again";
        return true;                                    // the new exe is in place; the next start runs it
    }
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return true;
}
