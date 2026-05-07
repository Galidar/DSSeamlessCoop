/// Modal panel for the "install / update server" flow. Shown as a
/// bottom-sheet style overlay so it never feels like leaving the app.

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../state/app_state.dart';
import '../theme.dart';

class InstallPanel extends StatefulWidget {
  const InstallPanel({super.key});

  static Future<void> show(BuildContext context) {
    return showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => const _InstallPanelHost(),
    );
  }

  @override
  State<InstallPanel> createState() => _InstallPanelState();
}

class _InstallPanelHost extends StatelessWidget {
  const _InstallPanelHost();
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
          padding: const EdgeInsets.fromLTRB(28, 16, 28, 28),
          child: const InstallPanel(),
        ),
      ),
    );
  }
}

class _InstallPanelState extends State<InstallPanel> {
  bool _running = false;
  String? _error;

  Future<void> _install() async {
    setState(() {
      _running = true;
      _error = null;
    });
    try {
      await context.read<AppState>().installLatest();
      if (!mounted) return;
      // Close panel — the MY BONFIRES empty state guides the user from here.
      Navigator.of(context).pop();
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _running = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final installed = app.installStatus?.installed ?? false;
    final progress = app.downloadProgress;
    final recv = app.downloadBytesReceived ?? 0;
    final total = app.downloadBytesTotal;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        // Pill grabber.
        Center(
          child: Container(
            width: 36, height: 4,
            margin: const EdgeInsets.only(bottom: 18),
            decoration: BoxDecoration(
              color: BonfireColors.border,
              borderRadius: BorderRadius.circular(2),
            ),
          ),
        ),
        Text(installed ? 'Update server' : 'Install server',
            style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w600)),
        const SizedBox(height: 6),
        Text(
          installed
              ? 'Re-download the latest build to get bug fixes and new features.'
              : 'Bonfire will download the latest server build (~115 MB) and '
                'set up the local install.',
          style: const TextStyle(color: BonfireColors.textSecondary),
        ),
        const SizedBox(height: 24),

        // Progress / status block.
        Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: BonfireColors.surfaceHi,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: BonfireColors.border),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(
                    installed ? Icons.check_circle : Icons.download_outlined,
                    color: installed ? BonfireColors.ok : BonfireColors.accent,
                    size: 20,
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      _running
                          ? 'Downloading…'
                          : installed
                              ? 'Server is installed'
                              : 'Server is not installed',
                      style:
                          const TextStyle(fontWeight: FontWeight.w500),
                    ),
                  ),
                ],
              ),
              if (_running) ...[
                const SizedBox(height: 12),
                ClipRRect(
                  borderRadius: BorderRadius.circular(4),
                  child: LinearProgressIndicator(
                    value: progress,
                    minHeight: 6,
                  ),
                ),
                const SizedBox(height: 8),
                Text(
                  total != null
                      ? '${(recv / (1024 * 1024)).toStringAsFixed(1)} / ${(total / (1024 * 1024)).toStringAsFixed(1)} MB'
                      : '${(recv / (1024 * 1024)).toStringAsFixed(1)} MB',
                  style: const TextStyle(
                      fontSize: 12,
                      fontFamily: 'Consolas',
                      color: BonfireColors.textMuted),
                ),
              ] else if (installed && app.installStatus != null) ...[
                const SizedBox(height: 6),
                Text(
                  'Path: ${app.installStatus!.serverDirectory}',
                  style: const TextStyle(
                      fontSize: 11,
                      fontFamily: 'Consolas',
                      color: BonfireColors.textMuted),
                ),
              ],
            ],
          ),
        ),

        if (_error != null) ...[
          const SizedBox(height: 12),
          Container(
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: const Color(0x33C2563E),
              borderRadius: BorderRadius.circular(6),
              border: Border.all(color: BonfireColors.err),
            ),
            child: Row(
              children: [
                const Icon(Icons.error_outline,
                    color: BonfireColors.err, size: 18),
                const SizedBox(width: 8),
                Expanded(
                    child: Text(_error!,
                        style: const TextStyle(fontSize: 12))),
              ],
            ),
          ),
        ],

        const SizedBox(height: 24),
        Row(
          children: [
            const Spacer(),
            TextButton(
              onPressed: _running ? null : () => Navigator.of(context).pop(),
              child: const Text('Close'),
            ),
            const SizedBox(width: 8),
            FilledButton.icon(
              onPressed: _running ? null : _install,
              icon: const Icon(Icons.download, size: 16),
              label: Padding(
                padding:
                    const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                child: Text(installed ? 'Re-download latest' : 'Download'),
              ),
            ),
          ],
        ),
      ],
    );
  }
}
