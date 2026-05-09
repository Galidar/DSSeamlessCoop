![Bonfire](./Resources/banner.png?raw=true)

# Bonfire

Bonfire is Galidar's Dark Souls online server hub for Windows. It gives
**Dark Souls II: Scholar of the First Sin** and **Dark Souls III** a guided
private online flow: download one release, open the Flutter interface, choose a
fire, and play.

Bonfire is built for a different experience than older manual private-server
setups. It combines server management, automatic WAN/LAN detection, firewall
setup, profile vaults, public fire discovery, Steam validation, and game runtime
preparation behind one desktop UI.

For players, the goal is comfort: a host can create a fire in a few clicks, and
players who join through Bonfire receive the required client preparation
automatically during launch. Under the hood, Bonfire uses advanced protocol
decoding, private-server routing, and a native runtime bridge to unlock online
flows that were previously painful or unreachable.

## Highlights

- **Created by Galidar.** Bonfire is its own product direction: a Dark Souls
  online hub focused on speed, comfort, and unlocks that feel natural to use.
- **One release, a few clicks.** Download `windows.zip`, extract it, run
  `Bonfire.exe`, and use the interface.
- **Fast hosting.** Create a fire, set its name/password/visibility, and click
  **Light the bonfire**. Bonfire prepares the profile, applies firewall rules,
  starts the server, registers the fire, and launches the game.
- **Fast joining.** Choose a listed fire and click **Travel to this fire**.
  Bonfire retrieves the server details, prepares the client, and launches into
  the selected private server.
- **Automatic IP detection.** Bonfire detects public WAN and private LAN
  addresses so normal hosts do not have to hunt through network settings.
- **DS2 unlock layer included.** The Windows release already contains the
  required DS2 online layer. Users do not install extra packages after
  downloading Bonfire.
- **Separate saves by default.** Private-server play stays away from retail save
  files.

## Quick Start

