using System.Text;
using HidSharp;

namespace AirLink.Core;

/// <summary>
/// Opens the FiiO Air Link vendor HID control interface and exposes the
/// request->response protocol (GET = empty payload, SET = value payload, both ACK'd).
/// </summary>
public sealed class AirLinkDevice : IDisposable
{
    public const int VendorId = 0x2972;
    public const int ProductId = 0x0158;

    // Report ids carried as byte[0] of each HID report.
    private const byte OutputReportId = 0x07;
    private const byte InputReportId = 0x08;

    // Commands (see FiiO-AirLink-Protocol.md §3).
    public const byte CmdName = 0x00;
    public const byte CmdSupportedCodecs = 0x05; // capability list (read-only)
    public const byte CmdGetCodecs = 0x06;
    public const byte CmdSetCodecs = 0x07;

    // Pairing / device management (see protocol doc §6).
    public const byte CmdRefreshDevices = 0x0A;
    public const byte CmdPairingMode = 0x0B;
    public const byte CmdDeviceTable = 0x0E;
    public const byte CmdDeviceName = 0x0F;   // request payload = MAC frame; reply = ASCII
    public const byte CmdConnect = 0x10;
    public const byte CmdDisconnect = 0x11;
    public const byte CmdPair = 0x12;
    public const byte CmdForget = 0x13;
    public const byte CmdGetAptxAdaptiveMode = 0x40;
    public const byte CmdSetAptxAdaptiveMode = 0x41;
    public const byte CmdGetLdacMode = 0x42;
    public const byte CmdSetLdacMode = 0x43;
    public const byte CmdGetBrightness = 0x52;
    public const byte CmdSetBrightness = 0x53;

    private readonly HidDevice _dev;
    private readonly HidStream _stream;

    // Single background reader dispatches replies to the pending request and
    // routes async 0x8x event frames to subscribers. Requests are serialized.
    private readonly Thread _reader;
    private volatile bool _running = true;
    private readonly object _reqLock = new();     // one request at a time
    private readonly object _replyLock = new();    // guards the reply slot
    private readonly AutoResetEvent _replyReady = new(false);
    private byte _awaitCmd;
    private bool _waiting;
    private byte[]? _reply;

    /// <summary>Raised (on the reader thread) for each scan-result event during pairing.</summary>
    public event Action<DiscoveredDevice>? DeviceDiscovered;

    /// <summary>Raised (on the reader thread) when the dongle pushes an updated paired-device table.</summary>
    public event Action? PairedListChanged;

    /// <summary>Diagnostic: raised (reader thread) for every parsed input frame (type, cmd, payload).</summary>
    public event Action<byte, byte, byte[]>? FrameReceived;

