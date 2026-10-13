// Campus Launcher (imgui + D3D11 edition).
//
// Merged from the parent Launcher\Launcher.cpp imgui dashboard + this folder's original
// CampusFolder.ini search + inject-CampusPatch.dll flow. Same behaviour as the GDI+ splash it
// replaces (auto-finds the game folder via CampusFolder.ini across every mounted drive, launches
// RecRoom_Release.exe, waits for GameAssembly + Referee, injects CampusPatch.dll) - just wrapped
// in the branded imgui UI.
//
// Window: pure WS_POPUP (no Windows chrome). Custom title bar with minimalist min/close.
// Body: central dark card (pink border, admin-panel style) with Screen Mode / Resolution / VR
// sections, status, LAUNCH row (green gradient pill), log tail. Background: Background.png
// stretched to fill + tiled AnimatedIcon with slow horizontal parallax.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <dwmapi.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wincodec.h>

#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cmath>

#include <string>
#include <vector>
#include <deque>
#include <mutex>
#include <atomic>
#include <unordered_map>

#include "vendor/imgui/imgui.h"
#include "vendor/imgui/backends/imgui_impl_win32.h"
#include "vendor/imgui/backends/imgui_impl_dx11.h"

#include "resource.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

using std::string;
using std::vector;

// ===============================================================================================
// Resource IDs
// ===============================================================================================
#define IDR_ANIMATED_ICON_PNG 200
#define IDR_BACKGROUND_PNG    201
#define IDR_CUTOUT_PNG        202
#define IDR_RUBIK_TTF         203
#define IDR_STEAM_ICON_PNG    204
#define IDR_META_LOGO_PNG     205

// ===============================================================================================
// Config: game exe name + injected DLL relative path (both discovered under the CampusFolder.ini
// folder). Keep these names in sync with what CampusPatch.dll expects.
// ===============================================================================================
static const char* GAME_EXE_NAME = "RecRoom_Release.exe";
static const char* PATCH_DLL_REL = "CampusPatch\\Patch\\CampusPatch.dll";

// ===============================================================================================
// Runtime state
// ===============================================================================================
static string g_gameFolder;   // resolved by ini search; empty until found
static string g_gameExe;      // gameFolder + "\\" + GAME_EXE_NAME
static string g_dllPath;      // gameFolder + "\\" + PATCH_DLL_REL
static HWND   g_hwnd = nullptr;

static std::mutex              g_logMtx;
static std::deque<std::string> g_logLines;
static void log_line(const char* fmt, ...) {
    char body[512]; va_list ap; va_start(ap, fmt);
    _vsnprintf_s(body, sizeof(body), _TRUNCATE, fmt, ap); va_end(ap);
    SYSTEMTIME st; GetLocalTime(&st);
    char line[600];
    _snprintf_s(line, sizeof(line), _TRUNCATE, "%02d:%02d:%02d  %s",
                st.wHour, st.wMinute, st.wSecond, body);
    std::lock_guard<std::mutex> lk(g_logMtx);
    g_logLines.push_back(line);
    if (g_logLines.size() > 500) g_logLines.pop_front();
    OutputDebugStringA(line); OutputDebugStringA("\n");
}

// ===============================================================================================
// Filesystem
// ===============================================================================================
static string module_dir() {
    char buf[MAX_PATH] = {}; GetModuleFileNameA(nullptr, buf, MAX_PATH);
    string s(buf); auto slash = s.find_last_of("\\/");
    return (slash == string::npos) ? string() : s.substr(0, slash);
}
static bool file_exists(const string& p) {
    DWORD a = GetFileAttributesA(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}
// Recursive FindFirstFile - matches the original GDI+ launcher's behaviour, including short-
// circuit return once found. Never follows reparse points (avoids junction loops on user profiles).
static bool find_file_recursive(const string& startDir, const string& fileName, string& foundPath) {
    string searchPath = startDir + "\\*";
    WIN32_FIND_DATAA fd{};
    HANDLE hFind = FindFirstFileA(searchPath.c_str(), &fd);
    if (hFind == INVALID_HANDLE_VALUE) return false;
    do {
        if (strcmp(fd.cFileName, ".") == 0 || strcmp(fd.cFileName, "..") == 0) continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) continue;
        string full = startDir + "\\" + fd.cFileName;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (find_file_recursive(full, fileName, foundPath)) { FindClose(hFind); return true; }
        } else if (_stricmp(fd.cFileName, fileName.c_str()) == 0) {
            foundPath = full; FindClose(hFind); return true;
        }
    } while (FindNextFileA(hFind, &fd));
    FindClose(hFind);
    return false;
}
// Ported from the original Campus\Launcher.cpp - searches the launcher's own dir, its parent,
// then every mounted drive letter for CampusFolder.ini. The FOLDER containing that ini must
// also contain RecRoom_Release.exe for us to accept it.
static string find_game_folder_from_config() {
    vector<string> roots;
    string here = module_dir();
    if (!here.empty()) roots.push_back(here);
    auto slash = here.find_last_of("\\/");
    if (slash != string::npos) roots.push_back(here.substr(0, slash));
    DWORD drives = GetLogicalDrives();
    for (int i = 0; i < 26; i++) if (drives & (1u << i)) {
        char d[4] = { char('A' + i), ':', '\\', 0 };
        roots.push_back(d);
    }
    for (const auto& r : roots) {
        string found;
        if (find_file_recursive(r, "CampusFolder.ini", found)) {
            auto s2 = found.find_last_of("\\/");
            if (s2 == string::npos) continue;
            string folder = found.substr(0, s2);
            if (file_exists(folder + "\\" + GAME_EXE_NAME)) return folder;
        }
    }
    return "";
}

// ===============================================================================================
// WIC PNG loader + D3D texture
// ===============================================================================================
struct GpuTexture {
    ID3D11Texture2D*          tex = nullptr;
    ID3D11ShaderResourceView* srv = nullptr;
    int w = 0, h = 0;
    void release() { if (srv) srv->Release(); if (tex) tex->Release(); srv=nullptr; tex=nullptr; }
};
static bool wic_decode_png(const uint8_t* data, size_t size,
                           vector<uint8_t>& outRGBA, int& outW, int& outH)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IWICImagingFactory* wic = nullptr;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&wic)))) return false;
    IWICStream* stream = nullptr; wic->CreateStream(&stream);
    if (FAILED(stream->InitializeFromMemory(const_cast<BYTE*>(data), (DWORD)size))) {
        stream->Release(); wic->Release(); return false;
    }
    IWICBitmapDecoder* dec = nullptr;
    if (FAILED(wic->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnLoad, &dec))) {
        stream->Release(); wic->Release(); return false;
    }
    IWICBitmapFrameDecode* frame = nullptr; dec->GetFrame(0, &frame);
    UINT w = 0, hgt = 0; frame->GetSize(&w, &hgt);
    IWICFormatConverter* conv = nullptr; wic->CreateFormatConverter(&conv);
    conv->Initialize(frame, GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone,
                     nullptr, 0.0, WICBitmapPaletteTypeCustom);
    outW = (int)w; outH = (int)hgt;
    outRGBA.assign((size_t)w * hgt * 4, 0);
    conv->CopyPixels(nullptr, w * 4, (UINT)outRGBA.size(), outRGBA.data());
    conv->Release(); frame->Release(); dec->Release(); stream->Release(); wic->Release();
    return true;
}
// Creates a texture with a full mipmap chain and auto-generates the mips. Without this the
// Cutout logo shows aliasing when drawn at ~340px because a 1536-wide source without mips is
// linearly downsampled (still shows single-pixel jaggies). MipLevels=0 + GENERATE_MIPS +
// GenerateMips call gives us clean trilinear downscale.
static bool make_d3d_texture_from_rgba(ID3D11Device* dev, ID3D11DeviceContext* ctx,
                                       const uint8_t* rgba, int w, int h,
                                       GpuTexture& out) {
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = w; td.Height = h;
    td.MipLevels = 0;                                       // 0 = full mip chain
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;   // RT needed by GenerateMips
    td.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
    // Cannot pass initial data alongside MipLevels=0; upload level 0 separately below.
    if (FAILED(dev->CreateTexture2D(&td, nullptr, &out.tex))) return false;
    ctx->UpdateSubresource(out.tex, 0, nullptr, rgba, w * 4, 0);
    D3D11_SHADER_RESOURCE_VIEW_DESC vd = {};
    vd.Format = td.Format;
    vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    vd.Texture2D.MipLevels = (UINT)-1;                      // all mips
    if (FAILED(dev->CreateShaderResourceView(out.tex, &vd, &out.srv))) return false;
    ctx->GenerateMips(out.srv);
    out.w = w; out.h = h; return true;
}
static bool find_rcdata(int rcid, const void*& out, size_t& outSz) {
    HRSRC h = FindResourceA(nullptr, MAKEINTRESOURCEA(rcid), (LPCSTR)RT_RCDATA);
    if (!h) return false;
    HGLOBAL g = LoadResource(nullptr, h);
    if (!g) return false;
    out = LockResource(g); outSz = SizeofResource(nullptr, h);
    return out && outSz;
}
static bool load_embedded_png_texture(ID3D11Device* dev, ID3D11DeviceContext* ctx,
                                       int rcid, GpuTexture& out) {
    const void* p; size_t n;
    if (!find_rcdata(rcid, p, n)) return false;
    vector<uint8_t> rgba; int w=0, hgt=0;
    if (!wic_decode_png((const uint8_t*)p, n, rgba, w, hgt)) return false;
    return make_d3d_texture_from_rgba(dev, ctx, rgba.data(), w, hgt, out);
}

