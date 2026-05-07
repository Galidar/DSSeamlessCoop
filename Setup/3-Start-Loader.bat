@echo off
:: ============================================================
::  DS3OS - Start the game Loader
::  IMPORTANT: Run as ADMINISTRATOR
::  (right click -> Run as administrator)
:: ============================================================

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo.
    echo [ERROR] The Loader REQUIRES ADMINISTRATOR privileges to patch the game.
    echo Close this window and reopen it with right click ^> Run as administrator.
    echo.
    pause
    exit /b 1
)

cd /d "%~dp0Loader"
echo Starting DS3OS Loader...
echo.
echo Set the game path to:
echo   C:\Program Files (x86)\Steam\steamapps\common\Dark Souls II Scholar of the First Sin\Game\DarkSoulsII.exe
echo.
Loader.exe
