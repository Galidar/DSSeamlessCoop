using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Bonfire.Service.Modules;

public sealed record RelayTunnelStatus(
    bool Running,
    string PublicHostname,
    int LoginPort,
    int AuthPort,
    int GamePort);

public static class RelayTunnel
{
    private static readonly object Gate = new();
    private static RelaySession? Current;

    public static RelayTunnelStatus QueryStatus()
    {
        lock (Gate)
        {
            return Current?.Status ?? new(false, "", 0, 0, 0);
        }
    }

    public static async Task<RelayTunnelStatus> StartAsync(
        ServerConfig cfg,
        string configPath,
        CancellationToken ct)
    {
        if (!cfg.RelayEnabled)
        {
            Stop();
            return QueryStatus();
        }

        if (string.IsNullOrWhiteSpace(cfg.RelayControlHost))
            throw new Exception("Relay mode is enabled, but RelayControlHost is empty.");

        Stop();

        var localPorts = new LocalPorts(
            Login: cfg.LoginServerPort > 0 ? cfg.LoginServerPort : 50050,
            Auth: ReadInt(configPath, "AuthServerPort", 50000),
            Game: ReadInt(configPath, "GameServerPort", 50010));

        var session = new RelaySession(
            cfg.ServerId,
            cfg.RelayControlHost,
            cfg.RelayControlPort > 0 ? cfg.RelayControlPort : 50030,
            cfg.RelayControlToken,
            localPorts);

        lock (Gate)
        {
            Current = session;
        }

        try
        {
            var status = await session.StartAsync(ct);
            cfg.RelayPublicHostname = status.PublicHostname;
            cfg.RelayLoginServerPort = status.LoginPort;
            cfg.RelayAuthServerPort = status.AuthPort;
            cfg.RelayGameServerPort = status.GamePort;
            if (!cfg.SaveOver(configPath))
                throw new Exception($"Could not write relay allocation to {configPath}");
            return status;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            Current?.Dispose();
            Current = null;
        }
    }