// Silhouette variant: after decoding the PNG, walks the pixels and forces RGB=255,255,255 for
// every pixel while preserving the alpha channel. Result: a pure white cut-out of the icon
// with soft edges. When drawn via ImGui::AddImage with a colour tint, the tint multiplies the
// white and produces a clean flat-colour silhouette (which is what we need for the Steam /
// Meta logos on the cards - they need to render in a solid purple/orange, not muddied by
// the source PNG's blues).
static bool load_embedded_png_silhouette(ID3D11Device* dev, ID3D11DeviceContext* ctx,
                                          int rcid, GpuTexture& out) {
    const void* p; size_t n;
    if (!find_rcdata(rcid, p, n)) return false;
    vector<uint8_t> rgba; int w=0, hgt=0;
    if (!wic_decode_png((const uint8_t*)p, n, rgba, w, hgt)) return false;
    // Force RGB to white; keep alpha. Small caveat: pre-multiplied-alpha sources would be
    // slightly off, but WIC hands us straight (non-premul) RGBA, so this is correct.
    for (size_t i = 0; i + 3 < rgba.size(); i += 4) {
        rgba[i + 0] = 255;
        rgba[i + 1] = 255;
        rgba[i + 2] = 255;
        // alpha (rgba[i+3]) preserved
    }
    return make_d3d_texture_from_rgba(dev, ctx, rgba.data(), w, hgt, out);
}

// ===============================================================================================
// D3D11
// ===============================================================================================
static ID3D11Device*           g_dev = nullptr;
static ID3D11DeviceContext*    g_ctx = nullptr;
static IDXGISwapChain*         g_sc  = nullptr;
static ID3D11RenderTargetView* g_rtv = nullptr;
static bool create_device(HWND hwnd) {
    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 2; sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hwnd; sd.SampleDesc.Count = 1; sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    D3D_FEATURE_LEVEL got;
    if (FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        fl, 2, D3D11_SDK_VERSION, &sd, &g_sc, &g_dev, &got, &g_ctx))) return false;
    ID3D11Texture2D* back = nullptr; g_sc->GetBuffer(0, IID_PPV_ARGS(&back));
    g_dev->CreateRenderTargetView(back, nullptr, &g_rtv); back->Release();
    return true;
}
static void destroy_device() {
    if (g_rtv) g_rtv->Release(); if (g_sc) g_sc->Release();
    if (g_ctx) g_ctx->Release(); if (g_dev) g_dev->Release();
    g_rtv=nullptr; g_sc=nullptr; g_ctx=nullptr; g_dev=nullptr;
}

// ===============================================================================================
// UI state
// ===============================================================================================
struct UiState {
    int  screenMode = 0, resIdx = 0, vrMode = 0;
    bool showSettings = false;
    std::string status = "Searching for Campus...";
    bool statusOk = true;
    bool injecting = false;
    bool injectionComplete = false;
    bool injectionSuccess  = false;
    GpuTexture faceTex, bgTex, logoTex, steamTex, metaTex;
    ImFont* fontBody  = nullptr;
    ImFont* fontLabel = nullptr;
    ImFont* fontH     = nullptr;
    ImFont* fontTiny  = nullptr;
};
static UiState g_ui;

static const char* SCREEN_MODES[] = { "Windowed", "Fullscreen", "Borderless" };
static const char* VR_MODES[]     = { "Desktop (None)", "SteamVR", "Oculus", "WMR" };
static const char* RESOLUTIONS[]  = { "1920x1080", "2560x1440", "1600x900", "1280x720", "3840x2160" };

static constexpr ImU32 COL_TEXT        = IM_COL32(0xF0, 0xF0, 0xF0, 255);
static constexpr ImU32 COL_TEXT_DIM    = IM_COL32(0xC8, 0xC0, 0xC8, 200);
static constexpr ImU32 COL_TEXT_LABEL  = IM_COL32(0xE8, 0xC0, 0xD0, 220);
static constexpr ImU32 COL_GLASS       = IM_COL32(255, 255, 255, 32);
static constexpr ImU32 COL_GLASS_HI    = IM_COL32(255, 255, 255, 58);
static constexpr ImU32 COL_GLASS_BORDER= IM_COL32(255, 255, 255, 90);

// ===============================================================================================
// Hover fade helper (micro-interactions)
// ===============================================================================================
static std::unordered_map<std::string, float> g_hoverFade;
static float hover_step(const std::string& key, bool hovered, float dt) {
    float& v = g_hoverFade[key];
    float target = hovered ? 1.0f : 0.0f;
    v += (target - v) * (1.0f - expf(-8.0f * dt));
    if (v < 0.001f) v = 0.0f; if (v > 0.999f) v = 1.0f;
    return v;
}
static ImU32 lerp_col(ImU32 a, ImU32 b, float t) {
    int ar=a&0xff, ag=(a>>8)&0xff, ab=(a>>16)&0xff, aa=(a>>24)&0xff;
    int br=b&0xff, bg=(b>>8)&0xff, bb=(b>>16)&0xff, ba=(b>>24)&0xff;
    return IM_COL32(ar+(int)((br-ar)*t), ag+(int)((bg-ag)*t),
                    ab+(int)((bb-ab)*t), aa+(int)((ba-aa)*t));
}

// ===============================================================================================
// Injection thread - ported from the GDI+ launcher. Blocks on: find folder, spawn game, wait
// for GameAssembly + Referee, VirtualAllocEx/WriteProcessMemory/CreateRemoteThread(LoadLibrary).
// ===============================================================================================
// Checks whether the target process has all of the given module names loaded RIGHT NOW.
// Case-insensitive match on the leaf module name. Returns false if we can't enumerate
// (e.g. process just spawned, snapshot not ready yet) - caller should just retry.
static bool has_modules(DWORD pid, const char* const* names, int count) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snap == INVALID_HANDLE_VALUE) return false;
    std::vector<bool> got(count, false);
    MODULEENTRY32 me{}; me.dwSize = sizeof(me);
    if (Module32First(snap, &me)) do {
        for (int i = 0; i < count; i++)
            if (_stricmp(me.szModule, names[i]) == 0) got[i] = true;
    } while (Module32Next(snap, &me));
    CloseHandle(snap);
    for (int i = 0; i < count; i++) if (!got[i]) return false;
    return true;
}

// Stage 1: engine + anti-tamper are up. Fires early (usually < 5 s after spawn).
static bool has_engine_modules(DWORD pid) {
    static const char* need[] = { "GameAssembly.dll", "Referee.dll" };
    return has_modules(pid, need, 2);
}

// Stage 2: Rec Room's Steam integration is active. steam_api64.dll only loads when the
// game code hits its first Steamworks call, which in Rec Room's boot sequence happens
// during the "logging in" phase after the splash screen. Injecting AT this point means
// Referee's initial integrity pass has already run against un-patched code (so it won't
// alarm), UnityPlayer is fully initialised, and the game is about to make its first
// auth request - exactly when our SendRequest URI-rewrite hook needs to be in place.
static bool has_steam_ready(DWORD pid) {
    static const char* need[] = { "steam_api64.dll" };
    return has_modules(pid, need, 1);
}

