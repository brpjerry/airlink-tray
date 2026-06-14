using AirLink.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single-instance guard.
        using var mutex = new Mutex(true, "AirLinkTray_SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
