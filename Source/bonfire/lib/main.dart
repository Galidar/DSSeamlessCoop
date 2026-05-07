/// Bonfire — Dark Souls Open Server companion (Galidar fork).
///
/// Entry point: spawns BonfireService.exe via stdio JSON-RPC, then runs
/// the Flutter UI on top of an [AppState] that wraps the RPC client.

import 'dart:io';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'diagnostics.dart';
import 'rpc/rpc_client.dart';
import 'screens/home_screen.dart';
import 'state/app_state.dart';
import 'theme.dart';

Future<void> main() async {
  // Hard-coded smoke test: prove main() runs at all and we have file write
  // permissions on this machine.
  try {
    File(r'C:\Users\Diux\Desktop\_bonfire_test\boot.txt').writeAsStringSync(
        'main() reached at ${DateTime.now()}\n');
  } catch (_) {}

  WidgetsFlutterBinding.ensureInitialized();
  BonfireLog.init();
  BonfireLog.write('main: started, log path = ${BonfireLog.diagPath()}');

  final servicePath = resolveServiceExePath();
  BonfireLog.write('main: servicePath=$servicePath');
  if (servicePath == null) {
    BonfireLog.write('main: BonfireService.exe not found, showing missing-service screen');
    runApp(const _ServiceMissingApp());
    return;
  }

  RpcClient? client;
  String? bootError;
  try {
    BonfireLog.write('main: spawning service…');
    client = await RpcClient.spawn(servicePath);
    BonfireLog.write('main: service spawned, sending ping…');
    final pong = await client.call('ping').timeout(const Duration(seconds: 3));
    BonfireLog.write('main: ping OK -> $pong');
  } catch (e, st) {
    bootError = e.toString();
    BonfireLog.error('boot', e, st);
    try {
      await client?.close();
    } catch (_) {}
    client = null;
  }

  if (client == null) {
    BonfireLog.write('main: showing boot-failed screen ($bootError)');
    runApp(_BootFailedApp(message: bootError ?? 'unknown error'));
    return;
  }

  BonfireLog.write('main: launching BonfireApp');
  runApp(BonfireApp(client: client));
}

class BonfireApp extends StatelessWidget {
  final RpcClient client;
  const BonfireApp({super.key, required this.client});

  @override
  Widget build(BuildContext context) {
    return ChangeNotifierProvider(
      create: (_) => AppState(client),
      child: MaterialApp(
        title: 'Bonfire',
        debugShowCheckedModeBanner: false,
        theme: BonfireTheme.dark,
        home: const HomeScreen(),
      ),
    );
  }
}

class _ServiceMissingApp extends StatelessWidget {
  const _ServiceMissingApp();
  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: BonfireTheme.dark,
      home: Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(40),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Icon(Icons.warning_amber_rounded,
                    color: BonfireColors.warn, size: 56),
                const SizedBox(height: 18),
                const Text('BonfireService.exe not found',
                    style: TextStyle(
                        fontSize: 20, fontWeight: FontWeight.w600)),
                const SizedBox(height: 10),
                const Text(
                  'Bonfire could not locate its companion service. Make sure '
                  'BonfireService.exe sits next to Bonfire.exe.',
                  textAlign: TextAlign.center,
                  style: TextStyle(color: BonfireColors.textSecondary),
                ),
                const SizedBox(height: 16),
                Text(
                  'Looked next to: ${Platform.resolvedExecutable}',
                  style: const TextStyle(
                      fontSize: 11,
                      fontFamily: 'Consolas',
                      color: BonfireColors.textMuted),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _BootFailedApp extends StatelessWidget {
  final String message;
  const _BootFailedApp({required this.message});
  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: BonfireTheme.dark,
      home: Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(40),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Icon(Icons.error_outline,
                    color: BonfireColors.err, size: 56),
                const SizedBox(height: 18),
                const Text('Bonfire failed to start',
                    style: TextStyle(
                        fontSize: 20, fontWeight: FontWeight.w600)),
                const SizedBox(height: 10),
                const Text(
                  "Couldn't establish a connection to the local service.",
                  textAlign: TextAlign.center,
                  style: TextStyle(color: BonfireColors.textSecondary),
                ),
                const SizedBox(height: 12),
                Text(message,
                    textAlign: TextAlign.center,
                    style: const TextStyle(
                        fontSize: 12,
                        fontFamily: 'Consolas',
                        color: BonfireColors.textMuted)),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
