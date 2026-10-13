#include "Theme.h"
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <string>

namespace ui
{
static ImFont* g_bold = nullptr;

static ImVec4 C(int r, int g, int b, float a = 1.0f) { return ImVec4(r / 255.0f, g / 255.0f, b / 255.0f, a); }

// Full-width strip bounds for panel headers: the window's edges minus a visible scrollbar,
// clipped vertically to the window (so a header never draws over the next panel).
static void StripBounds(float, float& x0, float& x1, float& cy0, float& cy1)
{
    ImVec2 wp = ImGui::GetWindowPos(), ws = ImGui::GetWindowSize();
    float right = ImGui::GetCursorScreenPos().x + ImGui::GetContentRegionAvail().x + ImGui::GetStyle().WindowPadding.x;
    x0 = wp.x; x1 = std::min(wp.x + ws.x, right);
    cy0 = wp.y; cy1 = wp.y + ws.y;
}

void ApplyTheme(float scale)
{
    ImGuiStyle& s = ImGui::GetStyle();
    s = ImGuiStyle();
    s.WindowPadding = ImVec2(8, 8);
    s.FramePadding = ImVec2(7, 4);
    s.ItemSpacing = ImVec2(6, 5);
    s.ItemInnerSpacing = ImVec2(5, 4);
    s.IndentSpacing = 14;
    s.ScrollbarSize = 11;
    s.GrabMinSize = 9;
    s.WindowBorderSize = 1; s.ChildBorderSize = 0; s.PopupBorderSize = 1; s.FrameBorderSize = 1; s.TabBorderSize = 0;
    s.WindowRounding = 3; s.ChildRounding = 0; s.FrameRounding = 2; s.PopupRounding = 3;
    s.ScrollbarRounding = 6; s.GrabRounding = 2; s.TabRounding = 0;
    s.SeparatorTextBorderSize = 1;
    s.WindowTitleAlign = ImVec2(0.0f, 0.5f);

    ImVec4* c = s.Colors;
    c[ImGuiCol_Text] = C(222, 222, 222);
    c[ImGuiCol_TextDisabled] = C(140, 140, 140);
    c[ImGuiCol_WindowBg] = C(24, 24, 24);
    c[ImGuiCol_ChildBg] = C(50, 50, 50);
    c[ImGuiCol_PopupBg] = C(44, 44, 44, 0.98f);
    c[ImGuiCol_Border] = C(20, 20, 20);
    c[ImGuiCol_BorderShadow] = C(0, 0, 0, 0);
    c[ImGuiCol_FrameBg] = C(33, 33, 33);
    c[ImGuiCol_FrameBgHovered] = C(38, 38, 38);
    c[ImGuiCol_FrameBgActive] = C(30, 30, 30);
    c[ImGuiCol_TitleBg] = C(36, 36, 36);
    c[ImGuiCol_TitleBgActive] = C(40, 40, 40);
    c[ImGuiCol_TitleBgCollapsed] = C(36, 36, 36);
    c[ImGuiCol_MenuBarBg] = C(40, 40, 40);
    c[ImGuiCol_ScrollbarBg] = C(0, 0, 0, 0);
    c[ImGuiCol_ScrollbarGrab] = C(88, 88, 88);
    c[ImGuiCol_ScrollbarGrabHovered] = C(110, 110, 110);
    c[ImGuiCol_ScrollbarGrabActive] = C(130, 130, 130);
    c[ImGuiCol_CheckMark] = C(235, 235, 235);
    c[ImGuiCol_SliderGrab] = C(170, 170, 170);
    c[ImGuiCol_SliderGrabActive] = C(222, 16, 118);
    c[ImGuiCol_Button] = C(64, 64, 64);
    c[ImGuiCol_ButtonHovered] = C(76, 76, 76);
    c[ImGuiCol_ButtonActive] = C(88, 88, 88);
    c[ImGuiCol_Header] = C(214, 0, 127, 0.42f);
    c[ImGuiCol_HeaderHovered] = C(74, 74, 74);
    c[ImGuiCol_HeaderActive] = C(214, 0, 127, 0.60f);
    c[ImGuiCol_Separator] = C(30, 30, 30);
    c[ImGuiCol_SeparatorHovered] = C(214, 0, 127, 0.7f);
    c[ImGuiCol_SeparatorActive] = C(222, 16, 118);
    c[ImGuiCol_ResizeGrip] = C(0, 0, 0, 0);
    c[ImGuiCol_ResizeGripHovered] = C(214, 0, 127, 0.5f);
    c[ImGuiCol_ResizeGripActive] = C(222, 16, 118);
    c[ImGuiCol_Tab] = C(40, 40, 40);
    c[ImGuiCol_TabHovered] = C(60, 60, 60);
    c[ImGuiCol_TabSelected] = C(50, 50, 50);
    c[ImGuiCol_TextSelectedBg] = C(214, 0, 127, 0.42f);
    c[ImGuiCol_NavCursor] = C(222, 16, 118);
    c[ImGuiCol_ModalWindowDimBg] = C(0, 0, 0, 0.55f);
    c[ImGuiCol_PlotHistogram] = C(222, 16, 118);
    c[ImGuiCol_PlotHistogramHovered] = C(245, 32, 58);
    s.ScaleAllSizes(scale);
    s.FontScaleDpi = scale;
}

void LoadFonts(float)
{
    ImGuiIO& io = ImGui::GetIO();
    auto exists = [](const wchar_t* p) { return GetFileAttributesW(p) != INVALID_FILE_ATTRIBUTES; };
    if (exists(L"C:\\Windows\\Fonts\\segoeui.ttf")) io.Fonts->AddFontFromFileTTF("C:\\Windows\\Fonts\\segoeui.ttf", 15.0f);
    else io.Fonts->AddFontDefault();
    if (exists(L"C:\\Windows\\Fonts\\seguisb.ttf")) g_bold = io.Fonts->AddFontFromFileTTF("C:\\Windows\\Fonts\\seguisb.ttf", 15.0f);
    ImGui::GetStyle().FontSizeBase = 15.0f;
}

ImFont* Bold() { return g_bold; }

void PanelTitle(const char* title, float height)
{
    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 p = ImGui::GetCursorScreenPos();
    float w = ImGui::GetContentRegionAvail().x;
    float h = height > 0 ? height : ImGui::GetFrameHeight() + 2;
    // span the full child width, including its padding
    float x0, x1, cy0, cy1;
    StripBounds(p.y, x0, x1, cy0, cy1);
    dl->PushClipRect(ImVec2(x0, std::max(p.y, cy0)), ImVec2(x1, std::min(p.y + h, cy1)), false);
    dl->AddRectFilled(ImVec2(x0, p.y), ImVec2(x1, p.y + h), kPanelHead);
    dl->AddLine(ImVec2(x0, p.y + h - 1), ImVec2(x1, p.y + h - 1), kGap);
    char buf[96]; size_t n = 0;
    for (const char* t = title; *t && n + 1 < sizeof(buf); t++) buf[n++] = (char)toupper((unsigned char)*t);
    buf[n] = 0;
    if (g_bold) ImGui::PushFont(g_bold, ImGui::GetStyle().FontSizeBase * 0.86f);
    ImVec2 ts = ImGui::CalcTextSize(buf);
    dl->AddText(ImVec2(p.x, p.y + (h - ts.y) / 2), kTextDim, buf);
    if (g_bold) ImGui::PopFont();
    dl->PopClipRect();
    ImGui::Dummy(ImVec2(w, h));
    (void)w;
}

bool Section(const char* title, bool defaultOpen)
{
    ImGuiStorage* st = ImGui::GetStateStorage();
    ImGuiID id = ImGui::GetID(title);
    bool open = st->GetBool(id, defaultOpen);

    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 p = ImGui::GetCursorScreenPos();
    float x0, x1, cy0, cy1;
    StripBounds(p.y, x0, x1, cy0, cy1);
    float h = ImGui::GetFrameHeight() + 2;
    ImGui::PushID(title);
    ImGui::SetCursorScreenPos(ImVec2(x0, p.y));
    bool clicked = ImGui::InvisibleButton("##sec", ImVec2(x1 - x0, h));
    bool hov = ImGui::IsItemHovered();
    ImGui::PopID();
    if (clicked) { open = !open; st->SetBool(id, open); }
    dl->PushClipRect(ImVec2(x0, std::max(p.y, cy0)), ImVec2(x1, std::min(p.y + h, cy1)), false);   // spans the padding, stays in the window
    dl->AddRectFilled(ImVec2(x0, p.y), ImVec2(x1, p.y + h), hov ? IM_COL32(46, 46, 46, 255) : kPanelHead);
    dl->AddLine(ImVec2(x0, p.y), ImVec2(x1, p.y), kGap);
    dl->AddLine(ImVec2(x0, p.y + h - 1), ImVec2(x1, p.y + h - 1), kGap);
    // chevron
    float cx = p.x + 5, cy = p.y + h / 2, r = 3.5f * ImGui::GetStyle().FontScaleDpi;
    if (open) dl->AddTriangleFilled(ImVec2(cx - r, cy - r * 0.5f), ImVec2(cx + r, cy - r * 0.5f), ImVec2(cx, cy + r * 0.7f), kTextDim);
    else dl->AddTriangleFilled(ImVec2(cx - r * 0.5f, cy - r), ImVec2(cx - r * 0.5f, cy + r), ImVec2(cx + r * 0.7f, cy), kTextDim);
    char buf[96]; size_t n = 0;
    for (const char* t = title; *t && *t != '#' && n + 1 < sizeof(buf); t++) buf[n++] = (char)toupper((unsigned char)*t);
    buf[n] = 0;
    if (g_bold) ImGui::PushFont(g_bold, ImGui::GetStyle().FontSizeBase * 0.86f);
    ImVec2 ts = ImGui::CalcTextSize(buf);
    dl->AddText(ImVec2(p.x + 16, p.y + (h - ts.y) / 2), hov ? kText : IM_COL32(190, 190, 190, 255), buf);
    if (g_bold) ImGui::PopFont();
    dl->PopClipRect();
    return open;
}

void DrawIcon(ImDrawList* dl, Icon icon, ImVec2 c, float s, ImU32 col)
{
    auto P = [&](float x, float y) { return ImVec2(c.x + x * s, c.y + y * s); };
    float t = std::max(1.3f, s * 0.085f);
    switch (icon)
    {
    case Icon::Wand:
        dl->AddLine(P(-0.36f, 0.36f), P(0.12f, -0.12f), col, t * 1.4f);
        dl->AddLine(P(0.30f, -0.46f), P(0.30f, -0.14f), col, t);
        dl->AddLine(P(0.14f, -0.30f), P(0.46f, -0.30f), col, t);
        dl->AddLine(P(0.19f, -0.41f), P(0.41f, -0.19f), col, t * 0.7f);
        dl->AddLine(P(0.19f, -0.19f), P(0.41f, -0.41f), col, t * 0.7f);
        break;
    case Icon::Brush:
        dl->AddLine(P(0.40f, -0.40f), P(-0.02f, 0.02f), col, t * 1.3f);
        dl->AddQuadFilled(P(-0.02f, -0.06f), P(0.06f, 0.02f), P(-0.10f, 0.30f), P(-0.30f, 0.10f), col);
        dl->AddTriangleFilled(P(-0.30f, 0.10f), P(-0.10f, 0.30f), P(-0.42f, 0.42f), col);
        break;
    case Icon::Eraser:
    {
        ImVec2 q[4] = { P(0.05f, -0.40f), P(0.42f, -0.03f), P(-0.05f, 0.44f), P(-0.42f, 0.07f) };
        dl->AddPolyline(q, 4, col, ImDrawFlags_Closed, t);
        dl->AddQuadFilled(P(-0.42f, 0.07f), P(-0.14f, -0.21f), P(0.23f, 0.16f), P(-0.05f, 0.44f), col);
        dl->AddLine(P(-0.05f, 0.46f), P(0.42f, 0.46f), col, t * 0.8f);
        break;
    }
    case Icon::Eyedropper:
        dl->AddLine(P(-0.38f, 0.38f), P(0.10f, -0.10f), col, t * 1.4f);
        dl->AddCircleFilled(P(0.24f, -0.24f), s * 0.16f, col);
        dl->AddLine(P(0.00f, -0.26f), P(0.26f, 0.00f), col, t * 1.2f);
        dl->AddCircleFilled(P(-0.40f, 0.40f), s * 0.05f, col);
        break;
    case Icon::Hand:
        for (int i = 0; i < 4; i++)
        {
            float x = -0.24f + i * 0.14f, top = i == 0 || i == 3 ? -0.18f : -0.34f;
            dl->AddLine(P(x, top), P(x, 0.05f), col, t * 1.5f);
        }
        dl->AddLine(P(-0.36f, 0.00f), P(-0.22f, 0.18f), col, t * 1.5f);
        dl->AddRectFilled(P(-0.28f, 0.02f), P(0.22f, 0.40f), col, s * 0.12f);
        break;
    case Icon::Undo:
    case Icon::Redo:
    {
        float dir = icon == Icon::Undo ? 1.0f : -1.0f;
        dl->PathArcTo(P(0.0f, 0.06f), s * 0.30f, dir > 0 ? -3.14159f * 0.95f : -3.14159f * 0.05f, dir > 0 ? 3.14159f * 0.35f : 3.14159f * 0.65f, 16);
        dl->PathStroke(col, ImDrawFlags_None, t * 1.2f);
        ImVec2 a = P(-0.30f * dir, 0.02f);
        dl->AddTriangleFilled(ImVec2(a.x - 0.16f * s * dir, a.y), ImVec2(a.x + 0.14f * s * dir, a.y), ImVec2(a.x - 0.01f * s * dir, a.y - 0.22f * s), col);
        break;
    }
    case Icon::Search:
        dl->AddCircle(P(-0.08f, -0.08f), s * 0.26f, col, 20, t * 1.2f);
        dl->AddLine(P(0.12f, 0.12f), P(0.38f, 0.38f), col, t * 1.6f);
        break;
    case Icon::Refresh:
        dl->PathArcTo(c, s * 0.30f, -3.14159f * 0.25f, 3.14159f * 1.4f, 20);
        dl->PathStroke(col, ImDrawFlags_None, t * 1.2f);
        dl->AddTriangleFilled(P(0.10f, -0.42f), P(0.34f, -0.30f), P(0.12f, -0.12f), col);
        break;
    case Icon::Fit:
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                dl->AddLine(P(0.38f * sx, 0.38f * sy), P(0.12f * sx, 0.38f * sy), col, t);
                dl->AddLine(P(0.38f * sx, 0.38f * sy), P(0.38f * sx, 0.12f * sy), col, t);
            }
        break;
    case Icon::Close:
        dl->AddLine(P(-0.28f, -0.28f), P(0.28f, 0.28f), col, t * 1.1f);
        dl->AddLine(P(-0.28f, 0.28f), P(0.28f, -0.28f), col, t * 1.1f);
        break;
    case Icon::Box:
        dl->AddRect(P(-0.36f, -0.30f), P(0.36f, 0.34f), col, s * 0.05f, 0, t);
        dl->AddLine(P(-0.36f, -0.08f), P(0.36f, -0.08f), col, t);
        dl->AddLine(P(-0.10f, -0.08f), P(-0.10f, 0.06f), col, t);
        dl->AddLine(P(0.10f, -0.08f), P(0.10f, 0.06f), col, t);
        break;
    }
}

