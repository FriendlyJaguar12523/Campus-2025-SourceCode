#pragma once
#include "AntiLog.h"

#include <windows.h>
#include <tlhelp32.h>
#include <intrin.h>
#include <string>
#include <iostream>
#include <algorithm>
#include <vector>
#include <set>
#include "../RR/Patching/Console.h"
#include "AntiKill.h"

namespace AntiSandbox {

    static bool IsHypervisorPresent() {
        int cpuInfo[4] = { 0, 0, 0, 0 };
        __cpuid(cpuInfo, 1);
        return (cpuInfo[2] & (1 << 31)) != 0;
    }

    static std::string GetHypervisorVendor() {
        int cpuInfo[4] = { 0, 0, 0, 0 };

        __cpuid(cpuInfo, 0);
        if (cpuInfo[0] < 0x40000000) {
            return "";
        }

        __cpuid(cpuInfo, 0x40000000);

        char vendor[13] = { 0 };
        memcpy(vendor, &cpuInfo[1], 4);
        memcpy(vendor + 4, &cpuInfo[2], 4);
        memcpy(vendor + 8, &cpuInfo[3], 4);

        return std::string(vendor);
    }

    static bool IsKnownVMHypervisor(const std::string& vendor) {
        static const std::vector<std::string> knownVMs = {
            "VMwareVMware",
            "VBoxVBoxVBox",
            "XenVMMXenVMM",
            "KVMKVMKVM",
            "QEMUQEMUQEMU",
            "VirtualPC",
            "Parallels",
        };

        for (const auto& vm : knownVMs) {
            if (vendor == vm) {
                return true;
            }
        }
        return false;
    }

    static bool IsHyperVHost() {
        // Check if Hyper-V is enabled but this is the host, not a VM
        HKEY hKey;
        LONG result = RegOpenKeyExA(HKEY_LOCAL_MACHINE,
            "SYSTEM\\CurrentControlSet\\Control\\Hyper-V",
            0, KEY_READ, &hKey);

        if (result == ERROR_SUCCESS) {
            RegCloseKey(hKey);

            // Check if vmwp.exe is running (VM worker process)
            HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (hSnapshot != INVALID_HANDLE_VALUE) {
                PROCESSENTRY32 pe32 = {};
                pe32.dwSize = sizeof(PROCESSENTRY32);
                if (Process32First(hSnapshot, &pe32)) {
                    do {
                        std::string name = pe32.szExeFile;
                        std::transform(name.begin(), name.end(), name.begin(), ::tolower);
                        if (name == "vmwp.exe") {
                            CloseHandle(hSnapshot);
                            return false; // VM is running, this is a VM
                        }
                    } while (Process32Next(hSnapshot, &pe32));
                }
                CloseHandle(hSnapshot);
            }
            return true; // Hyper-V enabled but no VMs running = host system
        }
        return false;
    }

    static bool IsVMProcesses() {
        static const std::set<std::string> vmProcesses = {
            "vmtoolsd.exe",
            "vmware.exe",
            "vmwaretray.exe",
            "vboxservice.exe",
            "vboxtray.exe",
            "xenservice.exe",
            "qemu-ga.exe",
            "parallels.exe",
            "prl_cc.exe",
            "prl_tools.exe",
        };

        HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (hSnapshot == INVALID_HANDLE_VALUE) return false;

        PROCESSENTRY32 pe32 = {};
        pe32.dwSize = sizeof(PROCESSENTRY32);

        if (Process32First(hSnapshot, &pe32)) {
            do {
                std::string name = pe32.szExeFile;
                std::transform(name.begin(), name.end(), name.begin(), ::tolower);

                if (vmProcesses.find(name) != vmProcesses.end()) {
                    CloseHandle(hSnapshot);
                    return true;
                }
            } while (Process32Next(hSnapshot, &pe32));
        }

        CloseHandle(hSnapshot);
        return false;
    }

