namespace AirLink.Core;

/// <summary>Codec id map and ordered-list helpers (see FiiO-AirLink-Protocol.md §4).</summary>
public static class Codec
{
    public const byte MaxBrightness = 0x07;

    // User-toggleable codecs, in canonical priority order.
    public const byte Ldac = 0x08;
    public const byte AptxAdaptive = 0x07;
    public const byte AptxHd = 0x06;
    public const byte Aptx = 0x03;
    public const byte AptxLl = 0x05;

    // Sub-mode tables for codecs that have a quality/latency selector.
    // LDAC bitrate quality mode (CMD 0x42 get / 0x43 set).
    public static readonly (byte value, string label)[] LdacModes =
    {
        (0x00, "990 kbps · Audio Quality"),
        (0x01, "660 kbps · Balanced"),
        (0x02, "330 kbps · Connection Quality"),
    };

    // aptX Adaptive mode (CMD 0x40 get / 0x41 set). 0x13 = High Quality (0x03) | lossless bit (0x10).
    public static readonly (byte value, string label)[] AptxAdaptiveModes =
    {
        (0x02, "Low Latency"),
        (0x03, "High Quality"),
        (0x13, "aptX Lossless"),
    };

    // Baseline codecs that are always present and not user-toggleable.
    // SBC is one of these; the dongle has no AAC. Exact id->name TBD.
    public static readonly byte[] Baseline = { 0x01, 0x00 };

    /// <summary>Full enabled list, all toggleable codecs on, in canonical order.</summary>
    public static readonly byte[] CanonicalOrder = { Ldac, AptxAdaptive, AptxHd, Aptx, AptxLl, 0x01, 0x00 };

    private static readonly Dictionary<string, byte> NameToId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ldac"] = Ldac,
        ["aptx-adaptive"] = AptxAdaptive, ["adaptive"] = AptxAdaptive,
        ["aptx-hd"] = AptxHd, ["hd"] = AptxHd,
        ["aptx"] = Aptx,
        ["aptx-ll"] = AptxLl, ["ll"] = AptxLl,
    };

    private static readonly Dictionary<byte, string> IdToName = new()
    {
        [Ldac] = "LDAC",
        [AptxAdaptive] = "aptX Adaptive",
        [AptxHd] = "aptX HD",
        [Aptx] = "aptX",
        [AptxLl] = "aptX LL",
        [0x01] = "baseline(01)",
        [0x00] = "SBC(00?)",
    };

    public static IEnumerable<string> ToggleableNames => new[] { "ldac", "aptx-adaptive", "aptx-hd", "aptx", "aptx-ll" };

    public static bool TryParse(string name, out byte id) => NameToId.TryGetValue(name, out id);

    public static string Describe(byte id) => IdToName.TryGetValue(id, out var n) ? n : $"unknown(0x{id:x2})";

    public static bool IsToggleable(byte id) => IdToName.ContainsKey(id) && Array.IndexOf(Baseline, id) < 0;

    /// <summary>
    /// Rebuild the enabled list with <paramref name="codecId"/> added/removed, preserving
    /// canonical order. Baseline codecs are always kept.
    /// </summary>
    public static byte[] ApplyToggle(byte[] current, byte codecId, bool enabled)
    {
        var set = new HashSet<byte>(current);
        foreach (var b in Baseline) set.Add(b);
        if (enabled) set.Add(codecId); else set.Remove(codecId);
        return CanonicalOrder.Where(set.Contains).ToArray();
    }
}
