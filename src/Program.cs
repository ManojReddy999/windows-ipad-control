namespace IpadBridgeBle;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "IpadBridgeBleSingleton", out bool createdNew);
        if (!createdNew) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new TrayApp());
    }
}
