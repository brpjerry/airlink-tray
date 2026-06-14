using AirLink.Core;

namespace AirLink.Tray;

/// <summary>
/// Tray icon + context menu controlling the FiiO Air Link (codecs + brightness).
/// State is re-read from the device every time the menu opens, and a light presence
/// timer flips the connected/disconnected indicator on plug/unplug.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _presenceTimer;

    private AirLinkDevice? _dev;
    private bool _lastPresent;

    private readonly Dictionary<byte, ToolStripMenuItem> _enabledItems = new(); // id -> enable toggle
    private readonly Dictionary<byte, ToolStripMenuItem> _codecParents = new(); // id -> submenu root (mode codecs)
    private readonly Dictionary<byte, List<ToolStripMenuItem>> _modeItems = new(); // id -> mode radios
    private readonly List<ToolStripMenuItem> _brightnessItems = new();
    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _brightnessRoot = null!;
    private ToolStripMenuItem _pairedRoot = null!;
    private ToolStripMenuItem _pairingRoot = null!;
    private ToolStripMenuItem _autoStartItem = null!;

    private PairingForm? _pairingForm;

    /// <summary>Identifies a codec sub-mode menu item.</summary>
    private readonly record struct ModeTag(byte CodecId, byte Value);

    // The five user-toggleable codecs, in menu order. LDAC and aptX Adaptive carry a
    // sub-mode table (quality / latency); the rest are simple on/off toggles. See protocol doc §4.
    private static readonly (byte id, string label, (byte value, string label)[]? modes)[] CodecDefs =
    {
        (Codec.Ldac,         "LDAC",          Codec.LdacModes),
        (Codec.AptxAdaptive, "aptX Adaptive", Codec.AptxAdaptiveModes),
        (Codec.AptxHd,       "aptX HD",       null),
        (Codec.Aptx,         "aptX",          null),
        (Codec.AptxLl,       "aptX LL",       null),
    };

    public TrayAppContext()
    {
        _menu = BuildMenu();
        _menu.Opening += (_, _) => { try { RefreshState(); } catch { /* never crash the tray menu */ } };

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Disconnected,
            Text = "FiiO Air Link",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _presenceTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _presenceTimer.Tick += (_, _) => UpdatePresence();
        _presenceTimer.Start();

        _lastPresent = AirLinkDevice.Enumerate().Any();
        RefreshState();
    }

    // ---- menu construction -------------------------------------------------

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Disconnected") { Enabled = false };
        m.Items.Add(_statusItem);
        m.Items.Add(new ToolStripSeparator());

        foreach (var (id, label, modes) in CodecDefs)
        {
            if (modes is null)
            {
                var item = new ToolStripMenuItem(label) { CheckOnClick = false, Tag = id };
                item.Click += OnCodecClick;
                _enabledItems[id] = item;
                m.Items.Add(item);
            }
            else
            {
                var parent = new ToolStripMenuItem(label);
                var enabled = new ToolStripMenuItem("Enabled") { CheckOnClick = false, Tag = id };
                enabled.Click += OnCodecClick;
                _enabledItems[id] = enabled;
                parent.DropDownItems.Add(enabled);
                parent.DropDownItems.Add(new ToolStripSeparator());

                var radios = new List<ToolStripMenuItem>();
                foreach (var (value, mlabel) in modes)
                {
                    var mi = new ToolStripMenuItem(mlabel) { Tag = new ModeTag(id, value) };
                    mi.Click += OnModeClick;
                    radios.Add(mi);
                    parent.DropDownItems.Add(mi);
                }
                _modeItems[id] = radios;
                _codecParents[id] = parent;
                m.Items.Add(parent);
            }
        }
        m.Items.Add(new ToolStripSeparator());

        _brightnessRoot = new ToolStripMenuItem("Brightness");
        for (int lvl = Codec.MaxBrightness; lvl >= 0; lvl--)
        {
            string text = lvl == Codec.MaxBrightness ? $"{lvl}  (brightest)"
                        : lvl == 0 ? "0  (dimmest)"
                        : lvl.ToString();
            var it = new ToolStripMenuItem(text) { Tag = (byte)lvl };
            it.Click += OnBrightnessClick;
            _brightnessItems.Add(it);
            _brightnessRoot.DropDownItems.Add(it);
        }
        m.Items.Add(_brightnessRoot);
        m.Items.Add(new ToolStripSeparator());

        _pairedRoot = new ToolStripMenuItem("Paired devices");
        m.Items.Add(_pairedRoot);
        _pairingRoot = new ToolStripMenuItem("Pairing");
        m.Items.Add(_pairingRoot);
        m.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled(),
        };
        _autoStartItem.CheckedChanged += (_, _) => AutoStart.Set(_autoStartItem.Checked);
        m.Items.Add(_autoStartItem);

        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += (_, _) => RefreshState();
        m.Items.Add(refresh);

        m.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        m.Items.Add(quit);

        return m;
    }

    // ---- device connection -------------------------------------------------

    private bool EnsureOpen()
    {
        if (_dev != null) return true;
        try
        {
            if (!AirLinkDevice.Enumerate().Any()) return false;
            _dev = AirLinkDevice.Open();
            return true;
        }
        catch { Drop(); return false; }
    }

    private void Drop()
    {
        try { _dev?.Dispose(); } catch { /* ignore */ }
        _dev = null;
    }

    // ---- state sync --------------------------------------------------------

    private void RefreshState()
    {
        // While the pairing window owns the device, leave the menu as-is — avoids request
        // contention and never Drops the device out from under the window.
        if (_pairingForm is { IsDisposed: false }) return;

        bool ok = false;
        int brightness = -1;
        int ldacMode = -1, aptxMode = -1;
        var enabled = new HashSet<byte>();
        IReadOnlyList<PairedDevice> devices = Array.Empty<PairedDevice>();

        try
        {
            if (EnsureOpen() && _dev != null)
            {
                brightness = _dev.GetBrightness();
                enabled = new HashSet<byte>(_dev.GetCodecs());
                ldacMode = _dev.GetLdacMode();
                aptxMode = _dev.GetAptxAdaptiveMode();
                devices = _dev.GetPairedDevices();
                ok = true;
            }
        }
        catch { Drop(); ok = false; }

        _tray.Icon = ok ? TrayIcons.Connected : TrayIcons.Disconnected;
        _tray.Text = ok
            ? $"FiiO Air Link — brightness {brightness}/{Codec.MaxBrightness}"
            : "FiiO Air Link — disconnected";
        _statusItem.Text = ok ? "●  Connected" : "○  Disconnected";

        foreach (var (id, _, _) in CodecDefs)
        {
            var item = _enabledItems[id];
            item.Enabled = ok;
            item.Checked = ok && enabled.Contains(id);
            if (_codecParents.TryGetValue(id, out var parent)) parent.Enabled = ok;
        }
        UpdateModeRadios(Codec.Ldac, ldacMode, ok);
        UpdateModeRadios(Codec.AptxAdaptive, aptxMode, ok);
        RebuildPairedMenu(devices, ok);
        RebuildPairingMenu(ok);

        _brightnessRoot.Enabled = ok;
        foreach (var it in _brightnessItems)
            it.Checked = ok && it.Tag is byte lvl && lvl == brightness;
    }

    private void UpdateModeRadios(byte codecId, int current, bool ok)
    {
        if (!_modeItems.TryGetValue(codecId, out var items)) return;
        foreach (var it in items)
        {
            it.Enabled = ok;
            it.Checked = ok && it.Tag is ModeTag t && t.Value == current;
        }
    }

    // Snapshot then clear before disposing: ToolStripItem.Dispose() removes itself from the
    // parent collection, which would otherwise mutate DropDownItems mid-enumeration.
    private static void ClearDropDown(ToolStripMenuItem root)
    {
        var stale = root.DropDownItems.Cast<ToolStripItem>().ToArray();
        root.DropDownItems.Clear();
        foreach (var old in stale) old.Dispose();
    }

    private void RebuildPairedMenu(IReadOnlyList<PairedDevice> devices, bool ok)
    {
        ClearDropDown(_pairedRoot);
        _pairedRoot.Enabled = ok;
        if (!ok) return;

        if (devices.Count == 0)
            _pairedRoot.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });

        foreach (var d in devices)
        {
            string title = (d.Connected ? "●  " : "○  ") + (string.IsNullOrEmpty(d.Name) ? d.MacString : d.Name);
            var item = new ToolStripMenuItem(title);
            var mac = d.Mac;

            if (d.Connected)
                item.DropDownItems.Add(MenuAction("Disconnect", () => DeviceAction(dev => dev.DisconnectDevice(mac), "Disconnect")));
            else
                item.DropDownItems.Add(MenuAction("Connect", () => DeviceAction(dev => dev.ConnectDevice(mac), "Connect")));

            string label = string.IsNullOrEmpty(d.Name) ? d.MacString : d.Name;
            item.DropDownItems.Add(MenuAction("Forget…", () => ForgetDevice(mac, label)));
            _pairedRoot.DropDownItems.Add(item);
        }

        _pairedRoot.DropDownItems.Add(new ToolStripSeparator());
        _pairedRoot.DropDownItems.Add(MenuAction("Refresh list", RefreshState));
    }

    private void RebuildPairingMenu(bool ok)
    {
        ClearDropDown(_pairingRoot);
        _pairingRoot.Enabled = ok;
        if (!ok) return;
        // "Connect a device" = the manual pick-a-device window (you choose which to pair).
        _pairingRoot.DropDownItems.Add(MenuAction("Connect a device…", OpenPairing));
        _pairingRoot.DropDownItems.Add(new ToolStripSeparator());
        // Auto = let the dongle reconnect/grab known or nearby sinks on its own.
        _pairingRoot.DropDownItems.Add(MenuAction("Auto-reconnect known devices",
            () => ApplyPairing(Core.PairingMode.Auto, "Auto-reconnect on — the dongle will connect to a known/nearby device.")));
        // The dongle auto-searches when idle; this gives the user a way to stop the flashing.
        _pairingRoot.DropDownItems.Add(MenuAction("Stop searching",
            () => ApplyPairing(Core.PairingMode.Close, "Stopped searching.")));
    }

    private void OpenPairing()
    {
        if (_pairingForm is { IsDisposed: false }) { _pairingForm.Activate(); return; }
        if (!EnsureOpen() || _dev == null) { Warn("Air Link not connected."); return; }

        _pairingForm = new PairingForm(_dev);
        _pairingForm.FormClosed += (_, _) => { _pairingForm = null; RefreshState(); };
        _pairingForm.Show();
        _pairingForm.Activate();
    }

    private void ApplyPairing(Core.PairingMode mode, string message)
    {
        try
        {
            if (!EnsureOpen() || _dev == null) { Warn("Air Link not connected."); return; }
            _dev.SetPairingMode(mode);
            Warn(message);
        }
        catch { Drop(); Warn("Couldn't change pairing state."); }
        finally { RefreshState(); }
    }

    private static ToolStripMenuItem MenuAction(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    private void DeviceAction(Action<AirLinkDevice> act, string label)
    {
        try
        {
            if (!EnsureOpen() || _dev == null) { Warn("Air Link not connected."); return; }
            act(_dev);
        }
        catch { Drop(); Warn($"{label} failed — device busy or unplugged."); }
        finally { RefreshState(); }
    }

    private void ForgetDevice(byte[] mac, string label)
    {
        if (MessageBox.Show($"Forget \"{label}\"?\nYou'll need to pair it again.", "FiiO Air Link",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        DeviceAction(dev => dev.ForgetDevice(mac), "Forget");
    }

    private void UpdatePresence()
    {
        bool present = AirLinkDevice.Enumerate().Any();
        if (present == _lastPresent) return;
        _lastPresent = present;
        if (!present) Drop();
        RefreshState();
    }

    // ---- actions -----------------------------------------------------------

    private void OnCodecClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.Tag is not byte id) return;
        bool enable = !item.Checked; // current check reflects last-read device state
        try
        {
            if (!EnsureOpen() || _dev == null) { Warn("Air Link not connected."); return; }
            _dev.SetCodecEnabled(id, enable);
        }
        catch { Drop(); Warn("Couldn't change codec — device busy or unplugged."); }
        finally { RefreshState(); }
    }

    private void OnModeClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.Tag is not ModeTag tag) return;
        try
        {
            if (!EnsureOpen() || _dev == null) { Warn("Air Link not connected."); return; }
            if (tag.CodecId == Codec.Ldac) _dev.SetLdacMode(tag.Value);
            else if (tag.CodecId == Codec.AptxAdaptive) _dev.SetAptxAdaptiveMode(tag.Value);
        }
        catch { Drop(); Warn("Couldn't change codec mode — device busy or unplugged."); }
        finally { RefreshState(); }
    }

    private void OnBrightnessClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.Tag is not byte level) return;
        try
        {
            if (!EnsureOpen() || _dev == null) { Warn("Air Link not connected."); return; }
            _dev.SetBrightness(level);
        }
        catch { Drop(); Warn("Couldn't set brightness — device busy or unplugged."); }
        finally { RefreshState(); }
    }

    private void Warn(string msg) => _tray.ShowBalloonTip(3000, "FiiO Air Link", msg, ToolTipIcon.Warning);

    private void Quit()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _presenceTimer?.Dispose();
            if (_pairingForm is { IsDisposed: false }) _pairingForm.Close();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Drop();
        }
        base.Dispose(disposing);
    }
}
