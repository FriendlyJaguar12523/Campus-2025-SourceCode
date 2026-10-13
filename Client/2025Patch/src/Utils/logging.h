#pragma once

#include <windows.h>
#include <streambuf>
#include <string>
#include <iostream>
#include <cstdio>
#include <cstring>
#include <vector>

// CampusPatch console tee: mirrors std::cout to console + log file.
// File path: <dll folder>\Logs\logsession<YYYYMMDD_HHMMSS>.txt

namespace CampusLogging {

    // TeeBuf: streambuf that writes to both CONOUT$ and a log file.
    // Uses Win32 HANDLEs and WriteFile - avoids CRT issues.
    class TeeBuf : public std::streambuf {
        HANDLE hConsole_ = INVALID_HANDLE_VALUE;
        HANDLE hFile_ = INVALID_HANDLE_VALUE;
        CRITICAL_SECTION cs_{};
        static thread_local std::string lineBuf_;

    public:
        TeeBuf() { InitializeCriticalSection(&cs_); }

        ~TeeBuf() {
            if (hFile_ != INVALID_HANDLE_VALUE) {
                FlushFileBuffers(hFile_);
                CloseHandle(hFile_);
            }
            if (hConsole_ != INVALID_HANDLE_VALUE) {
                CloseHandle(hConsole_);
            }
            DeleteCriticalSection(&cs_);
        }

