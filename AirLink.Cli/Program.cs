using AirLink.Core;

return Cli.Run(args);

static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "list": return CmdList();
                case "info": return CmdInfo();
                case "codec": return CmdCodec(args);
                case "brightness": return CmdBrightness(args);
                case "mode": return CmdMode(args);
                case "devices": return CmdDevices();
                case "connect": return CmdMacAction(args, (d, m) => d.ConnectDevice(m), "connect");
                case "disconnect": return CmdMacAction(args, (d, m) => d.DisconnectDevice(m), "disconnect");
                case "forget": return CmdMacAction(args, (d, m) => d.ForgetDevice(m), "forget");
                case "pair": return CmdPair(args);
                case "pairnew": return CmdPairNew(args);
                case "scan": return CmdScan(args);
                case "eventmon": return CmdEventMon(args);
                case "raw": return CmdRaw(args);
                default: Usage(); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    static int CmdList()
    {
        var devs = AirLinkDevice.Enumerate().ToList();
        if (devs.Count == 0) { Console.WriteLine("No FiiO device (2972:0158) found."); return 1; }
        foreach (var d in devs)
            Console.WriteLine($"VID 0x{d.VendorID:x4} PID 0x{d.ProductID:x4}  " +
                              $"out={d.GetMaxOutputReportLength()} in={d.GetMaxInputReportLength()}  " +
                              $"\"{Safe(() => d.GetProductName())}\"  {d.DevicePath}");
        return 0;
    }

    static int CmdInfo()
    {
        using var dev = AirLinkDevice.Open();
        Console.WriteLine($"Path        : {dev.DevicePath}");
        Console.WriteLine($"Report len  : out={dev.MaxOutputReportLength} in={dev.MaxInputReportLength}");
        Console.WriteLine($"Name        : {dev.GetName()}");
        Console.WriteLine($"Brightness  : {dev.GetBrightness()} / {Codec.MaxBrightness}");
        Console.WriteLine($"LDAC mode   : {ModeLabel(Codec.LdacModes, dev.GetLdacMode())}");
        Console.WriteLine($"aptX Ad mode: {ModeLabel(Codec.AptxAdaptiveModes, dev.GetAptxAdaptiveMode())}");

        var codecs = dev.GetCodecs();
        var supported = dev.GetSupportedCodecs();
        Console.WriteLine($"Enabled list: {FiioFrame.Hex(codecs)}");
        Console.WriteLine($"Supported   : {FiioFrame.Hex(supported)}");
        var enabled = new HashSet<byte>(codecs);
        Console.WriteLine("Codecs:");
        foreach (var name in Codec.ToggleableNames)
        {
            Codec.TryParse(name, out var id);
            Console.WriteLine($"  {(enabled.Contains(id) ? "[x]" : "[ ]")} {Codec.Describe(id)}");
        }
        return 0;
    }

    static int CmdCodec(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: codec <name> on|off"); return 1; }
        if (!Codec.TryParse(args[1], out var id))
        {
            Console.Error.WriteLine($"unknown codec '{args[1]}'. options: {string.Join(", ", Codec.ToggleableNames)}");
            return 1;
        }
        bool enable = args[2].ToLowerInvariant() is "on" or "true" or "1" or "enable";

        using var dev = AirLinkDevice.Open();
        Console.WriteLine($"before: {FiioFrame.Hex(dev.GetCodecs())}");
        var next = dev.SetCodecEnabled(id, enable);
        Console.WriteLine($"after : {FiioFrame.Hex(next)}");
        Console.WriteLine($"{Codec.Describe(id)} -> {(enable ? "ON" : "OFF")}");
        return 0;
    }

    static int CmdBrightness(string[] args)
    {
        using var dev = AirLinkDevice.Open();
        if (args.Length < 2)
        {
            Console.WriteLine($"brightness = {dev.GetBrightness()} / {Codec.MaxBrightness}");
            return 0;
        }
        byte level = (byte)int.Parse(args[1]);
        dev.SetBrightness(level);
        Console.WriteLine($"brightness -> {dev.GetBrightness()}");
        return 0;
    }

    // mode ldac [0..2]            get/set LDAC quality
    // mode adaptive [value]       get/set aptX Adaptive mode (0x02/0x03/0x13 or ll/hq/lossless)
    static int CmdMode(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: mode ldac|adaptive [value]"); return 1; }
        using var dev = AirLinkDevice.Open();
        switch (args[1].ToLowerInvariant())
        {
            case "ldac":
                if (args.Length < 3) { Console.WriteLine(ModeLabel(Codec.LdacModes, dev.GetLdacMode())); return 0; }
                dev.SetLdacMode((byte)int.Parse(args[2]));
                Console.WriteLine("LDAC mode -> " + ModeLabel(Codec.LdacModes, dev.GetLdacMode()));
                return 0;
            case "adaptive":
                if (args.Length < 3) { Console.WriteLine(ModeLabel(Codec.AptxAdaptiveModes, dev.GetAptxAdaptiveMode())); return 0; }
                byte v = args[2].ToLowerInvariant() switch
                {
                    "ll" or "lowlatency" => 0x02,
                    "hq" or "highquality" => 0x03,
                    "lossless" => 0x13,
                    var s => Convert.ToByte(s.Replace("0x", ""), 16),
                };
                dev.SetAptxAdaptiveMode(v);
                Console.WriteLine("aptX Adaptive mode -> " + ModeLabel(Codec.AptxAdaptiveModes, dev.GetAptxAdaptiveMode()));
                return 0;
            default:
                Console.Error.WriteLine("usage: mode ldac|adaptive [value]");
                return 1;
        }
    }

    static int CmdDevices()
    {
        using var dev = AirLinkDevice.Open();
        var devices = dev.GetPairedDevices();
        if (devices.Count == 0) { Console.WriteLine("No paired devices."); return 0; }
        foreach (var d in devices)
            Console.WriteLine($"  {(d.Connected ? "●" : "○")} {d.MacString}  {d.Name}");
        return 0;
    }

    static byte[] ParseMac(string s)
    {
        var parts = s.Split(':', '-');
        if (parts.Length != 6) throw new FormatException("MAC must be 6 colon/dash-separated bytes, e.g. 58:18:62:4B:AC:67");
        return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
    }

    static int CmdMacAction(string[] args, Action<AirLinkDevice, byte[]> action, string verb)
    {
        if (args.Length < 2) { Console.Error.WriteLine($"usage: {verb} <mac>"); return 1; }
        var mac = ParseMac(args[1]);
        using var dev = AirLinkDevice.Open();
        action(dev, mac);
        Console.WriteLine($"{verb} {string.Join(":", mac.Select(b => b.ToString("X2")))} -> ok");
        return 0;
    }

    static int CmdPair(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pair auto|manual|close"); return 1; }
        var mode = args[1].ToLowerInvariant() switch
        {
            "auto" => PairingMode.Auto,
            "manual" => PairingMode.Manual,
            "close" or "off" => PairingMode.Close,
            _ => throw new FormatException("pair mode must be auto|manual|close"),
        };
        using var dev = AirLinkDevice.Open();
        dev.SetPairingMode(mode);
        Console.WriteLine($"pairing mode -> {mode}");
        return 0;
    }

    static int CmdScan(string[] args)
    {
        int secs = args.Length > 1 ? int.Parse(args[1]) : 15;
        using var dev = AirLinkDevice.Open();

        var lastName = new Dictionary<string, string>();
        var gate = new object();
        dev.DeviceDiscovered += d =>
        {
            lock (gate)
            {
                if (lastName.TryGetValue(d.MacString, out var prev) && prev == d.Name) return;
                lastName[d.MacString] = d.Name;
                Console.WriteLine($"  {d.MacString}  rssi={d.Rssi,3}  {(string.IsNullOrEmpty(d.Name) ? "(no name yet)" : d.Name)}");
            }
        };

        Console.WriteLine($"Scanning (manual pairing) for {secs}s — put a device into pairing mode now...");
        dev.SetPairingMode(PairingMode.Manual);
        Thread.Sleep(secs * 1000);
        dev.SetPairingMode(PairingMode.Close);
        Console.WriteLine($"Done. {lastName.Count} device(s) seen.");
        return 0;
    }

    // pairnew <mac> — enter manual pairing, then pair+connect the given (discovered) device.
    static int CmdPairNew(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pairnew <mac>"); return 1; }
        var mac = ParseMac(args[1]);
        using var dev = AirLinkDevice.Open();

        using var discovered = new ManualResetEventSlim(false);
        dev.DeviceDiscovered += d =>
        {
            if (d.Mac.AsSpan().SequenceEqual(mac)) discovered.Set();
        };

        Console.WriteLine("Entering manual pairing, waiting for the target to be discovered...");
        dev.SetPairingMode(PairingMode.Manual);
        if (!discovered.Wait(20000))
        {
            dev.SetPairingMode(PairingMode.Close);
            Console.Error.WriteLine("Target not discovered — is it in pairing mode?");
            return 2;
        }

        Console.WriteLine("Discovered — sending pair + connect...");
        dev.PairAndConnect(mac);

        // Poll for the bond to appear (the link takes a moment to establish).
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(1500);
            var d = dev.GetPairedDevices().FirstOrDefault(x => x.Mac.AsSpan().SequenceEqual(mac));
            if (d != null) { Console.WriteLine($"  paired: {(d.Connected ? "● connected" : "○ paired")}  {d.Name}"); break; }
            if (i == 4) Console.WriteLine("  target did not appear in the paired list.");
        }

        dev.SetPairingMode(PairingMode.Close);
        Console.WriteLine("Final paired list:");
        foreach (var d in dev.GetPairedDevices())
            Console.WriteLine($"  {(d.Connected ? "●" : "○")} {d.MacString}  {d.Name}");
        return 0;
    }

    // eventmon [secs] [auto|manual] — enter pairing and print EVERY input frame.
    static int CmdEventMon(string[] args)
    {
        int secs = args.Length > 1 ? int.Parse(args[1]) : 12;
        var mode = args.Length > 2 && args[2].StartsWith("a", StringComparison.OrdinalIgnoreCase)
            ? PairingMode.Auto : PairingMode.Manual;
        using var dev = AirLinkDevice.Open();
        int count = 0;
        dev.FrameReceived += (t, c, p) =>
        {
            Interlocked.Increment(ref count);
            Console.WriteLine($"  type={t:x2} cmd={c:x2} len={p.Length,2} | {FiioFrame.Hex(p)}");
        };
        Console.WriteLine($"Entering {mode} pairing, monitoring all frames for {secs}s...");
        dev.SetPairingMode(mode);
        Thread.Sleep(secs * 1000);
        dev.SetPairingMode(PairingMode.Close);
        Console.WriteLine($"Done. {count} frame(s) received.");
        return 0;
    }

    static string ModeLabel((byte value, string label)[] modes, int current)
    {
        foreach (var (value, label) in modes)
            if (value == current) return $"{label} (0x{value:x2})";
        return current < 0 ? "?" : $"unknown (0x{current:x2})";
    }

    // raw <cmd>                -> GET (empty payload), prints response payload
    // raw <cmd> <bytes...>     -> SET with payload, prints ack
    static int CmdRaw(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: raw <cmd> [payload bytes...]"); return 1; }
        byte cmd = Convert.ToByte(args[1].Replace("0x", ""), 16);
        byte[] payload = FiioFrame.ParseHex(args.Skip(2));
        using var dev = AirLinkDevice.Open();
        if (payload.Length > 0)
        {
            dev.Set(cmd, payload);
            Console.WriteLine($"set cmd 0x{cmd:x2} payload {FiioFrame.Hex(payload)} -> ack");
        }
        else
        {
            var resp = dev.Get(cmd);
            Console.WriteLine($"cmd 0x{cmd:x2} -> {FiioFrame.Hex(resp)}");
        }
        return 0;
    }

    static string Safe(Func<string> f) { try { return f(); } catch { return "?"; } }

    static void Usage()
    {
        Console.WriteLine(
            """
            AirLink CLI — FiiO Air Link control (unofficial)

              airlink list                       enumerate matching HID devices
              airlink info                       name, firmware, brightness, enabled codecs
              airlink codec <name> on|off        toggle a codec
                  names: ldac, aptx-adaptive, aptx-hd, aptx, aptx-ll
              airlink brightness [0..7]          get or set indicator brightness
              airlink mode ldac [0..2]           LDAC quality (0=990 1=660 2=330)
              airlink mode adaptive [ll|hq|lossless]   aptX Adaptive mode
              airlink devices                    list paired devices (● = connected)
              airlink connect|disconnect|forget <mac>  act on a paired device
              airlink pair auto|manual|close     set Bluetooth pairing mode
              airlink raw <cmd> [bytes...]       GET (no payload) or SET a raw frame
            """);
    }
}
