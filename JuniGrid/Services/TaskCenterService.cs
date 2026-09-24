using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly string PersistPath = Path.Combine(StoragePaths.AppDataDir, "tasks.json");
    // v1.1.2：构造时创建、只在 RequestSave 里触发 —— 之前懒初始化无锁，并发首调可能建出两个 Timer 泄漏一个
    private readonly Timer _saveTimer;

    public TaskCenterService()
    {
        Load();
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static string PersistBackupPath => PersistPath + ".bak";

    private void Load()
    {
        try
        {
            if (File.Exists(PersistPath))
            {
                var raw = File.ReadAllText(PersistPath);
                var list = JsonSerializer.Deserialize<List<TaskItem>>(raw);
                if (list is null) return;
                foreach (var t in list)
                {
                    t.Status = RestoreStatus(t.Kind, t.Status);
                    Items.Add(t);
                }
                if (list.Count > 0)
                {
                    try { File.Copy(PersistPath, PersistBackupPath, overwrite: true); } catch { }
                }
                return;
            }
            // 主文件没了 → .bak 救回
            if (File.Exists(PersistBackupPath))
            {
                var list = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(PersistBackupPath));
                if (list is { Count: > 0 })
                {
                    foreach (var t in list)
                    {
                        t.Status = RestoreStatus(t.Kind, t.Status);
                        Items.Add(t);
                    }
                    AppLog.Warn("TaskCenter", $"tasks.json 缺失，已从 .bak 恢复 {list.Count} 条任务");
                    SaveNow();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("TaskCenter", "任务恢复失败: " + ex.Message);
            try
            {
                if (File.Exists(PersistPath))
                    File.Copy(PersistPath, PersistPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true);
            }
            catch { }
            try
            {
                if (File.Exists(PersistBackupPath))
                {
                    var list = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(PersistBackupPath));
                    if (list is { Count: > 0 })
                    {
                        foreach (var t in list)
                        {
                            t.Status = RestoreStatus(t.Kind, t.Status);
                            Items.Add(t);
                        }
                        AppLog.Warn("TaskCenter", "已从 .bak 恢复任务列表");
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>落盘恢复时"上次退出还在跑"的任务该标成什么：
    /// 版本下载能按标题里的 manifest 重新发起（VersionDownloadService 自己重建 worker）→ 标「已暂停」，
    /// 用户点得到「继续」；其余 kind 没有可重建的 worker，标成暂停等于给一个点了没反应的按钮 → 标失败。</summary>
    public static string RestoreStatus(string kind, string savedStatus)
        => savedStatus != "running" ? savedStatus
         : kind == "gameversion" ? "paused" : "failed";

    /// <summary>进度回报非常频繁，落盘用 800ms 防抖：静默 800ms 后才真正写盘。</summary>
    private void RequestSave() => _saveTimer.Change(800, Timeout.Infinite);

    private void SaveNow()
    {
        try
        {
            // v1.1.2：锁内序列化 —— 之前浅拷贝快照后锁外序列化，下载线程同时追加 t.Log
            // 会撞出「集合已修改」把该次落盘整个丢掉；tasks.json 很小（≤几百 KB）且 800ms
            // 防抖才写一次，锁内完成拷贝+写盘的代价可忽略
            lock (_lock)
            {
                var snapshot = Items.ToList();
                // 防呆：内存空、磁盘上还有任务 → 不许用 [] 盖掉（与 playtime 同款）
                if (snapshot.Count == 0 && File.Exists(PersistPath))
                {
                    try
                    {
                        var onDisk = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(PersistPath));
                        if (onDisk is { Count: > 0 })
                        {
                            AppLog.Warn("TaskCenter", "拒绝用空列表覆盖 tasks.json（磁盘上仍有 " + onDisk.Count + " 条）");
                            return;
                        }
                    }
                    catch
                    {
                        try
                        {
                            if (new FileInfo(PersistPath).Length > 4)
                            {
                                AppLog.Warn("TaskCenter", "tasks.json 暂不可读且非空，拒绝用空列表覆盖");
                                return;
                            }
                        }
                        catch { }
                    }
                }
                AtomicFile.WriteAllText(PersistPath, JsonSerializer.Serialize(snapshot));
            }
        }
        catch (Exception ex) { AppLog.Warn("TaskCenter", "任务落盘失败: " + ex.Message); }
    }

    /// <summary>线程安全的任务列表快照 —— UI 渲染必须用它，绝不能直接枚举 Items
    /// （下载线程随时 Insert/Remove，渲染线程枚举会撞「集合已修改」炸掉整页）。</summary>
    public List<TaskItem> Snapshot() { lock (_lock) return Items.ToList(); }

    /// <summary>单个任务日志的线程安全副本（渲染下拉面板用，避免与后台 Report 竞态）。</summary>
    public List<string> CopyLog(TaskItem t) { lock (_lock) return t.Log.ToList(); }

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
        // v1.08.2：下载进度行（"正在下载… x MB / y MB"）属于高频重复心跳，
        // 只覆盖日志里上一条同类行，不追加 —— 否则长下载轻松超 200 条上限，
        // 把备份/解压等真正的过程日志从头挤掉。事件行（切镜像/续传/阶段切换）照常追加。
        // v1.1.6：t.Log 的读写纳入 _lock —— 落盘定时器线程在锁内序列化整个 Items
        //（含每条 t.Log），这里锁外 Add 会撞「集合已修改」让该次落盘静默丢失。
        lock (_lock)
        {
            var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            var last = t.Log.Count > 0 ? t.Log[t.Log.Count - 1] : null;
            if (last is not null
                && (last == stamped
                    // 同一条心跳（下载进度）：覆盖上一行，别把 200 条上限刷满
                    || (IsDownloadHeartbeat(line) && IsDownloadHeartbeat(last))
                    // 同一步在重试循环里报了很多次（实测一次失败下载刷了 5 行
                    // 「Steam 授权成功，正在完成…」）：剥掉时间戳后同文就只留最新一条
                    || StripStamp(last) == line))
            {
                t.Log[t.Log.Count - 1] = stamped;
            }
            else
            {
                t.Log.Add(stamped);
                if (t.Log.Count > 200) t.Log.RemoveAt(0);
            }
        }
        if (percent is not null) t.Percent = percent.Value;
        if (speedMBps is not null) { t.SpeedMBps = speedMBps.Value; t.SpeedReported = true; }
        t.LastLine = line;
        OnChanged?.Invoke();
        RequestSave();
    }

    /// <summary>带时间戳的日志行是否是下载心跳行（Report 写入时已加 "[HH:mm:ss] " 前缀）。</summary>
    private static bool IsDownloadHeartbeat(string logLine) =>
        StripStamp(logLine).StartsWith("正在下载…", StringComparison.Ordinal);

    /// <summary>去掉行首 "[HH:mm:ss] " 时间戳，拿到正文。</summary>
    private static string StripStamp(string logLine)
    {
        var i = logLine.IndexOf("] ", StringComparison.Ordinal);
        return i >= 0 && logLine.StartsWith("[") ? logLine[(i + 2)..] : logLine;
    }

    public void Finish(TaskItem t, bool success, string? finalMsg = null)
    {
        t.Status = success ? "done" : "failed";
        t.Percent = success ? 100 : t.Percent;
        t.SpeedMBps = 0;
        if (finalMsg is not null)
        {
            lock (_lock)
            {
                t.Log.Add($"[{DateTime.Now:HH:mm:ss}] {finalMsg}");
                if (t.Log.Count > 200) t.Log.RemoveAt(0);
            }
            t.LastLine = finalMsg;
        }
        OnChanged?.Invoke();
        RequestSave();
    }

    public void Remove(TaskItem t)
    {
        lock (_lock) Items.Remove(t);
        // v1.1.3：移除 = 同步取消后台下载/安装 —— 之前只删条目，管线继续跑完，
        // mod 照样出现在 Mod 管理页（下载一半移除还会"复活"）。
        try { t.Cts.Cancel(); } catch { }
        UnregisterResume(t);
        OnChanged?.Invoke();
        RequestSave();
    }

    /// <summary>v1.06.6：按条件清理（下载页「全部删除」= 清掉当前筛选下的所有任务）。</summary>
    public void ClearMatching(Func<TaskItem, bool> match)
    {
        List<TaskItem>? killed = null;
        lock (_lock)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
                if (match(Items[i]))
                {
                    if (Items[i].Status == "running") (killed ??= new()).Add(Items[i]);
                    Items.RemoveAt(i);
                }
        }
        // v1.1.3：批量清理也取消还在跑的，避免"清了任务还继续装"
        if (killed is not null)
            foreach (var k in killed)
            {
                try { k.Cts.Cancel(); } catch { }
                UnregisterResume(k);
            }
        OnChanged?.Invoke();
        RequestSave();
    }

    public int RunningCount { get { lock (_lock) return Items.Count(t => t.Status == "running"); } }

    /// <summary>v1.1.7：暂停后继续用的 worker 注册表（进程内；落盘恢复的任务没有 worker，继续会失败）。</summary>
    private readonly ConcurrentDictionary<Guid, Func<CancellationToken, Task>> _resumeWork = new();

    public void RegisterResume(TaskItem t, Func<CancellationToken, Task> work)
        => _resumeWork[t.Id] = work;

    public void UnregisterResume(TaskItem t)
        => _resumeWork.TryRemove(t.Id, out _);

    /// <summary>暂停运行中的任务（worker 通过 Cts 感知；状态改为 paused）。</summary>
    public void Pause(TaskItem t)
    {
        if (t.Status != "running") return;
        t.Status = "paused";
        t.SpeedMBps = 0;
        try { t.Cts.Cancel(); } catch { }
        Report(t, "已暂停", t.Percent);
    }

    /// <summary>继续已暂停任务：重置 Cts，由调用方重新挂 worker。</summary>
    public void ResumeMark(TaskItem t)
    {
        if (t.Status != "paused") return;
        t.ResetCts();
        t.Status = "running";
        t.StartedAt = DateTime.Now;   // 「已运行」从这次继续算起，别把停机的那段也计进去
        Report(t, "继续中…", t.Percent);
    }

    /// <summary>v1.1.7：统一继续入口。有注册 worker 就重挂；历史版本下载走 VersionDownloadService 时由其自行处理。</summary>
    public bool Resume(TaskItem t)
    {
        if (t.Status != "paused") return false;
        if (!_resumeWork.TryGetValue(t.Id, out var work)) return false;
        ResumeMark(t);
        _ = Task.Run(() => work(t.Cts.Token), CancellationToken.None);
        return true;
    }

    public void Notify() => OnChanged?.Invoke();

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

    /// <summary>跑着的任务里有没有谁真报过速度 —— 一个都没有时界面不许显示 0.00 M/s。</summary>
    public bool TotalSpeedKnown
    {
        get { lock (_lock) return Items.Any(t => t.Status == "running" && t.SpeedReported); }
    }
}

public sealed class TaskItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "download";       // download / install / update / gameversion
    public string Status { get; set; } = "running";      // running / paused / done / failed
    public double Percent { get; set; }
    public double SpeedMBps { get; set; }

    /// <summary>这一路到底有没有报过速度。版本下载（DepotDownloader）压根不输出字节数，
    /// 从没报过 —— 界面据此显示「—」而不是 0.00 M/s，那是个假数字。</summary>
    [JsonIgnore]
    public bool SpeedReported { get; set; }

    /// <summary>v1.1.7：可暂停 = 下载/安装/更新/游戏版本；缓存清理等短任务不可暂停。</summary>
    [JsonIgnore]
    public bool CanPause => Kind is "download" or "install" or "update" or "gameversion";
        public string? LastLine { get; set; }
        // v1.06.7：必须有 setter —— 只读集合属性反序列化时不被填充，重启恢复的任务会丢光日志
        public List<string> Log { get; set; } = new();
    public DateTime StartedAt { get; set; }

    /// <summary>v1.1.3：任务取消源 —— 「移除/清理」时取消后台下载安装；不落盘。</summary>
    [JsonIgnore]
    private CancellationTokenSource _cts = new();
    [JsonIgnore]
    public CancellationTokenSource Cts => _cts;

    /// <summary>v1.4.5：暂停后继续 —— 旧 CTS 已 Cancel，换新的给 worker。</summary>
    public void ResetCts()
    {
        try { _cts.Dispose(); } catch { }
        _cts = new CancellationTokenSource();
    }
}
