![Dark Souls - Open Server](./Resources/banner.png?raw=true)

[![Discord](https://img.shields.io/discord/937318023495303188?label=Discord)](https://discord.gg/pBmquc9Jkj)

# DSSeamlessCoop — Fork of DS3OS

This repository is a personal fork of [TLeonardUK/ds3os](https://github.com/TLeonardUK/ds3os), the **Dark Souls Open Server** project — an open-source implementation of the game servers for Dark Souls 2 (SOTFS) and Dark Souls 3.

The goal of this fork is to host my own modifications focused on **Dark Souls II: Scholar of the First Sin**, with a streamlined installation experience. All server-side functionality is built on top of the original DS3OS codebase.

> **Full credit** for the underlying server implementation goes to **TLeonardUK** and the DS3OS contributors. This fork would not exist without their work. Please support the original project: https://github.com/TLeonardUK/ds3os

## What's different in this fork?

- **Streamlined installation** for Dark Souls 2 SOTFS via the [`Setup/`](./Setup) folder — automated installer that detects your IPs, downloads the latest build, and applies a preconfigured `config.json`.
- **Self-hosted Windows builds** via GitHub Actions — releases are produced from this fork's source code, no dependency on the upstream release page.
- **Customizations** to the server behavior (work in progress).

## Quick start

If you just want to run the server, see the [`Setup/`](./Setup) folder. TL;DR:

```sh
git clone https://github.com/Galidar/DSSeamlessCoop.git
cd DSSeamlessCoop\Setup
```

Then double click `setup.bat`. Full instructions are in [Setup/README.md](./Setup/README.md).

## What is the underlying project?

DS3OS is an open-source implementation of the game servers for Dark Souls 2 (SOTFS) and Dark Souls 3. It exists to provide an alternative for playing online with mods without the risk of being banned, or for people who simply want to play privately without cheaters or invasions.

If you have any trouble, the upstream Discord is the best place for tech support: https://discord.gg/pBmquc9Jkj

## Can I use it with a pirated game?

No. The server authenticates Steam tickets. Please do not ask about piracy or Steam emulators — neither this fork nor the upstream project supports them.

FROM SOFTWARE deserves your support too — please buy their games if you can.

## How does the server work?

When you build the project, you'll get a `Bin/` folder with two relevant subfolders: `Loader/` and `Server/`.

- The **Loader** lets you launch Dark Souls 2/3 in a way that connects to an unofficial server. You can either create a server or join an existing one.
- The **Server** is the actual game server. The first time it runs, it generates `Saved/default/config.json` with default matchmaking settings — you can edit this file and restart the server to apply changes.
- Servers can be password-protected by setting a `Password` value in `config.json`.

> **NOTE:** The Steam client (no login required) must be running when you launch `Server.exe`, otherwise it will fail to initialize.

For users of this fork, all of this is automated by `Setup/setup.bat`.

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

After running the server once, edit `Saved/default/config.json` and change `GameType` between `DarkSouls2` and `DarkSouls3`. This fork ships preconfigured for `DarkSouls2`.

### Why aren't my save files appearing?

DSOS uses its own saves to avoid issues with retail. To transfer your retail saves to DSOS, click the settings (cog) icon at the bottom of the Loader and press **Copy Retail Saves to DSOS**. The reverse transfer is not provided automatically — for safety.

### I launch the game but it can't connect

1. Make sure the Loader is running **as administrator** (it patches the game's memory).
2. Make sure ports `50000`, `50010`, `50050`, `50020` (TCP and UDP) are open in your firewall and forwarded on your router. The `Setup/1-Setup-Firewall.bat` script handles the firewall side automatically.
3. Verify `ServerHostname` (your WAN IP) and `ServerPrivateHostname` (your LAN IP) in `Saved/default/config.json`. The `Setup/setup.bat` and `Setup/Update-IPs.bat` scripts handle this automatically.

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
├── Setup/                 Streamlined installer for Dark Souls 2 SOTFS (this fork)
├── Source/                All source code for the project
│   ├── Injector/          DLL injected into the game to provide DS3OS functionality
│   ├── Loader/            WinForms app that loads DS2/DS3 to connect to a custom server
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
