/// "About Bonfire" dialog. Brand splash, version, links to the project's
/// GitHub and community Discord. Credits live in the README, not the app.

import 'dart:io';
import 'package:flutter/material.dart';

import '../design.dart';
import '../palette.dart';
import '../theme.dart';
import '../widgets/animated_flame.dart';

Future<void> showBonfireAbout(BuildContext context) {
  return showDialog(
    context: context,
    builder: (_) => const _AboutDialog(),
  );
}

class _AboutDialog extends StatelessWidget {
  const _AboutDialog();
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Dialog(
      backgroundColor: p.surface,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(R.md)),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 460),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(Sp.xl, Sp.xl, Sp.xl, Sp.lg),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const AnimatedFlame(size: 32),
                  const SizedBox(width: Sp.md),
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text('Bonfire',
                          style: TextStyle(
                              fontSize: 22,
                              fontWeight: FontWeight.w600,
                              letterSpacing: 0.6,
                              color: p.textPrimary)),
                      Text('v0.2.2',
                          style: TextStyle(
                              fontSize: 12,
                              color: p.textMuted,
                              fontFamily: 'Consolas')),
                    ],
                  ),
                ],
              ),
              const SizedBox(height: Sp.lg),
              Text(
                'A modern desktop hub for hosting and joining private '
                'Dark Souls bonfires. Light your own fire, browse the '
                'realm, travel between worlds.',
                style: BT.body.copyWith(color: p.textPrimary),
              ),
              const SizedBox(height: Sp.lg),
              Text('LINKS',
                  style: BT.eyebrow.copyWith(color: p.textMuted)),
              const SizedBox(height: Sp.sm),
              const _LinkRow(
                  icon: Icons.code,
                  label: 'Bonfire on GitHub',
                  url: 'https://github.com/Galidar/DSSeamlessCoop'),
              const _ComingSoonRow(
                  icon: Icons.chat_bubble_outline,
                  label: 'Community Discord'),
              const SizedBox(height: Sp.lg),
              Divider(color: p.border),
              const SizedBox(height: Sp.sm),
              Text(
                'MIT licensed. Praise the sun.',
                style: TextStyle(
                    fontSize: 11,
                    color: p.textMuted,
                    fontStyle: FontStyle.italic),
              ),
              const SizedBox(height: Sp.md),
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

class _LinkRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String url;
  const _LinkRow({required this.icon, required this.label, required this.url});

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return InkWell(
      onTap: () => _open(url),
      borderRadius: BorderRadius.circular(R.sm),
      child: Padding(
        padding:
            const EdgeInsets.symmetric(horizontal: Sp.xs, vertical: Sp.sm),
        child: Row(
          children: [
            Icon(icon, size: IS.md, color: BonfireColors.accent),
            const SizedBox(width: Sp.md),
            Expanded(
                child: Text(label,
                    style: BT.body.copyWith(color: p.textPrimary))),
            Icon(Icons.open_in_new, size: IS.sm, color: p.textMuted),
          ],
        ),
      ),
    );
  }

  void _open(String url) {
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
}

/// A link-row sibling for items that aren't ready yet — same visual rhythm
/// as [_LinkRow] but greyed out, non-clickable, with a "Coming soon" badge.
class _ComingSoonRow extends StatelessWidget {
  final IconData icon;
  final String label;
  const _ComingSoonRow({required this.icon, required this.label});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: Sp.xs, vertical: Sp.sm),
      child: Row(
        children: [
          Icon(icon, size: IS.md, color: p.textMuted),
          const SizedBox(width: Sp.md),
          Expanded(
            child: Text(label,
                style: BT.body.copyWith(color: p.textMuted)),
          ),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
            decoration: BoxDecoration(
              color: p.surfaceHi,
              borderRadius: BorderRadius.circular(R.pill),
              border: Border.all(color: p.border),
            ),
            child: Text(
              'Coming soon',
              style: BT.eyebrow.copyWith(
                  fontSize: 9,
                  letterSpacing: 0.8,
                  color: p.textMuted),
            ),
          ),
        ],
      ),
    );
  }
}
