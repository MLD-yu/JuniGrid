using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// v0.2.2：缓存/存储路径统一收口。此前下载 zip、SMAPI 安装包等路径散落在
/// 5 个服务里的硬编码 Path.Combine 全部改为从这里取。
/// CacheRoot（统一缓存目录）为 null 时走 DefaultCacheRoot —— v1.2.7 起那是<b>安装目录</b>
/// （程序和数据住一起，换目录安装由安装器搬家并改指），不再是 %TEMP%：
/// 临时目录是系统和各类清理工具会倒掉的地方，而里面住着 Nexus 登录态、SMAPI 更新前的
/// Mods 备份和几百 MB 版本缓存。日志与配置数据固定在 AppData（配置必须先于缓存位置
/// 可知加载，日志须在缓存目录被删后仍可诊断）。
/// </summary>
public static class StoragePaths
{
    /// <summary>统一缓存根目录；null = 走 <see cref="DefaultCacheRoot"/>。
    /// 由 ConfigService 在 Load/Save 时同步（SyncStoragePaths），改完立即生效。</summary>
    public static string? CacheRoot { get; internal set; }

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid");

    public static string LocalAppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid");

    /// <summary>可迁移项<b>历史</b>默认位置的统一根目录（v0.2.2~v1.2.6 的默认落点）。
    /// 现在只作为一次性搬家的来源，以及存储页里「旧位置」那一行的根 —— 搬家不查名单，
    /// 直接看这个目录下还有什么（见 StorageService.MigrateFromLegacyTempRoot）。</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "JuniGrid");

    private static string? _defaultCacheRoot;

    /// <summary>没设统一缓存目录时的默认落点：安装目录（与用户自己把缓存目录指到
    /// 安装目录的形态一致，WebView2 那一项在初始化之前就必须定下来，所以只算一次）。</summary>
    public static string DefaultCacheRoot =>
        _defaultCacheRoot ??= ResolveDefaultCacheRoot(AppContext.BaseDirectory);

    /// <summary>安装目录三种情况下不作默认缓存落点，退到 <c>%LOCALAPPDATA%\JuniGrid\cache</c>：
    /// ① 在同步盘（OneDrive 自己声明的根）之下 —— 几百 MB 缓存进同步队列；
    /// ② 不可写 —— 装在受保护位置时写什么都失败；
    /// ③ 本身就在 %TEMP% 里 —— 那正是这次要离开的地方（测试舱/临时跑一份会命中）。
    /// 判据读的是系统自己声明的位置（环境变量与实写探测），不靠路径里出现某个词。</summary>
    internal static string ResolveDefaultCacheRoot(string installDir)
    {
        var fallback = Path.Combine(LocalAppDataDir, "cache");
        var dir = TrimSep(installDir);
        if (dir.Length == 0) return fallback;

        foreach (var env in new[] { "OneDrive", "OneDriveConsumer" })
        {
            var root = TrimSep(Environment.GetEnvironmentVariable(env) ?? "");
            if (root.Length > 0 && IsSameOrInside(dir, root)) return fallback;
        }

        var temp = TrimSep(Path.GetTempPath());
        if (IsSameOrInside(dir, temp)) return fallback;

        if (!CanWrite(dir)) return fallback;
        return dir;
    }

    /// <summary>真写一个文件再删 —— 只问 Attributes 会把"只读目录/ACL 拒绝"判成可写。</summary>
    private static bool CanWrite(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, $".jg-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string TrimSep(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrInside(string child, string parent)
        => child.Equals(parent, StringComparison.OrdinalIgnoreCase)
           || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>可迁移项的当前落点：设了统一缓存目录就在它下面，否则在默认落点。</summary>
    public static string InCache(string sub) => Path.Combine(CacheRoot ?? DefaultCacheRoot, sub);

    /// <summary>下载与安装临时目录（直装/更新/nxm 的 zip、解压临时）。跟随缓存目录。</summary>
    public static string DownloadsDir => InCache("downloads");

    /// <summary>SMAPI 安装包下载与解压缓存。跟随缓存目录。</summary>
    public static string SmapiInstallerDir => InCache("smapi-installer");

    /// <summary>应用自更新安装包缓存（v1.0.8）。跟随缓存目录 —— 取消安装后文件保留，可续传/重装。</summary>
    public static string SelfUpdateDir => InCache("self-update");

    /// <summary>WebView2 用户数据目录（含登录态与网络缓存）。跟随缓存目录，变更后重启生效——
    /// MainWindow 启动时先执行遗留迁移再设 WEBVIEW2_USER_DATA_FOLDER。</summary>
    public static string WebView2Dir => InCache("webview2");

    /// <summary>SMAPI 更新前的 Mods 安全备份。跟随缓存目录。</summary>
    public static string ModsBackupDir => InCache("mods-backup");

    /// <summary>
    /// 游戏历史版本缓存（DepotDownloader 暂存，按版本号分目录）。
    /// 跟随统一缓存目录；未设置 CacheRoot 时用 <see cref="DefaultCacheRoot"/>（与其它可迁移项一致）。
    /// 旧路径 %LOCALAPPDATA%\JuniGrid\depot-staging 由 DepotDownloaderService 首次访问时搬迁。
    /// </summary>
    public static string DepotStagingDir => InCache("depot-staging");

    /// <summary>历史遗留的版本缓存位置（v1.4.4 前写死在 LocalAppData）。</summary>
    public static string LegacyDepotStagingDir => Path.Combine(LocalAppDataDir, "depot-staging");

    /// <summary>游戏 Mods 卸载回收站（相对游戏目录，固定不迁移）。</summary>
    public static string GameTrashDir(string gamePath) => Path.Combine(gamePath, "Mods", ".junigrid_trash");

    /// <summary>历史遗留：早期把 SMAPI install.dat 解到缓存根的临时文件（G3 审计钉住的无主条目）。</summary>
    public static string LegacySmapiInstallDat => InCache("_smapi-install.dat");

    /// <summary>版本收起的存档抽屉（LocalAppData，与 Saves 同盘以便瞬时改名）。见 SaveVersionService.DrawerRoot。</summary>
    public static string SavesHiddenDir => Path.Combine(LocalAppDataDir, "saves-hidden");

    /// <summary>切旧版本前备份的 Steam userdata 快照。</summary>
    public static string SteamUserdataBackupDir => Path.Combine(LocalAppDataDir, "userdata-backup");
}
