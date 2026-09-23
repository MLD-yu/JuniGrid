using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JuniGrid.Services;

/// <summary>
/// Reads the locally signed-in Steam account (persona name + cached avatar)
/// straight from Steam's own config files — no network and no API key needed.
///
/// Data sources:
///   HKCU\Software\Valve\Steam → SteamPath          (install location)
///   &lt;steam&gt;\config\loginusers.vdf             (accounts, MostRecent flag)
///   &lt;steam&gt;\config\avatarcache\&lt;sid64&gt;*.jpg (cached avatar images)
/// </summary>
public sealed class SteamService
{
    private SteamProfile? _cached;

    public SteamProfile GetProfile() => _cached ??= LoadProfile();

    /// <summary>Drop the cache and re-read from disk (e.g. user switched accounts).</summary>
    public void Refresh()
    {
        _cached = null;
        GetProfile();
    }

    private static SteamProfile LoadProfile()
    {
        try
        {
            var steamPath = FindSteamPath();
            if (steamPath is null) return SteamProfile.None;

            var loginUsers = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsers)) return SteamProfile.None;

            var text = File.ReadAllText(loginUsers);

            string? sid = null, persona = null, account = null;
            long bestTs = -1;
            var foundMostRecent = false;

            // loginusers.vdf: blocks keyed by SteamID64, each holding
            // "AccountName" / "PersonaName" / "MostRecent" / "Timestamp".
            foreach (Match m in Regex.Matches(text,
                "\"(?<sid>\\d{17})\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline))
            {
                var body = m.Groups["body"].Value;
                var mostRecent = GetVdfValue(body, "MostRecent") == "1";
                var ts = long.TryParse(GetVdfValue(body, "Timestamp"), out var t) ? t : 0;

                // Prefer the block flagged MostRecent=1; otherwise the newest Timestamp wins.
                if ((mostRecent && !foundMostRecent) || (!foundMostRecent && ts > bestTs))
                {
                    foundMostRecent |= mostRecent;
                    bestTs = Math.Max(bestTs, ts);
                    sid = m.Groups["sid"].Value;
                    persona = GetVdfValue(body, "PersonaName");
                    account = GetVdfValue(body, "AccountName");
                }
            }

            var accountCount = Regex.Matches(text, "\"(?<sid>\\d{17})\"\\s*\\{", RegexOptions.Singleline).Count;

            if (sid is null) return SteamProfile.None;

            return new SteamProfile(persona, account, LoadAvatar(steamPath, sid), true, accountCount);
        }
        catch
        {
            return SteamProfile.None;
        }
    }