// Legacy wrapper kept so old call sites don't need to change.
static bool has_all_modules(DWORD pid) { return has_engine_modules(pid); }
static void set_status(const char* s, bool ok = true) {
    g_ui.status = s; g_ui.statusOk = ok;
    log_line("%s", s);
}
static DWORD WINAPI inject_thread(LPVOID) {
    g_ui.injecting = true;
    set_status("Searching for Campus...");
    Sleep(300);

    g_gameFolder = find_game_folder_from_config();
    if (g_gameFolder.empty()) {
        set_status("ERROR: CampusFolder.ini not found on any drive", false);
        g_ui.injecting = false; g_ui.injectionComplete = true; g_ui.injectionSuccess = false;
        return 1;
    }
    g_gameExe = g_gameFolder + "\\" + GAME_EXE_NAME;
    g_dllPath = g_gameFolder + "\\" + PATCH_DLL_REL;
    log_line("Found game folder: %s", g_gameFolder.c_str());
    Sleep(200);

    if (!file_exists(g_gameExe)) { set_status("ERROR: RecRoom_Release.exe missing in game folder", false); g_ui.injecting = false; g_ui.injectionComplete = true; g_ui.injectionSuccess = false; return 1; }
    if (!file_exists(g_dllPath)) { set_status("ERROR: CampusPatch.dll missing", false); g_ui.injecting = false; g_ui.injectionComplete = true; g_ui.injectionSuccess = false; return 1; }

    set_status("Starting game...");
    Sleep(200);

    STARTUPINFOA si{}; si.cb = sizeof(si); PROCESS_INFORMATION pi{};
    // Force Unity to skip all XR / VR initialisation. On this install SteamVR reports
    // VRInitError_Init_InstallationCorrupt on every launch, and Rec Room's fallback
    // path from a failed VR init is unstable enough to have been implicated in the
    // vanilla-client crash. `-nohmd` = "no HMD, run flat"; `-window-mode exclusive`
    // tells Unity to skip windowed-mode probing. Both are stock Unity command-line
    // flags, no Rec Room integration required.
    //
    // CreateProcessA requires lpCommandLine to be a writable buffer and, when
    // lpApplicationName is set, to still start with the exe (becomes argv[0]).
    // Quote the exe path in case it lives under a folder with spaces.
    std::string cmdline = "\"" + g_gameExe + "\" -nohmd -window-mode exclusive";
    std::vector<char> cmdBuf(cmdline.begin(), cmdline.end());
    cmdBuf.push_back('\0');

    // Build a scrubbed environment block for the child. Windows Event Log has repeatedly
    // flagged `tier0_s64.dll` (Steam Overlay's proxy runtime) as the crashing module on
    // this build. Steam Overlay uses env vars like SteamAppId / SteamGameId /
    // SteamOverlayGameId to decide whether to inject into a child process. Wiping them
    // from OUR child means Steam sees the process as "not a Steam game" and skips the
    // overlay hook - the game still finds steam_api64.dll and does its own SteamAPI_Init,
    // so account auth is unaffected. Fallback: if BuildScrubbedEnv() ever fails we let
    // the child inherit our env, which is the historical behaviour.
    auto BuildScrubbedEnv = []() -> std::vector<char> {
        std::vector<char> block;
        LPCH env = GetEnvironmentStringsA();
        if (!env) return block;
        static const char* strip[] = {
            "SteamAppId=", "SteamGameId=", "SteamOverlayGameId=",
            "SteamOverlayNativeAppId=", "SteamClientLaunch=", "SteamNoOverlayUIDrawing=",
            "SteamEnv=", "SteamPath=", "SteamUniverse=", "SteamController="
        };
        LPCH cur = env;
        while (*cur) {
            size_t len = strlen(cur);
            bool drop = false;
            for (const char* pfx : strip) {
                size_t pl = strlen(pfx);
                if (len >= pl && _strnicmp(cur, pfx, (int)pl) == 0) { drop = true; break; }
            }
            if (!drop) {
                block.insert(block.end(), cur, cur + len);
                block.push_back('\0');
            }
            cur += len + 1;
        }
        block.push_back('\0');   // double-null terminator per Win32 env block spec
        FreeEnvironmentStringsA(env);
        return block;
    };
    std::vector<char> childEnv = BuildScrubbedEnv();
    LPVOID envPtr = childEnv.empty() ? nullptr : childEnv.data();

    if (!CreateProcessA(g_gameExe.c_str(), cmdBuf.data(), nullptr, nullptr, FALSE, 0,
                        envPtr, g_gameFolder.c_str(), &si, &pi)) {
        set_status("ERROR: CreateProcess failed", false);
        g_ui.injecting = false; g_ui.injectionComplete = true; g_ui.injectionSuccess = false;
        return 2;
    }
    log_line("Child env scrubbed of Steam vars (blocks Overlay attach path)");
    log_line("Spawned PID=%lu with -nohmd, waiting for GameAssembly + Referee...", pi.dwProcessId);

    // Poll for the game's engine modules to appear. GameAssembly.dll + Referee.dll are the
    // last modules to load during Unity's early bootstrap - once they're both present the
    // process is stable enough to accept a CreateRemoteThread inject. Timeout at 30 s so
    // a crashing / non-loading game surfaces as a clear error instead of hanging forever.
    int waited = 0;
    while (waited < 30000) {
        DWORD exitCode = 0;
        if (GetExitCodeProcess(pi.hProcess, &exitCode) && exitCode != STILL_ACTIVE) {
            set_status("ERROR: Game crashed before modules loaded", false);
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            g_ui.injecting = false; g_ui.injectionComplete = true; g_ui.injectionSuccess = false;
            return 3;
        }
        if (has_engine_modules(pi.dwProcessId)) break;
        Sleep(100); waited += 100;
        if (waited % 1000 == 0) {
            char b[96]; _snprintf_s(b, sizeof(b), _TRUNCATE, "Waiting for engine... (%d/30000 ms)", waited);
            set_status(b);
        }
    }
    if (waited >= 30000) {
        set_status("ERROR: Engine module load timeout", false);
        TerminateProcess(pi.hProcess, 1);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        g_ui.injecting = false; g_ui.injectionComplete = true; g_ui.injectionSuccess = false;
        return 4;
    }
    log_line("Modules loaded after %d ms - injecting CampusPatch.dll immediately", waited);
    // Formerly waited for steam_api64.dll (stage 2) here - reverted per user request to
    // inject as early as possible so our SendRequest URL rewrite hook is live BEFORE the
    // client's first HTTP call goes out.
    set_status("Loading Patch...");

    HMODULE k32 = GetModuleHandleA("kernel32.dll");
    FARPROC pLoad = GetProcAddress(k32, "LoadLibraryA");
    LPVOID remote = VirtualAllocEx(pi.hProcess, nullptr, g_dllPath.size() + 1,
                                   MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) { set_status("ERROR: VirtualAllocEx failed", false); g_ui.injecting = false; g_ui.injectionComplete = true; g_ui.injectionSuccess = false; return 5; }
    WriteProcessMemory(pi.hProcess, remote, g_dllPath.c_str(), g_dllPath.size() + 1, nullptr);
    HANDLE ht = CreateRemoteThread(pi.hProcess, nullptr, 0,
        (LPTHREAD_START_ROUTINE)pLoad, remote, 0, nullptr);
    if (!ht) { set_status("ERROR: CreateRemoteThread failed", false); g_ui.injecting = false; g_ui.injectionComplete = true; g_ui.injectionSuccess = false; return 6; }
    WaitForSingleObject(ht, 15000);
    DWORD injectExit = 0; GetExitCodeThread(ht, &injectExit);
    CloseHandle(ht); VirtualFreeEx(pi.hProcess, remote, 0, MEM_RELEASE);

    if (injectExit == 0) {
        set_status("Failed to load - check logs", false);
        g_ui.injectionSuccess = false;
    } else {
        set_status("Enjoy, made by Kyle");
        g_ui.injectionSuccess = true;
    }
    g_ui.injecting = false; g_ui.injectionComplete = true;
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return 0;
}

