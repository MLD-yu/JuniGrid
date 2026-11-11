using System.Diagnostics;
using System.IO;

namespace JuniGrid.Services;

/// <summary>这局该拉起哪个 exe 的<b>来源</b>。UI 状态行、启动前自检的报错文案、juni-grid.log
/// 三处都要能说清「用的是哪一个、为什么用它」—— 只说「找不到 SMAPI」对改了名的用户毫无帮助。</summary>
public enum LauncherOrigin
{
    /// <summary>游戏目录下的标准壳 <c>StardewModdingAPI.exe</c>。</summary>
    Standard,
    /// <summary>设置里指定的那个 exe（用户把壳改了名，或想固定用某一个）。</summary>
    Custom,
    /// <summary>没指定、标准壳也不在，但在游戏目录里认出了被改名的 SMAPI 壳。</summary>
    Renamed,
    /// <summary>一个都没有。</summary>
    NotFound,
    /// <summary>认出不止一个，不敢猜 —— 交给用户在设置里点一下。</summary>
    Ambiguous,
}

/// <summary>启动器的判定结果。<see cref="Warning"/> 只承载「说了但没拦住」的话
/// （比如设置里的路径已经不在），真正的失败由 <see cref="Origin"/> 表达。</summary>
public readonly record struct LauncherPick(
    string? ExePath, LauncherOrigin Origin, string[] Candidates, string? Warning = null)
{
    public bool Ok => !string.IsNullOrEmpty(ExePath);

    /// <summary>进程名（判活 / 清理工作集 / 优雅关闭都按它找进程）。启动器被改名后
    /// 进程名随之改变 —— 只认 <c>StardewModdingAPI</c> 会把这一局当成「游戏没在跑」。</summary>
    public string? ProcessName => Ok ? Path.GetFileNameWithoutExtension(ExePath!) : null;

    /// <summary>给日志/界面看的短名，没有就是空串。</summary>
    public string ExeName => Ok ? Path.GetFileName(ExePath!) : "";
}

/// <summary>
/// 「哪个 exe 是 SMAPI 启动器」的唯一判定入口。
///
/// <para><b>为什么需要它</b>：SMAPI 的启动壳就是游戏根目录下一个朴素的
/// <c>StardewModdingAPI.exe</c>，很多人为了让它在文件夹里一眼看见会改成
/// <c>SMAPI.exe</c> / <c>启动MOD.exe</c> 之类。改名本身完全不影响运行（壳按自己所在的
/// 目录找本体），但启动器一直按文件名硬找，于是一改名就「未找到 SMAPI」——
/// 用户看到的是「我明明装好了」。</para>
///
/// <para><b>判定顺序</b>：用户指定的 &gt; 标准壳 &gt; 在游戏根目录里认出改名的壳。
/// 认得出来的前提是 PE 版本资源还写着 SMAPI（改名不改资源）—— 版本资源读不到时
/// 才退回「游戏根目录里除本体外只剩一个 exe」这条弱判据；剩下多个就<b>不猜</b>，
/// 让用户自己指一下。宁可多一次点击，也不要把某个不相干的 exe 当成游戏拉起来。</para>
/// </summary>
public static class SmapiLauncher
{
    /// <summary>标准壳的文件名。整个应用只在这里写它一次。</summary>
    public const string StandardExeName = "StardewModdingAPI.exe";

    /// <summary>游戏本体 —— 它永远不是 SMAPI 壳，但它是唯一一个必须被排除的合法 exe。</summary>
    private const string VanillaExeName = "Stardew Valley.exe";

    /// <summary>这些前缀的 exe 天生不是启动器（卸载器、崩溃收集器、运行库安装器、平台客户端）。
    /// 少排一个的代价是「自动识别」多认出一个人，用户还能自己挑；多排一个的代价是
    /// 用户改了名的启动器被我们故意无视 —— 所以这份名单只放几乎不可能误伤的。</summary>
    private static readonly string[] NeverLoaderPrefixes =
    [
        "unins", "uninstall", "unitycrashhandler", "crashhandler", "crashreport",
        "createdump",   // .NET 运行时的崩溃转储工具，SMAPI 目录里真实存在（且没有 SMAPI 版本信息）
        "dxsetup", "dxwebsetup", "vcredist", "dotnet", "steam", "galaxy", "gog",
    ];

    /// <summary>标准位置。</summary>
    public static string StandardPath(string gamePath) => Path.Combine(gamePath, StandardExeName);

