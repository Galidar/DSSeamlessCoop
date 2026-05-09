/// Design system primitives — spacing, radii, type, icon sizes.
///
/// Use these constants everywhere. Magic numbers are forbidden in screen
/// code: every gap, every corner, every text size goes through here.

import 'package:flutter/material.dart';
import 'theme.dart';

// ───── Spacing (8px base) ─────
class Sp {
  static const double xs = 4;
  static const double sm = 8;
  static const double md = 12;
  static const double lg = 16;
  static const double xl = 24;
  static const double xxl = 32;
  static const double huge = 48;

  static const SizedBox gapXs = SizedBox(width: xs, height: xs);
  static const SizedBox gapSm = SizedBox(width: sm, height: sm);
  static const SizedBox gapMd = SizedBox(width: md, height: md);
  static const SizedBox gapLg = SizedBox(width: lg, height: lg);
  static const SizedBox gapXl = SizedBox(width: xl, height: xl);
}

// ───── Radii — sparing, deliberate ─────
class R {
  static const double none = 0;
  static const double xs = 2;
  static const double sm = 4;
  static const double md = 6;
  static const double pill = 999;
}

// ───── Icon sizes ─────
class IS {
  static const double sm = 14;
  static const double md = 18;
  static const double lg = 22;
  static const double xl = 32;
  static const double hero = 56;
}

// ───── Type scale (loosely 1.2 ratio) ─────
class BT {
  // Display — for "BONFIRE" wordmark, hero numbers
  static const TextStyle display = TextStyle(
      fontSize: 28,
      fontWeight: FontWeight.w600,
      letterSpacing: 1.2,
      color: BonfireColors.textPrimary);

  // Title — page heading
  static const TextStyle title = TextStyle(
      fontSize: 18,
      fontWeight: FontWeight.w600,
      letterSpacing: 0.4,
      color: BonfireColors.textPrimary);

  // Heading — section heading inside a panel
  static const TextStyle heading = TextStyle(
      fontSize: 15,
      fontWeight: FontWeight.w600,
      color: BonfireColors.textPrimary);

  // Body — default reading size
  static const TextStyle body =
      TextStyle(fontSize: 13, height: 1.4, color: BonfireColors.textPrimary);

  // Body secondary — descriptions, hints
  static const TextStyle bodyMuted =
      TextStyle(fontSize: 13, height: 1.4, color: BonfireColors.textSecondary);

  // Caption — small print, status text
  static const TextStyle caption =
      TextStyle(fontSize: 11, color: BonfireColors.textMuted);

  // Eyebrow — section label above content (UPPERCASE TRACKED)
  static const TextStyle eyebrow = TextStyle(
      fontSize: 10,
      letterSpacing: 1.6,
      fontWeight: FontWeight.w700,
      color: BonfireColors.textMuted);

  // Mono — IPs, ports, identifiers
  static const TextStyle mono = TextStyle(
      fontSize: 12, fontFamily: 'Consolas', color: BonfireColors.textPrimary);

  static const TextStyle monoMuted = TextStyle(
      fontSize: 11, fontFamily: 'Consolas', color: BonfireColors.textMuted);

  // Button — labels on filled / outlined buttons
  static const TextStyle button =
      TextStyle(fontSize: 13, fontWeight: FontWeight.w600, letterSpacing: 0.3);
}

// ───── Souls-themed copy ─────
class Lore {
  // Generic
  static const String appName = 'Bonfire';
  static const String tagline = 'Dark Souls Open Server';

  // Greeting / loading
  static const String boot = 'Lighting the bonfire…';
  static const String welcome = 'Welcome, Unkindled';
  static const String welcomeBody =
      'Bonfire kindles a private Dark Souls server on this machine — '
      'firewall, network, configuration, all in one window. No commands. '
      'No terminals.';

  // Server status
  static String yourServer({required bool running, int? pid}) =>
      running ? 'Your bonfire · kindled · PID $pid' : 'Your bonfire · unlit';
  static const String yourServerRunning = 'Kindled';
  static const String yourServerIdle = 'Unlit';

  // Sections
  static const String publicListSection = 'PUBLIC BONFIRES';
  static const String yourServerLabel = 'YOUR BONFIRE';

  // Empty / error
  static String noFires(String gameLabel) =>
      'No fires burning in $gameLabel right now.';
  static const String masterUnreachable =
      'The master keeper does not answer. Check your connection or try again.';
  static const String queryingMaster = 'Reaching out to the master keeper…';
  static String fireCount(int n) => n == 1 ? '1 bonfire' : '$n bonfires';

  // Actions
  static const String installServer = 'Kindle your bonfire';
  static const String updateServer = 'Re-kindle (update build)';
  static const String startServer = 'Light the bonfire';
  static const String stopServer = 'Snuff the flame';
  static const String launchGame = 'Travel to this fire';
  static const String configure = 'Tend the flame';
  static const String applyFirewall = 'Open the gates';
  static const String selectPrompt = 'Choose a bonfire above to travel to it.';

  // Setup flow
  static const String setupComplete = 'The flame is yours';
  static const String downloadHeading = 'Receive the embers';
  static const String firewallHeading = 'Open the gates';
  static const String networkHeading = 'Mark the road home';
  static const String settingsHeading = 'Inscribe the flame';

  // Game labels
  static const String ds1 = 'Dark Souls I';
  static const String ds2 = 'Dark Souls II';
  static const String ds3 = 'Dark Souls III';

  static String gameLabel(String gameType) {
    if (gameType == 'DarkSouls1') return ds1;
    if (gameType == 'DarkSouls3') return ds3;
    return ds2;
  }

  static String gameExeName(String gameType) {
    if (gameType == 'DarkSouls1') return 'DarkSoulsRemastered.exe';
    if (gameType == 'DarkSouls3') return 'DarkSoulsIII.exe';
    return 'DarkSoulsII.exe';
  }

  static String gameExeHint(String gameType) {
    if (gameType == 'DarkSouls1') {
      return r'C:\Program Files (x86)\Steam\steamapps\common\DARK SOULS REMASTERED\DarkSoulsRemastered.exe';
    }
    if (gameType == 'DarkSouls3') {
      return r'C:\Program Files (x86)\Steam\steamapps\common\DARK SOULS III\Game\DarkSoulsIII.exe';
    }
    return r'C:\Program Files (x86)\Steam\steamapps\common\Dark Souls II Scholar of the First Sin\Game\DarkSoulsII.exe';
  }
}
