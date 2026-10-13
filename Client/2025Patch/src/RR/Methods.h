#pragma once
#include "../Utils/globals.h"
#include <vector>
#include <cstdint>

// ================================================================
// REFEREE CHECKS – constants
// ================================================================
namespace RR::Methods::Referee {
    inline constexpr uintptr_t Check1 = 0x76E850;
    inline constexpr uintptr_t Check2 = 0x2E6F680;
    inline constexpr uintptr_t Check3 = 0x2E6F670;
    inline constexpr uintptr_t Check4 = 0x2E6F6E0;
}

// TLS certificate validation bypass
namespace RR::Methods::LegacyTlsAuthentication {
    inline constexpr uintptr_t NotifyServerCertificate = 0x77E5E30;
}

// HTTP request handler - redirects ns.rec.net to ns.camprec.net
namespace RR::Methods::HTTPManager {
    inline constexpr uintptr_t SendRequest = 0x77E0950;
}

// BestHTTP.HTTPRequest: nothing is resolved here any more. The X-Campus-Patch
// stamping that used SetHeader was removed from both patches - see
// Patching/SendRequest.h.


// HTTP response data accessors
namespace RR::Methods::HttpResponse {
    inline constexpr uintptr_t get_DataAsText = 0x77F4F80;
    inline constexpr uintptr_t Receive = 0x77F42D0;
}

// System.Uri methods for URL manipulation
namespace RR::Methods::System::Uri {
    inline constexpr uintptr_t ToString = 0x935AF50;
    inline constexpr uintptr_t ctor = 0x935C2A0;
}

// IL2CPP API functions - resolved at runtime by EXPORT NAME (GetProcAddress on
// GameAssembly.dll, with a UnityPlayer scan fallback). Export names are stable across
// every Unity build and every platform, which is why nothing here is an offset.
//
// class_get_method_from_name + class_get_parent are what let us look up game methods
// (currently HTTPRequest.SetHeader) out of the runtime's own metadata instead of a
// hardcoded RVA.
namespace RR::Methods::Il2cpp {
    extern uintptr_t il2cpp_string_new;
    extern uintptr_t il2cpp_object_get_class;
    extern uintptr_t il2cpp_object_new;
    extern uintptr_t il2cpp_class_get_method_from_name;
    extern uintptr_t il2cpp_class_get_parent;
}

// Unity Gradient methods - constants
namespace RR::Methods::UnityGradient {
    inline constexpr uintptr_t SetKeys = 0x9987030;
    inline constexpr uintptr_t get_colorKeys = 0x9987110;
    inline constexpr uintptr_t get_alphaKeys = 0x99870D0;
}

// UIGradient setter - hooked to recolor UI to Campus theme
namespace RR::Methods::UIGradient {
    inline constexpr uintptr_t set_Gradient = 0x8BFA6A0;
}

// RecNet.Images - Image loading system from dump
namespace RR::Methods::RecNetImages {
    inline constexpr uintptr_t LoadByName = 0x761D850;
    inline constexpr uintptr_t LoadRoom = 0x761D8E0;
    inline constexpr uintptr_t LoadInvention = 0x761D360;
    inline constexpr uintptr_t LoadPlayerEvent = 0x761D700;
    inline constexpr uintptr_t LoadAccount = 0x761D5C0;
    inline constexpr uintptr_t LoadAnnouncement = 0x761DB80;
    inline constexpr uintptr_t LoadBytes = 0x761D020;
    inline constexpr uintptr_t StaticConstructor = 0x761DDD0;

    inline constexpr uintptr_t OHAPJOGMJPD_MoveNext = 0x761F400;
    inline constexpr uintptr_t NFHOMKFHHFJ_MoveNext = 0x761ECB0;
    inline constexpr uintptr_t HGKCHKDFPIK_MoveNext = 0x761E410;
    inline constexpr uintptr_t KCJFMDKJCJJ_MoveNext = 0x761E7B0;
    inline constexpr uintptr_t GMMBKKHKLFK_MoveNext = 0x761E060;
    inline constexpr uintptr_t NKJKLKMGNBL_MoveNext = 0x761F050;
    inline constexpr uintptr_t FILCOLNCBFO_MoveNext = 0x761C7B0;
}

// IMAGE SIGNATURE - constant RVA
namespace RR::Methods::ImageSignature {
    inline constexpr uintptr_t Verify = 0x7CF32F0;
}

// STEAMVR FOV - constant RVA (kept for reference)
namespace RR::Methods::SteamVR {
    inline constexpr uintptr_t SetFieldOfView = 0xA906F0;
}

