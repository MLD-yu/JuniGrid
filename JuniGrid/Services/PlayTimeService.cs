using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// v1.1.5：每日游玩时长统计（GitHub 热力图数据源）。
/// 后台每 30s 轮询一次游戏进程（LauncherService.IsGameRunning 同时覆盖本程序启动
/// 与外部启动的 Stardew Valley / StardewModdingAPI），在运行就把 30s 累计进当天的
/// 秒数桶并落盘 —— 会话级 Start/Exit 钩子（LauncherService.OnGameExit 那套）在
/// JuniGrid 中途被杀时整段时长会丢，逐 tick 累计最多丢最后一个 tick。
/// 数据存 %APPDATA%/JuniGrid/playtime.json：{ "yyyy-MM-dd": 秒 }。
/// </summary>
public sealed class PlayTimeService : IDisposable
{
    private static readonly string FilePath = Path.Combine(StoragePaths.AppDataDir, "playtime.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly LauncherService _launcher;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private Dictionary<string, long> _seconds = new();   // 日期(本地) → 当天游玩秒数

    /// <summary>数据变化（tick 累计 / 手动修正）后触发；可能来自后台线程，订阅方自行调度。</summary>
    public event Action? OnChanged;

    public PlayTimeService(LauncherService launcher)
    {
        _launcher = launcher;
        Load();
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) => Save();
        // 首个 tick 延迟 5s：避开应用启动瞬间的一堆初始化争 IO
        _timer = new System.Threading.Timer(_ => Tick(), null, 5000, 30000);
    }

    public IReadOnlyDictionary<string, long> Snapshot { get { lock (_gate) return new Dictionary<string, long>(_seconds); } }

    public long GetSeconds(string dateKey) { lock (_gate) return _seconds.TryGetValue(dateKey, out var s) ? s : 0; }

    /// <summary>全部年份的游玩秒数总和（热力图口径，首页「最近游玩」卡片显示用）。</summary>
    public long TotalSeconds { get { lock (_gate) { var t = 0L; foreach (var v in _seconds.Values) t += v; return t; } } }

    private void Tick()
    {
        try
        {
            if (!_launcher.IsGameRunning) return;
            lock (_gate)
            {
                var key = DateTime.Now.ToString("yyyy-MM-dd");
                _seconds[key] = GetSeconds(key) + 30;
                Save();
            }
            OnChanged?.Invoke();
        }
        catch { /* 统计失败不影响主流程，下个 tick 再试 */ }
    }

    private static string BackupPath => FilePath + ".bak";

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                _seconds = JsonSerializer.Deserialize<Dictionary<string, long>>(text, JsonOpts)
                           ?? new Dictionary<string, long>();
                // 成功读入后留一份 .bak —— 主文件被误清/损坏时可救
                if (_seconds.Count > 0)
                {
                    try { File.Copy(FilePath, BackupPath, overwrite: true); } catch { }
                }
                return;
            }
            // 主文件没了：用 .bak 救回（曾出现 playtime.json 被写成 {} 导致热力图清零）
            if (File.Exists(BackupPath))
            {
                _seconds = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(BackupPath), JsonOpts)
                           ?? new Dictionary<string, long>();
                if (_seconds.Count > 0)
                {
                    AppLog.Warn("PlayTime", $"playtime.json 缺失，已从 .bak 恢复 {_seconds.Count} 天记录");
                    AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_seconds, JsonOpts));
                }
                return;
            }
            _seconds = new();
        }
        catch (Exception ex)
        {
            AppLog.Warn("PlayTime", ex.Message);
            // 解析失败绝不直接清零写回 —— 先把现场留档，再尽量从 .bak 读
            try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true); } catch { }
            _seconds = new();
            try
            {
                if (File.Exists(BackupPath))
                    _seconds = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(BackupPath), JsonOpts)
                               ?? new Dictionary<string, long>();
            }
            catch { _seconds = new(); }
        }
    }

    private void Save()
    {
        try
        {
            lock (_gate)
            {
                // 防呆：内存是空、磁盘上还有数据 → 绝不拿 {} 盖掉（误清/异常后的最后一道闸）
                if (_seconds.Count == 0 && File.Exists(FilePath))
                {
                    try
                    {
                        var raw = File.ReadAllText(FilePath);
                        var onDisk = JsonSerializer.Deserialize<Dictionary<string, long>>(raw, JsonOpts);
                        if (onDisk is { Count: > 0 })
                        {
                            AppLog.Warn("PlayTime", "拒绝用空数据覆盖 playtime.json（磁盘上仍有 " + onDisk.Count + " 天）");
                            return;
                        }
                    }
                    catch
                    {
                        // 文件存在但读不动（占用/半截）：只要不是空壳就不许写 {}
                        try
                        {
                            if (new FileInfo(FilePath).Length > 4)
                            {
                                AppLog.Warn("PlayTime", "playtime.json 暂不可读且非空，拒绝用空数据覆盖");
                                return;
                            }
                        }
                        catch { }
                    }
                }
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_seconds, JsonOpts));
            }
        }
        catch (Exception ex) { AppLog.Warn("PlayTime", ex.Message); }
    }

    public void Dispose() => _timer.Dispose();
}
