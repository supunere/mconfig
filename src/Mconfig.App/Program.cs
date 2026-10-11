static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        // 予期しない UI の例外は Startup が記録して概要を示す（最初のウィンドウより前に設定する必要がある）
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Mconfig.App.Composition.Startup.Run(args);
    }
}