// ===============================================================================================
// Background - Background.png full-window + tiled AnimatedIcon with slow parallax
// ===============================================================================================
static void draw_background(ImDrawList* dl, ImVec2 sz, float t) {
    if (g_ui.bgTex.srv) {
        dl->AddImage((ImTextureID)g_ui.bgTex.srv, ImVec2(0,0), sz,
                     ImVec2(0,0), ImVec2(1,1), IM_COL32(255,255,255,255));
    } else {
        dl->AddRectFilledMultiColor(ImVec2(0,0), sz,
            IM_COL32(0x79,0x1B,0x8D,255), IM_COL32(0xC7,0x2A,0x5C,255),
            IM_COL32(0xFA,0x99,0x12,255), IM_COL32(0xBF,0x29,0x66,255));
    }
    if (g_ui.faceTex.srv) {
        const float scale = 0.28f;
        float iw = g_ui.faceTex.w * scale, ih = g_ui.faceTex.h * scale;
        const float step = 128.0f;                       // wider spacing = calmer
        float offX = fmodf(t * (step / 40.0f), step);    // slower drift too
        ImU32 tint = IM_COL32(255, 255, 255, 35);        // was 60 - much softer now
        for (float y = -ih; y < sz.y + ih; y += step) {
            float xoff = ((int)(y / step) % 2) ? step * 0.5f : 0.0f;
            for (float x = -iw + xoff - step + offX; x < sz.x + iw; x += step) {
                dl->AddImage((ImTextureID)g_ui.faceTex.srv,
                             ImVec2(x, y), ImVec2(x + iw, y + ih),
                             ImVec2(0,0), ImVec2(1,1), tint);
            }
        }
    }
}

// ===============================================================================================
// Custom title bar - dark alpha gradient + minimalist glyphs
// ===============================================================================================
static const float TITLEBAR_H = 40.0f;
static void draw_titlebar(ImDrawList* dl, ImVec2 sz) {
    dl->AddRectFilledMultiColor(ImVec2(0,0), ImVec2(sz.x, TITLEBAR_H),
        IM_COL32(0, 0, 0, 130), IM_COL32(0, 0, 0, 130),
        IM_COL32(0, 0, 0, 0),   IM_COL32(0, 0, 0, 0));
    const float bw = 46.0f, bh = TITLEBAR_H;
    float dt = ImGui::GetIO().DeltaTime;
    ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 0.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_FrameBorderSize, 0.0f);

    ImGui::SetCursorScreenPos(ImVec2(sz.x - 2 * bw, 0));
    ImGui::PushStyleColor(ImGuiCol_Button,        IM_COL32(0,0,0,0));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, IM_COL32(255,255,255,30));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive,  IM_COL32(255,255,255,60));
    if (ImGui::Button("##min", ImVec2(bw, bh))) ShowWindow(g_hwnd, SW_MINIMIZE);
    bool minH = ImGui::IsItemHovered();
    ImGui::PopStyleColor(3);
    {
        float a = 180 + (int)(75 * hover_step("min_glyph", minH, dt));
        ImVec2 c(sz.x - 2 * bw + bw * 0.5f, bh * 0.5f + 6.0f);
        dl->AddLine(ImVec2(c.x - 6, c.y), ImVec2(c.x + 6, c.y),
                    IM_COL32(240, 240, 240, (int)a), 1.4f);
    }
    ImGui::SetCursorScreenPos(ImVec2(sz.x - bw, 0));
    ImGui::PushStyleColor(ImGuiCol_Button,        IM_COL32(0,0,0,0));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, IM_COL32(220, 60, 60, 220));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive,  IM_COL32(240, 80, 80, 240));
    if (ImGui::Button("##close", ImVec2(bw, bh))) PostMessage(g_hwnd, WM_CLOSE, 0, 0);
    bool clsH = ImGui::IsItemHovered();
    ImGui::PopStyleColor(3);
    {
        float a = 180 + (int)(75 * hover_step("close_glyph", clsH, dt));
        ImVec2 c(sz.x - bw * 0.5f, bh * 0.5f);
        dl->AddLine(ImVec2(c.x - 5.5f, c.y - 5.5f), ImVec2(c.x + 5.5f, c.y + 5.5f),
                    IM_COL32(240, 240, 240, (int)a), 1.4f);
        dl->AddLine(ImVec2(c.x - 5.5f, c.y + 5.5f), ImVec2(c.x + 5.5f, c.y - 5.5f),
                    IM_COL32(240, 240, 240, (int)a), 1.4f);
    }
    ImGui::PopStyleVar(2);

    ImVec2 mp = ImGui::GetIO().MousePos;
    bool inDrag = mp.y >= 0 && mp.y < TITLEBAR_H && mp.x >= 0 && mp.x < sz.x - 2 * bw;
    if (inDrag && ImGui::IsMouseClicked(0) && !ImGui::IsAnyItemHovered()) {
        ReleaseCapture();
        SendMessage(g_hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }
}

// ===============================================================================================
// Logo - Cutout.png centered with soft glow + drop shadow
// ===============================================================================================
static const float LOGO_TARGET_W = 260.0f;   // was 340 - shrunk so cards get more vertical room
static float logo_actual_h() {
    if (!g_ui.logoTex.srv || g_ui.logoTex.w == 0) return 100.0f;
    return LOGO_TARGET_W * ((float)g_ui.logoTex.h / (float)g_ui.logoTex.w);
}
static void draw_logo(ImDrawList* dl, ImVec2 sz) {
    const float topY = TITLEBAR_H + 16.0f;
    float lw = LOGO_TARGET_W, lh = logo_actual_h();
    float lx = floorf((sz.x - lw) * 0.5f);
    float ly = floorf(topY);
    if (!g_ui.logoTex.srv) return;
    ImTextureID tex = (ImTextureID)g_ui.logoTex.srv;
    // Two-pass soft shadow (wider fainter + tighter darker) for a real "lifted" look.
    dl->AddImage(tex,
        ImVec2(lx - 2, ly + 10), ImVec2(lx + lw + 2, ly + lh + 10),
        ImVec2(0,0), ImVec2(1,1), IM_COL32(0, 0, 0, 45));
    dl->AddImage(tex,
        ImVec2(lx + 1, ly + 5), ImVec2(lx + lw + 1, ly + lh + 5),
        ImVec2(0,0), ImVec2(1,1), IM_COL32(0, 0, 0, 90));
    // Main logo with mipmapped downscale = clean edges.
    dl->AddImage(tex,
        ImVec2(lx, ly), ImVec2(lx + lw, ly + lh),
        ImVec2(0,0), ImVec2(1,1), IM_COL32(255, 255, 255, 255));
    // (Previous "MADE BY KYLE" subtitle removed - byline now lives in the footer inside the
    // panel as "Campus · Made by Kyle", so this floating copy was redundant.)
}
static float logo_bottom_y() {
    return TITLEBAR_H + 16.0f + logo_actual_h() + 18.0f;
}

// ===============================================================================================
// Glass controls
// ===============================================================================================
static void draw_chevron(ImDrawList* dl, ImVec2 c, ImU32 col) {
    dl->AddLine(ImVec2(c.x-4, c.y-2), ImVec2(c.x,   c.y+3), col, 1.6f);
    dl->AddLine(ImVec2(c.x+4, c.y-2), ImVec2(c.x,   c.y+3), col, 1.6f);
}
// Dropdown that blends into the unified dashboard: same dark fill as the container, white text,
// white chevron, rounded to match the container's outer corners, no clashing blue border.
static void glass_combo(const char* label, int* v, const char* const items[], int n) {
    // Frame (closed state) - matches the dashboard's darker inner cells.
    ImGui::PushStyleColor(ImGuiCol_FrameBg,        IM_COL32(28, 20, 38, 220));
    ImGui::PushStyleColor(ImGuiCol_FrameBgHovered, IM_COL32(42, 30, 55, 235));
    ImGui::PushStyleColor(ImGuiCol_FrameBgActive,  IM_COL32(50, 36, 65, 240));
    ImGui::PushStyleColor(ImGuiCol_Border,         IM_COL32(255, 255, 255, 40));
    // Popup (open state) - also dark so the whole widget reads as one system.
    ImGui::PushStyleColor(ImGuiCol_PopupBg,        IM_COL32(22, 15, 32, 240));
    ImGui::PushStyleColor(ImGuiCol_Text,           IM_COL32(240, 240, 240, 255));
    ImGui::PushStyleColor(ImGuiCol_HeaderHovered,  IM_COL32(255, 150, 205, 60));
    ImGui::PushStyleColor(ImGuiCol_Header,         IM_COL32(255, 150, 205, 90));
    ImGui::PushStyleColor(ImGuiCol_HeaderActive,   IM_COL32(255, 150, 205, 130));
    ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 10.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_FrameBorderSize, 1.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_PopupRounding, 10.0f);
    ImGui::Combo(label, v, items, n);
    // Overpaint the default arrow area with our own dark chevron so nothing clashes.
    ImVec2 mx = ImGui::GetItemRectMax(); ImVec2 mn = ImGui::GetItemRectMin();
    float arrowW = 22.0f;
    ImDrawList* dl = ImGui::GetWindowDrawList();
    dl->AddRectFilled(ImVec2(mx.x - arrowW - 1, mn.y + 1), ImVec2(mx.x - 1, mx.y - 1),
                      IM_COL32(28, 20, 38, 255), 8.0f);
    draw_chevron(dl, ImVec2(mx.x - arrowW*0.5f - 1, (mn.y + mx.y)*0.5f),
                 IM_COL32(240, 240, 240, 220));
    ImGui::PopStyleVar(3); ImGui::PopStyleColor(9);
}
static void glass_radio(const char* label, int* v, int val) {
    ImGui::PushStyleColor(ImGuiCol_FrameBg,        COL_GLASS);
    ImGui::PushStyleColor(ImGuiCol_FrameBgHovered, COL_GLASS_HI);
    ImGui::PushStyleColor(ImGuiCol_FrameBgActive,  COL_GLASS_HI);
    ImGui::PushStyleColor(ImGuiCol_CheckMark,      IM_COL32(255, 210, 120, 255));
    ImGui::PushStyleColor(ImGuiCol_Border,         COL_GLASS_BORDER);
    ImGui::PushStyleColor(ImGuiCol_Text,           COL_TEXT);
    ImGui::RadioButton(label, v, val);
    ImGui::PopStyleColor(6);
}

