using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RenpyTranslator;

public partial class MainWindow : Window
{
    private JsonObject config = Core.Defaults();
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly Dictionary<string, CheckBox> flags = new();
    private ProviderCatalog catalog = Providers.Load();
    private bool suppressProviderChange;
    private bool suppressThemeChange;
    private bool liveModelsLoaded;
    private string loadedRoot = "";
    private Release? release;
    private bool busy;
    public bool SuppressBackgroundRefresh { get; set; }
    public MainWindow()
    {
        InitializeComponent();
        WindowChromeHelper.Attach(this, RootFrame, UpdateWindowStateChrome);
        ThemeManager.ThemeChanged += SyncThemeUi;
        SyncThemeUi();
        UpdateWindowStateChrome();
        Directory.CreateDirectory(Core.Home);
        Updates.PruneUpdates(Path.Combine(Core.Home, "updates"));
        ApplyCatalog(catalog, preserveInputs: false);
        // 参数控件统一在 MainWindow.xaml 中声明（便于套用主题），此处只建立「配置键 → 控件」映射。
        foreach (var (key, box) in new (string Key, TextBox Box)[]
        {
            ("batch_size", FieldBatchSize), ("batch_wait_ms", FieldBatchWaitMs), ("request_timeout_seconds", FieldTimeout),
            ("retry_cooldown_seconds", FieldRetryCooldown), ("temperature", FieldTemperature), ("max_output_tokens", FieldMaxTokens),
            ("system_prompt", FieldSystemPrompt), ("protected_names", FieldProtectedNames), ("skip_patterns", FieldSkipPatterns)
        }) fields[key] = box;
        foreach (var (key, box) in new (string Key, CheckBox Box)[]
        {
            ("enabled", FlagEnabled), ("thinking_enabled", FlagThinking), ("json_response_format", FlagJsonFormat)
        }) flags[key] = box;
        try { var state = Path.Combine(Core.Home, "games.json"); if (File.Exists(state)) foreach (var item in JsonNode.Parse(File.ReadAllText(state))!.AsArray()) Games.Items.Add(item!.GetValue<string>()); } catch { Log("历史目录读取失败，可重新添加。"); }
        ResourceVersion.Text = "内置资源 " + File.ReadAllText(Path.Combine(Core.Resources, "version.txt")).Trim();
        ManagerVersion.Text = "管理器 " + Core.ManagerVersion + " · Windows x64";
        ShowConfig();
        // 提示开关只影响成功提示；报错提示不受它控制。
        NoticeInstallSuccess.IsChecked = !Core.HintHidden(Core.InstallSuccessHint);
        ContentRendered += async (_, _) => await AutoRefreshCatalogAsync();
        Closing += (_, e) => { if (busy) { e.Cancel = true; StatusText.Text = "请等待当前操作完成。"; } };
        Closed += (_, _) => ThemeManager.ThemeChanged -= SyncThemeUi;
    }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ToggleMaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        var collapsed = !WindowChromeHelper.GetIsSidebarCollapsed(Pages);
        WindowChromeHelper.SetIsSidebarCollapsed(Pages, collapsed);
        SidebarToggleButton.ToolTip = collapsed ? "展开侧边栏" : "收起侧边栏";
    }
    private void UpdateWindowStateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = (Geometry)Application.Current.FindResource(maximized ? "IconWinRestore" : "IconWinMaximize");
        MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";
    }
    private void OpenThemeMenu(object sender, RoutedEventArgs e)
    {
        ThemeContextMenu.PlacementTarget = ThemeMenuButton;
        ThemeContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ThemeContextMenu.IsOpen = true;
    }
    private void ThemeMenuSelected(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string tag)
        {
            var mode = ThemeManager.ParseMode(tag);
            ThemeManager.SetMode(mode, persist: true);
            Log($"外观主题已切换为：{ThemeManager.DisplayName(mode)}。");
        }
    }
    private void ThemeChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressThemeChange || !IsLoaded || ThemeChoice.SelectedIndex < 0) return;
        var mode = ThemeChoice.SelectedIndex switch { 1 => AppThemeMode.Light, 2 => AppThemeMode.Dark, _ => AppThemeMode.System };
        ThemeManager.SetMode(mode, persist: true);
        Log($"外观主题已切换为：{ThemeManager.DisplayName(mode)}。");
    }
    private void SyncThemeUi()
    {
        var mode = ThemeManager.CurrentMode;
        var iconKey = mode switch
        {
            AppThemeMode.Light => "IconThemeLight",
            AppThemeMode.Dark => "IconThemeDark",
            _ => "IconThemeSystem"
        };
        ThemeButtonIcon.Data = (Geometry)Application.Current.FindResource(iconKey);
        ThemeButtonLabel.Text = ThemeManager.DisplayName(mode);
        MenuThemeSystem.IsChecked = mode == AppThemeMode.System;
        MenuThemeLight.IsChecked = mode == AppThemeMode.Light;
        MenuThemeDark.IsChecked = mode == AppThemeMode.Dark;

        suppressThemeChange = true;
        try
        {
            ThemeChoice.SelectedIndex = mode switch { AppThemeMode.Light => 1, AppThemeMode.Dark => 2, _ => 0 };
        }
        finally { suppressThemeChange = false; }

        var activeLabel = ThemeManager.IsDark ? "深色" : "浅色";
        ThemeStatus.Text = mode == AppThemeMode.System
            ? $"当前跟随 Windows 系统（已应用{activeLabel}模式），系统切换时将自动更新；也可点击窗口右上角快速切换。"
            : $"当前固定使用{activeLabel}模式；切换为「跟随系统」可自动适配 Windows 浅色 / 深色设置。";
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
        catch (Exception ex) { var message = Core.Explain(ex); Log(message); NoticeDialog.Show(this, "操作未完成", "操作未完成", message, error: true); }
        finally { busy = false; Pages.IsEnabled = true; Progress.Visibility = Visibility.Collapsed; }
    }
    private void NoticeChanged(object sender, RoutedEventArgs e)
    {
        // 初始化赋值也会触发该事件，界面加载完成后才处理用户操作。
        if (!IsLoaded) return;
        Core.SetHintHidden(Core.InstallSuccessHint, NoticeInstallSuccess.IsChecked != true);
        Log(NoticeInstallSuccess.IsChecked == true ? "汉化成功提示已开启。" : "汉化成功提示已关闭；报错提示仍会弹出。");
    }
    private void ApplyCatalog(ProviderCatalog nextCatalog, bool preserveInputs)
    {
        catalog = nextCatalog;
        var currentUrl = preserveInputs ? BaseUrl.Text.Trim().TrimEnd('/') : "";
        var currentModel = preserveInputs ? Model.Text : "";
        suppressProviderChange = true;
        try
        {
            Provider.Items.Clear();
            foreach (var provider in catalog.Items) Provider.Items.Add(provider.Name);
            CatalogStatus.Text = $"模型清单：{catalog.UpdatedAt}（{(catalog.FromCache ? "云端缓存" : "内置")}） · 预设仅作填写参考，实际可用地址与模型以服务商账户为准。";
            if (preserveInputs)
            {
                var matched = FindProviderIndex(currentUrl);
                Provider.SelectedIndex = matched;
                if (!liveModelsLoaded)
                {
                    var options = matched >= 0 ? catalog.Items[matched].Models : [];
                    SetModelChoices(options, currentModel);
                }
            }
        }
        finally { suppressProviderChange = false; }
    }
    private int FindProviderIndex(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        if (normalized.Length == 0) return -1;
        for (var i = 0; i < catalog.Items.Count; i++)
            if (catalog.Items[i].Url.Length > 0 && catalog.Items[i].Url.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
    private void SetModelChoices(IEnumerable<ModelOption> options, string text)
    {
        Model.SelectedIndex = -1;
        Model.Items.Clear();
        foreach (var option in options) Model.Items.Add(option);
        Model.Text = text;
    }
    private async Task AutoRefreshCatalogAsync()
    {
        if (SuppressBackgroundRefresh) return;
        try
        {
            var result = await Providers.RefreshAsync(force: false);
            if (result.Updated && result.Catalog is not null)
            {
                ApplyCatalog(result.Catalog, preserveInputs: true);
                Log(result.Message);
            }
        }
        catch (Exception) { } // 后台静默刷新失败不打扰用户，继续使用当前清单。
    }
    private void ShowConfig()
    {
        var baseUrl = Core.ReadString(config, "base_url");
        var model = Core.ReadString(config, "model");
        liveModelsLoaded = false;
        ModelFetchStatus.Visibility = Visibility.Collapsed;
        suppressProviderChange = true;
        try
        {
            var matched = FindProviderIndex(baseUrl);
            Provider.SelectedIndex = matched;
            BaseUrl.Text = baseUrl;
            SetModelChoices(matched >= 0 ? catalog.Items[matched].Models : [], model);
        }
        finally { suppressProviderChange = false; }
        ApiKey.Clear(); ClearKey.IsChecked = false;
        KeyStatus.Text = string.IsNullOrEmpty(Core.ReadString(config, "api_key_encrypted")) ? "尚未保存密钥" : "已有加密密钥；留空即可保留";
        ShowParameters(config);
    }
    // 只刷新高级参数编辑区；恢复默认值也走这里，不能顺带清掉正在填写的地址、模型和密钥。
    private void ShowParameters(JsonObject source)
    {
        foreach (var (key, box) in fields) box.Text = source[key] is JsonArray ? source[key]!.ToJsonString() : source[key]?.ToString() ?? "";
        foreach (var (key, box) in flags) box.IsChecked = Core.ReadBool(source, key);
    }
    private JsonObject Form()
    {
        if (loadedRoot != Root()) throw new UserError("请先点击读取 / 检查状态，以免将其他游戏的配置写入此目录。");
        var next = config.DeepClone().AsObject();
        var baseUrl = Api.ValidateApiBaseUrl(BaseUrl.Text);
        if (string.IsNullOrWhiteSpace(Model.Text)) throw new UserError("请填写模型名称。");
        next["base_url"] = baseUrl; next["model"] = Model.Text.Trim();
        foreach (var (key, box) in fields)
        {
            if (key == "system_prompt") { if (string.IsNullOrWhiteSpace(box.Text)) throw new UserError("提示词不能为空。"); next[key] = box.Text; }
            else if (key is "protected_names" or "skip_patterns")
            {
                next[key] = Core.StringArray(box.Text, key);
            }
            else if (key == "temperature") { if (!double.TryParse(box.Text, System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0 || number > 2) throw new UserError("温度应在 0 到 2 之间。"); next[key] = number; }
            else { if (!int.TryParse(box.Text, out var number) || number < 1 || number > 100000) throw new UserError("数值参数应为 1 到 100000 的整数。"); next[key] = number; }
        }
        foreach (var (key, box) in flags) next[key] = box.IsChecked == true;
        Core.NormalizeKey(next, ApiKey.Password, ClearKey.IsChecked == true); return next;
    }
    private async Task LoadGame()
    {
        var root = Root(); var result = await Task.Run(() => (Core.Config(root), Core.InspectInstallation(root)));
        // 配置展示失败时界面可能残留上一个游戏的值，此时不能允许保存，读取完成后才记录目录。
        loadedRoot = ""; config = result.Item1; ShowConfig(); loadedRoot = root; GameStatus.Text = result.Item2.Status;
        CustomPackDirectory.Text = result.Item2.CustomDirectory;
        Pack.SelectedIndex = result.Item2.Bundled ? 1 : result.Item2.CustomPack ? 2 : 0;
        var font = Core.ReadString(config, "font"); var preset = Core.FontPreset(font);
        if (FontChoice.Items.Count > Core.CustomFontPreset) FontChoice.Items.RemoveAt(Core.CustomFontPreset);
        if (preset == Core.CustomFontPreset) FontChoice.Items.Add(new ComboBoxItem { Content = "保留自定义字体：" + font, ToolTip = font });
        FontChoice.SelectedIndex = preset;
        if (!Games.Items.Contains(root)) Games.Items.Add(root);
        Core.AtomicWrite(Path.Combine(Core.Home, "games.json"), new JsonArray(Games.Items.Cast<string>().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()).ToJsonString());
        Log(result.Item2.Status);
    }
    private async void Browse(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "选择 Ren'Py 游戏根目录" }; if (dialog.ShowDialog(this) == true) { Games.Text = dialog.FolderName; await Run(LoadGame); } }
    private void GameSelected(object sender, SelectionChangedEventArgs e) { if (GameStatus != null) GameStatus.Text = "目录已选择，请点击读取 / 检查状态。"; }
    private async void Reload(object sender, RoutedEventArgs e) => await Run(LoadGame);
    private void PackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomPackPanel is not null) CustomPackPanel.Visibility = Pack.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void BrowseCustomPack(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var dialog = new OpenFolderDialog { Title = "选择自定义汉化包文件夹（包含 JSONL，可含子目录）" };
        if (dialog.ShowDialog(this) == true) CustomPackDirectory.Text = Core.TranslationDirectory(dialog.FolderName);
        return Task.CompletedTask;
    });
    // “自定义”没有预设地址，选中时保留已填写的内容供用户修改。
    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressProviderChange || Provider.SelectedIndex < 0 || Provider.SelectedIndex >= catalog.Items.Count) return;
        var p = catalog.Items[Provider.SelectedIndex];
        liveModelsLoaded = false;
        ModelFetchStatus.Visibility = Visibility.Collapsed;
        if (p.Url.Length == 0) { SetModelChoices([], Model.Text); return; }
        BaseUrl.Text = p.Url;
        SetModelChoices(p.Models, p.Model);
    }
    private async void RefreshCatalog(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        Log("正在刷新远程模型清单…");
        var result = await Providers.RefreshAsync(force: true);
        if (result.Catalog is not null) ApplyCatalog(result.Catalog, preserveInputs: true);
        Log(result.Message);
    });
    private async void FetchModels(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var baseUrl = Api.ValidateApiBaseUrl(BaseUrl.Text);
        var apiKey = ResolveApiKeyForUrl(baseUrl);
        int timeout = int.TryParse(FieldTimeout.Text, out var parsed) ? parsed : 20;
        Log("正在从服务商获取可用模型列表…");
        var fetched = await Api.FetchModelsAsync(baseUrl, apiKey, timeout);
        var matched = FindProviderIndex(baseUrl);
        var recommended = matched >= 0
            ? catalog.Items[matched].Models.ToDictionary(m => m.Id, m => m.Label, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        // 命中的推荐模型优先排在列表前列，便于从长列表中快速定位常用翻译模型。
        var options = fetched
            .OrderByDescending(id => recommended.ContainsKey(id))
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new ModelOption(id, recommended.TryGetValue(id, out var label) ? label : ""))
            .ToArray();
        var currentText = Model.Text.Trim();
        SetModelChoices(options, currentText.Length > 0 ? currentText : fetched[0]);
        liveModelsLoaded = true;
        ModelFetchStatus.Text = $"已从服务商获取 {fetched.Count} 个可用模型，可在下拉框中选择或继续手动输入。";
        ModelFetchStatus.Visibility = Visibility.Visible;
        Model.IsDropDownOpen = true;
        Log($"已从服务商获取 {fetched.Count} 个可用模型。");
    });
    // 仅当新地址与已保存配置一致时才复用加密密钥，防止把旧服务商密钥误发给新填写的地址。
    private string ResolveApiKeyForUrl(string normalizedBaseUrl)
    {
        var entered = ApiKey.Password.Trim();
        if (entered.Length > 0) return entered;
        if (ClearKey.IsChecked == true) return "";
        var encrypted = Core.ReadString(config, "api_key_encrypted");
        if (encrypted.Length == 0 || encrypted.Contains("请勿")) return "";
        var savedUrl = Core.ReadString(config, "base_url").Trim().TrimEnd('/');
        if (savedUrl.Equals(normalizedBaseUrl, StringComparison.OrdinalIgnoreCase))
            return Secret.Unprotect(encrypted);
        if (!new Uri(normalizedBaseUrl).IsLoopback)
            throw new UserError("API 地址已变更，请先填写该服务商的 API Key（避免向新地址发送已保存的旧密钥）。");
        return "";
    }
    private async void Install(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var root = Root(); var next = Form(); bool bundled = Pack.SelectedIndex == 1;
        var customDirectory = Pack.SelectedIndex == 2 ? Core.TranslationDirectory(CustomPackDirectory.Text) : null;
        if (bundled && !NoticeDialog.Ask(this, "安装专属译文", "确认所选游戏是 Camp Buddy Scoutmaster Season？专属译文将覆盖已有预译文，并保存备份。", "继续安装")) return;
        if (customDirectory is not null && !NoticeDialog.Ask(this, "安装自定义汉化包", "将导入所选文件夹及子目录中的全部 JSONL，替换游戏已有预译文并保存备份。确认这些译文适用于当前游戏？\n\n" + customDirectory, "继续安装")) return;
        // 自定义项沿用已保存的字体路径；系统预设交给 Core 解析，预设 0 优先雅黑并在缺失时回退。
        var font = FontChoice.SelectedIndex == Core.CustomFontPreset
            ? Core.ReadString(next, "font")
            : Core.SystemFont(FontChoice.SelectedIndex);
        Log("正在校验资源并安装…");
        int count;
        // 汉化结果必须明确告知用户；失败在此处理，避免 Run 再弹一次“操作未完成”。
        try { count = await Task.Run(() => Core.Install(root, next, bundled, font, customDirectory)); }
        catch (Exception error)
        {
            var reason = Core.Explain(error);
            Log("汉化失败：" + reason);
            // 报错提示不提供“不再提醒”，必须让用户看到。
            NoticeDialog.Show(this, "汉化失败", "汉化失败", reason + "\n\n写入前已备份，失败时会自动恢复原文件；可修正后重试。", error: true);
            return;
        }
        await LoadGame();
        var imported = count > 0 ? $"已导入 {count} 条译文。" : "模组文件已更新。";
        Log("汉化成功。" + imported);
        NoticeDialog.Show(this, "汉化成功", "汉化成功！", $"{imported}\n请重新启动游戏后生效。", Core.InstallSuccessHint, tip: "游戏运行时无需打开本管理器，它只在安装、升级或修复汉化时使用。");
    });
    private async void Uninstall(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var root = Root(); var clear = RemoveData.IsChecked == true;
        if (!NoticeDialog.Ask(this, "卸载汉化", clear ? "卸载汉化并删除 game/live_translator 内的全部文件？包括配置、缓存、译文、字体及自行存放的文件。操作前会备份。" : "卸载汉化？配置和缓存将保留。", "确认卸载", danger: true)) return;
        await Task.Run(() => Core.Uninstall(root, clear)); await LoadGame(); Log("汉化已卸载，原始游戏文件和存档未修改。");
    });
    private async void Save(object sender, RoutedEventArgs e) => await Run(async () => { var root = Root(); var next = Form(); await Task.Run(() => Core.SaveConfig(root, next)); config = next; ShowConfig(); Log("配置已保存，请重新启动游戏生效。"); });
    // 恢复默认只改动编辑区，需再点保存才写入游戏，避免误覆盖用户配置。
    private void RestoreDefaults(object sender, RoutedEventArgs e)
    {
        var basic = Core.Defaults();
        // 保护人名随资源包：专属译文恢复内置完整名单，其他模式恢复为空名单。
        if (Pack.SelectedIndex == 1)
            basic["protected_names"] = Core.ReadJson(Path.Combine(Core.Resources, "config.default.json"))["protected_names"]!.DeepClone();
        ShowParameters(basic);
        Log("已载入默认参数，点保存后写入游戏目录。");
    }
    private async void TestApi(object sender, RoutedEventArgs e) => await Run(async () => { if (!NoticeDialog.Ask(this, "测试连接", "将向所填接口发送一条 Hello 翻译请求，可能产生少量费用，是否继续？", "开始测试")) return; var next = Form(); Log("正在发送测试请求…"); await Api.Test(next); Log("连接成功，翻译响应格式有效。"); });
    private async void ValidateTranslations(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        bool custom = Pack.SelectedIndex == 2;
        var directory = custom ? Core.TranslationDirectory(CustomPackDirectory.Text) : Core.BundledTranslations;
        var result = await Task.Run(() => Core.Merge(directory, custom));
        Log($"{(custom ? "自定义汉化包" : "内置译文")}校验通过：{result.Count} 条译文，无重复原文。");
    });
    private async void ExportCache(object sender, RoutedEventArgs e) => await Run(() => { var path = Path.Combine(Core.Data(Root()), "cache.jsonl"); if (!File.Exists(path)) throw new UserError("当前没有缓存。"); var dialog = new SaveFileDialog { FileName = "cache-export.jsonl", Filter = "JSONL|*.jsonl" }; if (dialog.ShowDialog(this) == true) { File.Copy(path, dialog.FileName, true); Log("缓存已导出。"); } return Task.CompletedTask; });
    private async void ClearCache(object sender, RoutedEventArgs e) => await Run(async () => { var root = Root(); if (!NoticeDialog.Ask(this, "清空缓存", "备份并清空运行时缓存？已有预译文仍会保留。", "确认清空", danger: true)) return; await Task.Run(() => Core.Transaction(root, ["live_translator/cache.jsonl"], () => Core.AtomicWrite(Path.Combine(Core.Data(root), "cache.jsonl"), ""))); Log("缓存已备份并清空。"); });
    private void OpenDirectory(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private async void OpenGame(object sender, RoutedEventArgs e) => await Run(() => { OpenDirectory(Root()); return Task.CompletedTask; });
    private async void OpenData(object sender, RoutedEventArgs e) => await Run(() => { OpenDirectory(Core.Data(Root())); return Task.CompletedTask; });
    private async void OpenBackups(object sender, RoutedEventArgs e) => await Run(() => { OpenDirectory(Core.Backups); return Task.CompletedTask; });
    private async void ExportLog(object sender, RoutedEventArgs e) => await Run(() => { var dialog = new SaveFileDialog { FileName = "translator.log", Filter = "日志|*.log" }; if (dialog.ShowDialog(this) == true) Core.AtomicWrite(dialog.FileName, LogBox.Text); return Task.CompletedTask; });
    private async void CheckUpdate(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        release = null; UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "正在检查更新…"; ReleaseNotes.Visibility = Visibility.Collapsed; ReleaseNotes.Document = MarkdownView.Render("");
        try { ShowRelease(await Updates.Check()); }
        catch { UpdateStatus.Text = "检查更新失败，请重试。"; throw; }
    });
    internal void ShowRelease(Release result)
    {
        release = result; UpdateButton.IsEnabled = result.Available;
        UpdateStatus.Text = string.IsNullOrEmpty(result.Status) ? result.Notes : result.Status;
        ReleaseNotes.Document = MarkdownView.Render(result.Notes);
        ReleaseNotes.Visibility = result.Notes.StartsWith("# ") ? Visibility.Visible : Visibility.Collapsed;
        Log(UpdateStatus.Text);
    }
    private async void ApplyUpdate(object sender, RoutedEventArgs e) => await Run(async () => { if (release is null) return; Log("正在下载并校验更新…"); await Updates.Stage(release); busy = false; Application.Current.Shutdown(); });
    /// <summary>截图回归用：预置一个游戏目录并完成一次读取，使界面处于真实数据状态。</summary>
    public async Task PrimeAsync(string game)
    {
        Games.Text = game;
        await Run(LoadGame);
    }

    /// <summary>
    /// 把每个页面渲染为 PNG，用于界面回归核对。
    /// 参数可以是 "all"（缺省）或单个页索引；结果写入 snapshot.log，失败原因不再被吞掉。
    /// </summary>
    public async void SaveSnapshot(string? pages, string? prefix = null)
    {
        var namePrefix = string.IsNullOrWhiteSpace(prefix) ? "preview" : prefix.Trim();
        var report = new List<string>();
        try
        {
            Directory.CreateDirectory(Core.Home);
            var targets = new List<int>();
            if (int.TryParse(pages, out var only) && only >= 0 && only < Pages.Items.Count) targets.Add(only);
            else for (var i = 0; i < Pages.Items.Count; i++) targets.Add(i);
            foreach (var index in targets)
            {
                Pages.SelectedIndex = index;
                // 等布局与渲染管线跑完，否则 RenderTargetBitmap 会抓到未上屏的空白帧。
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                UpdateLayout();
                // 只渲染客户区根元素：直接渲染 Window 会带上非客户区偏移，底部留出空白带。
                var surface = Content as FrameworkElement ?? this;
                var width = (int)Math.Round(surface.ActualWidth); var height = (int)Math.Round(surface.ActualHeight);
                if (width <= 0 || height <= 0) throw new InvalidOperationException($"窗口尺寸无效（{width}×{height}），无法截图。");
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var buffer = new MemoryStream(); encoder.Save(buffer);
                var path = Path.Combine(Core.Home, $"{namePrefix}-{index}.png");
                File.WriteAllBytes(path, buffer.ToArray());
                report.Add("PASS " + path);
            }
        }
        catch (Exception ex) { report.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(Core.Home, "snapshot.log"), report);
        Application.Current.Shutdown();
    }
}
public static class Api
{
    private const int MaxModelsResponseBytes = 4 * 1024 * 1024;
    // 已确认接受 thinking 字段的服务商；OpenAI、Gemini 等严格接口遇到未知参数会返回 HTTP 400。
    private static readonly string[] ThinkingHosts = ["api.deepseek.com", "open.bigmodel.cn"];
    private const string ThinkingHostSuffix = ".volces.com";
    internal static string ValidateApiBaseUrl(string rawBaseUrl)
    {
        var text = rawBaseUrl.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new UserError("API 地址需使用 HTTPS；本机服务可使用 HTTP。地址不能包含账号、查询参数或片段。");
        return text.TrimEnd('/');
    }
    // 服务商内容风控常见特征词（小写比对），与游戏模组 _live_translator_content_block_markers 保持一致。
    private static readonly string[] ContentModerationMarkers =
    [
        "content exists risk",
        "content_policy_violation",
        "content policy",
        "content_filter",
        "datainspectionfailed",
        "inappropriate content",
        "sensitive content",
        "敏感内容",
        "内容安全",
        "违规内容",
        "审核未通过"
    ];
    /// <summary>按服务商调整请求字段，规则与游戏模组 _live_translator_apply_provider_options 保持一致。</summary>
    internal static void ApplyProviderOptions(JsonObject payload, string host, bool thinkingEnabled)
    {
        host = host.ToLowerInvariant();
        if (host == "api.openai.com")
        {
            // OpenAI 推理模型（GPT-5/6、o 系列等）拒绝已弃用的 max_tokens、非默认 temperature
            // 与扩展字段 thinking；省略 temperature 与 thinking 并改用 max_completion_tokens 可兼容全部新旧模型。
            if (payload.Remove("max_tokens", out var maxTokens)) payload["max_completion_tokens"] = maxTokens;
            payload.Remove("temperature");
            return;
        }
        // 已知服务商显式关闭思考以降低延迟；其他接口仅在用户主动开启时发送。
        if (ThinkingHosts.Contains(host) || host.EndsWith(ThinkingHostSuffix, StringComparison.Ordinal) || thinkingEnabled)
            payload["thinking"] = new JsonObject { ["type"] = thinkingEnabled ? "enabled" : "disabled" };
    }
    /// <summary>从服务商错误响应 JSON 中提取精简诊断并识别内容审查拦截；同步脱敏已发送的密钥。</summary>
    internal static (string Detail, bool IsContentBlocked) ExtractErrorDetail(string rawBody, string apiKey = "")
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return ("", false);
        JsonObject? obj;
        try { obj = JsonNode.Parse(rawBody) as JsonObject; }
        catch (JsonException) { return ("", false); }
        if (obj is null) return ("", false);

