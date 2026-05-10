/// Inline configuration panel — edit server name, description, password,
/// IPs (auto-detect or manual override), and advertise-flag without ever
/// leaving the home screen.

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../state/app_state.dart';
import '../theme.dart';

class ConfigPanel extends StatefulWidget {
  const ConfigPanel({super.key});

  static Future<void> show(BuildContext context) {
    return showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => const _ConfigHost(),
    );
  }

  @override
  State<ConfigPanel> createState() => _ConfigPanelState();
}

class _ConfigHost extends StatelessWidget {
  const _ConfigHost();
  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.75,
      maxChildSize: 0.9,
      minChildSize: 0.5,
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
          child: const ConfigPanel(),
        ),
      ),
    );
  }
}

class _ConfigPanelState extends State<ConfigPanel> {
  late final TextEditingController _nameCtrl;
  late final TextEditingController _descCtrl;
  late final TextEditingController _pwdCtrl;
  late final TextEditingController _publicIpCtrl;
  late final TextEditingController _privateIpCtrl;
  late final TextEditingController _relayHostCtrl;
  late final TextEditingController _relayPortCtrl;
  late final TextEditingController _relayTokenCtrl;
  late final TextEditingController _webuiUserCtrl;
  late final TextEditingController _webuiPwdCtrl;

  bool _autoIp = true;
  bool _advertise = true;
  bool _relayEnabled = false;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final cfg = context.read<AppState>().config;
    _nameCtrl = TextEditingController(text: cfg?.name ?? 'My DS3OS Server');
    _descCtrl = TextEditingController(text: cfg?.description ?? '');
    _pwdCtrl = TextEditingController(text: cfg?.password ?? '');

    final app = context.read<AppState>();
    final wan = app.wanIp ?? cfg?.publicIp ?? '';
    final lan = app.lanIp ?? cfg?.privateIp ?? '';
    _publicIpCtrl = TextEditingController(text: wan);
    _privateIpCtrl = TextEditingController(text: lan);
    _relayEnabled = cfg?.relayEnabled ?? false;
    _relayHostCtrl = TextEditingController(text: cfg?.relayControlHost ?? '');
    _relayPortCtrl = TextEditingController(
        text: (cfg?.relayControlPort ?? 50030).toString());
    _relayTokenCtrl =
        TextEditingController(text: cfg?.relayControlToken ?? '');
    // Default WebUI creds to admin/admin if unset, so the user has
    // SOMETHING to log in with the first time. They can override either.
    _webuiUserCtrl = TextEditingController(
        text: (cfg?.webuiUsername.isNotEmpty ?? false)
            ? cfg!.webuiUsername
            : 'admin');
    _webuiPwdCtrl = TextEditingController(
        text: (cfg?.webuiPassword.isNotEmpty ?? false)
            ? cfg!.webuiPassword
            : 'admin');

    _advertise = cfg?.advertise ?? true;

