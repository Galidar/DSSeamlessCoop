/// App-wide state — wraps the RpcClient, exposes typed wrappers for each
/// RPC method, and tracks UI-relevant state (server status, install state,
/// download progress, etc.) via ChangeNotifier so widgets can rebuild.

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';

import '../rpc/rpc_client.dart';

/// Strips the DS2 Native Session manifest line (`%%BNS-DS2-V1%%{...}`) and
/// everything after it from a server description before it reaches the UI.
///
/// The manifest is metadata BonfireService stamps into ServerDescription so the
/// master server (and other Bonfire instances) can detect / parse a DS2 native
/// session; it is not meant for human eyes and would otherwise show up as raw
/// JSON in the bonfire list and search results.
String _stripBnsSentinel(String desc) {
  if (desc.isEmpty) return desc;
  const sentinel = '%%BNS-DS2-V1%%';
  final idx = desc.indexOf(sentinel);
  if (idx < 0) return desc;
  if (idx == 0) return '';
  final lineStart = desc.lastIndexOf('\n', idx - 1);
  if (lineStart < 0) return '';
  return desc.substring(0, lineStart).trimRight();
}

class ServerInstallStatus {
  final bool installed;
  final String serverDirectory;
  final String installRoot;
  ServerInstallStatus({
    required this.installed,
    required this.serverDirectory,
    required this.installRoot,
  });
  factory ServerInstallStatus.fromJson(Map<String, dynamic> j) =>
      ServerInstallStatus(
        installed: j['installed'] as bool? ?? false,
        serverDirectory: j['server_directory'] as String? ?? '',
        installRoot: j['install_root'] as String? ?? '',
      );
}

class ServerLiveStatus {
  final bool running;
  final int? pid;
  final DateTime? startedAt;
  final bool relayRunning;
  final String relayHostname;
  final int relayLoginPort;
  ServerLiveStatus({
    required this.running,
    this.pid,
    this.startedAt,
    required this.relayRunning,
    required this.relayHostname,
    required this.relayLoginPort,
  });
  factory ServerLiveStatus.fromJson(Map<String, dynamic> j) => ServerLiveStatus(
        running: j['running'] as bool? ?? false,
        pid: (j['pid'] as num?)?.toInt(),
        startedAt: j['started_at'] != null
            ? DateTime.tryParse(j['started_at'] as String)
            : null,
        relayRunning: j['relay_running'] as bool? ?? false,
        relayHostname: j['relay_hostname'] as String? ?? '',
        relayLoginPort: (j['relay_login_port'] as num?)?.toInt() ?? 0,
      );
}

class ServerConfig {
  String name;
  String description;
  String password;
  String gameType;
  String publicIp;
  String privateIp;
  bool relayEnabled;
  String relayControlHost;
  int relayControlPort;
  String relayControlToken;
  String relayPublicHostname;
  int relayLoginPort;
  int relayAuthPort;
  int relayGamePort;
  bool advertise;
  String webuiUsername;
  String webuiPassword;
  final bool exists;
  ServerConfig({
    required this.name,
    required this.description,
    required this.password,
    required this.gameType,
    required this.publicIp,
    required this.privateIp,
    required this.relayEnabled,
    required this.relayControlHost,
    required this.relayControlPort,
    required this.relayControlToken,
    required this.relayPublicHostname,
    required this.relayLoginPort,
    required this.relayAuthPort,
    required this.relayGamePort,
    required this.advertise,
    required this.webuiUsername,
    required this.webuiPassword,
    required this.exists,
  });
  factory ServerConfig.fromJson(Map<String, dynamic> j) => ServerConfig(
        name: j['server_name'] as String? ?? '',
        description: _stripBnsSentinel(j['server_description'] as String? ?? ''),
        password: j['password'] as String? ?? '',
        gameType: j['game_type'] as String? ?? 'DarkSouls2',
        publicIp: j['server_hostname'] as String? ?? '',
        privateIp: j['server_private_hostname'] as String? ?? '',
        relayEnabled: j['relay_enabled'] as bool? ?? false,
        relayControlHost: j['relay_control_host'] as String? ?? '',
        relayControlPort: (j['relay_control_port'] as num?)?.toInt() ?? 50030,
        relayControlToken: j['relay_control_token'] as String? ?? '',
        relayPublicHostname: j['relay_public_hostname'] as String? ?? '',
        relayLoginPort: (j['relay_login_port'] as num?)?.toInt() ?? 0,
        relayAuthPort: (j['relay_auth_port'] as num?)?.toInt() ?? 0,
        relayGamePort: (j['relay_game_port'] as num?)?.toInt() ?? 0,
        advertise: j['advertise'] as bool? ?? true,
        webuiUsername: j['webui_username'] as String? ?? '',
        webuiPassword: j['webui_password'] as String? ?? '',
        exists: j['exists'] as bool? ?? false,
      );

