@echo off
:: ============================================================
::  DS3OS - Start the Dark Souls 2 SOTFS Server
:: ============================================================

cd /d "%~dp0Server"
title DS3OS Server - Dark Souls 2 SOTFS
echo Starting DS3OS server for Dark Souls 2 SOTFS...
echo.
echo - GameType: DarkSouls2
echo - WebUI:    http://localhost:50005
echo.
echo Steam must be open (no login required).
echo.
echo IMPORTANT: The server runs in the background.
echo To STOP it, use: 4-Stop-Server.bat
echo.
Server.exe
echo.
echo Server launched. It keeps running in the background as long as the
echo configured ports are listed under "Listening". Close this window any time.
echo.
pause
