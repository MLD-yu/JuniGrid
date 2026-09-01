using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace JuniGrid.Services;

/// <summary>
/// 全局下载/安装任务中心。Mods 直装、SMAPI 更新、nxm 接管都往这里报进度。
/// UI 在右下角悬浮小图标 + /tasks 页看到当前所有任务和实时输出。
/// v1.06.6：任务长存 —— 变更后防抖落盘（tasks.json），重启自动恢复，直到用户自己清理；
/// 落盘时仍是 running 的任务（上次异常中断）恢复后标记为失败。
/// </summary>
public sealed class TaskCenterService
{
    public ObservableCollection<TaskItem> Items { get; } = new();
    public event Action? OnChanged;

    private readonly object _lock = new();
    private static readonly string PersistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid", "tasks.json");
    private Timer? _saveTimer;

    public TaskCenterService()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(PersistPath)) return;
            var list = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(PersistPath));
            if (list is null) return;
            foreach (var t in list)
            {
                if (t.Status == "running") t.Status = "failed";   // 上次中断的下载不可能再继续
                Items.Add(t);
            }
        }
        catch (Exception ex) { AppLog.Warn("TaskCenter", "任务恢复失败: " + ex.Message); }
    }

    /// <summary>进度回报非常频繁，落盘用 800ms 防抖：静默 800ms 后才真正写盘。</summary>
    private void RequestSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = new Timer(_ => SaveNow(), null, 800, Timeout.Infinite);
            return;
        }
        _saveTimer.Change(800, Timeout.Infinite);
    }

    private void SaveNow()
    {
        try
        {
            List<TaskItem> snap;
            lock (_lock) snap = Items.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(PersistPath)!);
            File.WriteAllText(PersistPath, JsonSerializer.Serialize(snap));
        }
        catch (Exception ex) { AppLog.Warn("TaskCenter", "任务落盘失败: " + ex.Message); }
    }

    public TaskItem Start(string title, string? kind = null)
    {
        var t = new TaskItem { Id = Guid.NewGuid(), Title = title, Kind = kind ?? "download",
            StartedAt = DateTime.Now, Status = "running" };
        // 插到列表头，让最新创建/下载的任务始终排在最上面（/tasks 页从上到下看）。
        lock (_lock) Items.Insert(0, t);
        OnChanged?.Invoke();
        RequestSave();
        return t;
    }

    public void Report(TaskItem t, string line, double? percent = null, double? speedMBps = null)
    {
        t.Log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        if (t.Log.Count > 200) t.Log.RemoveAt(0);
        if (percent is not null) t.Percent = percent.Value;
        if (speedMBps is not null) t.SpeedMBps = speedMBps.Value;
        t.LastLine = line;
        OnChanged?.Invoke();
        RequestSave();
    }

    public void Finish(TaskItem t, bool success, string? finalMsg = null)
    {
        t.Status = success ? "done" : "failed";
        t.Percent = success ? 100 : t.Percent;
        t.SpeedMBps = 0;
        if (finalMsg is not null) { t.Log.Add($"[{DateTime.Now:HH:mm:ss}] {finalMsg}"); t.LastLine = finalMsg; }
        OnChanged?.Invoke();
        RequestSave();
    }

    public void Remove(TaskItem t)
    {
        lock (_lock) Items.Remove(t);
        OnChanged?.Invoke();
        RequestSave();
    }

    public void ClearFinished()
    {
        lock (_lock)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
                if (Items[i].Status != "running") Items.RemoveAt(i);
        }
        OnChanged?.Invoke();
        RequestSave();
    }

    /// <summary>v1.06.6：按条件清理（下载页「清理全部」= 清掉当前筛选下的所有任务）。</summary>
    public void ClearMatching(Func<TaskItem, bool> match)
    {
        lock (_lock)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
                if (match(Items[i])) Items.RemoveAt(i);
        }
        OnChanged?.Invoke();
        RequestSave();
    }

    public int RunningCount { get { lock (_lock) return Items.Count(t => t.Status == "running"); } }
    public double TotalPercent
    {
        get
        {
            lock (_lock)
            {
                var running = Items.Where(t => t.Status == "running").ToList();
                if (running.Count > 0) return running.Average(t => t.Percent);

                // 没有运行中任务但还有已完成/失败的记录时，仍显示最上面那个任务
                // 的最终进度（成功时就是 100%），避免一完成总进度突然变 0。
                var top = Items.FirstOrDefault();
                return top?.Percent ?? 0;
            }
        }
    }
    public double TotalSpeedMBps
    {
        get { lock (_lock) return Items.Where(t => t.Status == "running").Sum(t => t.SpeedMBps); }
    }
}

public sealed class TaskItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "download";       // download / install / update
    public string Status { get; set; } = "running";      // running / done / failed
    public double Percent { get; set; }
    public double SpeedMBps { get; set; }
        public string? LastLine { get; set; }
        // v1.06.7：必须有 setter —— 只读集合属性反序列化时不被填充，重启恢复的任务会丢光日志
        public List<string> Log { get; set; } = new();
    public DateTime StartedAt { get; set; }
}
