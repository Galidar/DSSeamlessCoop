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

== What is included ==

  Bonfire.exe              Flutter desktop interface.
  BonfireService.exe       Backend service used by the interface.
  flutter_windows.dll      Flutter runtime.
  data/                    Flutter assets and compiled Dart code.
  Server/                  Private multiplayer server and WebUI files.
  Loader/                  Runtime bridge and game online unlock data.
    DS1SeamlessCoop/       Dark Souls I packaged runtime payload.
    DS2SeamlessCoop/       Dark Souls II packaged runtime payload.
    DS3SeamlessCoop/       Dark Souls III packaged runtime payload.
  Prerequisites/           Visual C++ redistributables.

The supported online runtime data is already included. Users do not need extra
files after downloading this release.

Dark Souls I and Dark Souls III may create a SeamlessCoop/ folder inside the
game directory when launched through Bonfire. That is expected runtime staging
and should not be removed while the game is running.

Keep separate saves enabled. Dark Souls I and Dark Souls III runtime saves use
.co2, and DS2 private-server saves use .sl3.

Full guide:
https://github.com/Galidar/DSSeamlessCoop
