#pragma once
#include <windows.h>
#include <string>

// The launcher's "Dev" release channel starts the game with CAMPUS_NS_HOST ("localhost:44334": the developer's own server) and
// CAMPUS_PHOTON_HOST ("127.0.0.1"). Unset (every normal launch) = the live Campus hosts below. The value is read once, when the DLL loads.
inline const char* CampusEnvOr(const char* name, const char* fallback)
{
    static std::string keep[4]; static int n = 0;       // the returned pointers must outlive the call
    char buf[260];
    DWORD got = GetEnvironmentVariableA(name, buf, (DWORD)sizeof(buf));
    if (got == 0 || got >= sizeof(buf) || n >= 4) return fallback;
    keep[n] = std::string(buf, got);
    return keep[n++].c_str();
}
// "host" or "host:port" -> the WinHttp pieces (443 when there is no port)
inline std::wstring CampusWiden(const char* s) { return std::wstring(s, s + strlen(s)); }
inline std::wstring CampusHostW(const std::wstring& hp) { size_t c = hp.rfind(L':'); return c == std::wstring::npos ? hp : hp.substr(0, c); }
inline unsigned short CampusPortOf(const std::wstring& hp) { size_t c = hp.rfind(L':'); return c == std::wstring::npos ? 443 : (unsigned short)_wtoi(hp.c_str() + c + 1); }

// Campus Server Configuration
// ns.camprec.net answers the name-server request (the client's ns.rec.net is rewritten to it); the server then hands out api.camprec.net
// as every service address. The patch's own calls (verify / proof / theme / presence) go straight to the API host.
// Dev channel: both are the developer's own server instead.
#ifdef CAMPUS_DEV_BUILD
// The "Dev" build configuration (x64\Dev\2025Patch.dll, pushed to the dev channel by PushChannel.bat): wired to the developer's OWN servers -
// the Campus server on https://localhost:44334 and CampusNet on 127.0.0.1 - whatever the launcher passes. (CAMPUS_NS_HOST / CAMPUS_PHOTON_HOST can still
// move it, e.g. to another port.) Its anti-cheat is off too (main.cpp).
static const char* CAMPUS_NAMESERVER_HOST = CampusEnvOr("CAMPUS_NS_HOST", "localhost:44334");
static const char* CAMPUS_API_HOST = CampusEnvOr("CAMPUS_NS_HOST", "localhost:44334");
#else
static const char* CAMPUS_NAMESERVER_HOST = CampusEnvOr("CAMPUS_NS_HOST", "ns.camprec.net");
static const char* CAMPUS_API_HOST = CampusEnvOr("CAMPUS_NS_HOST", "api.camprec.net");
#endif
static const char* CAMPUS_APP_VERSION = "20250719_prod";

// CampusNet - the self-hosted Photon-protocol game server. Every Photon Cloud host name the
// client resolves is pointed here (PhotonRedirect.h) so PC and Quest players share one network.
// Blank = Photon Cloud.
#ifdef CAMPUS_DEV_BUILD
static const char* CAMPUS_PHOTON_SERVER = CampusEnvOr("CAMPUS_PHOTON_HOST", "127.0.0.1");
#else
static const char* CAMPUS_PHOTON_SERVER = CampusEnvOr("CAMPUS_PHOTON_HOST", "50.114.179.17");
#endif
