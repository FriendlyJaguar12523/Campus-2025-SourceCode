#pragma once

#include <string>
#include <chrono>
#include <iomanip>
#include <sstream>
#include <ctime>
#include <iostream>

// Console Colors
#define CLR_RESET   "\x1b[0m"
#define CLR_DIM     "\x1b[90m"
#define CLR_CYAN    "\x1b[38;5;51m"
#define CLR_GREEN   "\x1b[38;5;120m"
#define CLR_YELLOW  "\x1b[38;5;227m"
#define CLR_RED     "\x1b[38;5;203m"
#define CLR_MAGENTA "\x1b[38;5;213m"
#define CLR_BLUE    "\x1b[38;5;111m"
#define CLR_ORANGE  "\x1b[38;5;215m"
#define CLR_VIOLET  "\x1b[38;5;141m"
#define CLR_BOLD    "\x1b[1m"
#define CLR_ERR     "\x1b[1m\x1b[38;5;196m"
#define CLR_WHITE   "\x1b[38;5;255m"

inline std::string GetStatusTime() {
    auto now = std::chrono::system_clock::now();
    auto time_t_now = std::chrono::system_clock::to_time_t(now);
    std::tm tm_now;
    localtime_s(&tm_now, &time_t_now);

    std::ostringstream oss;
    oss << std::setfill('0') << std::setw(2) << tm_now.tm_hour << ":"
        << std::setfill('0') << std::setw(2) << tm_now.tm_min << ":"
        << std::setfill('0') << std::setw(2) << tm_now.tm_sec;
    return oss.str();
}

// Log Tags with better formatting
#define TAG_NET()     (CLR_DIM "[" + GetStatusTime() + "] " CLR_CYAN "[NET]     " CLR_RESET)
#define TAG_HTTP()    (CLR_DIM "[" + GetStatusTime() + "] " CLR_CYAN "[HTTP]    " CLR_RESET)
#define TAG_PATCH()   (CLR_DIM "[" + GetStatusTime() + "] " CLR_GREEN "[PATCH]   " CLR_RESET)
#define TAG_REFEREE() (CLR_DIM "[" + GetStatusTime() + "] " CLR_YELLOW "[REFEREE] " CLR_RESET)
#define TAG_TLS()     (CLR_DIM "[" + GetStatusTime() + "] " CLR_YELLOW "[TLS]     " CLR_RESET)
#define TAG_UI()      (CLR_DIM "[" + GetStatusTime() + "] " CLR_VIOLET "[UI]      " CLR_RESET)
#define TAG_HILE()    (CLR_DIM "[" + GetStatusTime() + "] " CLR_RED "[HILE]    " CLR_RESET)
#define TAG_IMAGES()  (CLR_DIM "[" + GetStatusTime() + "] " CLR_BLUE "[IMAGES]  " CLR_RESET)
#define TAG_DISCORD() (CLR_DIM "[" + GetStatusTime() + "] " CLR_BLUE "[DISCORD] " CLR_RESET)
#define TAG_ANTI()    (CLR_DIM "[" + GetStatusTime() + "] " CLR_RED "[ANTI]    " CLR_RESET)
#define TAG_WARN()    (CLR_DIM "[" + GetStatusTime() + "] " CLR_ORANGE "[WARN]    " CLR_RESET)
#define TAG_INIT()    (CLR_DIM "[" + GetStatusTime() + "] " CLR_BOLD CLR_GREEN "[INIT]    " CLR_RESET)
#define TAG_INFO()    (CLR_DIM "[" + GetStatusTime() + "] " CLR_WHITE "[INFO]    " CLR_RESET)