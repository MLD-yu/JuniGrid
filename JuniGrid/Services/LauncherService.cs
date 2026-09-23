using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// Launches Stardew Valley (SMAPI modded or Steam vanilla) and streams SMAPI
/// output to the Logs page: the leveled SMAPI-latest.txt is tailed for
/// colored log lines, stderr is kept for native errors, and [JuniGrid] lines
/// mark launcher events.
/// </summary>
public sealed class LauncherService
{
    private readonly ConfigService _cfg;
    private readonly DepotDownloaderService _depot;
    public LauncherService(ConfigService cfg, DepotDownloaderService? depot = null)
    {
        _cfg = cfg;
        _depot = depot ?? new DepotDownloaderService();
    }

    private DateTime? _sessionStart;

    private void OnGameExit()
    {
        if (_sessionStart is null) return;
        var mins = (long)Math.Round((DateTime.Now - _sessionStart.Value).TotalMinutes);
        if (mins > 0)
        {
            var c = _cfg.Current;
            c.TotalPlayMinutes += mins;
            _cfg.Save(c);
        }
        _sessionStart = null;
    }

    /// <summary>Raised for every stdout/stderr line SMAPI prints.</summary>
    public event Action<string>? OnLogLine;

    // The Logs page component is destroyed on every navigation, so the line
    // history must live here or the log "clears" whenever you leave /logs.
    private const int MaxLogLines = 2000;
    private readonly List<string> _logBuffer = new();
    private readonly object _logLock = new();

    private void RaiseLog(string line)
    {
        lock (_logLock)
        {
            _logBuffer.Add(line);
            if (_logBuffer.Count > MaxLogLines)
                _logBuffer.RemoveRange(0, _logBuffer.Count - MaxLogLines);
        }
        OnLogLine?.Invoke(line);
    }

    /// <summary>Copy of the buffered log lines, oldest first.</summary>
    public IReadOnlyList<string> GetLogSnapshot()
    {
        lock (_logLock) return _logBuffer.ToArray();
    }

    public void ClearLog()
    {
        lock (_logLock) _logBuffer.Clear();
    }

    // ------------------------------------------------------------------
    // SMAPI log-file tailing
    // ------------------------------------------------------------------
    private static string SmapiLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley", "ErrorLogs", "SMAPI-latest.txt");

    private CancellationTokenSource? _logTailCts;
    private int _logTailGen;

    // SMAPI 文件日志行头形如 [02:14:16 INFO  SMAPI]。非此形态的行是上一条的
    // 续行（多行消息/堆栈），跟随上一条所在组的可见性（SMAPI 控制台同款语义）。
    // 用完整头格式而非 line.StartsWith("[")，避免消息本身以 [ 开头的普通行误判成组头。
    private static readonly Regex LevelHeaderRegex =
        new(@"^\[\d{2}:\d{2}:\d{2}\s+(TRACE|DEBUG|INFO|WARN|ERROR|ALERT)\b", RegexOptions.Compiled);