bool ToolButton(const char* id, Icon icon, bool active, bool enabled, const char* tooltip)
{
    float sz = ImGui::GetFrameHeight() * 1.45f;
    ImVec2 p = ImGui::GetCursorScreenPos();
    ImGui::BeginDisabled(!enabled);
    bool clicked = ImGui::InvisibleButton(id, ImVec2(sz, sz));
    bool hov = ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled);
    ImGui::EndDisabled();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    if (active)
    {
        dl->AddRectFilled(p, ImVec2(p.x + sz, p.y + sz), IM_COL32(28, 28, 28, 255), 3);
        // active tool: a thin bar in the icon gradient (purple at the top -> orange at the bottom)
        for (int i = 0; i < 8; i++)
            dl->AddRectFilledMultiColor(ImVec2(p.x, p.y + sz * i / 8), ImVec2(p.x + 2, p.y + sz * (i + 1) / 8),
                                        Grad(i / 8.0f), Grad(i / 8.0f), Grad((i + 1) / 8.0f), Grad((i + 1) / 8.0f));
    }
    else if (hov && enabled) dl->AddRectFilled(p, ImVec2(p.x + sz, p.y + sz), IM_COL32(66, 66, 66, 255), 3);
    ImU32 col = !enabled ? IM_COL32(95, 95, 95, 255) : active ? IM_COL32(245, 245, 245, 255) : IM_COL32(200, 200, 200, 255);
    DrawIcon(dl, icon, ImVec2(p.x + sz / 2, p.y + sz / 2), sz * 0.62f, col);
    if (hov && tooltip) ImGui::SetTooltip("%s", tooltip);
    return clicked && enabled;
}

