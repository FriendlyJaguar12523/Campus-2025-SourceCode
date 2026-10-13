#pragma once
#include <filesystem>
#include <string>

// UTF-8 <-> UTF-16 (Win32 wants wide strings, ImGui and the engine speak UTF-8).
std::wstring Widen(const std::string& s);
std::string Narrow(const std::wstring& s);

inline std::filesystem::path U8Path(const std::string& s) { return std::filesystem::path(Widen(s)); }
inline std::string PathU8(const std::filesystem::path& p) { return Narrow(p.wstring()); }

std::string ToLower(std::string s);
std::string NowStamp();   // yyyyMMdd-HHmmss, for work folders
