@echo off
setlocal
title Campus launcher - debug build

REM Builds the DEBUG launcher for testing:  bin\Debug\Campus-Debug.exe
REM   - its own settings / session / log folder (%LOCALAPPDATA%\CampusLauncher-Debug), so it never touches
REM     the real launcher's sign-in or settings
REM   - never self-updates (it says what the server has and carries on)
REM   - the window title, version line and log say DEBUG; the log also goes to Visual Studio's Output window
REM   - can run next to the real launcher
REM   - does NOT change the version, the launcher key or anything in Deploy\ - nothing here is shipped.
REM The release is still built by build_release.bat (Release configuration) and sent out with PushUpdates.bat.
REM Extra arguments are passed to the launcher:  build_debug.bat run --server http://127.0.0.1:8080
REM (also: --data <folder>, --page play|customize|settings, --nofind)

set "ROOT=%~dp0.."
set "MSB=D:\VisualStudio\18\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe"

"%MSB%" "%ROOT%\Launcher.vcxproj" /t:Build /p:Configuration=Debug /p:Platform=x64 /m /nologo /v:minimal
if errorlevel 1 (
    echo.
    echo  === DEBUG BUILD FAILED ===
    endlocal
    exit /b 1
)
echo.
echo  === DONE: %ROOT%\bin\Debug\Campus-Debug.exe ===

if /i "%~1"=="run" (
    shift
    start "" "%ROOT%\bin\Debug\Campus-Debug.exe" %1 %2 %3 %4 %5 %6 %7 %8 %9
)
endlocal
exit /b 0
