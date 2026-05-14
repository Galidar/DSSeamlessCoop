![Bonfire](./Resources/banner.png?raw=true)

# Bonfire

Bonfire is Galidar's Dark Souls online server hub for Windows. It gives
**Dark Souls I**, **Dark Souls II: Scholar of the First Sin**, and
**Dark Souls III** a guided private online flow: download one release, open the
Flutter interface, choose a fire, and play.

Bonfire is built for a different experience than older manual private-server
setups. It combines server management, automatic WAN/LAN detection, firewall
setup, profile vaults, public fire discovery, Steam validation, and game runtime
preparation behind one desktop UI.

For players, the goal is comfort: a host can create a fire in a few clicks, and
players who join through Bonfire receive the required client preparation
automatically during launch. There is no separate mod installation step. Dark
Souls I and Dark Souls III ship with Bonfire-native seamless runtime payloads,
while Dark Souls II currently uses Bonfire's private-server bridge and
separate-save path as the new native DS2 seamless runtime is rebuilt.

Under the hood, Bonfire uses advanced protocol decoding, private-server routing,
and native runtime bridges to unlock online flows that were previously painful
or unreachable.

## Highlights

- **Created by Galidar.** Bonfire is its own product direction: a Dark Souls
  online hub focused on speed, comfort, and unlocks that feel natural to use.
- **The trilogy in one place.** Host and join fires for Dark Souls I, Dark
  Souls II, and Dark Souls III from the same interface.
- **One release, a few clicks.** Download `DSSeamlessCoop_V*.zip`, extract it, run
  `Bonfire.exe`, and use the interface.
- **Updates from inside Bonfire.** The app checks GitHub releases, notifies when
  a newer build is available, and can download, apply, and restart into it.
- **Fast hosting.** Create a fire, set its name/password/visibility, and click
  **Light the bonfire**. Bonfire prepares the profile, applies firewall rules,
  starts the server, registers the fire, and launches the game.
- **Fast joining.** Choose a listed fire and click **Travel to this fire**.
  Bonfire retrieves the server details, prepares the client, and launches into
  the selected private server.
- **Automatic IP detection.** Bonfire detects public WAN and private LAN
  addresses so normal hosts do not have to hunt through network settings.
- **Relay-ready hosting.** Fires can use Bonfire Relay when the host is behind
  CGNAT, double NAT, strict routers, dorm networks, or ISPs that cannot expose
  inbound ports.
- **Bonfire-native online preparation.** The Windows release contains the DS1
  and DS3 seamless runtime layers plus DS2 private-server launch preparation.
  Users do not install mods, copy folders, run patchers, or add extra packages
  after downloading Bonfire.
- **No launcher juggling.** Bonfire prepares each game from the UI: it stages
  the needed runtime files, writes the fire password, launches the game, and
  injects the native bridge when that game needs it.
- **Separate saves by default.** Private-server play stays away from retail save
  files.

## Quick Start

