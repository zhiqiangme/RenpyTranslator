using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RenpyTranslator;

/// <summary>
/// 自定义无边框窗口支持：负责 Windows 11 原生圆角/边框配色、最大化裁切补偿，以及侧边栏折叠状态属性。
/// </summary>
public static class WindowChromeHelper
{
    public static readonly DependencyProperty IsSidebarCollapsedProperty = DependencyProperty.RegisterAttached(
        "IsSidebarCollapsed",
        typeof(bool),
        typeof(WindowChromeHelper),
        new FrameworkPropertyMetadata(false));

    public static bool GetIsSidebarCollapsed(DependencyObject element) => (bool)element.GetValue(IsSidebarCollapsedProperty);
    public static void SetIsSidebarCollapsed(DependencyObject element, bool value) => element.SetValue(IsSidebarCollapsedProperty, value);

    // DWM 属性常量（Windows 10 20H1+ / Windows 11）
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    // 窗口最大化时系统外扩的调整边框像素，用于补偿客户区裁切。
    private const int SmCxSizeFrame = 32;
    private const int SmCxPaddedBorder = 92;
    private const int WmSettingChange = 0x001A;

    // 与 Light.xaml / Dark.xaml 的外框描边保持一致（COLORREF 格式为 0x00BBGGRR）。
    private const int DarkBorderColorRef = 0x00352D2D;  // #2D2D35
    private const int LightBorderColorRef = 0x00DDD5D0; // #D0D5DD

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static void Attach(Window window, FrameworkElement rootFrame, Action? onStateChanged = null)
    {
        window.SourceInitialized += (_, _) =>
        {
            ApplyDwmTheme(window, ThemeManager.IsDark);
            UpdateMaximizedMargin(window, rootFrame);
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook(WndProc);
        };
        window.StateChanged += (_, _) =>
        {
            UpdateMaximizedMargin(window, rootFrame);
            onStateChanged?.Invoke();
        };
    }

    public static void ApplyDwmTheme(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var useDark = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));

        var corner = DwmwcpRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        var border = dark ? DarkBorderColorRef : LightBorderColorRef;
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
    }

    private static void UpdateMaximizedMargin(Window window, FrameworkElement rootFrame)
    {
        if (window.WindowState != WindowState.Maximized)
        {
            rootFrame.Margin = new Thickness(0);
            return;
        }
        // 使用系统非客户区调整边框像素并按当前窗口 DPI 折算为 DIP，兼容高分屏与多显示器。
        var framePx = GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
        var dpi = VisualTreeHelper.GetDpi(window);
        var dx = framePx > 0 ? framePx / Math.Max(dpi.DpiScaleX, 1.0) : 7.0;
        var dy = framePx > 0 ? framePx / Math.Max(dpi.DpiScaleY, 1.0) : 7.0;
        rootFrame.Margin = new Thickness(dx, dy, dx, dy);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Windows 切换浅色/深色模式时会广播 WM_SETTINGCHANGE("ImmersiveColorSet")。
        if (msg == WmSettingChange && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStringUni(lParam);
            if (string.Equals(setting, "ImmersiveColorSet", StringComparison.Ordinal))
                Application.Current?.Dispatcher.BeginInvoke(ThemeManager.RefreshSystemTheme);
        }
        return IntPtr.Zero;
    }
}
