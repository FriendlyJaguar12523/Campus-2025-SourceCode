#pragma once
#include "AntiLog.h"

#include <windows.h>
#include <string>
#include <vector>
#include <algorithm>
#include <tlhelp32.h>
#include <iostream>
#include <thread>
#include <chrono>
#include "../RR/Patching/Console.h"
#include "AntiKill.h"  // This now has the forward declaration
#include "ProcessAllowlist.h"

namespace AntiDebug {

    static const std::vector<std::string> kBlacklistedProcesses = {
        "HTTPDebugger", "HTTPDebuggerUI", "HTTPDebuggerSvc",
        "Fiddler", "FiddlerEverywhere", "Charles", "BurpSuite",
        "Wireshark", "Tcpdump", "HTTPTool", "Proxifier",
        "CheatEngine", "cheatengine-x86_64", "DBG", "x64dbg",
        "x32dbg", "OllyDbg", "IDA", "ida64", "Radare2", "Ghidra",
        "ArtMoney", "ScanMem", "MHS", "TSearch",
        "ExtremeInjector", "Xenos", "Injector", "DLLInject",
        "ProcessHacker", "ProcessMonitor", "API Monitor", "WinDbg"
    };

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
                // entry (OculusDebugTool.exe <- "DBG" / "Debug", nvcontainer.exe <- "IDA", etc.).
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

    static bool PerformAntiDebugCheck() {
        Protections::log << TAG_ANTI() << "Running anti-debug checks...\n";

        for (const auto& process : kBlacklistedProcesses) {
            if (IsProcessRunning(process)) {
                Protections::log << TAG_ANTI() << "Blacklisted process detected: " << process << "\n";
                AntiKill::TriggerAntiCheat("Unauthorized application detected: " + process);
                return false;
            }
        }

        Protections::log << TAG_ANTI() << "Anti-debug checks passed.\n";
        return true;
    }

    static void AntiDebugThread() {
        Sleep(20000);

        while (true) {
            Sleep(10000);

            for (const auto& process : kBlacklistedProcesses) {
                if (IsProcessRunning(process)) {
                    Protections::log << TAG_ANTI() << "Blacklisted process detected: " << process << "\n";
                    AntiKill::TriggerAntiCheat("Unauthorized application detected during monitoring: " + process);
                    return;
                }
            }
        }
    }

    static void StartAntiDebugMonitoring() {
        Protections::log << TAG_ANTI() << "Starting anti-debug monitoring...\n";
        Protections::log << TAG_ANTI() << "Monitoring will start in 20 seconds to let the game stabilize...\n";
        CreateThread(nullptr, 0, [](LPVOID) -> DWORD {
            AntiDebugThread();
            return 0;
            }, nullptr, 0, nullptr);
    }

}