  Map<String, dynamic> toJson() => {
        'server_name': name,
        'server_description': description,
        'password': password,
        'game_type': gameType,
        'server_hostname': publicIp,
        'server_private_hostname': privateIp,
        'relay_enabled': relayEnabled,
        'relay_control_host': relayControlHost,
        'relay_control_port': relayControlPort,
        'relay_control_token': relayControlToken,
        'advertise': advertise,
        'webui_username': webuiUsername,
        'webui_password': webuiPassword,
      };
}

class FirewallStatus {
  final bool allInstalled;
  FirewallStatus({required this.allInstalled});
  factory FirewallStatus.fromJson(Map<String, dynamic> j) =>
      FirewallStatus(allInstalled: j['all_installed'] as bool? ?? false);
}

class AppUpdateStatus {
  final String currentVersion;
  final String latestVersion;
  final String latestTag;
  final String releaseUrl;
  final String assetName;
  final String assetUrl;
  final int assetSize;
  final bool updateAvailable;

  AppUpdateStatus({
    required this.currentVersion,
    required this.latestVersion,
    required this.latestTag,
    required this.releaseUrl,
    required this.assetName,
    required this.assetUrl,
    required this.assetSize,
    required this.updateAvailable,
  });

  factory AppUpdateStatus.fromJson(Map<String, dynamic> j) => AppUpdateStatus(
        currentVersion: j['current_version'] as String? ?? 'unknown',
        latestVersion: j['latest_version'] as String? ?? '',
        latestTag: j['latest_tag'] as String? ?? '',
        releaseUrl: j['release_url'] as String? ?? '',
        assetName: j['asset_name'] as String? ?? '',
        assetUrl: j['asset_url'] as String? ?? '',
        assetSize: (j['asset_size'] as num?)?.toInt() ?? 0,
        updateAvailable: j['update_available'] as bool? ?? false,
      );
}

class Profile {
  final String id;
  final String name;
  final String gameType;
  final String createdAt;
  final bool isActive;
  Profile({
    required this.id,
    required this.name,
    required this.gameType,
    required this.createdAt,
    required this.isActive,
  });
  factory Profile.fromJson(Map<String, dynamic> j) => Profile(
        id: j['id'] as String? ?? '',
        name: j['name'] as String? ?? '',
        gameType: j['game_type'] as String? ?? 'DarkSouls2',
        createdAt: j['created_at'] as String? ?? '',
        isActive: j['is_active'] as bool? ?? false,
      );
}

class GameSettings {
  final String ds1ExePath;
  final String ds2ExePath;
  final String ds3ExePath;
  final bool ds1Valid;
  final bool ds2Valid;
  final bool ds3Valid;
  final bool useSeparateSaves;
  GameSettings({
    required this.ds1ExePath,
    required this.ds2ExePath,
    required this.ds3ExePath,
    required this.ds1Valid,
    required this.ds2Valid,
    required this.ds3Valid,
    required this.useSeparateSaves,
  });
  factory GameSettings.fromJson(Map<String, dynamic> j) => GameSettings(
        ds1ExePath: j['ds1_exe_path'] as String? ?? '',
        ds2ExePath: j['ds2_exe_path'] as String? ?? '',
        ds3ExePath: j['ds3_exe_path'] as String? ?? '',
        ds1Valid: j['ds1_exe_valid'] as bool? ?? false,
        ds2Valid: j['ds2_exe_valid'] as bool? ?? false,
        ds3Valid: j['ds3_exe_valid'] as bool? ?? false,
        useSeparateSaves: j['use_separate_saves'] as bool? ?? true,
      );
  String pathFor(String gameType) {
    if (gameType == 'DarkSouls1') return ds1ExePath;
    if (gameType == 'DarkSouls3') return ds3ExePath;
    return ds2ExePath;
  }

  bool validFor(String gameType) {
    if (gameType == 'DarkSouls1') return ds1Valid;
    if (gameType == 'DarkSouls3') return ds3Valid;
    return ds2Valid;
  }
}

/// Parsed payload of the `%%BNS-DS2-V1%%` sentinel embedded by
/// BonfireService into `ServerConfig.ServerDescription`. Mirrors the schema
/// in `Source/BonfireService/Modules/Ds2NativeSession.cs::Manifest`.
///
/// `null` if a remote server doesn't carry the sentinel (i.e. it's not a
/// Bonfire DS2 native session host or it's currently in solo mode).
class Ds2NativeSessionManifest {
  final String sessionId;
  final String mode; // host | guest | invader | solo
  final String stage;
  final String intent;
  final String rulePreset;
  final int tauntCount;
  final int infectionCount;
  final int curseCount;
  final int recoveryCount;
  final int runtimeVersion;
  final DateTime? stampedAt;

