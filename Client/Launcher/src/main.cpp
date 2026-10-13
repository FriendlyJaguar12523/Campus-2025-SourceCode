// Campus launcher: a fixed-size window with no Windows frame - App.cpp draws everything, its top bar
// is the title bar (WM_NCHITTEST asks App::IsCaption). This file is the Win32 / D3D11 / ImGui plumbing.
#include "App.h"
#include "HostsFix.h"
#include "Settings.h"
#include "Tex.h"
#include "Theme.h"
#include "Util.h"
#include "imgui.h"
#include "imgui_impl_dx11.h"
#include "imgui_impl_win32.h"
#include <d3d11.h>
#include <dwmapi.h>
#include <windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <objbase.h>
#include <wincodec.h>
#include <vector>


static ID3D11Device* g_dev = nullptr;
static ID3D11DeviceContext* g_ctx = nullptr;
static IDXGISwapChain* g_swap = nullptr;
static ID3D11RenderTargetView* g_rtv = nullptr;
static UINT g_resizeW = 0, g_resizeH = 0;
static HANDLE g_single = nullptr;
static App* g_app = nullptr;

// An update starts the new exe before this one exits: let go of the one-launcher lock first.
void ReleaseSingleInstance() { if (g_single) { ReleaseMutex(g_single); CloseHandle(g_single); g_single = nullptr; } }

// --shot <file.png> (testing): the window as a PNG, then quit.
static void SaveBackBuffer(const std::wstring& path)
{
    ID3D11Texture2D* back = nullptr;
    if (FAILED(g_swap->GetBuffer(0, IID_PPV_ARGS(&back)))) return;
    D3D11_TEXTURE2D_DESC d{};
    back->GetDesc(&d);
    d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ; d.MiscFlags = 0;
    ID3D11Texture2D* staging = nullptr;
    if (SUCCEEDED(g_dev->CreateTexture2D(&d, nullptr, &staging)))
    {
        g_ctx->CopyResource(staging, back);
        D3D11_MAPPED_SUBRESOURCE m{};
        if (SUCCEEDED(g_ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m)))
        {
            std::vector<uint8_t> px((size_t)d.Width * d.Height * 4);
            for (UINT y = 0; y < d.Height; y++) memcpy(&px[(size_t)y * d.Width * 4], (uint8_t*)m.pData + (size_t)y * m.RowPitch, (size_t)d.Width * 4);
            g_ctx->Unmap(staging, 0);
            for (size_t i = 0; i < px.size(); i += 4) px[i + 3] = 255;
            IWICImagingFactory* wic = nullptr;
            IWICStream* st = nullptr; IWICBitmapEncoder* enc = nullptr; IWICBitmapFrameEncode* fr = nullptr;
            if (SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic))) &&
                SUCCEEDED(wic->CreateStream(&st)) && SUCCEEDED(st->InitializeFromFilename(path.c_str(), GENERIC_WRITE)) &&
                SUCCEEDED(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, &enc)) && SUCCEEDED(enc->Initialize(st, WICBitmapEncoderNoCache)) &&
                SUCCEEDED(enc->CreateNewFrame(&fr, nullptr)) && SUCCEEDED(fr->Initialize(nullptr)))
            {
                WICPixelFormatGUID fmt = GUID_WICPixelFormat32bppRGBA;
                fr->SetSize(d.Width, d.Height);
                fr->SetPixelFormat(&fmt);
                if (fmt != GUID_WICPixelFormat32bppRGBA)   // the PNG encoder may only take BGRA
                    for (size_t i = 0; i < px.size(); i += 4) std::swap(px[i], px[i + 2]);
                fr->WritePixels(d.Height, d.Width * 4, (UINT)px.size(), px.data());
                fr->Commit(); enc->Commit();
            }
            if (fr) fr->Release(); if (enc) enc->Release(); if (st) st->Release(); if (wic) wic->Release();
        }
        staging->Release();
    }
    back->Release();
}

static void CreateRenderTarget()
{
    ID3D11Texture2D* back = nullptr;
    g_swap->GetBuffer(0, IID_PPV_ARGS(&back));
    if (back) { g_dev->CreateRenderTargetView(back, nullptr, &g_rtv); back->Release(); }
}

static void CleanupRenderTarget() { if (g_rtv) { g_rtv->Release(); g_rtv = nullptr; } }

static bool CreateDevice(HWND hwnd)
{
    DXGI_SWAP_CHAIN_DESC sd{};
    sd.BufferCount = 2;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferDesc.RefreshRate.Numerator = 60; sd.BufferDesc.RefreshRate.Denominator = 1;
    sd.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hwnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    D3D_FEATURE_LEVEL got;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels, 2, D3D11_SDK_VERSION, &sd, &g_swap, &g_dev, &got, &g_ctx);
    if (hr == DXGI_ERROR_UNSUPPORTED)   // no GPU driver: the software renderer is plenty for this window
        hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, levels, 2, D3D11_SDK_VERSION, &sd, &g_swap, &g_dev, &got, &g_ctx);
    if (FAILED(hr)) return false;
    CreateRenderTarget();
    return true;
}