        bool Open(const std::string& path) {
            hConsole_ = CreateFileA("CONOUT$",
                GENERIC_WRITE,
                FILE_SHARE_WRITE | FILE_SHARE_READ,
                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            hFile_ = CreateFileA(path.c_str(),
                GENERIC_WRITE,
                FILE_SHARE_READ,
                nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            return hFile_ != INVALID_HANDLE_VALUE;
        }

        HANDLE FileHandle() const { return hFile_; }

    protected:
        int overflow(int ch) override {
            if (ch == traits_type::eof()) { sync(); return 0; }
            appendAndMaybeEmit(static_cast<char>(ch));
            return ch;
        }

        std::streamsize xsputn(const char* s, std::streamsize n) override {
            for (std::streamsize i = 0; i < n; i++) appendAndMaybeEmit(s[i]);
            return n;
        }

        int sync() override {
            if (hFile_ != INVALID_HANDLE_VALUE) FlushFileBuffers(hFile_);
            return 0;
        }

    private:
        void appendAndMaybeEmit(char c) {
            lineBuf_.push_back(c);
            if (c != '\n') return;

            // Filter out VR Path Registry messages
            if (lineBuf_.find("Unable to read VR Path Registry") != std::string::npos) {
                lineBuf_.clear();
                return;
            }

            writeBoth(lineBuf_.data(), static_cast<DWORD>(lineBuf_.size()));
            if (hFile_ != INVALID_HANDLE_VALUE) FlushFileBuffers(hFile_);
            lineBuf_.clear();
        }

        // Writes to console (raw ANSI) and file (stripped of ANSI escapes)
        void writeBoth(const char* p, DWORD n) {
            EnterCriticalSection(&cs_);
            DWORD w = 0;

            if (hConsole_ != INVALID_HANDLE_VALUE) {
                WriteFile(hConsole_, p, n, &w, nullptr);
            }

            if (hFile_ != INVALID_HANDLE_VALUE) {
                // Strip ANSI escape sequences for clean log files
                char stripped[1024];
                DWORD si = 0;

                for (DWORD i = 0; i < n; i++) {
                    unsigned char c = (unsigned char)p[i];

                    if (c == 0x1B && i + 1 < n && p[i + 1] == '[') {
                        i += 2;
                        while (i < n && !(p[i] >= 0x40 && p[i] <= 0x7E)) i++;
                        continue;
                    }

                    if (si < sizeof(stripped)) {
                        stripped[si++] = (char)c;
                    }
                    else {
                        WriteFile(hFile_, stripped, si, &w, nullptr);
                        si = 0;
                        stripped[si++] = (char)c;
                    }
                }

                if (si > 0) WriteFile(hFile_, stripped, si, &w, nullptr);
            }

            LeaveCriticalSection(&cs_);
        }
    };

    thread_local std::string TeeBuf::lineBuf_;

    static TeeBuf* g_tee = nullptr;
    // The rdbuf std::cout had BEFORE we installed the tee. Restored on Shutdown, so any
    // std::cout write that happens between our teardown and process exit (there ARE some
    // via the atexit-ordered destructors of static objects) doesn't touch freed memory.
    static std::streambuf* g_origCoutBuf = nullptr;

    // Convert UTC to BST (UTC + 1 hour)
    static void UtcNowAsBst(SYSTEMTIME& out) {
        SYSTEMTIME utc;
        GetSystemTime(&utc);

        FILETIME ft;
        SystemTimeToFileTime(&utc, &ft);

        ULARGE_INTEGER u;
        u.LowPart = ft.dwLowDateTime;
        u.HighPart = ft.dwHighDateTime;
        u.QuadPart += 3600ULL * 10000000ULL;  // +1 hour

        ft.dwLowDateTime = u.LowPart;
        ft.dwHighDateTime = u.HighPart;
        FileTimeToSystemTime(&ft, &out);
    }

    // Initialize logging. Call once from InitThread.
    static void Init(HMODULE selfModule) {
        char dllPath[MAX_PATH] = {};
        if (GetModuleFileNameA(selfModule, dllPath, MAX_PATH) == 0) {
            printf("[Campus/Log] GetModuleFileNameA failed err=%lu - console only\n", GetLastError());
            return;
        }

        char* slash = strrchr(dllPath, '\\');
        if (slash) *slash = 0;

        std::string dir = std::string(dllPath) + "\\Logs";
        CreateDirectoryA(dir.c_str(), nullptr);

        SYSTEMTIME bst;
        UtcNowAsBst(bst);

        char id[32];
        _snprintf_s(id, sizeof(id), _TRUNCATE, "%04d%02d%02d_%02d%02d%02d",
            bst.wYear, bst.wMonth, bst.wDay,
            bst.wHour, bst.wMinute, bst.wSecond);

        std::string path = dir + "\\logsession" + id + ".txt";
        printf("[Campus/Log] session log -> %s\n", path.c_str());

        TeeBuf* tee = new TeeBuf();
        if (!tee->Open(path)) {
            printf("[Campus/Log] failed to open '%s' err=%lu - console only\n",
                path.c_str(), GetLastError());
            delete tee;
            return;
        }

        // Write session header
        char header[512];
        int hn = _snprintf_s(header, sizeof(header), _TRUNCATE,
            "\n[CAMPUS LOG]\n"
            "[TIME] (BST): %04d-%02d-%02d  %02d:%02d:%02d BST\n"
            "[PATH]:      %s\n"
            "[CREDITS] Kyle & Trap\n",
            bst.wYear, bst.wMonth, bst.wDay,
            bst.wHour, bst.wMinute, bst.wSecond,
            path.c_str());

        if (hn > 0) tee->sputn(header, hn);

        g_tee = tee;
        // Remember the previous rdbuf so Shutdown can put it back before freeing our tee.
        // Without this, delete g_tee leaves std::cout.rdbuf() dangling -> next std::cout <<
        // writes freed memory -> UAF crash on unload (often via a static-destructor log).
        g_origCoutBuf = std::cout.rdbuf(g_tee);
    }

    static void Shutdown() {
        if (g_tee) {
            // Point std::cout back at its original streambuf FIRST so any late writes
            // (e.g. from destructors that run after us) land on the console instead of
            // a freed TeeBuf. Only then is it safe to delete.
            std::cout.rdbuf(g_origCoutBuf);
            g_origCoutBuf = nullptr;
            delete g_tee;
            g_tee = nullptr;
        }
    }

} // namespace CampusLogging