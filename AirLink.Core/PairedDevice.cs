namespace AirLink.Core;

/// <summary>A device in the Air Link's paired list (see protocol doc §6).</summary>
public sealed class PairedDevice
{
    public required byte[] Mac { get; init; }     // 6 bytes, as displayed
    public required bool Connected { get; init; } // record flag byte == 0x80
    public string Name { get; set; } = "";

    public string MacString => string.Join(":", Mac.Select(b => b.ToString("X2")));

    public override string ToString() =>
        $"{(Connected ? "●" : "○")} {(string.IsNullOrEmpty(Name) ? MacString : Name)} ({MacString})";
}

/// <summary>Pairing-mode values for CMD 0x0b.</summary>
public enum PairingMode : byte
{
    Close = 0x00,
    Auto = 0x01,
    Manual = 0x02,
}

/// <summary>A nearby device reported by a scan-result event (CMD 0x81) during pairing.</summary>
public sealed class DiscoveredDevice
{
    public required byte[] Mac { get; init; }
    public string Name { get; init; } = "";
    public int Rssi { get; init; }

    public string MacString => string.Join(":", Mac.Select(b => b.ToString("X2")));

    // Payload: 9-byte scan header, then MAC[6] · 00 · rssi · 00 · nameLen · 00 · name(nameLen incl NUL).
    private const int HeaderLen = 9;

    public static bool TryParse(byte[] p, out DiscoveredDevice device)
    {
        device = null!;
        if (p.Length < HeaderLen + 6) return false;

        var mac = p.AsSpan(HeaderLen, 6).ToArray();
        int rssi = p.Length > HeaderLen + 7 ? p[HeaderLen + 7] : 0;

        string name = "";
        int nameLenPos = HeaderLen + 9;   // byte after "MAC 00 rssi 00"
        if (p.Length > nameLenPos)
        {
            int nameLen = p[nameLenPos];
            int nameStart = nameLenPos + 2;   // skip the nameLen byte and a 00 separator
            if (nameLen > 0 && nameStart + nameLen <= p.Length)
                name = System.Text.Encoding.ASCII.GetString(p, nameStart, nameLen).TrimEnd('\0', ' ');
        }

        device = new DiscoveredDevice { Mac = mac, Name = name, Rssi = rssi };
        return true;
    }

    public override string ToString() => $"{(string.IsNullOrEmpty(Name) ? MacString : Name)} ({MacString})";
}
