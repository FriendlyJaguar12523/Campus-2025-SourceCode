#pragma once
#include <iostream>
#include <cmath>
#include "../../Utils/scan.h"
#include "../../Utils/globals.h"
#include "../../Utils/deps/minhook/include/MinHook.h"
#include "Console.h"
#include "UIThemes.h"

// We assume RR::Methods::UnityEngine::CanvasRenderer_SetColor is defined elsewhere

// Original function pointer
void (*CanvasRenderer_SetColor_O)(void* renderer, float r, float g, float b, float a);

// Accent + tone: the player's theme from the server (UIThemes::Current), like GraphicColor.

// Convert RGB to HSV
static void CanvasRgbToHsv(float r, float g, float b, float& h, float& s, float& v) {
    float mx = r; if (g > mx) mx = g; if (b > mx) mx = b;
    float mn = r; if (g < mn) mn = g; if (b < mn) mn = b;
    v = mx;
    float d = mx - mn;
    s = (mx == 0.0f) ? 0.0f : d / mx;
    if (d == 0.0f) { h = 0.0f; return; }
    if (mx == r) h = fmodf((g - b) / d, 6.0f);
    else if (mx == g) h = (b - r) / d + 2.0f;
    else h = (r - g) / d + 4.0f;
    h /= 6.0f;
    if (h < 0.0f) h += 1.0f;
}

static bool CanvasIsOrangeAccent(float r, float g, float b) {
    float h, s, v;
    CanvasRgbToHsv(r, g, b, h, s, v);
    return (h >= 0.02f && h <= 0.18f && s >= 0.4f && v >= 0.4f);
}

static bool CanvasIsPureWhite(float r, float g, float b) {
    return (r > 0.99f && g > 0.99f && b > 0.99f);
}

// Hook implementation
void CanvasRenderer_SetColor_H(void* renderer, float r, float g, float b, float a) {
    UIThemes::Theme theme = UIThemes::Current();
    if (!theme.enabled) { CanvasRenderer_SetColor_O(renderer, r, g, b, a); return; }   // gradient off: Rec Room's own colours

    if (CanvasIsOrangeAccent(r, g, b)) {
        const UIThemes::Color& accent = theme.accent;
        r = accent.r;
        g = accent.g;
        b = accent.b;
    }
    else if (CanvasIsPureWhite(r, g, b)) {
        if (theme.campus) {
            // Campus: keep white
        }
        else {
            // Make white transparent – this will hide the line
            a = 0.0f;
        }
    }
    else {
        if (theme.tone == 1) { // greyscale (Monochrome)
            float gray = r * 0.299f + g * 0.587f + b * 0.114f;
            r = gray;
            g = gray;
            b = gray;
        }
        else if (theme.tone == 2) { // darkened (Dark Mode)
            float factor = 0.30f;
            r = r * factor;
            g = g * factor;
            b = b * factor;
        }
    }

    CanvasRenderer_SetColor_O(renderer, r, g, b, a);
}

// Install the hook
static void InstallCanvasRendererColorHook() {
    if (!GA) {
        printf("[UI] GameAssembly.dll not loaded\n");
        return;
    }

    // Try to use the global slot if already set, else pattern scan
    uintptr_t addr = RR::Methods::UnityEngine::CanvasRenderer_SetColor;
    if (!addr) {
        addr = PatternScan<uintptr_t>(
            "48 89 5C 24 08 57 48 83 EC 20 0F 29 74 24 20",
            GA, NtHeaders->OptionalHeader.SizeOfImage
        );
        if (!addr) {
            addr = PatternScan<uintptr_t>(
                "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20",
                GA, NtHeaders->OptionalHeader.SizeOfImage
            );
        }
        if (addr) {
            RR::Methods::UnityEngine::CanvasRenderer_SetColor = addr - GA;
        }
    }
    else {
        addr = GA + RR::Methods::UnityEngine::CanvasRenderer_SetColor;
    }

    if (!addr) {
        printf("[UI] Could not find CanvasRenderer.SetColor – white line may remain.\n");
        return;
    }

    printf("[UI] CanvasRenderer.SetColor at 0x%p (RVA 0x%X)\n", (void*)addr, (unsigned int)(addr - GA));

    MH_STATUS s = MH_CreateHook((void*)addr, (void*)CanvasRenderer_SetColor_H, (LPVOID*)&CanvasRenderer_SetColor_O);
    if (s == MH_OK) s = MH_EnableHook((void*)addr);
    printf("[UI] CanvasRenderer.SetColor hook -> %s\n", (s == MH_OK ? "OK" : "FAIL"));
}