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
    bg: Color(0xFF14110F),
    surface: Color(0xFF1C1916),
    surfaceHi: Color(0xFF26221E),
    border: Color(0xFF2F2A24),
    textPrimary: Color(0xFFEDE6D6),
    textSecondary: Color(0xFFB8AC95),
    textMuted: Color(0xFF8A7E68),
  );

  static Palette of(BuildContext context) {
    final scope =
        context.dependOnInheritedWidgetOfExactType<PaletteScope>();
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
