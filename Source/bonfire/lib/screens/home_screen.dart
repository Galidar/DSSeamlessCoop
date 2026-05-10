/// Bonfire — main screen.
///
/// One window, one screen, no detours. Hierarchy:
///   - Header: brand + global server status (one place only)
///   - Tab strip: DS II / DS III + search + refresh
///   - Body: your server (pinned, compact strip — expandable) + public list
///   - Footer: contextual action bar (Travel to fire / Update build)
///
/// All padding/radius/typography goes through `design.dart` constants.
/// All user-facing strings go through `Lore` so the Souls voice is cohesive.

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import 'dart:io';

import '../design.dart';
import '../palette.dart';
import '../state/app_state.dart';
import '../theme.dart';
import '../widgets/animated_flame.dart';
import '../widgets/bonfire_card.dart';
import '../widgets/brand_mark.dart';
import '../widgets/server_badges.dart';
import '../widgets/status_dot.dart';
import 'about_dialog.dart';
import 'config_panel.dart';
import 'game_settings_panel.dart';
import 'help_dialog.dart';
import 'install_panel.dart';

class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});
  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  bool _busy = true;
  String? _error;
  final FocusNode _kbFocus = FocusNode();

  @override
  void initState() {
    super.initState();
    _bootstrap();
  }

  @override
  void dispose() {
    _kbFocus.dispose();
    super.dispose();
  }

  Future<void> _bootstrap() async {
    try {
      await context.read<AppState>().refreshAll();
      setState(() => _busy = false);
    } catch (e) {
      setState(() {
        _busy = false;
        _error = e.toString();
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: SafeArea(
        child: Focus(
          focusNode: _kbFocus,
          autofocus: true,
          onKeyEvent: _handleKey,
          child: _busy
              ? const _LoadingState()
              : (_error != null
                  ? _ErrorState(message: _error!)
                  : const _Content()),
        ),
      ),
    );
  }

  KeyEventResult _handleKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent) return KeyEventResult.ignored;
    final app = context.read<AppState>();

    // Esc — clear selection (any time).
    if (event.logicalKey == LogicalKeyboardKey.escape) {
      if (app.selectedServerId != null) {
        app.selectServer(null);
        return KeyEventResult.handled;
      }
    }

    // F5 / Ctrl+R — refresh.
    final ctrl = HardwareKeyboard.instance.isControlPressed;
    if (event.logicalKey == LogicalKeyboardKey.f5 ||
        (ctrl && event.logicalKey == LogicalKeyboardKey.keyR)) {
      app.refreshPublicServers();
      return KeyEventResult.handled;
    }

    // / — focus the search box (Twitter / GitHub style).
    if (event.logicalKey == LogicalKeyboardKey.slash) {
      _searchFocus.requestFocus();
      return KeyEventResult.handled;
    }

    // ↑ / ↓ — navigate the public list.
    final servers = app.publicServers;
    if (servers == null || servers.isEmpty) return KeyEventResult.ignored;

    int idx = -1;
    if (app.selectedServerId != null) {
      idx = servers.indexWhere((s) => s.id == app.selectedServerId);
    }
    if (event.logicalKey == LogicalKeyboardKey.arrowDown) {
      idx = (idx + 1).clamp(0, servers.length - 1);
      app.selectServer(servers[idx].id);
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.arrowUp) {
      idx = idx <= 0 ? 0 : idx - 1;
      app.selectServer(servers[idx].id);
      return KeyEventResult.handled;
    }
    return KeyEventResult.ignored;
  }
}

// Shared focus node so Ctrl+/ can grab the search box from the home screen.
final FocusNode _searchFocus = FocusNode();

// ────────────── Loading / Error ──────────────

class _LoadingState extends StatelessWidget {
  const _LoadingState();
  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: const [
          SizedBox(
              width: 28,
              height: 28,
              child: CircularProgressIndicator(strokeWidth: 2)),
          Sp.gapMd,
          Text(Lore.boot, style: BT.bodyMuted),
        ],
      ),
    );
  }
}

class _ErrorState extends StatelessWidget {
  final String message;
  const _ErrorState({required this.message});
  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(Sp.xxl),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.error_outline,
                color: BonfireColors.err, size: IS.xl),
            Sp.gapMd,
            const Text('Could not contact the Bonfire keeper.',
                style: BT.heading),
            Sp.gapXs,
            Text(message, textAlign: TextAlign.center, style: BT.caption),
          ],
        ),
      ),
    );
  }
}

// ────────────── Content ──────────────

class _Content extends StatelessWidget {
  const _Content();
  @override
  Widget build(BuildContext context) {
    return PaletteScope(
      palette: Palette.dark,
      child: ColoredBox(
        color: Palette.dark.bg,
        child: Column(
          children: const [
            _Header(),
            _TabBar(),
            Expanded(child: _ServerListView()),
            _BottomBar(),
          ],
        ),
      ),
    );
  }
}

// ────────────── Header ──────────────

class _Header extends StatelessWidget {
  const _Header();
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return LayoutBuilder(
      builder: (context, constraints) {
        final compact = constraints.maxWidth < 820;
        final headerHeight = compact ? 112.0 : 170.0;
        return Container(
          height: headerHeight,
          decoration: BoxDecoration(
            color: BonfireColors.bg,
            border: Border(bottom: BorderSide(color: p.border)),
          ),
          child: Stack(
            fit: StackFit.expand,
            children: [
              Image.asset(
                BrandAssets.headerBanner,
                fit: BoxFit.contain,
                alignment: Alignment.center,
                filterQuality: FilterQuality.medium,
              ),
              const DecoratedBox(
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    begin: Alignment.centerLeft,
                    end: Alignment.centerRight,
                    colors: [
                      Color(0xE8030303),
                      Color(0x30030303),
                      Color(0x00030303),
                      Color(0x00030303),
                      Color(0x30030303),
                      Color(0xE8030303),
                    ],
                    stops: [0.0, 0.12, 0.26, 0.74, 0.88, 1.0],
                  ),
                ),
              ),
              const DecoratedBox(
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    begin: Alignment.topCenter,
                    end: Alignment.bottomCenter,
                    colors: [
                      Color(0x10000000),
                      Color(0x00000000),
                      Color(0x38000000),
                    ],
                  ),
                ),
              ),
              Padding(
                padding: EdgeInsets.fromLTRB(
                  compact ? Sp.lg : Sp.xl,
                  Sp.md,
                  compact ? Sp.lg : Sp.xl,
                  Sp.md,
                ),
                child: Row(
                  children: [
                    BrandMark(size: compact ? 42 : 54),
                    const SizedBox(width: Sp.md),
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(
                          Lore.appName,
                          style: (compact ? BT.title : BT.display).copyWith(
                            color: p.textPrimary,
                            letterSpacing: 0,
                            shadows: const [
                              Shadow(
                                  color: Colors.black,
                                  blurRadius: 12,
                                  offset: Offset(0, 1)),
                            ],
                          ),
                        ),
                        Text(
                          Lore.tagline,
                          style: BT.caption.copyWith(
                            color: p.textSecondary,
                            shadows: const [
                              Shadow(color: Colors.black, blurRadius: 8),
                            ],
                          ),
                        ),
                      ],
                    ),
                    const Spacer(),
                    // Per-profile status + actions live in the MY BONFIRES rows;
                    // header is reserved for app-wide affordances only.
                    _IconBtn(
                      icon: Icons.help_outline,
                      tooltip: 'How to use Bonfire',
                      onTap: () => showBonfireHelp(context),
                    ),
                    _IconBtn(
                      icon: Icons.tune,
                      tooltip: 'Game settings',
                      onTap: () => GameSettingsPanel.show(context),
                    ),
                    _IconBtn(
                      icon: Icons.info_outline,
                      tooltip: 'About Bonfire',
                      onTap: () => showBonfireAbout(context),
                    ),
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}

class _IconBtn extends StatelessWidget {
  final IconData icon;
  final String tooltip;
  final VoidCallback onTap;
  const _IconBtn(
      {required this.icon, required this.tooltip, required this.onTap});
  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: tooltip,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(R.pill),
        child: const SizedBox(
          width: 28,
          height: 28,
        ).withChild(Icon(icon, size: IS.md, color: BonfireColors.accent)),
      ),
    );
  }
}