        var parts = new List<string>();
        string message = "";
        if (obj["error"] is JsonObject errObj)
        {
            foreach (var key in new[] { "code", "type", "message" })
            {
                if (errObj[key] is JsonValue val && val.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    var trimmed = text.Trim();
                    parts.Add(trimmed);
                    if (key == "message") message = trimmed;
                }
            }
        }
        else if (obj["error"] is JsonValue errVal && errVal.TryGetValue<string>(out var errText) && !string.IsNullOrWhiteSpace(errText))
        {
            message = errText.Trim();
            parts.Add(message);
        }

        if (message.Length == 0)
        {
            foreach (var key in new[] { "message", "detail" })
            {
                if (obj[key] is JsonValue val && val.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    message = text.Trim();
                    parts.Add(message);
                    break;
                }
            }
        }

        var combined = string.Join(" ", parts);
        var chosen = message.Length > 0 ? message : combined;
        var detail = System.Text.RegularExpressions.Regex.Replace(chosen, @"[\r\n\t ]+", " ").Trim();
        var trimmedKey = apiKey.Trim();
        if (trimmedKey.Length > 0 && detail.Contains(trimmedKey, StringComparison.Ordinal))
            detail = detail.Replace(trimmedKey, "***", StringComparison.Ordinal);
        if (detail.Length > 200) detail = detail[..200].TrimEnd() + "…";
        var lowered = combined.ToLowerInvariant();
        var blocked = ContentModerationMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
        return (detail, blocked);
    }
    // Claude 官方地址走 Anthropic Messages API，其余地址按 OpenAI 兼容接口处理；与游戏模组规则一致。
    private const string AnthropicHost = "api.anthropic.com";
    private const string AnthropicVersion = "2023-06-01";
    internal static bool IsAnthropic(string host) => host.Equals(AnthropicHost, StringComparison.OrdinalIgnoreCase);
    internal static string Endpoint(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        var suffix = IsAnthropic(new Uri(url).Host) ? "/messages" : "/chat/completions";
        return url.EndsWith(suffix, StringComparison.Ordinal) ? url : url + suffix;
    }
    internal static string ModelsEndpoint(string baseUrl)
    {
        var url = ValidateApiBaseUrl(baseUrl);
        foreach (var suffix in new[] { "/chat/completions", "/messages" })
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { url = url[..^suffix.Length]; break; }
        return url.EndsWith("/models", StringComparison.OrdinalIgnoreCase) ? url : url + "/models";
    }
    internal static IReadOnlyList<string> ParseModelsResponse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new UserError("服务商返回的模型列表不是有效的 JSON。", ex); }
        JsonArray? entries = root switch
        {
            JsonObject obj when obj["data"] is JsonArray data => data,
            JsonObject obj when obj["models"] is JsonArray models => models,
            JsonArray direct => direct,
            _ => null
        };
        if (entries is null) throw new UserError("服务商返回的模型列表格式无法识别。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var item in entries)
        {
            string id = "";
            if (item is JsonValue textNode && textNode.TryGetValue<string>(out var rawId)) id = rawId.Trim();
            else if (item is JsonObject obj)
            {
                if (obj["id"] is JsonValue idVal && idVal.TryGetValue<string>(out var idText)) id = idText.Trim();
                else if (obj["name"] is JsonValue nameVal && nameVal.TryGetValue<string>(out var nameText)) id = nameText.Trim();
            }
            if (id.Length > 0 && seen.Add(id)) ids.Add(id);
        }
        if (ids.Count == 0) throw new UserError("服务商返回成功，但未找到可用模型。");
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }
    public static async Task<IReadOnlyList<string>> FetchModelsAsync(string baseUrl, string apiKey, int timeoutSeconds = 20, CancellationToken cancellationToken = default)
    {
        var url = ModelsEndpoint(baseUrl);
        var uri = new Uri(url);
        var anthropic = IsAnthropic(uri.Host);
        if (string.IsNullOrWhiteSpace(apiKey) && !uri.IsLoopback)
            throw new UserError("请先填写或保存 API Key，再获取可用模型。");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60))
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RenpyTranslator/" + Core.ManagerVersion);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (anthropic)
        {
            request.Headers.Add("x-api-key", apiKey.Trim());
            request.Headers.Add("anthropic-version", AnthropicVersion);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var code = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            if (code is 401 or 403) throw new UserError($"获取模型列表失败（HTTP {code}）：鉴权失败或无权查看模型列表，请检查 API Key。");
            if (code == 404) throw new UserError("该服务商未提供 /models 接口（HTTP 404），请直接手动填写模型名称。");
            if (code == 429) throw new UserError("请求过于频繁或配额受限（HTTP 429），请稍后再试。");
            if (code is >= 300 and < 400) throw new UserError($"接口返回重定向（HTTP {code}），为保护密钥已中止，请核对 API 地址。");
            throw new UserError($"获取模型列表失败，服务商返回 HTTP {code}。");
        }
        if (response.Content.Headers.ContentLength is > MaxModelsResponseBytes)
            throw new UserError("服务商返回的模型列表体积过大，无法解析。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxModelsResponseBytes) throw new UserError("服务商返回的模型列表体积过大，无法解析。");
            buffer.Write(chunk, 0, read);
        }
        return ParseModelsResponse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
    /// <summary>Claude 请求体，规则与游戏模组 _live_translator_anthropic_payload 保持一致。</summary>
    internal static JsonObject AnthropicPayload(JsonObject config, string userContent)
    {
        // 当前 Claude 模型拒绝非默认 temperature，因此不发送；思考深度改用 effort 控制：
        // Opus 5.5、Sonnet 5.5 不接受关闭思考，low 是所有当前模型都接受的最省方式。
        var payload = new JsonObject
        {
            ["model"] = Core.ReadString(config, "model"), ["max_tokens"] = config["max_output_tokens"]!.DeepClone(),
            ["system"] = Core.ReadString(config, "system_prompt"),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = userContent })
        };
        var outputConfig = new JsonObject();
        if (!Core.ReadBool(config, "thinking_enabled")) outputConfig["effort"] = "low";
        // 结构化输出保证返回体一定是 {"translations": [...]}。
        if (Core.ReadBool(config, "json_response_format"))
            outputConfig["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["translations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
                    ["required"] = new JsonArray("translations"), ["additionalProperties"] = false
                }
            };
        if (outputConfig.Count > 0) payload["output_config"] = outputConfig;
        return payload;
    }
    /// <summary>取出 Claude 回复正文：回复可能以 thinking 块开头，只拼接 text 块；被拒绝时给出明确提示。</summary>
    internal static string AnthropicText(JsonNode response)
    {
        if (response["stop_reason"]?.GetValue<string>() == "refusal") throw new UserError("连接成功，但 Claude 拒绝了这次测试翻译。");
        return string.Concat((response["content"] as JsonArray ?? []).Where(block => block?["type"]?.GetValue<string>() == "text").Select(block => block!["text"]!.GetValue<string>()));
    }
    /// <summary>取出 OpenAI 兼容接口回复正文，并优先识别内容审查与模型拒答。</summary>
    internal static string OpenAiText(JsonNode response)
    {
        if (response["choices"] is not JsonArray choices || choices.Count == 0 || choices[0] is not JsonObject choice)
            throw new FormatException();
        if (choice["finish_reason"] is JsonValue finishVal && finishVal.TryGetValue<string>(out var finishReason) && finishReason == "content_filter")
            throw new UserError("连接成功，但服务商因内容审查拦截了这次测试翻译。");
        if (choice["message"] is not JsonObject message) throw new FormatException();
        if (message["refusal"] is JsonValue refusalVal && refusalVal.TryGetValue<string>(out var refusalText) && !string.IsNullOrWhiteSpace(refusalText))
            throw new UserError("连接成功，但模型拒绝了这次测试翻译。");
        var node = message["content"] ?? throw new FormatException();
        var text = node is JsonArray blocks ? string.Concat(blocks.Select(x => x?["text"]?.GetValue<string>() ?? "")) : node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException();
        return text;
    }
    public static async Task Test(JsonObject config)
    {
        var secret = Core.ReadString(config, "api_key_encrypted"); if (secret.Length == 0) throw new UserError("请填写 API Key。");
        var apiKey = Secret.Unprotect(secret);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(Math.Clamp(config["request_timeout_seconds"]!.GetValue<int>(), 1, 300)) };
        var url = Endpoint(Core.ReadString(config, "base_url")); var host = new Uri(url).Host; bool anthropic = IsAnthropic(host);
        const string userContent = "{\"texts\":[\"Hello\"]}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        JsonObject payload;
        if (anthropic)
        {
            request.Headers.Add("x-api-key", apiKey); request.Headers.Add("anthropic-version", AnthropicVersion);
            payload = AnthropicPayload(config, userContent);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            payload = new JsonObject { ["model"] = Core.ReadString(config, "model"), ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = Core.ReadString(config, "system_prompt") }, new JsonObject { ["role"] = "user", ["content"] = userContent }), ["temperature"] = config["temperature"]!.DeepClone(), ["max_tokens"] = config["max_output_tokens"]!.DeepClone() };
            if (Core.ReadBool(config, "json_response_format")) payload["response_format"] = new JsonObject { ["type"] = "json_object" };
            ApplyProviderOptions(payload, host, Core.ReadBool(config, "thinking_enabled"));
        }
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            var (detail, blocked) = ExtractErrorDetail(await response.Content.ReadAsStringAsync(), apiKey);
            var detailSuffix = detail.Length > 0 ? $"（服务商返回：{detail}）" : "";
            if (code is 400 or 422 && blocked)
                throw new UserError($"API 返回 HTTP {code}，触发服务商内容审查{detailSuffix}。");
            throw new UserError($"API 返回 HTTP {code}{detailSuffix}。400：参数或内容被拒；401/403：鉴权或权限；404：地址或模型；429：配额或限流。请核对服务商控制台。");
        }
        try
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new FormatException();
            // 拒绝与内容审查属于明确结论，先于格式校验给出，不并入“响应格式无效”。
            var content = anthropic ? AnthropicText(body) : OpenAiText(body);
            var start = content.IndexOf('{'); var end = content.LastIndexOf('}');
            var translations = JsonNode.Parse(content[start..(end + 1)])!["translations"]!.AsArray();
            if (translations.Count != 1 || string.IsNullOrWhiteSpace(translations[0]!.GetValue<string>())) throw new FormatException();
        }
        catch (UserError) { throw; }
        catch { throw new UserError("连接成功，但响应不是模组要求的 translations JSON 数组。"); }
    }
}
