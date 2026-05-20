/*
 * Minimal JSON-RPC 2.0 server over stdio.
 *
 * Wire format: one JSON object per line. Reading is line-buffered. Writing
 * is also line-buffered and serialised through a SemaphoreSlim so concurrent
 * notifications from background tasks don't interleave with replies.
 *
 * We intentionally don't depend on a third-party RPC framework — the surface
 * is small, the dependency footprint stays tiny, and the protocol is easy
 * to debug from a terminal.
 */

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bonfire.Service.Rpc;

public delegate Task<JsonNode?> RpcMethod(JsonNode? @params, CancellationToken ct);

public sealed class RpcServer
{
    private readonly Dictionary<string, RpcMethod> _methods = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // v2.9.33 fix — .NET 8 marks JsonSerializerOptions read-only after first
        // Serialize call. If TypeInfoResolver is null, subsequent calls fail with
        // "JsonSerializerOptions instance must specify a TypeInfoResolver setting
        // before being marked as read-only". Observed correlation: error started
        // EXACT 1 second after a Saponita Desbloqueada use triggered a NotifyAsync
        // with a complex payload (direct_summon_host_outbox DeepClone), then
        // every subsequent RPC poll failed and BonfireService spammed errors
        // every 2s. Symptom: peer's DS2 (via UI RPC dependency) disconnects ~5s
        // later. Fix: provide the default reflection-based resolver explicitly.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public void Register(string method, RpcMethod handler)
    {
        _methods[method] = handler;
    }

    public async Task RunAsync()
    {
        // Note: we deliberately don't make stdin async-friendly with
        // Console.OpenStandardInput().ReadAsync because reading stdin is
        // blocking on Windows even with Async; running on a worker thread
        // keeps the main loop responsive.
        var input = Console.In;
        while (!_cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Task.Run(input.ReadLine, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (line is null) break; // EOF — UI closed

            // Empty lines are tolerated and ignored.
            line = line.Trim();
            if (line.Length == 0) continue;

            _ = HandleAsync(line);
        }
    }

    private async Task HandleAsync(string raw)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(raw);
        }
        catch (Exception ex)
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["error"] = new JsonObject
                {
                    ["code"] = -32700,
                    ["message"] = "parse error: " + ex.Message,
                },
            });
            return;
        }

        var idNode = root?["id"];
        var method = root?["method"]?.GetValue<string>();
        var paramsNode = root?["params"];

        if (string.IsNullOrEmpty(method))
        {
            await WriteAsync(MakeError(idNode, -32600, "missing method"));
            return;
        }

        if (!_methods.TryGetValue(method, out var handler))
        {
            await WriteAsync(MakeError(idNode, -32601, "method not found: " + method));
            return;
        }

        try
        {
            var result = await handler(paramsNode, _cts.Token);
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode is null ? null : JsonNode.Parse(idNode.ToJsonString()),
                ["result"] = result,
            });
        }
        catch (Exception ex)
        {
            await WriteAsync(MakeError(idNode, -32603, ex.Message));
        }
    }

    /// <summary>
    /// Send an unsolicited notification to the UI (no response expected).
    /// Used for download progress, server log lines, etc.
    /// </summary>
    public Task NotifyAsync(string method, JsonNode? @params)
    {
        return WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        });
    }

    private async Task WriteAsync(JsonNode payload)
    {
        var text = payload.ToJsonString(JsonOptions);
        await _writeLock.WaitAsync();
        try
        {
            // Single line — UI parses one JSON object per line.
            await Console.Out.WriteLineAsync(text);
            await Console.Out.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static JsonObject MakeError(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is null ? null : JsonNode.Parse(id.ToJsonString()),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
}