extension _BoxChild on SizedBox {
  Widget withChild(Widget child) => SizedBox(
        width: width,
        height: height,
        child: Center(child: child),
      );
}

// ────────────── Tab strip ──────────────

class _TabBar extends StatefulWidget {
  const _TabBar();
  @override
  State<_TabBar> createState() => _TabBarState();
}

class _TabBarState extends State<_TabBar> {
  late final TextEditingController _searchCtrl;

  @override
  void initState() {
    super.initState();
    _searchCtrl = TextEditingController();
  }

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final p = Palette.of(context);
    return Container(
      decoration: BoxDecoration(
        color: p.surface,
        border: Border(bottom: BorderSide(color: p.border)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: Sp.md),
      child: Row(
        children: [
          _TabItem(
            label: Lore.ds1,
            selected: app.publicListGameFilter == 'DarkSouls1',
            onTap: () => app.setGameFilter('DarkSouls1'),
          ),
          _TabItem(
            label: Lore.ds2,
            selected: app.publicListGameFilter == 'DarkSouls2',
            onTap: () => app.setGameFilter('DarkSouls2'),
          ),
          _TabItem(
            label: Lore.ds3,
            selected: app.publicListGameFilter == 'DarkSouls3',
            onTap: () => app.setGameFilter('DarkSouls3'),
          ),
          const Spacer(),
          // Search bar — for filtering long server lists.
          SizedBox(
            width: 240,
            height: 32,
            child: TextField(
              controller: _searchCtrl,
              focusNode: _searchFocus,
              style: BT.body.copyWith(color: p.textPrimary),
              onChanged: (v) => app.setSearchQuery(v),
              decoration: InputDecoration(
                hintText: 'Filter…  (press / to focus)',
                hintStyle:
                    BT.bodyMuted.copyWith(fontSize: 12, color: p.textMuted),
                prefixIcon: Icon(Icons.search, size: IS.md, color: p.textMuted),
                isDense: true,
                contentPadding: const EdgeInsets.symmetric(vertical: Sp.sm),
                border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(R.sm),
                    borderSide: BorderSide(color: p.border)),
                enabledBorder: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(R.sm),
                    borderSide: BorderSide(color: p.border)),
              ),
            ),
          ),
          const SizedBox(width: Sp.sm),
          _IconBtn(
            icon: Icons.refresh,
            tooltip: 'Refresh (F5)',
            onTap: () => app.refreshPublicServers(),
          ),
          const SizedBox(width: Sp.xs),
        ],
      ),
    );
  }
}

class _TabItem extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;
  const _TabItem(
      {required this.label, required this.selected, required this.onTap});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: Sp.lg, vertical: 14),
          decoration: BoxDecoration(
            border: Border(
              bottom: BorderSide(
                color: selected ? p.accent : Colors.transparent,
                width: 2,
              ),
            ),
          ),
          child: Text(
            label,
            style: BT.button.copyWith(
              color: selected ? p.textPrimary : p.textMuted,
            ),
          ),
        ),
      ),
    );
  }
}

// ────────────── List ──────────────

class _ServerListView extends StatelessWidget {
  const _ServerListView();

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final installed = app.installStatus?.installed ?? false;

    // Profiles for the currently selected game tab — these become the
    // user's own bonfires that appear above the public list.
    final myProfiles = app.profiles
        .where((p) => p.gameType == app.publicListGameFilter)
        .toList();

    final all = app.publicServers ?? const <PublicServer>[];
    // Dedupe: hide any public-list entry whose name matches one of the
    // user's local bonfires for this game (their advertised server shouldn't
    // appear twice).
    final myNames = myProfiles
        .map((p) => p.name.trim().toLowerCase())
        .where((n) => n.isNotEmpty)
        .toSet();
    final deduped = myNames.isEmpty
        ? all
        : all
            .where((s) => !myNames.contains(s.name.trim().toLowerCase()))
            .toList();

    // Apply optional filters: hide sealed (passworded) + minimum player count.
    final afterFilters = deduped
        .where((s) => !app.hideSealed || !s.passwordRequired)
        .where((s) => s.playerCount >= app.minPlayers)
        .toList();

    final filtered = app.searchQuery.isEmpty
        ? afterFilters
        : afterFilters
            .where((s) =>
                s.name.toLowerCase().contains(app.searchQuery.toLowerCase()) ||
                s.description
                    .toLowerCase()
                    .contains(app.searchQuery.toLowerCase()))
            .toList();

