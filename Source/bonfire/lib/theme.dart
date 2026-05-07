/// Bonfire visual theme — Souls-inspired dark palette with firekeeper amber
/// as the single primary accent. Built on Material 3 so we get sane defaults
/// for typography, ripples, focus states, etc., then customised for our look.

import 'package:flutter/material.dart';

class BonfireColors {
  // Backgrounds — layered like a tomb at night.
  static const Color bg = Color(0xFF14110F);
  static const Color surface = Color(0xFF1C1916);
  static const Color surfaceHi = Color(0xFF26221E);
  static const Color border = Color(0xFF2F2A24);

  // Text.
  static const Color textPrimary = Color(0xFFEDE6D6);
  static const Color textSecondary = Color(0xFFB8AC95);
  static const Color textMuted = Color(0xFF8A7E68);

  // Firekeeper amber — the only accent.
  static const Color accent = Color(0xFFD79447);
  static const Color accentHover = Color(0xFFE3A35B);
  static const Color accentDim = Color(0xFF7A5527);

  // Status colors.
  static const Color ok = Color(0xFF8BB36B);
  static const Color warn = Color(0xFFD79447);
  static const Color err = Color(0xFFC2563E);

  // Mine — for the user's own server in the list.
  static const Color mineBg = Color(0xFF2A2114);
  static const Color mineAccent = accent;
}

class BonfireTheme {
  static ThemeData get dark {
    final base = ThemeData.dark(useMaterial3: true);
    return base.copyWith(
      scaffoldBackgroundColor: BonfireColors.bg,
      canvasColor: BonfireColors.surface,
      dividerColor: BonfireColors.border,
      colorScheme: const ColorScheme.dark(
        primary: BonfireColors.accent,
        onPrimary: Colors.black,
        secondary: BonfireColors.accent,
        onSecondary: Colors.black,
        surface: BonfireColors.surface,
        onSurface: BonfireColors.textPrimary,
        error: BonfireColors.err,
        onError: Colors.white,
      ),
      textTheme: base.textTheme.apply(
        bodyColor: BonfireColors.textPrimary,
        displayColor: BonfireColors.textPrimary,
        fontFamily: 'Segoe UI',
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          backgroundColor: BonfireColors.accent,
          foregroundColor: Colors.black,
          textStyle: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6)),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          foregroundColor: BonfireColors.textPrimary,
          side: const BorderSide(color: BonfireColors.border),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6)),
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 11),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: BonfireColors.accent,
        ),
      ),
      inputDecorationTheme: const InputDecorationTheme(
        filled: true,
        fillColor: BonfireColors.surfaceHi,
        contentPadding: EdgeInsets.symmetric(horizontal: 12, vertical: 12),
        border: OutlineInputBorder(
          borderSide: BorderSide(color: BonfireColors.border),
          borderRadius: BorderRadius.all(Radius.circular(4)),
        ),
        enabledBorder: OutlineInputBorder(
          borderSide: BorderSide(color: BonfireColors.border),
          borderRadius: BorderRadius.all(Radius.circular(4)),
        ),
        focusedBorder: OutlineInputBorder(
          borderSide: BorderSide(color: BonfireColors.accent, width: 1.5),
          borderRadius: BorderRadius.all(Radius.circular(4)),
        ),
        labelStyle: TextStyle(color: BonfireColors.textSecondary),
      ),
      progressIndicatorTheme: const ProgressIndicatorThemeData(
        color: BonfireColors.accent,
        linearTrackColor: BonfireColors.surfaceHi,
      ),
      dividerTheme: const DividerThemeData(
        color: BonfireColors.border,
        thickness: 1,
      ),
    );
  }
}
