@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Manage-ScheduledTask.ps1" -Action Interactive
echo.
pause
