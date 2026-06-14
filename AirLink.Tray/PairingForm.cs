using System.Drawing;
using AirLink.Core;

namespace AirLink.Tray;

/// <summary>
/// Live "add a Bluetooth device" window. Puts the dongle into pairing mode and lists
/// scan-result events (CMD 0x81) as they arrive, updating in real time — no menu reopen.
/// </summary>
internal sealed class PairingForm : Form
{
    private readonly AirLinkDevice _dev;
    private readonly ListView _list;
    private readonly Button _pairBtn;
    private readonly Label _status;
    private readonly Dictionary<string, ListViewItem> _rows = new();
    private bool _closing;

    public PairingForm(AirLinkDevice dev)
    {
        _dev = dev;

        Text = "Connect a Bluetooth device";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 360);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Icon = TrayIcons.Connected;

        var tip = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 48,
            Text = "Tip: the dongle connects to the strongest nearby device. To pair a specific one, "
                 + "power off louder speakers/TVs first.",
            Padding = new Padding(8, 6, 8, 6),
        };

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Dock = DockStyle.Fill,
        };
        _list.Columns.Add("Device");
        _list.Columns.Add("Signal");
        _list.Columns.Add("Address");
        _list.DoubleClick += (_, _) => PairSelected();
        _list.SizeChanged += (_, _) => ResizeColumns();

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 28,
            Text = "Scanning… put your device into pairing mode.",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6),
        };
        // AutoSize buttons grow to fit their (DPI-scaled) text, so labels never clip.
        var close = MakeButton("Close");
        close.Click += (_, _) => Close();
        _pairBtn = MakeButton("Pair && Connect");
        _pairBtn.Enabled = false;
        _pairBtn.Click += (_, _) => PairSelected();
        buttons.Controls.Add(close);
        buttons.Controls.Add(_pairBtn);

        _list.SelectedIndexChanged += (_, _) => _pairBtn.Enabled = _list.SelectedItems.Count > 0;

        // Dock z-order: the Fill control must be added first so it takes the leftover space;
        // edges added after dock around it (buttons at the very bottom, status above them).
        Controls.Add(_list);
        Controls.Add(tip);
        Controls.Add(_status);
        Controls.Add(buttons);
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        AutoEllipsis = false,
        Padding = new Padding(12, 6, 12, 6),
        Margin = new Padding(6),
    };

    // Device column fills the leftover width; Signal/Address are fixed (DPI-scaled).
    private void ResizeColumns()
    {
        if (_list.Columns.Count < 3) return;
        int signal = LogicalToDeviceUnits(64);
        int address = LogicalToDeviceUnits(132);
        int device = _list.ClientSize.Width - signal - address - 4;   // -4 avoids a h-scrollbar
        _list.Columns[0].Width = Math.Max(device, LogicalToDeviceUnits(120));
        _list.Columns[1].Width = signal;
        _list.Columns[2].Width = address;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ResizeColumns();
        _dev.DeviceDiscovered += OnDiscovered;
        try { _dev.SetPairingMode(Core.PairingMode.Manual); }
        catch { _status.Text = "Couldn't start pairing — is the dongle busy?"; }
    }

    // Reader thread: marshal onto the UI thread.
    private void OnDiscovered(DiscoveredDevice d)
    {
        if (_closing || !IsHandleCreated) return;
        try { BeginInvoke(() => AddOrUpdate(d)); } catch { /* form closing */ }
    }

    private void AddOrUpdate(DiscoveredDevice d)
    {
        string name = string.IsNullOrEmpty(d.Name) ? "(resolving…)" : d.Name;
        if (_rows.TryGetValue(d.MacString, out var row))
        {
            row.SubItems[0].Text = name;
            row.SubItems[1].Text = d.Rssi.ToString();
        }
        else
        {
            var item = new ListViewItem(new[] { name, d.Rssi.ToString(), d.MacString }) { Tag = d.Mac };
            _rows[d.MacString] = item;
            _list.Items.Add(item);
            _status.Text = $"Found {_rows.Count} device(s). Select one and Pair & Connect.";
        }
    }

    private void PairSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var item = _list.SelectedItems[0];
        var mac = (byte[])item.Tag!;

        _status.Text = $"Pairing {item.SubItems[0].Text}…";
        _pairBtn.Enabled = false;
        try { _dev.PairAndConnect(mac); }
        catch { _status.Text = "Pairing failed — try again."; _pairBtn.Enabled = true; return; }

        _closing = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _dev.DeviceDiscovered -= OnDiscovered;
        try { _dev.SetPairingMode(Core.PairingMode.Close); } catch { /* best effort */ }
        base.OnFormClosing(e);
    }
}