    return ListView(
      padding: const EdgeInsets.fromLTRB(Sp.xl, Sp.lg, Sp.xl, Sp.lg),
      children: [
        // ───── MY BONFIRES ─────
        Builder(builder: (ctx) {
          final p = Palette.of(ctx);
          return Row(
            children: [
              Text('MY BONFIRES',
                  style: BT.eyebrow.copyWith(color: p.textMuted)),
              const SizedBox(width: Sp.sm),
              Text('· ${myProfiles.length}',
                  style: BT.caption.copyWith(color: p.textMuted)),
              const Spacer(),
              if (installed) _KindleButton(gameType: app.publicListGameFilter),
            ],
          );
        }),
        const SizedBox(height: Sp.sm),
        if (!installed)
          _NotInstalledCard()
        else if (myProfiles.isEmpty)
          _NoProfilesCard(gameType: app.publicListGameFilter)
        else
          ...myProfiles.map((profile) => _LocalBonfireRow(profile: profile)),

        const SizedBox(height: Sp.xl),

        // ───── PUBLIC BONFIRES ─────
        Builder(builder: (ctx) {
          final p = Palette.of(ctx);
          return Row(
            children: [
              Text(Lore.publicListSection,
                  style: BT.eyebrow.copyWith(color: p.textMuted)),
              const SizedBox(width: Sp.sm),
              if (app.publicServersLoading)
                const SizedBox(
                    width: 12,
                    height: 12,
                    child: CircularProgressIndicator(strokeWidth: 1.5))
              else if (app.publicServers != null)
                Text(Lore.fireCount(filtered.length),
                    style: BT.caption.copyWith(color: p.textMuted)),
              const Spacer(),
              const _FilterBar(),
            ],
          );
        }),
        const SizedBox(height: Sp.sm),

        if (app.publicServers == null && !app.publicServersLoading)
          _MasterUnreachableCard()
        else if (app.publicServersLoading && all.isEmpty)
          ...List.generate(5, (_) => const _SkeletonRow())
        else if (filtered.isEmpty)
          _EmptyListCard(
              query: app.searchQuery, gameTab: app.publicListGameFilter)
        else
          ...filtered.map((s) => _PublicServerRow(server: s)),
      ],
    );
  }
}

// ────────── Your bonfires section: local profile rows ──────────

class _KindleButton extends StatelessWidget {
  final String gameType;
  const _KindleButton({required this.gameType});
  @override
  Widget build(BuildContext context) {
    final label = Lore.gameLabel(gameType);
    return Tooltip(
      message: 'Create a new bonfire for $label',
      child: TextButton.icon(
        style: TextButton.styleFrom(
          foregroundColor: BonfireColors.accent,
          padding:
              const EdgeInsets.symmetric(horizontal: Sp.md, vertical: Sp.xs),
          minimumSize: Size.zero,
          tapTargetSize: MaterialTapTargetSize.shrinkWrap,
        ),
        onPressed: () => _showCreate(context, gameType),
        icon: const Icon(Icons.add, size: IS.sm),
        label: const Text('New bonfire',
            style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600)),
      ),
    );
  }
}

Future<void> _showCreate(BuildContext context, String gameType) async {
  final name = await showDialog<String>(
    context: context,
    builder: (_) => _CreateBonfireDialog(gameType: gameType),
  );
  if (name == null || name.isEmpty) return;
  await context.read<AppState>().createProfile(name, gameType);
}

class _CreateBonfireDialog extends StatefulWidget {
  // Game type is already known — it's the tab the user clicked from. No
  // dropdown to ask twice.
  final String gameType;
  const _CreateBonfireDialog({required this.gameType});
  @override
  State<_CreateBonfireDialog> createState() => _CreateBonfireDialogState();
}

class _CreateBonfireDialogState extends State<_CreateBonfireDialog> {
  final _ctrl = TextEditingController();

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final gameLabel = Lore.gameLabel(widget.gameType);
    return AlertDialog(
      backgroundColor: p.surface,
      title: const Text('New bonfire'),
      content: SizedBox(
        width: 400,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('For $gameLabel.', style: BT.bodyMuted),
            const SizedBox(height: Sp.md),
            TextField(
              controller: _ctrl,
              autofocus: true,
              decoration: const InputDecoration(
                labelText: 'Server name',
                hintText: 'e.g. "PvP Chaos Server"',
              ),
              onSubmitted: (_) => _submit(),
            ),
            const SizedBox(height: Sp.sm),
            Text(
              'You can edit description, password, and other settings later '
              'from Tend the flame.',
              style: BT.caption.copyWith(color: p.textMuted),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(context).pop(null),
            child: const Text('Cancel')),
        FilledButton(onPressed: _submit, child: const Text('Create')),
      ],
    );
  }

  void _submit() {
    final name = _ctrl.text.trim();
    if (name.isEmpty) return;
    Navigator.of(context).pop(name);
  }
}

class _NotInstalledCard extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return BonfireCard(
      padding: const EdgeInsets.all(Sp.xl),
      child: Row(
        children: [
          const AnimatedFlame(size: 28),
          const SizedBox(width: Sp.md),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Server binaries are not installed yet.',
                    style: BT.body.copyWith(color: p.textPrimary)),
                Padding(
                  padding: const EdgeInsets.only(top: 2),
                  child: Text(
                    'Install once — then create as many bonfires as you like.',
                    style: BT.caption.copyWith(color: p.textMuted),
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: Sp.md),
          FilledButton.icon(
            onPressed: () => InstallPanel.show(context),
            icon: const Icon(Icons.download, size: IS.sm),
            label: const Text('Install'),
          ),
        ],
      ),
    );
  }
}

class _NoProfilesCard extends StatelessWidget {
  final String gameType;
  const _NoProfilesCard({required this.gameType});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final label = Lore.gameLabel(gameType);
    return BonfireCard(
      padding: const EdgeInsets.all(Sp.xl),
      child: Row(
        children: [
          const BrandMark(size: 28, glow: false),
          const SizedBox(width: Sp.md),
          Expanded(
            child: Text(
              'No bonfires for $label yet. Click Kindle bonfire to create one.',
              style: BT.bodyMuted.copyWith(color: p.textMuted),
            ),
          ),
        ],
      ),
    );
  }
}

class _LocalBonfireRow extends StatefulWidget {
  final Profile profile;
  const _LocalBonfireRow({required this.profile});
  @override
  State<_LocalBonfireRow> createState() => _LocalBonfireRowState();
}

class _LocalBonfireRowState extends State<_LocalBonfireRow> {
  bool _expanded = false;
  bool _busy = false;

  Future<void> _play() async {
    final app = context.read<AppState>();

    // Pre-flight: game .exe configured?
    final gs = app.gameSettings;
    if (gs == null || !gs.validFor(widget.profile.gameType)) {
      _toast(
          'Set the ${Lore.gameLabel(widget.profile.gameType)} game path in Game Settings.',
          isError: true);
      return;
    }

    setState(() => _busy = true);
    final res = await app.launchLocalGame(profileId: widget.profile.id);
    if (!mounted) return;
    setState(() => _busy = false);
    if (res.ok) {
      _toast('Travelling to "${widget.profile.name}"…',
          icon: Icons.local_fire_department);
    } else {
      _toast(res.error ?? 'Launch failed.', isError: true);
    }
  }

