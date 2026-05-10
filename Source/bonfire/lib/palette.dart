/// Palette of the app.
///
/// Single Souls-inspired dark theme — black tomb tones with firekeeper amber
/// accents — used regardless of which game is selected. The PaletteScope
/// architecture is preserved so any future per-game theming can drop in
/// without touching widgets.

import 'package:flutter/material.dart';
import 'theme.dart';

class Palette {
  final Color bg;
  final Color surface;
  final Color surfaceHi;
  final Color border;
  final Color textPrimary;
  final Color textSecondary;
  final Color textMuted;
  final Color accent = BonfireColors.accent;
  final Color accentDim = BonfireColors.accentDim;
  final Color ok = BonfireColors.ok;
  final Color warn = BonfireColors.warn;
  final Color err = BonfireColors.err;

  const Palette({
    required this.bg,
    required this.surface,
    required this.surfaceHi,
    required this.border,
    required this.textPrimary,
    required this.textSecondary,
    required this.textMuted,
  });

  // The one true Bonfire palette.
  static const Palette dark = Palette(
    bg: BonfireColors.bg,
    surface: BonfireColors.surface,
    surfaceHi: BonfireColors.surfaceHi,
    border: BonfireColors.border,
    textPrimary: BonfireColors.textPrimary,
    textSecondary: BonfireColors.textSecondary,
    textMuted: BonfireColors.textMuted,
  );

  static Palette of(BuildContext context) {
    final scope = context.dependOnInheritedWidgetOfExactType<PaletteScope>();
    return scope?.palette ?? dark;
  }
}

class PaletteScope extends InheritedWidget {
  final Palette palette;
  const PaletteScope({
    super.key,
    required this.palette,
    required super.child,
  });

  @override
  bool updateShouldNotify(PaletteScope oldWidget) =>
      oldWidget.palette != palette;
}
