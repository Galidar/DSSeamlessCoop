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
::     Server\
::       Server.exe
::       Server.pdb
::       steam_api64.dll
::       steam_appid.txt
::       WebUI\
::     Prerequisites\
::     ReadMe.txt

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
