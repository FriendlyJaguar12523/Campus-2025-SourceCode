#pragma once

#include <iostream>
#include <iomanip>
#include <algorithm>
#include "../Methods.h"
#include "Console.h"
#include "ServerConfiguration.h"
#include "RefereeBypass.h"
#include "Il2cppResolution.h"
#include "Il2CppApi.h"
#include "../../Utils/DebugTools.h"
#include "CheatQuitSuppress.h"
#include "CheatKill.h"
#include "CustomNotify.h"
#include "CustomChat.h"
#include "SendRequest.h"
#include "UIGradient.h"
#include "RadioPatch.h"
#include "UIThemes.h"
#include "GraphicColor.h"
#include "CanvasRendererColor.h"
#include "CanvasForceUpdate.h"
#include "PhotonRedirect.h"
#include "LogoSwap.h"
#include "RemoteVideoGate.h"
// #include "ModSettingsTabInjector.h"   // DISABLED – causing crashes
#include "../../Utils/globals.h"
#include "../../Utils/scan.h"
#include "../../Utils/deps/minhook/include/MinHook.h"

namespace RR::Patches {

    static void hook(const std::string& tag, uintptr_t abs, void* detour, void** orig, const char* label);

    void Resolve() {
        RetAddr = PatternScan<uint64_t*>("FF 23", GA, NtHeaders->OptionalHeader.SizeOfImage);
        if (RetAddr) {
            std::cout << "[Campus] RetSpoof gadget (FF 23): 0x" << std::hex << (uintptr_t)RetAddr << std::dec << "\n";
        }
        else {
            std::cout << "[Campus] RetSpoof gadget (FF 23): NOT FOUND! Trying alternative...\n";
            RetAddr = PatternScan<uint64_t*>("FF 25", GA, NtHeaders->OptionalHeader.SizeOfImage);
            if (RetAddr) {
                std::cout << "[Campus] RetSpoof gadget (FF 25): 0x" << std::hex << (uintptr_t)RetAddr << std::dec << "\n";
            }
            else {
                std::cout << "[Campus] WARNING: No RetSpoof gadget found! spoof_call will fail!\n";
                RetAddr = PatternScan<uint64_t*>("E8 ?? ?? ?? ?? FF", GA, NtHeaders->OptionalHeader.SizeOfImage);
                if (RetAddr) {
                    std::cout << "[Campus] Found fallback gadget: 0x" << std::hex << (uintptr_t)RetAddr << std::dec << "\n";
                }
            }
        }

        auto assm = GetModuleHandleA("GameAssembly.dll");

        auto tryExport = [&](const char* name) -> uintptr_t {
            void* p = spoof_call(RetAddr, GetProcAddress, assm, name);
            return p ? (uintptr_t)p - GA : 0;
            };
        RR::Methods::Il2cpp::il2cpp_string_new = tryExport("il2cpp_string_new");
        RR::Methods::Il2cpp::il2cpp_object_get_class = tryExport("il2cpp_object_get_class");
        RR::Methods::Il2cpp::il2cpp_object_new = tryExport("il2cpp_object_new");
        // Metadata lookup - these two are what replace the baked HTTPRequest.SetHeader RVA.
        RR::Methods::Il2cpp::il2cpp_class_get_method_from_name = tryExport("il2cpp_class_get_method_from_name");
        RR::Methods::Il2cpp::il2cpp_class_get_parent = tryExport("il2cpp_class_get_parent");

        auto fallback = [&](const char* name, uintptr_t& slot) {
            if (slot) return;
            void* p = UnityPlayerScan::find_il2cpp_api(name);
            if (p) slot = (uintptr_t)p - GA;
            };
        fallback("il2cpp_string_new", RR::Methods::Il2cpp::il2cpp_string_new);
        fallback("il2cpp_object_get_class", RR::Methods::Il2cpp::il2cpp_object_get_class);
        fallback("il2cpp_object_new", RR::Methods::Il2cpp::il2cpp_object_new);
        fallback("il2cpp_class_get_method_from_name", RR::Methods::Il2cpp::il2cpp_class_get_method_from_name);
        fallback("il2cpp_class_get_parent", RR::Methods::Il2cpp::il2cpp_class_get_parent);

        auto mark = [](uintptr_t v) {
            return v ? (CLR_GREEN "OK" CLR_RESET) : (CLR_RED "FAIL" CLR_RESET);
            };
        std::cout << TAG_INIT() << "il2cpp: string_new " << mark(RR::Methods::Il2cpp::il2cpp_string_new)
            << "  object_get_class " << mark(RR::Methods::Il2cpp::il2cpp_object_get_class)
            << "  object_new " << mark(RR::Methods::Il2cpp::il2cpp_object_new)
            << "  get_method " << mark(RR::Methods::Il2cpp::il2cpp_class_get_method_from_name)
            << "  get_parent " << mark(RR::Methods::Il2cpp::il2cpp_class_get_parent)
            << "\n";

        if (!RR::Methods::Il2cpp::il2cpp_string_new ||
            !RR::Methods::Il2cpp::il2cpp_object_get_class ||
            !RR::Methods::Il2cpp::il2cpp_object_new) {
            std::cout << TAG_WARN() << "Some il2cpp functions failed to resolve! URL rewriting may fail.\n";
        }

        // ---- Unity engine methods ------------------------------------------------------
        //
        // NOT resolved here. This function runs from InitThread, which waits only for
        // GameAssembly.dll to be LOADED - il2cpp_init has not finished at this point, so
        // asking the runtime for a domain or sweeping its assemblies here returns null
        // and can fault. Doing exactly that is what crashed the game a few seconds after
        // launch and left every lookup empty.
        //
        // RR::Patches::ResolveDeferred() does this instead, from the worker in main.cpp
        // that waits for Il2CppApi::RuntimeReady().
    }

