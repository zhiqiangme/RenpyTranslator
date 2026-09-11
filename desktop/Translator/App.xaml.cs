using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace RenpyTranslator;

public partial class App : Application
{
    private System.Threading.Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // ComboBox 聚焦后滚轮会直接切换选中项，滚动页面时极易误改服务商、游戏目录等选项。
        // 下拉未展开时拦截滚轮并转发给外层 ScrollViewer；展开后保留原生滚轮浏览。
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(ForwardComboBoxWheel));
        // 未处理异常先落盘再提示，避免进程静默退出后无从排查。
        DispatcherUnhandledException += (_, args) =>
        {
            var log = Path.Combine(Core.Home, "error.log");
            try { Directory.CreateDirectory(Core.Home); File.WriteAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{args.Exception}"); } catch { }
            MessageBox.Show($"程序遇到错误并已停止。详细信息见：{log}{Environment.NewLine}{Environment.NewLine}{args.Exception.Message}", "Ren'Py 汉化管理器", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Environment.Exit(1);
        };
        if (e.Args.Contains("--self-test")) { Shutdown(SelfTest.Run()); return; }
        instance = new System.Threading.Mutex(true, "Local\\RenpyTranslator.Desktop", out var created);
        if (!created) { MessageBox.Show("汉化管理器已经运行，请使用已有窗口。"); Shutdown(); return; }
        var window = new MainWindow(); MainWindow = window; window.Show();
        // 截图模式：--snapshot [页索引]；可加 --game <目录> 先载入一个游戏，核对真实数据下的界面。
        if (e.Args.Contains("--snapshot"))
        {
            var game = ValueOf(e.Args, "--game");
            window.ContentRendered += async (_, _) =>
            {
                if (game is not null) await window.PrimeAsync(game);
                window.SaveSnapshot(ValueOf(e.Args, "--snapshot"));
            };
        }
    }
    /// <summary>吞掉未展开 ComboBox 的滚轮事件，并转发给最近的 ScrollViewer 以维持页面滚动。</summary>
    private static void ForwardComboBoxWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not ComboBox { IsDropDownOpen: false } box) return;
        args.Handled = true;
        for (var parent = VisualTreeHelper.GetParent(box); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer)
            {
                viewer.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
                return;
            }
    }
    /// <summary>取 --名字 紧跟的取值；不存在或下一个仍是开关时返回 null。</summary>
    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) return null;
        var value = args[index + 1];
        return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }
}