  Future<void> _stop() async {
    setState(() => _busy = true);
    await context.read<AppState>().stopServer();
    if (mounted) {
      setState(() => _busy = false);
      _toast('The flame has been snuffed.');
    }
  }

  Future<void> _activate() async {
    setState(() => _busy = true);
    await context.read<AppState>().activateProfile(widget.profile.id);
    if (mounted) {
      setState(() => _busy = false);
      _toast('Switched to "${widget.profile.name}".');
    }
  }

  Future<void> _delete() async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        backgroundColor: Palette.of(context).surface,
        title: Text('Delete "${widget.profile.name}"?'),
        content: const Text(
          'Removes the profile, its config, RSA keys, and database. '
          'Cannot be undone. Server binaries stay installed.',
          style: TextStyle(height: 1.4),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(context).pop(false),
              child: const Text('Cancel')),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: BonfireColors.err),
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (ok != true) return;
    await context.read<AppState>().deleteProfile(widget.profile.id);
  }

  void _toast(String msg,
      {IconData icon = Icons.info_outline, bool isError = false}) {
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      backgroundColor: isError ? BonfireColors.err : BonfireColors.surfaceHi,
      content: Row(children: [
        Icon(icon,
            size: IS.md, color: isError ? Colors.white : BonfireColors.accent),
        const SizedBox(width: Sp.sm),
        Flexible(
            child: Text(msg,
                style: TextStyle(
                    color:
                        isError ? Colors.white : BonfireColors.textPrimary))),
      ]),
    ));
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final p = Palette.of(context);
    final isActive = widget.profile.isActive;
    final running = isActive && (app.liveStatus?.running ?? false);
    final fwOk = app.firewall?.allInstalled ?? false;
    final cfg = isActive ? app.config : null;

    // Prefer the live config's name + description (what the server actually
    // advertises) when this profile is active. Fall back to profile metadata.
    final displayName =
        (cfg != null && cfg.name.isNotEmpty) ? cfg.name : widget.profile.name;
    final displayDesc = cfg?.description ?? '';

    return Padding(
      padding: const EdgeInsets.only(bottom: Sp.sm),
      child: Container(
        decoration: BoxDecoration(
          color: isActive ? BonfireColors.mineBg : p.surface,
          borderRadius: BorderRadius.circular(R.sm),
          border: Border.all(
            color: isActive ? BonfireColors.accentDim : p.border,
            width: isActive ? 1.5 : 1,
          ),
        ),
        child: Column(
          children: [
            // ───── compact strip ─────
            InkWell(
              onTap: () => setState(() => _expanded = !_expanded),
              borderRadius: BorderRadius.circular(R.sm),
              child: Padding(
                padding: const EdgeInsets.fromLTRB(Sp.lg, Sp.md, Sp.md, Sp.md),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.center,
                  children: [
                    StatusDot(color: running ? BonfireColors.ok : p.textMuted),
                    const SizedBox(width: Sp.md),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Row(
                            children: [
                              Flexible(
                                child: Text(
                                  displayName.isEmpty
                                      ? '(unnamed)'
                                      : displayName,
                                  style:
                                      BT.heading.copyWith(color: p.textPrimary),
                                  overflow: TextOverflow.ellipsis,
                                ),
                              ),
                              if (isActive) ...[
                                const SizedBox(width: Sp.sm),
                                _StatePill(text: running ? 'LIVE' : 'ACTIVE'),
                              ],
                            ],
                          ),
                          if (displayDesc.isNotEmpty)
                            Padding(
                              padding: const EdgeInsets.only(top: 2),
                              child: Text(
                                displayDesc,
                                style: BT.caption.copyWith(color: p.textMuted),
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                        ],
                      ),
                    ),
                    const SizedBox(width: Sp.md),
                    // Button label communicates state:
                    //   • idle profile  → "Light the bonfire" (kindle + travel)
                    //   • LIVE profile  → "Travel" (the flame is already lit)
                    // Same RPC behind both — game.launch_local activates,
                    // starts the server if needed, then launches the game.
                    Tooltip(
                      message: running
                          ? 'Launch the game pointing at this running bonfire'
                          : 'Start the server and launch the game',
                      child: FilledButton.icon(
                        onPressed: _busy ? null : _play,
                        icon: _busy
                            ? const SizedBox(
                                width: 14,
                                height: 14,
                                child: CircularProgressIndicator(
                                    strokeWidth: 2, color: Colors.black))
                            : Icon(
                                running
                                    ? Icons.flight_takeoff
                                    : Icons.local_fire_department,
                                size: IS.md),
                        label: Padding(
                          padding: const EdgeInsets.symmetric(
                              horizontal: Sp.xs, vertical: 2),
                          child: Text(_busy
                              ? 'Travelling…'
                              : (running
                                  ? 'Travel to this fire'
                                  : 'Light the bonfire')),
                        ),
                      ),
                    ),
                    const SizedBox(width: Sp.xs),
                    Icon(
                      _expanded ? Icons.expand_less : Icons.expand_more,
                      size: IS.md,
                      color: p.textMuted,
                    ),
                  ],
                ),
              ),
            ),

            // ───── expanded detail ─────
            AnimatedSize(
              duration: const Duration(milliseconds: 180),
              curve: Curves.easeOutCubic,
              child: !_expanded
                  ? const SizedBox.shrink()
                  : Container(
                      decoration: BoxDecoration(
                        border: Border(
                            top: BorderSide(
                                color: isActive
                                    ? BonfireColors.accentDim
                                    : p.border)),
                      ),
                      padding:
                          const EdgeInsets.fromLTRB(Sp.lg, Sp.md, Sp.lg, Sp.md),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          // Info chips — what the server actually is.
                          Wrap(
                            spacing: Sp.sm,
                            runSpacing: Sp.sm,
                            children: [
                              _InfoChip(
                                  label: 'Game',
                                  value:
                                      Lore.gameLabel(widget.profile.gameType)),
                              if (isActive &&
                                  (app.wanIp != null || cfg != null))
                                _InfoChip(
                                  label: 'WAN',
                                  value: app.wanIp ??
                                      (cfg?.publicIp
                                              .replaceAll('__WAN_IP__', '?') ??
                                          '?'),
                                ),
                              if (isActive &&
                                  (app.lanIp != null || cfg != null))
                                _InfoChip(
                                  label: 'LAN',
                                  value: app.lanIp ??
                                      (cfg?.privateIp
                                              .replaceAll('__LAN_IP__', '?') ??
                                          '?'),
                                ),
                              if (cfg != null && cfg.password.isNotEmpty)
                                const _InfoChip(
                                    label: 'Password', value: 'set'),
                              if (cfg != null)
                                _InfoChip(
                                    label: 'Listed',
                                    value: cfg.advertise ? 'yes' : 'no'),
                              if (running)
                                _InfoChip(
                                    label: 'PID',
                                    value: '${app.liveStatus!.pid}'),
                            ],
                          ),
                          const SizedBox(height: Sp.lg),

                          // Two-row action layout: primary on the left,
                          // state-dependent + delete on the right.
                          Row(
                            crossAxisAlignment: CrossAxisAlignment.center,
                            children: [
                              // PRIMARY (left side). The bonfire's name is
                              // edited inside Configure (which writes the
                              // server's ServerName) — there is no separate
                              // rename, since the profile name and the
                              // running server's name are the same thing.
                              if (!isActive)
                                _AccentBtn(
                                  icon: Icons.swap_horiz,
                                  label: 'Make active',
                                  onTap: _busy ? null : _activate,
                                )
                              else
                                _AccentBtn(
                                  icon: Icons.tune,
                                  label: Lore.configure,
                                  onTap: _busy
                                      ? null
                                      : () => ConfigPanel.show(context),
                                ),
                              const Spacer(),

                              // STATE-CONDITIONAL (right side)
                              if (isActive && !fwOk) ...[
                                _GhostBtn(
                                  icon: Icons.shield_outlined,
                                  iconColor: BonfireColors.warn,
                                  label: 'Apply firewall',
                                  onTap: () => app.applyFirewall(),
                                ),
                                const SizedBox(width: Sp.sm),
                              ],
                              if (running) ...[
                                _GhostBtn(
                                  icon: Icons.open_in_new,
                                  label: 'WebUI',
                                  onTap: () =>
                                      _openUrl('http://localhost:50005'),
                                ),
                                const SizedBox(width: Sp.sm),
                                _GhostBtn(
                                  icon: Icons.stop,
                                  label: 'Stop',
                                  onTap: _busy ? null : _stop,
                                ),
                                const SizedBox(width: Sp.sm),
                              ],
                              IconButton(
                                tooltip: 'Delete bonfire',
                                onPressed: _busy ? null : _delete,
                                icon: const Icon(Icons.delete_outline,
                                    size: IS.md, color: BonfireColors.err),
                              ),
                            ],
                          ),
                        ],
                      ),
                    ),
            ),
          ],
        ),
      ),
    );
  }
}

