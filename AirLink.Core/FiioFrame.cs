using System.Text;

namespace AirLink.Core;

/// <summary>
/// Encodes/decodes the FiiO Air Link HID control frame:
///   FF 03 | LEN(16-bit BE) | 00 1D | DIR | CMD | payload[LEN]
/// See FiiO-AirLink-Protocol.md.
/// </summary>
public static class FiioFrame
{
    public const byte DirRequest = 0x30;   // host -> device (request)
    public const byte DirEvent = 0x30;     // device -> host async event (same value; cmd is 0x8x)
    public const byte DirResponse = 0x31;  // device -> host (standard get/set reply)
    // Offset-6 is a message-type field. 0x30 = request OR async event (distinguished by cmd>=0x80),
    // 0x31 = standard reply, 0x01 = an alternate reply class (e.g. firmware string).

    private static readonly byte[] Magic = { 0xFF, 0x03 };
    private const byte Const0 = 0x00;       // byte[4]
    private const byte Const1 = 0x1D;       // byte[5]

    /// <summary>Build a host frame (without the report-id byte). Default type is a request (0x30).</summary>
    public static byte[] BuildRequest(byte cmd, ReadOnlySpan<byte> payload = default, byte type = DirRequest)
    {
        int len = payload.Length;
        var f = new byte[8 + len];
        f[0] = Magic[0];
        f[1] = Magic[1];
        f[2] = (byte)(len >> 8);
        f[3] = (byte)(len & 0xFF);
        f[4] = Const0;
        f[5] = Const1;
        f[6] = type;
        f[7] = cmd;
        payload.CopyTo(f.AsSpan(8));
        return f;
    }

    /// <summary>
    /// Parse a device reply frame. <paramref name="data"/> is the report payload
    /// (report-id byte already stripped). Returns the message-type byte, command, payload.
    /// </summary>
    public static (byte type, byte cmd, byte[] payload) ParseResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new InvalidDataException($"Frame too short ({data.Length} bytes).");
        if (data[0] != Magic[0] || data[1] != Magic[1])
            throw new InvalidDataException($"Bad magic {data[0]:x2} {data[1]:x2}.");

        int len = (data[2] << 8) | data[3];
        byte type = data[6];
        byte cmd = data[7];

        if (8 + len > data.Length)
            throw new InvalidDataException($"Declared len {len} exceeds frame ({data.Length}).");

        return (type, cmd, data.Slice(8, len).ToArray());
    }

    public static string Hex(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length * 3);
        foreach (var x in b) sb.Append(x.ToString("x2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    public static byte[] ParseHex(IEnumerable<string> tokens)
    {
        var list = new List<byte>();
        foreach (var t in tokens)
        {
            var s = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t;
            list.Add(Convert.ToByte(s, 16));
        }
        return list.ToArray();
    }
}
