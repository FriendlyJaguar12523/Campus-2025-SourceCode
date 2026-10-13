// ModSettingsTabInjector.h
#pragma once

#include "../../Utils/globals.h"
#include "../../Utils/deps/minhook/include/MinHook.h"
#include "../../Utils/deps/spoofcall/RetSpoof.hpp"
#include "Console.h"
#include "../Methods.h"

namespace ModSettingsTabInjector {

    void (*AppNavMenu_OpenPage_O)(void*, Il2cppString*) = nullptr;
    static bool g_injected = false;
    static bool g_injection_attempted = false;

    static Il2cppString* CreateIl2CppString(const char* str) {
        using Il2cppStringNewFn = Il2cppString * (*)(const char*);
        auto strNew = (Il2cppStringNewFn)(GA + RR::Methods::Il2cpp::il2cpp_string_new);
        if (!strNew) return nullptr;
        return strNew(str);
    }

    // Helper: recursively search for a GameObject containing specific text (TMP or Text).
    static void* FindGameObjectByTextRecursive(void* transform, const wchar_t* targetText) {
        if (!transform) return nullptr;
        void* go = *(void**)((uintptr_t)transform + 0x10);
        if (go) {
            Il2cppString** textPtr = (Il2cppString**)((uintptr_t)go + 0xE8);
            if (*textPtr) {
                wchar_t* textW = (wchar_t*)((uintptr_t)*textPtr + 4);
                int len = (*textPtr)->Length;
                if (len > 0 && wcsncmp(textW, targetText, len) == 0) return go;
            }
            textPtr = (Il2cppString**)((uintptr_t)go + 0x48);
            if (*textPtr) {
                wchar_t* textW = (wchar_t*)((uintptr_t)*textPtr + 4);
                int len = (*textPtr)->Length;
                if (len > 0 && wcsncmp(textW, targetText, len) == 0) return go;
            }
        }
        auto getChild = (void* (*)(void*, int))(GA + RR::Methods::UnityEngine::Transform_GetChild);
        if (!getChild) return nullptr;
        int childCount = *(int*)((uintptr_t)transform + 0x38);
        for (int i = 0; i < childCount; i++) {
            void* childTransform = getChild(transform, i);
            if (!childTransform) continue;
            void* result = FindGameObjectByTextRecursive(childTransform, targetText);
            if (result) return result;
        }
        return nullptr;
    }

    // Helper: Find a GameObject by name using GameObject.Find.
    static void* FindGameObject(const char* name) {
        if (!RR::Methods::UnityEngine::GameObject_Find) return nullptr;
        using FindFn = void* (*)(Il2cppString*);
        auto find = (FindFn)(GA + RR::Methods::UnityEngine::GameObject_Find);
        Il2cppString* str = CreateIl2CppString(name);
        if (!str) return nullptr;
        return find(str);
    }

