# Setup - DS3OS for Dark Souls 2 SOTFS

This folder contains everything needed to install the DS3OS server for **Dark Souls II: Scholar of the First Sin** on any Windows machine. **IPs are detected automatically** — nothing needs to be edited by hand.

This setup is part of [Galidar/DSSeamlessCoop](https://github.com/Galidar/DSSeamlessCoop), a fork of the original [TLeonardUK/ds3os](https://github.com/TLeonardUK/ds3os) project. All credit for the underlying server goes to TLeonardUK and contributors.

## Quick install (on a fresh PC)

1. **Clone the repo:**
   ```
   git clone https://github.com/Galidar/DSSeamlessCoop.git
   cd DSSeamlessCoop\Setup
   ```

2. **Run `setup.bat`** (double click) — it automatically:
   - Detects your public IP (via https://api.ipify.org) and your local IP
   - Downloads the latest fork release (built by GitHub Actions)
   - Injects your IPs into the final `config.json`
   - Leaves everything ready to launch

3. **Configure the firewall** — Right click → **Run as administrator** on `1-Setup-Firewall.bat`

4. **Start the server** — Double click `2-Start-Server.bat`

5. **Launch the game** — Right click → **Run as administrator** on `3-Start-Loader.bat`
   - Set the path: `C:\Program Files (x86)\Steam\steamapps\common\Dark Souls II Scholar of the First Sin\Game\DarkSoulsII.exe`
   - Select your server → **Launch Game**

## Files

| File | Purpose |
|---|---|
| `setup.bat` | Automatic installer (detects IPs, downloads binaries, applies config) |
| `config.json` | Configuration template with `__WAN_IP__` / `__LAN_IP__` placeholders |
| `Update-IPs.bat` | Refreshes IPs in config when your public IP changes |
| `1-Setup-Firewall.bat` | Creates Windows Firewall rules (admin) |
| `2-Start-Server.bat` | Starts the server |
| `3-Start-Loader.bat` | Starts the game Loader (admin) |
| `4-Stop-Server.bat` | Stops the server |

## How does setup.bat handle IPs?

The `config.json` template uses placeholders instead of real IPs:
```json
"ServerHostname": "__WAN_IP__",
"ServerPrivateHostname": "__LAN_IP__",
```

When you run `setup.bat`, it:
1. Fetches your public IP with `Invoke-RestMethod -Uri 'https://api.ipify.org'`
2. Fetches your local IP with `Get-NetIPAddress` (filtering DHCP/Manual, excluding loopback and APIPA)
3. Replaces the placeholders and writes the result to `Server\Saved\default\config.json`

That means **the repo never exposes any private/public IP** and the config is **portable** — it works on any network without manual edits.

## If your public IP changes

Residential ISPs often rotate the public IP. When it happens:

1. Double click `Update-IPs.bat`
2. Stop and restart the server (`4-Stop-Server.bat` → `2-Start-Server.bat`)

## Current configuration

- **GameType:** DarkSouls2
- **Server Name:** Diux DS2 SOTFS Server
- **TCP Ports:** 50000 (auth), 50050 (login), 50005 (WebUI)
- **UDP Port:** 50010 (game)
- **WebUI:** http://localhost:50005

## Notes

- Steam must be **running** while the server is up (no login required).
- The Loader **requires administrator privileges** (it patches the game's memory).
- DSOS saves are separate from the retail ones (`.ds3os` vs `.sl2`) — anti-ban protection.
- Server keys (`private.key`, `public.key`) are generated on first launch and are NOT in the repo for security reasons.
- If you want others to connect from outside your network, forward TCP+UDP ports 50000, 50010, 50050, 50020 and 50060–50200 on your **router** to your local IP.

## Credits

- Original project: [TLeonardUK/ds3os](https://github.com/TLeonardUK/ds3os) (MIT License)
- Support Discord: https://discord.gg/pBmquc9Jkj
