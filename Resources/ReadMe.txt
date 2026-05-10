-------------------------------------------------------------------------
 Bonfire
 Created by Galidar
 https://github.com/Galidar/DSSeamlessCoop
-------------------------------------------------------------------------

Bonfire is a Dark Souls online server hub for Windows. It lets you host or
join Dark Souls I, Dark Souls II: Scholar of the First Sin, and Dark Souls III
fires from one desktop interface.

== Quick start ==

1. Install the C++ runtimes from Prerequisites/ if Windows does not already
   have them.

2. Run Bonfire.exe and accept the UAC prompt.
   Bonfire needs elevated permission for firewall setup and game runtime
   preparation.

3. Make sure Steam is running and logged in.

4. Pick the Dark Souls I, Dark Souls II, or Dark Souls III tab.

5. To join a public fire, click "Travel to this fire".
   To host, click "+ New bonfire", configure it, then click
   "Light the bonfire".

Bonfire detects your WAN/LAN IPs, manages the server profile, handles firewall
rules, starts the server, launches the game, and prepares the client for the
selected fire.

There is no manual mod installation step. The Dark Souls I, II, and III online
layers included in this release are adapted to run as Bonfire-native unlocks:
pick a fire in the UI, launch, and Bonfire prepares the game automatically.

== What is included ==

  Bonfire.exe              Flutter desktop interface.
  BonfireService.exe       Backend service used by the interface.
  flutter_windows.dll      Flutter runtime.
  data/                    Flutter assets and compiled Dart code.
  Server/                  Private multiplayer server, DS1 coordinator, and
                           WebUI files.
  Loader/                  Runtime bridge and game online unlock data.
    DS1SeamlessCoop/       Dark Souls I packaged runtime payload.
    DS2SeamlessCoop/       Dark Souls II packaged runtime payload.
    DS3SeamlessCoop/       Dark Souls III packaged runtime payload.
  Prerequisites/           Visual C++ redistributables.

The supported Bonfire-native online data is already included. Users do not need
extra files, external installers, patchers, or separate launchers after
downloading this release.

Dark Souls I and Dark Souls III may create a SeamlessCoop/ folder inside the
game directory when launched through Bonfire. That is expected runtime staging
and should not be removed while the game is running.

Keep separate saves enabled. Dark Souls I and Dark Souls III runtime saves use
.co2, and DS2 private-server saves use .sl3.

== Online unlock summary ==

Dark Souls I:
  - Full-session seamless co-op from tutorial to final boss.
  - Death, boss clears, and area clears no longer end the co-op session.
  - Multiplayer fog walls and zone barriers are removed.
  - NPC dialogue, talk events, bonfire world resets, and progression sync are
    handled by the included runtime.
  - Up to six players can share the open world.
  - Reconnects are streamlined, light sources sync locally, scaling is
    configurable, and invasions are supported when enabled in settings.
  - Hosting starts a visible Bonfire DS1 coordinator server for the selected
    fire. It keeps the local profile, keys, listing state, and diagnostics live
    while the included DS1 runtime owns the Steam P2P co-op transport.

Dark Souls II:
  - Multiplayer timers are removed.
  - Multiplayer fog gates and zone barriers are removed.
  - New characters receive the multiplayer tools at the start of the game.
  - Existing characters can obtain the tools through Maughlin the Armourer's
    shop.
  - Server routing, keys, save extension, and unlock data are applied by
    Bonfire during launch.

Dark Souls III:
  - Full-session seamless co-op from tutorial to final boss.
  - Death, boss clears, and area clears no longer end the co-op session.
  - Multiplayer fog walls and zone barriers are removed.
  - NPC dialogue, talk events, bonfire world resets, and progression sync are
    handled by the included runtime.
  - Up to six players can share the open world.
  - Reconnects are streamlined, scaling is configurable, boss-room bonfire
    handling is included, and invasions are supported when enabled in settings.

Full guide:
https://github.com/Galidar/DSSeamlessCoop