// ── small helper widgets used by the local-bonfire row ──

class _StatePill extends StatelessWidget {
  final String text;
  const _StatePill({required this.text});
  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
      decoration: BoxDecoration(
        color: BonfireColors.accent.withOpacity(0.15),
        borderRadius: BorderRadius.circular(R.pill),
        border: Border.all(color: BonfireColors.accent),
      ),
      child: Text(
        text,
        style: BT.eyebrow.copyWith(
            fontSize: 8.5, letterSpacing: 0, color: BonfireColors.accent),
      ),
    );
  }
}

class _AccentBtn extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback? onTap;
  const _AccentBtn(
      {required this.icon, required this.label, required this.onTap});
  @override
  Widget build(BuildContext context) {
    return OutlinedButton.icon(
      style: OutlinedButton.styleFrom(
        foregroundColor: BonfireColors.accent,
        side: const BorderSide(color: BonfireColors.accent),
        padding:
            const EdgeInsets.symmetric(horizontal: Sp.md, vertical: Sp.sm + 2),
      ),
      onPressed: onTap,
      icon: Icon(icon, size: IS.sm, color: BonfireColors.accent),
      label: Text(label),
    );
  }
}

class _GhostBtn extends StatelessWidget {
  final IconData icon;
  final String label;
  final Color? iconColor;
  final VoidCallback? onTap;
  const _GhostBtn(
      {required this.icon, required this.label, this.iconColor, this.onTap});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return TextButton.icon(
      style: TextButton.styleFrom(
        foregroundColor: p.textPrimary,
        padding:
            const EdgeInsets.symmetric(horizontal: Sp.md, vertical: Sp.sm + 2),
      ),
      onPressed: onTap,
      icon: Icon(icon, size: IS.sm, color: iconColor ?? p.textSecondary),
      label: Text(label, style: BT.button.copyWith(color: p.textPrimary)),
    );
  }
}

// Old _MyServerStrip kept for reference (no longer used).
class _MyServerStrip extends StatefulWidget {
  final ServerConfig config;
  const _MyServerStrip({required this.config});
  @override
  State<_MyServerStrip> createState() => _MyServerStripState();
}

class _MyServerStripState extends State<_MyServerStrip> {
  bool _expanded = true;

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final running = app.liveStatus?.running ?? false;
    final fwOk = app.firewall?.allInstalled ?? false;

