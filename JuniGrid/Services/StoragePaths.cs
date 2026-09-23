using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// v0.2.2：缓存/存储路径统一收口。此前下载 zip、SMAPI 安装包等路径散落在
/// 5 个服务里的硬编码 Path.Combine 全部改为从这里取。
/// CacheRoot（统一缓存目录）为 null 时全部走历史默认位置；
/// 设置后下载/安装临时、SMAPI 安装包、WebView2 数据、Mods 备份都落到该目录下的子目录。
/// 日志与配置数据固定在 AppData（配置必须先于缓存位置可知加载，日志须在缓存目录被删后仍可诊断）。
/// </summary>
public static class StoragePaths
{
    /// <summary>统一缓存根目录；null = 历史默认位置。
    /// 由 ConfigService 在 Load/Save 时同步（SyncStoragePaths），改完立即生效。</summary>
    public static string? CacheRoot { get; internal set; }

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid");

    public static string LocalAppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid");

    /// <summary>可迁移项历史默认位置的统一根目录（%TEMP%\JuniGrid）。</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "JuniGrid");

    /// <summary>可迁移项的当前落点：设了统一缓存目录就在它下面，否则在 %TEMP%\JuniGrid。</summary>
    public static string InCache(string sub) => Path.Combine(CacheRoot ?? TempRoot, sub);

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
    /// 跟随统一缓存目录；未设置 CacheRoot 时用 TempRoot（与其它可迁移项一致）。
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
