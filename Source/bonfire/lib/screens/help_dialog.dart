/// In-app guide. Two stories side by side: how to JOIN a public bonfire
/// (the 90% case for new players) and how to LIGHT YOUR OWN, i.e. host a
/// dedicated server. Souls-themed copy, but every step is concrete — what
/// to click, what to expect, what can go wrong.

import 'dart:io';
import 'package:flutter/material.dart';

import '../design.dart';
import '../palette.dart';
import '../theme.dart';
import '../widgets/animated_flame.dart';

Future<void> showBonfireHelp(BuildContext context) {
  return showDialog(
    context: context,
    builder: (_) => const _HelpDialog(),
  );
}

class _HelpDialog extends StatefulWidget {
  const _HelpDialog();
  @override
  State<_HelpDialog> createState() => _HelpDialogState();
}

class _HelpDialogState extends State<_HelpDialog>
    with SingleTickerProviderStateMixin {
  late final TabController _tab = TabController(length: 2, vsync: this);

  @override
  void dispose() {
    _tab.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Dialog(
      backgroundColor: p.surface,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(R.md)),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 720, maxHeight: 640),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(Sp.xl, Sp.xl, Sp.xl, Sp.lg),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const AnimatedFlame(size: 28),
                  const SizedBox(width: Sp.md),
                  Text('Lore of the Bonfire',
                      style: TextStyle(
                          fontSize: 20,
                          fontWeight: FontWeight.w600,
                          letterSpacing: 0.4,
                          color: p.textPrimary)),
                ],
              ),
              const SizedBox(height: Sp.md),
              Container(
                decoration: BoxDecoration(
                  border: Border(bottom: BorderSide(color: p.border)),
                ),
                child: TabBar(
                  controller: _tab,
                  isScrollable: false,
                  indicatorColor: BonfireColors.accent,
                  indicatorWeight: 2,
                  labelColor: p.textPrimary,
                  unselectedLabelColor: p.textMuted,
                  labelStyle: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w600,
                      letterSpacing: 0.5),
                  tabs: const [
                    Tab(text: 'TRAVEL TO A FIRE'),
                    Tab(text: 'LIGHT YOUR OWN'),
                  ],
                ),
              ),
              const SizedBox(height: Sp.md),
              Expanded(
                child: TabBarView(
                  controller: _tab,
                  children: const [
                    _JoinerGuide(),
                    _HostGuide(),
                  ],
                ),
              ),
              const SizedBox(height: Sp.sm),
              Align(
                alignment: Alignment.centerRight,
                child: TextButton(
                  onPressed: () => Navigator.of(context).pop(),
                  child: const Text('Close'),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ────────────────────────────────────────────────────────────────────
//  TRAVEL TO A FIRE — for players who just want to play online
// ────────────────────────────────────────────────────────────────────

class _JoinerGuide extends StatelessWidget {
  const _JoinerGuide();
  @override
  Widget build(BuildContext context) {
    return _Scroll([
      _Lead(
        'You only want to play. Someone else is already keeping the fire '
        'lit — you just travel to it.',
      ),
      _Step(
        n: 1,
        title: 'Have the game on Steam',
        body: 'Buy and install Dark Souls Remastered, Dark Souls II: Scholar '
            'of the First Sin, or Dark Souls III on Steam. Bonfire finds the .exe automatically '
            'from your Steam library — no path picking by hand.',
      ),
      _Step(
        n: 2,
        title: 'Open Bonfire (admin)',
        body: 'Windows asks for admin permission. Say yes — Bonfire needs it '
            'to inject the redirect code into the game so it talks to the '
            'unofficial server instead of the (long-shut-down) retail one.',
      ),
      _Step(
        n: 3,
        title: 'Pick your tab',
        body:
            'Dark Souls Remastered, Dark Souls II, or Dark Souls III at the top. Bonfire shows the '
            'public fires of that game in the PUBLIC BONFIRES list, '
            'refreshed live from the master server.',
      ),
      _Step(
        n: 4,
        title: 'Travel to a fire',
        body: 'Click "Travel to this fire" on any unsealed (open) bonfire. '
            'Sealed ones need a password — ask the host. Bonfire fetches '
            'the server\'s key, launches the game and patches it on the '
            'fly. The first time the game tries to go online you should '
            'see "Welcome to DSOS" — you\'re in.',
      ),
      _Section('Common pitfalls'),
      _Bullet(
        'Steam must be running and you must be logged in. The "Light the '
        'bonfire" / "Travel" buttons are disabled until that\'s true.',
      ),
      _Bullet(
        'If a sealed (locked icon) bonfire wants a password, type it in '
        'the prompt — it never touches your Steam account.',
      ),
      _Bullet(
        'Joining does NOT install the local server, configure your '
        'firewall, or open any inbound ports. None of that is needed for '
        'a player.',
      ),
      _Bullet(
        'The game will use a separate save (.ds3os) by default, kept apart '
        'from your retail save. Toggle this in Game Settings if you really '
        'want them mixed (not recommended).',
      ),
    ]);
  }
}

// ────────────────────────────────────────────────────────────────────
//  LIGHT YOUR OWN — for hosts who want a dedicated bonfire
// ────────────────────────────────────────────────────────────────────

class _HostGuide extends StatelessWidget {
  const _HostGuide();
  @override
  Widget build(BuildContext context) {
    return _Scroll([
      _Lead(
        'You want to host. Other Undead will see your fire in the public '
        'list (or you keep it private and share the password).',
      ),
      _Step(
        n: 1,
        title: 'Kindle a new bonfire',
        body:
            'In the tab matching the game you want to host (DS II or DS III), '
            'click "+ New bonfire" at the top right. Type a name. Bonfire '
            'creates a profile and, the first time, downloads the server '
            'binaries (~115 MB) into the install folder.',
      ),
      _Step(
        n: 2,
        title: 'Apply firewall rules',
        body: 'During first install Bonfire offers to add Windows Firewall '
            'rules (TCP+UDP on 50000/50010/50020/50050) and accept '
            'inbound for Server.exe. Accept the UAC prompt — without these '
            'rules outside players can\'t reach you.',
      ),
      _Step(
        n: 3,
        title: 'Tend the flame',
        body: 'Open Tend the flame to set: server name, description, '
            'optional password (locks your bonfire — only people with the '
            'password can join), and the WAN/LAN IPs. Auto-detect is '
            'usually right; switch to Manual only if you\'re behind paid '
            'hosting / a VPN. Also configure the WebUI admin '
            'credentials so you can log into the dashboard later.',
      ),
      _Step(
        n: 4,
        title: 'Light the bonfire',
        body: 'The button on your profile row starts the local Server.exe, '
            'registers it with the master server (so others see it in '
            'their public list), and launches your game pointed at it. '
            'You can play on your own server — being the host doesn\'t '
            'forbid joining yourself.',
      ),
      _Step(
        n: 5,
        title: 'Manage from the WebUI',
        body: 'http://localhost:50005/ shows live stats, players online, '
            'matchmaking knobs, and a ban list. Use the credentials you '
            'set in Tend the flame.',
      ),
      _Section('What hosting needs'),
      _Bullet(
        'Steam running on the host machine — Server.exe validates each '
        'player\'s Steam ticket through the Steam GameServer SDK.',
      ),
      _Bullet(
        'A real public IP, OR port-forwarding from your router for TCP+UDP '
        '50000 / 50010 / 50050 / 50020 to the host machine. Bonfire\'s '
        'firewall step only handles the local machine — your router is '
        'still on you.',
      ),
      _Bullet(
        'Letting Bonfire run as long as you want the bonfire lit. Closing '
        'Bonfire shuts down Server.exe and players get disconnected.',
      ),
      _Section('Multiple bonfires'),
      _Bullet(
        'Make as many profiles as you want — each one is a separate '
        'config / keypair / database. Light the bonfire on a different '
        'profile and Bonfire stops the running server, swaps the files, '
        'and starts again for the new game type. Only one profile is '
        'live at a time (Server.exe binds fixed ports).',
      ),
    ]);
  }
}

// ────────────────────────────────────────────────────────────────────
//  Layout primitives — keep the two guides visually consistent.
// ────────────────────────────────────────────────────────────────────

class _Scroll extends StatelessWidget {
  final List<Widget> children;
  const _Scroll(this.children);
  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      padding: const EdgeInsets.only(right: Sp.sm),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          ...children.expand((c) => [c, const SizedBox(height: Sp.md)]).toList()
            ..removeLast(),
        ],
      ),
    );
  }
}