    private static string? GetVdfValue(string body, string key)
    {
        var m = Regex.Match(body, $"\"{key}\"\\s+\"(?<v>[^\"]*)\"");
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string? FindSteamPath()
    {
        try
        {
            var p = Registry.CurrentUser
                .OpenSubKey(@"Software\Valve\Steam")?
                .GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(p))
            {
                p = p.Replace('/', Path.DirectorySeparatorChar);
                if (Directory.Exists(p)) return p;
            }
        }
        catch (Exception __ex) { AppLog.Warn("SteamService", __ex.Message); }

        string[] candidates =
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"E:\Steam",
            @"F:\Steam",
        };
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return null;
    }

    /// <summary>
    /// Steam caches avatars as &lt;steamid64&gt;.jpg / _medium.jpg / _full.jpg.
    /// Returned as a data URI so the WebView2 page can show it without file:// access.
    /// </summary>
    private static string? LoadAvatar(string steamPath, string sid)
    {
        try
        {
            var dir = Path.Combine(steamPath, "config", "avatarcache");
            if (!Directory.Exists(dir)) return null;

            var files = Directory.GetFiles(dir, sid + "*");
            if (files.Length == 0) return null;

            // Prefer the "_full" (184px) variant, else the largest file.
            var best = files
                .OrderByDescending(f => f.Contains("_full", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => new FileInfo(f).Length)
                .First();

            var bytes = File.ReadAllBytes(best);
            if (bytes.Length < 8) return null;

            var mime = bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg"
                     : bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png"
                     : "image/jpeg";
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Steam 云存档（Auto-Cloud）现在是开还是关
    //
    // 为什么要判这个：星露谷只有一个存档目录、所有版本共用，而「把这个版本读不了的档
    // 收起来」必然要把目录移出 Saves —— 云开着时 Steam 会把它原样拉回来（本机实测两次：
    // 2026-09-18 20:57、2026-09-22 03:52→03:55），白搬；顺序稍一反过来（先上传后下载），
    // 还会把「本地没有这些档」当成玩家删过档传到云端，那是不可逆的。
    //
    // 判据（按可信度从高到低）：
    //
    // ① **`<steam>\userdata\<账号>\7\remote\sharedconfig.vdf` 里的 `apps/<appId>/cloudenabled`** ——
    //    这就是「属性 → 通用 → Steam 云」那个勾选本身，而且**取消勾选时立刻落盘**（本机实测：
    //    2026-09-22 06:01 取消，文件 mtime 就是 06:01，同一秒 cloud_log 里出现
    //    `[AppID 7] Need to upload file sharedconfig.vdf`）。没有这个 app 的条目 = 玩家从没改过
    //    这个勾选 = 用游戏默认值（星露谷默认开），返回 Unknown 让调用方按「开着」处理。
    //    我先前搜过 config.vdf / localconfig.vdf / remotecache.vdf / Saves\steam_autocloud.vdf
    //    都没有这个值，一度下了「盘上查不出来」的错误结论 —— 漏了这个文件，别再漏。
    //
    // ② `<steam>\logs\cloud_log.txt` 里最后一条本 app 的 `Starting sync (…)`。云关掉之后再经 Steam
    //    启动一次游戏，就会写 `Starting sync (AC Launch,Sync Disabled,)` 和 `(AC Exit,Sync Disabled,)`
    //    —— 所以 `Sync Disabled` 是有效信号，但它**滞后**到「下次经 Steam 启动」才出现，
    //    取消勾选当秒落盘的是 ①。因此 ① 优先，② 只当退路。
    //    （我一度数出 `Sync Disabled` 出现 0 次就下了「Steam 关云时不写任何一行」的结论 ——
    //    那次观测没错、前提错了：数的时刻云其实还开着。下「某信号永远不出现」这种结论前，
    //    先确认观测时刻该条件已经成立。）
    //
    // 两条都判不出 → Unknown，按「开着」处理。注意这只影响「要不要绕开 Steam 启动」这一个判断
    // （见 LauncherService.LaunchVanillaDirectly）；收档本身不再看云状态 —— 实测移档对云端无损，
    // 本地缺文件时 Steam 判成「该恢复」并下载回来，不会当成玩家删档往云上传。
    // ------------------------------------------------------------------

    /// <summary>云同步状态。Unknown 在判「要不要绕开 Steam 启动」时按 Enabled 对待。</summary>
    public enum CloudSync { Disabled, Enabled, Unknown }

    public static CloudSync ReadCloudSyncState(string? gamePath, string appId)
    {
        try
        {
            var root = FindSteamPath();
            if (string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(gamePath))
            {
                // 注册表读不到（绿色版 Steam / 权限问题）就从游戏目录上溯：
                // …\<steam>\steamapps\appmanifest_413150.acf → …\<steam>
                var manifest = FindAppManifest(gamePath, appId);
                if (manifest is not null)
                    root = Path.GetDirectoryName(Path.GetDirectoryName(manifest));
            }

            if (!string.IsNullOrWhiteSpace(root))
            {
                // ① 勾选值本身（取消勾选立刻落盘，是唯一能证明「现在是关的」的信号）
                var fromConfig = CloudSyncFromSharedConfig(root, appId);
                if (fromConfig != CloudSync.Unknown) return fromConfig;

                // ② 退路：日志只能证明「那一刻在同步」，证明不了「现在关了」
                var fromLog = CloudSyncFromLog(Path.Combine(root, "logs", "cloud_log.txt"), appId);
                if (fromLog != CloudSync.Unknown) return fromLog;
            }

            // 一次同步都没跑过（新装的 Steam、或从没经 Steam 启动过这个游戏）：
            // 存档目录里有 steam_autocloud.vdf = Steam 为这个游戏配过 Auto-Cloud，状态不明；
            // 连这个文件都没有 = 这份存档根本不在任何云同步根里，动它是安全的。
            var saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StardewValley", "Saves");
            return File.Exists(Path.Combine(saves, "steam_autocloud.vdf"))
                ? CloudSync.Unknown
                : CloudSync.Disabled;
        }
        catch { return CloudSync.Unknown; }
    }

    /// <summary>
    /// 只看 Steam 自己写的 cloud_log：最后一条本 app 的 Starting sync 里带 "Sync Disabled" = 云是关的。
    /// 注意这个信号是单向的 —— 没有那行时返回的 Enabled 意思是「最后一次同步时云是开的」，
    /// 玩家在那之后取消勾选并不会在日志里留下任何痕迹（见上面的注释），调用方必须自己给出「再点一次」的出口。
    /// 日志里没有这个 app 的同步记录 = 判不出（Unknown），调用方必须按「开着」处理。
    /// </summary>
    public static CloudSync CloudSyncFromLog(string? logPath, string appId)
    {
        var line = LastCloudSyncLine(logPath, appId);
        if (line is null) return CloudSync.Unknown;
        return line.Contains("Sync Disabled", StringComparison.OrdinalIgnoreCase)
            ? CloudSync.Disabled
            : CloudSync.Enabled;
    }

    /// <summary>
    /// 读 Steam 的漫游配置 `userdata\&lt;账号&gt;\7\remote\sharedconfig.vdf`：
    /// `Software/Valve/Steam/apps/&lt;appId&gt;/cloudenabled` 就是「属性 → 通用 → Steam 云」那个勾选，
    /// 取消勾选时立刻落盘，所以这是唯一能证明「现在云是关的」的信号。
    /// 没有这个 app 的条目 = 玩家从没动过这个勾选 = 用游戏默认值（星露谷默认开），返回 Unknown。
    /// 多个 Steam 账号时只要有一个说开着就按开着算（往安全那边倒）。
    /// </summary>
    public static CloudSync CloudSyncFromSharedConfig(string? steamRoot, string appId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || string.IsNullOrWhiteSpace(appId)) return CloudSync.Unknown;
        try
        {
            var userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata)) return CloudSync.Unknown;
            var seen = CloudSync.Unknown;
            foreach (var account in Directory.GetDirectories(userdata))
            {
                var file = Path.Combine(account, "7", "remote", "sharedconfig.vdf");
                if (!File.Exists(file)) continue;
                var on = ReadCloudEnabled(file, appId);
                if (on is null) continue;
                if (on.Value) return CloudSync.Enabled;
                seen = CloudSync.Disabled;
            }
            return seen;
        }
        catch { return CloudSync.Unknown; }
    }

    /// <summary>在 VDF 文本里找 apps/&lt;appId&gt;/cloudenabled 的值。null = 没有这个条目。
    /// 只认父块叫 "apps"、块名等于 appId 的那一个 —— 别的地方出现同名键不算。</summary>
    private static bool? ReadCloudEnabled(string path, string appId)
    {
        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            text = sr.ReadToEnd();
        }
        catch { return null; }

        var stack = new List<string>();
        string? pendingKey = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{') { stack.Add(pendingKey ?? ""); pendingKey = null; continue; }
            if (c == '}') { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); pendingKey = null; continue; }
            if (c != '"') continue;

            var end = text.IndexOf('"', i + 1);
            if (end < 0) break;
            var token = text.Substring(i + 1, end - i - 1);
            i = end;

            if (pendingKey is null) { pendingKey = token; continue; }
            if (pendingKey == "cloudenabled" && stack.Count >= 2
                && stack[stack.Count - 1] == appId && stack[stack.Count - 2] == "apps")
                return token != "0";
            pendingKey = null;
        }
        return null;
    }

    /// <summary>从日志尾部找最后一条本 appId 的 Starting sync。日志能长到 1 MB 以上，只读尾部。</summary>
    private static string? LastCloudSyncLine(string logPath, string appId)
    {
        try
        {
            if (!File.Exists(logPath) || string.IsNullOrWhiteSpace(appId)) return null;
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const long tail = 1024 * 1024;
            if (fs.Length > tail) fs.Seek(fs.Length - tail, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var appTag = "[AppID " + appId + "]";
            string? hit = null, line;
            while ((line = sr.ReadLine()) is not null)
                if (line.Contains(appTag, StringComparison.Ordinal)
                    && line.Contains("Starting sync", StringComparison.Ordinal))
                    hit = line;
            return hit;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    // v1.2.4：游戏版本锁定（阻止 Steam 自动更新）
    //
    // Steam 对已装游戏的更新走 appmanifest_<appId>.acf：客户端要更新/验证时
    // 必须先改写这份清单。把它设为只读，Steam 的更新会一直失败，游戏文件保持
    // 旧版可玩 —— 星露谷玩家留在旧版本（配旧 mod）的通行做法。GOG/绿色版
    // 没有 steamapps 清单，整套 API 返回找不到，UI 侧据此禁用。
    // ------------------------------------------------------------------

    /// <summary>从游戏目录向上定位 appmanifest_&lt;appId&gt;.acf。
    /// 标准库结构是 …\steamapps\common\Stardew Valley，逐级上溯最多 5 层即可覆盖
    /// 自定义库盘符；用户把游戏拷到没有 steamapps 的目录 → 返回 null（非 Steam 版）。</summary>
    public static string? FindAppManifest(string? gamePath, string appId)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(appId)) return null;
        try
        {
            var dir = Path.GetFullPath(gamePath);
            for (var i = 0; i < 5 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir, "steamapps", $"appmanifest_{appId}.acf");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    /// <summary>读 Steam 清单里某个 depot 当前已装的 manifest id。切换时把官方本体归档成
    /// 版本包要用它写 <c>.junigrid-manifest</c> —— 没有 manifest 的目录不会被版本列表认成包。</summary>
    public static string? ReadInstalledDepotManifest(string? manifestPath, string depotId)
    {
        try
        {
            if (manifestPath is null || !File.Exists(manifestPath) || string.IsNullOrWhiteSpace(depotId)) return null;
            var text = File.ReadAllText(manifestPath);
            var at = text.IndexOf("\"" + depotId + "\"", StringComparison.Ordinal);
            if (at < 0) return null;
            var m = Regex.Match(text.Substring(at), "\"manifest\"\\s*\"(\\d+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>清单文件当前是否只读（= 版本锁定中）。找不到清单 → false。</summary>
    public static bool IsGameVersionLocked(string? manifestPath)
    {
        if (manifestPath is null || !File.Exists(manifestPath)) return false;
        try { return File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReadOnly); }
        catch { return false; }
    }

    /// <summary>Steam 是否认为该游戏待修复/待更新（StateFlags 含 UpdateRequired /
    /// FilesMissing / FilesCorrupt —— 上次更新失败、或版本切换后文件与清单对不上时置位）。
    /// 这种状态下 steam:// 启动会先排队修复，游戏窗口迟迟不出现。</summary>
    public static bool SteamNeedsRepair(string? manifestPath)
    {
        try
        {
            if (manifestPath is null || !File.Exists(manifestPath)) return false;
            var m = Regex.Match(File.ReadAllText(manifestPath), "\"StateFlags\"\\s*\"(\\d+)\"");
            if (!m.Success) return false;
            const long updateRequired = 2, filesMissing = 32, filesCorrupt = 128;
            var flags = long.Parse(m.Groups[1].Value);
            return (flags & (updateRequired | filesMissing | filesCorrupt)) != 0;
        }
        catch { return false; }
    }

    /// <summary>设置/解除版本锁：改 appmanifest 的只读属性。失败（Steam 正在写、权限不足等）
    /// 返回 false + 中文原因，UI 据此提示，配置不落盘。</summary>
    public static (bool Ok, string? Error) SetGameVersionLock(string manifestPath, bool locked)
    {
        try
        {
            var attr = File.GetAttributes(manifestPath);
            var hasRo = attr.HasFlag(FileAttributes.ReadOnly);
            if (locked == hasRo) return (true, null);   // 状态一致，幂等
            File.SetAttributes(manifestPath, locked
                ? attr | FileAttributes.ReadOnly
                : attr & ~FileAttributes.ReadOnly);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, locked
                ? "锁定失败（Steam 可能正在更新，或没有写权限）：" + ex.Message
                : "解锁失败：" + ex.Message);
        }
    }

    /// <summary>启动前同步只读锁与实际版本状态：
    /// 处于历史版本降级（LockGameVersion 开 + 部署的历史版本仍是当前本体）时补上锁，
    /// 防 Steam 自动更新把降级覆盖回最新版；已回到官方最新时【解除】锁 ——
    /// 最新版上锁毫无收益，只会把用户在 Steam 界面里切分支/更新的写入全部卡成
    /// 「磁盘写入错误」（appmanifest_413150.acf），游戏卡在更新失败无法启动。
    /// 失败只记日志，不阻塞启动。</summary>
    public static void EnsureGameVersionLock(string? gamePath, string appId, bool downgradeActive)
    {
        try
        {
            var manifest = FindAppManifest(gamePath, appId);
            if (manifest is null) return;
            var locked = IsGameVersionLocked(manifest);
            if (downgradeActive && !locked)
            {
                File.SetAttributes(manifest,
                    File.GetAttributes(manifest) | FileAttributes.ReadOnly);
                AppLog.Warn("Steam", "已重新锁上历史版本（appmanifest 只读）");
            }
            else if (!downgradeActive && locked)
            {
                File.SetAttributes(manifest,
                    File.GetAttributes(manifest) & ~FileAttributes.ReadOnly);
                AppLog.Warn("Steam", "当前已是官方最新版：解除 appmanifest 只读锁，Steam 分支切换/更新恢复正常");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Steam", "启动前同步版本锁状态失败: " + ex.Message);
        }
    }

}

public sealed record SteamProfile(
    string? PersonaName,
    string? AccountName,
    string? AvatarDataUri,
    bool Found,
    int AccountCount = 0)
{
    public static readonly SteamProfile None = new(null, null, null, false, 0);
}