bool IconButton(const char* id, Icon icon, bool enabled, const char* tooltip)
{
    float sz = ImGui::GetFrameHeight();
    ImVec2 p = ImGui::GetCursorScreenPos();
    ImGui::BeginDisabled(!enabled);
    bool clicked = ImGui::InvisibleButton(id, ImVec2(sz, sz));
    bool hov = ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled);
    bool held = ImGui::IsItemActive();
    ImGui::EndDisabled();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    if (enabled && (hov || held)) dl->AddRectFilled(p, ImVec2(p.x + sz, p.y + sz), held ? IM_COL32(80, 80, 80, 255) : IM_COL32(66, 66, 66, 255), 2);
    DrawIcon(dl, icon, ImVec2(p.x + sz / 2, p.y + sz / 2), sz * 0.66f, enabled ? IM_COL32(210, 210, 210, 255) : IM_COL32(95, 95, 95, 255));
    if (hov && tooltip) ImGui::SetTooltip("%s", tooltip);
    return clicked && enabled;
}

// The gradient everything coloured is drawn with. Default: the stops sampled from
// CampusStudioIcon.png; SetGradient swaps in the player's in-game gradient (evenly spaced, like the
// patch draws it).
static const float kIconSt[4] = { 0.0f, 0.38f, 0.68f, 1.0f };
static const float kIconCs[4][3] = { { 106, 0, 200 }, { 214, 0, 127 }, { 245, 32, 58 }, { 255, 152, 0 } };
static float g_st[4] = { 0.0f, 0.38f, 0.68f, 1.0f };
static float g_cs[4][3] = { { 106, 0, 200 }, { 214, 0, 127 }, { 245, 32, 58 }, { 255, 152, 0 } };

