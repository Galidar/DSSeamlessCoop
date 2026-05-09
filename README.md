![Bonfire](./Resources/banner.png?raw=true)

# Bonfire

Private multiplayer for **Dark Souls II: Scholar of the First Sin** and **Dark Souls III** — runs on your own machine, no Steam login, retail-account safe (separate saves), and the **DS2 Multiplayer Overhaul** mod is bundled inside the release. One download, one click, you play.

Players join your local server. You and your friends play with mods, custom rules, and zero exposure to FromSoftware's official servers.

## Why Bonfire

- **One download, one click.** Grab `windows.zip` from the [Releases page](https://github.com/Galidar/DSSeamlessCoop/releases/latest), extract anywhere, run `Bonfire.exe`. The DS2 multiplayer overhaul is already inside — no separate mod fetching, no ModEngine `dinput8.dll`, no batch files.
- **DS2 multiplayer mod, native.** Bonfire's own injector loads the Overhaul 1.0.4a server-side data at runtime. No third-party loader chained in, no `modengine.ini` for the user to wrangle.
- **Multi-profile Souls hub.** Keep several "bonfires" per game and per ruleset, switch between them with a click. Each profile carries its own server config, RSA keypair, and database.
- **Flutter desktop UI.** Dark/amber Souls-themed interface for everything: install, firewall rules, network detection, server management, in-app help, profile management.
- **Built for players, not engineers.** No `config.json` editing, no command-line setup, no manual install steps. WebUI credentials are user-editable from the UI. Steam check, UAC, firewall, and DLL injection are all handled transparently.

## Quick start

1. Download the latest `windows.zip` from the [Releases page](https://github.com/Galidar/DSSeamlessCoop/releases/latest).
2. Extract anywhere — for example `C:\Bonfire\`.
3. Run `Bonfire.exe` (Windows asks for admin — required for DLL injection into the game).
4. Pick the **Dark Souls II** or **Dark Souls III** tab.
5. Click **+ New bonfire**, name it. Bonfire downloads the server, applies firewall rules, and saves a profile.
6. Click **Light the bonfire** on the profile row → it starts your local server and launches the game pointed at it.

That's the whole install. Steam must be running while the server is up — no Steam login required.

## Won't this ban my retail account?

No. Bonfire keeps its own save files (`.sl3` for DS2, `.ds3os` for DS3). As long as you don't copy them back over your retail saves, your account is fine. The **Use separate saves** toggle is on by default — leave it on.

## Pirated games

Not supported. The server authenticates Steam tickets. Don't ask about Steam emulators or cracked builds — neither Bonfire nor its underlying networking layer will help you.

FromSoftware deserves your support. Buy their games.

## How it works under the hood

A release ships these working together inside one ZIP:

| Component | Tech | Role |
|-----------|------|------|
| `Bonfire.exe` | Flutter (Dart) | Desktop UI. Spawns BonfireService and talks to it over JSON-RPC. |
| `BonfireService.exe` | .NET 8 (C#) | Backend daemon. Manages local Server.exe, downloads/installs releases, talks to the master server, configures Windows Firewall, writes `Injector.config`, spawns the game, and patches it via `WriteProcessMemory` + `CreateRemoteThread`. |
| `Loader/Injector.dll` | Native C++ | Injected into the game on launch. Hooks server-address resolution, port replacement, save filename, DS2 file overrides, ModEngine-style mod-file resolution, and DS2 shadow-map patches. |
| `Server/Server.exe` | Native C++ | Local game server. Implements the Dark Souls 2/3 multiplayer protocols (matchmaking, summoning, invasions, ghosts, blood messages, covenants, etc.). |
| `Loader/ds2multoverhaul/` + `modengine.ini` | Game data | DS2 Multiplayer Overhaul 1.0.4a — Param/, map/, menu/, regulation overrides. Loaded at runtime via Bonfire's ModEngine-compatible file resolver. |

Bonfire keeps **one Server.exe alive at a time**. Switching profiles stops it, swaps `Server/Saved/default/config.json` + RSA keypair for the chosen profile, and restarts.

> **Note:** Steam (no login required) must be running when you light a bonfire — Server.exe initializes against the Steam SDK.

## Game features

|  | DS3 | DS2 SOTFS |
|---|---|---|
| Stable enough for use | ✅ | Experimental |
| Network transport | ✅ | ✅ |
| Blood messages, bloodstains, ghosts | ✅ | ✅ |
| Summoning, invasions, co-op | ✅ | ✅ |
| Auto-summoning (covenants) | ✅ | ✅ |
| Quick Matches (Arenas) | ✅ | ✅ |
| Matchmaking + leaderboards | ✅ | ✅ |
| Bell Ringing | ✅ | n/a |
| Mirror Knight | n/a | ✅ |
| Discord Activity Feed | ✅ | — |
| **DS2 Multiplayer Overhaul 1.0.4a (bundled)** | n/a | ✅ |

## I launch the game but it can't connect

1. Bonfire requires **admin** — the manifest enforces it, Windows asks at startup. Refusing UAC means DLL injection silently fails and the game can't reach the local server.
2. The firewall step inside Bonfire's installer adds rules for ports `50000`, `50010`, `50050`, `50020` (TCP+UDP) — accept the UAC prompt for `netsh` when asked.
3. **Tend the flame** lets you override `ServerHostname` (WAN) and `ServerPrivateHostname` (LAN) for VPNs or paid hosting.
4. Steam must be running. Bonfire's **Light the bonfire** button stays disabled until the Steam check passes.

## Building from source

Toolchain: **Visual Studio 2022**, **C++17**, **.NET 8 SDK**, **Flutter 3.27.x**.

```bat
:: 1. Generate VS solution (Server, Injector C++)
Tools\generate_vs2022.bat

:: 2. Build C++
msbuild /m /p:Configuration=Release intermediate\vs2022\ds3os.sln

:: 3. Publish BonfireService (.NET 8 self-contained single-file)
dotnet publish Source\BonfireService\BonfireService.csproj ^
    -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

:: 4. Build Bonfire (Flutter)
cd Source\bonfire && flutter pub get && flutter build windows --release && cd ..\..

:: 5. Assemble release ZIP
Tools\generate_package_windows.bat
```

CI does all of the above automatically on every tag push (`v*`) — see [.github/workflows/release.yml](./.github/workflows/release.yml). To cut a release: bump version in `Source/bonfire/pubspec.yaml`, `Source/BonfireService/BonfireService.csproj`, and `Source/bonfire/lib/screens/about_dialog.dart`, commit, then `git tag vX.Y.Z && git push origin vX.Y.Z`.

## Repository layout

```
/
├── Protobuf/              Protobuf definitions for the network protocol
├── Resources/             Banner, ReadMe, prerequisites, Loader/ (DS2 mod data bundled)
├── Source/
│   ├── bonfire/           Flutter desktop UI (Dart)
│   ├── BonfireService/    .NET 8 backend (C#) — JSON-RPC over stdio with Bonfire.exe
│   ├── Loader/            Linked-in Win32 / RSA / Steam utilities (legacy WinForms UI not shipped)
│   ├── Injector/          DLL injected into the game (C++) — hooks, mod-file resolution
│   ├── MasterServer/      Node.js master server for advertising and listing servers
│   ├── Server/            Local game server (C++)
│   ├── Server.DarkSouls3/ DS3 protocol-specific code
│   ├── Server.DarkSouls2/ DS2 protocol-specific code
│   ├── Shared/            Shared code (server + injector)
│   ├── ThirdParty/        Vendored libraries
│   └── WebUI/             Static admin web UI
└── Tools/                 Build scripts, packaging, analysis utilities
```

## Contributing

Issues and pull requests welcome. For deeper protocol / RE questions, the souls modding Discord is a great resource.

---

## Credits

Bonfire stands on significant prior work:

- **DS3OS** — [TLeonardUK/ds3os](https://github.com/TLeonardUK/ds3os) — original Dark Souls 2/3 server protocol implementation, master server, and injector blueprint. MIT.
- **DS2 Multiplayer Overhaul 1.0.4a** — bundled with Bonfire. Original mod authors and the SOTFS modding community.
- **ModEngine** — [katalash/ModEngine](https://github.com/katalash/ModEngine) — the loose-file override mechanism that Bonfire's injector reimplements natively for DS2.
- **Reverse engineering & community knowledge:**
  - [garyttierney/ds3-open-re](https://github.com/garyttierney/ds3-open-re)
  - [Jellybaby34/DkS3-Server-Emulator-Rust-Edition](https://github.com/Jellybaby34/DkS3-Server-Emulator-Rust-Edition)
  - [AmirBohd/ModEngine2](https://github.com/AmirBohd/ModEngine2)
- **Souls modding Discord community.**
- **Graphics:**
  - Campfire icon by ultimatearm — flaticon.com
  - UI icons by Mark James — famfamfam silk icons

## License

[MIT](./LICENSE)
