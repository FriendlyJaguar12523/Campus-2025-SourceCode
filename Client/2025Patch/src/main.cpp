#include "RR/Patching/Patches.h"
#include "Protections/Protections.h"
#include "Utils/logging.h"
#include "Utils/obf.h"
#include "Discord/DiscordRPC_Presence.h"
#include "RR/UnityExplorer/imgui_inspector.h"          // <-- added for ImGui Inspector
// The Present hook below feeds window messages to ImGui and asks it what it wants to
// capture, so main.cpp needs imgui itself - not just the inspector's facade.
#include "RR/imgui/imgui.h"
#include "RR/imgui/backends/imgui_impl_win32.h"
#include "RR/UI/CampusMenu.h"
#include "RR/UI/DevPanel.h"
#include "RR/Patching/Il2CppApi.h"
#include "RR/Patching/PatchProof.h"
#include "RR/Patching/LoadTurbo.h"
#include "Utils/DebugTools.h"
#include <DbgHelp.h>
#include <algorithm>
#include <vector>
#include <atomic>
#include <winhttp.h>
#include <d3d11.h>                    // for D3D11 device
#include <dxgi.h>                     // for swapchain
#pragma comment(lib, "dbghelp.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "winhttp.lib")
// FindPresentAddress creates a throwaway device to read IDXGISwapChain's vtable.
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

static HMODULE g_selfModule = nullptr;
static char g_crashDumpDir[MAX_PATH] = {};
static bool g_antiCheatStarted = false;
static bool g_consoleMinimized = true;
static std::atomic<bool> g_keyListenerRunning{ true };
static std::atomic<bool> g_gameWindowFound{ false };
static HANDLE g_keyThread = nullptr;
static CRITICAL_SECTION g_windowCS;

// ================================================================
// GLOBAL ANTI-CHEAT MASTER SWITCH
// ================================================================
#ifdef CAMPUS_DEV_BUILD
bool Protections::AntiCheatEnabled = false;   // the Dev build configuration: no anti-cheat (so a debugger / tools can be attached), the dev channel only
#else
bool Protections::AntiCheatEnabled = true;    // master switch: true = all anti-cheat modules armed, false = disable everything
#endif

// ================================================================
// IMGUI INSPECTOR – GLOBALS
// ================================================================
static bool g_imguiInitialized = false;
static ID3D11Device* g_pd3dDevice = nullptr;
static ID3D11DeviceContext* g_pd3dDeviceContext = nullptr;
static IDXGISwapChain* g_pSwapChain = nullptr;
static HWND g_gameHwnd = nullptr;

// ================================================================
// UI HIERARCHY DUMPER (debug only – no external DLL needed)
// ================================================================
// The dev panel (Avatar Fun: INSERT key + the ImGui Present hook) exists in Debug builds AND in the Dev-channel build.
// The Dev build is NDEBUG, but it is only ever handed to developer accounts (the launcher serves it to no one else),
// so it is the safe place to ship the panel before any "for everyone" toggle exists.
#if !defined(NDEBUG) || defined(CAMPUS_DEV_BUILD)
#define CAMPUS_DEV_PANEL 1
#endif

#ifndef NDEBUG

#include <iomanip>
#include <sstream>

static void DumpTransform(void* transform, int depth, int maxDepth = 20) {
    if (!transform || depth > maxDepth) return;

    void* gameObject = *(void**)((uintptr_t)transform + 0x10);
    if (!gameObject) return;

    Il2cppString* nameStr = *(Il2cppString**)((uintptr_t)gameObject + 0x60);
    std::string name = nameStr ? ReadIl2CppString(nameStr) : "(unnamed)";

    std::string indent(depth * 2, ' ');

    void* component = *(void**)((uintptr_t)gameObject + 0x30);
    std::string compType = "unknown";
    if (component) {
        std::stringstream ss;
        ss << std::hex << "(0x" << (uintptr_t)component << ")";
        compType = ss.str();
    }

    std::cout << indent << "├─ " << name << " [transform 0x" << std::hex << (uintptr_t)transform << std::dec << "] comp " << compType << "\n";

    int childCount = *(int*)((uintptr_t)transform + 0x38);
    auto getChild = (void* (*)(void*, int))(GA + RR::Methods::UnityEngine::Transform_GetChild);
    if (!getChild) return;

    for (int i = 0; i < childCount; i++) {
        void* child = getChild(transform, i);
        if (child) DumpTransform(child, depth + 1, maxDepth);
    }
}

