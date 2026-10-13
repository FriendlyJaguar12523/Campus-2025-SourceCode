#pragma once
#include "../../Utils/scan.h"
#include "../../Utils/globals.h"
#include <iostream>
#include "Console.h"

namespace CanvasForceUpdate {
    static void (*ForceUpdateCanvases)() = nullptr;

    static void Initialize() {
        if (!GA) {
            printf("[UI] GameAssembly.dll not loaded – can't find Canvas.ForceUpdateCanvases\n");
            return;
        }

        uintptr_t addr = PatternScan<uintptr_t>(
            "48 89 5C 24 08 57 48 83 EC 20 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 10",
            GA, NtHeaders->OptionalHeader.SizeOfImage
        );

        if (!addr) {
            addr = PatternScan<uintptr_t>(
                "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20",
                GA, NtHeaders->OptionalHeader.SizeOfImage
            );
        }

        if (!addr) {
            printf("[UI] Could not find Canvas.ForceUpdateCanvases – UI redraw may not work.\n");
            return;
        }

        RR::Methods::UnityEngine::Canvas_ForceUpdateCanvases = addr - GA;
        ForceUpdateCanvases = (void(*)())(addr);
        printf("[UI] Canvas.ForceUpdateCanvases found at 0x%p (RVA 0x%X)\n", (void*)addr, (unsigned int)(addr - GA));
    }

    static void ForceRedraw() {
        if (ForceUpdateCanvases) {
            ForceUpdateCanvases();
            printf("[UI] Canvas.ForceUpdateCanvases called – all UI elements should now refresh.\n");
        }
    }
}