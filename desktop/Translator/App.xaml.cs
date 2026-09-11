using System.Windows;
namespace RenpyTranslator;

public partial class App : Application
{
    private System.Threading.Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test")) { Shutdown(SelfTest.Run()); return; }
        instance = new System.Threading.Mutex(true, "Local\\RenpyTranslator.Desktop", out var created);
        if (!created) { MessageBox.Show("汉化管理器已经运行，请使用已有窗口。"); Shutdown(); return; }
        var window = new MainWindow(); MainWindow = window; window.Show();
        if (e.Args.Contains("--snapshot")) window.Loaded += (_, _) => window.SaveSnapshot();
    }
}