    return BonfireCard(
      background: BonfireColors.mineBg,
      borderColor: BonfireColors.accentDim,
      padding: EdgeInsets.zero,
      child: Column(
        children: [
          // Compact strip — always visible.
          InkWell(
            onTap: () => setState(() => _expanded = !_expanded),
            borderRadius: BorderRadius.circular(R.sm),
            child: Padding(
              padding: const EdgeInsets.fromLTRB(Sp.lg, Sp.md, Sp.md, Sp.md),
              child: Row(
                children: [
                  const Icon(Icons.star_rounded,
                      color: BonfireColors.accent, size: IS.md),
                  const SizedBox(width: Sp.sm),
                  Text(Lore.yourServerLabel,
                      style: BT.eyebrow.copyWith(color: BonfireColors.accent)),
                  const SizedBox(width: Sp.md),
                  Expanded(
                    child: Text(
                      widget.config.name.isEmpty
                          ? '(unnamed)'
                          : widget.config.name,
                      style: BT.heading,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  StatusDot(
                      color:
                          running ? BonfireColors.ok : BonfireColors.textMuted),
                  const SizedBox(width: Sp.sm),
                  Text(running ? Lore.yourServerRunning : Lore.yourServerIdle,
                      style: BT.caption),
                  const SizedBox(width: Sp.md),
                  Icon(
                    _expanded ? Icons.expand_less : Icons.expand_more,
                    size: IS.md,
                    color: BonfireColors.textMuted,
                  ),
                ],
              ),
            ),
          ),
          // Expanded body.
          AnimatedSize(
            duration: const Duration(milliseconds: 180),
            curve: Curves.easeOutCubic,
            child: _expanded
                ? Padding(
                    padding: const EdgeInsets.fromLTRB(Sp.lg, 0, Sp.lg, Sp.lg),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        if (widget.config.description.isNotEmpty)
                          Padding(
                            padding: const EdgeInsets.only(bottom: Sp.md),
                            child: Text(widget.config.description,
                                style: BT.bodyMuted),
                          ),
                        Wrap(
                          spacing: Sp.sm,
                          runSpacing: Sp.sm,
                          children: [
                            _InfoChip(
                              label: 'WAN',
                              value: app.wanIp ??
                                  widget.config.publicIp
                                      .replaceAll('__WAN_IP__', '?'),
                            ),
                            _InfoChip(
                              label: 'LAN',
                              value: app.lanIp ??
                                  widget.config.privateIp
                                      .replaceAll('__LAN_IP__', '?'),
                            ),
                            _InfoChip(
                                label: 'Game',
                                value: Lore.gameLabel(widget.config.gameType)),
                            if (widget.config.password.isNotEmpty)
                              const _InfoChip(label: 'Password', value: 'set'),
                            _InfoChip(
                                label: 'Listed',
                                value: widget.config.advertise ? 'yes' : 'no'),
                          ],
                        ),
                        const SizedBox(height: Sp.lg),
                        Row(
                          children: [
                            OutlinedButton.icon(
                              onPressed: () => ConfigPanel.show(context),
                              icon: const Icon(Icons.tune, size: IS.sm),
                              label: const Text(Lore.configure),
                            ),
                            const SizedBox(width: Sp.sm),
                            if (!fwOk)
                              OutlinedButton.icon(
                                onPressed: () => app.applyFirewall(),
                                icon: const Icon(Icons.shield_outlined,
                                    size: IS.sm, color: BonfireColors.warn),
                                label: const Text(Lore.applyFirewall),
                              ),
                            if (running) ...[
                              const SizedBox(width: Sp.sm),
                              OutlinedButton.icon(
                                onPressed: () async {
                                  // Open the WebUI in the system browser.
                                  // ignore: use_build_context_synchronously
                                  await _openUrl('http://localhost:50005');
                                },
                                icon:
                                    const Icon(Icons.open_in_new, size: IS.sm),
                                label: const Text('Open WebUI'),
                              ),
                            ],
                            const Spacer(),
                            FilledButton.icon(
                              onPressed: () async {
                                if (running) {
                                  await app.stopServer();
                                  if (context.mounted) {
                                    ScaffoldMessenger.of(context).showSnackBar(
                                        const SnackBar(
                                            content: Text(
                                                'The flame has been snuffed.')));
                                  }
                                } else {
                                  try {
                                    await app.startServer();
                                    if (context.mounted) {
                                      ScaffoldMessenger.of(context)
                                          .showSnackBar(const SnackBar(
                                              content:
                                                  Text('Bonfire kindled.')));
                                    }
                                  } catch (e) {
                                    if (context.mounted) {
                                      ScaffoldMessenger.of(context)
                                          .showSnackBar(SnackBar(
                                              backgroundColor:
                                                  BonfireColors.err,
                                              content: Text(e.toString())));
                                    }
                                  }
                                }
                              },
                              icon: Icon(
                                  running ? Icons.stop : Icons.play_arrow,
                                  size: IS.md),
                              label: Padding(
                                padding: const EdgeInsets.symmetric(
                                    horizontal: Sp.sm, vertical: 2),
                                child: Text(running
                                    ? Lore.stopServer
                                    : Lore.startServer),
                              ),
                            ),
                          ],
                        ),
                      ],
                    ),
                  )
                : const SizedBox.shrink(),
          ),
        ],
      ),
    );
  }
}

class _InfoChip extends StatelessWidget {
  final String label;
  final String value;
  const _InfoChip({required this.label, required this.value});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Container(
      padding:
          const EdgeInsets.symmetric(horizontal: Sp.md, vertical: Sp.xs + 2),
      decoration: BoxDecoration(
        color: p.surfaceHi,
        borderRadius: BorderRadius.circular(R.sm),
        border: Border.all(color: p.border),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(label,
              style: BT.eyebrow.copyWith(letterSpacing: 0, color: p.textMuted)),
          const SizedBox(width: Sp.sm),
          Text(value, style: BT.mono.copyWith(color: p.textPrimary)),
        ],
      ),
    );
  }
}

// ────────── Public server row (with inline expand on selection) ──────────

class _PublicServerRow extends StatelessWidget {
  final PublicServer server;
  const _PublicServerRow({required this.server});
  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final p = Palette.of(context);
    final selected = app.selectedServerId == server.id;
    return Padding(
      padding: const EdgeInsets.only(bottom: Sp.sm),
      child: _HoverableRow(
        selected: selected,
        onTap: () =>
            selected ? app.selectServer(null) : app.selectServer(server.id),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // Compact strip
            Padding(
              padding: const EdgeInsets.symmetric(
                  horizontal: Sp.lg, vertical: Sp.md),
              child: Row(
                children: [
                  Icon(
                    server.passwordRequired ? Icons.lock : Icons.public,
                    size: IS.md,
                    color: server.passwordRequired ? p.warn : p.textSecondary,
                  ),
                  const SizedBox(width: Sp.md),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          crossAxisAlignment: CrossAxisAlignment.center,
                          children: [
                            Flexible(
                              child: Text(server.name,
                                  style:
                                      BT.heading.copyWith(color: p.textPrimary),
                                  overflow: TextOverflow.ellipsis),
                            ),
                            const SizedBox(width: Sp.sm),
                            ServerBadges(server: server),
                          ],
                        ),
                        if (server.description.isNotEmpty)
                          Padding(
                            padding: const EdgeInsets.only(top: 2),
                            child: Text(server.description,
                                style: BT.caption.copyWith(color: p.textMuted),
                                overflow: TextOverflow.ellipsis),
                          ),
                      ],
                    ),
                  ),
                  const SizedBox(width: Sp.md),
                  _PlayerChip(count: server.playerCount),
                  const SizedBox(width: Sp.sm),
                  _LaunchButton(server: server),
                ],
              ),
            ),
            // Expanded detail (only when selected)
            AnimatedSize(
              duration: const Duration(milliseconds: 180),
              curve: Curves.easeOutCubic,
              child: selected
                  ? _DetailBlock(server: server)
                  : const SizedBox.shrink(),
            ),
          ],
        ),
      ),
    );
  }
}

class _DetailBlock extends StatelessWidget {
  final PublicServer server;
  const _DetailBlock({required this.server});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Container(
      decoration: BoxDecoration(
        border: Border(top: BorderSide(color: p.border)),
      ),
      padding: const EdgeInsets.fromLTRB(Sp.lg, Sp.md, Sp.lg, Sp.md),
      // Detail block — chips only. Play button is already inline on the
      // collapsed row to keep the action-target consistent.
      child: Wrap(
        spacing: Sp.sm,
        runSpacing: Sp.sm,
        children: [
          _InfoChip(label: 'Host', value: server.hostname),
          _InfoChip(label: 'IP', value: server.ipAddress),
          _InfoChip(label: 'Game', value: Lore.gameLabel(server.gameType)),
          if (server.passwordRequired)
            const _InfoChip(label: 'Password', value: 'required'),
        ],
      ),
    );
  }
}

