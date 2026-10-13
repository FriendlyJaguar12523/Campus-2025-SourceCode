#pragma once

// ================================================================================
// Campus menu - a sprite-drawn overlay, built from the PNGs in CampusPatch\Assets.
//
// Every pixel on screen comes from those files (background.png, button.png,
// toggle_on/off.png, menu_button.png, player.png). No ImGui widget is used for the
// look: ImGui supplies the textured-quad draw list, the window transform and hit
// testing, and nothing else. Styling is zeroed out below so the host window
// contributes no border, padding or background of its own.
//
// Rows are whole-sprite swaps - toggle_on.png / toggle_off.png are complete button
// images with the pill baked in, so a toggle is one AddImage with the source picked
// by state. That is how the art is authored; do not try to composite a separate pill.
// ================================================================================

#include "../imgui/imgui.h"
#include "AssetTextures.h"
#include <string>
#include <vector>
#include <functional>

namespace CampusMenu {

    // ---- state ------------------------------------------------------------------

    struct Toggle {
        const char* label;
        bool value;
        std::function<void(bool)> onChange;
    };

    struct Tab {
        const char* name;
        std::vector<Toggle> toggles;
    };

    inline std::vector<Tab> g_tabs;
    inline int   g_activeTab = 0;
    inline bool  g_visible = false;
    inline bool  g_built = false;
    inline ID3D11Device* g_device = nullptr;

    // Registered by whoever owns the actual settings, so this file stays pure UI.
    inline void AddTab(const char* name) { g_tabs.push_back(Tab{ name, {} }); }
    inline void AddToggle(const char* label, bool initial, std::function<void(bool)> onChange) {
        if (g_tabs.empty()) AddTab("General");
        g_tabs.back().toggles.push_back(Toggle{ label, initial, std::move(onChange) });
    }

    inline void Toggle_(bool& v) { v = !v; }

    // ---- drawing ----------------------------------------------------------------

    // Centre a string inside a rect using the current font.
    inline void CenteredText(ImDrawList* dl, ImVec2 min, ImVec2 max,
                             const char* text, ImU32 col, float scale = 1.0f) {
        ImFont* font = ImGui::GetFont();
        float size = ImGui::GetFontSize() * scale;
        ImVec2 ts = font->CalcTextSizeA(size, FLT_MAX, 0.0f, text);
        ImVec2 pos((min.x + max.x - ts.x) * 0.5f, (min.y + max.y - ts.y) * 0.5f);
        dl->AddText(font, size, pos, col, text);
    }