// ===============================================================================================
// LAUNCH button - hand-drawn green gradient pill with pulse + hover fade + top highlight
// ===============================================================================================
static bool draw_launch_button(ImVec2 pos, ImVec2 size, float t, bool ready) {
    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 br(pos.x + size.x, pos.y + size.y);
    ImGui::SetCursorScreenPos(pos);
    ImGui::InvisibleButton("##launch", size);
    bool hovered = ImGui::IsItemHovered();
    bool active  = ImGui::IsItemActive();
    bool clicked = ImGui::IsItemClicked();
    float hv = hover_step("launch_hover", hovered, ImGui::GetIO().DeltaTime);

    float pulse = 0.5f + 0.5f * sinf(t * 2.2f);
    if (!ready || hovered) pulse = 0.0f;
    if (pulse > 0.02f) {
        int a = 30 + (int)(50 * pulse);
        for (int i = 4; i >= 1; i--) {
            dl->AddRect(ImVec2(pos.x - i, pos.y - i), ImVec2(br.x + i, br.y + i),
                        IM_COL32(120, 240, 130, a / i), 14.0f + i, 0, 1.0f);
        }
    }
    ImU32 topBase  = IM_COL32( 96, 220, 108, 255);   // brighter lime top
    ImU32 midBase  = IM_COL32( 62, 175,  78, 255);   // mid stop for smoother gradient
    ImU32 botBase  = IM_COL32( 24, 110,  45, 255);   // deep forest bottom
    ImU32 topHover = IM_COL32(120, 240, 130, 255);
    ImU32 midHover = IM_COL32( 85, 200, 100, 255);
    ImU32 botHover = IM_COL32( 40, 140,  60, 255);
    ImU32 topPress = IM_COL32( 75, 185,  85, 255);
    ImU32 midPress = IM_COL32( 45, 140,  60, 255);
    ImU32 botPress = IM_COL32( 20,  90,  35, 255);
    ImU32 topC = active ? topPress : lerp_col(topBase, topHover, hv);
    ImU32 midC = active ? midPress : lerp_col(midBase, midHover, hv);
    ImU32 botC = active ? botPress : lerp_col(botBase, botHover, hv);
    // Two-stop vertical gradient for smoother depth (top->mid, mid->bot).
    float midY = pos.y + size.y * 0.55f;
    dl->AddRectFilledMultiColor(pos, ImVec2(br.x, midY), topC, topC, midC, midC);
    dl->AddRectFilledMultiColor(ImVec2(pos.x, midY), br, midC, midC, botC, botC);
    // Rounded 1.5px border - drawn on top clips corner "bleed" from the multi-color fill.
    dl->AddRect(pos, br, IM_COL32(255, 255, 255, 100), 12.0f, 0, 1.5f);
    // Bright inner top edge (specular highlight) - shortened to top ~30% for realism.
    dl->AddRectFilledMultiColor(
        ImVec2(pos.x + 2, pos.y + 1), ImVec2(br.x - 2, pos.y + size.y * 0.32f),
        IM_COL32(255,255,255,110), IM_COL32(255,255,255,110),
        IM_COL32(255,255,255,0),   IM_COL32(255,255,255,0));
    // Inner bottom shadow - the button sits INTO its own base slightly.
    dl->AddRectFilledMultiColor(
        ImVec2(pos.x + 2, br.y - size.y * 0.25f), ImVec2(br.x - 2, br.y - 1),
        IM_COL32(0,0,0,0),  IM_COL32(0,0,0,0),
        IM_COL32(0,0,0,90), IM_COL32(0,0,0,90));

    ImGui::PushFont(g_ui.fontLabel);
    const char* label = g_ui.injecting ? "LAUNCHING..." : "LAUNCH";
    ImVec2 ts = ImGui::CalcTextSize(label);
    dl->AddText(g_ui.fontLabel, g_ui.fontLabel->FontSize,
                ImVec2(pos.x + (size.x - ts.x)*0.5f + 1, pos.y + (size.y - ts.y)*0.5f + 2),
                IM_COL32(0, 40, 20, 130), label);
    dl->AddText(g_ui.fontLabel, g_ui.fontLabel->FontSize,
                ImVec2(pos.x + (size.x - ts.x)*0.5f, pos.y + (size.y - ts.y)*0.5f),
                IM_COL32(255, 255, 255, 255), label);
    ImGui::PopFont();
    return clicked && !g_ui.injecting;
}

// ===============================================================================================
// Small glass pill button - used for Exit / Uninstall / Repair in the Meow.Net-style header
// and footer rows. Rose-translucent, hover-fades to brighter, subtle white border.
// ===============================================================================================
// Glass pill button. Per spec:
//   default background: rgba(255,255,255,0.05) alpha 13
//   default border:     1px solid rgba(255,255,255,0.3) alpha 76
// tintFill != 0 overrides the fill (used by Uninstall for its red warning tint), same for
// tintBorder. Hover fades the fill up ~10 alpha for a subtle micro-interaction.
static bool draw_pill_button(const char* label, ImVec2 pos, ImVec2 size,
                             ImU32 tintFill, ImU32 tintBorder) {
    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 br(pos.x + size.x, pos.y + size.y);
    ImGui::SetCursorScreenPos(pos);
    ImGui::InvisibleButton(label, size);
    bool hovered = ImGui::IsItemHovered();
    bool clicked = ImGui::IsItemClicked();
    float hv = hover_step(std::string("pill_") + label, hovered, ImGui::GetIO().DeltaTime);
    ImU32 fill;
    if (tintFill) {
        // Use provided fill but push its alpha up on hover for feedback.
        int ta = (tintFill >> 24) & 0xff;
        int ba = ta + (int)(30 * hv);
        if (ba > 255) ba = 255;
        fill = (tintFill & 0x00FFFFFF) | ((ba & 0xff) << 24);
    } else {
        // Spec default: 5% -> 15% on hover
        int fillA = 13 + (int)(25 * hv);
        fill = IM_COL32(255, 255, 255, fillA);
    }
    dl->AddRectFilled(pos, br, fill, 10.0f);
    // Secondary-button border thinner per fix #5 - default alpha lowered 76 -> 50 so these
    // pills visually recede next to the LAUNCH buttons' brighter 60% white border.
    dl->AddRect     (pos, br, tintBorder ? tintBorder : IM_COL32(255, 255, 255, 50),
                     10.0f, 0, 1.0f);
    ImVec2 ts = ImGui::CalcTextSize(label);
    dl->AddText(g_ui.fontBody, g_ui.fontBody->FontSize,
                ImVec2(pos.x + (size.x - ts.x)*0.5f + 1, pos.y + (size.y - ts.y)*0.5f + 1),
                IM_COL32(0, 0, 0, 110), label);
    dl->AddText(g_ui.fontBody, g_ui.fontBody->FontSize,
                ImVec2(pos.x + (size.x - ts.x)*0.5f, pos.y + (size.y - ts.y)*0.5f),
                IM_COL32(255, 255, 255, 245), label);
    return clicked;
}

