#pragma once

#include "../imgui/imgui.h"
#include "../Patching/CustomNotify.h"
#include "../Patching/CustomChat.h"
#include "../Patching/CustomSpawn.h"
#include "../Patching/AvatarFun.h"
#include "../../Utils/globals.h"
#include <cstring>
#include <string>

// ================================================================================
// Dev panel  (DEBUG DLL ONLY - gated behind campus_debug.txt via DebugTools)
//
// A small ImGui window. It now has ONE tab, Avatar Fun (the notification / chat / spawn / players tabs
// were taken out of the window on request; their code below is unused and can be re-added to Render()).
//
//   - Notification: type a message, show it as the game's own toast on this client.
//   - Chat: type a message, send it into room chat - everyone in YOUR room sees it,
//     because chat is room-scoped by nature.
//
// Rendering is folded into the inspector's existing ImGui frame (imgui_inspector.cpp),
// so there is no second Present hook.
// ================================================================================

namespace DevPanel {

    // Hidden until INSERT is pressed (main.cpp key listener). The console
    // minimize key is the only key left (see main.cpp).
    inline bool g_visible = false;

    inline void ToggleVisibility() { g_visible = !g_visible; }
    inline bool IsVisible() { return g_visible; }

    // Persist the controls across frames.
    inline char  g_msg[256]   = "Welcome to Campus!";
    inline int   g_priority   = 2;      // 0 Minor 1 Medium 2 Major 3 Vital
    inline float g_duration   = 5.0f;
    inline char  g_chat[256]  = "";
    inline char  g_prefab[128] = "Whip";
    inline float g_scale      = 1.0f;
    inline float g_bodyScale  = 1.0f;   // Avatar fun
    inline float g_headScale  = 1.0f;
    inline std::string g_lastResult;  // status line

    inline void ReadyDot(bool ready, const char* readyText, const char* notText) {
        if (ready) ImGui::TextColored(ImVec4(.5f, 1, .5f, 1), "%s", readyText);
        else       ImGui::TextColored(ImVec4(1, .7f, .3f, 1), "%s", notText);
    }

    inline void TabNotifyChat() {
        // ---- notification (this client) ----
        ImGui::SeparatorText("Notification (you)");
        ImGui::InputText("message", g_msg, sizeof(g_msg));
        const char* prio[] = { "Minor", "Medium", "Major", "Vital" };
        ImGui::Combo("priority", &g_priority, prio, IM_ARRAYSIZE(prio));
        ImGui::SliderFloat("duration (s)", &g_duration, 1.0f, 15.0f, "%.1f");
        if (ImGui::Button("Show notification")) {
            bool ok = CustomNotify::Show(g_msg, (CustomNotify::Priority)g_priority, g_duration);
            g_lastResult = ok ? "notification shown" : "not shown (manager not ready?)";
        }
        ImGui::SameLine();
        ReadyDot(CustomNotify::g_manager != nullptr, "ready", "not captured yet");

        // ---- chat (everyone in your room) ----
        ImGui::SeparatorText("Chat (everyone in your room)");
        bool enter = ImGui::InputText("chat", g_chat, sizeof(g_chat),
                                      ImGuiInputTextFlags_EnterReturnsTrue);
        ImGui::SameLine();
        bool click = ImGui::Button("Send");
        if ((enter || click) && g_chat[0]) {
            bool ok = CustomChat::Send(g_chat);
            g_lastResult = ok ? "chat sent" : "chat not sent (open chat in-game once?)";
            if (ok) g_chat[0] = '\0';
        }
        ReadyDot(CustomChat::g_chatMenu != nullptr, "chat ready", "open chat in-game once");
    }

    inline void TabAssetSpawn() {
        ImGui::SeparatorText("Spawn a Tool by prefab name");
        ImGui::TextWrapped("Prefab names, e.g. Whip, PaintballShotgun, "
                           "Quest_Dracula1_Blunderbuss.");
        bool enter = ImGui::InputText("prefab", g_prefab, sizeof(g_prefab),
                                      ImGuiInputTextFlags_EnterReturnsTrue);
        ImGui::SliderFloat("scale", &g_scale, 0.1f, 5.0f, "%.2f");
        ImGui::SameLine();
        bool click = ImGui::Button("Spawn");
        if ((enter || click) && g_prefab[0]) {
            bool ok = CustomSpawn::Spawn(g_prefab, g_scale);
            g_lastResult = ok ? "spawned" : "spawn faulted (bad prefab name?)";
        }
        ImGui::TextDisabled("Spawns near the world origin for now - positioning on/in "
                            "front of you is a follow-up.");
    }

