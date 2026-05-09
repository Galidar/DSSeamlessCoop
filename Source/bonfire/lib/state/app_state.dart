/// App-wide state — wraps the RpcClient, exposes typed wrappers for each
/// RPC method, and tracks UI-relevant state (server status, install state,
/// download progress, etc.) via ChangeNotifier so widgets can rebuild.

import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';

import '../rpc/rpc_client.dart';

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
  ServerLiveStatus({required this.running, this.pid, this.startedAt});
  factory ServerLiveStatus.fromJson(Map<String, dynamic> j) => ServerLiveStatus(
        running: j['running'] as bool? ?? false,
        pid: (j['pid'] as num?)?.toInt(),
        startedAt: j['started_at'] != null
            ? DateTime.tryParse(j['started_at'] as String)
            : null,
      );
}

class ServerConfig {
  String name;
  String description;
  String password;
  String gameType;
  String publicIp;
  String privateIp;
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
    required this.advertise,
    required this.webuiUsername,
    required this.webuiPassword,
    required this.exists,
  });
  factory ServerConfig.fromJson(Map<String, dynamic> j) => ServerConfig(
        name: j['server_name'] as String? ?? '',
        description: j['server_description'] as String? ?? '',
        password: j['password'] as String? ?? '',
        gameType: j['game_type'] as String? ?? 'DarkSouls2',
        publicIp: j['server_hostname'] as String? ?? '',
        privateIp: j['server_private_hostname'] as String? ?? '',
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
  final bool allowSharding;
  final String modsWhitelist;
  final String modsBlacklist;
  final String modsRequired;

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
    required this.allowSharding,
    required this.modsWhitelist,
    required this.modsBlacklist,
    required this.modsRequired,
  });

  factory PublicServer.fromJson(Map<String, dynamic> j) => PublicServer(
        id: j['id'] as String? ?? '',
        name: j['name'] as String? ?? '(unnamed)',
        description: j['description'] as String? ?? '',
        gameType: j['game_type'] as String? ?? '',
        playerCount: (j['player_count'] as num?)?.toInt() ?? 0,
        passwordRequired: j['password_required'] as bool? ?? false,
        hostname: j['hostname'] as String? ?? '',
        ipAddress: j['ip_address'] as String? ?? '',
        isShard: j['is_shard'] as bool? ?? false,
        allowSharding: j['allow_sharding'] as bool? ?? false,
        modsWhitelist: j['mods_whitelist'] as String? ?? '',
        modsBlacklist: j['mods_blacklist'] as String? ?? '',
        modsRequired: j['mods_required'] as String? ?? '',
      );

  bool get hasMods =>
      modsWhitelist.trim().isNotEmpty ||
      modsBlacklist.trim().isNotEmpty ||
      modsRequired.trim().isNotEmpty;
}

class AppState extends ChangeNotifier {
  AppState(this._rpc) {
    _rpc.notifications.listen(_onNotification);
  }

  final RpcClient _rpc;

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
    ]);
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
    }
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
