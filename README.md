![Dark Souls - Open Server](./Resources/banner.png?raw=true)

[![Discord](https://img.shields.io/discord/937318023495303188?label=Discord)](https://discord.gg/pBmquc9Jkj)

# DSSeamlessCoop — Fork of DS3OS

This repository is a personal fork of [TLeonardUK/ds3os](https://github.com/TLeonardUK/ds3os), the **Dark Souls Open Server** project — an open-source implementation of the game servers for Dark Souls 2 (SOTFS) and Dark Souls 3.

The goal of this fork is to host my own modifications focused on **Dark Souls II: Scholar of the First Sin**, with a streamlined installation experience. All server-side functionality is built on top of the original DS3OS codebase.

> **Full credit** for the underlying server implementation goes to **TLeonardUK** and the DS3OS contributors. This fork would not exist without their work. Please support the original project: https://github.com/TLeonardUK/ds3os

## What's different in this fork?

- **Bonfire** — a modern Flutter desktop app that replaces the original WinForms Loader. It handles install, firewall rules, network detection, multiple server profiles (DS II / DS III in separate tabs), and per-bonfire launch. Dark/amber Souls-themed UI.
- **Multi-profile support** — keep separate "bonfires" for DS2 and DS3 (or several configs of the same game) and switch between them with one click. Each profile carries its own server config, RSA keypair, and database.
- **WebUI credentials** are user-editable from the Bonfire UI (no more hunting in `config.json`).
- **Self-hosted Windows builds** via GitHub Actions — releases are produced from this fork's source code, with no dependency on the upstream release page.
- **Customizations** to the server behavior, focused on Dark Souls II SOTFS (work in progress).

## Quick start

1. Download the latest `windows.zip` from the [Releases page](https://github.com/Galidar/DSSeamlessCoop/releases/latest).
2. Extract it anywhere (for example, `C:\DSSeamlessCoop\`).
3. Open `Bonfire.exe` (Windows asks for admin — required for DLL injection into the game).
4. Pick the **Dark Souls II** or **Dark Souls III** tab.
5. Click **+ New bonfire**, name it. Bonfire downloads the server (~115 MB), applies firewall rules, and saves a profile.
6. Click **Light the bonfire** on the profile row → it starts the local server and launches the game pointed at it.

That's the whole install. Steam must be running while the server is up — no Steam login required.

## What is the underlying project?

DS3OS is an open-source implementation of the game servers for Dark Souls 2 (SOTFS) and Dark Souls 3. It exists to provide an alternative for playing online with mods without the risk of being banned, or for people who simply want to play privately without cheaters or invasions.

If you have any trouble, the upstream Discord is the best place for tech support: https://discord.gg/pBmquc9Jkj

## Can I use it with a pirated game?

No. The server authenticates Steam tickets. Please do not ask about piracy or Steam emulators — neither this fork nor the upstream project supports them.

FROM SOFTWARE deserves your support too — please buy their games if you can.

## How does the server work?

A release ships three things working together:

- **Bonfire.exe** — Flutter desktop UI. Spawns BonfireService.exe and talks to it over JSON-RPC.
- **BonfireService.exe** — .NET 8 backend. Manages the local Server.exe lifecycle, downloads / installs releases, talks to the master server, configures Windows Firewall, writes Injector.config, spawns the game, and patches it via `WriteProcessMemory` + `CreateRemoteThread` (admin required).
- **Server.exe** — the actual unofficial game server. First run generates `Server/Saved/default/config.json` with default matchmaking settings — Bonfire surfaces the user-relevant fields (name, description, password, IPs, advertise toggle, WebUI creds) in the **Tend the flame** panel.

Bonfire's per-profile mode keeps **one Server.exe instance** alive; switching profiles stops, copies the chosen profile's `config.json` + RSA keypair into `Server/Saved/default/`, and restarts. Loader's WinForms UI is no longer shipped — its non-UI utilities (`Source/Loader/Utils/`, `Source/Loader/Config/`) are still linked into BonfireService since they implement the proven Win32 patching, RSA, and Steam-detection code.

> **NOTE:** The Steam client (no login required) must be running when you launch a bonfire, otherwise Server.exe will fail to initialize.

For users of this fork, all of this is automated by **Bonfire** (download / firewall / network detection / per-profile config / server lifecycle / game launch).

## Feature support (from upstream)

:bangbang: Dark Souls 2 SOTFS support is still marked **experimental** by the upstream project — some features may misbehave.

| Feature | Dark Souls 3 | Dark Souls 2 SOTFS |
| --- | --- | --- |
| Stable enough for use | :heavy_check_mark: | Experimental |
| Network transport | :heavy_check_mark: | :heavy_check_mark: |
| Announcement messages | :heavy_check_mark: | :heavy_check_mark: |
| Profile management | :heavy_check_mark: | :heavy_check_mark: |
| Blood messages | :heavy_check_mark: | :heavy_check_mark: |
| Bloodstains | :heavy_check_mark: | :heavy_check_mark: |
| Ghosts | :heavy_check_mark: | :heavy_check_mark: |
| Summoning | :heavy_check_mark: | :heavy_check_mark: |
| Invasions | :heavy_check_mark: | :heavy_check_mark: |
| Auto-Summoning (Covenants) | :heavy_check_mark: | :heavy_check_mark: |
| Mirror Knight | n/a | :heavy_check_mark: |
| Matchmaking | :heavy_check_mark: | :heavy_check_mark: |
| Leaderboards | :heavy_check_mark: | :heavy_check_mark: |
| Bell Ringing | :heavy_check_mark: | n/a |
| Quick Matches (Arenas) | :heavy_check_mark: | :heavy_check_mark: |
| Telemetry/Misc | :heavy_check_mark: | :heavy_check_mark: |
| Ticket Authentication | :heavy_check_mark: | :heavy_check_mark: |
| Master Server Support | :heavy_check_mark: | :heavy_check_mark: |
| Loader Support | :heavy_check_mark: | :heavy_check_mark: |
| WebUI For Admin | :heavy_check_mark: | :heavy_check_mark: |
| Sharding Support | :heavy_check_mark: | :heavy_check_mark: |
| Discord Activity Feed | :heavy_check_mark: | |

## Will this ban my retail account?

DSOS uses its own save files. As long as you don't copy `.ds3os` saves back over your retail saves, you should be fine.

## FAQ

### How do I switch between Dark Souls 3 and Dark Souls 2?

Each bonfire profile is fixed to one game (chosen by the tab you create it from). To play the other game, create a new bonfire under the corresponding tab — Bonfire keeps DS II and DS III profiles entirely separate.

### Why aren't my save files appearing?

DSOS uses its own saves to avoid issues with retail. The "Use separate saves" toggle (in Bonfire's Game Settings) is on by default and should stay on.

### I launch the game but it can't connect

1. Bonfire requires **admin** (the manifest enforces this — Windows asks at startup). Refusing UAC means DLL injection into the game silently fails and the game can't reach the local server.
2. The firewall step inside Bonfire's installer adds rules for ports `50000`, `50010`, `50050`, `50020` (TCP+UDP) — accept the UAC prompt for `netsh` when asked.
3. **Tend the flame** lets you override `ServerHostname` (WAN) / `ServerPrivateHostname` (LAN) for VPNs / paid hosting.
4. Steam must be running. Bonfire's **Light the bonfire** button is disabled until the Steam check passes.

### What do all the properties in the config file mean?

They are documented in the source: [Source/Server/Config/RuntimeConfig.h](./Source/Server/Config/RuntimeConfig.h)

## Building from source

This project uses Visual Studio 2022 and C++17. CMake generates the project files — either use the CMake GUI or run one of the `generate_*` scripts inside `Tools/`. Generated project files are stored in the `intermediate/` folder.

For automated builds, this fork uses GitHub Actions (see `.github/workflows/`).

## Repository structure

```
/
├── Protobuf/              Protobuf definitions used by the server's network traffic
├── Resources/             General resources for building and packaging
├── Source/                All source code for the project
│   ├── bonfire/           Flutter desktop UI (Dart). Replaces the old Loader's WinForms UI.
│   ├── BonfireService/    .NET 8 backend for Bonfire — server lifecycle, install/firewall/
│   │                      network/profile management, JSON-RPC over stdio with Bonfire.exe.
│   ├── Loader/            Legacy WinForms launcher (no longer shipped). Its Utils/ + Config/
│   │                      classes are still linked into BonfireService — they implement the
│   │                      proven Win32 patching, RSA, and Steam-detection code.
│   ├── Injector/          DLL injected into the game to provide DS3OS functionality
│   ├── MasterServer/      NodeJS API server for advertising and listing active servers
│   ├── Server/            Source code for the main server
│   ├── Server.DarkSouls3/ Code specific to Dark Souls 3 support
│   ├── Server.DarkSouls2/ Code specific to Dark Souls 2 support
│   ├── Shared/            Code shared between the server and injector
│   ├── ThirdParty/        Third-party libraries
│   └── WebUI/             Static resources for the management web page
└── Tools/                 Build scripts, cheat engine tables, and analysis utilities
```

## Credits

This fork stands on the shoulders of significant prior work:

- **Original project:** [TLeonardUK/ds3os](https://github.com/TLeonardUK/ds3os) — MIT License
- **Reverse engineering & community knowledge:**
  - https://github.com/garyttierney/ds3-open-re
  - https://github.com/Jellybaby34/DkS3-Server-Emulator-Rust-Edition
  - https://github.com/AmirBohd/ModEngine2
- **Community:** Members of the souls modding Discord
- **Graphics & icons:**
  - Campfire icon by ultimatearm from www.flaticon.com
  - UI icons by Mark James from http://www.famfamfam.com/lab/icons/silk/

## License

This fork preserves the [MIT License](./LICENSE) of the upstream project.
