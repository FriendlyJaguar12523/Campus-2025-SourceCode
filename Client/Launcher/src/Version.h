#pragma once
// The launcher's version - the one place it's set. tools\build_release.bat raises it by one for
// every release (1.01 -> 1.02 ... 1.99 -> 2.00) and stages the exe for UploadUpdate.bat; running
// copies see the higher LV_VERSION_BUILD on the server and update themselves (Update.h).
#define LV_VERSION_MAJOR 1
#define LV_VERSION_MINOR 24
#define LV_VERSION_STR "1.24"
#define LV_VERSION_BUILD 124      // MAJOR * 100 + MINOR - what the update check compares

// The DEBUG configuration (Launcher.vcxproj, bin\Debug\Campus-Debug.exe) is for testing: it defines CAMPUS_DEBUG,
// keeps its own settings folder, never self-updates, and says so wherever the version is shown.
#ifdef CAMPUS_DEBUG
#define LV_BUILD_TAG " DEBUG"
#else
#define LV_BUILD_TAG ""
#endif