1. Download the latest `DSSeamlessCoop_V*.zip` from the
   [Releases page](https://github.com/Galidar/DSSeamlessCoop/releases/latest).
2. Extract it anywhere, for example `C:\Bonfire\`.
3. Run `Bonfire.exe` and accept the UAC prompt.
4. Make sure Steam is running and logged in.
5. Pick **Dark Souls I**, **Dark Souls II**, or **Dark Souls III**.
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
- For Dark Souls I, starts a visible Bonfire coordinator server so the selected
  fire has a live local profile, keys, listing state, and diagnostic log while
  the DS1 runtime owns the co-op transport.
- Publishes public fires to the master list.
- Can tunnel a hosted fire through a public Bonfire Relay so the host only needs
  an outbound connection.
- Retrieves server keys for sealed and public fires.
- Launches Dark Souls I/II/III through the Bonfire runtime bridge.
- Writes per-fire session data into the supported client runtimes.
- Keeps private-server saves separate from retail saves.

## Native Online Layers

The Windows release keeps game-specific Bonfire-native runtime data under
consistent Loader names:

```text
Loader\DS1SeamlessCoop\
Loader\DS3SeamlessCoop\
```

Dark Souls I and Dark Souls III still create a `SeamlessCoop\` folder inside
the game directory during launch. That is the expected in-game runtime layout
used by those clients. Bonfire creates it automatically from the DS1/DS3 Loader
layers, so players should not rename or move it by hand. DS2 no longer ships a
loose param/map/menu data package. Its current launch path is handled by
`Injector.dll`, `Loader\modengine.ini`, the private server, and the native
runtime work tracked in `Docs\DS2NativeRuntime.md`.

## Dark Souls I Unlocks

For Dark Souls Remastered, Bonfire turns the **Dark Souls I** tab into a direct
full-session co-op launch path. It prepares its included
`Loader\DS1SeamlessCoop\` layer, writes the selected fire password into
`ds1sc_settings.ini`, creates the expected in-game `SeamlessCoop\` runtime
folder, injects `ds1sc.dll`, and starts `DarkSoulsRemastered.exe` directly.
Players do not install a separate mod or run a separate launcher.

When hosting a Dark Souls I fire, Bonfire also starts `Server\Server.exe` in
**DarkSouls1 coordinator mode**. The coordinator uses Steam AppID `570940`,
keeps the selected fire's profile/key/listing state live, listens on the normal
Bonfire ports, and writes explicit DS1 coordinator logs. The actual DS1
seamless co-op traffic is still handled by the included DS1 runtime's Steam P2P
networking layer; Bonfire owns launch, profile, password, local coordinator,
listing, and runtime preparation.

Player-facing unlocks:

- Seamless cooperative play across the whole game, from the tutorial through
  the final boss.
- Player death no longer ends the session. Dead players respawn in the same
  world at the last bonfire they rested at.
- Boss victories and area clears no longer send co-operators home.
- Multiplayer fog walls and zone barriers are removed.
- NPC dialogue and talk events are synchronized for the session.
- Resting at a bonfire resets the world state for all connected players.
- Progression completed online also progresses the player's own world.
- Up to six players can share the open world together: host plus five others.
- The runtime uses Steam's newer networking API for the co-op connection layer.
- Co-operators can reconnect from anywhere in the world and continue the same
  run quickly after a disconnect.
- Light sources carried by another player also cast light locally.
- Enemy and boss scaling is configurable through `ds1sc_settings.ini`; Bonfire
  ships with co-op-oriented defaults.
- Dying during a boss battle or invasion places the player into spectator limbo
  until the party is defeated or someone rests at a bonfire.
- Invasions are supported when `allow_invaders = 1`. During an active invasion,
  warping and bonfire resting are blocked until the invader is defeated.

Bonfire preparation:

- Visible tab: **Dark Souls I**.
- Supported Steam executable: `DarkSoulsRemastered.exe`.
- Packaged runtime payload: `Loader\DS1SeamlessCoop\`.
- In-game staged runtime folder: `SeamlessCoop\`.
- Local coordinator: `Server\Server.exe` with `GameType = DarkSouls1`.
- Coordinator data file: `SeamlessCoop\bonfire_coordinator.ini`.
- Runtime setting: `serverless_features = 0`, keeping DS1 scoped to the
  selected Bonfire fire instead of unrelated serverless events.
- Runtime save extension: `.co2`.

## Dark Souls II Runtime Rebuild

For DS2 SOTFS, Bonfire now starts from a clean native-runtime baseline instead
of the previous loose-data **DS2SeamlessCoop** package. On launch it routes the
game to the selected fire, writes server address/port/key data, and keeps
private-server saves on `.sl3` when separate saves are enabled. Players do not
install a DS2 package by hand; joining or hosting through Bonfire is still the
launch path.

Current DS2 support:

- The selected Bonfire fire is applied automatically. Players do not edit IPs,
  server keys, or local config files by hand.
- Separate DS2 private-server saves use `.sl3`.
- The old bundled loose param/map/menu package is removed from releases and is
  no longer auto-detected from stale installs.
- External DS2 overhaul data can still be used only when explicitly selected.
- The replacement seamless-style DS2 runtime is being built in the native
  bridge, with the first milestones documented in `Docs\DS2NativeRuntime.md`.

Bonfire preparation:

- Visible tab: **Dark Souls II**.
- Supported Steam executable: `DarkSoulsII.exe`.
- Packaged runtime payload: none for the old loose-data layer.
- Runtime config: `Loader\modengine.ini` with file overrides disabled.
- Runtime save extension: `.sl3`.
- Server address, port, public key, and save path are prepared during launch.

## Dark Souls III Unlocks

For Dark Souls III, Bonfire prepares its included `Loader\DS3SeamlessCoop\`
layer, writes the selected fire password into `ds3sc_settings.ini`, creates the
expected in-game `SeamlessCoop\` runtime folder, injects `ds3sc.dll`, and
launches `DarkSoulsIII.exe` through Bonfire. Players do not install a separate
mod or start `ds3sc_launcher.exe`.

Player-facing unlocks:

- Seamless cooperative play across the whole game, from the tutorial through
  the final boss.
- Player death no longer ends the session. Dead players respawn in the same
  world at the last bonfire they rested at.
- Boss victories and area clears no longer send co-operators home.
- Multiplayer fog walls and zone barriers are removed.
- NPC dialogue and talk events are synchronized for the session.
- Resting at a bonfire resets the world state for all connected players.
- Progression completed online also progresses the player's own world when the
  included runtime setting is enabled.
- Up to six players can share the open world together: host plus five others.
- The runtime uses Steam's newer networking API for Dark Souls III co-op.
- Co-operators can reconnect from anywhere in the world and continue the same
  run quickly after a disconnect.
- Enemy and boss scaling is configurable through `ds3sc_settings.ini`.
- If one player rests at a bonfire while others are inside boss rooms, those
  players are removed from the boss rooms.
- Dying during a boss battle or invasion places the player into spectator limbo
  until the party is defeated or someone rests at a bonfire.
- Invasions are supported when `allow_invaders = 1`. During an active invasion,
  warping and bonfire resting are blocked until the invader is defeated.
- Players who want to invade can use the in-game hairpin item to enter a random
  eligible session within matchmaking range.

Bonfire preparation:

- Visible tab: **Dark Souls III**.
- Supported Steam executable: `DarkSoulsIII.exe`.
- Packaged runtime payload: `Loader\DS3SeamlessCoop\`.
- In-game staged runtime folder: `SeamlessCoop\`.
- Runtime save extension: `.co2`.

## Hosting A Fire

1. Choose the game tab.
2. Click **+ New bonfire**.
3. Open **Tend the flame** to set name, description, optional password, public
   visibility, WAN/LAN hostnames, and WebUI credentials.
4. Use auto-detected IPs unless you are hosting through VPN, paid hosting, or a
   custom network setup.
5. If your ISP/router cannot accept inbound connections, enable
   **Use Bonfire Relay** and enter the relay endpoint provided by the community
   or operator you trust.
6. Click **Light the bonfire**.

If the fire is public, other Bonfire users can see it in the public list. If it
is sealed with a password, only players with the password can retrieve the key
needed to join.

## Bonfire Relay

Bonfire Relay is for hosts who cannot open ports because of CGNAT, double NAT,
locked-down routers, university networks, hotel networks, or ISP restrictions.
Instead of waiting for inbound traffic at home, BonfireService opens one
outbound control connection to a public relay. The relay allocates public
login/auth/game ports, Bonfire writes those ports into the active server config,
and the fire advertises the relay endpoint to players.

Player flow stays the same: choose a public fire and click
**Travel to this fire**. Bonfire prepares the runtime and connects to the
advertised endpoint. Relay-backed fires are marked with a `RELAY` badge when
the master list supports that metadata.

Relay host flow:

1. Open **Tend the flame**.
2. Enable **Use Bonfire Relay**.
3. Enter the relay host, relay control port, and optional token.
4. Click **Light the bonfire**.

Relay operator flow:

1. Deploy `Source\MasterServer` on a public machine or VPS.
2. In `Source\MasterServer\src\config\config.json`, set:

```json
"relay": {
  "enabled": true,
  "control_port": 50030,
  "public_hostname": "YOUR.PUBLIC.IPV4",
  "min_port": 51000,
  "max_port": 51999,
  "shared_secret": "optional-token"
}
```

3. Allow inbound TCP on the control port and inbound TCP/UDP on the relay port
   range. The default range gives enough room for many simultaneous fires.

The relay is not a database or a matchmaking rewrite. It is a transport bridge:
TCP login/auth and UDP game traffic enter through public relay ports and are
multiplexed back over the host's outbound BonfireService tunnel.

## Joining A Fire

1. Open Bonfire.
2. Pick the Dark Souls I, Dark Souls II, or Dark Souls III tab.
3. Browse or filter public fires.
4. Click **Travel to this fire**.
5. Enter the password if the fire is sealed.

Bonfire handles the launch and native online preparation. Players should not
need command lines, manual IP entry, external mod installers, or separate
launchers.

## Saves And Safety

Keep **Use separate saves** enabled.

- Dark Souls I runtime saves use `.co2`.
- DS2 private-server saves use `.sl3`.
- Dark Souls III runtime saves use `.co2`.

Do not copy private-server saves over retail saves. Do not connect
Bonfire-prepared clients to FromSoftware official servers.

## Requirements

- Windows.
- Steam running and logged in.
- A legitimate Steam copy of the game being launched.
- Admin approval when Bonfire asks for it.
- For direct hosting outside your LAN: router or hosting-provider networking
  must allow the server ports through. Bonfire handles Windows Firewall;
  external routing still depends on your network.
- For CGNAT or locked-down networks: use Bonfire Relay instead of direct
  port-forwarding.

Default server ports include `50000`, `50010`, `50020`, `50050`, and the
`50060-50200` game range. The WebUI uses `50005`. Relay operators also expose
the relay control port, default `50030`, and the configured public relay range,
default `51000-51999`.

## Troubleshooting

### Buttons stay disabled

Start Steam and log in. Bonfire checks Steam before launch because the server
auth flow depends on Steam tickets.

### Players cannot reach my fire

Apply Bonfire's firewall rules from the UI. If players are outside your LAN,
either forward the required ports on your router/hosting provider or enable
Bonfire Relay. If your ISP uses CGNAT, router port-forwarding alone will not
be enough because traffic never reaches your router from the public internet.

### Dark Souls I or III launches without the online runtime

Make sure you are launching through the current Bonfire release, not directly
through Steam. The release must contain the `Loader\` folder from the release zip.
For Dark Souls I and Dark Souls III, seeing `SeamlessCoop\` inside the game
directory after a Bonfire launch is normal; Bonfire recreates that runtime
folder from `Loader\DS1SeamlessCoop\` or `Loader\DS3SeamlessCoop\`.

### DS2 launches without private-server routing

Make sure you are launching through the current Bonfire release, not directly
through Steam. The release must contain the `Loader\` folder from the release zip.
The generated runtime log should show DS2 preparation succeeding. Current DS2
releases no longer include the old loose-data unlock package; that work is being
replaced by the native DS2 runtime path.

### I have several fires

That is expected. Each bonfire is a separate profile with its own server config,
keypair, and database. Lighting a different profile stops the old one, swaps the
active files, and starts the selected fire.

## Architecture

| Component | Role |
| --- | --- |
| `Bonfire.exe` | Flutter desktop app for hosting, joining, filtering, configuring, and launching. |
| `BonfireService.exe` | .NET backend service for JSON-RPC, network detection, firewall rules, profile state, master-list requests, server lifecycle, and game launch. |
| `Server\Server.exe` | Native private multiplayer server for DS2/DS3 online systems and DS1 local coordination/profile/listing state. |
| `Loader\` | Native runtime bridge and game-specific launch data used by Bonfire during client preparation. |
| Master server | Public listing and key lookup service used by the Bonfire fire list. |
| Bonfire Relay | Optional public TCP/UDP bridge for hosts behind CGNAT or strict networks. |

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
and publishes `DSSeamlessCoop_V<version>.zip`.

The canonical app version lives in `VERSION`. Local builds, Windows executable
metadata, BonfireService, and the release workflow all stamp from that file
unless a tagged release or manual workflow input overrides it.

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
- Yui, whose Dark Souls Remastered and Dark Souls III Seamless Co-op work
  provides important foundations for the Bonfire-native DS1/DS3 runtime layers.
- Dark Souls II SOTFS online research and enhancement work that made the DS2
  private-server bridge possible.
- [garyttierney/ds3-open-re](https://github.com/garyttierney/ds3-open-re)
- [Jellybaby34/DkS3-Server-Emulator-Rust-Edition](https://github.com/Jellybaby34/DkS3-Server-Emulator-Rust-Edition)
- Souls server and reverse engineering communities.

## License

[MIT](./LICENSE)