1. Download the latest `windows.zip` from the
   [Releases page](https://github.com/Galidar/DSSeamlessCoop/releases/latest).
2. Extract it anywhere, for example `C:\Bonfire\`.
3. Run `Bonfire.exe` and accept the UAC prompt.
4. Make sure Steam is running and logged in.
5. Pick **Dark Souls II** or **Dark Souls III**.
6. To join: click **Travel to this fire** on a listed public fire.
7. To host: click **+ New bonfire**, configure it, then click
   **Light the bonfire**.

Bonfire uses Steam ownership/ticket behavior for authentication. Cracked or
Steam-emulated builds are not supported.

## What Bonfire Handles

Bonfire removes the chores that usually make private Souls servers feel
technical:

- Finds supported Steam game installs.
- Detects public WAN and private LAN addresses.
- Applies the Windows Firewall rules needed by the server.
- Manages multiple bonfire profiles with their own config, keypair, and
  database.
- Keeps one live profile active at a time so fixed server ports remain
  predictable.
- Starts and stops the private server from the UI.
- Publishes public fires to the master list.
- Retrieves server keys for sealed and public fires.
- Launches DS2/DS3 through the Bonfire runtime bridge.
- Keeps private-server saves separate from retail saves.

## Dark Souls II Unlocks

For DS2 SOTFS, Bonfire ships with the online unlock layer required for the
enhanced multiplayer experience. When DS2 is launched through Bonfire, the
client is prepared locally and routed to the selected private server.

This is why users only need the release package. Hosting or joining through the
current Bonfire release is enough for the client to be ready automatically.

Expected DS2 behavior:

- New characters receive the multiplayer tools at startup.
- Existing characters use the updated in-game acquisition path instead of being
  retroactively rewritten.
- DS2 private-server saves use `.sl3`.
- Server address, port, public key, save path, and DS2 unlock data are prepared
  during launch.

## Hosting A Fire

1. Choose the game tab.
2. Click **+ New bonfire**.
3. Open **Tend the flame** to set name, description, optional password, public
   visibility, WAN/LAN hostnames, and WebUI credentials.
4. Use auto-detected IPs unless you are hosting through VPN, paid hosting, or a
   custom network setup.
5. Click **Light the bonfire**.

If the fire is public, other Bonfire users can see it in the public list. If it
is sealed with a password, only players with the password can retrieve the key
needed to join.

## Joining A Fire

1. Open Bonfire.
2. Pick the DS2 or DS3 tab.
3. Browse or filter public fires.
4. Click **Travel to this fire**.
5. Enter the password if the fire is sealed.

Bonfire handles the launch and runtime preparation. Players should not need
command lines, manual IP entry, or extra installers.

## Saves And Safety

Keep **Use separate saves** enabled.

- DS2 private-server saves use `.sl3`.
- DS3 private-server saves use `.ds3os`.

Do not copy private-server saves over retail saves. Do not connect
Bonfire-prepared clients to FromSoftware official servers.

## Requirements

- Windows.
- Steam running and logged in.
- A legitimate Steam copy of the game being launched.
- Admin approval when Bonfire asks for it.
- For hosting outside your LAN: router or hosting-provider networking must allow
  the server ports through. Bonfire handles Windows Firewall; external routing
  still depends on your network.

Default server ports include `50000`, `50010`, `50020`, `50050`, and the
`50060-50200` game range. The WebUI uses `50005`.

## Troubleshooting

### Buttons stay disabled

Start Steam and log in. Bonfire checks Steam before launch because the server
auth flow depends on Steam tickets.

### Players cannot reach my fire

Apply Bonfire's firewall rules from the UI. If players are outside your LAN,
also forward the required ports on your router or hosting provider.

### DS2 launches without the unlock behavior

Make sure you are launching through the current Bonfire release, not directly
through Steam. The release must contain the `Loader\` folder from `windows.zip`.
The generated runtime log should show DS2 preparation succeeding.

### I have several fires

That is expected. Each bonfire is a separate profile with its own server config,
keypair, and database. Lighting a different profile stops the old one, swaps the
active files, and starts the selected fire.

## Architecture

| Component | Role |
| --- | --- |
| `Bonfire.exe` | Flutter desktop app for hosting, joining, filtering, configuring, and launching. |
| `BonfireService.exe` | .NET backend service for JSON-RPC, network detection, firewall rules, profile state, master-list requests, server lifecycle, and game launch. |
| `Server\Server.exe` | Native private multiplayer server for login/auth, matchmaking, signs, invasions, ghosts, messages, ranking, and related online systems. |
| `Loader\` | Native runtime bridge and game-specific launch data used by Bonfire during client preparation. |
| Master server | Public listing and key lookup service used by the Bonfire fire list. |

Bonfire keeps the UI, backend service, native server, master-list lookup, and
runtime bridge in one coordinated flow. The user clicks; Bonfire does the
wiring.

## Building From Source

Toolchain:

- Visual Studio 2022 with C++ workload
- .NET 8 SDK
- Flutter 3.27.x

Build sequence:

```bat
Tools\generate_vs2022.bat
msbuild /m /p:Configuration=Release intermediate\vs2022\ds3os.sln

dotnet publish Source\BonfireService\BonfireService.csproj ^
  -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o Source\bonfire\build\bonfire_service\

cd Source\bonfire
flutter pub get
flutter build windows --release
cd ..\..

Tools\generate_package_windows.bat
```

The release workflow in `.github/workflows/release.yml` performs the same build
and publishes `windows.zip`.

## Repository Layout

```text
Protobuf\               Network protocol definitions
Resources\              Banner, release ReadMe, prerequisites, and launch files
Source\
  bonfire\              Flutter desktop UI
  BonfireService\       .NET backend service used by Bonfire.exe
  Loader\               Shared launcher and configuration utilities
  Injector\             Native runtime bridge code
  Server\               Native private multiplayer server
  Server.DarkSouls2\    DS2 protocol implementation
  Server.DarkSouls3\    DS3 protocol implementation
  MasterServer\         Public listing and key lookup server
  Shared\               Shared native code
  ThirdParty\           Vendored dependencies
  WebUI\                Static server admin UI
Tools\                  Build, packaging, protobuf, and utility scripts
```

## Credits

Bonfire is created and directed by Galidar.

Bonfire also builds on important foundations from Souls online and reverse
engineering work:

- [TLeonardUK/ds3os](https://github.com/TLeonardUK/ds3os), the original DS2/DS3
  private server foundation. MIT.
- Dark Souls II SOTFS online research and enhancement work that made the DS2
  unlock layer possible.
- [garyttierney/ds3-open-re](https://github.com/garyttierney/ds3-open-re)
- [Jellybaby34/DkS3-Server-Emulator-Rust-Edition](https://github.com/Jellybaby34/DkS3-Server-Emulator-Rust-Edition)
- Souls server and reverse engineering communities.

## License

[MIT](./LICENSE)