    // The main injection function – registers the panel and adds the tab.
    static void DoInjection(void* appNavMenu) {
        if (g_injected) return;

        // Resolve required Unity functions.
        auto findGO = (void* (*)(Il2cppString*))(GA + RR::Methods::UnityEngine::GameObject_Find);
        auto instantiate = (void* (*)(void*, void*))(GA + RR::Methods::UnityEngine::Object_Instantiate_GameObject);
        auto setIdent = (void(*)(void*, Il2cppString*))(GA + RR::Methods::AppNavPanel::set_Identifier);
        auto registerPanel = (void(*)(void*, void*))(GA + RR::Methods::AppNavPanelSwitcher::RegisterPanel);
        auto getTransform = (void* (*)(void*))(GA + RR::Methods::UnityEngine::GameObject_get_transform);
        auto getChild = (void* (*)(void*, int))(GA + RR::Methods::UnityEngine::Transform_GetChild);
        auto setParent = (void* (*)(void*, void*))(GA + RR::Methods::UnityEngine::Transform_SetParent);
        auto getSibling = (int (*)(void*))(GA + RR::Methods::UnityEngine::Transform_GetSiblingIndex);
        auto setSibling = (void (*)(void*, int))(GA + RR::Methods::UnityEngine::Transform_SetSiblingIndex);
        auto setActive = (void* (*)(void*, bool))(GA + RR::Methods::UnityEngine::GameObject_SetActive);
        auto openPage = (void(*)(void*, Il2cppString*))(GA + RR::Methods::AppNav::OpenPage);

        if (!findGO || !instantiate || !setIdent || !registerPanel ||
            !getTransform || !getChild || !setParent || !getSibling || !setSibling || !setActive || !openPage) {
            printf("[ModSettings] Required functions not resolved – injection skipped.\n");
            return;
        }

        // --- 1. Register the new panel (clone DevTools) ---
        void* panelSwitcher = *(void**)((uintptr_t)appNavMenu + 0x30);
        if (!panelSwitcher) {
            printf("[ModSettings] Could not get panelSwitcher.\n");
            return;
        }

        Il2cppString* devToolsName = CreateIl2CppString("DevToolsPanel");
        void* templatePanelGO = findGO(devToolsName);
        if (!templatePanelGO) {
            devToolsName = CreateIl2CppString("DevTools");
            templatePanelGO = findGO(devToolsName);
        }
        if (!templatePanelGO) {
            printf("[ModSettings] Could not find DevTools panel; injection skipped.\n");
            return;
        }

        void* newPanelGO = instantiate(templatePanelGO, nullptr);
        if (!newPanelGO) {
            printf("[ModSettings] Instantiate failed.\n");
            return;
        }

        void* panelComponent = *(void**)((uintptr_t)newPanelGO + 0x30);
        if (!panelComponent) {
            printf("[ModSettings] Could not get AppNavPanel component.\n");
            return;
        }
        setIdent(panelComponent, CreateIl2CppString("ModSettings"));
        registerPanel(panelSwitcher, panelComponent);
        printf("[ModSettings] Panel 'ModSettings' registered.\n");

        // --- 2. Find the Settings panel and its tab bar ---
        void* settingsGO = FindGameObject("SettingsPanel");
        if (!settingsGO) settingsGO = FindGameObject("Settings");
        if (!settingsGO) {
            printf("[ModSettings] Could not find Settings panel by name – trying text search.\n");
            // Fallback: search for "Controls" text and go up.
            void* controlsText = FindGameObjectByTextRecursive(getTransform(appNavMenu), L"Controls");
            if (controlsText) {
                void* parentTransform = *(void**)((uintptr_t)getTransform(controlsText) + 0x30);
                if (parentTransform) {
                    settingsGO = *(void**)((uintptr_t)parentTransform + 0x10);
                    printf("[ModSettings] Found Settings panel via 'Controls' text.\n");
                }
            }
        }
        if (!settingsGO) {
            printf("[ModSettings] Could not find Settings panel – no tab added.\n");
            g_injected = true;
            return;
        }

        void* settingsTransform = getTransform(settingsGO);
        if (!settingsTransform) {
            printf("[ModSettings] Settings panel has no transform.\n");
            g_injected = true;
            return;
        }

        // --- 3. Find the tab bar container (parent of the tabs) ---
        // We'll search for a child with text "Dev Only" or "Misc".
        void* devOnlyText = FindGameObjectByTextRecursive(settingsTransform, L"Dev Only");
        if (!devOnlyText) {
            devOnlyText = FindGameObjectByTextRecursive(settingsTransform, L"Misc");
        }
        if (!devOnlyText) {
            // Try "General"
            devOnlyText = FindGameObjectByTextRecursive(settingsTransform, L"General");
        }
        if (!devOnlyText) {
            printf("[ModSettings] Could not find any tab text (Dev Only / Misc / General).\n");
            g_injected = true;
            return;
        }

        // Get the button (parent of the text)
        void* textTransform = getTransform(devOnlyText);
        void* buttonTransform = *(void**)((uintptr_t)textTransform + 0x30);
        if (!buttonTransform) {
            printf("[ModSettings] Could not get parent of tab text.\n");
            g_injected = true;
            return;
        }
        void* buttonGO = *(void**)((uintptr_t)buttonTransform + 0x10);
        if (!buttonGO) {
            printf("[ModSettings] Could not get button GameObject.\n");
            g_injected = true;
            return;
        }

        // The container is the parent of the button.
        void* containerTransform = *(void**)((uintptr_t)buttonTransform + 0x30);
        if (!containerTransform) {
            printf("[ModSettings] Could not get container transform.\n");
            g_injected = true;
            return;
        }

        // --- 4. Clone the button and insert it ---
        void* newButtonGO = instantiate(buttonGO, nullptr);
        if (!newButtonGO) {
            printf("[ModSettings] Instantiate button failed.\n");
            g_injected = true;
            return;
        }
        void* newButtonTransform = getTransform(newButtonGO);
        setParent(newButtonTransform, containerTransform);

        // Insert at the end (after all existing buttons).
        int childCount = *(int*)((uintptr_t)containerTransform + 0x38);
        setSibling(newButtonTransform, childCount);

        // --- 5. Change button text to "Mod" ---
        bool textChanged = false;
        int btnChildCount = *(int*)((uintptr_t)newButtonTransform + 0x38);
        for (int i = 0; i < btnChildCount; i++) {
            void* childTransform = getChild(newButtonTransform, i);
            if (!childTransform) continue;
            void* childGO = *(void**)((uintptr_t)childTransform + 0x10);
            if (!childGO) continue;
            Il2cppString** textPtr = (Il2cppString**)((uintptr_t)childGO + 0xE8);
            if (*textPtr) {
                *textPtr = CreateIl2CppString("Mod");
                textChanged = true;
                break;
            }
            textPtr = (Il2cppString**)((uintptr_t)childGO + 0x48);
            if (*textPtr) {
                *textPtr = CreateIl2CppString("Mod");
                textChanged = true;
                break;
            }
        }
        if (!textChanged) {
            printf("[ModSettings] Could not change button text.\n");
        }

        // --- 6. Set the click handler (if possible) ---
        // We'll try to copy the onClick from the original button.
        void* origButtonComponent = *(void**)((uintptr_t)buttonGO + 0x30);
        void* newButtonComponent = *(void**)((uintptr_t)newButtonGO + 0x30);
        if (origButtonComponent && newButtonComponent) {
            // The onClick field is at offset 0x70 (typical for Unity UI Button).
            void* origOnClick = *(void**)((uintptr_t)origButtonComponent + 0x70);
            void* newOnClick = *(void**)((uintptr_t)newButtonComponent + 0x70);
            if (origOnClick && newOnClick) {
                // Copy the persistent calls – but this is complex.
                // We'll just leave it as is.
                printf("[ModSettings] Tab button cloned; click handler not set.\n");
                printf("[ModSettings] Use OpenPage(\"ModSettings\") to open the panel.\n");
            }
        }

        setActive(newButtonGO, true);
        g_injected = true;
        printf("[ModSettings] Mod tab added to Settings panel.\n");
    }