  const Ds2NativeSessionManifest({
    required this.sessionId,
    required this.mode,
    required this.stage,
    required this.intent,
    required this.rulePreset,
    required this.tauntCount,
    required this.infectionCount,
    required this.curseCount,
    required this.recoveryCount,
    required this.runtimeVersion,
    required this.stampedAt,
  });

  static const String sentinel = '%%BNS-DS2-V1%%';

  /// Extracts and decodes the manifest from a raw `ServerDescription` value.
  /// Returns `null` if the sentinel is absent or the JSON payload is invalid.
  static Ds2NativeSessionManifest? tryParseFromDescription(String desc) {
    if (desc.isEmpty) return null;
    final idx = desc.indexOf(sentinel);
    if (idx < 0) return null;
    final jsonStart = idx + sentinel.length;
    if (jsonStart >= desc.length) return null;
    final endIdx = desc.indexOf('\n', jsonStart);
    final raw = (endIdx < 0
            ? desc.substring(jsonStart)
            : desc.substring(jsonStart, endIdx))
        .trim();
    try {
      final node = jsonDecode(raw);
      if (node is! Map<String, dynamic>) return null;
      return Ds2NativeSessionManifest(
        sessionId: (node['session_id'] as String?) ?? '',
        mode: (node['mode'] as String?) ?? 'solo',
        stage: (node['stage'] as String?) ?? '',
        intent: (node['intent'] as String?) ?? '',
        rulePreset: (node['rules'] as String?) ?? '',
        tauntCount: (node['taunt'] as num?)?.toInt() ?? 0,
        infectionCount: (node['infection'] as num?)?.toInt() ?? 0,
        curseCount: (node['curse'] as num?)?.toInt() ?? 0,
        recoveryCount: (node['recovery'] as num?)?.toInt() ?? 0,
        runtimeVersion: (node['runtime_ver'] as num?)?.toInt() ?? 0,
        stampedAt: DateTime.tryParse((node['ts'] as String?) ?? ''),
      );
    } catch (_) {
      return null;
    }
  }

  bool get isHost => mode.toLowerCase() == 'host';
  bool get isGuest => mode.toLowerCase() == 'guest';
  bool get isInvader => mode.toLowerCase() == 'invader';
}

/// Client-side mirror of the BonfireService `Ds2NativeJoinTarget.JoinTarget`.
/// Populated by `ds2_runtime.get_join_target` / `set_join_target` RPCs and
/// rendered in the UI as a "TARGET ARMED" banner + chip on the matching row.
class Ds2NativeJoinTargetSnapshot {
  final String serverId;
  final String serverName;
  final String hostname;
  final String privateHostname;
  final int port;
  final bool passwordSet;
  final String gameType;
  final String sessionId;
  final String sessionMode;
  final DateTime? armedAtUtc;

  const Ds2NativeJoinTargetSnapshot({
    required this.serverId,
    required this.serverName,
    required this.hostname,
    required this.privateHostname,
    required this.port,
    required this.passwordSet,
    required this.gameType,
    required this.sessionId,
    required this.sessionMode,
    required this.armedAtUtc,
  });

  factory Ds2NativeJoinTargetSnapshot.fromJson(Map<String, dynamic> j) =>
      Ds2NativeJoinTargetSnapshot(
        serverId: j['server_id'] as String? ?? '',
        serverName: j['server_name'] as String? ?? '',
        hostname: j['hostname'] as String? ?? '',
        privateHostname: j['private_hostname'] as String? ?? '',
        port: (j['port'] as num?)?.toInt() ?? 0,
        passwordSet: j['password_set'] as bool? ?? false,
        gameType: j['game_type'] as String? ?? '',
        sessionId: j['session_id'] as String? ?? '',
        sessionMode: j['session_mode'] as String? ?? '',
        armedAtUtc: DateTime.tryParse((j['armed_at_utc'] as String?) ?? ''),
      );
}

class PublicServer {
  final String id;
  final String name;
  final String description;
  final String gameType;
  final int playerCount;
  final bool passwordRequired;
  final String hostname;
  final String ipAddress;
  final bool isShard;
  final bool isRelayed;
  final bool allowSharding;
  final String modsWhitelist;
  final String modsBlacklist;
  final String modsRequired;
  final Ds2NativeSessionManifest? bnsManifest;

  PublicServer({
    required this.id,
    required this.name,
    required this.description,
    required this.gameType,
    required this.playerCount,
    required this.passwordRequired,
    required this.hostname,
    required this.ipAddress,
    required this.isShard,
    required this.isRelayed,
    required this.allowSharding,
    required this.modsWhitelist,
    required this.modsBlacklist,
    required this.modsRequired,
    this.bnsManifest,
  });

