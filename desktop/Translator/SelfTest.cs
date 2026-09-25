using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
namespace RenpyTranslator;

public static class SelfTest
{
    // tests 目录留存每次自检的模拟游戏目录供排查（含译文与备份副本，单份约数 MB），
    // 不设上限会随自检次数无限堆积，故按创建时间只保留最近若干份（约两轮 Publish 产物）。
    private const int TestRetention = 4;
    /// <summary>按创建时间删除超出保留份数的旧测试目录；失败一律忽略，不影响自检流程。</summary>
    private static void PruneTests()
    {
        var root = Path.Combine(Core.Home, "tests");
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetCreationTimeUtc).Skip(TestRetention))
            {
                try { Directory.Delete(dir, true); }
                catch (IOException) { }                  // 被占用时留到下次清理
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public static int Run()
    {
        var root = Path.Combine(Core.Home, "tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); PruneTests();
        var log = new List<string>();
        void Assert(bool condition, string name) { if (!condition) throw new Exception(name); log.Add("PASS " + name); }
        void Reject(Action action, string message, string name)
        {
            try { action(); }
            catch (IOException ex) { Assert(ex.Message.Contains(message), name); return; }
            throw new Exception("Expected friendly rejection: " + name);
        }
        try
        {
            // 模拟发行列表，覆盖同版本、修订号等价、乱序和预发布筛选，不访问 GitHub。
            JsonObject ReleaseFixture(string version, bool prerelease = false, bool complete = true) => new()
            {
                ["tag_name"] = version, ["body"] = "## 修复\n\n- **说明**",
                ["prerelease"] = prerelease,
                ["assets"] = complete ? new JsonArray(
                    new JsonObject { ["name"] = "RenpyTranslator-win-x64.zip", ["browser_download_url"] = "https://example.com/release.zip" },
                    new JsonObject { ["name"] = "RenpyTranslator-win-x64.zip.sha256", ["browser_download_url"] = "https://example.com/release.sha256" }) : new JsonArray()
            };
            foreach (var same in new[] { "v26.9.11", "v26.9.11.0", "v26.9.10" })
            {
                var result = Updates.SelectRelease(new JsonArray(ReleaseFixture(same)), "26.9.11");
                Assert(!result.Available && result.ZipUrl == "" && result.Status.Contains("最新") && !result.Notes.Contains("## 修复"), "Same or older version does not advertise update: " + same);
            }
            var newer = Updates.SelectRelease(new JsonArray(ReleaseFixture("v26.9.10"), ReleaseFixture("v26.10.1"), ReleaseFixture("v27.1.1", true)), "26.9.11");
            Assert(newer.Available && newer.Notes.StartsWith("# v26.10.1"), "Select highest stable version rather than first release");
            Assert(!Updates.SelectRelease(new JsonArray(ReleaseFixture("v26.10.1", complete: false)), "26.9.11").Available, "Missing release assets disable download");
            var markdown = MarkdownView.Render("# 标题\n\n**加粗** 和 `代码`\n\n- 项目\n\n```text\n示例\n```\n\n[链接](https://example.com)");
            var paragraphs = markdown.Blocks.OfType<System.Windows.Documents.Paragraph>().ToArray();
            Assert(paragraphs[0].FontSize > markdown.FontSize && markdown.Blocks.OfType<System.Windows.Documents.List>().Any(), "Markdown renders headings and real list blocks");
            Assert(paragraphs.Any(p => p.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any()) && !new System.Windows.Documents.TextRange(markdown.ContentStart, markdown.ContentEnd).Text.Contains("**"), "Markdown renders hyperlinks and removes formatting markers");
            var focusStyle = (System.Windows.Style)System.Windows.Application.Current.FindResource(System.Windows.SystemParameters.FocusVisualStyleKey);
            var focusTemplate = (System.Windows.Controls.ControlTemplate)focusStyle.Setters.OfType<System.Windows.Setter>().Single(s => s.Property == System.Windows.Controls.Control.TemplateProperty).Value;
            Assert(focusTemplate.LoadContent() is null, "Default keyboard focus visual has no dotted border");
            Directory.CreateDirectory(Path.Combine(root, "game")); Directory.CreateDirectory(Path.Combine(root, "renpy"));
            Assert(Core.Game(root + "\\") == root && Core.Game(root + "/") == root, "Normalize trailing game separators");
            Assert(Core.IsInside("D:\\", "D:\\game\\test.exe") && !Core.IsInside(root, root + "-other\\test.exe"), "Path containment handles drive roots and sibling prefixes");
            // 使用隔离目录内的命令解释器模拟运行中的游戏，不打开真实游戏。
            var executable = Path.Combine(root, "game", "guard.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
            using (var process = Process.Start(new ProcessStartInfo(executable, "/d /q /k") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!)
            {
                try
                {
                    Reject(() => Core.Transaction(root + "\\", [], () => throw new Exception("Guard bypassed")), "请先关闭", "Running process blocks writes with trailing separator");
                }
                finally { if (!process.HasExited) process.Kill(); process.WaitForExit(); }
            }
            var data = Core.Data(root); Directory.CreateDirectory(data);
            // 新版不再随包分发字体：自检使用本机系统字体，缺失时退回默认解析结果，不依赖具体机型。
            var fontFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var systemFont = new[] { "simhei.ttf", "msyh.ttc", "simsun.ttc", "segoeui.ttf" }.Select(name => Path.Combine(fontFolder, name)).FirstOrDefault(File.Exists) ?? Core.DefaultFont();
            var config = Core.Defaults(); Core.NormalizeKey(config, "test-secret-本机");
            Assert(Secret.Unprotect(Core.ReadString(config, "api_key_encrypted")) == "test-secret-本机", "DPAPI round trip");
            Core.AtomicWrite(Path.Combine(root, "game", "story.rpy"), "original game");
            Core.AtomicWrite(Path.Combine(data, "cache.jsonl"), "private cache");
            Core.Install(root, config, false, systemFont);
            Assert(Core.Status(root).Contains("文件完整"), "Install and hash verification");
            Assert(!File.Exists(Path.Combine(data, "pretranslated.jsonl")), "Generic install excludes game-specific translations");
            var installedScript = Path.Combine(root, "game", "zz_live_translator.rpy");
            var confirmScript = Path.Combine(root, "game", "zz_live_translator_camp_buddy.rpy");
            Assert(!File.ReadAllText(installedScript).Contains("screen confirm(") && !File.Exists(confirmScript), "Generic install preserves original confirmation screen");
            var manifest = Path.Combine(data, "installation.json"); var validManifest = File.ReadAllText(manifest);
            foreach (var invalid in new[] { "", "[]", "{", "{}", "{\"files\":{}}", "{\"files\":[]}", "{\"files\":{\"zz_live_translator.rpy\":123}}" })
            {
                Core.AtomicWrite(manifest, invalid);
                var state = Core.InspectInstallation(root);
                Assert(state.Status.Contains("可直接安装 / 修复") && !state.Status.Contains("文件完整"), "Damaged manifest remains loadable for repair: " + invalid);
            }
            Core.Install(root, Core.Config(root), false, systemFont);
            Assert(Core.Status(root).Contains("文件完整") && Secret.Unprotect(Core.ReadString(Core.Config(root), "api_key_encrypted")) == "test-secret-本机", "Repair malformed manifest preserves valid configuration");
            foreach (var required in new[] { "zz_live_translator.rpy" })
            {
                var incomplete = JsonNode.Parse(validManifest)!.AsObject(); incomplete["files"]!.AsObject().Remove(required);
                Core.WriteJson(manifest, incomplete);
                Assert(!Core.Status(root).Contains("文件完整"), "Reject missing required hash: " + required);
            }
            Core.AtomicWrite(manifest, validManifest);
            var malformed = Path.Combine(root, "invalid.json");
            foreach (var invalid in new[] { "", "[]", "null", "{" })
            {
                File.WriteAllText(malformed, invalid);
                Reject(() => Core.ReadJson(malformed), "配置", "Invalid config produces Chinese diagnostic: " + invalid);
            }
            foreach (var key in new[] { "protected_names", "skip_patterns" })
            {
                foreach (var invalid in new[] { "[123]", "[null]", "{}", "[" })
                    Reject(() => Core.StringArray(invalid, key), key == "skip_patterns" ? "跳过规则" : "保护人名", "Reject invalid string array: " + key + invalid);
                Assert(Core.StringArray("[\"valid\"]", key).Count == 1, "Accept valid string array: " + key);
            }
            Reject(() => Core.StringArray("[\"[\"]", "skip_patterns"), "正则表达式", "Reject invalid regular expression");
            foreach (var pattern in new[] { "(?<name>Hello)", "(?P<name>Hello)", @"\p{L}", @"(a)\1", "a{,3}" })
                Reject(() => Core.StringArray(new JsonArray(pattern).ToJsonString(), "skip_patterns"), "跳过规则", "Reject non-portable regex: " + pattern);
            foreach (var pattern in new[] { "^https?://", @"^[A-Za-z]:[\\/]", "^[A-Z0-9_+.-]{1,4}$", @"(?:Hello|World)\s+\d{2,}" })
                Assert(Core.StringArray(new JsonArray(pattern).ToJsonString(), "skip_patterns").Count == 1, "Accept portable regex: " + pattern);
            Assert(File.ReadAllText(Path.Combine(data, "cache.jsonl")) == "private cache", "Install preserves cache");
            var before = File.ReadAllBytes(Path.Combine(data, "config.json"));
            try { Core.Transaction(root, ["live_translator/config.json", "failure.txt"], () => { File.WriteAllText(Path.Combine(data, "config.json"), "broken"); File.WriteAllText(Path.Combine(root, "game", "failure.txt"), "new"); throw new IOException("injected failure"); }); } catch (IOException) { }
            Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(data, "config.json"))) && !File.Exists(Path.Combine(root, "game", "failure.txt")), "Transaction rollback restores exact bytes");
            var count = Core.Install(root, Core.Config(root), true, systemFont); Assert(count > 0, "Bundled translations validation and install");
            Assert(File.ReadAllText(confirmScript).Contains("screen confirm(") && Core.Status(root).Contains("文件完整"), "Bundled confirm is installed and hashed");
            var bundledManifest = File.ReadAllText(manifest);
            foreach (var required in new[] { "zz_live_translator_camp_buddy.rpy", "live_translator/pretranslated.jsonl" })
            {
                var incomplete = JsonNode.Parse(bundledManifest)!.AsObject(); incomplete["files"]!.AsObject().Remove(required);
                Core.WriteJson(manifest, incomplete); var state = Core.InspectInstallation(root);
                Assert(state.Bundled && state.Status.Contains("修复") && !state.Status.Contains("文件完整"), "Preserve pack while rejecting incomplete bundled manifest: " + required);
            }
            Core.AtomicWrite(manifest, bundledManifest);
            // 自定义包使用隔离夹具，绝不把用户本机的私人全量包作为测试资源。
            var customPack = Path.Combine(root, "custom-pack"); Directory.CreateDirectory(Path.Combine(customPack, "chapters"));
            var firstPart = Path.Combine(customPack, "a.jsonl"); var secondPart = Path.Combine(customPack, "chapters", "b.jsonl");
            const string firstLine = "{\"source\":\"Custom first\",\"translation\":\"自定义一\"}";
            const string secondLine = "{\"source\":\"Custom second\",\"translation\":\"自定义二\"}";
            File.WriteAllText(firstPart, firstLine); File.WriteAllText(secondPart, secondLine);
            File.WriteAllText(Path.Combine(customPack, "README.txt"), "not a translation");
            var originalPretranslation = File.ReadAllBytes(Path.Combine(data, "pretranslated.jsonl"));
            Assert(Core.Install(root, Core.Config(root), false, systemFont, customPack) == 2, "Custom folder recursively imports JSONL only");
            var customState = Core.InspectInstallation(root);
            Assert(customState.CustomPack && customState.CustomDirectory == customPack && customState.Status.Contains("文件完整"), "Custom pack selection and folder persist with hash verification");
            Assert(!File.Exists(confirmScript) && File.ReadAllText(firstPart) == firstLine && File.ReadAllText(secondPart) == secondLine, "Custom pack preserves source files and original game screen");
            Assert(Directory.GetFiles(Path.Combine(Core.Home, "backups"), "pretranslated.jsonl", SearchOption.AllDirectories).Any(path => File.ReadAllBytes(path).SequenceEqual(originalPretranslation)), "Custom replacement backs up previous translations");
            var installedBeforeInvalid = File.ReadAllBytes(Path.Combine(data, "pretranslated.jsonl"));
            File.WriteAllText(secondPart, firstLine);
            Reject(() => Core.Install(root, Core.Config(root), false, systemFont, customPack), "原文重复", "Duplicate custom source is rejected before writing");
            File.WriteAllText(secondPart, "{broken");
            Reject(() => Core.Install(root, Core.Config(root), false, systemFont, customPack), "b.jsonl:1", "Malformed nested custom JSON identifies file and line");
            Assert(File.ReadAllBytes(Path.Combine(data, "pretranslated.jsonl")).SequenceEqual(installedBeforeInvalid) && Core.InspectInstallation(root).CustomPack, "Invalid custom import preserves installed data and manifest");
            var emptyPack = Path.Combine(root, "empty-pack"); Directory.CreateDirectory(emptyPack);
            Reject(() => Core.Install(root, Core.Config(root), false, systemFont, emptyPack), "没有可安装", "Empty custom folder is rejected");
            Reject(() => Core.Install(root, Core.Config(root), false, systemFont, ""), "请选择", "Unselected custom folder is rejected");
            Reject(() => Core.Install(root, Core.Config(root), false, systemFont, Path.Combine(root, "missing-pack")), "不存在", "Missing custom folder is rejected");
            var customManifest = File.ReadAllText(manifest);
            var incompleteCustom = Core.ReadJson(manifest); incompleteCustom["files"]!.AsObject().Remove("live_translator/pretranslated.jsonl"); Core.WriteJson(manifest, incompleteCustom);
            Assert(Core.Status(root).Contains("修复") && Core.InspectInstallation(root).CustomPack, "Custom pack requires pretranslation hash while remaining repairable");
            Core.AtomicWrite(manifest, customManifest);
            Core.Install(root, Core.Config(root), false, systemFont);
            Assert(File.ReadAllBytes(Path.Combine(data, "pretranslated.jsonl")).SequenceEqual(installedBeforeInvalid) && !Core.InspectInstallation(root).CustomPack, "Switching custom pack to generic preserves installed translations");
            // 模拟旧编译缓存，确保模式切换不能残留屏幕覆盖。
            File.WriteAllText(confirmScript + "c", "compiled fixture");
            Core.Install(root, Core.Config(root), false, systemFont);
            Assert(!File.Exists(confirmScript) && !File.Exists(confirmScript + "c") && File.Exists(Path.Combine(data, "pretranslated.jsonl")), "Generic mode removes dedicated screen and retains translations");
            // 旧版内置字体的残留副本应随安装清理，清空后的目录一并移除。
            var legacyFont = Path.Combine(root, "game", Core.LegacyBundledFont);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyFont)!);
            File.WriteAllText(legacyFont, "legacy bundled font fixture");
            Core.Install(root, Core.Config(root), false, systemFont);
            Assert(!File.Exists(legacyFont) && !Directory.Exists(Path.GetDirectoryName(legacyFont)), "Install removes the legacy bundled font copy");
            var customFont = "live_translator/fonts/custom-msyh.ttf";
            // 目录可能已被旧字体清理移除，夹具自行创建。
            var customFontDirectory = Path.GetDirectoryName(Path.Combine(root, "game", customFont))!;
            Directory.CreateDirectory(customFontDirectory);
            File.WriteAllText(Path.Combine(root, "game", customFont), "font fixture");
            Assert(Core.FontPreset(customFont) == 3 && Core.FontPreset("C:/custom/SIMSUN.ttf") == 3, "Custom font names do not match presets");
            // 默认预设为系统黑体；空值与旧版内置字体路径都归入该预设，系统字体匹配忽略大小写。
            Assert(Core.FontPreset("") == 0 && Core.FontPreset(Core.LegacyBundledFont) == 0 && Core.FontPreset(Path.Combine(fontFolder, "SIMHEI.TTF")) == 0, "Default preset is the system SimHei font");
            Assert(Core.FontPreset(Path.Combine(fontFolder, "MSYH.TTC")) == 1 && Core.FontPreset(Path.Combine(fontFolder, "SIMSUN.TTC")) == 2, "System font presets ignore case");
            Core.Install(root, Core.Config(root), false, customFont);
            Assert(Core.ReadString(Core.Config(root), "font") == customFont, "Install preserves relative custom font path");
            Core.Install(root, Core.Config(root), true, customFont);
            File.WriteAllText(confirmScript + "c", "compiled fixture");
            var notes = Path.Combine(data, "my-own-notes.txt"); File.WriteAllText(notes, "private notes");
            Assert(Secret.Unprotect(Core.ReadString(Core.Config(root), "api_key_encrypted")) == "test-secret-本机", "Upgrade retains encrypted key");
            Core.Uninstall(root, false);
            Assert(!File.Exists(confirmScript) && !File.Exists(confirmScript + "c") && File.Exists(notes), "Default uninstall removes dedicated screen and preserves personal files");
            Assert(!File.Exists(Path.Combine(root, "game", "zz_live_translator.rpy")) && File.Exists(Path.Combine(data, "config.json")) && File.Exists(Path.Combine(data, "cache.jsonl")), "Uninstall retains config and cache");
            Assert(File.ReadAllText(Path.Combine(root, "game", "story.rpy")) == "original game", "Uninstall preserves original game files");
            Core.Uninstall(root, true); Assert(!File.Exists(Path.Combine(data, "config.json")) && !File.Exists(Path.Combine(data, "cache.jsonl")), "Explicit data removal");
            Assert(!File.Exists(notes) && Directory.GetFiles(Path.Combine(Core.Home, "backups"), "my-own-notes.txt", SearchOption.AllDirectories).Any(path => File.ReadAllText(path) == "private notes"), "Explicit cleanup backs up personal files before deletion");
            var duplicates = Path.Combine(root, "duplicates"); Directory.CreateDirectory(duplicates); File.WriteAllText(Path.Combine(duplicates, "a.jsonl"), "{\"source\":\"a\",\"translation\":\"b\"}\n{\"source\":\"a\",\"translation\":\"c\"}");
            bool rejected = false; try { Core.Merge(duplicates); } catch (IOException) { rejected = true; }
            Assert(rejected, "Reject duplicate source strings");
            File.WriteAllText(Path.Combine(duplicates, "a.jsonl"), "{\"source\":123,\"translation\":\"b\"}");
            Reject(() => Core.Merge(duplicates), "a.jsonl:1", "Non-string translation includes file and line");
            File.WriteAllText(Path.Combine(duplicates, "a.jsonl"), "{\"source\":\"a\",\"translation\":123}");
            Reject(() => Core.Merge(duplicates), "a.jsonl:1", "Non-string output includes file and line");
            var updates = Path.Combine(root, "updates"); Directory.CreateDirectory(updates);
            var updateDirs = Enumerable.Range(0, 5).Select(_ => Path.Combine(updates, Guid.NewGuid().ToString("N"))).ToArray();
            for (int i = 0; i < updateDirs.Length; i++)
            {
                Directory.CreateDirectory(updateDirs[i]); File.WriteAllText(Path.Combine(updateDirs[i], "release.zip"), "fixture");
                Directory.SetCreationTimeUtc(updateDirs[i], DateTime.UtcNow.AddDays(-i - 2));
            }
            Directory.SetCreationTimeUtc(updateDirs[0], DateTime.UtcNow);
            var unrelated = Path.Combine(updates, "user-files"); Directory.CreateDirectory(unrelated);
            using (var lease = new FileStream(Path.Combine(updateDirs[4], "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Updates.PruneUpdates(updates);
                Assert(Directory.Exists(updateDirs[0]) && Directory.Exists(updateDirs[1]) && Directory.Exists(updateDirs[4]), "Update cleanup retains latest two and active directory");
                Assert(!Directory.Exists(updateDirs[2]) && !Directory.Exists(updateDirs[3]) && Directory.Exists(unrelated), "Update cleanup removes old managed directories only");
            }
            Updates.PruneUpdates(updates);
            Assert(!Directory.Exists(updateDirs[4]), "Released old update can be reclaimed");
            Assert(Core.ManagerVersion == File.ReadAllText(Path.Combine(Core.Resources, "version.txt")).Trim(), "Manager and resource versions match");
            var zip = Path.Combine(root, "bad.zip"); using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open()); writer.Write("unsafe"); }
            rejected = false; try { Updates.Extract(zip, Path.Combine(root, "extract")); } catch (IOException) { rejected = true; }
            Assert(rejected && !File.Exists(Path.Combine(root, "escape.txt")), "Reject update path traversal");
            // 使用回环地址模拟 API，验证网络协议而不调用真实模型或启动游戏。
            void MockApi(int status, string body, bool shouldPass)
            {
                using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
                using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
                var sample = Core.Defaults(); sample["base_url"] = $"http://127.0.0.1:{port}"; Core.NormalizeKey(sample, "mock-key");
                var server = Task.Run(async () =>
                {
                    var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    using var reader = new StreamReader(context.Request.InputStream); var requestBody = await reader.ReadToEndAsync();
                    bool valid = context.Request.Url!.AbsolutePath == "/chat/completions" && context.Request.Headers["Authorization"] == "Bearer mock-key" && requestBody.Contains("Hello");
                    var bytes = Encoding.UTF8.GetBytes(body); context.Response.StatusCode = status; context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); return valid;
                });
                bool passed = true;
                try { Task.Run(() => Api.Test(sample)).GetAwaiter().GetResult(); } catch (IOException) { passed = false; }
                Assert(server.GetAwaiter().GetResult() && passed == shouldPass, $"Mock API HTTP {status}, valid response = {shouldPass}");
            }
            MockApi(200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"translations\\\":[\\\"你好\\\"]}\"}}]}", true);
            MockApi(401, "{\"error\":\"invalid key\"}", false);
            MockApi(200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"translations\\\":[]}\"}}]}", false);
            File.WriteAllLines(Path.Combine(Core.Home, "self-test.log"), log); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(Core.Home, "self-test.log"), log); return 1; }
    }
}
