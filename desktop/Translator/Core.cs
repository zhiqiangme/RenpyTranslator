using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RenpyTranslator;

public static class Core
{
    public static readonly string ManagerVersion = typeof(Core).Assembly.GetName().Version!.ToString(3);
    public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenpyTranslator");
    public static readonly string Resources = Path.Combine(AppContext.BaseDirectory, "Resources");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string ReadString(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? "";
    public static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new IOException("配置为空。");
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void WriteJson(string path, JsonObject obj) => AtomicWrite(path, obj.ToJsonString(JsonOptions));
    // 拒绝符号链接和目录联接，保证文件操作始终落在用户选择的游戏目录内。
    public static void NoLinks(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不支持符号链接或目录联接：" + p);
    }
    public static string Game(string root)
    {
        var full = Path.GetFullPath(root.Trim().Trim('"')); NoLinks(full);
        if (!Directory.Exists(Path.Combine(full, "game")) || !Directory.Exists(Path.Combine(full, "renpy")))
            throw new IOException("请选择包含 game 和 renpy 文件夹的游戏根目录。");
        NoLinks(Path.Combine(full, "game", "live_translator"));
        return full;
    }
    public static string Data(string root) => Path.Combine(Game(root), "game", "live_translator");
    public static JsonObject Config(string root) => File.Exists(Path.Combine(Data(root), "config.json"))
        ? ReadJson(Path.Combine(Data(root), "config.json")) : Defaults();
    public static JsonObject Defaults()
    {
        var config = ReadJson(Path.Combine(Resources, "config.example.json"));
        config["api_key"] = ""; config["api_key_encrypted"] = "";
        config["protected_names"] = new JsonArray(); return config;
    }
    public static void NormalizeKey(JsonObject config, string newKey = "", bool clear = false)
    {
        var encrypted = ReadString(config, "api_key_encrypted");
        if (encrypted.Contains("请勿")) encrypted = "";
        var old = ReadString(config, "api_key");
        if (clear) encrypted = "";
        else if (!string.IsNullOrWhiteSpace(newKey)) encrypted = Secret.Protect(newKey.Trim());
        else if (encrypted.Length == 0 && old.Length > 0 && !old.Contains("填")) encrypted = Secret.Protect(old);
        config["api_key_encrypted"] = encrypted; config["api_key"] = "";
    }
    public static (string Text, int Count) Merge(string directory)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); var lines = new List<string>();
        foreach (var path in Directory.GetFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            int n = 0;
            foreach (var line in File.ReadLines(path))
            {
                n++; if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject obj;
                try { obj = JsonNode.Parse(line)!.AsObject(); }
                catch { throw new IOException($"译文 JSON 错误：{Path.GetFileName(path)}:{n}"); }
                var source = ReadString(obj, "source"); var translated = ReadString(obj, "translation");
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated) || !seen.Add(source))
                    throw new IOException($"译文为空或原文重复：{Path.GetFileName(path)}:{n}");
                lines.Add(line);
            }
        }
        if (lines.Count == 0) throw new IOException("没有可安装的译文。");
        return (string.Join("\n", lines) + "\n", lines.Count);
    }
    public static void EnsureStopped(string root)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? file = null;
                try { file = process.MainModule?.FileName; } catch { }
                if (file?.StartsWith(Game(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true)
                    throw new IOException("请先关闭该游戏，再修改汉化文件。");
            }
        }
    }
    public static readonly string[] InstalledFiles = ["zz_live_translator.rpy", "zz_live_translator.rpyc", "live_translator/pretranslated.jsonl", "live_translator/fonts/HarmonyOS_Sans_SC.ttf", "live_translator/config.json", "live_translator/installation.json"];
    // 每次写入前保存原始字节；失败时恢复。备份留在用户数据目录，卸载不会删除它。
    public static void Transaction(string root, IEnumerable<string> relative, Action action)
    {
        root = Game(root); EnsureStopped(root);
        var game = Path.Combine(root, "game"); var backup = Path.Combine(Home, "backups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup); var originals = new Dictionary<string, byte[]?>();
        foreach (var item in relative)
        {
            var path = Path.GetFullPath(Path.Combine(game, item));
            if (!path.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("非法目标路径。");
            NoLinks(path); originals[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (originals[path] is { } bytes) { var dest = Path.Combine(backup, item); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.WriteAllBytes(dest, bytes); }
        }
        AtomicWrite(Path.Combine(backup, "target.txt"), root);
        try { action(); }
        catch (Exception error)
        {
            var failed = new List<string>();
            foreach (var (path, bytes) in originals)
                try
                {
                    if (bytes is null) { if (File.Exists(path)) File.Delete(path); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
                }
                catch { failed.Add(path); }
            if (failed.Count > 0) throw new IOException("操作失败，部分文件未能自动恢复。请从此目录恢复备份：" + backup, error);
            throw;
        }
    }
    public static int Install(string root, JsonObject config, bool bundled, string font)
    {
        var merged = bundled ? Merge(Path.Combine(Resources, "translations")) : ("", 0);
        var script = File.ReadAllText(Path.Combine(Resources, "game", "zz_live_translator.rpy"));
        NormalizeKey(config);
        if (bundled) config["protected_names"] = ReadJson(Path.Combine(Resources, "config.example.json"))["protected_names"]!.DeepClone();
        config["font"] = font == "HarmonyOS" ? "live_translator/fonts/HarmonyOS_Sans_SC.ttf" : font.Replace('\\', '/');
        if (font != "HarmonyOS" && !File.Exists(font)) throw new IOException("所选字体不存在。");
        Transaction(root, InstalledFiles, () =>
        {
            var game = Path.Combine(Game(root), "game"); var data = Data(root);
            AtomicWrite(Path.Combine(game, "zz_live_translator.rpy"), script);
            if (File.Exists(Path.Combine(game, "zz_live_translator.rpyc"))) File.Delete(Path.Combine(game, "zz_live_translator.rpyc"));
            if (font == "HarmonyOS") { Directory.CreateDirectory(Path.Combine(data, "fonts")); File.Copy(Path.Combine(Resources, "fonts", "HarmonyOS_Sans_SC.ttf"), Path.Combine(data, "fonts", "HarmonyOS_Sans_SC.ttf"), true); }
            // 通用模式不覆盖用户已有译文；首装不注入其他游戏的专属资源。
            if (bundled) AtomicWrite(Path.Combine(data, "pretranslated.jsonl"), merged.Item1);
            WriteJson(Path.Combine(data, "config.json"), config);
            var hashes = new JsonObject();
            var owned = new List<string> { "zz_live_translator.rpy" };
            if (bundled) owned.Add("live_translator/pretranslated.jsonl");
            if (font == "HarmonyOS") owned.Add("live_translator/fonts/HarmonyOS_Sans_SC.ttf");
            foreach (var item in owned) if (File.Exists(Path.Combine(game, item))) hashes[item] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(game, item))));
            WriteJson(Path.Combine(data, "installation.json"), new JsonObject { ["manager_version"] = ManagerVersion, ["resource_version"] = File.ReadAllText(Path.Combine(Resources, "version.txt")).Trim(), ["pack"] = bundled ? "camp-buddy-scoutmaster" : "custom", ["files"] = hashes });
        });
        return merged.Item2;
    }
    public static void SaveConfig(string root, JsonObject config) => Transaction(root, ["live_translator/config.json"], () => WriteJson(Path.Combine(Data(root), "config.json"), config));
    public static void Uninstall(string root, bool removeData)
    {
        var files = InstalledFiles.ToList();
        if (removeData && Directory.Exists(Data(root)))
        {
            void Collect(string dir)
            {
                NoLinks(dir);
                foreach (var file in Directory.GetFiles(dir)) { NoLinks(file); files.Add(Path.GetRelativePath(Path.Combine(Game(root), "game"), file)); }
                foreach (var sub in Directory.GetDirectories(dir)) Collect(sub);
            }
            Collect(Data(root));
        }
        // 常规卸载只移除执行模组，保留译文、字体及用户数据，兼容旧版卸载语义。
        var targets = removeData ? files.Distinct().ToArray() : new[] { "zz_live_translator.rpy", "zz_live_translator.rpyc", "live_translator/installation.json" };
        Transaction(root, targets, () => { foreach (var item in targets) { var path = Path.Combine(Game(root), "game", item); if (File.Exists(path)) File.Delete(path); } });
    }
    public static string Status(string root)
    {
        var script = Path.Combine(Game(root), "game", "zz_live_translator.rpy");
        if (!File.Exists(script)) return "未安装汉化";
        var manifest = Path.Combine(Data(root), "installation.json");
        if (!File.Exists(manifest)) return "已安装旧版汉化 · 可直接升级";
        var obj = ReadJson(manifest); int changed = 0;
        foreach (var pair in obj["files"]!.AsObject())
        {
            if (!InstalledFiles.Contains(pair.Key)) continue;
            var path = Path.Combine(Game(root), "game", pair.Key); NoLinks(path);
            if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != pair.Value!.GetValue<string>()) changed++;
        }
        return $"已安装 · 资源 {ReadString(obj, "resource_version")} · " + (changed == 0 ? "文件完整" : $"{changed} 个文件需要修复");
    }
}

public static class Secret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    // 与旧 PowerShell ProtectedData.CurrentUser 使用相同的 DPAPI 格式及空熵。
    private static byte[] Convert(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var ok = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("密钥加密/解密失败，请在原 Windows 用户下操作或重新填写密钥。");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    public static string Protect(string text) => System.Convert.ToBase64String(Convert(Encoding.UTF8.GetBytes(text), true));
    public static string Unprotect(string text) => Encoding.UTF8.GetString(Convert(System.Convert.FromBase64String(text), false));
}
