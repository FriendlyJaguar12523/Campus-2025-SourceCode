#include "imgui_inspector.h"
#include "../imgui/imgui.h"
#include "../imgui/backends/imgui_impl_win32.h"
#include "../imgui/backends/imgui_impl_dx11.h"
#include "../../Utils/globals.h"
#include "../UI/DevPanel.h"
#include <d3d11.h>

// ================================================================================
// ImGui host (debug builds)
//
// This file used to hold the "Unity Explorer" inspector - a GameObject tree walked live
// every frame from the Present hook. It crashed the game (wrong method addresses, hard-coded
// object offsets, managed calls from an unattached thread) and has been REMOVED on request:
// the only UI left is the dev panel (INSERT), which now carries just the Avatar Fun tab.
//
// What is kept is the part everything else needed: bringing ImGui up against the game's own
// swapchain and drawing one frame per Present. The namespace and function names are unchanged
// so main.cpp's hook code did not have to move.
//
// Render() runs on the D3D present thread. Managed calls from a thread the runtime has not
// attached corrupt state, and AvatarFun calls into the game, so the thread is attached once
// before the first frame is drawn (cheap after that, and retried until it succeeds because at
// the login screen the runtime may not be up yet).
// ================================================================================

namespace ImGuiInspector {

    static bool g_initialized = false;

    void Initialize(HWND hwnd, ID3D11Device* device, ID3D11DeviceContext* context) {
        if (g_initialized) return;

        IMGUI_CHECKVERSION();
        ImGui::CreateContext();
        ImGuiIO& io = ImGui::GetIO();
        io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;

        ImGui_ImplWin32_Init(hwnd);
        ImGui_ImplDX11_Init(device, context);

        g_initialized = true;
    }

    void Shutdown() {
        if (!g_initialized) return;
        ImGui_ImplDX11_Shutdown();
        ImGui_ImplWin32_Shutdown();
        ImGui::DestroyContext();
        g_initialized = false;
    }

    // The explorer window is gone; nothing here is ever "visible" any more. Kept so callers still link.
    void ToggleVisibility() {}
    bool IsVisible() { return false; }

    void Render() {
        if (!g_initialized) return;
        if (!DevPanel::IsVisible()) return;

        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();

        DevPanel::Render();

        ImGui::Render();
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
    }
}
