#pragma once
#include "imgui.h"

// Photoshop-style dark grey theme + the small custom widgets the layout is built from.
namespace ui
{
    // Palette (sRGB, 0-255)
    constexpr ImU32 kGap        = IM_COL32(24, 24, 24, 255);    // between panels
    constexpr ImU32 kPanel      = IM_COL32(50, 50, 50, 255);    // panel body
    constexpr ImU32 kPanelHead  = IM_COL32(40, 40, 40, 255);    // panel title strip
    constexpr ImU32 kCanvas     = IM_COL32(38, 38, 38, 255);    // behind the document
    constexpr ImU32 kText       = IM_COL32(222, 222, 222, 255);
    constexpr ImU32 kTextDim    = IM_COL32(150, 150, 150, 255);
    // Accent = the app icon's gradient: purple -> magenta -> red -> orange.
    constexpr ImU32 kAccent     = IM_COL32(222, 16, 118, 255);     // the gradient's magenta, for single-colour accents
    constexpr ImU32 kAccentSoft = IM_COL32(222, 16, 118, 90);
    constexpr ImU32 kPurple     = IM_COL32(150, 70, 235, 255);
    constexpr ImU32 kWarn       = IM_COL32(232, 150, 60, 255);
    constexpr ImU32 kGood       = IM_COL32(110, 200, 120, 255);
    constexpr ImU32 kBad        = IM_COL32(235, 105, 95, 255);

    enum class Icon { Wand, Brush, Eraser, Eyedropper, Hand, Undo, Redo, Search, Refresh, Fit, Close, Box };

    void ApplyTheme(float dpiScale);
    void LoadFonts(float dpiScale);
    ImFont* Bold();

    // Uppercase title strip for a fixed panel (not collapsible).
    void PanelTitle(const char* title, float height = 0);
    // Collapsible panel section in the right dock. No TreePop needed.
    bool Section(const char* title, bool defaultOpen = true);
    // Vertical tool strip button with a drawn icon.
    bool ToolButton(const char* id, Icon icon, bool active, bool enabled, const char* tooltip);
    // Small square icon button (options bar).
    bool IconButton(const char* id, Icon icon, bool enabled, const char* tooltip);
    // Call-to-action button filled with the icon gradient.
    bool PrimaryButton(const char* label, const ImVec2& size = ImVec2(0, 0), bool enabled = true);
    // Segmented choice: returns true when changed.
    bool Segmented(const char* id, int* value, const char* const labels[], int count, float width = 0);
    // Small pill label ("ADMIN", "MINE").
    void Badge(ImDrawList* dl, ImVec2 pos, const char* text, ImU32 col);
    void DrawIcon(ImDrawList* dl, Icon icon, ImVec2 centre, float size, ImU32 col);
    // The icon gradient at t (0 = purple, 1 = orange); bright > 1 lightens, < 1 darkens.
    ImU32 Grad(float t, float bright = 1.0f, float alpha = 1.0f);
    // Draw the launcher in another gradient (4 evenly spaced RGB stops, 0-1) / back to the Campus one.
    void SetGradient(const float stops[4][3]);
    void ResetGradient();
    // Horizontal gradient fill (left purple -> right orange).
    void GradientRect(ImDrawList* dl, ImVec2 a, ImVec2 b, float bright = 1.0f, float alpha = 1.0f);
    // Text drawn letter by letter along the gradient.
    void GradientText(ImDrawList* dl, ImVec2 pos, const char* text, ImFont* font = nullptr, float size = 0);
    // Label on the left, widget after it at a fixed column (property-grid look).
    void PropLabel(const char* label, float column);
}