  factory PublicServer.fromJson(Map<String, dynamic> j) {
    final rawDesc = j['description'] as String? ?? '';
    return PublicServer(
      id: j['id'] as String? ?? '',
      name: j['name'] as String? ?? '(unnamed)',
      description: _stripBnsSentinel(rawDesc),
      gameType: j['game_type'] as String? ?? '',
      playerCount: (j['player_count'] as num?)?.toInt() ?? 0,
      passwordRequired: j['password_required'] as bool? ?? false,
      hostname: j['hostname'] as String? ?? '',
      ipAddress: j['ip_address'] as String? ?? '',
      isShard: j['is_shard'] as bool? ?? false,
      isRelayed: j['is_relayed'] as bool? ?? false,
      allowSharding: j['allow_sharding'] as bool? ?? false,
      modsWhitelist: j['mods_whitelist'] as String? ?? '',
      modsBlacklist: j['mods_blacklist'] as String? ?? '',
      modsRequired: j['mods_required'] as String? ?? '',
      bnsManifest: Ds2NativeSessionManifest.tryParseFromDescription(rawDesc),
    );
  }

  bool get hasMods =>
      modsWhitelist.trim().isNotEmpty ||
      modsBlacklist.trim().isNotEmpty ||
      modsRequired.trim().isNotEmpty;
}

class Ds2RuntimeSessionState {
  final DateTime? timeUtc;
  final String sessionId;
  final bool sessionOpen;
  final String sessionMode;
  final String serviceStage;
  final String onlineIntent;
  final String lastCommand;
  final int lastItemId;
  final String lastRuntimeName;
  final String lastMessageEn;
  final String lastMessageEs;
  final int actionCount;
  final String rulePreset;
  final int recoveryCount;
  final int tauntCount;
  final int infectionCount;
  final int curseCount;
  final bool serverRunning;
  final int? serverPid;
  final bool serverStartedByRuntime;
  final String serverName;
  final String serverGameType;
  final String effectStatus;
  final String effectError;
  final String effectNote;
  final String? error;

  Ds2RuntimeSessionState({
    this.timeUtc,
    required this.sessionId,
    required this.sessionOpen,
    required this.sessionMode,
    required this.serviceStage,
    required this.onlineIntent,
    required this.lastCommand,
    required this.lastItemId,
    required this.lastRuntimeName,
    required this.lastMessageEn,
    required this.lastMessageEs,
    required this.actionCount,
    required this.rulePreset,
    required this.recoveryCount,
    required this.tauntCount,
    required this.infectionCount,
    required this.curseCount,
    required this.serverRunning,
    this.serverPid,
    required this.serverStartedByRuntime,
    required this.serverName,
    required this.serverGameType,
    required this.effectStatus,
    required this.effectError,
    required this.effectNote,
    this.error,
  });

  factory Ds2RuntimeSessionState.fromJson(Map<String, dynamic> j) {
    final effect = j['effect'] is Map<String, dynamic>
        ? j['effect'] as Map<String, dynamic>
        : const <String, dynamic>{};
    return Ds2RuntimeSessionState(
      timeUtc: j['time_utc'] != null
          ? DateTime.tryParse(j['time_utc'] as String)
          : null,
      sessionId: j['session_id'] as String? ?? '',
      sessionOpen: j['session_open'] as bool? ?? false,
      sessionMode: j['session_mode'] as String? ?? 'solo',
      serviceStage: j['service_stage'] as String? ?? 'idle',
      onlineIntent: j['online_intent'] as String? ?? 'none',
      lastCommand: j['last_command'] as String? ?? 'none',
      lastItemId: (j['last_item_id'] as num?)?.toInt() ?? 0,
      lastRuntimeName: j['last_runtime_name'] as String? ?? '',
      lastMessageEn: j['last_message_en'] as String? ?? '',
      lastMessageEs: j['last_message_es'] as String? ?? '',
      actionCount: (j['action_count'] as num?)?.toInt() ?? 0,
      rulePreset: j['rule_preset'] as String? ?? '',
      recoveryCount: (j['recovery_count'] as num?)?.toInt() ?? 0,
      tauntCount: (j['taunt_count'] as num?)?.toInt() ?? 0,
      infectionCount: (j['infection_count'] as num?)?.toInt() ?? 0,
      curseCount: (j['curse_count'] as num?)?.toInt() ?? 0,
      serverRunning: j['server_running'] as bool? ?? false,
      serverPid: (j['server_pid'] as num?)?.toInt(),
      serverStartedByRuntime: j['server_started_by_runtime'] as bool? ?? false,
      serverName: j['server_name'] as String? ?? '',
      serverGameType: j['server_game_type'] as String? ?? '',
      effectStatus: effect['status'] as String? ?? '',
      effectError: effect['error'] as String? ?? '',
      effectNote: effect['note'] as String? ?? '',
    );
  }

