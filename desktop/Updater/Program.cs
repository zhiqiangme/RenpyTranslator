using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
    private static void NoLinks(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新目标包含目录联接或符号链接。");
    }
    private static int Main(string[] args)
    {
        if (args is ["--self-test"]) return SelfTest();
        var originals = new Dictionary<string, string?>();
        FileStream? lease = null;
        try
        {
            if (args.Length != 3) throw new IOException("更新参数不完整。");
            var target = Path.GetFullPath(args[1]); var stage = Path.GetFullPath(args[2]); NoLinks(target); NoLinks(stage);
            if (!File.Exists(Path.Combine(target, "RenpyTranslator.exe"))) throw new IOException("目标不是桌面程序目录。");
            // 最多等待两分钟，避免因退出失败而永久驻留。
            try { using var parent = Process.GetProcessById(int.Parse(args[0])); if (!parent.WaitForExit(120000)) throw new IOException("管理器未退出，更新已取消。"); } catch (ArgumentException) { }
            // 接管管理器已释放的租约，替换和回滚期间禁止新进程回收此更新目录。
            lease = new FileStream(Path.Combine(Path.GetDirectoryName(stage)!, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var backup = Path.Combine(Path.GetDirectoryName(stage)!, "previous"); Directory.CreateDirectory(backup);
            // 先移走新版本不再包含的资源，避免旧译文残留导致重复或错误匹配。
            var resourceRoot = Path.Combine(target, "Resources");
            if (Directory.Exists(resourceRoot)) foreach (var old in Directory.GetFiles(resourceRoot, "*", SearchOption.AllDirectories))
            {
                NoLinks(old); var relative = Path.GetRelativePath(target, old);
                if (File.Exists(Path.Combine(stage, relative))) continue;
                var saved = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(old, saved);
                originals[old] = saved; File.Delete(old);
            }
            foreach (var source in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
            {
                NoLinks(source); var relative = Path.GetRelativePath(stage, source);
                // 发行包仅允许程序和内置资源，不接受用户配置或游戏目录。
                if (!(relative.StartsWith("Resources" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || relative is "RenpyTranslator.exe" or "RenpyTranslator.Updater.exe")) throw new IOException("更新包包含非发行文件：" + relative);
                var destination = Path.GetFullPath(Path.Combine(target, relative));
                if (!destination.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("非法更新路径。");
                NoLinks(destination);
                string? saved = null;
                if (File.Exists(destination)) { saved = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(destination, saved); }
                originals[destination] = saved;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true);
            }
            if (!testing) Process.Start(new ProcessStartInfo(Path.Combine(target, "RenpyTranslator.exe")) { UseShellExecute = true }); return 0;
        }
        catch (Exception ex)
        {
            var recovery = new List<string>();
            foreach (var (path, saved) in originals)
                try { if (saved is null) { if (File.Exists(path)) File.Delete(path); } else File.Copy(saved, path, true); } catch { recovery.Add(path); }
            if (!testing) MessageBox(IntPtr.Zero, "更新失败：" + ex.Message + (recovery.Count == 0 ? "\n已恢复被修改文件。" : "\n部分文件未能恢复，请使用更新目录中的 previous 备份：\n" + string.Join("\n", recovery)), "Ren'Py 汉化管理器", 0x10); return 1;
        }
        finally { lease?.Dispose(); }
    }
    private static bool testing;
    // 与管理器自检一致：tests 下的模拟目录按创建时间只保留最近若干份，避免逐次自检无限堆积。
    private const int TestRetention = 4;
    /// <summary>按创建时间删除超出保留份数的旧测试目录；失败一律忽略，不影响自检流程。</summary>
    private static void PruneTests(string home)
    {
        var root = Path.Combine(home, "tests");
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
    private static int SelfTest()
    {
        testing = true;
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenpyTranslator");
        var root = Path.Combine(home, "tests", "updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); PruneTests(home); var log = new List<string>();
        try
        {
            void Fixture(string dir, bool broken)
            {
                var target = Path.Combine(dir, "target"); var stage = Path.Combine(dir, "stage");
                Directory.CreateDirectory(Path.Combine(target, "Resources")); Directory.CreateDirectory(Path.Combine(stage, "Resources"));
                File.WriteAllText(Path.Combine(target, "RenpyTranslator.exe"), "old");
                File.WriteAllText(Path.Combine(target, "Resources", "obsolete.jsonl"), "old resource");
                File.WriteAllText(Path.Combine(target, "user-settings.txt"), "private");
                File.WriteAllText(Path.Combine(stage, "RenpyTranslator.exe"), "new");
                File.WriteAllText(Path.Combine(stage, "Resources", "new.jsonl"), "new resource");
                if (broken) File.WriteAllText(Path.Combine(stage, "unexpected.txt"), "reject");
                int result = Main([int.MaxValue.ToString(), target, stage]);
                if (broken)
                {
                    if (result != 1 || File.ReadAllText(Path.Combine(target, "RenpyTranslator.exe")) != "old" || !File.Exists(Path.Combine(target, "Resources", "obsolete.jsonl")) || File.Exists(Path.Combine(target, "Resources", "new.jsonl"))) throw new Exception("Rollback failed");
                    log.Add("PASS Update failure rolls back changed and deleted files");
                }
                else
                {
                    if (result != 0 || File.ReadAllText(Path.Combine(target, "RenpyTranslator.exe")) != "new" || File.Exists(Path.Combine(target, "Resources", "obsolete.jsonl")) || !File.Exists(Path.Combine(target, "Resources", "new.jsonl"))) throw new Exception("Replacement failed");
                    log.Add("PASS Update replaces executable and removes obsolete bundled resources");
                }
                if (File.ReadAllText(Path.Combine(target, "user-settings.txt")) != "private") throw new Exception("Private data changed");
                log.Add("PASS Update preserves unrelated files");
            }
            Fixture(Path.Combine(root, "success"), false); Fixture(Path.Combine(root, "failure"), true);
            File.WriteAllLines(Path.Combine(home, "updater-test.log"), log); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(home, "updater-test.log"), log); return 1; }
    }
}
