/// Tiny pill-shaped badges shown on each public server row, telling the user
/// at a glance what kind of bonfire it is. Examples:
///   • OPEN    — no password, vanilla-ish
///   • SEALED  — password-protected
///   • SHARD   — sub-instance hosted via the master server
///   • MODDED  — has any whitelist/blacklist/required mods set
///
/// Each badge has its own colour so visual scanning is fast.

import 'package:flutter/material.dart';

import '../design.dart';
import '../state/app_state.dart';
import '../theme.dart';

class ServerBadges extends StatelessWidget {
  final PublicServer server;
  const ServerBadges({super.key, required this.server});

  @override
  Widget build(BuildContext context) {
    final tags = <_Tag>[];
    if (server.passwordRequired) {
      tags.add(const _Tag('SEALED', BonfireColors.warn));
    } else {
      tags.add(const _Tag('OPEN', BonfireColors.textSecondary));
    }
    if (server.hasMods) {
      tags.add(const _Tag('MODDED', BonfireColors.accent));
    }
    if (server.isShard) {
      tags.add(const _Tag('SHARD', BonfireColors.textSecondary));
    } else if (server.allowSharding) {
      tags.add(const _Tag('SHARDABLE', BonfireColors.textMuted));
    }
    if (tags.isEmpty) return const SizedBox.shrink();
    return Wrap(
      spacing: Sp.xs,
      runSpacing: Sp.xs,
      children: tags.map((t) => _Pill(tag: t)).toList(),
    );
  }
}

class _Tag {
  final String label;
  final Color color;
  const _Tag(this.label, this.color);
}

class _Pill extends StatelessWidget {
  final _Tag tag;
  const _Pill({required this.tag});
  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: Sp.sm, vertical: 2),
      decoration: BoxDecoration(
        color: tag.color.withOpacity(0.13),
        borderRadius: BorderRadius.circular(R.pill),
        border: Border.all(color: tag.color.withOpacity(0.45)),
      ),
      child: Text(
        tag.label,
        style: TextStyle(
          fontSize: 9,
          letterSpacing: 0,
          fontWeight: FontWeight.w700,
          color: tag.color,
        ),
      ),
    );
  }
}