  factory Ds2RuntimeSessionState.error(Map<String, dynamic>? j) =>
      Ds2RuntimeSessionState(
        timeUtc: j?['time_utc'] != null
            ? DateTime.tryParse(j!['time_utc'] as String)
            : null,
        sessionId: '',
        sessionOpen: false,
        sessionMode: 'error',
        serviceStage: 'service_error',
        onlineIntent: 'none',
        lastCommand: 'error',
        lastItemId: 0,
        lastRuntimeName: '',
        lastMessageEn: '',
        lastMessageEs: '',
        actionCount: 0,
        rulePreset: '',
        recoveryCount: 0,
        tauntCount: 0,
        infectionCount: 0,
        curseCount: 0,
        serverRunning: false,
        serverStartedByRuntime: false,
        serverName: '',
        serverGameType: 'DarkSouls2',
        effectStatus: 'error',
        effectError: j?['error'] as String? ?? 'Unknown DS2 runtime error.',
        effectNote: '',
        error: j?['error'] as String? ?? 'Unknown DS2 runtime error.',
      );

  String get displayMessage {
    if (error != null && error!.isNotEmpty) return error!;
    if (lastMessageEs.isNotEmpty) return lastMessageEs;
    if (lastMessageEn.isNotEmpty) return lastMessageEn;
    if (effectError.isNotEmpty) return effectError;
    if (effectNote.isNotEmpty) return effectNote;
    return serviceStage;
  }
}

class AppState extends ChangeNotifier {
  AppState(this._rpc) {
    _notificationSub = _rpc.notifications.listen(_onNotification);
    scheduleMicrotask(() => refreshUpdateStatus(silent: true));
    _updateTimer = Timer.periodic(
      const Duration(minutes: 30),
      (_) => refreshUpdateStatus(silent: true),
    );
  }

  final RpcClient _rpc;
  late final StreamSubscription<RpcNotification> _notificationSub;
  Timer? _updateTimer;

  // Convenience accessors for screens.
  RpcClient get rpc => _rpc;

  // Live status snapshots.
  ServerInstallStatus? installStatus;
  ServerLiveStatus? liveStatus;
  ServerConfig? config;
  FirewallStatus? firewall;
  String? wanIp;
  String? lanIp;

  // Public server list (from master server).
  List<PublicServer>? publicServers;
  bool publicServersLoading = false;
  bool publicServersAvailable = true; // false when master server unreachable
  String? selectedServerId;
  String publicListGameFilter = 'DarkSouls2'; // tab selector
  String searchQuery = '';
  bool hideSealed = false;
  int minPlayers = 0;

  // Game settings (exe paths, save mode).
  GameSettings? gameSettings;
  bool steamOk = false;

  // Profiles — list of saved local servers, plus the active one.
  List<Profile> profiles = const [];
  String? activeProfileId;
  Profile? get activeProfile {
    for (final p in profiles) {
      if (p.id == activeProfileId) return p;
    }
    return null;
  }

  void setSearchQuery(String q) {
    searchQuery = q;
    notifyListeners();
  }

  void setHideSealed(bool v) {
    hideSealed = v;
    notifyListeners();
  }

  void setMinPlayers(int v) {
    minPlayers = v;
    notifyListeners();
  }

  // Download progress (0-1 or null).
  double? downloadProgress;
  int? downloadBytesReceived;
  int? downloadBytesTotal;

  // App update status/progress.
  AppUpdateStatus? updateStatus;
  bool updateChecking = false;
  bool updateInstalling = false;
  double? updateProgress;
  int? updateBytesReceived;
  int? updateBytesTotal;
  String updatePhase = '';
  String? updateError;
  DateTime? updateLastCheckedAt;
  bool updateNoticeVisible = false;

  // DS2 in-game runtime item/session status.
  Ds2RuntimeSessionState? ds2RuntimeSession;
  bool ds2RuntimeNoticeVisible = false;

  // DS2 Native Session join-target arming. When non-null, the next
  // game.launch_local redirects DS2 to this peer's host/port/public-key
  // instead of the local profile's loopback Server.exe. Single-shot:
  // BonfireService clears it on consume, so the user must re-arm to
  // join again. Persisted server-side as
  // Runtime/DS2Native/join_target.json so a service restart between
  // arming and launching does not lose the selection.
  Ds2NativeJoinTargetSnapshot? ds2JoinTarget;

