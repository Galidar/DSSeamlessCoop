@echo off
:: ============================================================
::  DS3OS - Firewall configuration for Dark Souls 2 SOTFS
::  Run as ADMINISTRATOR (right click -> Run as administrator)
:: ============================================================

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo.
    echo [ERROR] This script requires ADMINISTRATOR privileges.
    echo Close this window and reopen it with right click ^> Run as administrator.
    echo.
    pause
    exit /b 1
)

echo.
echo Removing previous DS3OS rules if they exist...
netsh advfirewall firewall delete rule name="DS3OS Server TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="DS3OS Server UDP" >nul 2>&1
netsh advfirewall firewall delete rule name="DS3OS GameRange TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="DS3OS GameRange UDP" >nul 2>&1
netsh advfirewall firewall delete rule name="DS3OS WebUI" >nul 2>&1
netsh advfirewall firewall delete rule name="DS3OS Server.exe" >nul 2>&1
netsh advfirewall firewall delete rule name="DS3OS Loader.exe" >nul 2>&1

echo Creating firewall rules for server ports (50000, 50010, 50050, 50020)...
netsh advfirewall firewall add rule name="DS3OS Server TCP" dir=in action=allow protocol=TCP localport=50000,50010,50050,50020
netsh advfirewall firewall add rule name="DS3OS Server UDP" dir=in action=allow protocol=UDP localport=50000,50010,50050,50020

echo Creating firewall rule for WebUI (50005)...
netsh advfirewall firewall add rule name="DS3OS WebUI" dir=in action=allow protocol=TCP localport=50005

echo Creating rules for match port range (50060-50200)...
netsh advfirewall firewall add rule name="DS3OS GameRange TCP" dir=in action=allow protocol=TCP localport=50060-50200
netsh advfirewall firewall add rule name="DS3OS GameRange UDP" dir=in action=allow protocol=UDP localport=50060-50200

echo Creating rules for executables...
netsh advfirewall firewall add rule name="DS3OS Server.exe" dir=in action=allow program="%~dp0Server\Server.exe" enable=yes
netsh advfirewall firewall add rule name="DS3OS Loader.exe" dir=in action=allow program="%~dp0Loader\Loader.exe" enable=yes

echo.
echo ============================================================
echo  Firewall rules configured successfully.
echo ============================================================
echo.
echo NOTE: If you want other people to connect from outside your
echo local network, you also need to forward these ports on your
echo ROUTER (port forwarding) to your LAN IP:
echo   - TCP/UDP 50000, 50010, 50050, 50020
echo   - TCP/UDP 50060-50200
echo.
pause
