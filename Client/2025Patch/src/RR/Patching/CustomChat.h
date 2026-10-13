#pragma once

#include <windows.h>
#include <cstdint>
#include <iostream>
#include "../Methods.h"
#include "Console.h"
#include "../../Utils/globals.h"

// ================================================================================
// Send a chat message as the local player
//
// Types straight into room chat, so everyone in YOUR room sees it - chat is already
// room-scoped, so this is the natural "same room" broadcast.
//
// RoomChatExpandedMenu.SendChatMessage(string) runs the normal send pipeline, so the
// message goes out exactly as if you'd typed it in the chat box. It's an instance
// method, so we capture the menu the first time its Initialize() runs (hooked below).
//
// RVAs (runtime dump, build 20250718.01):
//   RecRoom.Core.UI.RoomChat.RoomChatExpandedMenu.Initialize(model, kind)   0x1A0D4C0
//   RecRoom.Core.UI.RoomChat.RoomChatExpandedMenu.SendChatMessage(string)   0x1A0F0C0
//
// Same safety rules as CustomNotify: only called after capture, SEH-guarded, GA+RVA
// (no metadata / runtime worker).
// ================================================================================

namespace CustomChat {

    inline constexpr uintptr_t InitRVA = 0x1A0D4C0;
    inline constexpr uintptr_t SendRVA = 0x1A0F0C0;

    inline void* g_chatMenu = nullptr;

    // Initialize(this, CannedChatMenuModel*, enum kind, MethodInfo*)
    using InitFn = void(*)(void* self, void* model, int32_t kind, void* mi);
    inline InitFn Init_O = nullptr;

    // SendChatMessage(this, string text, MethodInfo*)
    using SendFn = void(*)(void* self, void* text, void* mi);

    inline void Init_H(void* self, void* model, int32_t kind, void* mi) {
        if (self && !g_chatMenu) {
            g_chatMenu = self;
            std::cout << TAG_INIT() << "RoomChatExpandedMenu captured @ 0x"
                << std::hex << (uintptr_t)self << std::dec << " - chat send ready\n";
        }
        if (Init_O) Init_O(self, model, kind, mi);
    }

    // Destructor-free SEH half (C2712-safe). Returns false on any fault.
    inline bool RawSend(const char* text) {
        __try {
            using StrNewFn = void* (*)(const char*);
            auto strNew = reinterpret_cast<StrNewFn>(GA + RR::Methods::Il2cpp::il2cpp_string_new);
            void* managed = strNew(text);
            if (!managed) return false;

            auto send = reinterpret_cast<SendFn>(GA + SendRVA);
            send(g_chatMenu, managed, nullptr);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // Send a chat message. No-ops (with a log) until the chat menu is captured - i.e.
    // until you've opened chat at least once this session, which is when Initialize runs.
    inline bool Send(const char* text) {
        if (!g_chatMenu) {
            std::cout << TAG_WARN() << "chat send skipped - chat menu not captured yet "
                         "(open the chat panel in-game once first)\n";
            return false;
        }
        if (!RR::Methods::Il2cpp::il2cpp_string_new) {
            std::cout << TAG_WARN() << "chat send skipped - il2cpp_string_new unresolved\n";
            return false;
        }
        if (!text || !*text) return false;

        bool ok = RawSend(text);
        if (ok) std::cout << TAG_INIT() << "chat sent: \"" << text << "\"\n";
        else    std::cout << TAG_WARN() << "chat send faulted - suppressed\n";
        return ok;
    }
}