  Future<void> refreshAll() async {
    await Future.wait([
      refreshInstallStatus(),
      refreshLiveStatus(),
      refreshConfig(),
      refreshFirewall(),
      refreshIps(),
      refreshPublicServers(),
      refreshGameSettings(),
      refreshSteamStatus(),
      refreshProfiles(),
      refreshDs2RuntimeStatus(),
      refreshDs2JoinTarget(),
    ]);
    notifyListeners();
  }

  Future<void> refreshDs2JoinTarget() async {
    try {
      final j = await _rpc.call('ds2_runtime.get_join_target')
          as Map<String, dynamic>;
      final armed = j['armed'] as bool? ?? false;
      final target =
          armed ? j['target'] as Map<String, dynamic>? : null;
      ds2JoinTarget = target == null
          ? null
          : Ds2NativeJoinTargetSnapshot.fromJson(target);
    } catch (_) {
      ds2JoinTarget = null;
    }
    notifyListeners();
  }

  /// Arms `serverId` as the next-launch join target. Throws on failure
  /// (server not in master list, not a DS2 native session, wrong password,
  /// etc.) — caller renders the message to the user.
  Future<void> armDs2JoinTarget(String serverId, {String password = ''}) async {
    final j = await _rpc.call('ds2_runtime.set_join_target', {
      'server_id': serverId,
      'password': password,
    }) as Map<String, dynamic>;
    final armed = j['armed'] as bool? ?? false;
    final target = armed ? j['target'] as Map<String, dynamic>? : null;
    ds2JoinTarget = target == null
        ? null
        : Ds2NativeJoinTargetSnapshot.fromJson(target);
    notifyListeners();
  }

  Future<void> clearDs2JoinTarget() async {
    try {
      await _rpc.call('ds2_runtime.clear_join_target');
    } catch (_) {
      // Best effort — UI optimistically clears local state regardless.
    }
    ds2JoinTarget = null;
    notifyListeners();
  }

  Future<void> refreshProfiles() async {
    try {
      final j = await _rpc.call('profiles.list') as Map<String, dynamic>;
      activeProfileId = j['active_id'] as String?;
      final list = (j['profiles'] as List?) ?? const [];
      profiles =
          list.map((e) => Profile.fromJson(e as Map<String, dynamic>)).toList();
    } catch (_) {
      profiles = const [];
      activeProfileId = null;
    }
    notifyListeners();
  }

  Future<Profile?> createProfile(String name, String gameType) async {
    try {
      final j = await _rpc.call('profiles.create', {
        'name': name,
        'game_type': gameType,
      }) as Map<String, dynamic>;
      await refreshProfiles();
      final id = j['id'] as String?;
      if (id == null) return null;
      return profiles.firstWhere((p) => p.id == id,
          orElse: () => Profile(
              id: id,
              name: name,
              gameType: gameType,
              createdAt: '',
              isActive: false));
    } catch (_) {
      return null;
    }
  }

  Future<void> renameProfile(String id, String newName) async {
    await _rpc.call('profiles.rename', {'id': id, 'name': newName});
    await refreshProfiles();
  }

  Future<void> deleteProfile(String id) async {
    await _rpc.call('profiles.delete', {'id': id});
    await Future.wait([refreshProfiles(), refreshLiveStatus()]);
  }

  Future<void> activateProfile(String id) async {
    await _rpc.call('profiles.activate', {'id': id});
    await Future.wait([
      refreshProfiles(),
      refreshConfig(),
      refreshLiveStatus(),
    ]);
  }

  Future<void> refreshGameSettings() async {
    try {
      final j = await _rpc.call('game.get_settings') as Map<String, dynamic>;
      gameSettings = GameSettings.fromJson(j);
    } catch (_) {
      gameSettings = null;
    }
    notifyListeners();
  }

  Future<void> refreshSteamStatus() async {
    try {
      final j = await _rpc.call('game.steam_status') as Map<String, dynamic>;
      steamOk = j['ok'] as bool? ?? false;
    } catch (_) {
      steamOk = false;
    }
    notifyListeners();
  }

  Future<bool> setGameExe(String gameType, String path) async {
    try {
      final j = await _rpc.call('game.set_exe_path', {
        'game_type': gameType,
        'path': path,
      }) as Map<String, dynamic>;
      await refreshGameSettings();
      return (j['recognised'] as bool?) ?? false;
    } catch (_) {
      return false;
    }
  }

