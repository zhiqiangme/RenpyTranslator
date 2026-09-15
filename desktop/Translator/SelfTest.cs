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
    // tests 目录留存每次自检的模拟游戏目录供排查，单份约 25MB（主要是内置字体副本），
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
            var config = Core.Defaults(); Core.NormalizeKey(config, "test-secret-本机");
            Assert(Secret.Unprotect(Core.ReadString(config, "api_key_encrypted")) == "test-secret-本机", "DPAPI round trip");
            Core.AtomicWrite(Path.Combine(root, "game", "story.rpy"), "original game");
            Core.AtomicWrite(Path.Combine(data, "cache.jsonl"), "private cache");
            Core.Install(root, config, false, "HarmonyOS");
            Assert(Core.Status(root).Contains("文件完整"), "Install and hash verification");
            Assert(!File.Exists(Path.Combine(data, "pretranslated.jsonl")), "Generic install excludes game-specific translations");
            var installedScript = Path.Combine(root, "game", "zz_live_translator.rpy");
            var confirmScript = Path.Combine(root, "game", "zz_live_translator_camp_buddy.rpy");
            Assert(!File.ReadAllText(installedScript).Contains("screen confirm(") && !File.Exists(confirmScript), "Generic install preserves original confirmation screen");
            var manifest = Path.Combine(data, "installation.json"); var validManifest = File.ReadAllText(manifest);
            foreach (var invalid in new[] { "{}", "{\"files\":[]}", "{\"files\":{\"zz_live_translator.rpy\":123}}" })
            {
                Core.AtomicWrite(manifest, invalid);
                Reject(() => Core.Status(root), "安装记录", "Invalid manifest produces Chinese diagnostic: " + invalid);
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
            Assert(File.ReadAllText(Path.Combine(data, "cache.jsonl")) == "private cache", "Install preserves cache");
            var before = File.ReadAllBytes(Path.Combine(data, "config.json"));
            try { Core.Transaction(root, ["live_translator/config.json", "failure.txt"], () => { File.WriteAllText(Path.Combine(data, "config.json"), "broken"); File.WriteAllText(Path.Combine(root, "game", "failure.txt"), "new"); throw new IOException("injected failure"); }); } catch (IOException) { }
            Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(data, "config.json"))) && !File.Exists(Path.Combine(root, "game", "failure.txt")), "Transaction rollback restores exact bytes");
            var count = Core.Install(root, Core.Config(root), true, "HarmonyOS"); Assert(count > 0, "Bundled translations validation and install");
            Assert(File.ReadAllText(confirmScript).Contains("screen confirm(") && Core.Status(root).Contains("文件完整"), "Bundled confirm is installed and hashed");
            // 模拟旧编译缓存，确保模式切换不能残留屏幕覆盖。
            File.WriteAllText(confirmScript + "c", "compiled fixture");
            Core.Install(root, Core.Config(root), false, "HarmonyOS");
            Assert(!File.Exists(confirmScript) && !File.Exists(confirmScript + "c") && File.Exists(Path.Combine(data, "pretranslated.jsonl")), "Generic mode removes dedicated screen and retains translations");
            var customFont = "live_translator/fonts/custom-msyh.ttf";
            File.WriteAllText(Path.Combine(root, "game", customFont), "font fixture");
            Assert(Core.FontPreset(customFont) == 3 && Core.FontPreset("C:/custom/SIMSUN.ttf") == 3, "Custom font names do not match presets");
            Assert(Core.FontPreset(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "MSYH.TTC")) == 1, "System font matching ignores case");
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
