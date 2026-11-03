using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JuniGrid.Services;

/// <summary>
/// Locates the Stardew Valley install and probes SMAPI metadata.
/// </summary>
public sealed class GameService
{
    /// <summary>星露谷的 Steam AppID（配置里 SteamAppId 的默认值）。</summary>
    public const string DefaultSteamAppId = "413150";

    /// <summary>GOG 版星露谷在注册表里的游戏编号。</summary>
    private const string GogGameId = "1453375253";

    /// <summary>一次自动检测的总预算。候选是**并行**探的，正常几毫秒全部返回；
    /// 只有碰上「盘符还在、设备没就绪」的盘才会吃满这个预算，之后直接当没找到 ——
    /// 绝不学旧版那样一条条同步等到设备超时（实测能拖到分钟级）。</summary>
    private const int ProbeBudgetMs = 900;

    private string? _cachedPath;
    private DateTime _cachedAt;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private readonly object _cacheGate = new();

    /// <summary>自动检测游戏目录，返回 "" = 没找到。
    /// <para><b>候选来源按可信度排</b>：① 注册表 SteamPath → steamapps\libraryfolders.vdf 里的
    /// 全部库（覆盖装在自定义库里的情况，只读一两个小文件）② Steam 卸载项的 InstallLocation
    /// ③ GOG 注册表 ④ 老的写死路径兜底。</para>
    /// <para><b>网络盘 / 未就绪盘一律不进探测队列</b>：在断开的映射盘或拔掉的移动盘上做一次
    /// <c>Directory.Exists</c> 会阻塞到设备超时（实测几十秒），旧版按 9 条写死路径串行探、
    /// 全部落空时能把「自动检测」拖成分钟级 —— 这就是用户报的「超级超级慢」。</para>
    /// <para>命中结果缓存 10 分钟；没找到不缓存，用户插回盘符后可立刻重试。
    /// <paramref name="refresh"/> = 用户手动点「自动检测」，绕开缓存重探一遍。</para></summary>
    public string DetectGamePath(string appId = DefaultSteamAppId, bool refresh = false)
    {
        if (!refresh)
        {
            lock (_cacheGate)
            {
                if (_cachedPath is not null && DateTime.UtcNow - _cachedAt < CacheTtl) return _cachedPath;
            }
        }

        var found = ProbeCandidates(BuildCandidates(appId)) ?? "";

        if (found.Length > 0)
        {
            lock (_cacheGate)
            {
                _cachedPath = found;
                _cachedAt = DateTime.UtcNow;
            }
        }
        return found;
    }

    /// <summary>UI 侧专用：整体丢线程池，点「自动检测」不再堵渲染线程。</summary>
    public Task<string> DetectGamePathAsync(string appId = DefaultSteamAppId, bool refresh = false)
        => Task.Run(() => DetectGamePath(appId, refresh));

    // ------------------------------------------------------------------
    // 候选路径：从「真实安装记录」里取，而不是硬猜盘符
    // ------------------------------------------------------------------

    private static List<string> BuildCandidates(string appId)
    {
        var list = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (full.Length == 0 || list.Contains(full, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(full);
        }

        // ① Steam：注册表里的 SteamPath + 它的库列表（libraryfolders.vdf 才列出全部库）
        var steamRoot = SteamService.FindSteamPath();
        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            Add(Path.Combine(steamRoot, "steamapps", "common", "Stardew Valley"));
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (TryReadText(vdf) is { Length: > 0 } text)
                foreach (var lib in ParseLibraryFolders(text))
                    Add(Path.Combine(lib, "steamapps", "common", "Stardew Valley"));
        }

        // ② Steam 给每个已装游戏写的卸载项：InstallLocation 直接就是游戏目录
        Add(ReadSteamInstallLocation(appId));

        // ③ GOG 版
        Add(ReadGogInstallPath());

        // ④ 兜底：旧版写死的常见位置（Steam 注册表读不到时仍能命中）
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(@"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley");
        Add(@"C:\Program Files\Steam\steamapps\common\Stardew Valley");
        Add(@"D:\Steam\steamapps\common\Stardew Valley");
        Add(@"D:\SteamLibrary\steamapps\common\Stardew Valley");
        Add(@"E:\Steam\steamapps\common\Stardew Valley");
        Add(@"E:\SteamLibrary\steamapps\common\Stardew Valley");
        Add(@"F:\SteamLibrary\steamapps\common\Stardew Valley");
        Add(Path.Combine(home, @"AppData\Local\Programs\Stardew Valley"));
        Add(@"C:\GOG Games\Stardew Valley");

        return list;
    }

