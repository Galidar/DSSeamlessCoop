@echo off
title Stop DS3OS Server
echo Stopping Server.exe...
taskkill /IM Server.exe /F >nul 2>&1
if %errorLevel% equ 0 (
    echo Server stopped successfully.
) else (
    echo No server was running.
)
echo.
pause