    static bool IsVMDevices() {
        static const std::vector<std::string> vmDeviceStrings = {
            "vmware", "virtualbox", "vbox", "xen", "qemu", "parallels"
        };

        HKEY hKey;
        LONG result = RegOpenKeyExA(HKEY_LOCAL_MACHINE,
            "HARDWARE\\DESCRIPTION\\System\\BIOS",
            0, KEY_READ, &hKey);

        if (result == ERROR_SUCCESS) {
            char biosVendor[512] = { 0 };
            DWORD size = sizeof(biosVendor);

            if (RegQueryValueExA(hKey, "SystemManufacturer", nullptr, nullptr,
                (LPBYTE)biosVendor, &size) == ERROR_SUCCESS) {

                std::string vendor = biosVendor;
                std::transform(vendor.begin(), vendor.end(), vendor.begin(), ::tolower);

                for (const auto& vm : vmDeviceStrings) {
                    if (vendor.find(vm) != std::string::npos) {
                        RegCloseKey(hKey);
                        return true;
                    }
                }
            }

            RegCloseKey(hKey);
        }

        return false;
    }

    static bool IsLowMemoryVM() {
        MEMORYSTATUSEX memStatus = {};
        memStatus.dwLength = sizeof(memStatus);
        GlobalMemoryStatusEx(&memStatus);
        return memStatus.ullTotalPhys < 2ULL * 1024 * 1024 * 1024;
    }

    static bool PerformAntiSandboxChecks() {
        Protections::log << TAG_ANTI() << "Running anti-sandbox checks...\n";

        if (IsHypervisorPresent()) {
            std::string vendor = GetHypervisorVendor();
            Protections::log << TAG_ANTI() << "Hypervisor detected: " << vendor << "\n";

            if (vendor.find("Microsoft Hv") != std::string::npos) {
                if (IsHyperVHost()) {
                    Protections::log << TAG_ANTI() << "Hyper-V is enabled on the host system - this is NOT a VM\n";
                    Protections::log << TAG_ANTI() << "Continuing with other checks...\n";
                }
                else {
                    Protections::log << TAG_ANTI() << "Hyper-V virtual machine detected\n";
                    AntiKill::TriggerAntiCheat("Hyper-V Virtual Machine detected");
                    return false;
                }
            }
            else if (IsKnownVMHypervisor(vendor)) {
                Protections::log << TAG_ANTI() << "Virtual machine detected: " << vendor << "\n";
                AntiKill::TriggerAntiCheat("Virtual machine detected: " + vendor);
                return false;
            }
            else {
                Protections::log << TAG_ANTI() << "Unknown hypervisor vendor: " << vendor << "\n";
                Protections::log << TAG_ANTI() << "This may be a physical machine with virtualization enabled\n";
                Protections::log << TAG_ANTI() << "Skipping hypervisor check - continuing with other checks...\n";
            }
        }

        // Only check VM processes if not Hyper-V host
        if (IsVMProcesses()) {
            Protections::log << TAG_ANTI() << "Virtual machine processes detected\n";
            AntiKill::TriggerAntiCheat("Virtual machine detected (processes)");
            return false;
        }

        if (IsVMDevices()) {
            Protections::log << TAG_ANTI() << "Virtual machine devices detected\n";
            AntiKill::TriggerAntiCheat("Virtual machine detected (devices)");
            return false;
        }

        if (IsLowMemoryVM()) {
            Protections::log << TAG_ANTI() << "Virtual machine detected (low memory)\n";
            AntiKill::TriggerAntiCheat("Virtual machine detected (low memory)");
            return false;
        }

        Protections::log << TAG_ANTI() << "Anti-sandbox checks passed.\n";
        return true;
    }

    static void AntiSandboxThread() {
        Sleep(30000);

        while (true) {
            Sleep(15000);

            if (IsVMProcesses() || IsVMDevices() || IsLowMemoryVM()) {
                Protections::log << TAG_ANTI() << "Virtual machine detected during monitoring\n";
                AntiKill::TriggerAntiCheat("Virtual machine detected during monitoring");
                return;
            }
        }
    }

    static void StartMonitoring() {
        Protections::log << TAG_ANTI() << "Starting anti-sandbox monitoring...\n";
        Protections::log << TAG_ANTI() << "Monitoring will start in 30 seconds to let the system stabilize...\n";
        CreateThread(nullptr, 0, [](LPVOID) -> DWORD {
            AntiSandboxThread();
            return 0;
            }, nullptr, 0, nullptr);
    }

}