    // If saved IPs differ from detected ones, default to manual.
    if (cfg != null &&
        cfg.publicIp.isNotEmpty &&
        app.wanIp != null &&
        cfg.publicIp != app.wanIp &&
        cfg.publicIp != '__WAN_IP__') {
      _autoIp = false;
    }
  }

  @override
  void dispose() {
    _nameCtrl.dispose();
    _descCtrl.dispose();
    _pwdCtrl.dispose();
    _publicIpCtrl.dispose();
    _privateIpCtrl.dispose();
    _relayHostCtrl.dispose();
    _relayPortCtrl.dispose();
    _relayTokenCtrl.dispose();
    _webuiUserCtrl.dispose();
    _webuiPwdCtrl.dispose();
    super.dispose();
  }

  Future<void> _redetect() async {
    await context.read<AppState>().refreshIps();
    final app = context.read<AppState>();
    if (app.wanIp != null) _publicIpCtrl.text = app.wanIp!;
    if (app.lanIp != null) _privateIpCtrl.text = app.lanIp!;
    setState(() {});
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final app = context.read<AppState>();
      // Game type is intentionally NOT mutable from here — it's fixed at
      // bonfire creation (DS II / DS III tab). Send the existing value so
      // the backend's regex-replace doesn't blank the field.
      final newCfg = ServerConfig(
        name: _nameCtrl.text.trim(),
        description: _descCtrl.text.trim(),
        password: _pwdCtrl.text,
        gameType: app.config?.gameType ?? 'DarkSouls2',
        publicIp: _autoIp
            ? (app.wanIp ?? _publicIpCtrl.text)
            : _publicIpCtrl.text.trim(),
        privateIp: _autoIp
            ? (app.lanIp ?? _privateIpCtrl.text)
            : _privateIpCtrl.text.trim(),
        relayEnabled: _relayEnabled,
        relayControlHost: _relayHostCtrl.text.trim(),
        relayControlPort: int.tryParse(_relayPortCtrl.text.trim()) ?? 50030,
        relayControlToken: _relayTokenCtrl.text,
        relayPublicHostname: app.config?.relayPublicHostname ?? '',
        relayLoginPort: app.config?.relayLoginPort ?? 0,
        relayAuthPort: app.config?.relayAuthPort ?? 0,
        relayGamePort: app.config?.relayGamePort ?? 0,
        advertise: _advertise,
        webuiUsername: _webuiUserCtrl.text.trim(),
        webuiPassword: _webuiPwdCtrl.text,
        exists: true,
      );
      await app.saveConfig(newCfg);
      if (mounted) Navigator.of(context).pop();
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Center(
          child: Container(
            width: 36,
            height: 4,
            margin: const EdgeInsets.only(bottom: 18),
            decoration: BoxDecoration(
              color: BonfireColors.border,
              borderRadius: BorderRadius.circular(2),
            ),
          ),
        ),
        const Text('Server configuration',
            style: TextStyle(fontSize: 20, fontWeight: FontWeight.w600)),
        const SizedBox(height: 6),
        const Text('All settings save to config.json instantly.',
            style: TextStyle(color: BonfireColors.textSecondary)),
        const SizedBox(height: 24),

        // Identity
        const _SectionTitle('Identity'),
        const SizedBox(height: 8),
        _LabelledField(
          label: 'Server name',
          controller: _nameCtrl,
        ),
        const SizedBox(height: 12),
        _LabelledField(
          label: 'Description',
          controller: _descCtrl,
        ),
        const SizedBox(height: 12),
        _LabelledField(
          label: 'Password',
          controller: _pwdCtrl,
          hint: 'Leave blank for none',
          obscure: false,
        ),
        const SizedBox(height: 12),
        // The bonfire's game is fixed at creation (you create it from the
        // DS II or DS III tab) — changing it here would just desync the
        // profile from its tab. To run on the other game, create a new
        // bonfire under that tab.
        SwitchListTile.adaptive(
          contentPadding: EdgeInsets.zero,
          title: const Text('List publicly'),
          subtitle: const Text('Hide if you only share with friends',
              style: TextStyle(fontSize: 11, color: BonfireColors.textMuted)),
          value: _advertise,
          onChanged: (v) => setState(() => _advertise = v),
        ),

        const SizedBox(height: 24),
        const _SectionTitle('Network'),
        const SizedBox(height: 4),
        Row(
          children: [
            Expanded(
              child: RadioListTile<bool>(
                contentPadding: EdgeInsets.zero,
                value: true,
                groupValue: _autoIp,
                onChanged: (v) => setState(() => _autoIp = v ?? true),
                title: const Text('Auto-detect'),
                subtitle: const Text('Recommended at home',
                    style: TextStyle(fontSize: 11)),
                dense: true,
              ),
            ),
            Expanded(
              child: RadioListTile<bool>(
                contentPadding: EdgeInsets.zero,
                value: false,
                groupValue: _autoIp,
                onChanged: (v) => setState(() => _autoIp = v ?? false),
                title: const Text('Manual'),
                subtitle: const Text('Paid hosting / VPN',
                    style: TextStyle(fontSize: 11)),
                dense: true,
              ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        _LabelledField(
          label: 'Public IP (WAN)',
          controller: _publicIpCtrl,
          enabled: !_autoIp,
          monospace: true,
        ),
        const SizedBox(height: 12),
        _LabelledField(
          label: 'Private IP (LAN)',
          controller: _privateIpCtrl,
          enabled: !_autoIp,
          monospace: true,
        ),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            onPressed: _autoIp ? _redetect : null,
            icon: const Icon(Icons.refresh, size: 14),
            label: const Text('Re-detect IPs', style: TextStyle(fontSize: 12)),
          ),
        ),
        const SizedBox(height: 12),
        SwitchListTile.adaptive(
          contentPadding: EdgeInsets.zero,
          title: const Text('Use Bonfire Relay'),
          subtitle: const Text('For CGNAT or networks that cannot open ports',
              style: TextStyle(fontSize: 11, color: BonfireColors.textMuted)),
          value: _relayEnabled,
          onChanged: (v) => setState(() => _relayEnabled = v),
        ),
        if (_relayEnabled) ...[
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                flex: 3,
                child: _LabelledField(
                  label: 'Relay host',
                  controller: _relayHostCtrl,
                  hint: 'public relay IPv4 or host',
                  monospace: true,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: _LabelledField(
                  label: 'Port',
                  controller: _relayPortCtrl,
                  monospace: true,
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          _LabelledField(
            label: 'Relay token',
            controller: _relayTokenCtrl,
            hint: 'Optional',
            obscure: true,
          ),
          if ((context.watch<AppState>().config?.relayPublicHostname ??
                  '')
              .isNotEmpty) ...[
            const SizedBox(height: 8),
            Text(
              'Last relay: ${context.watch<AppState>().config!.relayPublicHostname}:${context.watch<AppState>().config!.relayLoginPort}',
              style: const TextStyle(
                  fontSize: 11,
                  color: BonfireColors.textMuted,
                  fontFamily: 'monospace'),
            ),
          ],
        ],

        const SizedBox(height: 24),
        const _SectionTitle('Web admin'),
        const SizedBox(height: 4),
        const Text(
          'Credentials for the server\'s web dashboard '
          '(http://localhost:50005).',
          style: TextStyle(fontSize: 11, color: BonfireColors.textMuted),
        ),
        const SizedBox(height: 12),
        Row(
          children: [
            Expanded(
              child: _LabelledField(
                label: 'Username',
                controller: _webuiUserCtrl,
                hint: 'admin',
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: _LabelledField(
                label: 'Password',
                controller: _webuiPwdCtrl,
                hint: 'admin',
                obscure: true,
              ),
            ),
          ],
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
            child: Text(_error!, style: const TextStyle(fontSize: 12)),
          ),
        ],

        const SizedBox(height: 24),
        Row(
          children: [
            const Spacer(),
            TextButton(
              onPressed: _saving ? null : () => Navigator.of(context).pop(),
              child: const Text('Cancel'),
            ),
            const SizedBox(width: 8),
            FilledButton.icon(
              onPressed: _saving ? null : _save,
              icon: const Icon(Icons.save_outlined, size: 16),
              label: const Padding(
                padding: EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                child: Text('Save changes'),
              ),
            ),
          ],
        ),

        // ───── Danger Zone ─────
        const SizedBox(height: 32),
        Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: const Color(0x14C2563E), // 8% red tint
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: BonfireColors.err.withOpacity(0.5)),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: const [
                  Icon(Icons.warning_amber_rounded,
                      size: 16, color: BonfireColors.err),
                  SizedBox(width: 6),
                  Text('Danger zone',
                      style: TextStyle(
                          fontSize: 11,
                          letterSpacing: 0,
                          color: BonfireColors.err,
                          fontWeight: FontWeight.w700)),
                ],
              ),
              const SizedBox(height: 12),

              // Reset to defaults
              Row(
                children: [
                  const Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text('Reset configuration',
                            style: TextStyle(
                                fontWeight: FontWeight.w600, fontSize: 13)),
                        SizedBox(height: 2),
                        Text(
                          'Wipe config.json + server keys + database, leaving '
                          'binaries intact. The next start regenerates fresh '
                          'defaults.',
                          style: TextStyle(
                              fontSize: 11, color: BonfireColors.textMuted),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 12),
                  OutlinedButton(
                    style: OutlinedButton.styleFrom(
                      foregroundColor: BonfireColors.err,
                      side: const BorderSide(color: BonfireColors.err),
                    ),
                    onPressed: _saving ? null : _confirmReset,
                    child: const Text('Reset'),
                  ),
                ],
              ),
              const Divider(height: 24),

              // Uninstall
              Row(
                children: [
                  const Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text('Uninstall server',
                            style: TextStyle(
                                fontWeight: FontWeight.w600, fontSize: 13)),
                        SizedBox(height: 2),
                        Text(
                          'Stop and delete the entire Server folder (binaries, '
                          'config, keys, database). You can re-install later '
                          'from the home screen.',
                          style: TextStyle(
                              fontSize: 11, color: BonfireColors.textMuted),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 12),
                  FilledButton(
                    style: FilledButton.styleFrom(
                      backgroundColor: BonfireColors.err,
                    ),
                    onPressed: _saving ? null : _confirmUninstall,
                    child: const Text('Uninstall'),
                  ),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }

  Future<void> _confirmReset() async {
    final ok = await _confirm(
      title: 'Reset server configuration?',
      body:
          "This wipes config.json, the server's RSA keypair, and its database. "
          'Players who saved your server will need to reconnect. Cannot be '
          'undone.',
      destructiveLabel: 'Reset',
    );
    if (!ok) return;
    setState(() => _saving = true);
    try {
      await context.read<AppState>().resetServerConfig();
      if (mounted) {
        Navigator.of(context).pop();
        ScaffoldMessenger.of(context).showSnackBar(
            const SnackBar(content: Text('Configuration reset.')));
      }
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _confirmUninstall() async {
    final ok = await _confirm(
      title: 'Uninstall the server?',
      body:
          'This deletes the entire Server folder — binaries, config.json, RSA '
          "keys, and database. The Bonfire app and your game settings remain. "
          "You can install the server again from the home screen.",
      destructiveLabel: 'Uninstall',
    );
    if (!ok) return;
    setState(() => _saving = true);
    try {
      await context.read<AppState>().uninstallServer();
      if (mounted) {
        Navigator.of(context).pop();
        ScaffoldMessenger.of(context)
            .showSnackBar(const SnackBar(content: Text('Server uninstalled.')));
      }
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<bool> _confirm({
    required String title,
    required String body,
    required String destructiveLabel,
  }) async {
    final result = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        backgroundColor: BonfireColors.surface,
        title: Text(title),
        content: Text(body, style: const TextStyle(height: 1.4)),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: BonfireColors.err),
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(destructiveLabel),
          ),
        ],
      ),
    );
    return result ?? false;
  }
}

class _SectionTitle extends StatelessWidget {
  final String text;
  const _SectionTitle(this.text);
  @override
  Widget build(BuildContext context) {
    return Text(
      text.toUpperCase(),
      style: const TextStyle(
        fontSize: 10,
        letterSpacing: 0,
        color: BonfireColors.textMuted,
        fontWeight: FontWeight.w700,
      ),
    );
  }
}

class _LabelledField extends StatelessWidget {
  final String label;
  final String? hint;
  final TextEditingController controller;
  final bool enabled;
  final bool obscure;
  final bool monospace;

  const _LabelledField({
    required this.label,
    required this.controller,
    this.hint,
    this.enabled = true,
    this.obscure = false,
    this.monospace = false,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(left: 4, bottom: 4),
          child: Text(label,
              style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: BonfireColors.textSecondary)),
        ),
        TextField(
          controller: controller,
          enabled: enabled,
          obscureText: obscure,
          style: TextStyle(
              fontFamily: monospace ? 'Consolas' : null, fontSize: 14),
          decoration: InputDecoration(hintText: hint),
        ),
      ],
    );
  }
}

class _DropdownField extends StatelessWidget {
  final String label;
  final String value;
  final List<String> items;
  final ValueChanged<String?> onChanged;
  const _DropdownField({
    required this.label,
    required this.value,
    required this.items,
    required this.onChanged,
  });
  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(left: 4, bottom: 4),
          child: Text(label,
              style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: BonfireColors.textSecondary)),
        ),
        DropdownButtonFormField<String>(
          value: value,
          items: items
              .map((v) => DropdownMenuItem(value: v, child: Text(v)))
              .toList(),
          onChanged: onChanged,
          dropdownColor: BonfireColors.surfaceHi,
        ),
      ],
    );
  }
}