    private void StartLogTail(bool readFromStart = false)
    {
        var old = _logTailCts;
        var cts = new CancellationTokenSource();
        _logTailCts = cts;
        try { old?.Cancel(); old?.Dispose(); } catch { }   // v1.1.6：旧 CTS 释放，不再每次启动泄漏一个
        var token = cts.Token;
        ++_logTailGen;
        var path = SmapiLogPath;

        // 基线：SMAPI 每次启动都重写整个文件，首行带时间戳必然变化，
        // 用「首行变了 / 文件变短」识别新会话，届时从头读。
        // readFromStart：接续已运行的游戏时从文件头读，把本会话历史补进视图。
        long pos = 0;
        string? baseFirstLine = null;
        try
        {
            if (File.Exists(path))
            {
                baseFirstLine = FirstLineOf(path);
                pos = readFromStart ? 0 : new FileInfo(path).Length;
            }
        }
        catch { /* 基线读不到就从 0 开始读 */ }

        _ = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            // TRACE 组跨读取轮次持续（半行留给下一轮，组状态同理）：
            // 组头后跟着的续行（多行消息/堆栈）与组头同生共死。
            var inTrace = false;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        var first = FirstLineOf(path);
                        if (pos > fs.Length || (first is not null && first != baseFirstLine))
                        {
                            pos = 0;                    // 文件被新会话重写
                            baseFirstLine = first;
                        }
                        if (fs.Length > pos)
                        {
                            fs.Seek(pos, SeekOrigin.Begin);
                            using var ms = new MemoryStream();
                            int n;
                            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                                ms.Write(buf, 0, n);
                            var bytes = ms.ToArray();
                            // 只消费到最后一个换行，半行留给下一轮；
                            // \n 不会出现在 UTF-8 多字节序列中间，按字节找换行是安全的
                            var lastNl = -1;
                            for (var i = bytes.Length - 1; i >= 0; i--)
                                if (bytes[i] == (byte)'\n') { lastNl = i; break; }
                            if (lastNl >= 0)
                            {
                                foreach (var raw in Encoding.UTF8.GetString(bytes, 0, lastNl + 1).Split('\n'))
                                {
                                    var line = raw.TrimEnd('\r');
                                    if (line.Length == 0) continue;
                                    // SMAPI 文件日志默认 verbose，游戏期 96% 是 TRACE
                                    // （Content Patcher / Event Lookup 刷屏），而视图永远不显示
                                    // TRACE 组——进缓冲只会把 MaxLogLines 上限里的可见行挤掉，
                                    // 启动日志就是这么「玩着玩着没了」的。在源头整组丢弃。
                                    if (LevelHeaderRegex.IsMatch(line)) inTrace = line.Contains(" TRACE ");
                                    if (!inTrace) RaiseLog(line);
                                }
                                pos += lastNl + 1;
                            }
                        }
                    }
                }
                catch { /* 文件被占用等瞬态错误：下一轮再试 */ }
                try { await Task.Delay(250, token); }
                catch (TaskCanceledException) { break; }
            }
        });
    }

    private void StopLogTail()
    {
        var old = _logTailCts;
        _logTailCts = null;
        try { old?.Cancel(); old?.Dispose(); } catch { }
    }

    /// <summary>
    /// 游戏在运行但不是本程序启动的（例如 JuniGrid 被重启过）→ 接上现有
    /// SMAPI 日志文件，从文件头把本会话内容补进日志视图。
    /// </summary>
    /// <summary>游戏进程名的唯一权威来源：SMAPI 启动时是 StardewModdingAPI，
    /// 直启本体时是 Stardew Valley。判活 / 清理工作集 / 优雅关闭都从这里取。</summary>
    public static readonly string[] GameProcessNames = ["StardewModdingAPI", "Stardew Valley"];

    /// <summary>系统里是否存在游戏进程（与是否由本程序启动无关）。</summary>
    public static bool IsGameProcessRunning() => AnyProcess(GameProcessNames);

    /// <summary>是否存在任一指定名的进程。v1.1.6：GetProcessesByName 返回的每个 Process
    /// 各持一个 OS 句柄，之前不 Dispose 全靠 finalizer —— IsGameRunning 被 1.5s 轮询 +
    /// 30s 统计 + 看门狗共用，是常驻热路径，句柄/GC 压力持续积累。</summary>
    private static bool AnyProcess(params string[] names)
    {
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name))
                using (p) return true;
        return false;
    }

    /// <summary>对指定名的所有进程执行动作（结果 Dispose；单进程失败不影响其余）。</summary>
    private static void ForEachProcess(string[] names, Action<Process> action)
    {
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    try { action(p); }
                    catch (Exception ex) { AppLog.Warn("LauncherService", ex.Message); }
    }

    public void AttachIfGameRunning()
    {
        if (_smapiProcess is { HasExited: false }) return;   // 自己启动的，已在跟踪
        if (AnyProcess("StardewModdingAPI", "Stardew Valley")) StartLogTail(readFromStart: true);
    }

    private static string? FirstLineOf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadLine();
        }
        catch { return null; }
    }

    private Process? _smapiProcess;

    public bool IsGameRunning =>
        _smapiProcess is { HasExited: false } || AnyProcess(GameProcessNames);

    /// <summary>能否向 SMAPI 控制台发命令：游戏须由本程序启动且未退出
    /// （接续的外部进程没有我们这条 stdin 管道，输入框该置灰）。</summary>
    public bool CanSendCommand => _smapiProcess is { HasExited: false };

    /// <summary>命令管道的编码。不显式指定的话 .NET 跟随父进程控制台代码页 —— 无控制台地
    /// 启动时退化成 UTF-8，而子进程 Console.In 按系统 ANSI 码页（中文机器 = 936）解码，
    /// 中文参数就变乱码（离线实测：写出 E6-8A-80-E8-83-BD，子进程读出「鎶€鑳?」）。
    /// 钉到 936 与解码端对齐；ASCII 命令两种码页字节相同，不受影响。</summary>
    private static readonly Encoding? SmapiPipeEncoding = CreatePipeEncoding();

    private static Encoding? CreatePipeEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch { return null; }   // 拿不到就退回 .NET 默认行为
    }

    // v1.3.4：不再自动加 debug 前缀 —— 旧逻辑把白名单外的输入一律当游戏调试命令
    // 补 "debug "，但 SMAPI 生态的【mod 自定义命令】（CJB Cheats、Lookup Anything、
    // 自定义 NPC mod 的命令……）都在白名单之外，被加上前缀后 SMAPI 就报
    // "没有名为 debug xxx 的命令"——用户实测"好多命令用不了"的根源。
    // SMAPI 本身会把收不到的裸命令提示"你是想输入 debug xxx 吗"，错误处理有兜底，
    // 直接原样转发是正确且宽容的行为。
    /// <summary>向 SMAPI 写入一条命令，等价于在 SMAPI 控制台里输入后回车（原样转发）。
    /// 走 LaunchSmapiCore 建的 stdin 管道；v1.6.9~v1.6.9b 曾试图改用「往控制台注入按键」，
    /// 但 Windows Terminal（ConPTY）下跨进程注入不生效，已放弃。</summary>
    public bool SendCommand(string command)
    {
        var p = _smapiProcess;
        if (p is not { HasExited: false }) return false;
        var actual = command.Trim();
        if (actual.Length == 0) return false;

        RaiseLog("[JuniGrid] > " + actual);
        try
        {
            p.StandardInput.WriteLine(actual);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("LauncherService", ex.Message);
            RaiseLog("[JuniGrid] 命令发送失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 取消启动的清场看门狗：Steam 的拉起管线可能在「取消」之后才把游戏进程拉出来
    /// （家庭共享校验、预载都要几秒），一次性 KillGame 会扑空 → 游戏照样跑起来。
    /// 持续监视 windowMs 毫秒，期间凡是出现 SMAPI/游戏进程一律关闭；
    /// 连续 2.5s 无进程、或 stopWhen() 返回 true（用户重新点了启动）则提前结束。
    /// </summary>
    public async Task KillGameWatchdogAsync(int windowMs = 20000, Func<bool>? stopWhen = null)
    {
        KillGame();
        var deadline = Environment.TickCount64 + windowMs;
        var lastActive = Environment.TickCount64;
        while (Environment.TickCount64 < deadline)
        {
            if (stopWhen?.Invoke() == true) return;
            var found = false;
            foreach (var name in GameProcessNames)
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) found = true;
                foreach (var p in procs)
                using (p)
                {
                    try { p.CloseMainWindow(); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
                    try { p.Kill(true); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
                }
            }
            if (found) lastActive = Environment.TickCount64;
            else if (Environment.TickCount64 - lastActive > 2500) return;   // 连续 2.5s 无进程 → 清场完成
            await Task.Delay(400);
        }
    }

    /// <summary>
    /// 关闭游戏进程：与 Steam 自己关游戏一致——先通知正常退出让游戏写盘，
    /// 稍后再回收仍未退出的进程。覆盖 SMAPI 与 Steam 官方两种启动模式。
    /// </summary>
    public void KillGame()
    {
        // 优先温和关闭所持有的 SMAPI 子进程
        if (_smapiProcess is { HasExited: false })
        {
            try { _smapiProcess.CloseMainWindow(); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
            try { _smapiProcess.Kill(true); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
            _smapiProcess = null;
        }

        // 主游戏进程（StardewModdingAPI.exe / Stardew Valley.exe），先通知保存
        ForEachProcess(GameProcessNames, p => p.CloseMainWindow());

        // 给主界面进程一点写盘时间，再强制回收仍在的
        Task.Delay(300).ContinueWith(_ =>
        {
            ForEachProcess(GameProcessNames, p => p.Kill(true));
        });
    }

    // ------------------------------------------------------------------
    // Pre-flight checks
    // ------------------------------------------------------------------
    public PreFlightResult CheckSmapi(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return PreFlightResult.Fail("尚未设置游戏路径，请先到设置页选择。");

        if (!Directory.Exists(gamePath))
            return PreFlightResult.Fail($"游戏目录不存在：{gamePath}");

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        if (!File.Exists(exe))
            return PreFlightResult.Fail(
                $"未找到 SMAPI：{exe}\n\n去 smapi.io 下载安装，或在首页切换到「Steam 官方」启动。");

        return PreFlightResult.Ok();
    }

    public PreFlightResult CheckSteam()
    {
        if (!IsSteamRunning)
            return PreFlightResult.Warn(
                "Steam 客户端似乎没在运行，将通过 steam:// 协议拉起（可能稍慢）。");

        // v1.6.8：Steam 认为文件待修复/待更新时（上次更新失败、版本切换后内容对不上），
        // steam:// 启动会先排队修复再开游戏 —— 表现为「点启动后干等、游戏窗口永远不出来」。
        // 此时提示改走 SMAPI 方式（直接拉起 StardewModdingAPI.exe，不经过 Steam 的更新排队）。
        var manifest = SteamService.FindAppManifest(_cfg.Current.GamePath, _cfg.Current.SteamAppId);
        if (SteamService.SteamNeedsRepair(manifest))
            return PreFlightResult.Warn(
                "Steam 认为游戏文件待修复/待更新（上次更新失败或切换过版本）。" +
                "「Steam 官方」方式启动会一直排队修复，游戏窗口迟迟不出现。" +
                "建议改用 SMAPI 方式启动，或在 Steam 库里点一下游戏让修复跑完。");

        return PreFlightResult.Ok();
    }

    /// <summary>Steam 客户端是否在运行（含 webhelper）。供启动等待期检测「Steam 被关闭」用。</summary>
    public static bool IsSteamRunning => AnyProcess("steam", "steamwebhelper");

    /// <summary>是否需要给 appmanifest 上只读锁：仅当用户开了版本锁，且部署的历史版本
    /// 【不是】目录里最新的官方正式版 —— 真降级才需要防 Steam 覆盖；部署的若是最新版
    /// （如用历史通道重装 1.6.15），锁毫无收益，只会把 Steam 界面里的分支切换/更新
    /// 卡成「磁盘写入错误」（v1.6.8 实际事故）。此时返回 false → 启动时自动解锁。</summary>
    private bool IsDowngradeLockNeeded()
    {
        if (!_cfg.Current.LockGameVersion) return false;
        // 没有降级记录 = 从没切过历史版本（或记录已被判失效清空，实测 15:44 / 16:36 各一次），
        // 此时本体就是 Steam 装的官方最新版：照样上锁会复现 v1.6.8 的「磁盘写入错误」，
        // 因为 IsLatestOfficialManifest(空) 返回的是 false（"不确定"）而不是 true。
        if (string.IsNullOrWhiteSpace(_cfg.Current.LastHistoricalManifest)) return false;
        return !DepotDownloaderService.IsLatestOfficialManifest(
            _cfg.Current.SteamAppId, _cfg.Current.LastHistoricalManifest);
    }

    /// <summary>清除 SMAPI 的「上次崩溃」标记，让本次启动跳过崩溃恢复提示。
    /// SMAPI 检测到 smapi-internal/StardewModdingAPI.crash.marker 时会在启动时打印
    /// 「按任意键删除崩溃数据，继续游戏」并 Console.ReadKey() 等键 —— 我们的 stdin 是
    /// 重定向管道（日志要进 UI 面板），ReadKey 直接抛异常，SMAPI 当场退出、游戏永远
    /// 启动不了。标记只是给下次启动看的崩溃消息，删掉无副作用；ErrorLogs 里的崩溃
    /// 日志本体改名 .prev.txt 保留一份供排查（SMAPI 自己在玩家按键后也是直接删除）。</summary>
    private static void ClearSmapiCrashState(string gamePath)
    {
        try
        {
            var marker = Path.Combine(gamePath, "smapi-internal", "StardewModdingAPI.crash.marker");
            if (File.Exists(marker)) File.Delete(marker);

            var crashLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StardewValley", "ErrorLogs", "SMAPI-crash.txt");
            if (File.Exists(crashLog))
            {
                var keep = crashLog + ".prev.txt";
                if (File.Exists(keep)) File.Delete(keep);
                File.Move(crashLog, keep);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Launcher", "清理 SMAPI 崩溃标记失败: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Launch
    // ------------------------------------------------------------------
    /// <summary>无窗口模式启动失败时，带控制台重试一次的机会（每次启动只给一次）。</summary>
    private bool _relaunchWithConsoleTried;

    /// <summary>
    /// v1.6.10：默认**不显示 SMAPI 控制台窗口** —— 日志页（尾随 SMAPI-latest.txt）+ 命令输入框
    /// 就是为取代它而做的。代价是 SMAPI 走「按任意键退出」那条路时玩家看不到提示，
    /// 所以下面 Exited 里一旦发现非 0 退出，就自动带窗口重拉一次，让真实报错可见。
    /// </summary>
    public LaunchResult LaunchSmapi(string gamePath)
    {
        _relaunchWithConsoleTried = false;
        return LaunchSmapiCore(gamePath, showConsole: false);
    }

    private LaunchResult LaunchSmapiCore(string gamePath, bool showConsole)
    {
        var half = HalfSyncedBlock();
        if (half is not null) return half.Value;
        // 带控制台重入那一次不再拦：能走到重入说明这一局已经启动过一次（闸门那次要么放行了、
        // 要么根本拦不住崩溃），而重入是后台 Task 拉起来的，没有界面能承接「仍然启动」这个出口。
        if (!showConsole)
        {
            // 同 LaunchSteam：先收档，收干净了闸门自然不拦。SMAPI 这条路本来就是直启，
            // 不触发 Steam 的 AC Launch 下载，所以收完不用换启动方式。
            var stashedSmapi = PrepareSavesForLaunch(gamePath);
            _ = stashedSmapi;
        }
        var check = CheckSmapi(gamePath);
        if (!check.Success) return LaunchResult.Fail(check.Message!);

        // v1.2.4：版本锁定开启 → SMAPI 启动前同步锁状态。虽然不走 steam://，
        // Steam 客户端自己在后台也会自动更新已装游戏，锁的是同一个 manifest。
        // v1.6.8：只在历史版本降级生效时上锁；已回官方最新则解除（见 EnsureGameVersionLock）。
        SteamService.EnsureGameVersionLock(gamePath, _cfg.Current.SteamAppId, IsDowngradeLockNeeded());

        // v1.6.8：上次会话崩溃时 SMAPI 启动会打印「按任意键删除崩溃数据」并用
        // Console.ReadKey() 等键 —— ReadKey 遇到重定向输入直接抛 InvalidOperationException。
        // 启动前清掉崩溃标记，就把这个最常见的触发点消掉了（崩溃日志改名保留一份供排查）。
        ClearSmapiCrashState(gamePath);

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        try
        {
            var startedAt = DateTime.Now;
            var firstAttempt = !_relaunchWithConsoleTried;
            _smapiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = gamePath,
                    UseShellExecute = false,
                    // 只接 stdin：命令输入框靠这条管道。stdout/stderr 一律不接 ——
                    // 没人持续读的话管道缓冲写满会把 SMAPI 卡死，而日志页读的是文件。
                    RedirectStandardInput = true,
                    StandardInputEncoding = SmapiPipeEncoding,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = !showConsole
                },
                EnableRaisingEvents = true
            };
            var proc = _smapiProcess;

            proc.Exited += (_, _) =>
            {
                OnGameExit();
                int? code = null;
                try { code = proc.ExitCode; } catch { }
                RaiseLog($"[JuniGrid] 游戏进程已退出，代码 {code}");
                ExplainSmapiExit(gamePath, code, startedAt);
                var gen = _logTailGen;
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    if (_logTailGen == gen) StopLogTail();
                });

                if (!showConsole && code is not 0 && !_relaunchWithConsoleTried)
                {
                    _relaunchWithConsoleTried = true;
                    RaiseLog("[JuniGrid] 启动异常，正在带控制台窗口重试一次 —— 真实报错会显示在那个窗口里");
                    _ = Task.Run(() => LaunchSmapiCore(gamePath, showConsole: true));
                }
            };

            proc.Start();
            _sessionStart = startedAt;
            if (firstAttempt) ClearLog();
            RaiseLog($"[JuniGrid] 已启动 SMAPI 进程 (PID {proc.Id})"
                     + (showConsole ? "，控制台窗口已显示" : "，日志见下方尾随"));
            StartLogTail();

            return LaunchResult.Ok(proc.Id);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// SMAPI 异常退出时把「真实原因」补进启动器日志。
    /// -532462766 (0xE0434352) = .NET 未处理异常，常见是无控制台时 PressAnyKeyToExit 的 ReadKey；
    /// 根因一般在 SMAPI-latest.txt 里更早的 ERROR 行。
    ///
    /// 只在**真的没跑成**时开口：SMAPI 每次启动都把「缺依赖的 mod 被跳过」整块写成 ERROR，
    /// 而正常玩完一局退出是代码 0 —— 不加这道闸门，玩家每次关游戏都会看到一句假的「未能正常进入游戏」。
    /// </summary>
    private void ExplainSmapiExit(string gamePath, int? exitCode, DateTime startedAt)
    {
        try
        {
            if (exitCode is null or 0) return;
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StardewValley", "ErrorLogs", "SMAPI-latest.txt");
            if (!File.Exists(logPath)) return;
            // 只认这一次会话写的日志。SMAPI 起不来时 latest.txt 还是上一局的，
            // 拿上局的报错解释这局的退出就是张冠李戴。
            if (File.GetLastWriteTime(logPath) < startedAt.AddSeconds(-5)) return;
            var lines = File.ReadAllLines(logPath);
            var errors = lines
                .Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("Press any key", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("crashed", StringComparison.OrdinalIgnoreCase))
                .TakeLast(8)
                .ToList();
            if (errors.Count == 0) return;
            RaiseLog("[JuniGrid] 非正常退出，SMAPI 日志里的关键报错：");
            foreach (var e in errors)
                RaiseLog("[SMAPI] " + e);
            var gameVer = UpdateService.ReadLocalGameVersion(gamePath);
            var smapi = Path.Combine(gamePath, "StardewModdingAPI.exe");
            var smapiVer = File.Exists(smapi)
                ? FileVersionInfo.GetVersionInfo(smapi).FileVersion : "(未安装)";
            RaiseLog($"[JuniGrid] 当前游戏 {gameVer} / SMAPI {smapiVer}。" +
                     "版本不匹配请用「Steam 官方」启动，或切到与 SMAPI 匹配的游戏版本。");
        }
        catch { }
    }


    private async Task TrackSteamExitAsync()
    {
        // 等游戏进程起来，再等它退出
        for (int i = 0; i < 60 && _sessionStart is not null; i++)
        {
            if (AnyProcess("Stardew Valley")) break;
            await Task.Delay(1000);
        }
        while (_sessionStart is not null && AnyProcess("Stardew Valley"))
        {
            await Task.Delay(3000);
        }
        // 聚焦启动暂存的档在这时放回，游戏列表恢复完整
        try { SaveVersionService.RestoreHidden(m => AppLog.Warn("存档", m)); } catch { }
        OnGameExit();
    }

    /// <summary>
    /// 云存档下到一半时拒绝启动（SMAPI 直启与 steam:// 两条路都过这里）。
    /// 这一步只读目录，不动任何文件；判定见 SaveVersionService.HalfSyncedSaves。
    /// </summary>
    private static DateTime _halfSyncWarnedAt = DateTime.MinValue;

    private static LaunchResult? HalfSyncedBlock()
    {
        var bad = SaveVersionService.HalfSyncedSaves();
        if (bad.Count == 0) return null;
        var since = DateTime.Now - _halfSyncWarnedAt;
        // 第一次拦住说明后果；同一状态 90 秒内再点一次 = 玩家看过说明仍要启动 → 放行并记日志。
        // 不给出口的话他会改用 Steam 直接启动，那条路我们连拦都拦不到，风险更大。
        if (since < TimeSpan.FromSeconds(90))
        {
            AppLog.Warn("Launcher", $"看过说明后仍要启动（{(int)since.TotalSeconds} 秒前拦过一次），" +
                                    "云存档半同步中的档：" + string.Join("、", bad));
            return null;
        }
        _halfSyncWarnedAt = DateTime.Now;
        AppLog.Warn("Launcher", "云存档半同步状态，已拒绝启动：" + string.Join("、", bad) +
                                "\n" + SaveVersionService.DescribeHalfSynced(bad));
        return LaunchResult.Blocked("halfsync", SaveVersionService.DescribeHalfSyncedShort(bad));
    }

    /// <summary>
    /// 当前部署版本读不了的存档还留在列表里时，启动前拦一次（SMAPI 直启与 steam:// 两条路都过这里）。
    ///
    /// 为什么必须有这道闸门：星露谷只有一个存档目录、所有版本共用，而存档是单向升级的 ——
    /// 被 1.6 存过一次盘的档，1.0 点进去就是 NPC..ctor 里的 NullReferenceException 闪退到桌面。
    /// 更坏的是老版本的存档列表读不懂这些档，把它们显示成「农场主 1 / 农场 1 / 开局第 1 年」，
    /// 和玩家自己的真 1.0 档长得一模一样（1.0 的 SaveGameInfo 没有 createdDate/daysPlayed 可用来区分），
    /// 玩家没法靠自己点对行。本机 35 次崩溃日志的农场主名全部是同一份 1.6.8 的档，一次例外都没有。
    ///
    /// 这一步只读存档目录、不动任何文件 —— 云存档开着也安全（移档那条路见 SaveVersionService.HideUnreadable）。
    /// 同一批档只拦一次：90 秒内再点「启动游戏」= 玩家看过说明仍要启动 → 放行并写日志。
    /// </summary>
    private static string _saveGateFingerprint = "";
    private static DateTime _saveGateWarnedAt = DateTime.MinValue;

    private static LaunchResult? SaveCompatibilityBlock(string? gamePath)
    {
        try
        {
            var version = UpdateService.ResolveCurrentVersion(gamePath);
            var bad = SaveVersionService.UnreadableBy(version);
            if (bad.Count == 0) return null;

            var fingerprint = SaveVersionService.UnreadableFingerprint(version, bad);
            var since = DateTime.Now - _saveGateWarnedAt;
            if (fingerprint == _saveGateFingerprint && since < TimeSpan.FromSeconds(90))
            {
                AppLog.Warn("Launcher",
                    $"看过说明后仍以 {version} 启动（{(int)since.TotalSeconds} 秒前拦过一次），" +
                    "这个版本读不了的档：" + string.Join("、", bad.Select(b => b.Name)));
                return null;
            }

            _saveGateFingerprint = fingerprint;
            _saveGateWarnedAt = DateTime.Now;
            AppLog.Warn("Launcher", $"当前版本 {version} 读不了 {bad.Count} 份存档，已拦下这次启动\n" +
                                    SaveVersionService.DescribeUnreadable(version, bad));
            return LaunchResult.Blocked("saves",
                SaveVersionService.DescribeUnreadableShort(version, bad));
        }
        catch (Exception ex)
        {
            // 闸门自己出问题绝不能变成「启动不了」
            AppLog.Warn("Launcher", "存档版本闸门没跑起来（本次不拦启动）：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 启动前收拾存档：先放回上次聚焦/收起的（列表回全），再按需留底。
    /// 启动前整理存档：放回上次收起的 → **把当前版本读不了的收进抽屉**（游戏列表只显示读得了的）。
    /// 返回仍暂存着的份数（持久量，判要不要绕开 Steam）。
    /// </summary>
    private static int PrepareSavesForLaunch(string? gamePath)
    {
        try
        {
            // 先解析版本再放回：RestoreHidden 要用它做差集（只放回这个版本读得了的档），
            // 否则每次启动都会把抽屉里全部档搬回 Saves、紧接着又被 HideUnreadable 搬回去 ——
            // 和版本切换是同一笔跨盘往返开销（存档真身在 C:，抽屉在缓存盘）。
            // 这个调换是安全的：ResolveCurrentVersion 读的是游戏目录，RestoreHidden 动的是 Saves，两者不相干。
            var version = UpdateService.ResolveCurrentVersion(gamePath);
            SaveVersionService.RestoreHidden(m => AppLog.Warn("Launcher", m), version);
            if (version is null) return SaveVersionService.StashedCountAll();
            // 没有版本包抽屉也照样收 —— 收进聚焦抽屉，保证游戏列表干净
            var pkg = DepotDownloaderService.StagingDirForVersion(version) ?? SaveVersionService.FocusRootOf();
            var n = SaveVersionService.HideUnreadable(version, pkg, m => AppLog.Warn("Launcher", m));
            if (n > 0)
                AppLog.Warn("Launcher", $"启动前收起 {n} 份 {version} 读不了的存档（游戏列表里不会出现）");
            return SaveVersionService.StashedCountAll();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Launcher", "启动前存档整理没跑成：" + ex.Message);
            return SaveVersionService.StashedCountAll();
        }
    }

    /// <summary>
    /// 这次启动该不该走直启本体、不叫 Steam 帮忙开。抽成独立判定是为了能在沙箱里验（--saves-guard S34）。
    ///
    /// 判据必须用「抽屉里现在压着几份」这个**持久量**，不能用「这一次我搬了没有」：
    /// 搬运动作本身会把"列表里还有读不了的档"这个前提消耗掉 —— 第二次点启动时列表已经干净、
    /// 这次一份都没搬，按"这次搬没搬"就会回落到叫 Steam 开，而 Steam 开场那次同步会把抽屉里
    /// 压着的档全数下载回来。实测 2026-09-22：08:44:18 第一次点（搬了 15 份）直启成功；
    /// 08:45:45 第二次点（没得搬）回落 steam:// → 08:45:45 `AC Launch,down` 把 15 份拉回 →
    /// 08:46:31 又是同一个 NPC..ctor 闪退。
    ///
    /// 云确认是关的话没有"开场下载"这回事，照旧叫 Steam 开（Steam 在线状态、游戏时长都还是正常的）。
    /// </summary>
    private static bool ShouldBypassSteam(int stashedInDrawer, SteamService.CloudSync cloud)
        => stashedInDrawer > 0 && cloud != SteamService.CloudSync.Disabled;

    /// <summary>
    /// 直启游戏本体（不经 steam://）。只在「暂存区还压着别的档」（聚焦启动）时用：
    /// steam:// 会在游戏进程起来之前先跑一次 Auto-Cloud 的 `AC Launch,down`，把刚收走的档全数拉回
    /// Saves（06:36:29 实测：Saves 从 5 个目录回到 21 个），等游戏读存档列表时又是闪退现场。
    /// 直启不触发那次下载（22 次会话实测：20/20 经 Steam 的都有 `AC Launch`，2/2 直启的都没有），
    /// 退出时仍触发 `AC Exit` → 这一局的进度照样上传到云。
    ///
    /// 刻意**不**给 _smapiProcess 赋值：这条路上没有 stdin 管道，赋了 CanSendCommand 就变成 true，
    /// 命令输入框会显示成可用、发出去石沉大海。运行态交给 TrackSteamExitAsync + AnyProcess，
    /// GameProcessNames 里本来就有 "Stardew Valley"。
    /// </summary>
    private LaunchResult LaunchVanillaDirectly(string gamePath, string? warning)
    {
        var exe = Path.Combine(gamePath, "Stardew Valley.exe");
        if (!File.Exists(exe))
            return LaunchResult.Fail(
                $"找不到游戏本体：{exe}\n\n" +
                "这次本来要直启本体（避免 Steam 云把刚收起来的存档拉回来），但目录里没有 Stardew Valley.exe。");
        try
        {
            _sessionStart = DateTime.Now;
            _ = TrackSteamExitAsync();
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = gamePath,
                    UseShellExecute = false,
                    CreateNoWindow = false
                }
            };
            proc.Start();
            // 这条路压根没起 SMAPI，而这一页叫「SMAPI 日志」——把说明性播报写进去，
            // 看着就像本次的 SMAPI 输出。原因记进 juni-grid.log 备查即可。
            AppLog.Info("Launcher", $"已直启游戏本体 (PID {proc.Id}) —— 未经 Steam，" +
                                    "避免云同步把聚焦暂存的其它存档拉回来；退出时 Steam 仍会上传这一局的进度");
            return LaunchResult.Ok(proc.Id, warning);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail("无法直启游戏本体：" + ex.Message);
        }
    }

    public LaunchResult LaunchSteam(string steamAppId)
    {
        var half = HalfSyncedBlock();
        if (half is not null) return half.Value;
        // 后台收起读不了的档即可；**不再弹「版本不匹配」说明**（用户明确不要这个框）。
        // 收不动的残档也会被塞进聚焦抽屉，游戏列表尽量干净。
        var stashed = PrepareSavesForLaunch(_cfg.Current.GamePath);
        return LaunchGameCore(steamAppId, stashed);
    }

    /// <summary>首页「用这份档玩」：自动匹配能读它的版本 → 必要时切过去并留底 → 聚焦只留这一份 → 启动。
    /// 新用户不用碰版本管理或云开关，点一下就能进对的档。</summary>
    public sealed record SavePlayRow(
        string Name, string? Farmer, string? Farm, string? GameVersion,
        bool ReadableNow, bool WillUpgrade, string Badge, string Hint);

    /// <summary>启动器存档列表：全部档 + 当前版本能否读 + 该用哪个版本开。</summary>
    public List<SavePlayRow> ListSavesForPlay()
    {
        var current = UpdateService.ResolveCurrentVersion(_cfg.Current.GamePath);
        var rows = new List<SavePlayRow>();
        foreach (var s in SaveVersionService.Scan())
        {
            var readable = SaveVersionService.ReadableBy(s, current);
            var upgrade = SaveVersionService.UpgradedBy(s, current);
            string badge, hint;
            if (!s.MetaComplete)
            {
                badge = "元数据缺失";
                hint = "没有 SaveGameInfo，认不出写入版本 —— 旧版本进得去也可能闪退，建议用最新版试；或从留底/云端找回完整档";
            }
            else if (readable && !upgrade)
            {
                badge = "可直接玩";
                hint = $"当前 {current ?? "未知"} 读得了";
            }
            else if (readable)
            {
                badge = "可直接玩 · 会升级";
                hint = $"当前 {current ?? "未知"} 读得了；存一次盘后旧版本就回不去了（会自动留底）";
            }
            else
            {
                var need = MinReadablePackageVersion(s);
                badge = $"需 {need ?? "更高版本"}";
                hint = need is null
                    ? "本机还没有能读这份档的版本包，先到「版本管理」下载"
                    : $"会自动切到 {need} 再进（约几十秒）";
            }
            rows.Add(new SavePlayRow(s.Name, s.FarmerName, s.FarmName, s.GameVersion, readable, upgrade, badge, hint));
        }
        return rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>能读这份档、且本机已有完整缓存的版本里，最旧的那个（尽量少升盘）。没有则 null。</summary>
    public string? MinReadablePackageVersion(SaveVersionService.Slot slot)
    {
        // 残缺档（无 SaveGameInfo）不给旧版本：直接找 1.3+ 里最旧的可读包
        var candidates = _depot.ListStagedPackages()
            .Where(p => p.Complete && SaveVersionService.ReadableBy(slot, p.Label))
            .ToList();
        if (candidates.Count == 0) return null;
        return candidates
            .OrderBy(p => p.Label, Comparer<string>.Create((a, b) => SaveVersionService.CompareVersions(a, b)))
            .First().Label;
    }

    /// <summary>PlaySave 的只读规划（UI 先显示将要发生什么；测试也走同一口径）。</summary>
    public (string? TargetVersion, bool NeedSwitch, bool WillUpgrade, string Message) PlanPlaySave(string saveName)
    {
        var slot = SaveVersionService.Scan().FirstOrDefault(x => x.Name == saveName);
        if (slot is null) return (null, false, false, "找不到这份存档");
        var current = UpdateService.ResolveCurrentVersion(_cfg.Current.GamePath);
        if (SaveVersionService.ReadableBy(slot, current))
            return (current, false, SaveVersionService.UpgradedBy(slot, current),
                $"用当前版本 {current ?? "未知"} 进「{slot.FarmName ?? slot.Name}」");
        var need = MinReadablePackageVersion(slot);
        if (need is null)
            return (null, false, false,
                $"当前版本 {current ?? "未知"} 读不了「{slot.FarmName ?? slot.Name}」（档内 " +
                $"{slot.GameVersion ?? "未记录"}），本机也没有能读它的版本包 —— 先到「版本管理」下载对应版本");
        return (need, true, true,
            $"会先切到 {need}（约几十秒），再进「{slot.FarmName ?? slot.Name}」");
    }

    /// <summary>真正执行「用这份档玩」。</summary>
    public LaunchResult PlaySave(string saveName, IProgress<string>? progress = null)
    {
        void Log(string m)
        {
            AppLog.Warn("存档", m);
            progress?.Report(m);
            RaiseLog("[JuniGrid] " + m);
        }

        var half = HalfSyncedBlock();
        if (half is not null) return half.Value;

        // 清掉上一局的聚焦/暂存，列表先回全
        try { SaveVersionService.RestoreHidden(Log); } catch { }

        var (target, needSwitch, willUpgrade, planMsg) = PlanPlaySave(saveName);
        Log(planMsg);
        if (target is null) return LaunchResult.Fail(planMsg);

        var cfg = _cfg.Current;
        var gamePath = cfg.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return LaunchResult.Fail("未检测到 Stardew Valley 游戏目录，请先在「设置」里指定。");

        // 升级前留底已去掉：后果用户自担

        if (needSwitch)
        {
            if (DepotDownloaderService.IsApplyInProgress)
                return LaunchResult.Fail("正在切换版本，请等它完成后再玩这份档。");
            if (IsGameProcessRunning())
                return LaunchResult.Fail("游戏正在运行 — 请先退出，再点「用这份档玩」。");
            var staged = _depot.ListStagedPackages()
                .FirstOrDefault(p => p.Complete &&
                    SaveVersionService.CompareVersions(p.Label, target) == 0);
            if (staged is null)
                return LaunchResult.Fail($"版本包 {target} 不完整，请到「版本管理」重新下载后再玩这份档。");
            Log($"正在切到 {target} …");
            var ok = false;
            try
            {
                ok = Task.Run(() => _depot.TryApplyStaged(gamePath, cfg.SteamAppId, "413151",
                    staged.ManifestId, new Progress<DepotDownloaderService.Progress>(p => Log(p.State)),
                    currentGameVersion: UpdateService.ResolveCurrentVersion(gamePath))).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return LaunchResult.Fail("切换版本失败：" + ex.Message);
            }
            if (!ok) return LaunchResult.Fail($"版本包 {target} 不完整，无法切换。请到「版本管理」重新下载。");
            // 真历史降级才锁，避免 Steam 覆盖（与版本弹窗同一口径）
            try
            {
                var manifestPath = SteamService.FindAppManifest(gamePath, cfg.SteamAppId);
                if (manifestPath is not null
                    && !DepotDownloaderService.IsLatestOfficialManifest(cfg.SteamAppId, staged.ManifestId))
                    SteamService.SetGameVersionLock(manifestPath, true);
                cfg.LastHistoricalManifest = staged.ManifestId;
                cfg.LastHistoricalLabel = staged.Label;
                cfg.LastHistoricalInternalVersion = UpdateService.ResolveCurrentVersion(gamePath) ?? "";
                _cfg.Save(cfg);
            }
            catch { /* 锁失败只记日志，不拦启动 */ }
        }

        // 聚焦：游戏里只出现这一份，点错行闪退从根上没了
        var focused = SaveVersionService.FocusHideExcept(saveName, Log);
        var visible = SaveVersionService.Scan();
        var stashed = SaveVersionService.StashedCountAll();
        Log(visible.Count <= 1
            ? $"聚焦启动：游戏里只显示「{saveName}」（暂存了 {focused} 份其它档，玩完自动放回）"
            : $"聚焦未能收干净（列表仍有 {visible.Count} 份），已尽量收起");

        if (visible.Count > 1)
        {
            // 聚焦失败也**不再弹「版本不匹配」** —— 与普通启动同一策略：后台尽力收起，不打断玩家
            AppLog.Warn("Launcher", $"聚焦未能收干净（列表仍有 {visible.Count} 份），已尽量收起读不了的档");
        }

        return LaunchGameCore(cfg.SteamAppId, stashed);
    }

    private LaunchResult LaunchGameCore(string steamAppId, int stashed)
    {
        // 前置：路径都没有 → 极大概率 Steam 账号未拥有此游戏或未安装
        if (string.IsNullOrWhiteSpace(_cfg.Current.GamePath) || !Directory.Exists(_cfg.Current.GamePath))
            return LaunchResult.Fail(
                "未检测到 Stardew Valley 游戏目录。\n\n" +
                "可能原因：\n" +
                "  · 当前 Steam 账号未拥有本游戏（需先在 Steam 购买）\n" +
                "  · 游戏未安装或路径异常 → 请在「设置」里手动指定目录\n\n" +
                "启动器会尝试用 steam:// 协议拉起，若 Steam 弹\"此账号不拥有该游戏\"即为此因。");

        // v1.2.4：版本锁定开启 → 启动前同步锁状态（appmanifest 只读）。
        // v1.6.8：只在历史版本降级生效时上锁；已回官方最新则自动解除 ——
        // 否则用户在 Steam 界面换 beta 分支时客户端写不了清单，报「磁盘写入错误」
        // 卡死在更新失败状态。失败只记日志，不阻塞启动。
        SteamService.EnsureGameVersionLock(_cfg.Current.GamePath, _cfg.Current.SteamAppId, IsDowngradeLockNeeded());

        // Steam 启动选项若指向 SMAPI，同样会被崩溃恢复提示卡死（stdin 是 Steam 的管道），
        // 启动前一并清理
        ClearSmapiCrashState(_cfg.Current.GamePath);

        var check = CheckSteam();
        var warning = check.Success ? null : check.Message;

        // 抽屉里压着档、且云可能把它们下回来 → 自己直接开游戏本体，不叫 Steam 帮忙开。
        // 判据和理由见 ShouldBypassSteam。
        var cloud = SteamService.ReadCloudSyncState(_cfg.Current.GamePath, _cfg.Current.SteamAppId);
        if (ShouldBypassSteam(stashed, cloud))
        {
            AppLog.Warn("Launcher", $"这次自己开游戏本体、不经 Steam：暂存区压着 {stashed} 份" +
                                    $"（云 = {cloud}），叫 Steam 开会先把它们下载回存档目录");
            return LaunchVanillaDirectly(_cfg.Current.GamePath, warning);
        }

        // Steam 模式也开秒表（Steam 官方模式下我们看不到子进程退出，靠 Stardew Valley.exe 探测）
        _sessionStart = DateTime.Now;
        _ = TrackSteamExitAsync();
        AppLog.Warn("Launcher", $"这次叫 Steam 帮忙开：暂存区没压档（云 = {cloud}）");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{steamAppId}",
                UseShellExecute = true
            });
            return LaunchResult.Ok(null, warning);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail("无法通过 Steam 启动：" + ex.Message);
        }
    }
}

public readonly record struct PreFlightResult(bool Success, bool IsWarning, string? Message)
{
    public static PreFlightResult Ok() => new(true, false, null);
    public static PreFlightResult Warn(string msg) => new(true, true, msg);
    public static PreFlightResult Fail(string msg) => new(false, false, msg);
}

/// <summary>
/// GateKind 非空 = 这次不是「启动失败」，是启动前的闸门主动拦下来的：Error 里是给玩家看的完整说明，
/// 必须整段显示（弹窗），并且要带出路 —— 走 toast 的话文案会被 SummarizeLaunchError 截到 60 字，
/// 「再点一次就能启动」那半句正好被切掉，闸门就变成了没有出口的硬拦（人会改从 Steam 直接启动，
/// 那条路我们连拦都拦不到）。
/// </summary>
public readonly record struct LaunchResult(bool Success, int? Pid, string? Error, string? Warning = null,
    string? GateKind = null)
{
    public static LaunchResult Ok(int? pid, string? warning = null) => new(true, pid, null, warning);
    public static LaunchResult Fail(string err) => new(false, null, err);
    public static LaunchResult Blocked(string gateKind, string err) => new(false, null, err, null, gateKind);
}