    private static int ReadInt(string path, string key, int fallback)
    {
        if (!File.Exists(path)) return fallback;

        var text = File.ReadAllText(path);
        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?\\d+)");
        return m.Success && int.TryParse(m.Groups["v"].Value, out var v) && v > 0
            ? v
            : fallback;
    }

    private sealed record LocalPorts(int Login, int Auth, int Game);

    private sealed class RelaySession : IDisposable
    {
        private readonly string _serverId;
        private readonly string _controlHost;
        private readonly int _controlPort;
        private readonly string _token;
        private readonly LocalPorts _localPorts;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _tcpGate = new();
        private readonly object _udpGate = new();
        private readonly Dictionary<int, TcpChannel> _tcpChannels = new();
        private readonly Dictionary<string, UdpChannel> _udpChannels = new();
        private TcpClient? _control;
        private NetworkStream? _stream;
        private bool _disposed;

        public RelayTunnelStatus Status { get; private set; } = new(false, "", 0, 0, 0);

        public RelaySession(
            string serverId,
            string controlHost,
            int controlPort,
            string token,
            LocalPorts localPorts)
        {
            _serverId = string.IsNullOrWhiteSpace(serverId)
                ? Guid.NewGuid().ToString("N")
                : serverId;
            _controlHost = controlHost;
            _controlPort = controlPort;
            _token = token;
            _localPorts = localPorts;
        }

        public async Task<RelayTunnelStatus> StartAsync(CancellationToken ct)
        {
            _control = new TcpClient();
            await _control.ConnectAsync(_controlHost, _controlPort, ct);
            _control.NoDelay = true;
            _stream = _control.GetStream();

            var registered = new TaskCompletionSource<RelayTunnelStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(() => ReadLoopAsync(registered), _cts.Token);

            await SendFrameAsync(new
            {
                type = "register",
                serverId = _serverId,
                token = _token,
                localLoginPort = _localPorts.Login,
                localAuthPort = _localPorts.Auth,
                localGamePort = _localPorts.Game,
            }, ct);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var registration = linked.Token.Register(() => registered.TrySetCanceled(linked.Token));
            Status = await registered.Task;
            return Status;
        }

        private async Task ReadLoopAsync(TaskCompletionSource<RelayTunnelStatus> registered)
        {
            try
            {
                using var reader = new StreamReader(_stream!, Encoding.UTF8, leaveOpen: true);
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    using var doc = JsonDocument.Parse(line);
                    await HandleFrameAsync(doc.RootElement, registered);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                registered.TrySetException(ex);
            }
            finally
            {
                Status = new(false, "", 0, 0, 0);
                Dispose();
            }
        }

        private async Task HandleFrameAsync(
            JsonElement frame,
            TaskCompletionSource<RelayTunnelStatus> registered)
        {
            var type = GetString(frame, "type");
            switch (type)
            {
                case "registered":
                {
                    var status = new RelayTunnelStatus(
                        Running: true,
                        PublicHostname: GetString(frame, "publicHost"),
                        LoginPort: GetInt(frame, "loginPort"),
                        AuthPort: GetInt(frame, "authPort"),
                        GamePort: GetInt(frame, "gamePort"));

                    if (string.IsNullOrWhiteSpace(status.PublicHostname) ||
                        status.LoginPort <= 0 ||
                        status.AuthPort <= 0 ||
                        status.GamePort <= 0)
                    {
                        registered.TrySetException(new Exception("Relay returned an incomplete allocation."));
                    }
                    else
                    {
                        Status = status;
                        registered.TrySetResult(status);
                    }
                    break;
                }

                case "error":
                    registered.TrySetException(new Exception(GetString(frame, "message")));
                    break;

                case "tcp_open":
                    await OpenTcpChannelAsync(
                        GetInt(frame, "channel"),
                        GetString(frame, "service"));
                    break;

                case "tcp_data":
                    await WriteTcpChannelAsync(
                        GetInt(frame, "channel"),
                        GetBytes(frame, "data"));
                    break;

                case "tcp_close":
                    CloseTcpChannel(GetInt(frame, "channel"), notifyRelay: false);
                    break;

                case "udp_data":
                    await WriteUdpEndpointAsync(
                        GetString(frame, "endpoint"),
                        GetBytes(frame, "data"));
                    break;
            }
        }

        private async Task OpenTcpChannelAsync(int channelId, string service)
        {
            if (channelId <= 0) return;

            var port = service switch
            {
                "login" => _localPorts.Login,
                "auth" => _localPorts.Auth,
                _ => 0,
            };
            if (port <= 0)
            {
                await SendFrameAsync(new { type = "tcp_close", channel = channelId }, _cts.Token);
                return;
            }

            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, _cts.Token);
                client.NoDelay = true;

                var tcp = new TcpChannel(channelId, client);
                lock (_tcpGate)
                {
                    _tcpChannels[channelId] = tcp;
                }
                _ = Task.Run(() => PumpTcpToRelayAsync(tcp), _cts.Token);
            }
            catch
            {
                await SendFrameAsync(new { type = "tcp_close", channel = channelId }, _cts.Token);
            }
        }

        private async Task PumpTcpToRelayAsync(TcpChannel channel)
        {
            var buffer = new byte[32 * 1024];
            try
            {
                var stream = channel.Client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token);
                    if (read <= 0) break;
                    await SendFrameAsync(new
                    {
                        type = "tcp_data",
                        channel = channel.Id,
                        data = Convert.ToBase64String(buffer, 0, read),
                    }, _cts.Token);
                }
            }
            catch
            {
            }
            finally
            {
                CloseTcpChannel(channel.Id, notifyRelay: true);
            }
        }

        private async Task WriteTcpChannelAsync(int channelId, byte[] data)
        {
            TcpChannel? channel;
            lock (_tcpGate)
            {
                _tcpChannels.TryGetValue(channelId, out channel);
            }
            if (channel is null)
                return;

            try
            {
                await channel.Client.GetStream().WriteAsync(data, _cts.Token);
            }
            catch
            {
                CloseTcpChannel(channelId, notifyRelay: true);
            }
        }

        private void CloseTcpChannel(int channelId, bool notifyRelay)
        {
            TcpChannel? channel;
            lock (_tcpGate)
            {
                _tcpChannels.Remove(channelId, out channel);
            }
            if (channel is null)
                return;

            try { channel.Client.Dispose(); } catch { }
            if (notifyRelay && !_cts.IsCancellationRequested)
            {
                _ = SendFrameAsync(new { type = "tcp_close", channel = channelId }, _cts.Token);
            }
        }

        private async Task WriteUdpEndpointAsync(string endpoint, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return;

            UdpChannel? channel;
            lock (_udpGate)
            {
                _udpChannels.TryGetValue(endpoint, out channel);
                if (channel is null)
                {
                    var client = new UdpClient(AddressFamily.InterNetwork);
                    client.Connect(IPAddress.Loopback, _localPorts.Game);
                    channel = new UdpChannel(endpoint, client);
                    _udpChannels[endpoint] = channel;
                    _ = Task.Run(() => PumpUdpToRelayAsync(channel), _cts.Token);
                }
            }

            try
            {
                await channel.Client.SendAsync(data, _cts.Token);
            }
            catch
            {
                CloseUdpEndpoint(endpoint);
            }
        }

        private async Task PumpUdpToRelayAsync(UdpChannel channel)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var result = await channel.Client.ReceiveAsync(_cts.Token);
                    await SendFrameAsync(new
                    {
                        type = "udp_data",
                        endpoint = channel.Endpoint,
                        data = Convert.ToBase64String(result.Buffer),
                    }, _cts.Token);
                }
            }
            catch
            {
                CloseUdpEndpoint(channel.Endpoint);
            }
        }

        private void CloseUdpEndpoint(string endpoint)
        {
            UdpChannel? channel;
            lock (_udpGate)
            {
                _udpChannels.Remove(endpoint, out channel);
            }
            if (channel is null)
                return;

            try { channel.Client.Dispose(); } catch { }
        }

        private async Task SendFrameAsync(object frame, CancellationToken ct)
        {
            if (_stream is null || _control is null || !_control.Connected)
                return;

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame) + "\n");
            await _writeLock.WaitAsync(ct);
            try
            {
                await _stream.WriteAsync(bytes, ct);
                await _stream.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static string GetString(JsonElement frame, string name)
        {
            return frame.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }

        private static int GetInt(JsonElement frame, string name)
        {
            return frame.TryGetProperty(name, out var value) &&
                   value.TryGetInt32(out var parsed)
                ? parsed
                : 0;
        }

        private static byte[] GetBytes(JsonElement frame, string name)
        {
            var text = GetString(frame, name);
            return string.IsNullOrEmpty(text) ? Array.Empty<byte>() : Convert.FromBase64String(text);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (!_cts.IsCancellationRequested)
                _cts.Cancel();

            int[] tcpChannels;
            lock (_tcpGate)
            {
                tcpChannels = _tcpChannels.Keys.ToArray();
            }
            foreach (var channel in tcpChannels)
                CloseTcpChannel(channel, notifyRelay: false);

            string[] udpEndpoints;
            lock (_udpGate)
            {
                udpEndpoints = _udpChannels.Keys.ToArray();
            }
            foreach (var endpoint in udpEndpoints)
                CloseUdpEndpoint(endpoint);

            try { _stream?.Dispose(); } catch { }
            try { _control?.Dispose(); } catch { }
            _writeLock.Dispose();
            _cts.Dispose();
        }

        private sealed record TcpChannel(int Id, TcpClient Client);
        private sealed record UdpChannel(string Endpoint, UdpClient Client);
    }
}