// ImGui's own highlight colours (selection, list headers, sliders) follow the gradient's middle.
static void AccentStyle()
{
    ImVec4* c = ImGui::GetStyle().Colors;
    ImVec4 a = ImGui::ColorConvertU32ToFloat4(Grad(0.5f));
    auto with = [&](float alpha) { return ImVec4(a.x, a.y, a.z, alpha); };
    c[ImGuiCol_SliderGrabActive] = with(1); c[ImGuiCol_Header] = with(0.42f); c[ImGuiCol_HeaderActive] = with(0.6f);
    c[ImGuiCol_SeparatorHovered] = with(0.7f); c[ImGuiCol_SeparatorActive] = with(1);
    c[ImGuiCol_ResizeGripHovered] = with(0.5f); c[ImGuiCol_ResizeGripActive] = with(1);
    c[ImGuiCol_TextSelectedBg] = with(0.42f); c[ImGuiCol_NavCursor] = with(1);
    c[ImGuiCol_PlotHistogram] = with(1); c[ImGuiCol_PlotHistogramHovered] = ImGui::ColorConvertU32ToFloat4(Grad(0.7f));
}

void SetGradient(const float stops[4][3])
{
    for (int i = 0; i < 4; i++)
    {
        g_st[i] = i / 3.0f;
        for (int j = 0; j < 3; j++) g_cs[i][j] = std::clamp(stops[i][j], 0.0f, 1.0f) * 255.0f;
    }
    AccentStyle();
}

