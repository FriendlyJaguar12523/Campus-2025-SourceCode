#pragma once

#include <windows.h>
#include <cstdint>
#include <iostream>
#include "../Methods.h"
#include "Console.h"
#include "../../Utils/globals.h"

// ================================================================================
// Spawn a Tool/gadget by prefab name
//
// Tool.PhotonInstantiate(string prefabName, Vector3 pos, Quaternion rot, float scale)
// is Rec Room's networked spawn-by-name. Prefab names are what you'd type: "Whip",
// "PaintballShotgun", "Quest_Dracula1_Blunderbuss". It is STATIC, so no instance is
// needed - just call it.
//
//   Assembly-CSharp / <global ns> / Tool.PhotonInstantiate(string,Vector3,Quaternion,float)
//   RVA 0x21EFFE0   (dump build 20250718.01)
//
// Vector3 (12B) and Quaternion (16B) are passed BY VALUE. We declare matching structs
// and let MSVC emit the correct x64 ABI (it passes >8-byte structs by hidden pointer,
// which is exactly what the game's compiled method expects). The trailing MethodInfo*
// is passed null, as with the other direct calls in this patch.
//
// POSITION: for now it spawns at a fixed offset from the world origin. "In front of you"
// needs the local player's head transform (position + forward), which is a follow-up -
// getting that reliably needs the player/camera object, not just an RVA. The spawn call
// itself is proven by the object appearing at all.
//
// SEH-guarded: a bad prefab name or a wrong ABI must log, not crash.
// ================================================================================

namespace CustomSpawn {

    inline constexpr uintptr_t PhotonInstantiateRVA = 0x21EFFE0;   // 4-arg overload

    struct Vec3 { float x, y, z; };
    struct Quat { float x, y, z, w; };

    // static Tool* PhotonInstantiate(Il2CppString*, Vector3, Quaternion, float, MethodInfo*)
    using SpawnFn = void* (*)(void* name, Vec3 pos, Quat rot, float scale, void* mi);

    inline bool RawSpawn(const char* prefab, Vec3 pos, Quat rot, float scale) {
        __try {
            using StrNewFn = void* (*)(const char*);
            auto strNew = reinterpret_cast<StrNewFn>(GA + RR::Methods::Il2cpp::il2cpp_string_new);
            void* name = strNew(prefab);
            if (!name) return false;

            auto spawn = reinterpret_cast<SpawnFn>(GA + PhotonInstantiateRVA);
            spawn(name, pos, rot, scale, nullptr);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // Spawn a Tool by prefab name. Position defaults to a small offset in front of
    // world origin; tune once the player transform is wired.
    inline bool Spawn(const char* prefab, float scale = 1.0f) {
        if (!prefab || !*prefab) return false;
        if (!RR::Methods::Il2cpp::il2cpp_string_new) {
            std::cout << TAG_WARN() << "spawn skipped - il2cpp_string_new unresolved\n";
            return false;
        }

        Vec3 pos{ 0.0f, 1.5f, 2.0f };          // ~1.5m up, 2m out from origin
        Quat rot{ 0.0f, 0.0f, 0.0f, 1.0f };    // identity

        bool ok = RawSpawn(prefab, pos, rot, scale);
        if (ok) std::cout << TAG_INIT() << "spawned: \"" << prefab << "\"\n";
        else    std::cout << TAG_WARN() << "spawn faulted for \"" << prefab
                          << "\" - bad prefab name? (suppressed)\n";
        return ok;
    }
}
