#pragma once

#include <iostream>
#include "../Methods.h"
#include "Console.h"
#include "../../Utils/globals.h"
#include "../../Utils/deps/spoofcall/RetSpoof.hpp"
#include "UIThemes.h"

// Gradient recolour - the player's gradient from the server (UIThemes), unless they turned it off.
namespace CampusGradient {
    struct Color { float r, g, b, a; };

    static Color rampAt(const UIThemes::Theme& theme, float t) {
        const UIThemes::Color* stops = theme.stops;
        if (t <= 0.0f) return { stops[0].r, stops[0].g, stops[0].b, 1.0f };
        if (t >= 1.0f) return { stops[3].r, stops[3].g, stops[3].b, 1.0f };
        float f = t * 3.0f;
        int i = (int)f;
        float u = f - (float)i;
        if (i < 0) i = 0;
        if (i >= 3) i = 2;
        return {
            stops[i].r * (1.0f - u) + stops[i + 1].r * u,
            stops[i].g * (1.0f - u) + stops[i + 1].g * u,
            stops[i].b * (1.0f - u) + stops[i + 1].b * u,
            1.0f
        };
    }
}

void (*UIGradient_set_Gradient_O)(void*, void*);

void UIGradient_set_Gradient_H(void* self, void* gradient) {
    UIThemes::Theme theme = UIThemes::Current();
    if (gradient && theme.enabled) {
        typedef void* (*GetKeysFn)(void*);
        typedef void (*SetKeysFn)(void*, void*, void*);

        GetKeysFn getColorKeys = (GetKeysFn)(GA + RR::Methods::UnityGradient::get_colorKeys);
        GetKeysFn getAlphaKeys = (GetKeysFn)(GA + RR::Methods::UnityGradient::get_alphaKeys);
        SetKeysFn setKeys = (SetKeysFn)(GA + RR::Methods::UnityGradient::SetKeys);

        void* colorKeys = spoof_call((const void*)RetAddr, getColorKeys, gradient);
        if (colorKeys) {
            uint64_t len = read<uint64_t>(colorKeys, 24);
            if (len > 0 && len <= 32) {
                uint8_t* keyData = (uint8_t*)colorKeys + 32;
                constexpr size_t STRIDE = 20;

                // Always recolor the gradient with the current theme
                void* alphaKeys = spoof_call((const void*)RetAddr, getAlphaKeys, gradient);
                for (uint64_t i = 0; i < len; i++) {
                    float t = (len > 1) ? (float)i / (float)(len - 1) : 0.5f;
                    auto c = CampusGradient::rampAt(theme, t);
                    *(float*)(keyData + i * STRIDE + 0) = c.r;
                    *(float*)(keyData + i * STRIDE + 4) = c.g;
                    *(float*)(keyData + i * STRIDE + 8) = c.b;
                    *(float*)(keyData + i * STRIDE + 12) = c.a;
                }
                spoof_call((const void*)RetAddr, setKeys, gradient, colorKeys, alphaKeys);
            }
        }
    }
    UIGradient_set_Gradient_O(self, gradient);
}