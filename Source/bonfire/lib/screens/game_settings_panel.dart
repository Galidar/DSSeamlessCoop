/// Game settings — the .exe path for each Souls game and the
/// "use separate save files" toggle. Bonfire auto-detects the standard
/// Steam install paths on first launch; this panel is for overrides
/// (custom installs, GoG, modded copies, etc.).

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../design.dart';
import '../state/app_state.dart';
import '../theme.dart';

class GameSettingsPanel extends StatefulWidget {
  const GameSettingsPanel({super.key});

  static Future<void> show(BuildContext context) {
    return showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => const _Host(),
    );
  }

  @override
  State<GameSettingsPanel> createState() => _GameSettingsPanelState();
}

class _Host extends StatelessWidget {
  const _Host();
  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.55,
      maxChildSize: 0.85,
      minChildSize: 0.35,
      expand: false,
      builder: (_, scrollCtrl) => Container(
        decoration: const BoxDecoration(
          color: BonfireColors.surface,
          borderRadius: BorderRadius.vertical(top: Radius.circular(12)),
          border: Border(
            top: BorderSide(color: BonfireColors.border),
            left: BorderSide(color: BonfireColors.border),
            right: BorderSide(color: BonfireColors.border),
          ),
        ),
        child: SingleChildScrollView(
          controller: scrollCtrl,
          padding: const EdgeInsets.fromLTRB(Sp.xl + 4, Sp.lg, Sp.xl + 4, Sp.xl),
          child: const GameSettingsPanel(),
        ),
      ),
    );
  }
}

class _GameSettingsPanelState extends State<GameSettingsPanel> {
  late final TextEditingController _ds2Ctrl;
  late final TextEditingController _ds3Ctrl;

  @override
  void initState() {
    super.initState();
    final gs = context.read<AppState>().gameSettings;
    _ds2Ctrl = TextEditingController(text: gs?.ds2ExePath ?? '');
    _ds3Ctrl = TextEditingController(text: gs?.ds3ExePath ?? '');
  }

  @override
  void dispose() {
    _ds2Ctrl.dispose();
    _ds3Ctrl.dispose();
    super.dispose();
  }

  Future<void> _saveDs2() async {
    final ok = await context
        .read<AppState>()
        .setGameExe('DarkSouls2', _ds2Ctrl.text.trim());
    if (mounted) _toast(ok ? 'DS2 path saved.' : 'Path not recognised.', !ok);
  }

  Future<void> _saveDs3() async {
    final ok = await context
        .read<AppState>()
        .setGameExe('DarkSouls3', _ds3Ctrl.text.trim());
    if (mounted) _toast(ok ? 'DS3 path saved.' : 'Path not recognised.', !ok);
  }

  void _toast(String message, bool isError) {
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      backgroundColor:
          isError ? BonfireColors.err : BonfireColors.surfaceHi,
      content: Text(message),
    ));
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final gs = app.gameSettings;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Center(
          child: Container(
            width: 36,
            height: 4,
            margin: const EdgeInsets.only(bottom: Sp.lg),
            decoration: BoxDecoration(
              color: BonfireColors.border,
              borderRadius: BorderRadius.circular(R.pill),
            ),
          ),
        ),
        const Text('Game settings', style: BT.title),
        const SizedBox(height: Sp.xs),
        const Text(
          'Where Bonfire finds each Souls game. Auto-detected from Steam '
          'on first launch — override here if you have a custom install.',
          style: BT.bodyMuted,
        ),
        const SizedBox(height: Sp.xl),

        _GameRow(
          title: 'Dark Souls II — Scholar of the First Sin',
          steamFolder:
              r'\Steam\steamapps\common\Dark Souls II Scholar of the First Sin\Game\DarkSoulsII.exe',
          controller: _ds2Ctrl,
          isValid: gs?.ds2Valid ?? false,
          onSave: _saveDs2,
        ),
        const SizedBox(height: Sp.lg),
        _GameRow(
          title: 'Dark Souls III',
          steamFolder:
              r'\Steam\steamapps\common\DARK SOULS III\Game\DarkSoulsIII.exe',
          controller: _ds3Ctrl,
          isValid: gs?.ds3Valid ?? false,
          onSave: _saveDs3,
        ),

        const SizedBox(height: Sp.xl),
        const Divider(),
        const SizedBox(height: Sp.lg),
        const Text('SAVES', style: BT.eyebrow),
        const SizedBox(height: Sp.sm),
        const Text(
          'Bonfire and DSOS use a separate save folder than the retail '
          'servers do. This is the safest default — anything you do on '
          'a private server cannot get flagged on Steam matchmaking.',
          style: BT.bodyMuted,
        ),
        const SizedBox(height: Sp.lg),
        Align(
          alignment: Alignment.centerRight,
          child: TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Close'),
          ),
        ),
      ],
    );
  }
}

class _GameRow extends StatelessWidget {
  final String title;
  final String steamFolder;
  final TextEditingController controller;
  final bool isValid;
  final VoidCallback onSave;
  const _GameRow({
    required this.title,
    required this.steamFolder,
    required this.controller,
    required this.isValid,
    required this.onSave,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(Sp.lg),
      decoration: BoxDecoration(
        color: BonfireColors.surfaceHi,
        borderRadius: BorderRadius.circular(R.sm),
        border: Border.all(color: BonfireColors.border),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(child: Text(title, style: BT.heading)),
              Icon(
                isValid ? Icons.check_circle : Icons.warning_amber_rounded,
                size: IS.md,
                color: isValid ? BonfireColors.ok : BonfireColors.warn,
              ),
              const SizedBox(width: Sp.xs),
              Text(
                isValid ? 'Recognised' : 'Not configured',
                style: BT.caption.copyWith(
                    color: isValid
                        ? BonfireColors.ok
                        : BonfireColors.warn),
              ),
            ],
          ),
          const SizedBox(height: Sp.sm),
          TextField(
            controller: controller,
            style: BT.mono,
            decoration: InputDecoration(
              hintText: 'C:\\…' + steamFolder,
              hintStyle: BT.monoMuted,
            ),
          ),
          const SizedBox(height: Sp.sm),
          Align(
            alignment: Alignment.centerRight,
            child: OutlinedButton(
                onPressed: onSave, child: const Text('Save path')),
          ),
        ],
      ),
    );
  }
}
