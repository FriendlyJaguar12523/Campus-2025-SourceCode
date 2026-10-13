#pragma once

#include <windows.h>
#include <d3d11.h>          // <-- added for ID3D11Device/Context

namespace ImGuiInspector {
    void Initialize(HWND hwnd, ID3D11Device* device, ID3D11DeviceContext* context);
    void Shutdown();
    void Render();
    void ToggleVisibility();
    bool IsVisible();
}