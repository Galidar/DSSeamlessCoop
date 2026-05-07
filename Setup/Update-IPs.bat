@echo off
:: ============================================================
::  DS3OS - Update IPs (wrapper for Update-IPs.ps1)
:: ============================================================
title DS3OS - Update IPs

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-IPs.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
pause
exit /b %EXITCODE%
