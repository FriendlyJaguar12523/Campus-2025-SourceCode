#pragma once
// Processes that legitimately run alongside the game and happen to contain substrings
// that the AntiDebug / AntiKill blacklists would otherwise match - so the scanner has
// to skip them, or players running an Oculus / SteamVR headset get kicked.
//
//  - OculusDebugTool.exe       contains "debug"  (ships with the Oculus runtime; needed for VR debug / performance HUD)
//  - OVRServer_x64.exe         Oculus runtime server
//  - OVRRedir.exe              Oculus runtime launcher redirector
//  - vrserver.exe              SteamVR
//  - nvcontainer.exe           NVIDIA (contains "ida")
//  - nvidia web helper.exe     NVIDIA
//
// Match is case-insensitive on the process's full exe name. Add rows here before
// relaxing the blacklist itself - exact names are much safer than substrings.

#include <string>
#include <algorithm>
#include <set>

namespace Protections {

    inline const std::set<std::string>& ProcessAllowlist() {
        static const std::set<std::string> kAllowed = {
            // ---- Oculus / Meta ----
            "oculusdebugtool.exe",
            "ovrserver_x64.exe",
            "ovrserviceengine.exe",
            "ovrredir.exe",
            "ovrservicelauncher.exe",
            "oculusclient.exe",
            "oculusdash.exe",
            "ovrairlinkservice.exe",
            "oculus-platform-runtime.exe",
            // ---- SteamVR ----
            "vrserver.exe",
            "vrmonitor.exe",
            "vrcompositor.exe",
            "vrstartup.exe",
            "vrdashboard.exe",
            "vrwebhelper.exe",
            // ---- Virtual Desktop / ALVR / wireless VR ----
            "virtualdesktop.streamer.exe",
            "alvr_streamer.exe",
            // ---- NVIDIA (processes contain "ida" and would substring-match "ida") ----
            "nvcontainer.exe",
            "nvdisplay.container.exe",
            "nvidia web helper.exe",
            "nvidia share.exe",
            "nvbroadcast.container.exe",
            // ---- AMD ----
            "amddvr.exe",
            "amdrsserv.exe",
            // ---- Windows services that can contain "debug" ----
            "svchost.exe",
            "wudfhost.exe",
        };
        return kAllowed;
    }

    inline bool IsAllowedProcess(const std::string& processNameLower) {
        return ProcessAllowlist().find(processNameLower) != ProcessAllowlist().end();
    }

}
