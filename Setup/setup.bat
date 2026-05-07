@echo off
:: ============================================================
::  DS3OS - Automatic Installer for Dark Souls 2 SOTFS
::  Downloads the latest release from this fork (Galidar/DSSeamlessCoop)
::  Auto-detects WAN/LAN IPs and applies the preconfigured config.json
:: ============================================================
title DS3OS - Setup
setlocal EnableDelayedExpansion

set "REPO=Galidar/DSSeamlessCoop"
set "ASSET_NAME=windows.zip"
set "ZIP_FILE=%~dp0windows.zip"

echo ============================================================
echo  Installing DS3OS for Dark Souls 2 SOTFS...
echo ============================================================
echo.

echo [1/5] Detecting IPs automatically...
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

echo       Public IP : !WAN_IP!
echo       Local IP  : !LAN_IP!
echo.

echo [2/5] Resolving latest release from %REPO% via GitHub API...
for /f "delims=" %%i in ('powershell -NoProfile -Command "try { $r = Invoke-RestMethod -Uri 'https://api.github.com/repos/%REPO%/releases/latest' -TimeoutSec 15; ($r.assets ^| Where-Object { $_.name -eq '%ASSET_NAME%' } ^| Select-Object -First 1).browser_download_url } catch { 'ERROR' }"') do set "DOWNLOAD_URL=%%i"

if "%DOWNLOAD_URL%"=="ERROR" (
    echo [ERROR] Could not query GitHub API.
    echo Check your internet connection or that the repo has a published release.
    pause
    exit /b 1
)
if "%DOWNLOAD_URL%"=="" (
    echo [ERROR] No '%ASSET_NAME%' asset found in the latest release of %REPO%.
    echo Make sure the GitHub Actions release workflow has run successfully.
    pause
    exit /b 1
)

echo       URL: !DOWNLOAD_URL!
echo.

echo [3/5] Downloading binaries...
powershell -NoProfile -Command "Invoke-WebRequest -Uri '%DOWNLOAD_URL%' -OutFile '%ZIP_FILE%' -UseBasicParsing"
if not exist "%ZIP_FILE%" (
    echo [ERROR] Download failed.
    pause
    exit /b 1
)

echo.
echo [4/5] Extracting files...
powershell -NoProfile -Command "Expand-Archive -Path '%ZIP_FILE%' -DestinationPath '%~dp0' -Force"

echo.
echo [5/5] Applying configuration with detected IPs...
if not exist "%~dp0Server\Saved\default" mkdir "%~dp0Server\Saved\default"
powershell -NoProfile -Command "$c = Get-Content '%~dp0config.json' -Raw; $c = $c -replace '__WAN_IP__', '%WAN_IP%' -replace '__LAN_IP__', '%LAN_IP%'; Set-Content -Path '%~dp0Server\Saved\default\config.json' -Value $c -NoNewline -Encoding UTF8"
if %errorLevel% neq 0 (
    echo [ERROR] Failed to apply configuration.
    pause
    exit /b 1
)

del "%ZIP_FILE%"

echo.
echo ============================================================
echo  Installation completed successfully.
echo ============================================================
echo.
echo Configuration applied with your current IPs:
echo   - Public IP : %WAN_IP%
echo   - Local IP  : %LAN_IP%
echo.
echo Next steps:
echo   1. Right click ^> "Run as administrator" on 1-Setup-Firewall.bat
echo   2. Double click 2-Start-Server.bat
echo   3. Right click ^> "Run as administrator" on 3-Start-Loader.bat
echo.
echo NOTE: If your public IP changes later, run Update-IPs.bat.
echo.
pause