class _HoverableRow extends StatefulWidget {
  final Widget child;
  final bool selected;
  final VoidCallback onTap;
  const _HoverableRow({
    required this.child,
    required this.selected,
    required this.onTap,
  });
  @override
  State<_HoverableRow> createState() => _HoverableRowState();
}

class _HoverableRowState extends State<_HoverableRow> {
  bool _hover = false;
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final isActive = widget.selected || _hover;
    return MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 120),
          decoration: BoxDecoration(
            color: widget.selected ? p.surfaceHi : p.surface,
            borderRadius: BorderRadius.circular(R.sm),
            // Left edge glows amber on hover/selection — subtle but readable.
            border: Border(
              left: BorderSide(
                color: isActive ? p.accent : p.border,
                width: isActive ? 3 : 1,
              ),
              top: BorderSide(color: p.border),
              right: BorderSide(color: p.border),
              bottom: BorderSide(color: p.border),
            ),
          ),
          child: DefaultTextStyle(
            // Cascade palette text colour into child Text widgets that
            // don't override their style.
            style: TextStyle(color: p.textPrimary),
            child: widget.child,
          ),
        ),
      ),
    );
  }
}

class _PlayerChip extends StatelessWidget {
  final int count;
  const _PlayerChip({required this.count});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final hot = count > 0;
    return Container(
      padding:
          const EdgeInsets.symmetric(horizontal: Sp.md, vertical: Sp.xs + 2),
      decoration: BoxDecoration(
        color: hot ? BonfireColors.accent.withOpacity(0.20) : p.surfaceHi,
        borderRadius: BorderRadius.circular(R.pill),
        border: Border.all(color: hot ? p.accentDim : p.border),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(hot ? Icons.local_fire_department : Icons.person_outline,
              size: 12, color: hot ? p.accent : p.textMuted),
          const SizedBox(width: Sp.xs),
          Text('$count',
              style: BT.caption.copyWith(
                  color: hot ? p.accent : p.textPrimary,
                  fontWeight: FontWeight.w600)),
        ],
      ),
    );
  }
}

// ────────── Skeleton row ──────────

class _SkeletonRow extends StatefulWidget {
  const _SkeletonRow();
  @override
  State<_SkeletonRow> createState() => _SkeletonRowState();
}

class _SkeletonRowState extends State<_SkeletonRow>
    with SingleTickerProviderStateMixin {
  late final AnimationController _c;
  @override
  void initState() {
    super.initState();
    _c = AnimationController(
        duration: const Duration(milliseconds: 1100), vsync: this)
      ..repeat(reverse: true);
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _c,
      builder: (_, __) {
        final t = 0.6 + (_c.value * 0.4);
        final c =
            Color.lerp(BonfireColors.surface, BonfireColors.surfaceHi, t)!;
        return Padding(
          padding: const EdgeInsets.only(bottom: Sp.sm),
          child: Container(
            height: 56,
            decoration: BoxDecoration(
              color: c,
              borderRadius: BorderRadius.circular(R.sm),
              border: Border.all(color: BonfireColors.border),
            ),
          ),
        );
      },
    );
  }
}

// ────────── Empty / unreachable states ──────────

class _EmptyListCard extends StatelessWidget {
  final String query;
  final String gameTab;
  const _EmptyListCard({required this.query, required this.gameTab});
  @override
  Widget build(BuildContext context) {
    final label = Lore.gameLabel(gameTab);
    final msg =
        query.isNotEmpty ? 'No bonfires match "$query".' : Lore.noFires(label);
    return BonfireCard(
      padding: const EdgeInsets.fromLTRB(Sp.xl, Sp.xxl, Sp.xl, Sp.xxl),
      child: Column(
        children: [
          const BrandMark(size: 44),
          const SizedBox(height: Sp.md),
          Text(msg,
              style: BT.body.copyWith(color: Palette.of(context).textPrimary),
              textAlign: TextAlign.center),
          const SizedBox(height: Sp.md),
          // Souls-flavoured aside.
          Text(
            query.isNotEmpty
                ? '"Seek hollows in another realm."'
                : '"The fire fades, and the lords go without thrones."',
            textAlign: TextAlign.center,
            style: BT.caption.copyWith(
              fontStyle: FontStyle.italic,
              color: Palette.of(context).textMuted,
            ),
          ),
        ],
      ),
    );
  }
}

class _MasterUnreachableCard extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final app = context.read<AppState>();
    return BonfireCard(
      padding: const EdgeInsets.all(Sp.lg),
      child: Row(
        children: [
          const Icon(Icons.cloud_off_outlined,
              color: BonfireColors.warn, size: IS.lg),
          const SizedBox(width: Sp.md),
          const Expanded(
            child: Text(Lore.masterUnreachable, style: BT.body),
          ),
          const SizedBox(width: Sp.md),
          OutlinedButton(
            onPressed: () => app.refreshPublicServers(),
            child: const Text('Try again'),
          ),
        ],
      ),
    );
  }
}

// ────────────── Bottom bar ──────────────

class _BottomBar extends StatelessWidget {
  const _BottomBar();
  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final p = Palette.of(context);
    final selectedId = app.selectedServerId;
    PublicServer? selected;
    if (selectedId != null && app.publicServers != null) {
      for (final s in app.publicServers!) {
        if (s.id == selectedId) {
          selected = s;
          break;
        }
      }
    }
    return Container(
      decoration: BoxDecoration(
        color: p.surface,
        border: Border(top: BorderSide(color: p.border)),
      ),
      padding:
          const EdgeInsets.symmetric(horizontal: Sp.xl, vertical: Sp.sm + 2),
      child: Row(
        children: [
          Text('Bonfire v2.3.0',
              style: BT.caption.copyWith(color: p.textMuted)),
          const SizedBox(width: Sp.lg),
          if (selected != null)
            Flexible(
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.local_fire_department,
                      size: IS.sm, color: p.accent),
                  const SizedBox(width: Sp.xs),
                  Flexible(
                      child: Text('Selected · ${selected.name}',
                          style: BT.caption.copyWith(color: p.textSecondary),
                          overflow: TextOverflow.ellipsis)),
                ],
              ),
            ),
          const Spacer(),
          Text(
            'Hint: ↑↓ pick · Esc deselect · / search',
            style: BT.caption.copyWith(color: p.textMuted),
          ),
        ],
      ),
    );
  }
}

// ────────────── Filter bar (above the public list) ──────────────

