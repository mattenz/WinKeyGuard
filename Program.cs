namespace WinKeyGuard;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\WinKeyGuard.SingleInstance", out bool created);
        if (!created) return;   // already running

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
