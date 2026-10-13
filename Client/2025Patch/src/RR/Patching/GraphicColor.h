#pragma once
#include <iostream>
#include <cmath>
#include "../../Utils/scan.h"
#include "../../Utils/globals.h"
#include "../../Utils/deps/minhook/include/MinHook.h"
#include "Console.h"
#include "UIThemes.h"

// We assume RR::Methods::UnityEngine::Graphic_set_color is defined elsewhere (Methods.h)

// Original function pointer
void (*Graphic_set_color_O)(void* graphic, float r, float g, float b, float a);

// The accent (replaces Rec Room's orange and tints its white) and the tone for everything else come
// from the player's theme on the server (UIThemes::Current).

// Convert RGB to HSV
static void RgbToHsv(float r, float g, float b, float& h, float& s, float& v) {
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

// Check if color is "Rec Room orange" (accent)
static bool IsOrangeAccent(float r, float g, float b) {
    float h, s, v;
    RgbToHsv(r, g, b, h, s, v);
    return (h >= 0.02f && h <= 0.18f && s >= 0.4f && v >= 0.4f);
}

// Broader white detection – catches near-white colors too
static bool IsWhiteOrNearWhite(float r, float g, float b) {
    return (r > 0.85f && g > 0.85f && b > 0.85f);
}

// Hook implementation
void Graphic_set_color_H(void* graphic, float r, float g, float b, float a) {
    UIThemes::Theme theme = UIThemes::Current();
    if (!theme.enabled) { Graphic_set_color_O(graphic, r, g, b, a); return; }   // gradient off: Rec Room's own colours

    // If the color is orange-ish, replace it with the theme accent
    if (IsOrangeAccent(r, g, b)) {
        const UIThemes::Color& accent = theme.accent;
        r = accent.r;
        g = accent.g;
        b = accent.b;
    }
    // If the color is white/near-white, replace it with the theme accent (scaled)
    else if (IsWhiteOrNearWhite(r, g, b)) {
        const UIThemes::Color& accent = theme.accent;
        float brightness = 0.7f; // 70% brightness
        r = accent.r * brightness;
        g = accent.g * brightness;
        b = accent.b * brightness;
        // Darkened tone (Dark Mode): darker still
        if (theme.tone == 2) {
            r *= 0.3f;
            g *= 0.3f;
            b *= 0.3f;
        }
    }
    else {
        // For other non-accent colors, apply darkening/desaturation if needed
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

    Graphic_set_color_O(graphic, r, g, b, a);
}

// Install the hook
static void InstallGraphicColorHook() {
    if (!GA) {
        printf("[UI] GameAssembly.dll not loaded\n");
        return;
    }

    // Try to use the global slot if already set, else pattern scan
    uintptr_t addr = RR::Methods::UnityEngine::Graphic_set_color;
    if (!addr) {
        addr = PatternScan<uintptr_t>(
            "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 0F 29 74 24 20",
            GA, NtHeaders->OptionalHeader.SizeOfImage
        );
        if (!addr) {
            addr = PatternScan<uintptr_t>(
                "48 89 5C 24 08 57 48 83 EC 20 0F 29 74 24 20",
                GA, NtHeaders->OptionalHeader.SizeOfImage
            );
        }
        if (addr) {
            RR::Methods::UnityEngine::Graphic_set_color = addr - GA;
        }
    }
    else {
        addr = GA + RR::Methods::UnityEngine::Graphic_set_color;
    }

    if (!addr) {
        printf("[UI] Could not find Graphic.set_color – white line may remain.\n");
        return;
    }

    printf("[UI] Graphic.set_color at 0x%p (RVA 0x%X)\n", (void*)addr, (unsigned int)(addr - GA));

    MH_STATUS s = MH_CreateHook((void*)addr, (void*)Graphic_set_color_H, (LPVOID*)&Graphic_set_color_O);
    if (s == MH_OK) s = MH_EnableHook((void*)addr);
    printf("[UI] Graphic.set_color hook -> %s\n", (s == MH_OK ? "OK" : "FAIL"));
}