// ===============================================================================================
// UI
// ===============================================================================================
static void draw_ui() {
    ImGuiIO& io = ImGui::GetIO();
    ImVec2 sz = io.DisplaySize;
    float  t  = (float)ImGui::GetTime();

    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(sz);
    ImGui::PushStyleColor(ImGuiCol_WindowBg, IM_COL32(0,0,0,0));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0,0));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 0.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0.0f);
    ImGui::Begin("root", nullptr,
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
        ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoBringToFrontOnFocus);

    draw_background(ImGui::GetBackgroundDrawList(), sz, t);
    ImDrawList* fg = ImGui::GetWindowDrawList();
    // Big CAMPUS Cutout logo at the top - restored per operator request. Sits between the
    // title bar and the outer Meow.Net-style panel.
    draw_logo(fg, sz);
    draw_titlebar(fg, sz);

    ImGui::PushFont(g_ui.fontBody);
    ImGui::PushStyleColor(ImGuiCol_Text, COL_TEXT);

    // ---------- Meow.Net-style outer container ---------------------------------------------
    // Wider outer margin (fix #6) so the glass panel doesn't crowd the window edges. 26 -> 40.
    const float outerPad = 40.0f;
    const float outerX = outerPad;
    const float outerY = logo_bottom_y();
    const float outerW = sz.x - outerPad * 2;
    const float outerH = sz.y - outerY - outerPad;
    ImVec2 oPos(outerX, outerY);
    ImVec2 oBR (outerX + outerW, outerY + outerH);

    // Soft outer shadow for lift.
    fg->AddRectFilled(ImVec2(oPos.x + 4, oPos.y + 10), ImVec2(oBR.x + 4, oBR.y + 12),
                      IM_COL32(0, 0, 0, 60), 22.0f);
    // Glassmorphism body - dark, semi-transparent so the underlying purple-to-orange gradient
    // still shows through, but darker than before for stronger content contrast.
    fg->AddRectFilled(oPos, oBR, IM_COL32(12, 6, 18, 130), 20.0f);
    // Top-inner highlight - thin white sheen at the top edge for that "lit from above" feel.
    fg->AddRectFilledMultiColor(
        ImVec2(oPos.x + 1.5f, oPos.y + 1.5f), ImVec2(oBR.x - 1.5f, oPos.y + 60),
        IM_COL32(255, 255, 255, 42), IM_COL32(255, 255, 255, 42),
        IM_COL32(255, 255, 255, 0),  IM_COL32(255, 255, 255, 0));
    // Thin white hairline border - the frosted-glass edge signature.
    fg->AddRect(oPos, oBR, IM_COL32(255, 255, 255, 120), 20.0f, 0, 1.2f);

    // ---------- Header row (icon + title/subtitle + Exit) -----------------------------------
    const float hpad     = 28.0f;
    const float iconSz   = 60.0f;
    const float headerY  = oPos.y + 26.0f;
    const float iconX    = oPos.x + hpad;

    // Header Campus chip - vivid RED square (#E42C3E) with:
    //   - soft top-inner white highlight (raised glass look)
    //   - 1px bright-red INNER glow inset 2px from the outer edge (fix #1c)
    //   - crisp white outer border
    fg->AddRectFilled(ImVec2(iconX, headerY),
                      ImVec2(iconX + iconSz, headerY + iconSz),
                      IM_COL32(0xE4, 0x2C, 0x3E, 235), 12.0f);
    fg->AddRectFilledMultiColor(
        ImVec2(iconX + 1, headerY + 1),
        ImVec2(iconX + iconSz - 1, headerY + iconSz * 0.5f),
        IM_COL32(255, 255, 255, 60), IM_COL32(255, 255, 255, 60),
        IM_COL32(255, 255, 255, 0),  IM_COL32(255, 255, 255, 0));
    // Bright red inner glow - a hairline inset that catches light on the chip edge.
    fg->AddRect     (ImVec2(iconX + 2, headerY + 2),
                      ImVec2(iconX + iconSz - 2, headerY + iconSz - 2),
                      IM_COL32(255, 90, 110, 180), 10.0f, 0, 1.0f);
    fg->AddRect     (ImVec2(iconX, headerY),
                      ImVec2(iconX + iconSz, headerY + iconSz),
                      IM_COL32(255, 255, 255, 140), 12.0f, 0, 1.2f);
    if (g_ui.faceTex.srv) {
        float inset = 8.0f;
        fg->AddImage((ImTextureID)g_ui.faceTex.srv,
                     ImVec2(iconX + inset, headerY + inset),
                     ImVec2(iconX + iconSz - inset, headerY + iconSz - inset),
                     ImVec2(0,0), ImVec2(1,1), IM_COL32(255, 255, 255, 255));
    }

    // Title + subtitle
    float textX = iconX + iconSz + 16.0f;
    if (g_ui.fontH) {
        const char* title = "Campus Launcher";
        fg->AddText(g_ui.fontH, g_ui.fontH->FontSize,
                    ImVec2(textX + 1, headerY + 3 + 1), IM_COL32(0, 0, 0, 100), title);
        fg->AddText(g_ui.fontH, g_ui.fontH->FontSize,
                    ImVec2(textX, headerY + 3), IM_COL32(255, 255, 255, 255), title);
    }
    // Live subtitle - normally "Select your play mode", replaced with status while injecting.
    // Muted white per new spec (rgba(255,255,255,0.6) -> alpha 153) so main titles pop more.
    const char* subtitle = g_ui.status.c_str();
    if (!g_ui.injecting && !g_ui.injectionComplete) subtitle = "Select your play mode";
    fg->AddText(g_ui.fontBody, g_ui.fontBody->FontSize,
                ImVec2(textX, headerY + 40),
                g_ui.statusOk ? IM_COL32(255, 255, 255, 153) : IM_COL32(255, 190, 200, 230),
                subtitle);

    // Exit pill - vertically centered with the "Campus Launcher" title text specifically
    // (per spec) instead of the icon block. Title Y-center = headerY + 3 + fontH/2.
    const float exitW = 84.0f, exitH = 36.0f;
    float titleFontH = g_ui.fontH ? g_ui.fontH->FontSize : 28.0f;
    float titleCenterY = headerY + 3.0f + titleFontH * 0.5f;
    ImVec2 exitPos(oBR.x - hpad - exitW, floorf(titleCenterY - exitH * 0.5f));
    if (draw_pill_button("Exit", exitPos, ImVec2(exitW, exitH), 0, 0))
        PostMessage(g_hwnd, WM_CLOSE, 0, 0);

    // Divider under header - thin white
    float divY = headerY + iconSz + 22.0f;
    fg->AddLine(ImVec2(oPos.x + hpad, divY), ImVec2(oBR.x - hpad, divY),
                IM_COL32(255, 255, 255, 90), 1.0f);

    // ---------- Two play-mode cards ---------------------------------------------------------
    // Space between divider and footer row, split in half horizontally.
    const float footerRowH = 62.0f;                              // brand + Uninstall + Repair
    const float cardsTop   = divY + 22.0f;
    const float cardsBot   = oBR.y - footerRowH;
    const float cardsGap   = 16.0f;
    const float cardW      = (outerW - hpad * 2 - cardsGap) * 0.5f;
    const float cardH      = cardsBot - cardsTop;

    // Renders a Screen/VR play-mode card. `iconDraw` is a lambda that draws a vector icon
    // (monitor or headset) as a solid flat colour in the top-left, replacing the earlier
    // colored-chip-with-PNG approach - the PNG icons rendered muddy/pixelated at 40px, while
    // hand-drawn primitives are crisp and clearly recolourable. `iconColor` is the flat fill.
    auto draw_mode_card = [&](const char* title, const char* subtitle, const char* btnLabel,
                              ImVec2 pos, ImVec2 size,
                              auto iconDraw, ImU32 iconColor, auto onClick) {
        ImVec2 br(pos.x + size.x, pos.y + size.y);
        // Frosted glass card per spec:
        //   background: rgba(255,255,255,0.03)  ->  IM_COL32(255,255,255, 8)
        //   border:     1px solid rgba(255,255,255,0.15) -> IM_COL32(255,255,255, 38)
        fg->AddRectFilled(pos, br, IM_COL32(255, 255, 255, 8), 14.0f);
        // Subtle top-inner sheen kept for depth (a hair of white at the top edge).
        fg->AddRectFilledMultiColor(
            ImVec2(pos.x + 1, pos.y + 1), ImVec2(br.x - 1, pos.y + 40),
            IM_COL32(255, 255, 255, 25), IM_COL32(255, 255, 255, 25),
            IM_COL32(255, 255, 255, 0),  IM_COL32(255, 255, 255, 0));
        fg->AddRect(pos, br, IM_COL32(255, 255, 255, 38), 14.0f, 0, 1.0f);

        // Coloured chip BEHIND the native-colour icon. The chip supplies the brand-adjacent
        // colour context (purple for Screen, orange for VR) that ties the card into the
        // background gradient, while the icon on top keeps all its native detail.
        const float icSz = 56.0f;
        ImVec2 icPos(pos.x + 22.0f, pos.y + 20.0f);
        ImVec2 icBR (icPos.x + icSz, icPos.y + icSz);
        // soft drop shadow beneath the chip
        fg->AddRectFilled(ImVec2(icPos.x + 1, icPos.y + 4), ImVec2(icBR.x + 1, icBR.y + 4),
                          IM_COL32(0, 0, 0, 60), 12.0f);
        // chip body (iconColor is the flat brand tint)
        fg->AddRectFilled(icPos, icBR, iconColor, 12.0f);
        // top-inner white highlight for the raised-tile feel
        fg->AddRectFilledMultiColor(
            ImVec2(icPos.x + 1, icPos.y + 1), ImVec2(icBR.x - 1, icPos.y + icSz * 0.5f),
            IM_COL32(255, 255, 255, 55), IM_COL32(255, 255, 255, 55),
            IM_COL32(255, 255, 255, 0),  IM_COL32(255, 255, 255, 0));
        // thin white glass border
        fg->AddRect     (icPos, icBR, IM_COL32(255, 255, 255, 120), 12.0f, 0, 1.2f);

        // Icon sits ON TOP of the chip, in its native colours (Steam blue+white, Meta blue).
        // iconDraw does aspect-preserved fit inside a 44x44 inner box (icSz - 12 = 12px pad).
        const float iconPad = 10.0f;
        ImVec2 iconInnerPos(icPos.x + iconPad, icPos.y + iconPad);
        iconDraw(fg, iconInnerPos, icSz - iconPad * 2, IM_COL32(255,255,255,255));

        // Tightened typography (fix #3 - was too much whitespace):
        //   title at pos.y + 80 (was 92)
        //   subtitle at pos.y + 116 (was 130)
        // Subtitle uses muted white alpha 153 (0.6) per new spec (was 178).
        if (g_ui.fontH) {
            fg->AddText(g_ui.fontH, g_ui.fontH->FontSize,
                        ImVec2(pos.x + 22, pos.y + 80),
                        IM_COL32(255, 255, 255, 255), title);
        }
        fg->AddText(g_ui.fontBody, g_ui.fontBody->FontSize,
                    ImVec2(pos.x + 22, pos.y + 116),
                    IM_COL32(255, 255, 255, 153), subtitle);

        // LAUNCH pill anchored to the bottom of the card, closer to subtitle now.
        const float bh = 42.0f;
        const float bMargin = 18.0f;
        float bw = size.x - bMargin * 2;
        ImVec2 bPos(pos.x + bMargin, br.y - bh - bMargin);
        ImVec2 bBR (bPos.x + bw, bPos.y + bh);
        ImGui::SetCursorScreenPos(bPos);
        ImGui::InvisibleButton(btnLabel, ImVec2(bw, bh));
        bool hov = ImGui::IsItemHovered();
        bool clk = ImGui::IsItemClicked();
        float hv = hover_step(std::string("play_") + btnLabel, hov, ImGui::GetIO().DeltaTime);
        // LAUNCH button per spec:
        //   background: rgba(255,255,255,0.1)   -> alpha 26
        //   hover:      rgba(255,255,255,0.2)   -> alpha 51
        //   border: 1px solid rgba(255,255,255,0.6)  -> alpha 153
        //   text + play triangle: pure white
        int fillA = 26 + (int)(25 * hv);
        fg->AddRectFilled(bPos, bBR, IM_COL32(255, 255, 255, fillA), 12.0f);
        fg->AddRect     (bPos, bBR, IM_COL32(255, 255, 255, 153), 12.0f, 0, 1.0f);
        // Play triangle + label, both bright white. Triangle drawn via AddConvexPolyFilled
        // (fix #2 - larger, crisper vector triangle with proper AA rather than the earlier
        // small AddTriangleFilled). Sized to match the label's cap-height so it reads as a
        // proper play glyph, not a bullet.
        {
            ImVec2 ts = ImGui::CalcTextSize(btnLabel);
            const float triH = ts.y * 0.90f;                      // slightly under cap-height
            const float triW = triH * 0.85f;                      // narrower isoceles play arrow
            const float triGap = 12.0f;
            float groupW = triW + triGap + ts.x;
            float baseX = floorf(bPos.x + (bw - groupW) * 0.5f);
            float baseY = bPos.y + bh * 0.5f;
            ImVec2 pts[3] = {
                ImVec2(baseX,          baseY - triH * 0.5f),
                ImVec2(baseX + triW,   baseY),
                ImVec2(baseX,          baseY + triH * 0.5f),
            };
            ImVec2 ptsShadow[3] = {
                ImVec2(pts[0].x + 1, pts[0].y + 1),
                ImVec2(pts[1].x + 1, pts[1].y + 1),
                ImVec2(pts[2].x + 1, pts[2].y + 1),
            };
            fg->AddConvexPolyFilled(ptsShadow, 3, IM_COL32(0, 0, 0, 110));
            fg->AddConvexPolyFilled(pts, 3, IM_COL32(255, 255, 255, 255));
            float labelX = baseX + triW + triGap;
            float labelY = bPos.y + (bh - ts.y) * 0.5f;
            fg->AddText(g_ui.fontBody, g_ui.fontBody->FontSize,
                        ImVec2(labelX + 1, labelY + 1),
                        IM_COL32(0, 0, 0, 110), btnLabel);
            fg->AddText(g_ui.fontBody, g_ui.fontBody->FontSize,
                        ImVec2(labelX, labelY),
                        IM_COL32(255, 255, 255, 255), btnLabel);
        }
        if (clk && !g_ui.injecting) onClick();
    };

    // Icon drawer: paint the given texture at its NATIVE colours, aspect-preserved, centred
    // in the 56x56 box. Tint colour parameter is now just for API compatibility (kept white
    // = no colour multiply) - the PNGs render as-is with their real Steam/Meta artwork.
    auto draw_native_icon = [](ImDrawList* d, ImVec2 pos, float sz, ImU32 /*col*/,
                                GpuTexture* tex) {
        if (!tex || !tex->srv) return;
        float ar = (float)tex->w / (float)tex->h;
        float boxW = sz, boxH = sz;
        float dw = boxW, dh = boxH;
        if (ar > 1.0f) dh = dw / ar; else dw = dh * ar;
        float dx = floorf(pos.x + (sz - dw) * 0.5f);
        float dy = floorf(pos.y + (sz - dh) * 0.5f);
        d->AddImage((ImTextureID)tex->srv,
                    ImVec2(dx, dy), ImVec2(dx + dw, dy + dh),
                    ImVec2(0,0), ImVec2(1,1), IM_COL32(255, 255, 255, 255));
    };

    ImVec2 scPos(oPos.x + hpad, cardsTop);
    draw_mode_card("Screen", "Play in Screen Mode", "LAUNCH SCREEN",
                   scPos, ImVec2(cardW, cardH),
                   [&draw_native_icon](ImDrawList* d, ImVec2 p, float s, ImU32 c) {
                       draw_native_icon(d, p, s, c, &g_ui.steamTex);
                   },
                   IM_COL32(255, 255, 255, 255),             // ignored (icon renders native)
                   []{
        HANDLE th = CreateThread(nullptr, 0, inject_thread, nullptr, 0, nullptr);
        if (th) CloseHandle(th);
    });
    ImVec2 vrPos(scPos.x + cardW + cardsGap, cardsTop);
    draw_mode_card("VR", "Play in VR", "LAUNCH VR",
                   vrPos, ImVec2(cardW, cardH),
                   [&draw_native_icon](ImDrawList* d, ImVec2 p, float s, ImU32 c) {
                       draw_native_icon(d, p, s, c, &g_ui.metaTex);
                   },
                   IM_COL32(255, 255, 255, 255),             // ignored (icon renders native)
                   []{
        HANDLE th = CreateThread(nullptr, 0, inject_thread, nullptr, 0, nullptr);
        if (th) CloseHandle(th);
    });

    // ---------- Footer row inside the outer card --------------------------------------------
    // "Campus  ·  Made by Kyle" on the left. Uninstall (red-tinted) + Repair pills on right.
    float fRowY = cardsBot + 14.0f;
    // Draw "Campus" bold-ish, then a subtle "Made by Kyle" beside it in dimmer white.
    const char* brand   = "Campus";
    const char* byline  = "Made by Kyle";
    float txtBaseY = fRowY + 10;
    fg->AddText(g_ui.fontBody, g_ui.fontBody->FontSize,
                ImVec2(oPos.x + hpad, txtBaseY),
                IM_COL32(255, 255, 255, 230), brand);
    float bw = ImGui::CalcTextSize(brand).x;
    fg->AddText(g_ui.fontBody, g_ui.fontBody->FontSize,
                ImVec2(oPos.x + hpad + bw + 10, txtBaseY),
                IM_COL32(255, 255, 255, 130), byline);

    const float fbtnW = 92.0f, fbtnH = 34.0f;
    ImVec2 repPos(oBR.x - hpad - fbtnW, fRowY + 4);
    ImVec2 uninPos(repPos.x - fbtnW - 10, fRowY + 4);
    // Repair - default glass pill (fill 5% white, border 30% white).
    if (draw_pill_button("Repair", repPos, ImVec2(fbtnW, fbtnH), 0, 0)) {
        log_line("Repair: no source configured (placeholder)");
        g_ui.status = "Repair: source not configured"; g_ui.statusOk = false;
    }
    // Uninstall - red warning tint per spec:
    //   background: rgba(200,50,50,0.15) -> IM_COL32(200,50,50,38)
    //   border:     1px solid rgba(200,50,50,0.4) -> IM_COL32(200,50,50,102)
    if (draw_pill_button("Uninstall", uninPos, ImVec2(fbtnW, fbtnH),
                         IM_COL32(200, 50, 50, 38),
                         IM_COL32(200, 50, 50, 102))) {
        log_line("Uninstall: not wired up yet (placeholder)");
        g_ui.status = "Uninstall: not wired up"; g_ui.statusOk = false;
    }

    // Outer pink window edge - matches the GDI+ splash's #9B3C96 frame.
    fg->AddRect(ImVec2(0.5f, 0.5f), ImVec2(sz.x - 0.5f, sz.y - 0.5f),
                IM_COL32(155, 60, 150, 220), 12.0f, 0, 1.5f);

    ImGui::PopStyleColor();
    ImGui::PopFont();
    ImGui::End();
    ImGui::PopStyleVar(3);
    ImGui::PopStyleColor();
}