    /// <summary>解析 libraryfolders.vdf 里的库路径：新格式 <c>"path"  "D:\\SteamLibrary"</c>，
    /// 老格式 <c>"1"  "D:\\SteamLibrary"</c>，两种都认；vdf 里反斜杠是转义过的（\\），读出来还原。</summary>
    internal static IEnumerable<string> ParseLibraryFolders(string vdfText)
    {
        foreach (Match m in Regex.Matches(vdfText, "\"path\"\\s*\"([^\"]+)\""))
        {
            var p = UnescapeVdfPath(m.Groups[1].Value);
            if (p.Length > 0) yield return p;
        }
        foreach (Match m in Regex.Matches(vdfText, "\"\\d+\"\\s*\"([A-Za-z]:[^\"]*)\""))
        {
            var p = UnescapeVdfPath(m.Groups[1].Value);
            if (p.Length > 0) yield return p;
        }
    }

    private static string UnescapeVdfPath(string v) => v.Replace("\\\\", "\\").Trim();

    /// <summary>并行探候选，整体最多等 <see cref="ProbeBudgetMs"/>；按候选顺序取第一个命中
    /// （并行只影响「谁先返回」，不影响谁优先）。</summary>
    private static string? ProbeCandidates(List<string> candidates)
    {
        // 先按「盘就绪」粗筛：不就绪的盘连 Directory.Exists 都不做
        var probeable = candidates.Where(DriveUsable).ToList();
        if (probeable.Count == 0) return null;

        var tasks = probeable.Select(p => Task.Run(() => ProbeGameDir(p))).ToArray();
        try { Task.WhenAll(tasks).Wait(ProbeBudgetMs); } catch { }

        foreach (var t in tasks)
            if (t.IsCompletedSuccessfully && t.Result is { Length: > 0 } hit) return hit;
        return null;
    }