    // ---------------------------------------------------------------------------------
    // Metadata-dependent resolution. Both MUST be called only after
    // Il2CppApi::RuntimeReady() returns true - see the note above.
    //
    // Split in two on purpose. The cheat-quit hook is NOT optional tooling: without it
    // CheatManager sees the injected DLL and calls SessionManager.FatalApplicationQuit,
    // which is the "logout then the game closes" in the session log. It has to run in a
    // normal session. The Unity method table is only used by the inspector and the UI
    // dumper, and its lookups fall back to sweeping every loaded assembly, so that stays
    // behind the debug switch.
    // ---------------------------------------------------------------------------------


    /// Debug tooling only - the inspector and Ctrl+Shift+U dumper read these slots.
    void ResolveUnityMethods() {
        Il2CppApi::ReportOnce();

        auto resolveUnity = [&](uintptr_t& slot, const char* ns, const char* type,
                                const char* method, int argc, const char* label) {
            if (slot != 0) return;
            void* fn = Il2CppApi::FindUnityMethod(ns, type, method, argc);
            if (fn) {
                slot = (uintptr_t)fn - GA;
                std::cout << "[Resolver] " << label << " RVA = 0x"
                          << std::hex << slot << std::dec << "\n";
            }
            else {
                std::cout << TAG_WARN() << "[Resolver] " << label << " NOT FOUND\n";
            }
            };

        using namespace RR::Methods;
        resolveUnity(UnityEngine::GameObject_Find,               "UnityEngine", "GameObject", "Find",             1, "GameObject.Find");
        resolveUnity(UnityEngine::GameObject_get_transform,      "UnityEngine", "GameObject", "get_transform",    0, "GameObject.get_transform");
        resolveUnity(UnityEngine::GameObject_SetActive,          "UnityEngine", "GameObject", "SetActive",        1, "GameObject.SetActive");
        // get_name is declared on UnityEngine.Object, not GameObject.
        resolveUnity(UnityEngine::GameObject_get_name,           "UnityEngine", "Object",     "get_name",         0, "Object.get_name");
        resolveUnity(UnityEngine::Transform_GetChild,            "UnityEngine", "Transform",  "GetChild",         1, "Transform.GetChild");
        resolveUnity(UnityEngine::Transform_SetParent,           "UnityEngine", "Transform",  "SetParent",        1, "Transform.SetParent");
        resolveUnity(UnityEngine::Transform_GetSiblingIndex,     "UnityEngine", "Transform",  "GetSiblingIndex",  0, "Transform.GetSiblingIndex");
        resolveUnity(UnityEngine::Transform_SetSiblingIndex,     "UnityEngine", "Transform",  "SetSiblingIndex",  1, "Transform.SetSiblingIndex");
        resolveUnity(UnityEngine::Object_Instantiate_GameObject, "UnityEngine", "Object",     "Instantiate",      1, "Object.Instantiate");
        resolveUnity(UnityEngine::UnityEvent_AddListener,        "UnityEngine.Events", "UnityEvent", "AddListener", 1, "UnityEvent.AddListener");
    }