    // Hook for AppNavMenu.OpenPage – logs all page identifiers and triggers on "Settings".
    void AppNavMenu_OpenPage_H(void* self, Il2cppString* pageIdentifier) {
        // Log the identifier for debugging.
        if (pageIdentifier) {
            std::string id;
            if (pageIdentifier->Length > 0) {
                const wchar_t* wstr = reinterpret_cast<const wchar_t*>((uintptr_t)pageIdentifier + 4);
                int len = pageIdentifier->Length;
                int utf8Len = WideCharToMultiByte(CP_UTF8, 0, wstr, len, nullptr, 0, nullptr, nullptr);
                if (utf8Len > 0) {
                    id.resize(utf8Len);
                    WideCharToMultiByte(CP_UTF8, 0, wstr, len, &id[0], utf8Len, nullptr, nullptr);
                }
            }
            printf("[ModSettings] OpenPage called with identifier: '%s'\n", id.c_str());

            // Trigger injection if the identifier contains "Settings" (case-insensitive).
            if (!g_injected && (id.find("Settings") != std::string::npos || id.find("settings") != std::string::npos)) {
                printf("[ModSettings] Settings panel opened – injecting...\n");
                DoInjection(self);
            }
        }

        // Call original.
        if (AppNavMenu_OpenPage_O) {
            AppNavMenu_OpenPage_O(self, pageIdentifier);
        }
    }

    // Fallback: attempt injection after a short delay (in case Settings was already open).
    static void DelayedInjection() {
        Sleep(5000); // Wait 5 seconds for the UI to settle.
        if (!g_injected) {
            // Get AppNavMenu instance.
            using GetInstanceFn = void* (*)();
            auto getInstance = (GetInstanceFn)(GA + RR::Methods::AppNav::GetInstance);
            if (getInstance) {
                void* appNavMenu = getInstance();
                if (appNavMenu) {
                    printf("[ModSettings] Fallback injection attempt...\n");
                    DoInjection(appNavMenu);
                }
            }
        }
    }

    // Install the hook – called from PatchInstaller.
    static void InstallModSettingsTabInjector() {
        if (!GA) {
            printf("[ModSettings] GameAssembly.dll not loaded\n");
            return;
        }

        uintptr_t addr = GA + RR::Methods::AppNav::OpenPage;
        printf("[ModSettings] AppNavMenu.OpenPage at 0x%p (RVA 0x%X)\n", (void*)addr, (unsigned int)RR::Methods::AppNav::OpenPage);

        MH_STATUS s = MH_CreateHook((void*)addr, (void*)AppNavMenu_OpenPage_H, (LPVOID*)&AppNavMenu_OpenPage_O);
        if (s == MH_OK) s = MH_EnableHook((void*)addr);
        printf("[ModSettings] AppNavMenu.OpenPage hook -> %s\n", (s == MH_OK ? "OK" : "FAIL"));
        if (s != MH_OK) {
            printf("[ModSettings] Hook creation failed with status %d\n", s);
        }

        // Start a fallback thread to try injection after a delay.
        CreateThread(nullptr, 0, [](LPVOID) -> DWORD {
            DelayedInjection();
            return 0;
            }, nullptr, 0, nullptr);
    }

} // namespace ModSettingsTabInjector