    private AirLinkDevice(HidDevice dev, HidStream stream)
    {
        _dev = dev;
        _stream = stream;
        _stream.ReadTimeout = 250;   // short, so the reader can notice shutdown
        _stream.WriteTimeout = 2000;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "AirLink HID reader" };
        _reader.Start();
    }

    public static IEnumerable<HidDevice> Enumerate() =>
        DeviceList.Local.GetHidDevices(VendorId, ProductId);

    /// <summary>
    /// Open the control interface. The dongle exposes several HID collections; we open each
    /// usable one and keep the one that actually answers a GET (the real control channel),
    /// rather than guessing from report sizes.
    /// </summary>
    public static AirLinkDevice Open()
    {
        var candidates = Enumerate()
            .Where(d => d.GetMaxOutputReportLength() >= 9) // room for report-id + frame
            // Prefer collections whose input report is ~64 bytes (matches captured frames).
            .OrderBy(d => Math.Abs(d.GetMaxInputReportLength() - 64))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No usable FiiO HID interface found (VID 0x{VendorId:x4} PID 0x{ProductId:x4}). Is it plugged in?");

        Exception? last = null;
        foreach (var d in candidates)
        {
            HidStream? stream = null;
            try
            {
                var opts = new OpenConfiguration();
                opts.SetOption(OpenOption.Exclusive, false);
                if (!d.TryOpen(opts, out stream)) continue;

                var dev = new AirLinkDevice(d, stream);
                if (dev.Probe()) return dev; // this is the control interface
                stream.Dispose();
            }
            catch (Exception ex) { last = ex; stream?.Dispose(); }
        }
        throw new InvalidOperationException(
            "Found the device but no interface answered the control protocol. " +
            "Close the FiiO Control app / web page (it may hold the handle), then retry.", last);
    }

    /// <summary>Quick GET-name to confirm this interface speaks the control protocol.</summary>
    private bool Probe()
    {
        try { _ = Transact(CmdName, ReadOnlySpan<byte>.Empty, timeoutMs: 700); return true; }
        catch { return false; }
    }

    public string DevicePath => _dev.DevicePath;
    public int MaxOutputReportLength => _dev.GetMaxOutputReportLength();
    public int MaxInputReportLength => _dev.GetMaxInputReportLength();

    /// <summary>Background loop: read every input report, deliver replies, dispatch events.</summary>
    private void ReadLoop()
    {
        var buf = new byte[Math.Max(MaxInputReportLength, 64)];
        while (_running)
        {
            int n;
            try { n = _stream.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch { break; } // stream closed / device gone
            if (n <= 1) continue;

            byte type, cmd; byte[] payload;
            try { (type, cmd, payload) = FiioFrame.ParseResponse(buf.AsSpan(1, n - 1)); }
            catch (InvalidDataException) { continue; }

            FrameReceived?.Invoke(type, cmd, payload);

            if (type == FiioFrame.DirEvent && cmd >= 0x80)
            {
                DispatchEvent(cmd, payload);
            }
            else
            {
                // Replies normally echo the request cmd, but some (e.g. connect 0x10) come back
                // with the high bit set (0x90). Accept either as the ack for the awaited cmd.
                lock (_replyLock)
                {
                    if (_waiting && (cmd == _awaitCmd || cmd == (byte)(_awaitCmd | 0x80)))
                    {
                        _reply = payload;
                        _replyReady.Set();
                    }
                }
            }
        }
    }

    private void DispatchEvent(byte cmd, byte[] payload)
    {
        switch (cmd)
        {
            case 0x81 when DiscoveredDevice.TryParse(payload, out var d):
                DeviceDiscovered?.Invoke(d);
                break;
            case 0x83: // pushed paired-device table
                PairedListChanged?.Invoke();
                break;
            // 0x84/0x85/0x86 are scan/connection progress — ignored for now.
        }
    }

    /// <summary>Send a request frame and return the matching reply payload (reader delivers it).</summary>
    private byte[] Transact(byte cmd, ReadOnlySpan<byte> payload, int timeoutMs = 2000)
    {
        byte[] frame = FiioFrame.BuildRequest(cmd, payload);
        var report = new byte[Math.Max(MaxOutputReportLength, frame.Length + 1)];
        report[0] = OutputReportId;
        frame.CopyTo(report.AsSpan(1));

        lock (_reqLock)
        {
            lock (_replyLock) { _awaitCmd = cmd; _reply = null; _waiting = true; _replyReady.Reset(); }
            try
            {
                _stream.Write(report);
                if (!_replyReady.WaitOne(timeoutMs))
                    throw new TimeoutException($"No response for cmd 0x{cmd:x2}.");
                lock (_replyLock) { return _reply ?? Array.Empty<byte>(); }
            }
            finally { lock (_replyLock) { _waiting = false; } }
        }
    }

    public byte[] Get(byte cmd) => Transact(cmd, ReadOnlySpan<byte>.Empty);
    public void Set(byte cmd, ReadOnlySpan<byte> payload) => Transact(cmd, payload);

    /// <summary>Fire-and-forget frame with a custom type byte (e.g. the type=0x00 scan trigger). No reply awaited.</summary>
    public void SendNoReply(byte type, byte cmd, ReadOnlySpan<byte> payload)
    {
        byte[] frame = FiioFrame.BuildRequest(cmd, payload, type);
        var report = new byte[Math.Max(MaxOutputReportLength, frame.Length + 1)];
        report[0] = OutputReportId;
        frame.CopyTo(report.AsSpan(1));
        lock (_reqLock) { _stream.Write(report); }
    }
    /// <summary>Send a request with a payload and return the reply payload (e.g. name-by-MAC).</summary>
    public byte[] Query(byte cmd, ReadOnlySpan<byte> payload) => Transact(cmd, payload);

    public string GetName() => AsciiTrim(Get(CmdName));
    public byte[] GetSupportedCodecs() => Get(CmdSupportedCodecs);
    public byte[] GetCodecs() => Get(CmdGetCodecs);
    public void SetCodecs(byte[] orderedIds) => Set(CmdSetCodecs, orderedIds);

    public int GetLdacMode() => Get(CmdGetLdacMode) is { Length: > 0 } p ? p[0] : -1;
    public void SetLdacMode(byte value) => Set(CmdSetLdacMode, new[] { value });
    public int GetAptxAdaptiveMode() => Get(CmdGetAptxAdaptiveMode) is { Length: > 0 } p ? p[0] : -1;
    public void SetAptxAdaptiveMode(byte value) => Set(CmdSetAptxAdaptiveMode, new[] { value });

    public int GetBrightness() => Get(CmdGetBrightness) is { Length: > 0 } p ? p[0] : -1;
    public void SetBrightness(byte level)
    {
        if (level > Codec.MaxBrightness)
            throw new ArgumentOutOfRangeException(nameof(level), $"0..{Codec.MaxBrightness}");
        Set(CmdSetBrightness, new[] { level });
    }

    /// <summary>Enable/disable a codec by rebuilding the ordered list and writing it.</summary>
    public byte[] SetCodecEnabled(byte codecId, bool enabled)
    {
        var current = GetCodecs();
        var next = Codec.ApplyToggle(current, codecId, enabled);
        SetCodecs(next);
        return next;
    }

    // ---- pairing / device management --------------------------------------

    private const int DeviceRecordSize = 12; // 00 | MAC[6] | flag | 06 | 00 00 00
    private const byte ConnectedFlag = 0x80;

    /// <summary>MAC commands wrap the 6-byte address as 00 + MAC + 00.</summary>
    private static byte[] MacFrame(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("MAC must be 6 bytes.", nameof(mac));
        var p = new byte[8];
        Array.Copy(mac, 0, p, 1, 6);
        return p;
    }

    /// <summary>Refresh and read the paired-device list, resolving each name.</summary>
    public IReadOnlyList<PairedDevice> GetPairedDevices()
    {
        Get(CmdRefreshDevices);                 // 0x0A: ask the dongle to refresh its list
        byte[] table = Get(CmdDeviceTable);     // 0x0E: count + 12-byte records

        var list = new List<PairedDevice>();
        if (table.Length == 0) return list;

        int count = table[0];
        for (int i = 0; i < count; i++)
        {
            int off = 1 + i * DeviceRecordSize;
            if (off + DeviceRecordSize > table.Length) break;

            var mac = table.AsSpan(off + 1, 6).ToArray();
            var dev = new PairedDevice { Mac = mac, Connected = table[off + 7] == ConnectedFlag };
            try { dev.Name = AsciiTrim(Query(CmdDeviceName, MacFrame(mac))); } catch { /* name optional */ }
            list.Add(dev);
        }
        return list;
    }

    public void ConnectDevice(byte[] mac) => Query(CmdConnect, MacFrame(mac));
    public void DisconnectDevice(byte[] mac) => Query(CmdDisconnect, MacFrame(mac));
    public void PairDevice(byte[] mac) => Query(CmdPair, MacFrame(mac));
    public void ForgetDevice(byte[] mac) => Query(CmdForget, MacFrame(mac));

    /// <summary>Pair then connect a discovered device (the order the web app uses).</summary>
    public void PairAndConnect(byte[] mac)
    {
        PairDevice(mac);
        ConnectDevice(mac);
    }

    // The scan engine is driven by type=0x00 frames, separate from the 0x0b mode flag:
    //   07 18 = start scanning, 08 18 = stop scanning. 0x0b alone does NOT stop the scan.
    private const byte ScanType = 0x00;
    private const byte ScanStartCmd = 0x07;
    private const byte ScanStopCmd = 0x08;
    private const byte ScanParam = 0x18;

    public void SetPairingMode(PairingMode mode)
    {
        // Replicate the web app exactly. Both entering and leaving need the type=0x00 scan
        // start/stop frame; 0x0b only sets the mode flag, so 0x0b alone never stops scanning.
        Get(CmdRefreshDevices);
        if (mode == PairingMode.Close)
        {
            SendNoReply(ScanType, ScanStopCmd, new byte[] { ScanParam });   // stop scan engine
            Set(CmdPairingMode, new[] { (byte)PairingMode.Close });
            return;
        }
        SendNoReply(ScanType, ScanStartCmd, new byte[] { ScanParam });      // start scan engine
        Set(CmdPairingMode, new[] { (byte)PairingMode.Close });             // reset
        Set(CmdPairingMode, new[] { (byte)mode });                          // enter auto/manual
    }

    private static string AsciiTrim(byte[] b) => Encoding.ASCII.GetString(b).TrimEnd('\0', ' ');

    public void Dispose()
    {
        _running = false;
        try { _stream.Dispose(); } catch { /* breaks the blocking Read */ }
        try { if (_reader.IsAlive) _reader.Join(1000); } catch { /* ignore */ }
        _replyReady.Dispose();
    }
}
