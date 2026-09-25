using System.Windows;
using System.Windows.Media;

namespace RenpyTranslator;

// 汉化结果提示弹窗。成功提示可以“不再提醒”，失败提示不提供该勾选框，保证报错始终弹出。
public partial class NoticeDialog : Window
{
    internal NoticeDialog() => InitializeComponent();
    /// <summary>显示结果提示；hintKey 为空表示不可关闭的提示（如报错），否则按“不再提醒”设置决定是否显示。</summary>
    public static void Show(Window owner, string title, string headline, string body, string hintKey = "", bool error = false)
    {
        if (hintKey.Length > 0 && Core.HintHidden(hintKey)) return;
        var dialog = new NoticeDialog { Owner = owner, Title = title };
        dialog.Headline.Text = headline;
        dialog.Headline.Foreground = (Brush)Application.Current.FindResource(error ? "DangerBrush" : "SuccessBrush");
        dialog.Body.Text = body;
        dialog.Never.Visibility = hintKey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        dialog.ShowDialog();
        // 勾选后立即保存，用户直接关闭窗口同样生效。
        if (hintKey.Length > 0 && dialog.Never.IsChecked == true) Core.SetHintHidden(hintKey, true);
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) => Close();
}
