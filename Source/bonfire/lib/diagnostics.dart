/// Diagnostic file logger. Writes to `bonfire.log` next to Bonfire.exe so
/// we can see what's happening even when the app's stdout/stderr aren't
/// attached to a console (the default for Flutter Windows desktop apps).

import 'dart:io';

class BonfireLog {
  static File? _file;
  static final List<File> _attempted = [];

  static void init() {
    // Try a sequence of locations until one accepts the write. We log
    // the chain of attempts so debugging is possible even if the
    // primary path is read-only or the install dir is somewhere weird.
    final candidates = <String?>[
      // Primary: next to the exe.
      _safe(() =>
          '${File(Platform.resolvedExecutable).parent.path}${Platform.pathSeparator}bonfire.log'),
      // Fallback: temp dir.
      _safe(() =>
          '${Directory.systemTemp.path}${Platform.pathSeparator}bonfire.log'),
      // Last resort: user home.
      _safe(() =>
          '${Platform.environment['USERPROFILE'] ?? Platform.environment['HOME'] ?? '.'}${Platform.pathSeparator}bonfire.log'),
    ];

    for (final path in candidates) {
      if (path == null) continue;
      final f = File(path);
      _attempted.add(f);
      try {
        f.writeAsStringSync(
            '=== Bonfire boot ${DateTime.now().toIso8601String()} ===\n');
        _file = f;
        return;
      } catch (_) {}
    }
  }

  static String? _safe(String Function() compute) {
    try {
      return compute();
    } catch (_) {
      return null;
    }
  }

  static String diagPath() {
    if (_file != null) return _file!.path;
    if (_attempted.isEmpty) return '(none)';
    return _attempted.map((f) => f.path).join('|');
  }

  static void write(String line) {
    final stamped =
        '${DateTime.now().toIso8601String().substring(11, 23)} $line\n';
    try {
      _file?.writeAsStringSync(stamped, mode: FileMode.append);
    } catch (_) {}
    // Also try stderr (works in dev / `flutter run`).
    try {
      stderr.writeln(stamped.trim());
    } catch (_) {}
  }

  static void error(String label, Object e, [StackTrace? st]) {
    write('[ERR] $label: $e');
    if (st != null) write(st.toString());
  }
}