    inline void TabAvatarFun() {
        ImGui::SeparatorText("Resize yourself");
        ImGui::TextWrapped("The panel only records what you ask for; a hook on the game's own per-frame Player.Update "
                           "(main thread) applies it to your local player and reads the size back. "
                           "Visual first: whether other players see it depends on the game syncing the value.");

        const bool hooked = AvatarFun::g_hooked;
        const bool live   = AvatarFun::g_havePlayer.load();
        if (!hooked)    ReadyDot(false, "", "hook not installed (see the log)");
        else if (!live) ReadyDot(false, "", "hook installed - waiting for your player (join a room)");
        else            ReadyDot(true, "local player found", "");

        ImGui::SliderFloat("body scale", &g_bodyScale, 0.1f, 5.0f, "%.2f");
        ImGui::SameLine();
        if (ImGui::Button("Apply##body")) { AvatarFun::RequestBody(g_bodyScale); g_lastResult = "body scale requested"; }

        ImGui::SliderFloat("head scale", &g_headScale, 0.1f, 5.0f, "%.2f");
        ImGui::SameLine();
        if (ImGui::Button("Apply##head")) { AvatarFun::RequestHead(g_headScale); g_lastResult = "head scale requested"; }

        static bool s_hold = false;
        if (ImGui::Checkbox("keep applying every frame", &s_hold)) AvatarFun::g_hold.store(s_hold);

        if (ImGui::Button("Reset to normal")) {
            g_bodyScale = g_headScale = 1.0f;
            AvatarFun::RequestReset();
            g_lastResult = "reset requested";
        }
        ImGui::SameLine();
        ImGui::TextDisabled("game body scale: %.2f   (frames seen: %llu)",
                            AvatarFun::g_seenBody.load(), (unsigned long long)AvatarFun::g_frames.load());
        ImGui::TextDisabled("hook fired (any player): %llu   local: %llu   is-local faults: %u",
                            (unsigned long long)AvatarFun::g_anyFrames.load(), (unsigned long long)AvatarFun::g_frames.load(),
                            AvatarFun::g_isLocalFault.load());
        ImGui::TextDisabled("last head scale the game was given: %.3f (%u calls)    last body scale set: %.3f (%u calls)",
                            AvatarFun::g_lastHeadSeen.load(), AvatarFun::g_headCalls.load(),
                            AvatarFun::g_lastBodySetSeen.load(), AvatarFun::g_bodySetCalls.load());
        ImGui::TextDisabled("calls made - body: %u ok / %u failed    head: %u ok / %u failed",
                            AvatarFun::g_bodyOk.load(), AvatarFun::g_bodyFail.load(),
                            AvatarFun::g_headOk.load(), AvatarFun::g_headFail.load());
        ImGui::TextWrapped("In first person the game hides your own head from you, so a head change can only be seen "
                           "from outside: a mirror, a photo camera, or another player. Body scale you can see yourself "
                           "(your eye height moves).");
    }

    inline void TabPlayers() {
        ImGui::SeparatorText("Players in room");
        ImGui::TextWrapped("Not wired yet - the room player list needs the game's player "
                           "manager, which isn't cleanly located in the dump. Coming next.");
    }

    inline void Render() {
        if (!g_visible) return;

        ImGui::SetNextWindowSize(ImVec2(460, 340), ImGuiCond_FirstUseEver);
        ImGui::Begin("Campus - Avatar Fun", &g_visible);

        if (ImGui::BeginTabBar("devtabs")) {
            if (ImGui::BeginTabItem("Avatar Fun"))    { TabAvatarFun();  ImGui::EndTabItem(); }
            ImGui::EndTabBar();
        }

        if (!g_lastResult.empty()) {
            ImGui::Separator();
            ImGui::TextDisabled("%s", g_lastResult.c_str());
        }

        ImGui::End();
    }
}