    static void hook(const std::string& tag, uintptr_t abs, void* detour, void** orig, const char* label) {
        if (!abs) {
            std::cout << tag << label << " - SKIPPED (address is null)\n";
            return;
        }
        MH_STATUS s = MH_CreateHook((void*)abs, detour, (LPVOID*)orig);
        if (s == MH_OK) s = MH_EnableHook((void*)abs);
        std::cout << tag << label
            << CLR_DIM " @ 0x" << std::hex << abs << std::dec << CLR_RESET
            << "  -> " << (s == MH_OK ? CLR_GREEN "OK" CLR_RESET
                : CLR_ERR "FAIL" CLR_RESET) << "\n";
    }

    void Patch() {
        if (MH_Initialize() != MH_OK) {
            std::cout << "[Campus] MH_Initialize FAILED\n";
            return;
        }

        std::cout << "\n";

        hook(TAG_REFEREE(), Referee + RR::Methods::Referee::Check1, (void*)nop_H, (void**)&nop_O, "Check1");
        hook(TAG_REFEREE(), Referee + RR::Methods::Referee::Check2, (void*)nop_H, (void**)&nop_O, "Check2");
        hook(TAG_REFEREE(), Referee + RR::Methods::Referee::Check3, (void*)nop_H, (void**)&nop_O, "Check3");
        hook(TAG_REFEREE(), Referee + RR::Methods::Referee::Check4, (void*)nop_H, (void**)&nop_O, "Check4");
        std::cout << "\n";

        hook(TAG_TLS(), GA + RR::Methods::LegacyTlsAuthentication::NotifyServerCertificate,
            (void*)nop_H, (void**)&nop_O, "NotifyServerCertificate");
        std::cout << "\n";

        // ---- cheat-quit machinery: OPT-IN, default OFF ---------------------------------
        //
        // Every hook-based attempt to defeat the PC self-quit broke the game a different
        // way (crash, then loading soft-lock, then crash again) - Referee escalates when it
        // sees these hooks. Meanwhile the game loads and plays fine WITHOUT them (it just
        // self-quits ~40s in). So this whole block is now behind a marker file and OFF by
        // default: the shipped DLL loads the game normally and the dev panel is usable.
        //
        // To experiment with quit suppression, drop campus_cheatquit.txt next to the DLL.
        if (DebugTools::Flag("cheatquit")) {
            std::cout << TAG_ANTI() << "cheat-quit suppression ENABLED (experimental)\n";
            hook(TAG_ANTI(), GA + CheatQuit::FatalApplicationQuit_RVA,
                (void*)CheatQuit::FatalQuit_H, (void**)&CheatQuit::oFatalQuit,
                "SessionManager.FatalApplicationQuit");
            hook(TAG_ANTI(), GA + CheatQuit::ModuleScanClosureRVA,
                (void*)CheatQuit::NoOp_H, (void**)&CheatQuit::oModuleScan, "CheatManager.moduleScan");
            hook(TAG_ANTI(), GA + CheatQuit::OnTimeCheatRVA,
                (void*)CheatQuit::NoOp_H, (void**)&CheatQuit::oTime, "CheatManager.OnTimeCheatDetected");
            hook(TAG_ANTI(), GA + CheatQuit::OnObscuredTypeCheatRVA,
                (void*)CheatQuit::NoOp_H, (void**)&CheatQuit::oObscured, "CheatManager.OnObscuredTypeCheatDetected");
            hook(TAG_ANTI(), GA + CheatQuit::OnAdvancedMoveCheatRVA,
                (void*)CheatQuit::NoOp_H, (void**)&CheatQuit::oMove, "CheatManager.OnAdvancedMovementCheatDetected");
        }
        std::cout << "\n";

        // ---- CheatManager neutralise: OPT-IN, default OFF ------------------------------
        //
        // Surgical, cannot crash: lets Awake/Start run (the game reads CheatManager.Instance
        // during init, so it must exist), and only forces CheckForDUIDMismatch to report
        // "no mismatch" so the device-id check never quits. Detectors + scene-load callback
        // are no-op'd as a backstop. This is also a diagnostic: if the game still quits with
        // this on, the DUID path is ruled out and the culprit is Referee (native). See
        // CheatKill.h for the full history of what was tried and why deleting the object
        // (the earlier "killcheat") crashes.
        //
        // Drop campus_killcheat.txt next to the DLL to try it.
        if (DebugTools::Flag("killcheat")) {
            std::cout << TAG_ANTI() << "CheatManager neutralise enabled (DUID check forced ok)\n";
            hook(TAG_ANTI(), GA + CheatKill::DuidCheckRVA,
                (void*)CheatKill::DuidCheck_H, (void**)&CheatKill::oDuid, "CheatManager.CheckForDUIDMismatch");
            hook(TAG_ANTI(), GA + CheatKill::SceneLoadedRVA,
                (void*)CheatKill::NoOp_H, (void**)&CheatKill::oScene, "CheatManager.sceneLoaded (backstop)");
            hook(TAG_ANTI(), GA + CheatKill::OnTimeRVA,
                (void*)CheatKill::NoOp_H, (void**)&CheatKill::oTime, "CheatManager.OnTimeCheatDetected (backstop)");
            hook(TAG_ANTI(), GA + CheatKill::OnObscuredRVA,
                (void*)CheatKill::NoOp_H, (void**)&CheatKill::oObscured, "CheatManager.OnObscuredTypeCheatDetected (backstop)");
            hook(TAG_ANTI(), GA + CheatKill::OnMoveRVA,
                (void*)CheatKill::NoOp_H, (void**)&CheatKill::oMove, "CheatManager.OnAdvancedMovementCheatDetected (backstop)");
        }
        std::cout << "\n";

        // Capture the NotificationManager instance so the patch can pop its own toasts.
        // Awake runs once when the manager is created; the hook only stores `this`.
        hook(TAG_INIT(), GA + CustomNotify::AwakeRVA,
            (void*)CustomNotify::Awake_H, (void**)&CustomNotify::Awake_O,
            "NotificationManager.Awake");
        std::cout << "\n";

        // Capture the room chat menu so the dev panel can send chat. Initialize runs
        // when the chat UI is first set up; the hook only stores `this`.
        hook(TAG_INIT(), GA + CustomChat::InitRVA,
            (void*)CustomChat::Init_H, (void**)&CustomChat::Init_O,
            "RoomChatExpandedMenu.Initialize");
        std::cout << "\n";

        hook(TAG_HTTP(), GA + RR::Methods::HTTPManager::SendRequest,
            (void*)SendRequest_H, (void**)&SendRequest_O, "HTTPManager.SendRequest");
        std::cout << "\n";

        hook(TAG_HTTP(), GA + RR::Methods::HttpResponse::get_DataAsText,
            (void*)GetDataAsText_H, (void**)&GetDataAsText_O, "HttpResponse.get_DataAsText");
        std::cout << "\n";

        hook(TAG_HTTP(), GA + RR::Methods::HttpResponse::Receive,
            (void*)Receive_H, (void**)&Receive_O, "HttpResponse.Receive");
        std::cout << "\n";

        hook(TAG_UI(), GA + RR::Methods::UIGradient::set_Gradient,
            (void*)UIGradient_set_Gradient_H, (void**)&UIGradient_set_Gradient_O,
            "UIGradient.set_Gradient");
        std::cout << "\n";

        {
            uintptr_t verifyAddr = GA + RR::Methods::ImageSignature::Verify;
            if (verifyAddr) {
                MH_STATUS s = MH_CreateHook((void*)verifyAddr, (void*)VerifyImageSig_H, (LPVOID*)&VerifyImageSig_O);
                if (s == MH_OK) {
                    s = MH_EnableHook((void*)verifyAddr);
                    std::cout << "[IMAGES] " << "VerifyImageSig hook @ 0x" << std::hex << verifyAddr << std::dec
                        << " -> " << (s == MH_OK ? CLR_GREEN "OK" CLR_RESET : CLR_RED "FAIL" CLR_RESET) << "\n";
                }
                else {
                    std::cout << "[IMAGES] " << "VerifyImageSig hook creation FAILED (status: " << s << ")\n";
                }
            }
            else {
                std::cout << "[IMAGES] " << "VerifyImageSig address is null - hook skipped\n";
            }
        }

        // The UI gradient is read from the server once the game has signed in (SendRequest -> UIThemes::SetAccount).

        // Remote Video Player: let the "Video Url" field take http(s) addresses (RemoteVideoGate.h)
        hook(TAG_INIT(), GA + RemoteVideoGate::IsHttpOrHttpsRVA,
            (void*)RemoteVideoGate::IsHttp_H, (void**)&RemoteVideoGate::o_IsHttp, "RemoteVideoPlayer url check");
        std::cout << "\n";

        InstallGraphicColorHook();
        std::cout << "\n";
        InstallCanvasRendererColorHook();
        std::cout << "\n";
        CanvasForceUpdate::Initialize();
        std::cout << "\n";
        PhotonRedirect::Install();
        std::cout << "\n";

        // Campus logo in place of Rec Room's (two textures in the UI bundle, swapped as they are handed to
        // Direct3D - see LogoSwap.h). campus_nologo.txt next to the DLL switches it off.
        if (DebugTools::Flag("nologo")) std::cout << TAG_INIT() << "Campus logo: OFF (campus_nologo.txt)\n";
        else LogoSwap::Install();
        std::cout << "\n";

        // ================================================================
        // INJECT MOD SETTINGS TAB – DISABLED (was causing black screens/crashes)
        // ================================================================
        // ModSettingsTabInjector::InstallModSettingsTabInjector();
        // std::cout << "\n";

        std::cout << "\n";
        std::cout << TAG_INIT() << "all hooks installed  |  target = "
            << CLR_YELLOW << CAMPUS_NAMESERVER_HOST << CLR_RESET << "\n";
        std::cout << TAG_INIT() << "radio patches: DISABLED\n";
        std::cout << TAG_INIT() << "UI Themes: ENABLED (6 themes, Ctrl+Shift+G)\n";
        std::cout << TAG_INIT() << "Graphic Color Hook: ENABLED\n";
        std::cout << TAG_INIT() << "CanvasRenderer Color Hook: ENABLED\n";
        std::cout << TAG_INIT() << "Canvas ForceUpdate: ENABLED\n";
        std::cout << TAG_INIT() << "ImageSignature: HOOK (forces true)\n";
        std::cout << TAG_INIT() << "Photon: CampusNet @ " << CAMPUS_PHOTON_SERVER << "\n";

        if (!SendRequest_O) {
            std::cout << TAG_WARN() << "SendRequest hook failed! URL rewriting will not work.\n";
        }
        if (!GetDataAsText_O) {
            std::cout << TAG_WARN() << "GetDataAsText hook failed! Response parsing will not work.\n";
        }
        if (!VerifyImageSig_O) {
            std::cout << TAG_WARN() << "VerifyImageSig hook failed! Image signatures will not be bypassed.\n";
        }
    }
}