  Future<({bool ok, String? error, int? pid})> launchLocalGame({
    required String profileId,
  }) async {
    try {
      final j = await _rpc.call('game.launch_local', {
        'profile_id': profileId,
      }) as Map<String, dynamic>;
      // After launch we usually have a freshly running server — refresh.
      // Also re-read the live config: launch_local may have re-activated
      // the profile (drift detection) and pulled in user changes from the
      // vault config.json that the previous in-memory cfg didn't know
      // about.
      await Future.wait([
        refreshLiveStatus(),
        refreshProfiles(),
        refreshConfig(),
      ]);
      return (
        ok: j['ok'] as bool? ?? false,
        error: null,
        pid: (j['pid'] as num?)?.toInt(),
      );
    } catch (e) {
      return (ok: false, error: e.toString(), pid: null);
    }
  }

  Future<({bool ok, String? error, int? pid})> launchGame({
    required String serverId,
    String password = '',
  }) async {
    try {
      final j = await _rpc.call('game.launch', {
        'server_id': serverId,
        'password': password,
      }) as Map<String, dynamic>;
      return (
        ok: j['ok'] as bool? ?? false,
        error: null,
        pid: (j['pid'] as num?)?.toInt(),
      );
    } catch (e) {
      return (ok: false, error: e.toString(), pid: null);
    }
  }

  Future<void> refreshPublicServers() async {
    publicServersLoading = true;
    notifyListeners();
    try {
      final j = await _rpc.call('servers.list', {
        'game_type': publicListGameFilter,
      }) as Map<String, dynamic>;
      publicServersAvailable = j['available'] as bool? ?? false;
      final list = (j['servers'] as List?) ?? const [];
      publicServers = list
          .map((e) => PublicServer.fromJson(e as Map<String, dynamic>))
          .toList()
        ..sort((a, b) => b.playerCount.compareTo(a.playerCount));
    } catch (_) {
      publicServersAvailable = false;
      publicServers = const [];
    } finally {
      publicServersLoading = false;
      notifyListeners();
    }
  }

  void setGameFilter(String gameType) {
    if (publicListGameFilter == gameType) return;
    publicListGameFilter = gameType;
    selectedServerId = null;
    notifyListeners();
    refreshPublicServers();
  }

  void selectServer(String? id) {
    selectedServerId = id;
    notifyListeners();
  }

  Future<void> refreshInstallStatus() async {
    final j = await _rpc.call('server.is_installed') as Map<String, dynamic>;
    installStatus = ServerInstallStatus.fromJson(j);
    notifyListeners();
  }

  Future<void> refreshLiveStatus() async {
    final j = await _rpc.call('server.status') as Map<String, dynamic>;
    liveStatus = ServerLiveStatus.fromJson(j);
    notifyListeners();
  }

  Future<void> refreshConfig() async {
    final j = await _rpc.call('server.config_get') as Map<String, dynamic>;
    config = ServerConfig.fromJson(j);
    notifyListeners();
  }

  Future<void> refreshFirewall() async {
    final j = await _rpc.call('firewall.status') as Map<String, dynamic>;
    firewall = FirewallStatus.fromJson(j);
    notifyListeners();
  }

  Future<void> refreshIps() async {
    final j = await _rpc.call('network.detect_ips') as Map<String, dynamic>;
    wanIp = j['wan'] as String?;
    lanIp = j['lan'] as String?;
    notifyListeners();
  }

  Future<void> applyFirewall() async {
    await _rpc.call('firewall.apply');
    await refreshFirewall();
  }

  Future<void> refreshUpdateStatus({bool silent = false}) async {
    if (updateChecking || updateInstalling) return;
    updateChecking = true;
    if (!silent) {
      updateError = null;
      updateNoticeVisible = true;
    }
    notifyListeners();
    try {
      final raw = await _rpc.call('app.update_status');
      if (raw is Map<String, dynamic>) {
        updateStatus = AppUpdateStatus.fromJson(raw);
        updateLastCheckedAt = DateTime.now();
        updateError = null;
        if (updateStatus?.updateAvailable ?? false) {
          updateNoticeVisible = true;
        }
      } else {
        updateStatus = null;
        if (!silent) {
          updateError =
              'Could not resolve the latest DSSeamlessCoop release from GitHub.';
        }
      }
    } catch (e) {
      if (!silent) updateError = e.toString();
    } finally {
      updateChecking = false;
      notifyListeners();
    }
  }

  Future<void> refreshDs2RuntimeStatus({String? sessionId}) async {
    try {
      final raw = await _rpc.call('ds2_runtime.status', {
        if (sessionId != null && sessionId.isNotEmpty) 'session_id': sessionId,
      });
      if (raw is! Map<String, dynamic>) return;
      final serviceState = raw['service_state'];
      if (serviceState is Map<String, dynamic>) {
        ds2RuntimeSession = Ds2RuntimeSessionState.fromJson(serviceState);
        ds2RuntimeNoticeVisible = true;
      }
    } catch (_) {
      // DS2 runtime status is optional; Bonfire can boot before DS2 has run.
    }
    notifyListeners();
  }

