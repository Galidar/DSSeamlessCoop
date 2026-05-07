/// JSON-RPC 2.0 client over stdio.
///
/// Spawns BonfireService.exe and communicates with it via line-delimited
/// JSON. Each [call] returns a Future that resolves when the matching
/// response arrives. Server-initiated notifications (no `id`) are surfaced
/// through the [notifications] stream so widgets can subscribe to e.g.
/// `download.progress`.

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../diagnostics.dart';

class RpcException implements Exception {
  final int code;
  final String message;
  RpcException(this.code, this.message);
  @override
  String toString() => 'RpcException($code): $message';
}

class RpcNotification {
  final String method;
  final Map<String, dynamic>? params;
  RpcNotification(this.method, this.params);
}

class RpcClient {
  RpcClient._(this._process, this._stdin) {
    _process.stdout
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(_onLine, onDone: _onDone);
    _process.stderr
        .transform(utf8.decoder)
        .listen((line) {
      BonfireLog.write('[svc.stderr] ${line.trimRight()}');
    });
  }

  static Future<RpcClient> spawn(String executablePath) async {
    final proc = await Process.start(
      executablePath,
      const [],
      // We want stdin/stdout for protocol; leave stderr passthrough.
      mode: ProcessStartMode.normal,
    );
    return RpcClient._(proc, proc.stdin);
  }

  final Process _process;
  final IOSink _stdin;
  int _nextId = 1;
  final Map<int, Completer<dynamic>> _pending = {};
  final StreamController<RpcNotification> _notifications =
      StreamController<RpcNotification>.broadcast();
  bool _closed = false;

  // Serialise writes. `IOSink.flush()` internally binds the sink while it
  // runs, so two concurrent `writeln + flush` pairs throw
  // "StreamSink is bound to a stream". We chain every write through a
  // single future so they happen one after another.
  Future<void> _writeChain = Future<void>.value();

  Stream<RpcNotification> get notifications => _notifications.stream;

  Future<int> get exitCode => _process.exitCode;

  /// Sends an RPC call and returns the parsed JSON `result`. Throws
  /// [RpcException] on protocol-level errors.
  Future<dynamic> call(String method, [Map<String, dynamic>? params]) async {
    if (_closed) {
      throw StateError('RpcClient closed');
    }
    final id = _nextId++;
    final completer = Completer<dynamic>();
    _pending[id] = completer;

    final payload = <String, dynamic>{
      'jsonrpc': '2.0',
      'id': id,
      'method': method,
      if (params != null) 'params': params,
    };
    final encoded = jsonEncode(payload);

    // Append this write to the serial write chain.
    final next = _writeChain.then((_) async {
      _stdin.writeln(encoded);
      await _stdin.flush();
    });
    // Swallow errors in the chain so one bad write doesn't poison the
    // whole pipeline; surface them on the caller's future instead.
    _writeChain = next.catchError((_) {});
    try {
      await next;
    } catch (e) {
      _pending.remove(id);
      rethrow;
    }
    return completer.future;
  }

  void _onLine(String line) {
    line = line.trim();
    if (line.isEmpty) return;
    BonfireLog.write('[svc.in ] $line');
    try {
      final obj = jsonDecode(line) as Map<String, dynamic>;
      final id = obj['id'];
      if (id is int) {
        final completer = _pending.remove(id);
        if (completer != null) {
          if (obj.containsKey('error')) {
            final err = obj['error'] as Map<String, dynamic>;
            completer.completeError(RpcException(
              (err['code'] as num?)?.toInt() ?? -1,
              (err['message'] as String?) ?? 'unknown error',
            ));
          } else {
            completer.complete(obj['result']);
          }
        }
      } else {
        // Notification (no id).
        final method = obj['method'] as String?;
        if (method != null) {
          _notifications.add(RpcNotification(
            method,
            obj['params'] is Map<String, dynamic>
                ? obj['params'] as Map<String, dynamic>
                : null,
          ));
        }
      }
    } catch (e) {
      BonfireLog.error('parse line', e);
    }
  }

  void _onDone() {
    _closed = true;
    for (final c in _pending.values) {
      c.completeError(StateError('Service exited'));
    }
    _pending.clear();
    _notifications.close();
  }

  Future<void> close() async {
    if (_closed) return;
    try {
      await call('service.shutdown').timeout(const Duration(seconds: 2));
    } catch (_) {
      // best effort
    }
    try {
      _process.kill();
    } catch (_) {}
  }
}
