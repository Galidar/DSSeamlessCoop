-------------------------------------------------------------------------
 DSSeamlessCoop / Bonfire — Galidar fork of TLeonardUK/ds3os
 https://github.com/Galidar/DSSeamlessCoop
-------------------------------------------------------------------------

Before running anything, install the C++ runtimes from the Prerequisites/
folder. Bonfire and Server.exe may fail to start without them.

== How to use ==

1. Run Bonfire.exe.
   Windows will ask for admin permission — accept it. Bonfire needs admin
   to inject Bonfire's code into the Dark Souls process at launch (this is
   how the game gets pointed at the unofficial server instead of the
   shut-down retail one).

2. Pick the Dark Souls II or Dark Souls III tab.

3. Click "+ New bonfire", give the profile a name, and follow the install
   steps. Bonfire downloads the server, applies the firewall rules, and
   detects your WAN/LAN IPs automatically.

4. Click "Light the bonfire" on your profile. Bonfire starts the local
   server and launches the game pointed at it.

Steam must be running while the server is up. No Steam login is required
on the server side.

== What's in this folder ==

  Bonfire.exe              Flutter desktop app (the launcher)
  BonfireService.exe       Backend the launcher talks to (admin)
  flutter_windows.dll      Flutter runtime
  data/                    Flutter assets and compiled Dart code
  Server/                  The actual unofficial game server (Server.exe)
                           and its WebUI/static files. Server.exe writes
                           Saved/default/config.json on first run.
  Loader/                  Injector.dll (the C++ DLL injected into the
                           game). Despite the folder name, the legacy
                           Loader.exe is no longer shipped — Bonfire
                           replaces it.
  Prerequisites/           Visual C++ redistributables.

For the full setup guide and troubleshooting, see the README on GitHub:
https://github.com/Galidar/DSSeamlessCoop

This fork stands on the shoulders of the upstream DS3OS project by
TLeonardUK and contributors. Please support the original:
https://github.com/TLeonardUK/ds3os
