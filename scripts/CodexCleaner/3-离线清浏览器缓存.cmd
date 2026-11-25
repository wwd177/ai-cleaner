@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CodexCleaner.ps1" -Mode OfflineBrowserCache -Interactive
echo.
pause