  Future<bool> applyAppUpdate() async {
    updateInstalling = true;
    updatePhase = 'downloading';
    updateProgress = 0;
    updateBytesReceived = 0;
    updateBytesTotal = null;
    updateError = null;
    updateNoticeVisible = true;
    notifyListeners();
    try {
      final raw = await _rpc.call('app.apply_update', {'ui_pid': pid});
      if (raw is! Map<String, dynamic>) return false;
      updateStatus = AppUpdateStatus.fromJson(raw);
      updateLastCheckedAt = DateTime.now();
      return raw['restart_required'] as bool? ?? false;
    } catch (e) {
      updateError = e.toString();
      rethrow;
    } finally {
      updateInstalling = false;
      updatePhase = '';
      updateProgress = null;
      updateBytesReceived = null;
      updateBytesTotal = null;
      notifyListeners();
    }
  }

  Future<void> installLatest() async {
    downloadProgress = 0;
    downloadBytesReceived = 0;
    downloadBytesTotal = null;
    notifyListeners();
    try {
      await _rpc.call('server.install');
    } finally {
      downloadProgress = null;
      downloadBytesReceived = null;
      downloadBytesTotal = null;
      await refreshInstallStatus();
      notifyListeners();
    }
  }

  Future<void> startServer() async {
    await _rpc.call('server.start');
    await refreshLiveStatus();
  }

  Future<void> stopServer() async {
    await _rpc.call('server.stop');
    await refreshLiveStatus();
  }

  Future<void> saveConfig(ServerConfig newCfg) async {
    await _rpc.call('server.config_set', newCfg.toJson());
    config = newCfg;
    notifyListeners();
  }

  Future<void> uninstallServer({bool keepConfig = false}) async {
    await _rpc.call('server.uninstall', {'keep_config': keepConfig});
    await refreshAll();
  }

  Future<void> resetServerConfig() async {
    await _rpc.call('server.reset_config');
    await refreshAll();
  }

  void dismissUpdateNotice() {
    updateNoticeVisible = false;
    updateError = null;
    notifyListeners();
  }

  void dismissDs2RuntimeNotice() {
    ds2RuntimeNoticeVisible = false;
    notifyListeners();
  }

  void _onNotification(RpcNotification n) {
    switch (n.method) {
      case 'download.progress':
        final recv = (n.params?['bytes_received'] as num?)?.toInt() ?? 0;
        final total = (n.params?['bytes_total'] as num?)?.toInt();
        downloadBytesReceived = recv;
        downloadBytesTotal = total;
        downloadProgress = (total != null && total > 0) ? recv / total : null;
        notifyListeners();
        break;
      case 'app.update_progress':
        final recv = (n.params?['bytes_received'] as num?)?.toInt() ?? 0;
        final total = (n.params?['bytes_total'] as num?)?.toInt();
        updatePhase = n.params?['phase'] as String? ?? 'downloading';
        updateBytesReceived = recv;
        updateBytesTotal = total;
        updateProgress = (total != null && total > 0) ? recv / total : null;
        updateNoticeVisible = true;
        notifyListeners();
        break;
      case 'ds2_runtime.session':
        if (n.params != null) {
          ds2RuntimeSession = Ds2RuntimeSessionState.fromJson(n.params!);
          ds2RuntimeNoticeVisible = true;
          unawaited(refreshLiveStatus());
          notifyListeners();
        }
        break;
      case 'ds2_runtime.session_error':
        ds2RuntimeSession = Ds2RuntimeSessionState.error(n.params);
        ds2RuntimeNoticeVisible = true;
        notifyListeners();
        break;
    }
  }

  @override
  void dispose() {
    _updateTimer?.cancel();
    _notificationSub.cancel();
    super.dispose();
  }
}

/// Resolves the BonfireService.exe path next to the running app, with
/// dev-build fallbacks so `flutter run` works without an install.
String? resolveServiceExePath() {
  // Production: sibling of Bonfire.exe.
  final exeDir = File(Platform.resolvedExecutable).parent.path;
  final sibling = File('$exeDir${Platform.pathSeparator}BonfireService.exe');
  if (sibling.existsSync()) return sibling.path;

  // Dev (flutter run): walk up to Source/, then into BonfireService bin.
  final candidates = [
    '$exeDir/../../../../../BonfireService/bin/Debug/net8.0-windows/win-x64/BonfireService.exe',
    '$exeDir/../../../../../BonfireService/bin/Release/net8.0-windows/win-x64/BonfireService.exe',
    '$exeDir/../../../../../BonfireService/bin/Debug/net8.0-windows/BonfireService.exe',
  ];
  for (final c in candidates) {
    final f = File(c);
    if (f.existsSync()) return f.absolute.path;
  }
  return null;
}
