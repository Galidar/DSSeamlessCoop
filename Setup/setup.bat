@echo off
:: ============================================================
::  DS3OS - Automatic Installer for Dark Souls 2 SOTFS
::  Thin wrapper that invokes setup.ps1 with bypass policy
:: ============================================================
title DS3OS - Setup

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
pause
exit /b %EXITCODE%
