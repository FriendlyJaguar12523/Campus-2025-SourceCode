#pragma once
#include "AntiLog.h"

#include <windows.h>
#include <string>
#include <iostream>
#include <thread>
#include <chrono>
#include <sstream>
#include <winhttp.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <intrin.h>
#include <algorithm>
#include <vector>
#include "../RR/Patching/Console.h"
#include "DiscordWebhooks.h"
#include "ProcessAllowlist.h"

#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "advapi32.lib")

// Forward declaration of TriggerAntiCheat for use in other headers
namespace AntiKill {
    void TriggerAntiCheat(const std::string& message);
}

namespace AntiKill {

    static const std::string& WEBHOOK_URL = DiscordWebhooks::GetAntiCheatWebhook();
    static bool g_triggered = false;
    static HWND g_gameWindow = NULL;
    static bool g_blockGameLoad = false;
    static bool g_initialized = false;

    static const std::vector<std::string> kBlacklistedProcesses = {
        "x64dbg", "x32dbg", "ollydbg", "ida", "ida64", "windbg", "cheatengine",
        "cheatengine-x86_64", "dbg", "debug", "debugger", "dnspy", "ildasm",
        "extremeinjector", "xenos", "injector", "dllinject", "processhacker",
        "httpdebugger", "fiddler", "charles", "burpsuite", "wireshark",
        "artmoney", "scanmem", "mhs", "tsearch", "wemod", "trainer", "cheat", "hack"
    };

    static void KillProcess() {
        Protections::log << TAG_ANTI() << "Terminating process...\n";
        TerminateProcess(GetCurrentProcess(), 1);
        ExitProcess(1);
    }

    static void SendDiscordWebhook(const std::string& message) {
        Protections::log << TAG_ANTI() << "Sending Discord webhook...\n";
    }

    static HWND FindGameWindow() {
        HWND hWnd = FindWindowA(NULL, "Rec Room");
        if (hWnd) return hWnd;
        hWnd = FindWindowA(NULL, "Campus");
        if (hWnd) return hWnd;
        hWnd = FindWindowA(NULL, "Unity Player");
        if (hWnd) return hWnd;
        return NULL;
    }

    static void ShowAutoClosePopup(const std::string& message) {
        std::string webhookMessage = "**Campus Anti-Cheat Triggered**\n```\n" + message + "\n```\n*The process will now be terminated.*";
        SendDiscordWebhook(webhookMessage);

        HANDLE hTimerThread = CreateThread(NULL, 0, [](LPVOID) -> DWORD {
            Sleep(5000);

            HWND hWnd = FindWindowA(NULL, "Campus Anti-Cheat");
            if (hWnd) {
                PostMessageA(hWnd, WM_CLOSE, 0, 0);
                PostMessageA(hWnd, WM_COMMAND, IDOK, 0);
                SendMessageA(hWnd, WM_CLOSE, 0, 0);
            }

            KillProcess();
            return 0;
            }, NULL, 0, NULL);

        if (hTimerThread) {
            CloseHandle(hTimerThread);
        }

        MessageBoxA(
            NULL,
            message.c_str(),
            "Campus Anti-Cheat",
            MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SYSTEMMODAL
        );

        KillProcess();
    }

    void TriggerAntiCheat(const std::string& message) {
        if (g_triggered) return;
        g_triggered = true;
        g_blockGameLoad = true;

        std::string fullMessage = "Campus Anti-Cheat has detected unauthorized activity on your system:\n\n" + message + "\n\nClose any cheating / debugging tools and restart Campus. If you believe this is a mistake, contact support.";
        ShowAutoClosePopup(fullMessage);
    }

    static bool IsProcessRunning(const std::string& processName) {
        HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (hSnapshot == INVALID_HANDLE_VALUE) return false;

        PROCESSENTRY32 pe32;
        pe32.dwSize = sizeof(PROCESSENTRY32);

        std::string searchName = processName;
        std::transform(searchName.begin(), searchName.end(),
            searchName.begin(), ::tolower);

        if (Process32First(hSnapshot, &pe32)) {
            do {
                std::string currentProcess = pe32.szExeFile;
                std::transform(currentProcess.begin(), currentProcess.end(),
                    currentProcess.begin(), ::tolower);

                // Skip legitimate processes whose names happen to substring-match a blacklist
                // entry (OculusDebugTool.exe <- "debug", nvcontainer.exe <- "ida", etc.).
                // Without this the scanner kicks any player using an Oculus headset.
                if (Protections::IsAllowedProcess(currentProcess)) {
                    continue;
                }

                if (currentProcess.find(searchName) != std::string::npos) {
                    CloseHandle(hSnapshot);
                    return true;
                }
            } while (Process32Next(hSnapshot, &pe32));
        }

        CloseHandle(hSnapshot);
        return false;
    }

