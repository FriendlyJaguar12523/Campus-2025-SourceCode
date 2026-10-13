@echo off
setlocal EnableDelayedExpansion
title Campus launcher - release build

REM Makes the next release of bin\Campus.exe:
REM   1. raises the version by one (src\Version.h: 1.01 -> 1.02); put back if anything fails
REM   2. builds the exe (static C++ runtime - that one file is all anyone needs)
REM   3. builds the Campus patch (CampusPatch2025\2025Patch.sln)
REM   4. stages the exe in Deploy\launcher-app and the patch in Deploy\launcher-patch
REM UploadUpdate.bat puts both on the server: everyone's launcher updates itself to the new exe, and
REM every Play downloads the newest patch. The launcher may stay open: its exe is moved aside to
REM bin\Campus.old.exe (the new one deletes it when it starts).
REM Only the patch changed? "python tools\release_app.py patch" then UploadUpdate.bat is enough.

set "ROOT=%~dp0.."
set "MSB=D:\VisualStudio\18\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe"
set "BUMPED="

where python >nul 2>&1 || (echo [x] python is not on PATH & goto :fail)
REM Make a free slot for the previous build. If something is still holding Campus.old.exe (a
REM previous auto-update's old process hasn't let go yet), slide it to Campus.old2.exe /
REM Campus.old3.exe / ... .old9.exe - RemoveOldExe() on the next launcher start sweeps all of
REM them. Only bail out if there is nowhere left to park the stale copy.
if exist "%ROOT%\bin\Campus.old.exe" (
    del /q "%ROOT%\bin\Campus.old.exe" 2>nul
    if exist "%ROOT%\bin\Campus.old.exe" (
        set "PARKED="
        for %%N in (2 3 4 5 6 7 8 9) do (
            if not defined PARKED (
                if not exist "%ROOT%\bin\Campus.old%%N.exe" (
                    ren "%ROOT%\bin\Campus.old.exe" "Campus.old%%N.exe" 2>nul
                    if not exist "%ROOT%\bin\Campus.old.exe" set "PARKED=1"
                )
            )
        )
        if not defined PARKED (
            echo [x] bin\Campus.old.exe is locked and every parking slot is taken. Close every running Campus launcher first.
            goto :fail
        )
    )
)
if exist "%ROOT%\bin\Campus.exe" (
    move /y "%ROOT%\bin\Campus.exe" "%ROOT%\bin\Campus.old.exe" >nul || (echo [x] Can't move bin\Campus.exe aside - close every running Campus launcher first. & goto :fail)
)
python "%ROOT%\tools\release_app.py" bump || goto :fail
set "BUMPED=1"
REM Rotate the launcher-launch secret - every release gets its own key, so an exfiltrated one is
REM short-lived. The server reads the same key from Data/Launcher/app/<sha>.launchkey, which
REM "release_app.py stage" uploads next to the exe below.
python "%ROOT%\tools\release_app.py" newlaunchkey || goto :fail
"%MSB%" "%ROOT%\Launcher.vcxproj" /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m /nologo /v:minimal || goto :fail
"%MSB%" "%ROOT%\..\2025Patch.sln" /t:Build /p:Configuration=Release /p:Platform=x64 /m /nologo /v:minimal || goto :fail
python "%ROOT%\tools\release_app.py" stage || goto :fail
python "%ROOT%\tools\release_app.py" patch || goto :fail

echo.
echo  === DONE: %ROOT%\bin\Campus.exe - run UploadUpdate.bat to release it. ===
endlocal
exit /b 0

:fail
if defined BUMPED python "%ROOT%\tools\release_app.py" unbump
if not exist "%ROOT%\bin\Campus.exe" if exist "%ROOT%\bin\Campus.old.exe" move /y "%ROOT%\bin\Campus.old.exe" "%ROOT%\bin\Campus.exe" >nul
echo.
echo  === BUILD FAILED ===
endlocal
exit /b 1
