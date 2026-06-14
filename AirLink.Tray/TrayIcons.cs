using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AirLink.Tray;

/// <summary>Generates simple "AL" tray icons at runtime (no .ico asset to ship).</summary>
internal static class TrayIcons
{
    public static readonly Icon Connected = Make(Color.FromArgb(0x2E, 0x7D, 0xFF));
    public static readonly Icon Disconnected = Make(Color.FromArgb(0x77, 0x77, 0x77));

    private static Icon Make(Color bg)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var brush = new SolidBrush(bg);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("AL", font, fg, new RectangleF(0, 0, 32, 32), fmt);
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
