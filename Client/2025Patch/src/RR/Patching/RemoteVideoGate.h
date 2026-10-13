#pragma once
// ================================================================
// REMOTE VIDEO PLAYER - allow http(s) video urls
// ================================================================
// The Remote Video Player chip's "Video Url" field refuses web addresses. When the field is committed the client's
// RemoteVideoPlayer coroutine (async state machine OGLJOCPANOO, MoveNext at RVA 0x1C9A7E0 in build 20250718.01) does:
//
//     if (IsHttpOrHttps(newVideoUrl))        // RVA 0x8D1F980 - parses the string as a Uri, true for the http / https schemes
//         Debug.LogError("Setting arbitrary video urls is not allowed");     // and the VideoPlayer is never given the url
//     else
//         videoPlayer.url = <the string>;
//
// so a https://cdn.camprec.net/cdn/video/<file>.mp4 address is rejected on the player's own PC and the server never even
// sees a request. The check is purely client side - nothing about the url (host, role, server answer) is consulted.
//
// IsHttpOrHttps has one other caller (RVA 0xDDB4BB), so it is hooked, not byte-patched: when it is asked from the video
// state machine it answers "no", for every other caller it is the original. The videos themselves are served by the
// server from /cdn/video/ (video/mp4, range requests).
#include <intrin.h>
#include <cstdint>
#include <iostream>
#include "Console.h"
#include "../../Utils/globals.h"

namespace RemoteVideoGate {

    inline constexpr uintptr_t IsHttpOrHttpsRVA = 0x8D1F980;   // static bool (string, MethodInfo*)
    inline constexpr uintptr_t VideoMoveNextBegin = 0x1C9A7E0;  // OGLJOCPANOO.MoveNext
    inline constexpr uintptr_t VideoMoveNextEnd = 0x1C9ABD0;  // OGLJOCPANOO.SetStateMachine follows

    using IsHttpFn = bool(*)(void* str, void* mi);
    inline IsHttpFn o_IsHttp = nullptr;

    inline bool IsHttp_H(void* str, void* mi) {
        const uintptr_t ra = (uintptr_t)_ReturnAddress();
        if (ra >= GA + VideoMoveNextBegin && ra < GA + VideoMoveNextEnd) {
            static int shown = 0;
            if (shown < 5) { ++shown; std::cout << "[Video] remote video player: web address allowed\n"; }
            return false;
        }
        return o_IsHttp ? o_IsHttp(str, mi) : false;
    }
}
