using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace RenpyTranslator;

public record Release(bool Available, string Notes, string ZipUrl, string HashUrl);
public static class Updates
{
    private const string AssetName = "RenpyTranslator-win-x64.zip";
    // 保留最近两份供回退；一天内的目录不动，覆盖下载到更新器接管之间的交接窗口。
    internal static void PruneUpdates(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            Core.NoLinks(root);
            var directories = Directory.GetDirectories(root)
                .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
                .OrderByDescending(Directory.GetCreationTimeUtc).Skip(2);
            foreach (var directory in directories)
            {
                if (Directory.GetCreationTimeUtc(directory) > DateTime.UtcNow.AddDays(-1)) continue;
                try
                {
                    // 只清理受管理目录，拒绝链接；独占租约防止清理正在使用的备份。
                    void CheckTree(string path)
                    {
                        Core.NoLinks(path);
                        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                        {
                            Core.NoLinks(entry);
                            if (Directory.Exists(entry)) CheckTree(entry);
                        }
                    }
                    CheckTree(directory);
                    using var lease = new FileStream(Path.Combine(directory, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
                    Directory.Delete(directory, true);
                }
                catch (IOException) { } // 占用或无权限时留到下次启动清理。
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("RenpyTranslator/" + Core.ManagerVersion); return client;
    }
    // 桌面发行标签形如 v26.9.11；只认 v + 数字开头，避免匹配非版本标签。
    private static bool IsReleaseTag(string tag) => tag.Length > 1 && tag[0] == 'v' && char.IsDigit(tag[1]);
    public static async Task<Release> Check()
    {
        using var client = Client();
        // 桌面发行统一打 v* 标签（如 v26.9.11）。仍拉取列表而非 /releases/latest，以便跳过预发布并按版本号比较新旧。
        using var response = await client.GetAsync("https://api.github.com/repos/zhiqiangme/RenpyTranslator/releases?per_page=20");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(false, "仓库尚未发布桌面版本。", "", "");
        response.EnsureSuccessStatusCode();
        // 列表按创建时间倒序，第一个非预发行的 v* 即最新桌面版。
        var latest = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Select(node => node?.AsObject())
            .FirstOrDefault(obj => obj != null
                && IsReleaseTag(Core.ReadString(obj, "tag_name"))
                && obj["prerelease"]?.GetValue<bool>() != true);
        if (latest is null) return new(false, "仓库尚未发布桌面版本。", "", "");
        var tag = Core.ReadString(latest, "tag_name");
        var notes = tag + "\n\n" + Core.ReadString(latest, "body");
        var assets = latest["assets"]!.AsArray();
        string Url(string name) => assets.FirstOrDefault(x => x?["name"]?.GetValue<string>() == name)?["browser_download_url"]?.GetValue<string>() ?? "";
        var zip = Url(AssetName); var hash = Url(AssetName + ".sha256");
        if (zip.Length == 0 || hash.Length == 0) return new(false, notes + "\n\n此发行版没有桌面包及校验文件，不能安装。", "", "");
        var version = tag.TrimStart('v');
        bool newer = Version.TryParse(version, out var remote) && remote > Version.Parse(Core.ManagerVersion);
        return new(newer, notes, zip, hash);
    }
    public static void Extract(string zip, string target)
    {
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!path.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':')) throw new IOException("更新包包含非法路径。");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, false);
        }
    }
    public static async Task Stage(Release release)
    {
        if (!release.Available) throw new IOException("没有可安装的更新。");
        foreach (var url in new[] { release.ZipUrl, release.HashUrl })
            if (!url.StartsWith("https://github.com/zhiqiangme/RenpyTranslator/releases/download/", StringComparison.Ordinal)) throw new IOException("更新资源来源不匹配。");
        var updates = Path.Combine(Core.Home, "updates"); PruneUpdates(updates);
        var root = Path.Combine(updates, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var client = Client(); var zip = Path.Combine(root, "release.zip");
        await using (var source = await client.GetStreamAsync(release.ZipUrl))
        await using (var dest = File.Create(zip)) await source.CopyToAsync(dest);
        var expected = (await client.GetStringAsync(release.HashUrl)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        using (var stream = File.OpenRead(zip)) if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新包 SHA-256 不匹配，已停止更新。");
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage); Extract(zip, stage);
        if (!File.Exists(Path.Combine(stage, "RenpyTranslator.exe")) || !File.Exists(Path.Combine(stage, "Resources", "game", "zz_live_translator.rpy"))) throw new IOException("更新包结构不完整。");
        // 使用当前版本的独立更新器，避免覆盖正在运行的进程。
        var helper = Path.Combine(root, "RenpyTranslator.Updater.exe"); File.Copy(Path.Combine(AppContext.BaseDirectory, "RenpyTranslator.Updater.exe"), helper);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { Environment.ProcessId.ToString(), AppContext.BaseDirectory, stage }) start.ArgumentList.Add(arg);
        _ = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
    }
}
