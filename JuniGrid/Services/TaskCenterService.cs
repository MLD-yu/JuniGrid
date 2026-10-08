using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// <summary>单个任务落定（成功/失败）时触发，供全局弹 toast + 置顶主窗。可能来自后台下载线程。</summary>
    public event Action<TaskItem, bool>? OnFinished;

    private readonly object _lock = new();
    private static readonly string PersistPath = Path.Combine(StoragePaths.AppDataDir, "tasks.json");
    // v1.1.2：构造时创建、只在 RequestSave 里触发 —— 之前懒初始化无锁，并发首调可能建出两个 Timer 泄漏一个
    private readonly Timer _saveTimer;
    // v1.09.0 僵尸任务看门狗（见ReapZombieTasks）
    private readonly Timer _reapTimer;

    public TaskCenterService()
    {
        Load();
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _reapTimer = new Timer(_ => ReapZombieTasks(), null,
            ZombieGraceMs, ZombieGraceMs);
    }

    /// <summary>僵尸判定的宽限期。看门狗只处理"超过这么久、且一行字都没多"的 running 任务。
    /// 取 60s 的理由：正常任务每几百毫秒到几秒就会 Report 一行（下载还有心跳行），
    /// 一分钟连一个字都没多 ⇒ 那个工人已经死了，不是还在干活。</summary>
    private const int ZombieGraceMs = 60_000;

    /// <summary>每条任务最后一次报进度的时刻（看门狗判僵尸用）。
    /// 存在服务侧而不塞进 TaskItem：TaskItem 是落盘对象，加时间戳字段会改动 tasks.json
    /// 结构，而恢复出来的任务本来就已被 RestoreStatus 改写，拿旧时间戳算"多久没动"会误杀。</summary>
    private readonly Dictionary<Guid, DateTime> _lastProgress = new();

    /// <summary>正处于"长耗时同步段"的任务（装大包、解压、CP 转换、全盘扫描）。
    /// 这些段落实测可达几十秒到几分钟，期间**一行进度都不会报**（见 InstallService
    /// 里"InstallNew 实测可达 50s+"、"Scan 是磁盘全量扫描"的注释）。
    /// 纯靠"多久没报进度"判僵尸会误杀它们，所以长任务进出这段时登记/注销一次豁免。</summary>
    private readonly HashSet<Guid> _longRunning = new();

    /// <summary>报进度的同时刷新"最后活动时间"（Report 里一并做，不另开旁路）。</summary>
    private void MarkProgress(TaskItem t) => _lastProgress[t.Id] = DateTime.Now;

    /// <summary>登记一段"会很久且不报进度"的作业（如 InstallNew / Scan）。
    /// 期间看门狗不碰它；<b>务必用 try/finally 或 using 成对注销</b>，否则任务会永远免疫
    /// （那样比误杀更隐蔽：真卡死的任务也抓不到了）。Dispose 时会顺手刷新一次记活时间，
    /// 给作业收尾阶段一个完整宽限，不会刚结束就判它僵尸。
    /// <para>典型用法：<c>using var longRun = _center.BeginLongRunning(task);</c></para>
    /// </summary>
    public IDisposable BeginLongRunning(TaskItem t) => new LongRunningScope(this, t.Id);

    private sealed class LongRunningScope : IDisposable
    {
        private readonly TaskCenterService _c;
        private readonly Guid _id;
        private bool _done;
        public LongRunningScope(TaskCenterService c, Guid id) { _c = c; _id = id; lock (_c._lock) _c._longRunning.Add(id); }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (_c._lock) _c._longRunning.Remove(_id);
            _c.MarkProgressFor(_id);      // 作业结束立刻恢复正常节奏
        }
    }

    private void MarkProgressFor(Guid id) => _lastProgress[id] = DateTime.Now;

    /// <summary>
    /// v1.09.0 看门狗：把"工人已死但任务还写着 running"的僵尸行扫掉，落成 failed。
    /// <para>
    /// 这是「转圈永远不停」的<b>最后一道兜底</b>。第一道是各 worker 的异常收尾
    /// （InstallService.FinishIfTimeout 等），但那要靠每个调用点都记得写对；
    /// 只要将来新增一条路径又忘了收尾，界面就又会永远转圈。看门狗不依赖任何调用点配合。
    /// </para>
    /// <para>
    /// 判据是「<b>状态还是 running、且从未取消过、且超过宽限期一行字都没多</b>」——
    /// 只看时间会误杀正在解 50MB+ 压缩包的安装（那段确实长时间不报进度），
    /// 所以必须同时要求"没报过任何新行"。看门狗只补状态，不碰 Cts。
    /// </para>
    /// </summary>
    private void ReapZombieTasks()
    {
        try
        {
            var cutoff = DateTime.Now - TimeSpan.FromMilliseconds(ZombieGraceMs);
            List<TaskItem> zombies;
            lock (_lock) zombies = Items.Where(t => IsZombie(t, cutoff)).ToList();

            foreach (var t in zombies)
            {
                // 二次确认：出锁后状态可能已被真正的 worker 收走
                if (!IsZombie(t, DateTime.Now - TimeSpan.FromMilliseconds(ZombieGraceMs))) continue;
                lock (_lock) _lastProgress.Remove(t.Id);
                // announce:false —— 不弹 toast、不抢主窗焦点。用户没碰过这个任务，
                // 回收它不该打断他正在做的事；失败记录安静躺在 /tasks 页就够了。
                // ⚠ 文案里【不许】承诺"可点重试"：CanRetry 要求 worker 已注册或带 NexusId，
                //   而"一键装依赖"这类任务两者都没有（见 InstallService.InstallMissingDependenciesAsync）——
                //   写了就是骗用户点一个根本不存在的按钮。
                Finish(t, false, LocService.Tr(
                    "任务已中断（超过 1 分钟没有任何进度，通常是网络请求卡住或程序内部异常）。请重新触发该操作。"),
                    announce: false);
            }
        }
        catch (Exception ex) { AppLog.Warn("TaskCenter", "僵尸任务扫描失败: " + ex.Message); }
    }

    /// <summary>这条任务是不是"工人已死"的僵尸。抽成独立方法是为了能被测试直接调用
    /// （看门狗本体是 Timer 驱动的私有循环，白盒里没法驱它）。四个条件缺一不可：
    /// <list type="number">
    /// <item>状态还是 running —— 已落终态的不用管；</item>
    /// <item>Cts 没被取消 —— 真·用户取消/移除走 Remove/Pause 自己的语义，不该被改写成失败；</item>
    /// <item>不在 <see cref="_longRunning"/> 里 —— 长作业段（InstallNew / Scan）实测可达
    ///       几分钟且期间不报任何进度，靠"多久没报"判会把<em>正在正常安装</em>的任务误杀。
    ///       这类段用 <see cref="BeginLongRunning"/> 登记豁免。</item>
    /// <item>最后活动时间早于 cutoff —— 这条才是"死"的证据。</item>
    /// </list>
    /// <para>
    /// ⚠ 调用方必须已持有 <c>_lock</c>（本方法读三个集合/字典，不自己加锁）。
    /// </para>
    /// <para>
    /// 判据读的是 <see cref="_lastProgress"/> 里的时刻，而 Start 时就会写入一个"现在"
    /// （见 MarkProgress），所以"刚创建还没报过第一行"的任务也有时间戳 —— 它在头 60s 内
    /// 不会被误判，正是宽限期该有的样子。字典里完全没有该 Id 时按最旧处理（判为僵尸），
    /// 那只可能是落盘恢复的 running 任务，而那些已被 RestoreStatus 改写过，走不到这里。
    /// </para>
    /// </summary>
    internal bool IsZombie(TaskItem t, DateTime cutoff)
        => t.Status == "running"
        && !t.Cts.IsCancellationRequested
        && !_longRunning.Contains(t.Id)          // 长作业段自我豁免（见 BeginLongRunning）
        && (!_lastProgress.TryGetValue(t.Id, out var last) || last < cutoff);
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
        var t = new TaskItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            Kind = kind ?? "download",
            StartedAt = DateTime.Now,
            Status = "running"
        };
        // 插到列表头，让最新创建/下载的任务始终排在最上面（/tasks 页从上到下看）。
        lock (_lock) { Items.Insert(0, t); MarkProgress(t); }
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
            MarkProgress(t);
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
    => Finish(t, success, finalMsg, announce: true);

    /// <summary>收尾并可选是否惊动用户。<paramref name="announce"/>=false 时不触发 OnFinished
    /// —— 不弹 toast、不把主窗置顶。看门狗回收僵尸任务时必须用这个：用户可能正在
    /// 干别的事，一个他没碰过的任务突然抢焦点 + 弹红条，比不回收更招人烦
    /// （他会在 /tasks 页看到那条失败记录，不会有任何打断）。</summary>
    public void Finish(TaskItem t, bool success, string? finalMsg, bool announce)
    {
        lock (_lock) _lastProgress.Remove(t.Id);   // 落终态即退出看门狗视野，字典同步回收
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
        if (announce) OnFinished?.Invoke(t, success);
        RequestSave();
    }

    /// <summary>这条任务还在列表里吗 —— 已被 Remove 的行不该再落终态（那会弹一个幽灵 toast）。
    /// 供各 worker 的异常收尾判断用：有些失败路径必须先确认行还在，才敢调 Finish。</summary>
    public bool Contains(TaskItem t) { lock (_lock) return Items.Contains(t); }

    public void Remove(TaskItem t)
    {
        lock (_lock) { Items.Remove(t); _lastProgress.Remove(t.Id); }
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
        List<TaskItem>? removed = null;
        lock (_lock)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
                if (match(Items[i]))
                {
                    (removed ??= new()).Add(Items[i]);
                    Items.RemoveAt(i);
                }
        }
        // v1.1.3：批量清理也取消还在跑的，避免"清了任务还继续装"
        if (removed is not null)
            foreach (var k in removed)
            {
                if (k.Status == "running")
                    try { k.Cts.Cancel(); } catch { }
                // 每个被删掉的条目都回收 worker：失败任务现在也留着一份（给「重试」用），
                // 只回收 running 的话这些会永久留在字典里。
                UnregisterResume(k);
                _lastProgress.Remove(k.Id);
            }
        OnChanged?.Invoke();
        RequestSave();
    }

    public int RunningCount { get { lock (_lock) return Items.Count(t => t.Status == "running"); } }

    /// <summary>v1.1.7：重挂 worker 的注册表（进程内；落盘恢复的任务没有 worker，继续/重试都会失败）。
    /// 失败的任务也留着这份 worker —— 「重试」走的就是「继续」那条重挂路径，
    /// 摘掉就没有可重跑的东西了。条目在 Remove/ClearMatching 里回收。</summary>
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
        Report(t, LocService.Tr("已暂停"), t.Percent);
    }

    /// <summary>继续已暂停任务：重置 Cts，由调用方重新挂 worker。</summary>
    public void ResumeMark(TaskItem t)
    {
        if (t.Status != "paused") return;
        t.ResetCts();
        t.Status = "running";
        t.StartedAt = DateTime.Now;   // 「已运行」从这次继续算起，别把停机的那段也计进去
        Report(t, LocService.Tr("继续中…"), t.Percent);
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

    /// <summary>这条失败任务能不能重试 —— 本次运行里真跑过的（worker 还在内存），
    /// 或者行上带着 Nexus modId 的（重启后仍能按这个号重新发起一条）。
    /// 两者都没有的历史失败行，按下去不会有任何动静，界面据此不画按钮。</summary>
    public bool CanRetry(TaskItem t) => t.Status == "failed"
        && (_resumeWork.ContainsKey(t.Id) || (t.NexusId is > 0 && t.Kind == "install"));

    /// <summary>重试失败的任务：与「继续」共用同一条重挂路径，不新造第二条执行逻辑。</summary>
    public bool Retry(TaskItem t)
    {
        if (t.Status != "failed") return false;
        if (!_resumeWork.TryGetValue(t.Id, out var work)) return false;
        t.ResetCts();
        t.Status = "running";
        t.Percent = 0;
        t.SpeedMBps = 0;
        t.StartedAt = DateTime.Now;
        Report(t, LocService.Tr("正在重试…"), 0);
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

    /// <summary>这条任务源自哪个 Nexus mod。它是唯一能跨重启重建下载动作的凭据，因此必须落盘：
    /// 内存里那份 worker 一重启就没了，只有带着这个号的失败行还能重新发起一条。</summary>
    public int? NexusId { get; set; }

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