void ResetGradient()
{
    memcpy(g_st, kIconSt, sizeof g_st);
    memcpy(g_cs, kIconCs, sizeof g_cs);
    AccentStyle();
}

ImU32 Grad(float t, float bright, float alpha)
{
    t = std::clamp(t, 0.0f, 1.0f);
    int i = t < g_st[1] ? 0 : t < g_st[2] ? 1 : 2;
    float k = (t - g_st[i]) / (g_st[i + 1] - g_st[i]);
    int c[3];
    for (int j = 0; j < 3; j++)
    {
        float v = g_cs[i][j] + (g_cs[i + 1][j] - g_cs[i][j]) * k;
        v = bright >= 1 ? v + (255 - v) * (bright - 1) : v * bright;
        c[j] = (int)std::clamp(v, 0.0f, 255.0f);
    }
    return IM_COL32(c[0], c[1], c[2], (int)(255 * std::clamp(alpha, 0.0f, 1.0f)));
}

void GradientRect(ImDrawList* dl, ImVec2 a, ImVec2 b, float bright, float alpha)
{
    const int n = 16;   // slices, so all four stops show (one quad would blend purple straight to orange)
    float w = (b.x - a.x) / n;
    for (int i = 0; i < n; i++)
    {
        ImU32 c0 = Grad((float)i / n, bright, alpha), c1 = Grad((float)(i + 1) / n, bright, alpha);
        dl->AddRectFilledMultiColor(ImVec2(a.x + w * i, a.y), ImVec2(i == n - 1 ? b.x : a.x + w * (i + 1) + 0.5f, b.y), c0, c1, c1, c0);
    }
}

