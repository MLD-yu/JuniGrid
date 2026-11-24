using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// 「每份存档上次是用哪个游戏版本启动的、什么时候」—— 存档自己只记最后一次<b>存盘</b>的版本
/// （&lt;gameVersion&gt;），而玩家真正想确认的是「这份档上次被哪个版本打开过」，两者并不总是同一件事
/// （打开旧档、没存盘就退出，版本不会变）。所以这里按会话记账：
/// <list type="number">
/// <item>启动游戏时 <see cref="Begin"/> 记下「这次是哪个版本 + 几点开始」；</item>
/// <item>游戏退出时 <see cref="End"/> 扫一遍 Saves，凡是在本会话期间被写过的档，
///       就盖上这次的版本号和时间 —— 没被写过的档说明这局没进去，保持原样。</item>
/// </list>
/// <para>数据存 <c>%APPDATA%\JuniGrid\savelog.json</c>，与配置分开：它是纯统计，
/// 坏了丢了最多少一行提示，不该连累 <c>junigrid.config.json</c> 的读写。</para>
/// </summary>
public static class SaveLaunchJournal
{
    private sealed record Entry(string Version, DateTime At);

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string FilePath => Path.Combine(StoragePaths.AppDataDir, "savelog.json");

    private static Dictionary<string, Entry>? _map;

    private static DateTime? _sessionStart;
    private static string? _sessionVersion;

    private static Dictionary<string, Entry> Map
    {
        get
        {
            if (_map is not null) return _map;
            try
            {
                if (File.Exists(FilePath))
                    _map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                        File.ReadAllText(FilePath), JsonOpts) ?? new();
            }
            catch (Exception ex) { AppLog.Warn("存档", "savelog 读取失败：" + ex.Message); }
            return _map ??= new();
        }
    }

    /// <summary>查一份档的上次启动记录（没有就是 null）。</summary>
    public static (string? Version, DateTime? At) Lookup(string dirName)
    {
        lock (Gate)
            return Map.TryGetValue(dirName, out var e) ? (e.Version, e.At) : (null, null);
    }

    /// <summary>游戏启动时调：记下版本与会话起点。版本为空就只记时间（总比什么都没有强）。</summary>
    public static void Begin(string? version, DateTime startedAt)
    {
        lock (Gate)
        {
            _sessionStart = startedAt;
            _sessionVersion = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
    }

    /// <summary>
    /// 游戏退出时调：把本会话里被写过的档盖上版本 + 时间。
    /// <para>容差 2 分钟：存档文件的写入时间与进程退出之间存在落盘/关闭的延迟，
    /// 而「上一局」的写入不该被算进来 —— 2 分钟既盖得住抖动，又短于两局之间的间隔。</para>
    /// </summary>
    public static void End(Action<string>? log = null)
    {
        lock (Gate)
        {
            if (_sessionStart is null) return;
            var since = _sessionStart.Value.AddMinutes(-2);
            var root = SaveVersionService.SavesDir();
            _sessionStart = null;
            if (root is null) return;

            var changed = 0;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (SaveVersionService.IsIncompleteShell(name)) continue;
                    DateTime newest;
                    try { newest = NewestWrite(dir); } catch { continue; }
                    if (newest < since) continue;   // 这局没进过这份档
                    Map[name] = new Entry(_sessionVersion ?? "", newest);
                    changed++;
                }
            }
            catch (Exception ex) { AppLog.Warn("存档", "会话记账失败：" + ex.Message); return; }

            if (changed > 0)
            {
                Save();
                log?.Invoke($"已记录 {changed} 份存档的启动版本：{_sessionVersion ?? "未知版本"}");
            }
        }
    }

    /// <summary>本会话是否已开始（应用退出时用：没开过游戏就不用扫盘）。</summary>
    public static bool SessionActive { get { lock (Gate) return _sessionStart is not null; } }

    /// <summary>只更新「上次启动时间」，不动版本 —— 用于版本读不出来但确实启动过的情况。</summary>
    public static void Touch(string dirName, DateTime at)
    {
        lock (Gate)
        {
            var v = Map.TryGetValue(dirName, out var e) ? e.Version : "";
            Map[dirName] = new Entry(v, at);
            Save();
        }
    }

    /// <summary>导入/删除后清掉孤儿记录（目录已经不在的条目）。</summary>
    public static void Prune()
    {
        lock (Gate)
        {
            var root = SaveVersionService.SavesDir();
            if (root is null) return;
            var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var d in Directory.GetDirectories(root)) alive.Add(Path.GetFileName(d));
            }
            catch { return; }
            var dead = Map.Keys.Where(k => !alive.Contains(k)).ToList();
            if (dead.Count == 0) return;
            foreach (var k in dead) Map.Remove(k);
            Save();
        }
    }

    private static DateTime NewestWrite(string dir)
    {
        var newest = DateTime.MinValue;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var t = File.GetLastWriteTime(f);
            if (t > newest) newest = t;
        }
        return newest;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(StoragePaths.AppDataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Map, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { AppLog.Warn("存档", "savelog 落盘失败：" + ex.Message); }
    }
}
