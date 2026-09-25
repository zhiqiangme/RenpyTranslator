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
    public static readonly string BundledTranslations = Path.Combine(Resources, "translations", "camp-buddy-scoutmaster");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string ReadString(JsonObject obj, string key)
    {
        if (obj[key] is null) return "";
        if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new IOException($"配置字段 {key} 必须是字符串。");
    }
    public static JsonObject ReadJson(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new IOException($"配置文件必须是 JSON 对象：{Path.GetFileName(path)}"); }
        catch (JsonException ex) { throw new IOException($"配置 JSON 错误：{Path.GetFileName(path)}，第 {(ex.LineNumber ?? 0) + 1} 行。", ex); }
    }
    public static JsonArray StringArray(string text, string key)
    {
        var label = key == "skip_patterns" ? "跳过规则" : "保护人名";
        try
        {
            var array = JsonNode.Parse(text) as JsonArray ?? throw new IOException($"{label}必须是 JSON 字符串数组。");
            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var entry)) throw new IOException($"{label}的每个元素必须是字符串。");
                if (key == "skip_patterns") ValidateSkipPattern(entry);
            }
            return array;
        }
        catch (JsonException ex) { throw new IOException($"{label} JSON 格式错误，第 {(ex.LineNumber ?? 0) + 1} 行。", ex); }
        catch (ArgumentException ex) { throw new IOException($"{label}包含无效的正则表达式。", ex); }
    }
    // 发行包不依赖 Python，限定为 .NET 与游戏 Python re 都支持的基础语法。
    internal static void ValidateSkipPattern(string pattern)
    {
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            if (ch == '\\' && i + 1 < pattern.Length)
            {
                char escaped = pattern[++i];
                if (char.IsLetterOrDigit(escaped) && !"dDsSwWbBAnrtfav".Contains(escaped))
                    throw new IOException("跳过规则使用基础正则语法，不支持命名组、反向引用或扩展转义；请直接填写文字或使用字符类。");
                continue;
            }
            if (ch == '[') { if (inClass) throw new IOException("跳过规则不支持嵌套字符类，请转义字面量方括号。"); inClass = true; }
            else if (ch == ']') inClass = false;
            else if (!inClass && ch == '{')
            {
                int end = pattern.IndexOf('}', i + 1);
                if (end < 0 || !System.Text.RegularExpressions.Regex.IsMatch(pattern[(i + 1)..end], @"^[0-9]+(,[0-9]*)?$"))
                    throw new IOException("跳过规则的次数限定请使用 {n}、{n,} 或 {n,m}；字面量花括号请转义。");
                i = end;
            }
            else if (!inClass && ch == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?'
                && (i + 2 >= pattern.Length || pattern[i + 2] != ':'))
                throw new IOException("跳过规则仅支持普通组和 (?:...) 非捕获组，不支持命名组、前后查找或内联选项。");
        }
        _ = new System.Text.RegularExpressions.Regex(pattern);
    }
    // 系统字体预设顺序：黑体为默认，其后雅黑、宋体。
    public static readonly string[] SystemFonts = ["simhei.ttf", "msyh.ttc", "simsun.ttc"];
    // 旧版随模组内置的鸿蒙字体；新版改用系统字体，安装时据此清理游戏目录中的残留副本。
    public const string LegacyBundledFont = "live_translator/fonts/HarmonyOS_Sans_SC.ttf";
    private static readonly string[] LegacyBundledFonts = [LegacyBundledFont];
    /// <summary>默认系统字体：优先黑体，本机缺失时回退雅黑、宋体，避免默认安装因字体缺失而失败。</summary>
    public static string DefaultFont()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in SystemFonts) { var path = Path.Combine(fonts, name); if (File.Exists(path)) return path.Replace('\\', '/'); }
        return Path.Combine(fonts, SystemFonts[0]).Replace('\\', '/');
    }
    // 只识别完整预设路径，自定义字体不能因文件名包含 msyh 等字样而被替换。
    public static int FontPreset(string font)
    {
        var normalized = font.Replace('\\', '/');
        // 未配置字体或仍是旧版内置字体时，都归入默认预设（系统黑体）。
        if (string.IsNullOrWhiteSpace(font) || normalized.Equals(LegacyBundledFont, StringComparison.OrdinalIgnoreCase)) return 0;
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts).Replace('\\', '/');
        for (var index = 0; index < SystemFonts.Length; index++)
            if (normalized.Equals(fonts + "/" + SystemFonts[index], StringComparison.OrdinalIgnoreCase)) return index;
        return 3;
    }
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
        return Path.TrimEndingDirectorySeparator(full);
    }
    public static string Data(string root) => Path.Combine(Game(root), "game", "live_translator");
    public static JsonObject Config(string root) => File.Exists(Path.Combine(Data(root), "config.json"))
        ? ReadJson(Path.Combine(Data(root), "config.json")) : Defaults();
    public static JsonObject Defaults()
    {
        var config = ReadJson(Path.Combine(Resources, "config.default.json"));
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
    public static (string Text, int Count) Merge(string directory, bool recursive = false)
    {
        directory = TranslationDirectory(directory);
        var seen = new HashSet<string>(StringComparer.Ordinal); var lines = new List<string>();
        // 逐层检查目录联接，避免递归导入跳出所选目录或陷入循环。
        IEnumerable<string> Files(string folder)
        {
            NoLinks(folder);
            foreach (var file in Directory.GetFiles(folder, "*.jsonl")) yield return file;
            if (recursive) foreach (var child in Directory.GetDirectories(folder)) foreach (var file in Files(child)) yield return file;
        }
        foreach (var path in Files(directory).Order(StringComparer.Ordinal))
        {
            NoLinks(path);
            int n = 0;
            foreach (var line in File.ReadLines(path))
            {
                n++; if (string.IsNullOrWhiteSpace(line)) continue;
                string source, translated;
                try
                {
                    var obj = JsonNode.Parse(line) as JsonObject ?? throw new IOException("译文必须是 JSON 对象。");
                    source = ReadString(obj, "source"); translated = ReadString(obj, "translation");
                }
                catch (Exception ex) when (ex is JsonException or IOException) { throw new IOException($"译文 JSON 错误：{Path.GetRelativePath(directory, path)}:{n}", ex); }
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated) || !seen.Add(source))
                    throw new IOException($"译文为空或原文重复：{Path.GetRelativePath(directory, path)}:{n}");
                lines.Add(line);
            }
        }
        if (lines.Count == 0) throw new IOException("没有可安装的译文。");
        return (string.Join("\n", lines) + "\n", lines.Count);
    }
    public static string TranslationDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("请选择自定义汉化包文件夹。");
        var full = Path.GetFullPath(directory.Trim().Trim('"')); NoLinks(full);
        if (!Directory.Exists(full)) throw new IOException("汉化包文件夹不存在，请重新选择。");
        return Path.TrimEndingDirectorySeparator(full);
    }
    public static void EnsureStopped(string root)
    {
        root = Game(root);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? file = null;
                try { file = process.MainModule?.FileName; } catch { }
                if (file is not null && IsInside(root, file))
                    throw new IOException("请先关闭该游戏，再修改汉化文件。");
            }
        }
    }
    // 相对路径判断同时兼容尾随分隔符与盘符根目录，并排除同名前缀的相邻目录。
    internal static bool IsInside(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    public static readonly string[] InstalledFiles = ["zz_live_translator.rpy", "zz_live_translator.rpyc", "zz_live_translator_camp_buddy.rpy", "zz_live_translator_camp_buddy.rpyc", "live_translator/pretranslated.jsonl", "live_translator/config.json", "live_translator/installation.json"];
    // 备份保留份数。单次事务会把待覆盖文件整份备份，译文与旧版内置字体可达 20MB 量级，
    // 不设上限时 backups 目录会随每次安装/自检持续堆积，故只保留最近若干份。
    private const int BackupRetention = 10;
    /// <summary>按创建时间删除超出保留份数的旧备份；失败一律忽略，不影响安装流程。</summary>
    private static void PruneBackups()
    {
        var root = Path.Combine(Home, "backups");
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetCreationTimeUtc).Skip(BackupRetention))
            {
                try { Directory.Delete(dir, true); }
                catch (IOException) { }                  // 被占用时留到下次清理
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
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
        finally { PruneBackups(); }
    }
    public static int Install(string root, JsonObject config, bool bundled, string font, string? customDirectory = null)
    {
        if (bundled && customDirectory is not null) throw new IOException("内置译文和自定义汉化包不能同时安装。");
        var source = customDirectory is null ? null : TranslationDirectory(customDirectory);
        // 自定义包包含所选目录的子目录，全部校验完成后才修改游戏。
        var merged = source is not null ? Merge(source, true) : bundled ? Merge(BundledTranslations) : ("", 0);
        bool importsTranslations = bundled || source is not null;
        var script = File.ReadAllText(Path.Combine(Resources, "game", "zz_live_translator.rpy"));
        NormalizeKey(config);
        if (bundled) config["protected_names"] = ReadJson(Path.Combine(Resources, "config.default.json"))["protected_names"]!.DeepClone();
        // 未指定字体时使用默认系统字体；显式路径必须真实存在，避免写入游戏后无法生效。
        var fontPath = string.IsNullOrWhiteSpace(font) ? DefaultFont() : font.Replace('\\', '/');
        config["font"] = fontPath;
        if (!File.Exists(Path.IsPathRooted(fontPath) ? fontPath : Path.Combine(Game(root), "game", fontPath))) throw new IOException("所选字体不存在。");
        Transaction(root, InstalledFiles.Concat(LegacyBundledFonts), () =>
        {
            var game = Path.Combine(Game(root), "game"); var data = Data(root);
            AtomicWrite(Path.Combine(game, "zz_live_translator.rpy"), script);
            if (File.Exists(Path.Combine(game, "zz_live_translator.rpyc"))) File.Delete(Path.Combine(game, "zz_live_translator.rpyc"));
            // 专属屏幕独立安装；切回通用模式时一并清除其编译缓存，恢复原游戏屏幕。
            var confirm = Path.Combine(game, "zz_live_translator_camp_buddy.rpy");
            if (bundled) AtomicWrite(confirm, File.ReadAllText(Path.Combine(Resources, "game", "zz_live_translator_camp_buddy.rpy")));
            else if (File.Exists(confirm)) File.Delete(confirm);
            if (File.Exists(confirm + "c")) File.Delete(confirm + "c");
            // 旧版内置字体不再随包分发，安装时清理游戏目录中的残留副本；目录为空时一并移除，用户自备字体的目录保留。
            foreach (var item in LegacyBundledFonts) { var legacy = Path.Combine(game, item); if (File.Exists(legacy)) File.Delete(legacy); }
            var legacyDirectory = Path.Combine(data, "fonts");
            if (Directory.Exists(legacyDirectory) && !Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
                try { Directory.Delete(legacyDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // 通用模式不覆盖用户已有译文；首装不注入其他游戏的专属资源。
            if (importsTranslations) AtomicWrite(Path.Combine(data, "pretranslated.jsonl"), merged.Item1);
            WriteJson(Path.Combine(data, "config.json"), config);
            var hashes = new JsonObject();
            var owned = new List<string> { "zz_live_translator.rpy" };
            if (bundled) owned.Add("zz_live_translator_camp_buddy.rpy");
            if (importsTranslations) owned.Add("live_translator/pretranslated.jsonl");
            foreach (var item in owned) if (File.Exists(Path.Combine(game, item))) hashes[item] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(game, item))));
            WriteJson(Path.Combine(data, "installation.json"), new JsonObject { ["manager_version"] = ManagerVersion, ["resource_version"] = File.ReadAllText(Path.Combine(Resources, "version.txt")).Trim(), ["pack"] = bundled ? "camp-buddy-scoutmaster" : source is not null ? "folder" : "custom", ["custom_directory"] = source ?? "", ["files"] = hashes });
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
        var targets = removeData ? files.Distinct().ToArray() : new[] { "zz_live_translator.rpy", "zz_live_translator.rpyc", "zz_live_translator_camp_buddy.rpy", "zz_live_translator_camp_buddy.rpyc", "live_translator/installation.json" };
        Transaction(root, targets, () => { foreach (var item in targets) { var path = Path.Combine(Game(root), "game", item); if (File.Exists(path)) File.Delete(path); } });
    }
    public static string Status(string root) => InspectInstallation(root).Status;
    public record InstallationState(string Status, bool Bundled, bool CustomPack = false, string CustomDirectory = "");
    public static InstallationState InspectInstallation(string root)
    {
        var script = Path.Combine(Game(root), "game", "zz_live_translator.rpy");
        var manifest = Path.Combine(Data(root), "installation.json");
        if (!File.Exists(manifest)) return new(File.Exists(script) ? "已安装旧版汉化 · 可直接升级" : "未安装汉化", false);
        NoLinks(manifest);
        JsonObject? obj;
        // 仅将记录格式错误降级为可修复状态；权限和链接错误仍阻止操作。
        try { obj = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject; }
        catch (JsonException) { obj = null; }
        var pack = obj?["pack"] is JsonValue packValue && packValue.TryGetValue<string>(out var packText) ? packText : "";
        bool bundled = pack == "camp-buddy-scoutmaster";
        bool custom = pack == "folder";
        var source = obj?["custom_directory"] is JsonValue sourceValue && sourceValue.TryGetValue<string>(out var sourceText) ? sourceText : "";
        InstallationState Damaged() => new("安装记录损坏或不完整 · 可直接安装 / 修复，请确认资源模式", bundled, custom, source);
        if (obj is null || pack is not ("custom" or "camp-buddy-scoutmaster" or "folder") || obj["files"] is not JsonObject hashes) return Damaged();
        if (obj["resource_version"] is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var version) || !Version.TryParse(version, out _)) return Damaged();
        var required = new List<string> { "zz_live_translator.rpy" };
        if (bundled) required.AddRange(["zz_live_translator_camp_buddy.rpy", "live_translator/pretranslated.jsonl"]);
        if (custom) required.Add("live_translator/pretranslated.jsonl");
        if (required.Any(key => !hashes.ContainsKey(key))) return Damaged();
        int changed = 0;
        foreach (var pair in hashes)
        {
            if (!InstalledFiles.Contains(pair.Key)) continue;
            var path = Path.Combine(Game(root), "game", pair.Key); NoLinks(path);
            if (pair.Value is not JsonValue value || !value.TryGetValue<string>(out var expected) || expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return Damaged();
            if (!File.Exists(path) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(expected, StringComparison.OrdinalIgnoreCase)) changed++;
        }
        return new($"已安装 · 资源 {version} · " + (changed == 0 ? "文件完整" : $"{changed} 个文件需要修复"), bundled, custom, source);
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
