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
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid", "playtime.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _seconds = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(FilePath), JsonOpts)
                           ?? new Dictionary<string, long>();
        }
        catch (Exception ex) { AppLog.Warn("PlayTime", ex.Message); _seconds = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (_gate)
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_seconds, JsonOpts));
        }
        catch (Exception ex) { AppLog.Warn("PlayTime", ex.Message); }
    }

    public void Dispose() => _timer.Dispose();
}
