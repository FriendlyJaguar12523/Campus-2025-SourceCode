#pragma once
#include "AntiLog.h"

#include <windows.h>
#include <psapi.h>
#include <iostream>
#include <string>
#include <algorithm>
#include <tlhelp32.h>
#include "../RR/Patching/Console.h"
#include "AntiKill.h"  // This now has the forward declaration

#pragma comment(lib, "psapi.lib")

namespace AntiDebugger {

    typedef struct _PEB_CUSTOM {
        BYTE Reserved1[2];
        BYTE BeingDebugged;
        BYTE Reserved2[1];
        PVOID Reserved3[2];
        PVOID Ldr;
        PVOID ProcessParameters;
        PVOID Reserved4[3];
        PVOID AtlThunkSListPtr;
        PVOID Reserved5;
        ULONG Reserved6;
        PVOID Reserved7;
        ULONG Reserved8;
        ULONG AtlThunkSListPtr32;
        PVOID Reserved9[45];
        BYTE Reserved10[96];
        PVOID PostProcessInitRoutine;
        BYTE Reserved11[128];
        PVOID Reserved12[1];
        ULONG SessionId;
        ULONG NtGlobalFlag;
    } PEB_CUSTOM, * PPEB_CUSTOM;

    static bool IsDebuggerAttached() {
        return IsDebuggerPresent() != 0;
    }

    static bool IsRemoteDebuggerAttached() {
        PPEB_CUSTOM peb = nullptr;

#ifdef _WIN64
        peb = (PPEB_CUSTOM)__readgsqword(0x60);
#else
        peb = (PPEB_CUSTOM)__readfsdword(0x30);
#endif

        if (peb && peb->BeingDebugged) {
            return true;
        }

        if (peb && (peb->NtGlobalFlag & 0x70)) {
            return true;
        }

        return false;
    }

    static bool HasHardwareBreakpoints() {
        CONTEXT ctx = {};
        ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(GetCurrentThread(), &ctx)) {
            return ctx.Dr0 || ctx.Dr1 || ctx.Dr2 || ctx.Dr3;
        }
        return false;
    }

    static bool IsDebuggerViaTiming() {
        LARGE_INTEGER start, end, freq;
        QueryPerformanceFrequency(&freq);
        QueryPerformanceCounter(&start);

        for (int i = 0; i < 10000; i++) { volatile int x = i * i; }

        QueryPerformanceCounter(&end);
        double elapsed = (double)(end.QuadPart - start.QuadPart) / freq.QuadPart;

        return elapsed > 0.002;
    }

    static bool IsParentProcessDebugger() {
        HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (hSnapshot == INVALID_HANDLE_VALUE) return false;

        PROCESSENTRY32 pe32;
        pe32.dwSize = sizeof(PROCESSENTRY32);

        if (Process32First(hSnapshot, &pe32)) {
            do {
                if (pe32.th32ProcessID == GetCurrentProcessId()) {
                    DWORD parentPid = pe32.th32ParentProcessID;
                    CloseHandle(hSnapshot);

                    HANDLE hParent = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, parentPid);
                    if (hParent) {
                        char name[MAX_PATH];
                        DWORD size = MAX_PATH;
                        if (QueryFullProcessImageNameA(hParent, 0, name, &size)) {
                            std::string parentName = name;
                            std::transform(parentName.begin(), parentName.end(), parentName.begin(), ::tolower);
                            CloseHandle(hParent);

                            if (parentName.find("debug") != std::string::npos ||
                                parentName.find("dbg") != std::string::npos ||
                                parentName.find("olly") != std::string::npos ||
                                parentName.find("x64dbg") != std::string::npos ||
                                parentName.find("ida") != std::string::npos ||
                                parentName.find("windbg") != std::string::npos) {
                                return true;
                            }
                        }
                        else {
                            CloseHandle(hParent);
                        }
                    }
                    return false;
                }
            } while (Process32Next(hSnapshot, &pe32));
        }

        CloseHandle(hSnapshot);
        return false;
    }

    static bool IsDebuggedViaNtQuery() {
        typedef NTSTATUS(NTAPI* pNtQueryInformationProcess)(
            HANDLE ProcessHandle,
            ULONG ProcessInformationClass,
            PVOID ProcessInformation,
            ULONG ProcessInformationLength,
            PULONG ReturnLength
            );

        HMODULE hNtdll = GetModuleHandleA("ntdll.dll");
        if (!hNtdll) return false;

        auto NtQueryInformationProcess = (pNtQueryInformationProcess)GetProcAddress(hNtdll, "NtQueryInformationProcess");
        if (!NtQueryInformationProcess) return false;

        ULONG debugPort = 0;
        NTSTATUS status = NtQueryInformationProcess(
            GetCurrentProcess(),
            7,
            &debugPort,
            sizeof(debugPort),
            nullptr
        );

        if (status == 0 && debugPort != 0) {
            return true;
        }

        return false;
    }

    static bool PerformAntiDebugChecks() {
        Protections::log << TAG_ANTI() << "Running anti-debugger checks...\n";

        if (IsDebuggerAttached()) {
            Protections::log << TAG_ANTI() << "Debugger detected (IsDebuggerPresent)\n";
            AntiKill::TriggerAntiCheat("Debugger detected: IsDebuggerPresent");
            return false;
        }

        if (IsRemoteDebuggerAttached()) {
            Protections::log << TAG_ANTI() << "Debugger detected (PEB flags)\n";
            AntiKill::TriggerAntiCheat("Debugger detected: PEB flags");
            return false;
        }

        if (HasHardwareBreakpoints()) {
            Protections::log << TAG_ANTI() << "Hardware breakpoints detected\n";
            AntiKill::TriggerAntiCheat("Hardware breakpoints detected");
            return false;
        }

        if (IsParentProcessDebugger()) {
            Protections::log << TAG_ANTI() << "Debugger parent process detected\n";
            AntiKill::TriggerAntiCheat("Debugger parent process detected");
            return false;
        }

        if (IsDebuggedViaNtQuery()) {
            Protections::log << TAG_ANTI() << "Debugger detected (NtQueryInformationProcess)\n";
            AntiKill::TriggerAntiCheat("Debugger detected: NtQueryInformationProcess");
            return false;
        }

        Protections::log << TAG_ANTI() << "Anti-debugger checks passed.\n";
        return true;
    }

    static void AntiDebuggerThread() {
        Sleep(20000);

        while (true) {
            Sleep(10000);

            if (IsDebuggerAttached() ||
                IsRemoteDebuggerAttached() ||
                HasHardwareBreakpoints() ||
                IsParentProcessDebugger() ||
                IsDebuggedViaNtQuery()) {

                Protections::log << TAG_ANTI() << "Debugger detected during monitoring\n";
                AntiKill::TriggerAntiCheat("Debugger detected during monitoring");
                return;
            }
        }
    }

    static void StartMonitoring() {
        Protections::log << TAG_ANTI() << "Starting anti-debugger monitoring...\n";
        Protections::log << TAG_ANTI() << "Monitoring will start in 20 seconds to let the game stabilize...\n";
        CreateThread(nullptr, 0, [](LPVOID) -> DWORD {
            AntiDebuggerThread();
            return 0;
            }, nullptr, 0, nullptr);
    }

}