    private static string? ProbeGameDir(string dir)
    {
        try
        {
            // 只认真正装了游戏的目录（有本体 exe）；残留的空目录直接跳过
            return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "Stardew Valley.exe"))
                ? dir : null;
        }
        catch { return null; }
    }

    /// <summary>这个路径所在的盘现在能不能安全探一下。
    /// <para><c>Directory.Exists</c> 碰到「盘符还在、设备不在」的盘（断开的网络映射盘、拔掉的
    /// 移动硬盘、空读卡器）会一直卡到设备超时，实测几十秒一次 —— 必须提前拦掉。</para>
    /// <para>判据只用不触发设备 IO 的 <see cref="DriveInfo.DriveType"/>：网络盘/光驱/无根目录直接
    /// 排除；剩下的才问 IsReady。UNC 路径同样排除（探测等于走网络）。</para></summary>
    internal static bool DriveUsable(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || root.Length < 2) return false;
            var drive = new DriveInfo(root);
            if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.CDRom)
                return false;
            return drive.IsReady;
        }
        catch { return false; }
    }

    private static string? ReadSteamInstallLocation(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        var sub = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {appId}";
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(sub);
                if (key?.GetValue("InstallLocation") is string p && !string.IsNullOrWhiteSpace(p))
                    return p;
            }
            catch { /* 无权限/无此项：继续看别的来源 */ }
        }
        return null;
    }

    private static string? ReadGogInstallPath()
    {
        foreach (var sub in new[]
                 {
                     $@"SOFTWARE\WOW6432Node\GOG.com\Games\{GogGameId}",
                     $@"SOFTWARE\GOG.com\Games\{GogGameId}",
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(sub);
                if (key?.GetValue("path") is string p && !string.IsNullOrWhiteSpace(p)) return p;
            }
            catch { /* 同上 */ }
        }
        return null;
    }

    private static string? TryReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    /// <summary>本机 SMAPI 的版本号。
    /// <para><b>权威源是 <c>StardewModdingAPI.dll</c>（真正被加载的程序集）</b>：SMAPI 启动时
    /// 打印的 "SMAPI 4.5.2" 就是它的程序集版本。游戏根下那两个 166 KB 的启动壳
    /// （<c>StardewModdingAPI.exe</c> / <c>Stardew Valley.exe</c>）只是宿主，它们的版本资源是
    /// <b>装它们那一刻的安装包版本</b>，升级 SMAPI 时官方安装载荷（install.dat）根本不含 exe
    /// （实测 4.5.2：载荷里只有 dll + smapi-internal），所以壳的版本资源会永远停在旧版。</para>
    /// <para>实机 2026-10-29：装好 4.5.2 后 dll=4.5.2，而 <c>Stardew Valley.exe</c> 的版本资源
    /// 还写着 4.5.1（它的 mtime 是几天前那次安装）。先前按 exe 读会报 4.5.2、按两个壳取较低
    /// 又永远报 4.5.1 —— 两边都是读了壳、没读 dll。</para>
    /// <para>只有老安装（2.x/3.x 没有独立 dll）才退回读启动器 exe 的版本资源。</para></summary>
    public string? ProbeSmapiVersion(string gamePath, bool freshRead = false)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return null;

        var dll = Path.Combine(gamePath, "StardewModdingAPI.dll");
        if (File.Exists(dll) && ReadAssemblyVersion(dll, freshRead) is { } managed) return managed;

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        if (File.Exists(exe) && ReadExeVersion(exe, freshRead) is { } fromExe) return fromExe;

        // 2) Fallback: smapi-internal metadata JSON (name varies by SMAPI build).
        foreach (var metaName in new[] { "SMAPI.metadata.json", "metadata.json" })
        {
            var metaPath = Path.Combine(gamePath, "smapi-internal", metaName);
            if (!File.Exists(metaPath)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("Version", out var v))
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch (Exception __ex) { AppLog.Warn("GameService", __ex.Message); }
        }

        // 3) 兵底：只能确认装了但读不到版本号。
        // 返回一个确定不会被 Version.TryParse 当成有效版本的占位符，
        // 且保证 CheckSmapiAsync 能识别出「本地版本未知但已装」。
        return null;   // 读不到版本 → 视为未识别，避免与 GitHub 版本比较时抖动
    }

    /// <summary>读程序集元数据里的版本，取前三段（"4.5.2.0" → "4.5.2"）。
    /// freshRead 时同样先拷到临时路径：游戏正在跑时 dll 可能被独占，直接读会抛。</summary>
    private static string? ReadAssemblyVersion(string dll, bool freshRead)
    {
        var probePath = dll;
        try
        {
            if (freshRead)
            {
                probePath = Path.Combine(Path.GetTempPath(),
                    "jg-smapi-probe-" + Guid.NewGuid().ToString("N") + ".dll");
                File.Copy(dll, probePath, true);
            }
            var name = System.Reflection.AssemblyName.GetAssemblyName(probePath);
            var v = name.Version;
            return v is null ? null : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
        catch (Exception ex) { AppLog.Warn("GameService", "读 SMAPI 程序集版本失败: " + ex.Message); return null; }
        finally
        {
            if (freshRead) { try { File.Delete(probePath); } catch { } }
        }
    }

    /// <summary>读一个 exe 的版本资源，取前三段（"4.5.2.0" → "4.5.2"）。
    /// <para>freshRead —— Windows 对版本资源有按路径的进程内缓存，SMAPI 覆盖安装后【立即】
    /// 重探会拿到旧版本号（实测：装完 4.5.2 列表仍显示 4.5.1 并误报可更新）。
    /// 拷到临时路径读，绕开缓存。</para></summary>
    private static string? ReadExeVersion(string exe, bool freshRead)
    {
        var probePath = exe;
        try
        {
            if (freshRead)
            {
                probePath = Path.Combine(Path.GetTempPath(),
                    "jg-smapi-probe-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(exe, probePath, true);
            }
            var info = FileVersionInfo.GetVersionInfo(probePath);
            var raw = info.FileVersion ?? info.ProductVersion;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var parts = raw.Split('.');
            return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
        }
        catch (Exception ex) { AppLog.Warn("GameService", "读版本资源失败 " + Path.GetFileName(exe) + ": " + ex.Message); return null; }
        finally
        {
            if (freshRead) { try { File.Delete(probePath); } catch { } }
        }
    }

    /// <summary>SMAPI 住在游戏根目录（主程序 + smapi-internal/），不在 Mods\ 里 —— 所以
    /// 「扫 Mods 判已装」对它永远为假，浏览页/详情页/依赖栏各自补过一遍特判。这里是一个入口：
    /// 装了没有看主程序文件在不在，版本号能读到就带上（读不到 Installed 仍为 true，
    /// 不能因为抽不出版本就说人没装 —— 那是 ProbeSmapiVersion 的口径，不是"装没装"的口径）。</summary>
    public (bool Installed, string? Version) SmapiState(string? gamePath, bool freshRead = false)
    {
        if (!HasSmapiLoader(gamePath)) return (false, null);
        return (true, ProbeSmapiVersion(gamePath!, freshRead));
    }

    /// <summary>游戏根下有没有 SMAPI 的主程序文件（exe 或 dll 任一）。
    /// 单独抽出来是给安装路由用的：「装了没有」这件事全应用只能有一个问法，
    /// 否则浏览页、详情页、依赖栏、安装路由会各说各话（历史上正是这样补了四遍特判）。</summary>
    public static bool HasSmapiLoader(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath)) return false;
        // exe 与 dll 任一即算装了：正常安装两者都有，但覆盖/中断安装会只剩一个，
        // 命令桥那条判据看的是 dll，依赖栏看的是 exe，两边都不该说假话。
        return File.Exists(Path.Combine(gamePath, "StardewModdingAPI.exe"))
            || File.Exists(Path.Combine(gamePath, "StardewModdingAPI.dll"));
    }

    // ------------------------------------------------------------------
    // SMAPI 自己的图标（从 exe 里抽取，转成 data URI 给界面用）
    // ------------------------------------------------------------------
    private string? _smapiIconCache;
    private string? _smapiIconForPath;

    public string? GetSmapiIconDataUri(string gamePath)
    {
        if (_smapiIconForPath == gamePath) return _smapiIconCache;
        _smapiIconForPath = gamePath;
        _smapiIconCache = null;
        try
        {
            var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
            if (!File.Exists(exe)) return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            _smapiIconCache = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception __ex) { AppLog.Warn("GameService", __ex.Message); }
        return _smapiIconCache;
    }
}
