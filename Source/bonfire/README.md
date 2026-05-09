# Bonfire Flutter App

This folder contains Bonfire's Windows desktop interface. The app is the user's
control room: it lists public fires, creates local fires, edits server settings,
detects network addresses, applies firewall setup through the service, and
launches DS2/DS3 through the Bonfire runtime path.

The Flutter app does not talk to the games directly. It starts
`BonfireService.exe` and communicates with it through line-delimited JSON-RPC
over stdio.

## Product Role

Bonfire's UI exists to make private Souls online feel simple:

- no manual `config.json` editing;
- no manual WAN/LAN IP lookup for normal hosts;
- no separate launcher workflow;
- no direct user handling of server keys;
- no manual DS2 unlock setup.

The user's main verbs are the same ones shown in the interface:

- **Travel to this fire** joins a listed public or sealed server.
- **Light the bonfire** starts a local profile and launches into it.
- **Tend the flame** edits the active profile's name, visibility, network
  values, password, and WebUI credentials.

## Runtime Layout

A packaged release keeps these pieces beside each other:

```text
Bonfire.exe
BonfireService.exe
Loader\
Server\
  Server.exe
```

`BonfireService.exe` must sit beside `Bonfire.exe`. In development,
`lib/state/app_state.dart` also searches common
`Source\BonfireService\bin` output paths so `flutter run` can work.

## Development

Requirements:

- Flutter 3.27.x on Windows
- .NET 8 SDK for `BonfireService.exe`
- Native Server/Loader binaries when testing full game launch behavior

Common local workflow:

```powershell
cd C:\Users\Diux\Desktop\DSSeamlessCoop

dotnet publish .\Source\BonfireService\BonfireService.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\Source\bonfire\build\bonfire_service\

cd .\Source\bonfire
flutter pub get
flutter run -d windows
```

Build the release UI with:

```powershell
cd C:\Users\Diux\Desktop\DSSeamlessCoop\Source\bonfire
flutter build windows --release
```

Assemble the full Windows release from the repository root:

```powershell
C:\Users\Diux\Desktop\DSSeamlessCoop\Tools\generate_package_windows.bat
```

## Important Source Areas

```text
lib\main.dart                  Starts the service and shows boot errors
lib\rpc\rpc_client.dart        JSON-RPC stdio client
lib\state\app_state.dart       App state and typed RPC wrappers
lib\screens\home_screen.dart   Main public/local bonfire experience
lib\screens\help_dialog.dart   In-app user guide
lib\screens\about_dialog.dart  Product/version dialog
lib\design.dart                Shared strings, spacing, type, and labels
lib\theme.dart                 Theme construction
```

## Checks

Run these before shipping UI changes:

```powershell
cd C:\Users\Diux\Desktop\DSSeamlessCoop\Source\bonfire
flutter analyze
flutter test
```

Full launch validation also needs a packaged layout with `BonfireService.exe`,
`Loader\`, `Server\Server.exe`, and the bundled DS2 online unlock layer.