static void DumpUIHierarchy() {
    if (!GA || !NtHeaders) {
        std::cout << "[UI Dump] GameAssembly not ready\n";
        return;
    }

    if (!RR::Methods::UnityEngine::GameObject_Find) {
        std::cout << "[UI Dump] GameObject_Find not resolved – cannot dump.\n";
        return;
    }

    using FindFn = void* (*)(Il2cppString*);
    auto find = (FindFn)(GA + RR::Methods::UnityEngine::GameObject_Find);
    if (!find) {
        std::cout << "[UI Dump] Find function null\n";
        return;
    }

    const char* rootNames[] = { "UICanvas", "Main Canvas", "Canvas", "AppNavMenu", "UI Root" };
    void* rootGO = nullptr;

    using Il2cppStringNewFn = Il2cppString * (*)(const char*);
    auto strNew = (Il2cppStringNewFn)(GA + RR::Methods::Il2cpp::il2cpp_string_new);
    if (!strNew) {
        std::cout << "[UI Dump] il2cpp_string_new not available\n";
        return;
    }

    for (const char* name : rootNames) {
        Il2cppString* s = strNew(name);
        if (!s) continue;
        rootGO = find(s);
        if (rootGO) {
            std::cout << "[UI Dump] Found root: " << name << "\n";
            break;
        }
    }

    if (!rootGO) {
        std::cout << "[UI Dump] No known UI root found – dumping from AppNavMenu instance if available.\n";
        using GetInstanceFn = void* (*)();
        auto getInstance = (GetInstanceFn)(GA + RR::Methods::AppNav::GetInstance);
        if (getInstance) {
            void* appNav = getInstance();
            if (appNav) {
                void* transform = *(void**)((uintptr_t)appNav + 0x30);
                if (transform) {
                    std::cout << "[UI Dump] Dumping from AppNavMenu transform\n";
                    DumpTransform(transform, 0);
                    return;
                }
            }
        }
        std::cout << "[UI Dump] Could not find any UI root.\n";
        return;
    }

    auto getTransform = (void* (*)(void*))(GA + RR::Methods::UnityEngine::GameObject_get_transform);
    if (!getTransform) {
        std::cout << "[UI Dump] get_transform not resolved\n";
        return;
    }
    void* rootTransform = getTransform(rootGO);
    if (!rootTransform) {
        std::cout << "[UI Dump] Root has no transform\n";
        return;
    }

    std::cout << "[UI Dump] ========== UI HIERARCHY ==========\n";
    DumpTransform(rootTransform, 0);
    std::cout << "[UI Dump] ========== END ==========\n";
}

#endif // NDEBUG

// ================================================================
// CRASH HANDLER
// ================================================================
static LONG WINAPI CrashDumpFilter(EXCEPTION_POINTERS* ep) {
    if (g_crashDumpDir[0] == 0) return EXCEPTION_CONTINUE_SEARCH;

    SYSTEMTIME st; GetLocalTime(&st);
    char path[MAX_PATH];
    _snprintf_s(path, sizeof(path), _TRUNCATE,
        "%s\\crash_%04d%02d%02d_%02d%02d%02d.dmp",
        g_crashDumpDir,
        st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond);

    HANDLE f = CreateFileA(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return EXCEPTION_CONTINUE_SEARCH;

    MINIDUMP_EXCEPTION_INFORMATION mei;
    mei.ThreadId = GetCurrentThreadId();
    mei.ExceptionPointers = ep;
    mei.ClientPointers = FALSE;
    MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), f,
        MiniDumpNormal, &mei, nullptr, nullptr);
    CloseHandle(f);
    return EXCEPTION_CONTINUE_SEARCH;
}

static void InstallCrashHandler(HMODULE self) {
    char dllPath[MAX_PATH] = {};
    if (!GetModuleFileNameA(self, dllPath, MAX_PATH)) return;
    char* slash = strrchr(dllPath, '\\');
    if (slash) *slash = 0;
    _snprintf_s(g_crashDumpDir, sizeof(g_crashDumpDir), _TRUNCATE, "%s\\Logs", dllPath);
    CreateDirectoryA(g_crashDumpDir, nullptr);
    SetUnhandledExceptionFilter(CrashDumpFilter);
}

