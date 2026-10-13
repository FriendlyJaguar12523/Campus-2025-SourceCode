#pragma once
// Anti-cheat log sink.
//
// All the Protections/Anti* files spam std::cout with detection state, watchdog
// heartbeats, "PASSED"/"DETECTED" lines etc. In Debug builds that's useful for
// verifying the checks fire. In Release builds it's just noise (and a signal to
// anyone reading the console that "Kurt Cobain" is running).
//
// So: route every anti-cheat log through Protections::log. In Debug that alias
// is std::cout (unchanged behavior). In Release it's a real std::ostream backed
// by a null streambuf that discards every write — the whole `<<` chain compiles
// down to a handful of no-op calls the optimizer folds away. No per-line #ifdefs.
//
// Usage:  Protections::log << TAG_ANTI() << "checking...\n";
//
// PATCH_DEBUG (not _DEBUG) is the switch. _DEBUG is deliberately NOT defined in this
// project's Debug|x64 config to keep the CRT off the debug heap (see vcxproj comment) —
// third-party libs like discord-rpc.lib ship only in release-CRT form and can't link
// with the debug CRT. PATCH_DEBUG is our project-owned equivalent gate.
#include <iostream>
#include <streambuf>

namespace Protections {

#ifdef PATCH_DEBUG
    inline std::ostream& log = std::cout;
#else
    namespace detail {
        struct NullBuf : std::streambuf {
            int overflow(int c) override { return c; }
        };
        inline NullBuf g_nullBuf;
    }
    inline std::ostream log(&detail::g_nullBuf);
#endif

}
