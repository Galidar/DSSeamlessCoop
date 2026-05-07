/// Reusable surface card with a subtle border, used everywhere to group
/// related controls. Resolves its colours through Palette.of(context) so
/// the same card renders Hollow (DS2) or Lordvessel (DS3) appropriately.

import 'package:flutter/material.dart';
import '../palette.dart';

class BonfireCard extends StatelessWidget {
  final Widget child;
  final EdgeInsets padding;
  final Color? background;
  final Color? borderColor;
  final VoidCallback? onTap;
  final bool selected;

  const BonfireCard({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(16),
    this.background,
    this.borderColor,
    this.onTap,
    this.selected = false,
  });

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final bg = background ?? p.surface;
    final border = borderColor ?? (selected ? p.accent : p.border);
    final w = AnimatedContainer(
      duration: const Duration(milliseconds: 120),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: border, width: selected ? 1.5 : 1),
      ),
      padding: padding,
      child: child,
    );
    if (onTap == null) return w;
    return Material(
      color: Colors.transparent,
      child: InkWell(
        borderRadius: BorderRadius.circular(8),
        onTap: onTap,
        hoverColor: p.surfaceHi,
        child: w,
      ),
    );
  }
}