static void CleanupDevice()
{
    CleanupRenderTarget();
    if (g_swap) { g_swap->Release(); g_swap = nullptr; }
    if (g_ctx) { g_ctx->Release(); g_ctx = nullptr; }
    if (g_dev) { g_dev->Release(); g_dev = nullptr; }
}

static void DarkTitleBar(HWND h)
{
    BOOL dark = TRUE;
    if (FAILED(DwmSetWindowAttribute(h, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &dark, sizeof(dark))))
        DwmSetWindowAttribute(h, 19, &dark, sizeof(dark));
    COLORREF caption = RGB(40, 40, 40), text = RGB(200, 200, 200), border = RGB(24, 24, 24);
    DwmSetWindowAttribute(h, 35 /* DWMWA_CAPTION_COLOR */, &caption, sizeof(caption));
    DwmSetWindowAttribute(h, 36 /* DWMWA_TEXT_COLOR */, &text, sizeof(text));
    DwmSetWindowAttribute(h, 34 /* DWMWA_BORDER_COLOR */, &border, sizeof(border));
}

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND, UINT, WPARAM, LPARAM);

static LRESULT WINAPI WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (ImGui_ImplWin32_WndProcHandler(hwnd, msg, wp, lp)) return true;
    switch (msg)
    {
    case WM_NCCALCSIZE:
        if (wp) return 0;                            // no frame: the client area is the whole window
        break;
    case WM_NCHITTEST:
    {
        POINT pt{ GET_X_LPARAM(lp), GET_Y_LPARAM(lp) };
        ScreenToClient(hwnd, &pt);
        return g_app && g_app->IsCaption((float)pt.x, (float)pt.y) ? HTCAPTION : HTCLIENT;
    }
    case WM_SIZE:
        if (wp == SIZE_MINIMIZED) return 0;
        g_resizeW = (UINT)LOWORD(lp); g_resizeH = (UINT)HIWORD(lp);
        return 0;
    case WM_SYSCOMMAND:
        if ((wp & 0xfff0) == SC_KEYMENU) return 0;   // no Alt menu
        break;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int WINAPI wWinMain(HINSTANCE hi, HINSTANCE, PWSTR, int)
{
    // Elevated hosts-file fix: when the launcher relaunches itself with "--fix-hosts <ip>"
    // (via UAC) to write the Campus hosts entries, that run skips every launcher init below
    // and just performs the write + flushes DNS, then exits with the hosts-write result as
    // its process exit code. See HostsFix.h for the full flow and why it exists.
    {
        int rc = hostsfix::MaybeHandleFixHostsFlag();
        if (rc >= 0) return rc;
    }

    std::string serverOverride, page, installDir, dbgPreset;   // dbgPreset: --dbg offline,nogame,... (Debug build)
    bool noFind = false;
    int shotDelay = 0;          // --shot-delay N (testing): take the --shot after N seconds instead of 6
    std::wstring shotPath;
    unsigned long long shotAt = 0;
    {
        int argc = 0;
        LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        for (int i = 1; argv && i < argc; i++)
            if (wcscmp(argv[i], L"--nofind") == 0) noFind = true;
        for (int i = 1; argv && i + 1 < argc; i++)
            if (wcscmp(argv[i], L"--server") == 0) serverOverride = Narrow(argv[++i]);
            else if (wcscmp(argv[i], L"--data") == 0) SetAppDataDir(Narrow(argv[++i]));
            else if (wcscmp(argv[i], L"--page") == 0) page = Narrow(argv[++i]);
            else if (wcscmp(argv[i], L"--dbg") == 0) dbgPreset = Narrow(argv[++i]);
            else if (wcscmp(argv[i], L"--install-dir") == 0) installDir = Narrow(argv[++i]);
            else if (wcscmp(argv[i], L"--shot") == 0) { shotPath = argv[++i]; shotAt = GetTickCount64() + 6000; }
            else if (wcscmp(argv[i], L"--shot-delay") == 0) shotDelay = _wtoi(argv[++i]);
        if (argv) LocalFree(argv);
        if (shotDelay > 0 && !shotPath.empty()) shotAt = GetTickCount64() + (unsigned long long)shotDelay * 1000;
    }

    // One launcher at a time: a second start brings the first one forward. (A --shot test run
    // doesn't count: it never touches the real launcher's window.)
    if (shotPath.empty())
    {
        #ifdef CAMPUS_DEBUG
        g_single = CreateMutexW(nullptr, TRUE, L"CampusLauncher.SingleInstance.Debug");   // runs beside the real launcher
#else
        g_single = CreateMutexW(nullptr, TRUE, L"CampusLauncher.SingleInstance");
#endif
        if (GetLastError() == ERROR_ALREADY_EXISTS)
        {
            if (HWND other = FindWindowW(L"CampusLauncher", nullptr)) { ShowWindow(other, SW_SHOW); ShowWindow(other, SW_RESTORE); SetForegroundWindow(other); }
            return 0;
        }
    }

    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);   // WIC pictures + the folder picker
    ImGui_ImplWin32_EnableDpiAwareness();
    float scale = ImGui_ImplWin32_GetDpiScaleForMonitor(MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY));

    WNDCLASSEXW wc{ sizeof(wc), CS_CLASSDC, WndProc, 0, 0, hi, LoadIconW(hi, MAKEINTRESOURCEW(1)), LoadCursor(nullptr, IDC_ARROW),
                    nullptr, nullptr, L"CampusLauncher", LoadIconW(hi, MAKEINTRESOURCEW(1)) };
    RegisterClassExW(&wc);
    // fixed size, centred on the main screen; WS_CAPTION keeps the taskbar / minimise behaviour and
    // the shadow, WM_NCCALCSIZE takes the frame itself away
    RECT r{ 0, 0, (LONG)(1100 * scale), (LONG)(680 * scale) };
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    MONITORINFO mi{ sizeof(mi) };
    GetMonitorInfoW(MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY), &mi);
    int w = r.right - r.left, h = r.bottom - r.top;
    int x = (mi.rcWork.left + mi.rcWork.right - w) / 2, y = (mi.rcWork.top + mi.rcWork.bottom - h) / 2;
    if (!shotPath.empty()) { x = GetSystemMetrics(SM_XVIRTUALSCREEN) - w - 200; y = 0; }   // a test shot stays out of sight
    #ifdef CAMPUS_DEBUG
    const wchar_t* kTitle = L"Campus (DEBUG)";
