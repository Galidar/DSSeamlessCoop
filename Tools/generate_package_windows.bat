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
::       modengine.ini              -- DS2 runtime data loader config
::       ds2multoverhaul\           -- DS2 online unlock data
::       SeamlessCoop\              -- DS3 online unlock runtime
::     Server\
::       Server.exe
::       Server.pdb
::       steam_api64.dll
::       steam_appid.txt
::       WebUI\
::     Prerequisites\
::     ReadMe.txt
::
:: Release-time Loader payloads live in Resources\Loader\ and are xcopied
:: straight into DSSeamlessCoop\Loader\ at packaging time. End users get a
:: single windows.zip with everything bundled inside it.

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

:: Loader payload bundle (committed under Resources\Loader\). Produces a
:: single-download windows.zip with client preparation data pre-installed,
:: so end users don't need to fetch or configure anything separately.
xcopy /s /e /y Resources\Loader\ DSSeamlessCoop\Loader\
if errorlevel 1 (
    echo ERROR: failed to copy Loader payload bundle into Loader\
    exit /b 1
)
mkdir DSSeamlessCoop\Loader\SeamlessCoop\crashdumps\attachments
mkdir DSSeamlessCoop\Loader\SeamlessCoop\crashdumps\reports
echo Loader payload bundled into DSSeamlessCoop\Loader\
