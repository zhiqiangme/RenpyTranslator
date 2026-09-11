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
    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("RenpyTranslator/" + Core.ManagerVersion); return client;
    }
    public static async Task<Release> Check()
    {
        using var client = Client();
        using var response = await client.GetAsync("https://api.github.com/repos/zhiqiangme/renpy-translator/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(false, "仓库尚未发布桌面版本。", "", "");
        response.EnsureSuccessStatusCode();
        var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var tag = Core.ReadString(obj, "tag_name");
        var notes = tag + "\n\n" + Core.ReadString(obj, "body");
        var assets = obj["assets"]!.AsArray();
        string Url(string name) => assets.FirstOrDefault(x => x?["name"]?.GetValue<string>() == name)?["browser_download_url"]?.GetValue<string>() ?? "";
        var zip = Url(AssetName); var hash = Url(AssetName + ".sha256");
        if (zip.Length == 0 || hash.Length == 0) return new(false, notes + "\n\n此发行版没有桌面包及校验文件，不能安装。", "", "");
        var version = tag.Replace("desktop-v", "").TrimStart('v');
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
            if (!url.StartsWith("https://github.com/zhiqiangme/renpy-translator/releases/download/", StringComparison.Ordinal)) throw new IOException("更新资源来源不匹配。");
        var root = Path.Combine(Core.Home, "updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
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
