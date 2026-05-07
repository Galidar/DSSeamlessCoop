@echo off
:: ============================================================
::  DS3OS - Update IPs in config.json
::  Useful when your public IP changes (common with residential ISPs)
:: ============================================================
title DS3OS - Update IPs
setlocal EnableDelayedExpansion

set "CONFIG_FILE=%~dp0Server\Saved\default\config.json"

if not exist "%CONFIG_FILE%" (
    echo [ERROR] Could not find: %CONFIG_FILE%
    echo Run setup.bat first to install the server.
    pause
    exit /b 1
)

echo ============================================================
echo  Updating DS3OS server IPs...
echo ============================================================
echo.

echo Detecting current IPs...
for /f "delims=" %%i in ('powershell -NoProfile -Command "try { (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10).Trim() } catch { 'ERROR' }"') do set "WAN_IP=%%i"
for /f "delims=" %%i in ('powershell -NoProfile -Command "(Get-NetIPAddress -AddressFamily IPv4 ^| Where-Object { ($_.PrefixOrigin -eq 'Dhcp' -or $_.PrefixOrigin -eq 'Manual') -and $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } ^| Select-Object -First 1).IPAddress"') do set "LAN_IP=%%i"

if "%WAN_IP%"=="ERROR" (
    echo [ERROR] Could not get public IP. Check your internet connection.
    pause
    exit /b 1
)
if "%LAN_IP%"=="" (
    echo [ERROR] Could not detect local IP.
    pause
    exit /b 1
)

echo   Public IP   : !WAN_IP!
echo   Local IP    : !LAN_IP!
echo.

echo Applying changes to config.json...
powershell -NoProfile -Command "$c = Get-Content '%CONFIG_FILE%' -Raw; $c = $c -replace '\"ServerHostname\":\\s*\"[^\"]*\"', ('\"ServerHostname\": \"' + '%WAN_IP%' + '\"'); $c = $c -replace '\"ServerPrivateHostname\":\\s*\"[^\"]*\"', ('\"ServerPrivateHostname\": \"' + '%LAN_IP%' + '\"'); Set-Content -Path '%CONFIG_FILE%' -Value $c -NoNewline -Encoding UTF8"

if %errorLevel% neq 0 (
    echo [ERROR] Failed to update config.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo  IPs updated successfully.
echo ============================================================
echo.
echo If the server is running, restart it to apply the changes:
echo   1. Run 4-Stop-Server.bat
echo   2. Run 2-Start-Server.bat
echo.
pause