    static bool IsDebuggerPresent() {
        return ::IsDebuggerPresent() != FALSE;
    }

    static bool HasHardwareBreakpoints() {
        CONTEXT ctx = {};
        ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(GetCurrentThread(), &ctx)) {
            return ctx.Dr0 || ctx.Dr1 || ctx.Dr2 || ctx.Dr3;
        }
        return false;
    }

    // VM detection DISABLED - was causing false positives on legitimate hardware
    static bool IsVirtualMachine() {
        return false;
    }

    static bool IsVMProcesses() {
        return false;
    }

    static bool HasProxyEnabled() {
        HKEY hKey;
        if (RegOpenKeyExA(HKEY_CURRENT_USER, "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", 0, KEY_READ, &hKey) == ERROR_SUCCESS) {
            DWORD proxyEnable = 0;
            DWORD size = sizeof(proxyEnable);
            if (RegQueryValueExA(hKey, "ProxyEnable", nullptr, nullptr, (LPBYTE)&proxyEnable, &size) == ERROR_SUCCESS) {
                RegCloseKey(hKey);
                return proxyEnable != 0;
            }
            RegCloseKey(hKey);
        }
        return false;
    }

    static std::string PerformAntiCheatChecks() {
        std::string detected = "";
        Protections::log << TAG_ANTI() << "Running anti-cheat checks...\n";

        for (const auto& process : kBlacklistedProcesses) {
            if (IsProcessRunning(process)) {
                detected += "[-] Detected process: " + process + "\n";
            }
        }

        if (IsDebuggerPresent()) {
            detected += "[-] Debugger detected (IsDebuggerPresent)\n";
        }
        if (HasHardwareBreakpoints()) {
            detected += "[-] Hardware breakpoints detected\n";
        }
        // VM checks DISABLED - they cause false positives on legitimate hardware
        // if (IsVirtualMachine()) {
        //     detected += "[-] Virtual machine detected (CPUID)\n";
        // }
        // if (IsVMProcesses()) {
        //     detected += "[-] Virtual machine processes detected\n";
        // }
        if (HasProxyEnabled()) {
            detected += "[-] Proxy enabled\n";
        }

        if (!detected.empty()) {
            Protections::log << TAG_ANTI() << "Anti-cheat triggered!\n" << detected;
            return detected;
        }
        else {
            Protections::log << TAG_ANTI() << "All anti-cheat checks passed\n";
        }
        return "";
    }

    static void AntiCheatMonitor() {
        Sleep(10000);

        while (true) {
            Sleep(10000);
            if (g_triggered) return;

            std::string result = PerformAntiCheatChecks();
            if (!result.empty()) {
                TriggerAntiCheat(result);
                return;
            }
        }
    }

    static bool ShouldBlockGameLoad() {
        return g_blockGameLoad;
    }

    static void Initialize() {
        if (g_initialized) {
            return;
        }
        g_initialized = true;

        Protections::log << TAG_ANTI() << "Starting anti-cheat system...\n";
        Protections::log << TAG_ANTI() << "Anti-cheat features loaded:\n";
        Protections::log << TAG_ANTI() << "  - Process scanning\n";
        Protections::log << TAG_ANTI() << "  - Debugger detection\n";
        Protections::log << TAG_ANTI() << "  - Network/Proxy detection\n";
        Protections::log << TAG_ANTI() << "  - Discord webhook logging\n";
        Protections::log << TAG_ANTI() << "  - VM detection: DISABLED (false positive fix)\n";

        g_gameWindow = FindGameWindow();
        Protections::log << TAG_ANTI() << "Game window found: " << std::hex << g_gameWindow << std::dec << "\n";

        SendDiscordWebhook("**AntiKill Initialized**\nAnti-cheat system is now active.");

        std::string result = PerformAntiCheatChecks();
        if (!result.empty()) {
            g_triggered = true;
            g_blockGameLoad = true;
            ShowAutoClosePopup("Campus Anti-Cheat has detected an unauthorized application on your system.\n\n" + result + "\n\nThis window will close automatically.");
            return;
        }

        Protections::log << TAG_ANTI() << "Starting anti-cheat monitoring...\n";
        CreateThread(nullptr, 0, [](LPVOID) -> DWORD {
            AntiCheatMonitor();
            return 0;
            }, nullptr, 0, nullptr);

        Protections::log << TAG_ANTI() << "Anti-cheat system active.\n";
    }

}