#else
    const wchar_t* kTitle = L"Campus";
#endif
    HWND hwnd = CreateWindowW(wc.lpszClassName, kTitle, style, x, y, w, h, nullptr, nullptr, hi, nullptr);
    DarkTitleBar(hwnd);
    MARGINS shadow{ 1, 1, 1, 1 };
    DwmExtendFrameIntoClientArea(hwnd, &shadow);
    int round = 2;                                   // DWMWCP_ROUND (Windows 11)
    DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, &round, sizeof(round));
    SetWindowPos(hwnd, nullptr, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
    if (!CreateDevice(hwnd))
    {
        CleanupDevice();
        MessageBoxW(nullptr, L"Couldn't start the graphics for the launcher (Direct3D 11).", L"Campus", MB_ICONERROR);
        return 1;
    }
    TexSetDevice(g_dev);

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    ui::ApplyTheme(scale);
    ui::LoadFonts(scale);
    ImGui_ImplWin32_Init(hwnd);
    ImGui_ImplDX11_Init(g_dev, g_ctx);

    App app;
    app.SetTestOptions(noFind, installDir);
    app.Init(hwnd, serverOverride);
#ifdef CAMPUS_DEBUG
    app.DebugPreset(dbgPreset);
#endif
    g_app = &app;
    if (!page.empty()) app.ShowPage(page);
    ShowWindow(hwnd, shotPath.empty() ? SW_SHOWDEFAULT : SW_SHOWNOACTIVATE);   // a test shot never takes the keyboard
    if (!shotPath.empty()) ShowWindow(hwnd, SW_SHOWNOACTIVATE);              // (the first call may obey how we were started)
    UpdateWindow(hwnd);

    bool done = false, hidden = false;
    while (!done)
    {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
            if (msg.message == WM_QUIT) done = true;
        }
        if (done) break;
        if (g_resizeW && g_resizeH)
        {
            CleanupRenderTarget();
            g_swap->ResizeBuffers(0, g_resizeW, g_resizeH, DXGI_FORMAT_UNKNOWN, 0);
            g_resizeW = g_resizeH = 0;
            CreateRenderTarget();
        }
        // "Close when the game starts": hide, keep running until the game closes (to delete the patch copy)
        if (app.WantsHidden() && !hidden) { ShowWindow(hwnd, SW_HIDE); hidden = true; }
        if (hidden || IsIconic(hwnd)) { Sleep(hidden ? 250 : 50); if (hidden) { ImGui_ImplDX11_NewFrame(); ImGui_ImplWin32_NewFrame(); ImGui::NewFrame(); app.Frame(); ImGui::EndFrame(); if (app.WantsQuit()) break; continue; } }
        else if (GetForegroundWindow() != hwnd) Sleep(30);   // in the background: fewer frames

        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();
        app.Frame();
        ImGui::Render();
        const float clear[4] = { 21 / 255.0f, 21 / 255.0f, 25 / 255.0f, 1.0f };
        g_ctx->OMSetRenderTargets(1, &g_rtv, nullptr);
        g_ctx->ClearRenderTargetView(g_rtv, clear);
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
        if (!shotPath.empty() && GetTickCount64() > shotAt) { SaveBackBuffer(shotPath); break; }
        g_swap->Present(1, 0);
        if (app.WantsQuit()) break;
    }

    g_app = nullptr;
    app.Shutdown();
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    CleanupDevice();
    DestroyWindow(hwnd);
    UnregisterClassW(wc.lpszClassName, hi);
    ReleaseSingleInstance();
    CoUninitialize();
    return 0;
}
