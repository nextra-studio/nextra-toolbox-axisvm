@echo off
rem Double-click installer: runs Install-Addons.ps1 without needing a PowerShell window or execution-policy changes.
rem Optional: drag your AxisVM AddOns folder onto this file, or run:  Install-Addons.bat "D:\AxisVM X8\AddOns"
setlocal
cd /d "%~dp0"
if not exist "%~dp0Install-Addons.ps1" (
    echo Install-Addons.ps1 must be in the same folder as this file. Extract the whole ZIP first.
    pause
    exit /b 1
)
set "ARGS="
if not "%~1"=="" set "ARGS=-AddOnsDir "%~1""
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Addons.ps1" %ARGS%
if errorlevel 1 (
    echo.
    echo The install did not finish. If the message above mentions access or permission, the AxisVM
    echo folder needs administrator rights. Close AxisVM if it is running.
    choice /m "Try again as administrator"
    if not errorlevel 2 (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%ARGS%' -Verb RunAs"
    )
    exit /b 1
)
echo.
pause
