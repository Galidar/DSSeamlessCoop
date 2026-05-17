/*
 * Plan v3 Track C Phase 2A — Paramdex-derived item-ID name resolver.
 *
 * Backs the bridge.status RPC's char_data response with human-readable
 * weapon/armor/ring/item names so the Flutter UI can render a
 * Connected Peers panel like:
 *
 *   NetworkPlayer_000100  HP 1304/1304
 *     R1: Hollow Soldier Shield
 *     R2: Club
 *     Head: Standard Helm
 *     Body: Old Knight Armor
 *     ...
 *
 * Source: Paramdex DS2S/Names/{ItemParam,WeaponParam,ArmorParam}.txt
 * pre-flattened by Tools/build_ds2_item_names.py into the embedded
 * resource Resources/ds2_item_names.json. Total ~1853 entries, ~60 KB.
 *
 * The JSON is loaded once on first lookup, cached in memory for the
 * service lifetime. Lookups are O(1). Unknown IDs return null so the
 * caller can decide whether to show the raw integer or hide the slot.
 */

using System.Reflection;
using System.Text.Json;

namespace Bonfire.Service.Modules;

public static class Ds2ItemNames
{
    private const string ResourceName = "Bonfire.Service.Resources.ds2_item_names.json";

    private static readonly object Lock = new();
    private static IReadOnlyDictionary<uint, string>? _table;

    /// <summary>
    /// Returns the human-readable name for a DS2 param ID
    /// (weapon/armor/ring/item) — or null when unknown. The "Fists"
    /// sentinel (3,400,000) returns "Fists"; pass-through callers
    /// typically render that as "empty hand".
    /// </summary>
    public static string? Resolve(uint id)
    {
        EnsureLoaded();
        return _table is not null && _table.TryGetValue(id, out var name) ? name : null;
    }

    /// <summary>Resolve with a sane fallback for UI binding.</summary>
    public static string ResolveOrFallback(uint id, string fallback = "")
    {
        return Resolve(id) ?? fallback;
    }

    private static void EnsureLoaded()
    {
        if (_table is not null) return;
        lock (Lock)
        {
            if (_table is not null) return;
            try
            {
                var asm = typeof(Ds2ItemNames).Assembly;
                using var stream = asm.GetManifestResourceStream(ResourceName);
                if (stream is null)
                {
                    // Resource missing — fall back to an empty table so
                    // the service still works (just won't resolve names).
                    _table = new Dictionary<uint, string>();
                    return;
                }
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                if (raw is null)
                {
                    _table = new Dictionary<uint, string>();
                    return;
                }
                var dict = new Dictionary<uint, string>(raw.Count);
                foreach (var (k, v) in raw)
                {
                    if (uint.TryParse(k, out var key))
                        dict[key] = v;
                }
                _table = dict;
            }
            catch
            {
                _table = new Dictionary<uint, string>();
            }
        }
    }
}