    /// <summary>
    /// 定这一局用哪个 exe。<paramref name="configured"/> 是设置里存的自定义启动项
    /// （空 = 没指定）。<paramref name="gamePath"/> 无效时只可能返回自定义的那个。
    /// </summary>
    public static LauncherPick Resolve(string? gamePath, string? configured)
    {
        var custom = NormalizePath(configured);
        // 1) 指定的优先，即使标准壳也在 —— 用户改名就是为了这个，不能被他自己的安装目录顶掉。
        if (custom is not null && File.Exists(custom))
            return new LauncherPick(custom, LauncherOrigin.Custom, []);

        // 指定了但不能用：不当成"没指定"，要把这句话带出去（设置页状态行/启动日志都要看得见）。
        // 静默忽略的后果是「我设了但它没生效」，而现场什么线索都没有。
        var problem = string.IsNullOrWhiteSpace(configured) ? null
            : custom is null
                ? LocService.Tf("设置里的启动项不是有效的 exe 路径：{0}", configured.Trim())
                : LocService.Tf("设置里指定的启动项已经不存在：{0}", custom);

        var gameOk = !string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath);
        if (!gameOk)
            return new LauncherPick(null, LauncherOrigin.NotFound, [], problem);

        // 2) 标准位置
        var standard = StandardPath(gamePath!);
        if (File.Exists(standard))
            return new LauncherPick(standard, LauncherOrigin.Standard, [], problem);

        // 3) 被改了名：扫游戏根目录
        var (strong, weak) = Scan(gamePath!);
        if (strong.Length == 1) return new LauncherPick(strong[0], LauncherOrigin.Renamed, [], problem);
        if (strong.Length > 1) return new LauncherPick(null, LauncherOrigin.Ambiguous, strong, problem);
        // 版本资源读不出来的老/魔改壳：只有"除本体外仅剩一个 exe"时才敢认
        if (weak.Length == 1) return new LauncherPick(weak[0], LauncherOrigin.Renamed, [], problem);
        if (weak.Length > 1) return new LauncherPick(null, LauncherOrigin.Ambiguous, weak, problem);

        return new LauncherPick(null, LauncherOrigin.NotFound, [], problem);
    }

    /// <summary>便捷入口：只要路径，不关心来源（给"装了没有"这类一次性判定用）。</summary>
    public static string? ExeFor(string? gamePath, string? configured) => Resolve(gamePath, configured).ExePath;

    /// <summary>扫游戏根目录（只看一层，SMAPI 壳必须在根目录才可能工作）。
    /// strong = 版本资源里写着 SMAPI 的；weak = 其余没被排除的 exe。
    ///
    /// <para><b>weak 必须再过一道「这机器确实装过 SMAPI」</b>（根目录有 <c>smapi-internal\</c>，
    /// SMAPI 2.x 起一直如此）：没有这道闸，一个从没装过 SMAPI、只是文件夹里多躺了一个
    /// 不相干 exe 的用户会被我们拉错东西 —— 那种失败现场比「未找到 SMAPI」难查得多。
    /// strong 不需要这道闸：版本资源自己已经证明了身份。</para></summary>
    private static (string[] Strong, string[] Weak) Scan(string gamePath)
    {
        var strong = new List<string>();
        var weak = new List<string>();
        try
        {
            foreach (var file in new DirectoryInfo(gamePath).EnumerateFiles("*.exe"))
            {
                var name = file.Name;
                if (name.Equals(VanillaExeName, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(StandardExeName, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsNeverLoader(name)) continue;

                if (CarriesSmapiIdentity(file.FullName)) strong.Add(file.FullName);
                else weak.Add(file.FullName);
            }
            if (strong.Count == 0 && !Directory.Exists(Path.Combine(gamePath, "smapi-internal")))
                weak.Clear();
        }
        catch (Exception ex)
        {
            // 目录读不动（权限/盘掉了）：返回已经认到的，外层按"没找到"处理，不抛给调用方。
            AppLog.Warn("Launcher", $"扫描游戏目录里的启动器失败: {ex.Message}");
        }
        return (strong.ToArray(), weak.ToArray());
    }

    private static bool IsNeverLoader(string fileName)
    {
        foreach (var p in NeverLoaderPrefixes)
            if (fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 这个 exe 的版本资源是不是写着 SMAPI。改名不会动 PE 里的版本信息，
    /// 所以这是把 <c>SMAPI.exe</c> / <c>启动MOD.exe</c> 认出来的最可靠手段。
    /// 读不到（不是合法 PE / 权限不足）一律当"不是"—— 认错比认不出贵得多。
    /// </summary>
    public static bool CarriesSmapiIdentity(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            foreach (var s in new[]
                     {
                         info.FileDescription, info.ProductName, info.OriginalFilename,
                         info.InternalName, info.Comments,
                     })
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (s.Contains("stardew modding api", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Contains("smapi", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Launcher", $"读 {Path.GetFileName(exePath)} 的版本资源失败: {ex.Message}");
        }
        return false;
    }

    /// <summary>把用户填的路径整理成绝对路径（去引号/去首尾空白）。填的是目录就返回 null ——
    /// 这里要的是 exe，目录交给调用方去提示。</summary>
    private static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var p = raw.Trim().Trim('"').Trim();
        if (p.Length == 0) return null;
        try
        {
            p = Path.GetFullPath(p);
        }
        catch { return null; }
        return string.Equals(Path.GetExtension(p), ".exe", StringComparison.OrdinalIgnoreCase) ? p : null;
    }
}
