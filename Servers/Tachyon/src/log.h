#pragma once
//
// Minimal leveled logging. Default level is Info (lifecycle: start, connect/disconnect, handshake
// completion, plus all warnings/errors) - quiet enough to run as a service. Debug adds the
// per-message / per-relay flood; Warn drops the lifecycle chatter for a near-silent service.
//
// The macros preserve printf format strings verbatim and self-gate, so a disabled level costs
// nothing but the comparison.
#include <cstdio>

enum class LogLevel { Error = 0, Warn = 1, Info = 2, Debug = 3 };

namespace tlog {
    inline LogLevel level = LogLevel::Info;
    inline bool enabled(LogLevel l) { return (int) l <= (int) level; }
}

#define LOGE(...) do { if (tlog::enabled(LogLevel::Error)) { printf(__VA_ARGS__); } } while (0)
#define LOGW(...) do { if (tlog::enabled(LogLevel::Warn))  { printf(__VA_ARGS__); } } while (0)
#define LOGI(...) do { if (tlog::enabled(LogLevel::Info))  { printf(__VA_ARGS__); } } while (0)
#define LOGD(...) do { if (tlog::enabled(LogLevel::Debug)) { printf(__VA_ARGS__); } } while (0)