// ================================================================
// APPNav SYSTEM RVAs – from the provided dump
// ================================================================
namespace RR::Methods::AppNav {
    inline constexpr uintptr_t GetInstance = 0x7F21D10;
    inline constexpr uintptr_t OpenPage = 0x7F21540;
    inline constexpr uintptr_t OpenMenu = 0x7F21490;
    inline constexpr uintptr_t CloseMenu = 0x7F21110;
}

namespace RR::Methods::AppNavViewModel {
    inline constexpr uintptr_t get_WidgetPanelIdentifiers = 0xA6EF90;
    inline constexpr uintptr_t set_WidgetPanelIdentifiers = 0x7F27060;
    inline constexpr uintptr_t get_ActivePanelIdentifier = 0xA63200;
    inline constexpr uintptr_t set_ActivePanelIdentifier = 0x7F269A0;
}

namespace RR::Methods::AppNavPanelSwitcher {
    inline constexpr uintptr_t RegisterPanel = 0x7F22D10;
}

namespace RR::Methods::AppNavPanel {
    inline constexpr uintptr_t get_Identifier = 0xA63FC0;
    inline constexpr uintptr_t set_Identifier = 0xA63FA0;
}

// ================================================================
// UNITY ENGINE FUNCTIONS – resolved at runtime (defined in main.cpp)
// ================================================================
namespace RR::Methods::UnityEngine {
    extern uintptr_t Camera_set_fieldOfView;
    extern uintptr_t Camera_get_fieldOfView;
    extern uintptr_t Graphic_set_color;
    extern uintptr_t CanvasRenderer_SetColor;
    extern uintptr_t Canvas_ForceUpdateCanvases;
    extern uintptr_t GameObject_Find;
    extern uintptr_t GameObject_get_transform;
    extern uintptr_t Transform_GetChild;
    extern uintptr_t Transform_SetParent;
    extern uintptr_t Transform_GetSiblingIndex;
    extern uintptr_t Transform_SetSiblingIndex;
    extern uintptr_t Object_Instantiate_GameObject;
    extern uintptr_t GameObject_SetActive;
    extern uintptr_t GameObject_get_name;
    extern uintptr_t UnityEvent_AddListener;
    extern uintptr_t SettingsMenuController_OnEnable;
}

// ================================================================
// PHOTON BACKEND SELECTION – constants
// ================================================================
namespace RR::Offsets::AppSettings {
    inline constexpr int AppIdRealtime = 16;
    inline constexpr int AppIdChat = 24;
    inline constexpr int AppIdVoice = 32;
    inline constexpr int AppVersion = 40;
    inline constexpr int UseNameServer = 48;
    inline constexpr int FixedRegion = 56;
    inline constexpr int Server = 72;
    inline constexpr int Port = 80;
}

namespace RR::Methods::Photon {
    inline constexpr uintptr_t ConnectUsingSettings = 0x757E8C0;
    inline constexpr uintptr_t SendOperation = 0x7534610;
    inline constexpr uintptr_t EnqueueStatusCallback = 0x752C540;
    inline constexpr uintptr_t Protocol16_DeserializeOperationResponse = 0x75388D0;
    inline constexpr uintptr_t Protocol18_DeserializeOperationResponse = 0x753E610;
    inline constexpr uintptr_t Protocol16_DeserializeEventData = 0x7537E30;
    inline constexpr uintptr_t Protocol18_DeserializeEventData = 0x753E2F0;
    inline constexpr uintptr_t EnetPeer_IsTransportEncrypted = 0x7521380;
}

enum class HHFAEMGHOHN : int {
    Obscured = 0,
    Time = 1,
    Inject = 2,
    GiftCount = 3,
    Engine = 4,
    UnknownDll = 5,
    ImageSignature = 6,
    AvatarHack = 7,
    NetworkCertificatePublicKey = 100,
    NetworkCertificateIssuer = 101,
    NetworkCertificateMissing = 102,
    NetworkCertificateMismatch = 103,
    AutosaveChecksumMismatch = 150,
    AutosaveSubRoomIdMismatch = 151,
    AutosaveChecksumException = 152,
    Photon_MissingHash = 200,
    Photon_CorruptHash = 201,
    Photon_DifferentHash = 203,
    Photon_ChatPuppeteering = 204,
    AppData_Runtime_LengthMismatch = 300,
    AppData_Runtime_LastWriteTimeMismatch = 301,
    AppData_Runtime_FileModified = 302,
    AppData_Boot_InvalidSignature = 310,
    AppData_Boot_UnableToVerifySignatures = 311,
    Config_MissingHash = 320,
    Config_DifferentHash = 321,
    Photon_InstantiateTool = 400,
    Memory_Hash_Mismatch = 500,
    Driver_Invalid_Signature = 600,
    Native_Memory_Hash_Mismatch = 700,
    Native_Numbers_Mismatch = 701,
    Native_Requirements_Failed = 702,
    Referee_TelemetryEvents = 800,
};