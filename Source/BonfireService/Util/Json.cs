/*
 * Tiny ergonomic helpers around System.Text.Json.Nodes so RPC method
 * implementations stay focused on intent rather than boilerplate.
 */

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Bonfire.Service.Util;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? GetParam<T>(this JsonNode? @params, string key) where T : class
    {
        if (@params is null) return null;
        var node = @params[key];
        if (node is null) return null;
        return JsonSerializer.Deserialize<T>(node.ToJsonString(), Options);
    }

    public static string? GetString(this JsonNode? @params, string key)
    {
        return @params?[key]?.GetValue<string?>();
    }

    public static bool? GetBool(this JsonNode? @params, string key)
    {
        var node = @params?[key];
        if (node is null) return null;
        try { return node.GetValue<bool>(); } catch { return null; }
    }

    public static int? GetInt(this JsonNode? @params, string key)
    {
        var node = @params?[key];
        if (node is null) return null;
        try { return node.GetValue<int>(); } catch { return null; }
    }

    public static JsonNode ToNode<T>(this T value)
    {
        return JsonNode.Parse(JsonSerializer.Serialize(value, Options))!;
    }
}