void GradientText(ImDrawList* dl, ImVec2 pos, const char* text, ImFont* font, float size)
{
    if (!font) font = ImGui::GetFont();
    if (size <= 0) size = ImGui::GetFontSize();
    float total = font->CalcTextSizeA(size, 1e9f, 0, text).x, x = pos.x;
    for (const char* c = text; *c; c++)
    {
        char ch[2] = { *c, 0 };
        float cw = font->CalcTextSizeA(size, 1e9f, 0, ch).x;
        dl->AddText(font, size, ImVec2(x, pos.y), Grad(total > 0 ? (x - pos.x + cw / 2) / total : 0, 1.08f), ch);
        x += cw;
    }
}

bool PrimaryButton(const char* label, const ImVec2& sizeArg, bool enabled)
{
    const ImGuiStyle& st = ImGui::GetStyle();
    const char* end = strstr(label, "##");
    std::string text(label, end ? end : label + strlen(label));
    ImVec2 ts = ImGui::CalcTextSize(text.c_str());
    float avail = ImGui::GetContentRegionAvail().x;
    ImVec2 size(sizeArg.x > 0 ? sizeArg.x : sizeArg.x < 0 ? std::max(4.0f, avail + sizeArg.x) : ts.x + st.FramePadding.x * 2,
                sizeArg.y > 0 ? sizeArg.y : ImGui::GetFrameHeight());
    ImVec2 p = ImGui::GetCursorScreenPos();
    ImGui::BeginDisabled(!enabled);
    bool clicked = ImGui::InvisibleButton(label, size);
    bool hov = ImGui::IsItemHovered(), held = ImGui::IsItemActive();
    ImGui::EndDisabled();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 q(p.x + size.x, p.y + size.y);
    if (enabled)
    {
        GradientRect(dl, p, q, held ? 0.82f : hov ? 1.14f : 1.0f);
        dl->AddRect(p, q, IM_COL32(0, 0, 0, 70));
    }
    else
    {
        dl->AddRectFilled(p, q, IM_COL32(62, 62, 62, 255));
        dl->AddRect(p, q, IM_COL32(28, 28, 28, 255));
    }
    ImVec2 tp(p.x + (size.x - ts.x) / 2, p.y + (size.y - ts.y) / 2);
    // a light gradient (Candy, Monochrome's white end...) gets dark text, so it stays readable
    ImVec4 mid = ImGui::ColorConvertU32ToFloat4(Grad(0.5f));
    bool light = mid.x * 0.299f + mid.y * 0.587f + mid.z * 0.114f > 0.62f;
    if (enabled && !light) dl->AddText(ImVec2(tp.x, tp.y + 1), IM_COL32(0, 0, 0, 90), text.c_str());
    dl->AddText(tp, !enabled ? IM_COL32(130, 130, 130, 255) : light ? IM_COL32(22, 22, 28, 255) : IM_COL32(255, 255, 255, 255), text.c_str());
    return clicked && enabled;
}

