#pragma once

#include "AntiLog.h"      // MUST be first so Protections::log resolves in the Anti* headers
#include "AntiDebug.h"
#include "AntiDebugger.h"
#include "AntiInjection.h"
#include "AntiSandbox.h"
#include "AntiTamper.h"
#include "AntiKill.h"
#include "AntiHook.h"
#include "../Utils/logging.h"
#include "../Discord/DiscordRPC_Presence.h"

namespace Protections {
    // Master on/off switch for all anti-cheat functionality.
    // Defined in main.cpp - set to false to completely disable anti-cheat.
    extern bool AntiCheatEnabled;

    inline void Initialize() {
        if (!AntiCheatEnabled) {
            Protections::log << "\n";
            Protections::log << "\t[////////////////////////////////////////////////////////////////]\n";
            Protections::log << "  " << CLR_YELLOW << "\t\tANTI-CHEAT DISABLED (by AntiCheatEnabled)" << CLR_RESET << "\n";
            Protections::log << "\t[////////////////////////////////////////////////////////////////]\n";
            Protections::log << "\n";
            return;
        }

        // Full anti-cheat active
        Protections::log << "\n";
        Protections::log << "\t[////////////////////////////////////////////////////////////////]\n";
        Protections::log << "  " << CLR_RED << "\t\tCAMPUS ANTI-CHEAT SYSTEM" << CLR_RESET << "\n";
        Protections::log << "\t[////////////////////////////////////////////////////////////////]\n";

        // 1. Anti-Debug - initial process-blacklist sweep + continuous monitoring
        Protections::log << "| [1/7] Anti-Debug     ";
        AntiDebug::PerformAntiDebugCheck();
        AntiDebug::StartAntiDebugMonitoring();
        Protections::log << CLR_GREEN << "[ACTIVE]" << CLR_RESET;
        Protections::log << "|                      ";

        // 2. Anti-Debugger - IsDebuggerPresent / PEB / HW breakpoints + continuous monitoring
        Protections::log << "| [2/7] Anti-Debugger  ";
        AntiDebugger::PerformAntiDebugChecks();
        AntiDebugger::StartMonitoring();
        Protections::log << CLR_GREEN << "[ACTIVE]" << CLR_RESET;
        Protections::log << "|                      ";

        // 3. Anti-Sandbox - VM / hypervisor detection + continuous monitoring
        Protections::log << "| [3/7] Anti-Sandbox   ";
        AntiSandbox::PerformAntiSandboxChecks();
        AntiSandbox::StartMonitoring();
        Protections::log << CLR_GREEN << "[ACTIVE]" << CLR_RESET;
        Protections::log << "|                      ";

        // 4. Anti-Tamper - verifies CampusPatch.dll hasn't been patched after load
        Protections::log << "| [4/7] Anti-Tamper    ";
        AntiTamper::VerifyIntegrity();
        Protections::log << CLR_GREEN << "[ACTIVE]" << CLR_RESET;
        Protections::log << "|                      ";

        // 5. Anti-Injection - watches for injected / .tmp DLLs loaded into the process
        Protections::log << "| [5/7] Anti-Injection ";
        AntiInjection::Install();
        Protections::log << CLR_GREEN << "[ACTIVE]" << CLR_RESET;
        Protections::log << "|                      ";

        // 6. Anti-Cheat - process scanner + webhook reporting
        Protections::log << "| [6/7] Anti-Cheat     ";
        AntiKill::Initialize();
        Protections::log << CLR_GREEN << "[ACTIVE]" << CLR_RESET;
        Protections::log << "|                      ";

        // 7. Anti-Hook - scans GameAssembly for unauthorized hooks outside the allowlist
        Protections::log << "| [7/7] Anti-Hook      ";
        AntiHook::Initialize();
        Protections::log << CLR_GREEN << "[ACTIVE]" << CLR_RESET;
        Protections::log << "|                      ";

        Protections::log << "\t[////////////////////////////////////////////////////////////////]\n";
        Protections::log << "  \tStatus: " << CLR_GREEN << "All subsystems operational" << CLR_RESET << "\n";
        Protections::log << "\t[////////////////////////////////////////////////////////////////]\n";
        Protections::log << "\n";

        // Monitoring threads are started inside their respective modules.
        // (They are still disabled in debug builds because the modules themselves
        //  may be compiled with PATCH_DEBUG - but that is independent.)
    }

    inline void Shutdown() {
        if (!AntiCheatEnabled) return;   // nothing was started

        Protections::log << TAG_ANTI() << "Shutting down anti-cheat protections...\n";

        AntiHook::Shutdown();
        DiscordRPC::Shutdown();

#ifndef NDEBUG
        CampusLogging::Shutdown();
#endif
    }
}
