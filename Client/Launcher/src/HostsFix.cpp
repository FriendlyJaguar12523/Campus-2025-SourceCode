#include "HostsFix.h"
#include "Util.h"

#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <shellapi.h>
#include <shlobj.h>
#include <string>
#include <fstream>
#include <sstream>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "shell32.lib")

namespace hostsfix {

    static const char* kMarkerBegin = "# Campus Launcher - DNS fallback (safe to delete if the connection stops working)";
    static const char* kMarkerEnd   = "# end Campus Launcher";

    bool CanResolve(const char* host)
    {
        WSADATA wsa{};
        if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return false;
        addrinfo hints{}, *res = nullptr;
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        int rc = getaddrinfo(host, nullptr, &hints, &res);
        if (res) freeaddrinfo(res);
        WSACleanup();
        return rc == 0;
    }

    static std::wstring HostsPath()
    {
        wchar_t sys[MAX_PATH] = L"";
        if (!GetSystemDirectoryW(sys, MAX_PATH)) return L"";
        std::wstring p = sys;
        p += L"\\drivers\\etc\\hosts";
        return p;
    }

    bool WriteHostsBlock(const char* ip)
    {
        std::wstring path = HostsPath();
        if (path.empty()) return false;

        std::string cur;
        {
            std::ifstream in(path.c_str(), std::ios::binary);
            if (in.is_open())
            {
                std::stringstream buf;
                buf << in.rdbuf();
                cur = buf.str();
            }
        }

        // Strip any previous Campus block so we replace, not append.
        size_t b = cur.find(kMarkerBegin);
        if (b != std::string::npos)
        {
            size_t e = cur.find(kMarkerEnd, b);
            if (e != std::string::npos)
            {
                size_t eol = cur.find('\n', e);
                e = (eol == std::string::npos) ? cur.size() : eol + 1;
                cur.erase(b, e - b);
            }
            // Also clean a trailing blank line we might have left behind.
            while (b > 0 && (cur[b - 1] == '\n' || cur[b - 1] == '\r')) { cur.erase(b - 1, 1); b--; }
        }

        // Ensure the file ends with a newline before we add our block.
        if (!cur.empty() && cur.back() != '\n') cur += "\r\n";
        if (cur.empty() || cur.back() != '\n') cur += "\r\n";

        // Build the block.
        std::string blk;
        blk += kMarkerBegin; blk += "\r\n";
        for (int i = 0; i < kHostsCount; i++)
        {
            blk += ip;
            blk += "  ";
            blk += kHosts[i];
            blk += "\r\n";
        }
        blk += kMarkerEnd; blk += "\r\n";
        cur += blk;

        // Write via temp + ReplaceFile so permissions/ACLs on hosts are kept.
        std::wstring tmp = path + L".campus.tmp";
        {
            std::ofstream out(tmp.c_str(), std::ios::binary | std::ios::trunc);
            if (!out.is_open()) return false;
            out.write(cur.data(), (std::streamsize)cur.size());
            if (!out.good()) { out.close(); DeleteFileW(tmp.c_str()); return false; }
        }
        BOOL ok = ReplaceFileW(path.c_str(), tmp.c_str(), nullptr, REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr);
        if (!ok)
        {
            // ReplaceFile can fail on hosts because it's not a usual file; fall back to a plain
            // overwrite (we are elevated at this point, so CopyFileW works).
            ok = CopyFileW(tmp.c_str(), path.c_str(), FALSE);
            DeleteFileW(tmp.c_str());
        }
        return !!ok;
    }

    void FlushDns()
    {
        STARTUPINFOW si{}; si.cb = sizeof si;
        si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION pi{};
        wchar_t cmd[] = L"ipconfig /flushdns";
        if (CreateProcessW(nullptr, cmd, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        {
            WaitForSingleObject(pi.hProcess, 5000);
            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        }
    }

    bool RelaunchElevatedAndFix(const char* ip)
    {
        wchar_t exe[MAX_PATH] = L"";
        if (!GetModuleFileNameW(nullptr, exe, MAX_PATH)) return false;
        std::wstring args = L"--fix-hosts " + Widen(ip);

        SHELLEXECUTEINFOW sei{}; sei.cbSize = sizeof sei;
        sei.lpVerb = L"runas";       // UAC
        sei.lpFile = exe;
        sei.lpParameters = args.c_str();
        sei.nShow = SW_HIDE;
        sei.fMask = SEE_MASK_NOCLOSEPROCESS;
        if (!ShellExecuteExW(&sei) || !sei.hProcess) return false;
        WaitForSingleObject(sei.hProcess, 60000);
        DWORD code = 1;
        GetExitCodeProcess(sei.hProcess, &code);
        CloseHandle(sei.hProcess);
        return code == 0;
    }

    int MaybeHandleFixHostsFlag()
    {
        int argc = 0;
        LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        if (!argv) return -1;
        std::string ip;
        for (int i = 1; i + 1 < argc; i++)
        {
            if (wcscmp(argv[i], L"--fix-hosts") == 0)
            {
                ip = Narrow(argv[i + 1]);
                break;
            }
        }
        LocalFree(argv);
        if (ip.empty()) return -1;
        bool ok = WriteHostsBlock(ip.c_str());
        if (ok) FlushDns();
        return ok ? 0 : 1;
    }

}
