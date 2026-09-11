using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace RenpyTranslator;

public partial class MainWindow : Window
{
    private JsonObject config = Core.Defaults();
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly Dictionary<string, CheckBox> flags = new();
    private string loadedRoot = "";
    private Release? release;
    private bool busy;
    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(Core.Home);
        foreach (var provider in Providers.All) Provider.Items.Add(provider.Name);
        foreach (var (key, label) in new[] { ("batch_size", "每批条数"), ("batch_wait_ms", "批量等待（毫秒）"), ("request_timeout_seconds", "请求超时（秒）"), ("retry_cooldown_seconds", "重试冷却（秒）"), ("temperature", "温度"), ("max_output_tokens", "最大输出 token"), ("system_prompt", "翻译提示词"), ("protected_names", "保护人名（JSON 数组）"), ("skip_patterns", "跳过规则（JSON 数组）") })
        {
            Advanced.Children.Add(new TextBlock { Text = label });
            var box = new TextBox { TextWrapping = TextWrapping.Wrap, MinHeight = key == "system_prompt" ? 100 : 35 };
            fields[key] = box; Advanced.Children.Add(box);
        }
        foreach (var (key, label) in new[] { ("enabled", "启用翻译"), ("thinking_enabled", "启用思考模式"), ("json_response_format", "请求 JSON 格式") })
        { var box = new CheckBox { Content = label, Margin = new Thickness(0, 8, 0, 8) }; flags[key] = box; Advanced.Children.Add(box); }
        var save = new Button { Content = "保存配置" }; save.Click += Save; Advanced.Children.Add(save);
        var defaults = new Button { Content = "恢复翻译参数默认值" }; defaults.Click += (_, _) => { var basic = Core.Defaults(); foreach (var key in fields.Keys.Concat(flags.Keys)) config[key] = basic[key]?.DeepClone(); ShowConfig(); }; Advanced.Children.Add(defaults);
        try { var state = Path.Combine(Core.Home, "games.json"); if (File.Exists(state)) foreach (var item in JsonNode.Parse(File.ReadAllText(state))!.AsArray()) Games.Items.Add(item!.GetValue<string>()); } catch { Log("历史目录读取失败，可重新添加。"); }
        ResourceVersion.Text = "内置资源 " + File.ReadAllText(Path.Combine(Core.Resources, "version.txt")).Trim();
        ManagerVersion.Text = "管理器 " + Core.ManagerVersion + " · Windows x64";
        ShowConfig();
        Closing += (_, e) => { if (busy) { e.Cancel = true; StatusText.Text = "请等待当前操作完成。"; } };
    }
    private string Root() => Core.Game(Games.Text);
    private void Log(string message)
    {
        // 日志仅记录操作结果，不记录请求体、响应正文或密钥。
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        StatusText.Text = message; LogBox.AppendText(line + Environment.NewLine); LogBox.ScrollToEnd();
    }
    private async Task Run(Func<Task> work)
    {
        if (busy) return; busy = true; Pages.IsEnabled = false; Progress.Visibility = Visibility.Visible;
        try { await work(); }
        catch (Exception ex) { var message = ex is HttpRequestException ? "网络请求失败，请检查网络与服务地址。" : ex is TaskCanceledException ? "请求超时，请稍后重试。" : ex.Message; Log(message); MessageBox.Show(this, message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; Pages.IsEnabled = true; Progress.Visibility = Visibility.Collapsed; }
    }
    private void ShowConfig()
    {
        Provider.SelectedIndex = Array.FindIndex(Providers.All, p => p.Url == Core.ReadString(config, "base_url"));
        BaseUrl.Text = Core.ReadString(config, "base_url"); Model.Text = Core.ReadString(config, "model"); ApiKey.Clear(); ClearKey.IsChecked = false;
        KeyStatus.Text = string.IsNullOrEmpty(Core.ReadString(config, "api_key_encrypted")) ? "尚未保存密钥" : "已有加密密钥；留空即可保留";
        foreach (var (key, box) in fields) box.Text = config[key] is JsonArray ? config[key]!.ToJsonString() : config[key]?.ToString() ?? "";
        foreach (var (key, box) in flags) box.IsChecked = config[key]?.GetValue<bool>() ?? false;
    }
    private JsonObject Form()
    {
        if (loadedRoot != Root()) throw new IOException("请先点击读取 / 检查状态，以免将其他游戏的配置写入此目录。");
        var next = config.DeepClone().AsObject();
        if (!Uri.TryCreate(BaseUrl.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new IOException("API 地址需使用 HTTPS；本机服务可使用 HTTP。地址不能包含账号、查询参数或片段。");
        if (string.IsNullOrWhiteSpace(Model.Text)) throw new IOException("请填写模型名称。");
        next["base_url"] = BaseUrl.Text.Trim().TrimEnd('/'); next["model"] = Model.Text.Trim();
        foreach (var (key, box) in fields)
        {
            if (key == "system_prompt") { if (string.IsNullOrWhiteSpace(box.Text)) throw new IOException("提示词不能为空。"); next[key] = box.Text; }
            else if (key is "protected_names" or "skip_patterns")
            {
                var array = JsonNode.Parse(box.Text)?.AsArray() ?? throw new IOException("请输入 JSON 数组。");
                foreach (var item in array) { var value = item!.GetValue<string>(); if (key == "skip_patterns") _ = new System.Text.RegularExpressions.Regex(value); }
                next[key] = array;
            }
            else if (key == "temperature") { if (!double.TryParse(box.Text, System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0 || number > 2) throw new IOException("温度应在 0 到 2 之间。"); next[key] = number; }
            else { if (!int.TryParse(box.Text, out var number) || number < 1 || number > 100000) throw new IOException("数值参数应为 1 到 100000 的整数。"); next[key] = number; }
        }
        foreach (var (key, box) in flags) next[key] = box.IsChecked == true;
        Core.NormalizeKey(next, ApiKey.Password, ClearKey.IsChecked == true); return next;
    }
    private async Task LoadGame()
    {
        var root = Root(); var result = await Task.Run(() => (Core.Config(root), Core.Status(root)));
        config = result.Item1; loadedRoot = root; GameStatus.Text = result.Item2; ShowConfig();
        var manifest = Path.Combine(Core.Data(root), "installation.json");
        Pack.SelectedIndex = File.Exists(manifest) && Core.ReadString(Core.ReadJson(manifest), "pack") == "camp-buddy-scoutmaster" ? 1 : 0;
        var font = Core.ReadString(config, "font"); FontChoice.SelectedIndex = font.Contains("msyh") ? 1 : font.Contains("simsun") ? 2 : 0;
        if (!Games.Items.Contains(root)) Games.Items.Add(root);
        Core.AtomicWrite(Path.Combine(Core.Home, "games.json"), new JsonArray(Games.Items.Cast<string>().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()).ToJsonString());
        Log(result.Item2);
    }
    private async void Browse(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "选择 Ren'Py 游戏根目录" }; if (dialog.ShowDialog(this) == true) { Games.Text = dialog.FolderName; await Run(LoadGame); } }
    private void GameSelected(object sender, SelectionChangedEventArgs e) { if (GameStatus != null) GameStatus.Text = "目录已选择，请点击读取 / 检查状态。"; }
    private async void Reload(object sender, RoutedEventArgs e) => await Run(LoadGame);
    private void ProviderChanged(object sender, SelectionChangedEventArgs e) { if (Provider.SelectedIndex < 0) return; var p = Providers.All[Provider.SelectedIndex]; BaseUrl.Text = p.Url; Model.Text = p.Model; }
    private async void Install(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var root = Root(); var next = Form(); bool bundled = Pack.SelectedIndex == 1;
        if (bundled && MessageBox.Show(this, "确认所选游戏是 Camp Buddy Scoutmaster Season？专属译文将覆盖已有预译文，并保存备份。", "安装专属译文", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var font = FontChoice.SelectedIndex == 0 ? "HarmonyOS" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), FontChoice.SelectedIndex == 1 ? "msyh.ttc" : "simsun.ttc");
        Log("正在校验资源并安装…"); var count = await Task.Run(() => Core.Install(root, next, bundled, font)); await LoadGame(); Log($"安装完成，导入 {count} 条译文。请重新启动游戏。");
    });
    private async void Uninstall(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var root = Root(); var clear = RemoveData.IsChecked == true;
        if (MessageBox.Show(this, clear ? "卸载汉化并清理配置和缓存？操作前会备份。" : "卸载汉化？配置和缓存将保留。", "卸载汉化", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Task.Run(() => Core.Uninstall(root, clear)); await LoadGame(); Log("汉化已卸载，原始游戏文件和存档未修改。");
    });
    private async void Save(object sender, RoutedEventArgs e) => await Run(async () => { var root = Root(); var next = Form(); await Task.Run(() => Core.SaveConfig(root, next)); config = next; ShowConfig(); Log("配置已保存，请重新启动游戏生效。"); });
    private async void TestApi(object sender, RoutedEventArgs e) => await Run(async () => { var next = Form(); Log("正在发送测试请求…"); await Api.Test(next); Log("连接成功，翻译响应格式有效。"); });
    private async void ValidateTranslations(object sender, RoutedEventArgs e) => await Run(async () => { var result = await Task.Run(() => Core.Merge(Path.Combine(Core.Resources, "translations"))); Log($"校验通过：{result.Count} 条译文，无重复原文。"); });
    private async void ExportCache(object sender, RoutedEventArgs e) => await Run(() => { var path = Path.Combine(Core.Data(Root()), "cache.jsonl"); if (!File.Exists(path)) throw new IOException("当前没有缓存。"); var dialog = new SaveFileDialog { FileName = "cache-export.jsonl", Filter = "JSONL|*.jsonl" }; if (dialog.ShowDialog(this) == true) { File.Copy(path, dialog.FileName, true); Log("缓存已导出。"); } return Task.CompletedTask; });
    private async void ClearCache(object sender, RoutedEventArgs e) => await Run(async () => { var root = Root(); if (MessageBox.Show(this, "备份并清空运行时缓存？已有预译文仍会保留。", "清空缓存", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; await Task.Run(() => Core.Transaction(root, ["live_translator/cache.jsonl"], () => Core.AtomicWrite(Path.Combine(Core.Data(root), "cache.jsonl"), ""))); Log("缓存已备份并清空。"); });
    private void OpenDirectory(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private async void OpenGame(object sender, RoutedEventArgs e) => await Run(() => { OpenDirectory(Root()); return Task.CompletedTask; });
    private async void OpenData(object sender, RoutedEventArgs e) => await Run(() => { OpenDirectory(Core.Data(Root())); return Task.CompletedTask; });
    private async void OpenBackups(object sender, RoutedEventArgs e) => await Run(() => { OpenDirectory(Path.Combine(Core.Home, "backups")); return Task.CompletedTask; });
    private async void ExportLog(object sender, RoutedEventArgs e) => await Run(() => { var dialog = new SaveFileDialog { FileName = "translator.log", Filter = "日志|*.log" }; if (dialog.ShowDialog(this) == true) Core.AtomicWrite(dialog.FileName, LogBox.Text); return Task.CompletedTask; });
    private async void CheckUpdate(object sender, RoutedEventArgs e) => await Run(async () => { release = await Updates.Check(); ReleaseNotes.Text = release.Notes; UpdateButton.IsEnabled = release.Available; Log(release.Available ? "发现桌面更新。" : "没有可安装的桌面更新。"); });
    private async void ApplyUpdate(object sender, RoutedEventArgs e) => await Run(async () => { if (release is null) return; Log("正在下载并校验更新…"); await Updates.Stage(release); busy = false; Application.Current.Shutdown(); });
    public void SaveSnapshot()
    {
        for (int i = 0; i < Pages.Items.Count; i++)
        {
            Pages.SelectedIndex = i; UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(Core.Home, $"preview-{i}.png")); encoder.Save(stream);
        }
        Application.Current.Shutdown();
    }
}
public record Provider(string Name, string Url, string Model);
public static class Providers
{
    public static readonly Provider[] All = [new("自定义", "", ""), new("DeepSeek", "https://api.deepseek.com", "deepseek-flash"), new("OpenAI", "https://api.openai.com/v1", "gpt-5.6-luna"), new("小米 MiMo", "https://api.xiaomimimo.com/v1", "mimo-v2.5"), new("MiniMax", "https://api.minimax.chat/v1", "minimax-m3"), new("腾讯混元", "https://api.hunyuan.cloud.tencent.com/v1", "hy3"), new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.6-flash"), new("阿里通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-flash"), new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.7-flash"), new("Kimi", "https://api.moonshot.cn/v1", "kimi-k2.6"), new("字节豆包", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-2.0-lite")];
}
public static class Api
{
    public static async Task Test(JsonObject config)
    {
        var secret = Core.ReadString(config, "api_key_encrypted"); if (secret.Length == 0) throw new IOException("请填写 API Key。");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(Math.Clamp(config["request_timeout_seconds"]!.GetValue<int>(), 1, 300)) };
        var url = Core.ReadString(config, "base_url").TrimEnd('/'); if (!url.EndsWith("/chat/completions")) url += "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret.Unprotect(secret));
        var payload = new JsonObject { ["model"] = Core.ReadString(config, "model"), ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = Core.ReadString(config, "system_prompt") }, new JsonObject { ["role"] = "user", ["content"] = "{\"texts\":[\"Hello\"]}" }), ["temperature"] = config["temperature"]!.DeepClone(), ["max_tokens"] = config["max_output_tokens"]!.DeepClone(), ["thinking"] = new JsonObject { ["type"] = config["thinking_enabled"]!.GetValue<bool>() ? "enabled" : "disabled" } };
        if (config["json_response_format"]!.GetValue<bool>()) payload["response_format"] = new JsonObject { ["type"] = "json_object" };
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new IOException($"API 返回 HTTP {(int)response.StatusCode}。401/403：鉴权或权限；404：地址或模型；429：配额或限流。请核对服务商控制台。");
        try
        {
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["choices"]![0]!["message"]!["content"]!;
            var content = node is JsonArray blocks ? string.Concat(blocks.Select(x => x?["text"]?.GetValue<string>() ?? "")) : node.GetValue<string>();
            var start = content.IndexOf('{'); var end = content.LastIndexOf('}');
            var translations = JsonNode.Parse(content[start..(end + 1)])!["translations"]!.AsArray();
            if (translations.Count != 1 || string.IsNullOrWhiteSpace(translations[0]!.GetValue<string>())) throw new FormatException();
        }
        catch { throw new IOException("连接成功，但响应不是模组要求的 translations JSON 数组。"); }
    }
}