class _Lead extends StatelessWidget {
  final String text;
  const _Lead(this.text);
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Container(
      padding: const EdgeInsets.all(Sp.md),
      decoration: BoxDecoration(
        color: p.surfaceHi,
        borderRadius: BorderRadius.circular(R.sm),
        border: Border.all(color: BonfireColors.accentDim),
      ),
      child: Text(
        text,
        style: BT.body.copyWith(color: p.textPrimary, height: 1.5),
      ),
    );
  }
}

class _Step extends StatelessWidget {
  final int n;
  final String title;
  final String body;
  const _Step({required this.n, required this.title, required this.body});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          width: 28,
          height: 28,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            color: BonfireColors.accentDim,
            borderRadius: BorderRadius.circular(R.pill),
          ),
          child: Text('$n',
              style: TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w700,
                  color: p.textPrimary)),
        ),
        const SizedBox(width: Sp.md),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: BT.heading.copyWith(color: p.textPrimary)),
              const SizedBox(height: 4),
              Text(body,
                  style: BT.body.copyWith(color: p.textSecondary, height: 1.5)),
            ],
          ),
        ),
      ],
    );
  }
}

class _Section extends StatelessWidget {
  final String text;
  const _Section(this.text);
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Padding(
      padding: const EdgeInsets.only(top: Sp.sm, bottom: 4),
      child: Text(text.toUpperCase(),
          style: BT.eyebrow.copyWith(color: p.textMuted)),
    );
  }
}

class _Bullet extends StatelessWidget {
  final String text;
  const _Bullet(this.text);
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Padding(
      padding: const EdgeInsets.only(left: Sp.xs, top: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 7, right: Sp.sm),
            child: Container(
              width: 5,
              height: 5,
              decoration: const BoxDecoration(
                color: BonfireColors.accent,
                shape: BoxShape.circle,
              ),
            ),
          ),
          Expanded(
            child: Text(text,
                style: BT.body.copyWith(color: p.textSecondary, height: 1.5)),
          ),
        ],
      ),
    );
  }
}

// Quick-and-dirty: open a URL in the default browser. Used by future
// links from the guide (none yet, but the dialog has space for them).
// ignore: unused_element
void _openUrl(String url) {
  try {
    if (Platform.isWindows) {
      Process.start('cmd', ['/c', 'start', '', url], runInShell: true);
    } else if (Platform.isMacOS) {
      Process.start('open', [url]);
    } else {
      Process.start('xdg-open', [url]);
    }
  } catch (_) {}
}