    inline void Render() {
        if (!g_visible) return;

        auto bg      = AssetTextures::LoadFile(g_device, "background.png");
        auto btn     = AssetTextures::LoadFile(g_device, "button.png");
        auto tglOn   = AssetTextures::LoadFile(g_device, "toggle_on.png");
        auto tglOff  = AssetTextures::LoadFile(g_device, "toggle_off.png");

        // Panel size: keep background.png's aspect ratio, sized to the viewport so it
        // looks the same at any resolution instead of being pinned to pixel sizes.
        ImGuiIO& io = ImGui::GetIO();
        float panelW = io.DisplaySize.x * 0.55f;
        float panelH = bg ? panelW * ((float)bg.height / (float)bg.width) : panelW * 0.6f;

        ImGui::SetNextWindowSize(ImVec2(panelW, panelH), ImGuiCond_Always);
        ImGui::SetNextWindowPos(
            ImVec2(io.DisplaySize.x * 0.5f, io.DisplaySize.y * 0.42f),
            ImGuiCond_FirstUseEver, ImVec2(0.5f, 0.5f));

        // Strip every bit of ImGui's own chrome - the art provides all of it.
        ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0, 0));
        ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0.0f);
        ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0, 0, 0, 0));

        ImGui::Begin("##campusmenu", nullptr,
            ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
            ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoCollapse |
            ImGuiWindowFlags_NoSavedSettings);

        ImDrawList* dl = ImGui::GetWindowDrawList();
        ImVec2 p0 = ImGui::GetWindowPos();
        ImVec2 p1 = ImVec2(p0.x + panelW, p0.y + panelH);

        if (bg) dl->AddImage(bg.srv, p0, p1);
        else    dl->AddRectFilled(p0, p1, IM_COL32(40, 12, 70, 235), 12.0f);

        // ---- tab strip ----------------------------------------------------------
        const float pad = panelW * 0.03f;
        float tabW = (panelW - pad * 2) / (g_tabs.empty() ? 1 : (float)g_tabs.size());
        float tabH = panelH * 0.11f;
        float y = p0.y + panelH * 0.06f;

        for (size_t i = 0; i < g_tabs.size(); i++) {
            ImVec2 a(p0.x + pad + tabW * i + 4, y);
            ImVec2 b(a.x + tabW - 8, y + tabH);

            ImGui::SetCursorScreenPos(a);
            ImGui::InvisibleButton(g_tabs[i].name, ImVec2(b.x - a.x, b.y - a.y));
            bool hovered = ImGui::IsItemHovered();
            if (ImGui::IsItemClicked()) g_activeTab = (int)i;

            // The active tab is the same sprite at full brightness; inactive ones are
            // dimmed via tint rather than a second asset, so only one image is needed.
            ImU32 tint = ((int)i == g_activeTab) ? IM_COL32(255, 255, 255, 255)
                       : hovered                ? IM_COL32(210, 210, 210, 220)
                                                : IM_COL32(150, 150, 150, 180);
            if (btn) dl->AddImage(btn.srv, a, b, ImVec2(0, 0), ImVec2(1, 1), tint);
            else     dl->AddRectFilled(a, b, IM_COL32(90, 30, 150, 220), 8.0f);

            CenteredText(dl, a, b, g_tabs[i].name, IM_COL32(255, 255, 255, 255), 1.05f);
        }

        // ---- rows ---------------------------------------------------------------
        if (g_activeTab >= 0 && g_activeTab < (int)g_tabs.size()) {
            auto& tab = g_tabs[g_activeTab];

            float rowH = panelH * 0.13f;
            float rowW = panelW * 0.42f;
            float ry = y + tabH + panelH * 0.06f;

            for (size_t i = 0; i < tab.toggles.size(); i++) {
                auto& t = tab.toggles[i];
                ImVec2 a(p0.x + pad, ry + (rowH + panelH * 0.025f) * i);
                ImVec2 b(a.x + rowW, a.y + rowH);

                if (b.y > p1.y - 8) break;   // ran out of panel - no clipping artefacts

                ImGui::SetCursorScreenPos(a);
                ImGui::InvisibleButton(t.label, ImVec2(b.x - a.x, b.y - a.y));
                bool hovered = ImGui::IsItemHovered();
                if (ImGui::IsItemClicked()) {
                    t.value = !t.value;
                    if (t.onChange) t.onChange(t.value);
                }

                auto& img = t.value ? tglOn : tglOff;
                ImU32 tint = hovered ? IM_COL32(255, 255, 255, 255)
                                     : IM_COL32(232, 232, 232, 255);

                if (img) dl->AddImage(img.srv, a, b, ImVec2(0, 0), ImVec2(1, 1), tint);
                else     dl->AddRectFilled(a, b, t.value ? IM_COL32(60, 140, 50, 230)
                                                         : IM_COL32(70, 25, 120, 230), 10.0f);

                // Label sits left of the pill, which occupies roughly the last 12%.
                ImVec2 textMax(b.x - (b.x - a.x) * 0.14f, b.y);
                CenteredText(dl, a, textMax, t.label, IM_COL32(255, 255, 255, 255), 1.1f);
            }

            if (tab.toggles.empty()) {
                ImVec2 a(p0.x + pad, ry);
                ImVec2 b(p1.x - pad, ry + rowH);
                CenteredText(dl, a, b, "Nothing here yet.", IM_COL32(210, 200, 230, 200));
            }
        }

        ImGui::End();
        ImGui::PopStyleColor();
        ImGui::PopStyleVar(2);
    }

    inline void SetDevice(ID3D11Device* device) { g_device = device; }
    inline void ToggleVisibility() { g_visible = !g_visible; }
    inline bool IsVisible() { return g_visible; }
}
