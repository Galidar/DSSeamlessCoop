:: Builds the windows.zip release layout. Run from repo root after
:: building C++ (Server, Injector) into Bin\x64_release\, building the
:: Flutter Bonfire app into Source\bonfire\build\windows\x64\runner\Release\,
:: and publishing BonfireService into Source\bonfire\build\bonfire_service\.
::
:: Layout produced:
::
::   DSSeamlessCoop\
::     Bonfire.exe                  -- Flutter desktop UI (admin manifest)
::     BonfireService.exe           -- .NET 8 self-contained backend
::     flutter_windows.dll
::     data\                        -- Flutter assets + compiled Dart
::     Loader\
::       Injector.dll
::       Injector.pdb
::       modengine.ini              -- ModEngine config (DS2 overhaul)
::       ds2multoverhaul\           -- DS2 Multiplayer Overhaul 1.0.4a mod files
::     Server\
::       Server.exe
::       Server.pdb
::       steam_api64.dll
::       steam_appid.txt
::       WebUI\
::     Prerequisites\
::     ReadMe.txt
::
:: The DS2 mod payload (ds2multoverhaul\ + modengine.ini) is fetched at
:: build time from a permanent pre-release tagged `dsseamlesscoop-ds2-data-1.0.4a`
:: and extracted into Loader\. It is NOT in the public git repo because it
:: contains data derived from retail Dark Souls II files.

mkdir DSSeamlessCoop
mkdir DSSeamlessCoop\Loader
mkdir DSSeamlessCoop\Server
mkdir DSSeamlessCoop\Prerequisites
copy Resources\ReadMe.txt DSSeamlessCoop\ReadMe.txt
xcopy /s Resources\Prerequisites DSSeamlessCoop\Prerequisites

:: Server (C++)
xcopy /s /y Resources\steam_appid.txt DSSeamlessCoop\Server\
xcopy /s Bin\x64_release\steam_api64.dll DSSeamlessCoop\Server\
xcopy /s Bin\x64_release\WebUI\ DSSeamlessCoop\Server\WebUI\
xcopy /s Bin\x64_release\Server.exe DSSeamlessCoop\Server\
xcopy /s Bin\x64_release\Server.pdb DSSeamlessCoop\Server\

:: Injector DLL (C++)
xcopy /s Bin\x64_release\Injector.dll DSSeamlessCoop\Loader\
xcopy /s Bin\x64_release\Injector.pdb DSSeamlessCoop\Loader\

:: Bonfire UI (Flutter) — full Release dir contains bonfire.exe, data\,
:: flutter_windows.dll. Rename bonfire.exe to Bonfire.exe for consistency.
xcopy /s /y Source\bonfire\build\windows\x64\runner\Release\* DSSeamlessCoop\
ren DSSeamlessCoop\bonfire.exe Bonfire.exe

:: BonfireService (C# .NET 8 self-contained single-file)
xcopy /s /y Source\bonfire\build\bonfire_service\BonfireService.exe DSSeamlessCoop\

:: DS2 data bundle (fetched from permanent pre-release).
:: Produces a single-download windows.zip with the DS2 server-side data
:: pre-installed, so end users don't need to fetch or configure anything.
echo Fetching DSSeamlessCoop DS2 data bundle...
curl -fsSL -o ds2-data-bundle.zip "https://github.com/Galidar/DSSeamlessCoop/releases/download/dsseamlesscoop-ds2-data-1.0.4a/DSSeamlessCoop-ds2-data-1.0.4a.zip"
if errorlevel 1 (
    echo ERROR: failed to download DS2 data bundle
    exit /b 1
)
:: Use Expand-Archive — Windows tar.exe (bsdtar) does not reliably
:: extract zips written by Compress-Archive even though libarchive
:: claims zip support. PowerShell ships on every Windows runner.
powershell -NoProfile -Command "Expand-Archive -Path ds2-data-bundle.zip -DestinationPath DSSeamlessCoop\Loader\ -Force"
if errorlevel 1 (
    echo ERROR: failed to extract DS2 data bundle
    exit /b 1
)
del ds2-data-bundle.zip
echo DS2 data bundled into DSSeamlessCoop\Loader\
