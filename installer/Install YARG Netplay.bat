@echo off
rem Double-click to install BepInEx (if needed) and YARG Netplay into YARG.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