// ================================================================
// WINDOW HELPERS
// ================================================================
static HWND SafeFindWindow(const char* title) {
    __try {
        return FindWindowA(nullptr, title);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

static HWND SafeGetGameWindowRaw() {
    __try {
        HWND hWnd = FindWindowA(nullptr, "Rec Room");
        if (!hWnd)
            hWnd = FindWindowA(nullptr, "Unity Player");
        if (!hWnd)
            hWnd = FindWindowA(nullptr, "Campus");
        return hWnd;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

static bool SafeIsWindow(HWND hWnd) {
    __try {
        return IsWindow(hWnd) != FALSE;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool SafeSetWindowText(HWND hWnd, const char* text) {
    __try {
        SetWindowTextA(hWnd, text);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool SafeGetWindowRect(HWND hWnd, RECT* rect) {
    __try {
        return GetWindowRect(hWnd, rect) != FALSE;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool SafeIsWindowVisible(HWND hWnd) {
    __try {
        return IsWindowVisible(hWnd) != FALSE;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static void SafeShowWindow(HWND hWnd, int cmd) {
    __try {
        ShowWindow(hWnd, cmd);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void SafeSetForegroundWindow(HWND hWnd) {
    __try {
        SetForegroundWindow(hWnd);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void SafeSetWindowTopmost(HWND hWnd) {
    __try {
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void SafeSetWindowNotTopmost(HWND hWnd) {
    __try {
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static HWND GetGameWindow()
{
    EnterCriticalSection(&g_windowCS);
    HWND result = SafeGetGameWindowRaw();
    LeaveCriticalSection(&g_windowCS);
    return result;
}

static void SetCampusWindowTitle()
{
    HWND hWnd = GetGameWindow();
    if (hWnd && SafeIsWindow(hWnd)) {
        if (SafeSetWindowText(hWnd, "Campus")) {
            g_gameWindowFound = true;
        }
    }
}

static void SimulateAltEnter()
{
    HWND hWnd = GetGameWindow();
    if (!hWnd || !SafeIsWindow(hWnd)) return;

    INPUT ip[4] = {};
    int index = 0;

    ip[index].type = INPUT_KEYBOARD;
    ip[index].ki.wVk = VK_MENU;
    index++;

    ip[index].type = INPUT_KEYBOARD;
    ip[index].ki.wVk = VK_RETURN;
    index++;

    ip[index].type = INPUT_KEYBOARD;
    ip[index].ki.wVk = VK_RETURN;
    ip[index].ki.dwFlags = KEYEVENTF_KEYUP;
    index++;

    ip[index].type = INPUT_KEYBOARD;
    ip[index].ki.wVk = VK_MENU;
    ip[index].ki.dwFlags = KEYEVENTF_KEYUP;
    index++;

    SendInput(index, ip, sizeof(INPUT));
    std::cout << TAG_INIT() << "Simulated Alt+Enter\n";
}

static bool IsGameReady()
{
    HWND hWnd = GetGameWindow();
    if (!hWnd || !SafeIsWindow(hWnd) || !SafeIsWindowVisible(hWnd))
        return false;

    RECT rect;
    if (!SafeGetWindowRect(hWnd, &rect))
        return false;

    int width = rect.right - rect.left;
    int height = rect.bottom - rect.top;

    return (width > 400 && height > 300);
}

// ================================================================
// KEY LISTENER THREAD (includes UI dump and ImGui toggle)
// ================================================================
static DWORD WINAPI KeyListenerThread(LPVOID)
{
    std::cout << TAG_INIT() << "Key listener started\n";
    std::cout << TAG_INIT() << "Shift+ESC - Toggle console\n";
    std::cout << TAG_INIT() << "F11 - Toggle fullscreen\n";
    std::cout << TAG_INIT() << "F12 - Toggle always on top\n";
#ifdef CAMPUS_DEV_PANEL
    std::cout << TAG_INIT() << "INSERT - Toggle the Avatar Fun panel\n";
#endif

    bool shiftHeld = false;
    bool f11Pressed = false;
    bool f12Pressed = false;
    bool isTopmost = true;
    bool nPressed = false;   // Ctrl+Shift+N notification - always-on feature (Release too)
#ifdef CAMPUS_DEV_PANEL
    bool uPressed = false;
    bool dPressed = false;
    bool iPressed = false;
    bool mPressed = false;
#endif

    while (g_keyListenerRunning) {
        bool shiftNow = (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;
        bool escNow = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
        bool f11Now = (GetAsyncKeyState(VK_F11) & 0x8000) != 0;
        bool f12Now = (GetAsyncKeyState(VK_F12) & 0x8000) != 0;
#ifndef NDEBUG
        bool uNow = (GetAsyncKeyState('U') & 0x8000) != 0;
        bool iNow = (GetAsyncKeyState('I') & 0x8000) != 0;
#endif
        bool ctrlNow = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;

        // Shift+ESC: toggle console
        if (shiftNow && escNow && !shiftHeld) {
            HWND hWnd = GetConsoleWindow();
            if (hWnd && SafeIsWindow(hWnd)) {
                if (g_consoleMinimized) {
                    SafeShowWindow(hWnd, SW_RESTORE);
                    SafeSetForegroundWindow(hWnd);
                    g_consoleMinimized = false;
                    std::cout << TAG_INIT() << "Console restored\n";
                }
                else {
                    SafeShowWindow(hWnd, SW_MINIMIZE);
                    g_consoleMinimized = true;
                    std::cout << TAG_INIT() << "Console minimized\n";
                }
            }
            shiftHeld = true;
            Sleep(200);
        }
        else if (!shiftNow) {
            shiftHeld = false;
        }

        // F11: toggle fullscreen
        if (f11Now && !f11Pressed) {
            SimulateAltEnter();
            f11Pressed = true;
            Sleep(200);
        }
        else if (!f11Now) {
            f11Pressed = false;
        }

        // F12: toggle topmost
        if (f12Now && !f12Pressed) {
            HWND gameWnd = GetGameWindow();
            if (gameWnd && SafeIsWindow(gameWnd)) {
                isTopmost = !isTopmost;
                if (isTopmost) {
                    SafeSetWindowTopmost(gameWnd);
                    std::cout << TAG_INIT() << "Game set to TOPMOST\n";
                }
                else {
                    SafeSetWindowNotTopmost(gameWnd);
                    std::cout << TAG_INIT() << "Game removed from TOPMOST\n";
                }
            }
            f12Pressed = true;
            Sleep(200);
        }
        else if (!f12Now) {
            f12Pressed = false;
        }

#ifdef CAMPUS_DEV_PANEL
        // INSERT: show/hide the dev panel. The only non-console key in the build, and
        // deliberately a single key (not a Ctrl+Shift chord). Debug builds only.
        if ((GetAsyncKeyState(VK_INSERT) & 0x8000) && !dPressed) {
            DevPanel::ToggleVisibility();
            dPressed = true;
            Sleep(200);
        }
        else if (!(GetAsyncKeyState(VK_INSERT) & 0x8000)) {
            dPressed = false;
        }
#endif

        Sleep(30);
    }
    return 0;
}

// ================================================================
// ANTI-CHEAT STARTER
// ================================================================
static DWORD WINAPI AntiCheatStarterThread(LPVOID) {
    std::cout << "\n";
    std::cout << TAG_INIT() << "Waiting for game to fully load...\n";
    std::cout << TAG_INIT() << "Anti-cheat will start automatically when login page appears.\n";
    std::cout << "\n";

    int waitCount = 0;
    bool gameReady = false;

    while (waitCount < 600 && g_keyListenerRunning) {
        HWND gameWindow = GetGameWindow();
        if (gameWindow && SafeIsWindow(gameWindow)) {
            if (!g_gameWindowFound) {
                if (SafeSetWindowText(gameWindow, "Campus")) {
                    g_gameWindowFound = true;
                    std::cout << TAG_INIT() << "Window renamed to Campus\n";
                }
            }
            static bool topmostSet = false;
            if (!topmostSet) {
                SafeSetWindowTopmost(gameWindow);
                topmostSet = true;
                std::cout << TAG_INIT() << "Game set to TOPMOST\n";
            }
        }

        if (waitCount > 200 && IsGameReady()) {
            gameReady = true;
            std::cout << "\n";
            std::cout << TAG_INIT() << "[+] Game window detected! Login page is visible.\n";
            break;
        }

        if (waitCount % 50 == 0 && waitCount > 0) {
            std::cout << TAG_INIT() << "Waiting for game... " << (waitCount / 10) << "s elapsed\n";
        }

        Sleep(100);
        waitCount++;
    }

    std::cout << "\n";
    std::cout << "\t[////////////////////////////////////////////////////////////////]\n";
    std::cout << "       \t\tKurt Cobain just fucking hates you, go suck a fat one\n";
    std::cout << "\t[////////////////////////////////////////////////////////////////]\n";
    std::cout << "\n";

    Protections::Initialize();
    g_antiCheatStarted = true;

    DiscordRPC::Start();

    return 0;
}

// ================================================================
// SERVER CONNECTION TEST
// ================================================================
static void TestServerConnection() {
    std::cout << TAG_INIT() << "Testing connection to " << CAMPUS_API_HOST << "...\n";

    HINTERNET hSession = WinHttpOpen(L"Test/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, NULL, NULL, 0);
    if (hSession) {
        std::wstring host = CampusWiden(CAMPUS_API_HOST);
        HINTERNET hConnect = WinHttpConnect(hSession, CampusHostW(host).c_str(), CampusPortOf(host), 0);
        if (hConnect) {
            HINTERNET hRequest = WinHttpOpenRequest(hConnect, L"GET", L"/", NULL, NULL, NULL, WINHTTP_FLAG_SECURE);
            if (hRequest) {
                DWORD flags = SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_CN_INVALID | SECURITY_FLAG_IGNORE_CERT_DATE_INVALID;
                WinHttpSetOption(hRequest, WINHTTP_OPTION_SECURITY_FLAGS, &flags, sizeof(flags));

                if (WinHttpSendRequest(hRequest, NULL, 0, NULL, 0, 0, 0)) {
                    std::cout << TAG_INIT() << "[+] Connection test to " << CAMPUS_API_HOST << " SUCCESSFUL\n";
                    WinHttpCloseHandle(hRequest);
                    WinHttpCloseHandle(hConnect);
                    WinHttpCloseHandle(hSession);
                    return;
                }
                else {
                    DWORD error = GetLastError();
                    std::cout << TAG_INIT() << "[-] Connection test to " << CAMPUS_API_HOST << " FAILED (error: " << error << ")\n";
                }
                WinHttpCloseHandle(hRequest);
            }
            WinHttpCloseHandle(hConnect);
        }
        WinHttpCloseHandle(hSession);
    }
}

// ================================================================
// PATCH VERIFICATION
// ================================================================
static bool VerifyPatch() {
    bool ok = false;
    std::wstring host = CampusWiden(CAMPUS_API_HOST);
    HINTERNET hSession = WinHttpOpen(L"CampusPatch/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, NULL, NULL, 0);
    if (hSession) {
        HINTERNET hConnect = WinHttpConnect(hSession, CampusHostW(host).c_str(), CampusPortOf(host), 0);
        if (hConnect) {
            HINTERNET hRequest = WinHttpOpenRequest(hConnect, L"POST", L"/api/patch/verify", NULL, NULL, NULL, WINHTTP_FLAG_SECURE);
            if (hRequest) {
                DWORD flags = SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_CN_INVALID | SECURITY_FLAG_IGNORE_CERT_DATE_INVALID;
                WinHttpSetOption(hRequest, WINHTTP_OPTION_SECURITY_FLAGS, &flags, sizeof(flags));

                std::wstring header = std::wstring((const wchar_t*)skCrypt(L"X-Campus-Patch: ")) + std::wstring((const wchar_t*)skCrypt(L"CAMPUS-2025-2h7Fc20FKokap9Ah3t")) + L"\r\n";
                if (WinHttpSendRequest(hRequest, header.c_str(), (DWORD)-1L, NULL, 0, 0, 0)
                    && WinHttpReceiveResponse(hRequest, NULL)) {
                    DWORD status = 0, size = sizeof(status);
                    WinHttpQueryHeaders(hRequest,
                        WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX);
                    ok = (status == 200);
                    std::cout << TAG_INIT() << "[patch] verify -> HTTP " << status << (ok ? " (VALID)" : " (DENIED)") << "\n";
                }
                else {
                    std::cout << TAG_INIT() << "[patch] verify request FAILED (error " << GetLastError() << ")\n";
                }
                WinHttpCloseHandle(hRequest);
            }
            WinHttpCloseHandle(hConnect);
        }
        WinHttpCloseHandle(hSession);
    }
    return ok;
}

// ================================================================
// GLOBAL VARIABLES (defined here)
// ================================================================
uintptr_t GA = 0;
const IMAGE_NT_HEADERS* NtHeaders = nullptr;
uintptr_t Referee = 0;
uint64_t* RetAddr = nullptr;

// UnityEngine method RVAs
uintptr_t RR::Methods::UnityEngine::Camera_set_fieldOfView = 0;
uintptr_t RR::Methods::UnityEngine::Camera_get_fieldOfView = 0;
uintptr_t RR::Methods::UnityEngine::Graphic_set_color = 0;
uintptr_t RR::Methods::UnityEngine::CanvasRenderer_SetColor = 0;
uintptr_t RR::Methods::UnityEngine::Canvas_ForceUpdateCanvases = 0;
uintptr_t RR::Methods::UnityEngine::GameObject_Find = 0;
uintptr_t RR::Methods::UnityEngine::GameObject_get_transform = 0;
uintptr_t RR::Methods::UnityEngine::Transform_GetChild = 0;
uintptr_t RR::Methods::UnityEngine::Transform_SetParent = 0;
uintptr_t RR::Methods::UnityEngine::Transform_GetSiblingIndex = 0;
uintptr_t RR::Methods::UnityEngine::Transform_SetSiblingIndex = 0;
uintptr_t RR::Methods::UnityEngine::Object_Instantiate_GameObject = 0;
uintptr_t RR::Methods::UnityEngine::GameObject_SetActive = 0;
uintptr_t RR::Methods::UnityEngine::GameObject_get_name = 0;
uintptr_t RR::Methods::UnityEngine::UnityEvent_AddListener = 0;

// IL2CPP API functions
uintptr_t RR::Methods::Il2cpp::il2cpp_string_new = 0;
uintptr_t RR::Methods::Il2cpp::il2cpp_object_get_class = 0;
uintptr_t RR::Methods::Il2cpp::il2cpp_object_new = 0;
uintptr_t RR::Methods::Il2cpp::il2cpp_class_get_method_from_name = 0;
uintptr_t RR::Methods::Il2cpp::il2cpp_class_get_parent = 0;

// ================================================================
// INIT GLOBALS
// ================================================================
bool InitGlobals() {
    GA = (uintptr_t)GetModuleHandleA("GameAssembly.dll");
    Referee = (uintptr_t)GetModuleHandleA("Referee.dll");

    if (!GA) {
        printf("[Campus] init: GameAssembly.dll not loaded\n");
        return false;
    }
    if (!Referee) {
        printf("[Campus] init: Referee.dll not loaded\n");
        return false;
    }

    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(GA);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        printf("[Campus] init: GameAssembly has bad DOS header\n");
        return false;
    }

    NtHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(GA + dos->e_lfanew);
    if (NtHeaders->Signature != IMAGE_NT_SIGNATURE) {
        printf("[Campus] init: GameAssembly has bad NT header\n");
        NtHeaders = nullptr;
        return false;
    }

    printf("[Campus] init: GameAssembly=%p Referee=%p SizeOfImage=0x%X\n",
        (void*)GA, (void*)Referee, NtHeaders->OptionalHeader.SizeOfImage);
    return true;
}

// ================================================================
// D3D11 PRESENT HOOK  -  what actually puts the inspector on screen
// ================================================================
// Both halves of this used to be stubs: GetD3D11DeviceAndSwapchain was a TODO that
// only printed "not implemented", and InstallPresentHook had its MinHook calls
// commented out and read vtable[7] (which is GetDevice - Present is index 8). So
// ImGuiInspector::Initialize was never reached and Render() never ran. The inspector
// had never drawn a single frame.
//
// Approach: we do NOT need the game's swapchain to find Present. Every
// IDXGISwapChain shares one vtable, so we spin up a throwaway device+swapchain on a
// hidden window, read Present's address out of its vtable, throw it away, and hook
// that address. The game's own swapchain arrives as `this` on the first real call,
// and we initialise ImGui from it then - which also means we cannot race the game's
// renderer setup.

// (The sprite menu and its "Unity Explorer" toggle were removed: the dev panel's Avatar Fun
// tab is the only UI this build draws.)

static bool  g_imguiReady = false;      // ImGui initialised against the real swapchain
static HWND  g_imguiHwnd = nullptr;
static WNDPROC g_oWndProc = nullptr;

// ImGui's Win32 backend input handler.
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND, UINT, WPARAM, LPARAM);

// Input only goes to ImGui while the window is open, so gameplay is untouched when
// the inspector is hidden.
static LRESULT CALLBACK ImGuiWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (g_imguiReady && DevPanel::IsVisible()) {
        if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
            return 1;

        // Swallow mouse/keyboard the UI is using so clicks don't also reach the game.
        ImGuiIO& io = ImGui::GetIO();
        bool mouse = (msg >= WM_MOUSEFIRST && msg <= WM_MOUSELAST);
        bool key = (msg >= WM_KEYFIRST && msg <= WM_KEYLAST);
        if ((mouse && io.WantCaptureMouse) || (key && io.WantCaptureKeyboard))
            return 1;
    }
    return CallWindowProc(g_oWndProc, hWnd, msg, wParam, lParam);
}

typedef HRESULT(WINAPI* PresentFn)(IDXGISwapChain*, UINT, UINT);
static PresentFn oPresent = nullptr;

HRESULT WINAPI PresentHook(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags) {
    // First real frame: everything we need is reachable from the swapchain itself.
    if (!g_imguiReady && pSwapChain) {
        if (SUCCEEDED(pSwapChain->GetDevice(__uuidof(ID3D11Device), (void**)&g_pd3dDevice)) && g_pd3dDevice) {
            g_pd3dDevice->GetImmediateContext(&g_pd3dDeviceContext);

            DXGI_SWAP_CHAIN_DESC desc{};
            pSwapChain->GetDesc(&desc);
            g_imguiHwnd = desc.OutputWindow;
            g_pSwapChain = pSwapChain;

            if (g_pd3dDeviceContext && g_imguiHwnd) {
                ImGuiInspector::Initialize(g_imguiHwnd, g_pd3dDevice, g_pd3dDeviceContext);

                // Without this the window draws but never responds - no clicks, no
                // typing, no expanding tree nodes.
                g_oWndProc = (WNDPROC)SetWindowLongPtrW(g_imguiHwnd, GWLP_WNDPROC, (LONG_PTR)ImGuiWndProc);

                g_imguiReady = true;
                g_imguiInitialized = true;
                std::cout << "[ImGui] Inspector attached to the game's swapchain (hwnd 0x"
                    << std::hex << (uintptr_t)g_imguiHwnd << std::dec << "). Ctrl+Shift+I to toggle.\n";
            }
        }
        if (!g_imguiReady)
            std::cout << "[ImGui] Could not initialise from the swapchain.\n";
    }

    // Mouse detach: while any ImGui surface is up, free the cursor from the game so it
    // can click the panel, and let ImGui draw its own cursor. The game re-locks the
    // cursor every frame for mouselook, so this has to run every frame too. When all
    // surfaces are hidden, release the clip so the game takes the cursor back. INSERT
    // toggles the dev panel, so pressing it once frees the mouse, pressing again grabs.
    {
        static bool s_wasFree = false;
        bool wantFree = DevPanel::IsVisible();
        if (wantFree) {
            ClipCursor(nullptr);                       // undo the game's cursor clip
            while (ShowCursor(TRUE) < 0) {}            // force the cursor visible
            if (g_imguiReady) ImGui::GetIO().MouseDrawCursor = true;
            s_wasFree = true;
        }
        else if (s_wasFree) {
            while (ShowCursor(FALSE) >= 0) {}          // hand the cursor back to the game
            if (g_imguiReady) ImGui::GetIO().MouseDrawCursor = false;
            s_wasFree = false;
        }
    }

    if (g_imguiReady && DevPanel::IsVisible()) {
        // ImGui draws into whatever render target is bound. The game does not
        // guarantee the backbuffer is still bound at Present, so bind it ourselves.
        // Built per frame on purpose: it stays correct across resolution changes and
        // fullscreen toggles without hooking ResizeBuffers.
        ID3D11Texture2D* backBuffer = nullptr;
        if (SUCCEEDED(pSwapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBuffer)) && backBuffer) {
            ID3D11RenderTargetView* rtv = nullptr;
            if (SUCCEEDED(g_pd3dDevice->CreateRenderTargetView(backBuffer, nullptr, &rtv)) && rtv) {
                // Unity assumes the output bindings it set stay put, and the ImGui backend only restores the
                // states IT changes (not the render targets), so put the game's own targets back afterwards.
                ID3D11RenderTargetView* prevRtv[1] = { nullptr };
                ID3D11DepthStencilView* prevDsv = nullptr;
                g_pd3dDeviceContext->OMGetRenderTargets(1, prevRtv, &prevDsv);

                g_pd3dDeviceContext->OMSetRenderTargets(1, &rtv, nullptr);
                ImGuiInspector::Render();

                g_pd3dDeviceContext->OMSetRenderTargets(1, prevRtv, prevDsv);
                if (prevRtv[0]) prevRtv[0]->Release();
                if (prevDsv) prevDsv->Release();
                rtv->Release();
            }
            backBuffer->Release();
        }
    }

    if (LoadTurbo::Active()) SyncInterval = 0;     // room bundle downloading: no vsync (see LoadTurbo.h)
    return oPresent(pSwapChain, SyncInterval, Flags);
}

// The same, for normal (non-inspector) runs: ONLY the load-turbo part of PresentHook.
static HRESULT WINAPI TurboPresentHook(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags) {
    if (LoadTurbo::Active()) SyncInterval = 0;
    return oPresent(pSwapChain, SyncInterval, Flags);
}

// Create a throwaway device + swapchain purely to read Present out of the vtable.
// Returns nullptr if the game is not D3D11 (a DX12/Vulkan title will land here).
static void* FindPresentAddress() {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"CampusDummyWnd";
    if (!RegisterClassExW(&wc)) {
        if (GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return nullptr;
    }

    HWND wnd = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW,
        0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
    if (!wnd) { UnregisterClassW(wc.lpszClassName, wc.hInstance); return nullptr; }

    DXGI_SWAP_CHAIN_DESC sd{};
    sd.BufferCount = 1;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = wnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    IDXGISwapChain* sc = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };

    HRESULT hr = D3D11CreateDeviceAndSwapChain(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
        &sd, &sc, &dev, nullptr, &ctx);

    void* present = nullptr;
    if (SUCCEEDED(hr) && sc) {
        // IDXGISwapChain vtable: 0-2 IUnknown, 3-6 IDXGIObject, 7 GetDevice,
        // 8 Present. The old code took index 7 and would have hooked GetDevice.
        void** vtable = *(void***)sc;
        present = vtable[8];
    }
    else {
        std::cout << "[ImGui] D3D11CreateDeviceAndSwapChain failed (0x"
            << std::hex << hr << std::dec << ") - is this game D3D11?\n";
    }

    if (sc)  sc->Release();
    if (ctx) ctx->Release();
    if (dev) dev->Release();
    DestroyWindow(wnd);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);

    return present;
}

static void InstallTurboPresentHook() {
    void* present = FindPresentAddress();
    if (!present) { std::cout << "[LoadTurbo] could not locate IDXGISwapChain::Present - room loads keep vsync.\n"; return; }
    if (MH_CreateHook(present, (void*)TurboPresentHook, (LPVOID*)&oPresent) != MH_OK || MH_EnableHook(present) != MH_OK) {
        std::cout << "[LoadTurbo] Present hook failed - room loads keep vsync.\n";
        return;
    }
    std::cout << "[LoadTurbo] ready (vsync is lifted while a room bundle downloads)\n";
}

// Hook Present. MinHook is already initialised by RR::Patches (MH_Initialize).
static void InstallPresentHook() {
    void* present = FindPresentAddress();
    if (!present) {
        std::cout << "[ImGui] Could not locate IDXGISwapChain::Present - inspector will not draw.\n";
        return;
    }

    std::cout << "[ImGui] IDXGISwapChain::Present at 0x"
        << std::hex << (uintptr_t)present << std::dec << "\n";

    if (MH_CreateHook(present, (void*)PresentHook, (LPVOID*)&oPresent) != MH_OK) {
        std::cout << "[ImGui] MH_CreateHook(Present) failed.\n";
        return;
    }
    if (MH_EnableHook(present) != MH_OK) {
        std::cout << "[ImGui] MH_EnableHook(Present) failed.\n";
        return;
    }
    std::cout << "[ImGui] Present hook installed - inspector renders from the next frame.\n";
}

// ================================================================
// INIT THREAD
// ================================================================
static DWORD WINAPI InitThread(LPVOID)
{
    for (int i = 0; i < 200; i++) {
        if (GetModuleHandleA("GameAssembly.dll") && GetModuleHandleA("Referee.dll")) break;
        Sleep(100);
    }

#ifdef CAMPUS_DEV_BUILD
    std::cout << TAG_INIT() << CLR_YELLOW << "[DEV BUILD] connects to " << CAMPUS_NAMESERVER_HOST << " / Photon " << CAMPUS_PHOTON_SERVER
              << " - anti-cheat and patch verification are OFF\n" << CLR_RESET;
#else
    if (!VerifyPatch()) {
        std::cout << TAG_INIT() << CLR_RED << "[patch] server rejected this patch - closing.\n" << CLR_RESET;
        Sleep(2500);
        ExitProcess(0);
    }
#endif
    // the login gate: prove this is the current patch now (and again right before each login)
    std::thread([] { PatchProof::Send(); }).detach();

    if (!InitGlobals()) {
        printf("[Campus] init failed - aborting (no hooks installed)\n");
        return 1;
    }

#ifndef NDEBUG
    CampusLogging::Init(g_selfModule);
#endif

    ULONGLONG t0 = GetTickCount64();
    RR::Patches::Resolve();
    ULONGLONG t1 = GetTickCount64();
    RR::Patches::Patch();
#ifdef CAMPUS_DEV_PANEL
    AvatarFun::Install();   // main-thread hook the Avatar Fun panel relies on
#endif
    ULONGLONG t2 = GetTickCount64();

    // ---- deferred, metadata-dependent setup -------------------------------------
    // Everything above only needs GameAssembly.dll to be LOADED. Anything that asks
    // the IL2CPP RUNTIME a question (class lookups, the assembly sweep, the
    // cheat-quit hook) has to wait for il2cpp_init to finish - the loop at the top of
    // this function does not wait for that, it only waits for the module.
    //
    // Running metadata work at inject time returned null for everything and crashed
    // the game a few seconds in. This thread waits for a real runtime, then does it.
    // Gated. This thread enters the IL2CPP runtime and patches a game method, and
    // running it unconditionally crashed the game on startup - so it is opt-in until it
    // is proven on a real session. Without it the client can still self-quit after a
    // Hile trip, which is the pre-existing bug this was meant to fix.
    // Debug-only worker: resolves the Unity method table (inspector + Ctrl+Shift+U
    // dumper) from il2cpp metadata once the runtime is up. The cheat-quit suppression
    // is NOT here any more - it hooks by GA+RVA in Patch(), like every other hook, so
    // it needs no runtime worker and carries no startup-timing risk.
    // (The debug worker that resolved the Unity method table for the Unity Explorer / UI dumper
    // was removed together with them: the dev panel's Avatar Fun calls fixed RVAs and needs no
    // metadata lookups, so nothing enters the IL2CPP runtime from a background thread any more.)

    std::cout << "\n";
    std::cout << "\t[////////////////////////////////////////////////////////////////]\n";
    std::cout << "\t                      I miss Vanessa so badly\n";
    std::cout << "\t[////////////////////////////////////////////////////////////////]\n";
    std::cout << "\n";
    std::cout << TAG_INIT() << "Target Server: " << CLR_YELLOW << "camprec.net" << CLR_RESET << "\n";
    std::cout << TAG_INIT() << "Build Date:   " << __DATE__ << " " << __TIME__ << "\n";
    std::cout << TAG_INIT() << "Patches:      Referee [OK]  TLS [OK]  HTTP [OK]  UI [OK]\n";
    std::cout << TAG_INIT() << "Radio:        DISABLED\n";
    std::cout << TAG_INIT() << "Timing:       Resolve " << (t1 - t0) << "ms, Patch " << (t2 - t1) << "ms, Total " << (t2 - t0) << "ms\n";
    std::cout << "\n";
    std::cout << TAG_INIT() << "[+] Patches applied successfully!\n";
    std::cout << "\n";

    TestServerConnection();

    // ================================================================
    // INITIALIZE IMGUI INSPECTOR
    // ================================================================
#ifdef CAMPUS_DEV_PANEL
    // Install the Present hook only, and only when the debug tools are switched on.
    // This replaces the swapchain's Present and the window's WndProc - by far the most
    // invasive thing this DLL does, and not something to impose on a normal session.
    //
    // ImGui itself is initialised on the first real frame, from inside the hook, using
    // the swapchain the game hands us.
    //
    // The old code tried to do it the other way round - fetch the device up front and
    // only then hook - which cannot work here: this runs during injection, long before
    // the game's renderer exists, so the device was always null and Initialize() was
    // never called. That is why no window ever appeared.
    if (DebugTools::Enabled()) InstallPresentHook(); else InstallTurboPresentHook();
#endif

    std::cout << TAG_INIT() << "Starting anti-cheat starter thread...\n";
    HANDLE hThread = CreateThread(nullptr, 0, AntiCheatStarterThread, nullptr, 0, nullptr);
    if (hThread) CloseHandle(hThread);

    std::cout << TAG_INIT() << "Starting key listener thread...\n";
    g_keyThread = CreateThread(nullptr, 0, KeyListenerThread, nullptr, 0, nullptr);
    if (g_keyThread) CloseHandle(g_keyThread);

    return 0;
}

// ================================================================
// CONSOLE SETUP
// ================================================================
static void CreateConsole()
{
    SetEnvironmentVariableA("STEAMVR_ENABLED", "0");
    SetEnvironmentVariableA("OPENVR_ENABLED", "0");
    SetEnvironmentVariableA("DISABLE_VR", "1");
    SetEnvironmentVariableA("VR_DISABLED", "1");

#ifndef NDEBUG
    AllocConsole();

    HANDLE hConsole = GetStdHandle(STD_OUTPUT_HANDLE);
    if (hConsole != INVALID_HANDLE_VALUE && hConsole != nullptr) {
        COORD bufferSize = { 200, 9999 };
        SetConsoleScreenBufferSize(hConsole, bufferSize);
        SMALL_RECT windowSize = { 0, 0, 190, 50 };
        SetConsoleWindowInfo(hConsole, TRUE, &windowSize);
    }

    FILE* fOut = nullptr;
    FILE* fErr = nullptr;
    freopen_s(&fOut, "CONOUT$", "w", stdout);
    freopen_s(&fErr, "CONOUT$", "w", stderr);
    setvbuf(stdout, nullptr, _IONBF, 0);
    setvbuf(stderr, nullptr, _IONBF, 0);

    SetConsoleTitleA("Vanessa");

    HWND hWnd = GetConsoleWindow();
    if (hWnd) {
        __try {
            HMONITOR hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi;
            mi.cbSize = sizeof(MONITORINFO);
            if (GetMonitorInfoA(hMonitor, &mi)) {
                SetWindowPos(hWnd, HWND_TOPMOST,
                    mi.rcWork.left, mi.rcWork.top,
                    mi.rcWork.right - mi.rcWork.left,
                    mi.rcWork.bottom - mi.rcWork.top,
                    SWP_SHOWWINDOW);
            }
            else {
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 1400, 900, SWP_SHOWWINDOW);
            }

            LONG_PTR style = GetWindowLongPtr(hWnd, GWL_STYLE);
            style = WS_OVERLAPPEDWINDOW | WS_CAPTION | WS_SYSMENU |
                WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME;
            SetWindowLongPtr(hWnd, GWL_STYLE, style);

            SetWindowPos(hWnd, NULL, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            ShowWindow(hWnd, SW_MINIMIZE);
            g_consoleMinimized = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    if (hConsole != INVALID_HANDLE_VALUE && hConsole != nullptr) {
        DWORD mode = 0;
        if (GetConsoleMode(hConsole, &mode)) {
            SetConsoleMode(hConsole, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
    }

    printf("\x1b[1m\x1b[38;5;120m[CampusPatch]\x1b[0m loaded - target: "
        "\x1b[38;5;227mcamprec.net\x1b[0m  "
        "\x1b[38;5;240m(build " __DATE__ " " __TIME__ ")\x1b[0m\n");
#else
    std::cout << "[CampusPatch] loaded - target: camprec.net (build " __DATE__ " " __TIME__ ")\n";
#endif
}

// ================================================================
// CLEANUP
// ================================================================
static void CleanupThreads()
{
    g_keyListenerRunning = false;

    if (g_keyThread) {
        WaitForSingleObject(g_keyThread, 2000);
        CloseHandle(g_keyThread);
        g_keyThread = nullptr;
    }
}

// ================================================================
// DLL MAIN
// ================================================================
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        InitializeCriticalSection(&g_windowCS);
        DisableThreadLibraryCalls(hModule);
        g_selfModule = hModule;
        InstallCrashHandler(hModule);
        CreateConsole();
        HANDLE h = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        if (h) CloseHandle(h);
    }
    else if (ul_reason_for_call == DLL_PROCESS_DETACH) {
        if (g_antiCheatStarted) {
            CleanupThreads();
            Protections::Shutdown();
            DiscordRPC::Shutdown();
#ifndef NDEBUG
            CampusLogging::Shutdown();
#endif
        }
        // Shutdown ImGui Inspector
        ImGuiInspector::Shutdown();
        DeleteCriticalSection(&g_windowCS);
        FreeConsole();
    }
    return TRUE;
}