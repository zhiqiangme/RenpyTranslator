using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace RenpyTranslator;

public static class SelfTest
{
    public static int Run()
    {
        var root = Path.Combine(Core.Home, "tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var log = new List<string>();
        void Assert(bool condition, string name) { if (!condition) throw new Exception(name); log.Add("PASS " + name); }
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "game")); Directory.CreateDirectory(Path.Combine(root, "renpy"));
            var data = Core.Data(root); Directory.CreateDirectory(data);
            var config = Core.Defaults(); Core.NormalizeKey(config, "test-secret-本机");
            Assert(Secret.Unprotect(Core.ReadString(config, "api_key_encrypted")) == "test-secret-本机", "DPAPI round trip");
            Core.AtomicWrite(Path.Combine(root, "game", "story.rpy"), "original game");
            Core.AtomicWrite(Path.Combine(data, "cache.jsonl"), "private cache");
            Core.Install(root, config, false, "HarmonyOS");
            Assert(Core.Status(root).Contains("文件完整"), "Install and hash verification");
            Assert(!File.Exists(Path.Combine(data, "pretranslated.jsonl")), "Generic install excludes game-specific translations");
            Assert(File.ReadAllText(Path.Combine(data, "cache.jsonl")) == "private cache", "Install preserves cache");
            var before = File.ReadAllBytes(Path.Combine(data, "config.json"));
            try { Core.Transaction(root, ["live_translator/config.json", "failure.txt"], () => { File.WriteAllText(Path.Combine(data, "config.json"), "broken"); File.WriteAllText(Path.Combine(root, "game", "failure.txt"), "new"); throw new IOException("injected failure"); }); } catch (IOException) { }
            Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(data, "config.json"))) && !File.Exists(Path.Combine(root, "game", "failure.txt")), "Transaction rollback restores exact bytes");
            var count = Core.Install(root, Core.Config(root), true, "HarmonyOS"); Assert(count > 0, "Bundled translations validation and install");
            Assert(Secret.Unprotect(Core.ReadString(Core.Config(root), "api_key_encrypted")) == "test-secret-本机", "Upgrade retains encrypted key");
            Core.Uninstall(root, false);
            Assert(!File.Exists(Path.Combine(root, "game", "zz_live_translator.rpy")) && File.Exists(Path.Combine(data, "config.json")) && File.Exists(Path.Combine(data, "cache.jsonl")), "Uninstall retains config and cache");
            Assert(File.ReadAllText(Path.Combine(root, "game", "story.rpy")) == "original game", "Uninstall preserves original game files");
            Core.Uninstall(root, true); Assert(!File.Exists(Path.Combine(data, "config.json")) && !File.Exists(Path.Combine(data, "cache.jsonl")), "Explicit data removal");
            var duplicates = Path.Combine(root, "duplicates"); Directory.CreateDirectory(duplicates); File.WriteAllText(Path.Combine(duplicates, "a.jsonl"), "{\"source\":\"a\",\"translation\":\"b\"}\n{\"source\":\"a\",\"translation\":\"c\"}");
            bool rejected = false; try { Core.Merge(duplicates); } catch (IOException) { rejected = true; }
            Assert(rejected, "Reject duplicate source strings");
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