// ===============================================================================================
// Window proc - pure WS_POPUP borderless (WM_NCCALCSIZE returns 0)
// ===============================================================================================
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND, UINT, WPARAM, LPARAM);
static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (ImGui_ImplWin32_WndProcHandler(hwnd, msg, wp, lp)) return true;
    switch (msg) {
        case WM_NCCALCSIZE: if (wp) return 0; return 0;
        case WM_SIZE:
            if (g_dev && wp != SIZE_MINIMIZED) {
                if (g_rtv) { g_rtv->Release(); g_rtv = nullptr; }
                g_sc->ResizeBuffers(0, (UINT)LOWORD(lp), (UINT)HIWORD(lp), DXGI_FORMAT_UNKNOWN, 0);
                ID3D11Texture2D* back = nullptr; g_sc->GetBuffer(0, IID_PPV_ARGS(&back));
                g_dev->CreateRenderTargetView(back, nullptr, &g_rtv); back->Release();
            }
            return 0;
        case WM_SYSCOMMAND:
            if ((wp & 0xfff0) == SC_KEYMENU) return 0;
            break;
        case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcA(hwnd, msg, wp, lp);
}

// ===============================================================================================
// WinMain
// ===============================================================================================
int WINAPI WinMain(HINSTANCE hInst, HINSTANCE, LPSTR, int) {
    log_line("Campus Launcher started. base=%s", module_dir().c_str());

    WNDCLASSEXA wc = { sizeof(wc), CS_CLASSDC, WndProc, 0L, 0L, hInst,
        LoadIconA(hInst, MAKEINTRESOURCEA(IDI_MAIN_ICON)),
        LoadCursor(nullptr, IDC_ARROW),
        nullptr, nullptr, "CampusLauncher",
        LoadIconA(hInst, MAKEINTRESOURCEA(IDI_MAIN_ICON)) };
    RegisterClassExA(&wc);

    HWND hwnd = CreateWindowExA(WS_EX_APPWINDOW,
        "CampusLauncher", "Campus Launcher", WS_POPUP,
        CW_USEDEFAULT, CW_USEDEFAULT, 1020, 700,
        nullptr, nullptr, hInst, nullptr);
    g_hwnd = hwnd;
    { int c = 2; DwmSetWindowAttribute(hwnd, 33, &c, sizeof(c)); }

    if (!create_device(hwnd)) { MessageBoxA(hwnd, "D3D11 init failed", "Campus", MB_ICONERROR); return 1; }
    ShowWindow(hwnd, SW_SHOWDEFAULT); UpdateWindow(hwnd);

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    ImGui::StyleColorsDark();
    ImGui_ImplWin32_Init(hwnd);
    ImGui_ImplDX11_Init(g_dev, g_ctx);

    {
        const void* fontData; size_t fontSz;
        bool haveRubik = find_rcdata(IDR_RUBIK_TTF, fontData, fontSz);
        auto add = [&](float px) -> ImFont* {
            if (haveRubik) {
                ImFontConfig cfg; cfg.FontDataOwnedByAtlas = false;
                return io.Fonts->AddFontFromMemoryTTF(const_cast<void*>(fontData),
                    (int)fontSz, px, &cfg);
            } else {
                ImFontConfig cfg; cfg.SizePixels = px;
                return io.Fonts->AddFontDefault(&cfg);
            }
        };
        g_ui.fontBody  = add(15.0f);
        g_ui.fontLabel = add(17.0f);
        g_ui.fontH     = add(28.0f);
        g_ui.fontTiny  = add(12.5f);
        io.Fonts->Build();
    }
    {
        ImGuiStyle& s = ImGui::GetStyle();
        s.FrameRounding = 8.0f;
        s.GrabRounding  = 6.0f;
        s.WindowRounding = 0.0f;
        s.ItemSpacing = ImVec2(10, 8);
        s.FramePadding = ImVec2(10, 6);
    }

    if (load_embedded_png_texture(g_dev, g_ctx, IDR_ANIMATED_ICON_PNG, g_ui.faceTex))
        log_line("Loaded AnimatedIcon (%dx%d) w/ mips", g_ui.faceTex.w, g_ui.faceTex.h);
    if (load_embedded_png_texture(g_dev, g_ctx, IDR_BACKGROUND_PNG, g_ui.bgTex))
        log_line("Loaded Background (%dx%d) w/ mips", g_ui.bgTex.w, g_ui.bgTex.h);
    if (load_embedded_png_texture(g_dev, g_ctx, IDR_CUTOUT_PNG, g_ui.logoTex))
        log_line("Loaded Cutout logo (%dx%d) w/ mips", g_ui.logoTex.w, g_ui.logoTex.h);
    // Steam + Meta loaded at NATIVE colours. Earlier we made silhouettes and tinted with a
    // brand colour, but that collapsed Steam's inner gearwheel + "S" detail into a solid
    // purple blob (Meta's infinity is a pure silhouette so it worked fine either way). Native
    // colours preserve both icons' inner detail cleanly.
    if (load_embedded_png_texture(g_dev, g_ctx, IDR_STEAM_ICON_PNG, g_ui.steamTex))
        log_line("Loaded Steam icon (%dx%d) w/ mips", g_ui.steamTex.w, g_ui.steamTex.h);
    if (load_embedded_png_texture(g_dev, g_ctx, IDR_META_LOGO_PNG, g_ui.metaTex))
        log_line("Loaded Meta logo (%dx%d) w/ mips", g_ui.metaTex.w, g_ui.metaTex.h);

    g_ui.status = "Click LAUNCH to find Campus and start the game.";

    bool done = false;
    while (!done) {
        MSG msg;
        while (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg); DispatchMessage(&msg);
            if (msg.message == WM_QUIT) done = true;
        }
        if (done) break;
        if (GetForegroundWindow() != hwnd) Sleep(80);

        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();
        draw_ui();
        ImGui::Render();

        float clear[4] = { 0.05f, 0.02f, 0.07f, 1.0f };
        g_ctx->OMSetRenderTargets(1, &g_rtv, nullptr);
        g_ctx->ClearRenderTargetView(g_rtv, clear);
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
        g_sc->Present(1, 0);
    }

    g_ui.faceTex.release(); g_ui.bgTex.release(); g_ui.logoTex.release();
    g_ui.steamTex.release(); g_ui.metaTex.release();
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    destroy_device();
    DestroyWindow(hwnd);
    UnregisterClassA("CampusLauncher", hInst);
    return 0;
}