bool Segmented(const char* id, int* value, const char* const labels[], int count, float width)
{
    ImGui::PushID(id);
    bool changed = false;
    float w = width > 0 ? width / count : 0;
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(0, 0));
    for (int i = 0; i < count; i++)
    {
        if (i) ImGui::SameLine();
        bool on = *value == i;
        if (on)
        {
            ImGui::PushStyleColor(ImGuiCol_Button, C(92, 92, 92));
            ImGui::PushStyleColor(ImGuiCol_ButtonHovered, C(100, 100, 100));
        }
        if (ImGui::Button(labels[i], ImVec2(w, 0)) && !on) { *value = i; changed = true; }
        if (on)
        {
            ImGui::PopStyleColor(2);
            ImVec2 a = ImGui::GetItemRectMin(), b = ImGui::GetItemRectMax();
            GradientRect(ImGui::GetWindowDrawList(), ImVec2(a.x + 1, b.y - 2), ImVec2(b.x - 1, b.y));
        }
    }
    ImGui::PopStyleVar();
    ImGui::PopID();
    return changed;
}

void Badge(ImDrawList* dl, ImVec2 pos, const char* text, ImU32 col)
{
    float fs = ImGui::GetFontSize() * 0.72f;
    ImFont* f = g_bold ? g_bold : ImGui::GetFont();
    ImVec2 ts = f->CalcTextSizeA(fs, 1e9f, 0, text);
    float padx = fs * 0.45f, h = fs + 3;
    dl->AddRectFilled(pos, ImVec2(pos.x + ts.x + padx * 2, pos.y + h), (col & 0x00FFFFFF) | (70u << 24), h / 2);
    dl->AddText(f, fs, ImVec2(pos.x + padx, pos.y + (h - ts.y) / 2), col, text);
}

void PropLabel(const char* label, float column)
{
    ImGui::AlignTextToFramePadding();
    ImGui::TextColored(C(170, 170, 170), "%s", label);
    ImGui::SameLine(column);
}
}
