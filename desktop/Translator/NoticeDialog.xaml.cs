using System.Windows;

namespace RenpyTranslator;

// 自定义无边框弹窗：用于汉化结果提示、错误提示与操作确认，外观随深浅色主题实时切换。
public partial class NoticeDialog : Window
{
    private bool accepted;
    internal NoticeDialog()
    {
        InitializeComponent();
        WindowChromeHelper.Attach(this, RootFrame);
    }
    /// <summary>显示结果提示；hintKey 为空表示不可关闭的提示（如报错），否则按“不再提醒”设置决定是否显示；tip 非空时以加粗段落展示使用提醒。</summary>
    public static void Show(Window owner, string title, string headline, string body, string hintKey = "", bool error = false, string tip = "")
    {
        if (hintKey.Length > 0 && Core.HintHidden(hintKey)) return;
        var dialog = new NoticeDialog { Owner = owner, Title = title };
        dialog.DialogTitle.Text = title;
        dialog.Headline.Text = headline;
        dialog.Headline.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error ? "DangerBrush" : "SuccessBrush");
        dialog.Body.Text = body;
        // 提示段落单独加粗渲染；为空时保持折叠，报错弹窗排版不变。
        dialog.Tip.Text = tip.Length > 0 ? "提示：" + tip : "";
        dialog.Tip.Visibility = tip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        dialog.Never.Visibility = hintKey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        dialog.ShowDialog();
        // 勾选后立即保存，用户直接关闭窗口同样生效。
        if (hintKey.Length > 0 && dialog.Never.IsChecked == true) Core.SetHintHidden(hintKey, true);
    }
    /// <summary>显示双按钮确认弹窗，替代系统白底 MessageBox，确保深色模式下界面风格一致。</summary>
    public static bool Ask(Window owner, string title, string body, string confirmText = "确认", bool danger = false)
    {
        var dialog = new NoticeDialog { Owner = owner, Title = title };
        dialog.DialogTitle.Text = title;
        dialog.Headline.Text = title;
        if (danger)
            dialog.Headline.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
        dialog.Body.Text = body;
        dialog.Cancel.Visibility = Visibility.Visible;
        dialog.Cancel.IsCancel = true;
        dialog.Confirm.IsCancel = false;
        dialog.Confirm.Content = confirmText;
        if (danger) dialog.Confirm.Style = (Style)Application.Current.FindResource("DangerButton");
        dialog.ShowDialog();
        return dialog.accepted;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) { accepted = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { accepted = false; Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) { accepted = false; Close(); }
}