class _FilterBar extends StatelessWidget {
  const _FilterBar();
  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final p = Palette.of(context);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        // Hide sealed
        InkWell(
          onTap: () => app.setHideSealed(!app.hideSealed),
          borderRadius: BorderRadius.circular(R.sm),
          child: Padding(
            padding:
                const EdgeInsets.symmetric(horizontal: Sp.sm, vertical: Sp.xs),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  app.hideSealed
                      ? Icons.check_box
                      : Icons.check_box_outline_blank,
                  size: IS.sm,
                  color: app.hideSealed ? p.accent : p.textMuted,
                ),
                const SizedBox(width: 6),
                Text('Hide sealed',
                    style: BT.caption.copyWith(
                        color: app.hideSealed ? p.textPrimary : p.textMuted)),
              ],
            ),
          ),
        ),
        const SizedBox(width: Sp.sm),
        // Min players: 0 / 1 / 5 / 10
        Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text('Min players ',
                style: BT.caption.copyWith(color: p.textMuted)),
            ...[0, 1, 5, 10].map((n) => Padding(
                  padding: const EdgeInsets.only(left: Sp.xs),
                  child: InkWell(
                    onTap: () => app.setMinPlayers(n),
                    borderRadius: BorderRadius.circular(R.pill),
                    child: Container(
                      padding: const EdgeInsets.symmetric(
                          horizontal: 8, vertical: 2),
                      decoration: BoxDecoration(
                        color: app.minPlayers == n
                            ? p.accent.withOpacity(0.15)
                            : Colors.transparent,
                        borderRadius: BorderRadius.circular(R.pill),
                        border: Border.all(
                            color: app.minPlayers == n ? p.accent : p.border),
                      ),
                      child: Text(
                        n == 0 ? 'any' : '$n+',
                        style: BT.caption.copyWith(
                          color: app.minPlayers == n ? p.accent : p.textMuted,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                  ),
                )),
          ],
        ),
      ],
    );
  }
}

// Cross-platform URL opener — uses `start` on Windows, `xdg-open` on Linux,
// `open` on macOS. We don't pull url_launcher because it needs a plugin
// (symlinks), which we sidestepped earlier.
Future<void> _openUrl(String url) async {
  try {
    if (Platform.isWindows) {
      await Process.start('cmd', ['/c', 'start', '', url], runInShell: true);
    } else if (Platform.isMacOS) {
      await Process.start('open', [url]);
    } else {
      await Process.start('xdg-open', [url]);
    }
  } catch (_) {/* best effort */}
}

// ────────────── Launch button with full pre-flight ──────────────

class _LaunchButton extends StatefulWidget {
  final PublicServer server;
  const _LaunchButton({required this.server});
  @override
  State<_LaunchButton> createState() => _LaunchButtonState();
}

class _LaunchButtonState extends State<_LaunchButton> {
  bool _busy = false;

  Future<void> _go() async {
    final app = context.read<AppState>();

    // 1. Game .exe path configured?
    final gs = app.gameSettings;
    if (gs == null || !gs.validFor(widget.server.gameType)) {
      final picked = await _pickExe(widget.server.gameType);
      if (picked == null) return;
      final ok = await app.setGameExe(widget.server.gameType, picked);
      if (!ok && mounted) {
        _toast(
            'That .exe is not a recognised version. The patcher does not '
            'know where to write the server info for this build.',
            isError: true);
        return;
      }
    }

    // 2. Steam running + logged in?
    await app.refreshSteamStatus();
    if (!app.steamOk) {
      _toast('Steam is not running, or you are not logged in.', isError: true);
      return;
    }

    // 3. Password if needed.
    var password = '';
    if (widget.server.passwordRequired) {
      final entered = await _askPassword();
      if (entered == null) return;
      password = entered;
    }

    // 4. Launch.
    setState(() => _busy = true);
    final res =
        await app.launchGame(serverId: widget.server.id, password: password);
    if (!mounted) return;
    setState(() => _busy = false);

    if (res.ok) {
      _toast('Travelling to "${widget.server.name}"…',
          icon: Icons.local_fire_department);
    } else {
      _toast(res.error ?? 'Launch failed.', isError: true);
    }
  }

  Future<String?> _pickExe(String gameType) async {
    // Manual entry — Flutter on this dev box can't load file_picker plugin
    // (no symlink support). Plain dialog with monospace input.
    final ctrl = TextEditingController();
    final hint = Lore.gameExeHint(gameType);
    return showDialog<String>(
      context: context,
      builder: (_) => AlertDialog(
        backgroundColor: BonfireColors.surface,
        title: Text('Path to ${Lore.gameExeName(gameType)}'),
        content: SizedBox(
          width: 580,
          child: TextField(
            controller: ctrl,
            autofocus: true,
            style: BT.mono,
            decoration:
                InputDecoration(hintText: hint, hintStyle: BT.monoMuted),
          ),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(context).pop(null),
              child: const Text('Cancel')),
          FilledButton(
              onPressed: () => Navigator.of(context).pop(ctrl.text.trim()),
              child: const Text('Save')),
        ],
      ),
    );
  }

  Future<String?> _askPassword() async {
    final ctrl = TextEditingController();
    return showDialog<String>(
      context: context,
      builder: (_) => AlertDialog(
        backgroundColor: BonfireColors.surface,
        title: const Text('This bonfire is sealed'),
        content: SizedBox(
          width: 360,
          child: TextField(
            controller: ctrl,
            autofocus: true,
            obscureText: true,
            decoration: const InputDecoration(hintText: 'Password'),
            onSubmitted: (v) => Navigator.of(context).pop(v),
          ),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(context).pop(null),
              child: const Text('Cancel')),
          FilledButton(
              onPressed: () => Navigator.of(context).pop(ctrl.text),
              child: const Text('Travel')),
        ],
      ),
    );
  }

  void _toast(String message,
      {IconData icon = Icons.info_outline, bool isError = false}) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        backgroundColor: isError ? BonfireColors.err : BonfireColors.surfaceHi,
        content: Row(
          children: [
            Icon(icon,
                size: IS.md,
                color: isError ? Colors.white : BonfireColors.accent),
            const SizedBox(width: Sp.sm),
            Flexible(
                child: Text(message,
                    style: TextStyle(
                        color: isError
                            ? Colors.white
                            : BonfireColors.textPrimary))),
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return FilledButton.icon(
      onPressed: _busy ? null : _go,
      icon: _busy
          ? const SizedBox(
              width: 16,
              height: 16,
              child: CircularProgressIndicator(
                  strokeWidth: 2, color: Colors.black))
          : const Icon(Icons.local_fire_department, size: IS.md),
      label: Padding(
        padding: const EdgeInsets.symmetric(horizontal: Sp.sm, vertical: 2),
        child: Text(_busy ? 'Travelling…' : Lore.launchGame),
      ),
    );
  }
}
