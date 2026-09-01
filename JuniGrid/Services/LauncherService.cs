using System.Diagnostics;
using System.IO;
using System.Text;

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
    public LauncherService(ConfigService cfg) { _cfg = cfg; }

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

    private void StartLogTail(bool readFromStart = false)
    {
        _logTailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _logTailCts = cts;
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
                                    if (line.Length > 0) RaiseLog(line);
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
        _logTailCts?.Cancel();
        _logTailCts = null;
    }

    /// <summary>
    /// 游戏在运行但不是本程序启动的（例如 JuniGrid 被重启过）→ 接上现有
    /// SMAPI 日志文件，从文件头把本会话内容补进日志视图。
    /// </summary>
    public void AttachIfGameRunning()
    {
        if (_smapiProcess is { HasExited: false }) return;   // 自己启动的，已在跟踪
        var running = Process.GetProcessesByName("StardewModdingAPI").Length > 0
                   || Process.GetProcessesByName("Stardew Valley").Length > 0;
        if (running) StartLogTail(readFromStart: true);
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
        _smapiProcess is { HasExited: false } ||
        Process.GetProcessesByName("StardewModdingAPI").Length > 0 ||
        Process.GetProcessesByName("Stardew Valley").Length > 0;

    /// <summary>能否向 SMAPI 控制台发命令：游戏须由本程序启动且未退出
    /// （接续的外部进程拿不到 stdin，输入框会置灰）。</summary>
    public bool CanSendCommand => _smapiProcess is { HasExited: false };

    // SMAPI 能直接识别的命令：核心命令 + 随 SMAPI 安装的 Console Commands mod（TrainerMod）。
    // 白名单外的输入视为游戏自带调试命令（如 money 5000、warp …），自动补 debug 前缀转发。
    private static readonly HashSet<string> SmapiKnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // SMAPI 核心
        "help", "harmony_summary", "reload_i18n",
        // Console Commands mod
        "apply_save_fix", "debug", "hurry_all", "list_items", "log_context",
        "player_add", "player_changecolor", "player_changestyle", "player_sethealth",
        "player_setmaxhealth", "player_setmaxstamina", "player_setmoney", "player_setname",
        "player_setstamina", "regenerate_bundles", "set_farm_type", "set_verbose",
        "show_data_files", "show_game_files",
        "world_clear", "world_downminelevel", "world_freezetime", "world_setday",
        "world_setminelevel", "world_setseason", "world_settime", "world_setyear"
    };

    /// <summary>向 SMAPI 控制台写入一条命令，等价于在 SMAPI 窗口输入后回车。
    /// 非 SMAPI 内置命令自动加 debug 前缀（游戏调试命令必须经 debug 转发才生效）。</summary>
    public bool SendCommand(string command)
    {
        var p = _smapiProcess;
        if (p is not { HasExited: false }) return false;
        try
        {
            var trimmed = command.Trim();
            var first = trimmed.Split(' ', 2)[0];
            var actual = SmapiKnownCommands.Contains(first) ? trimmed : "debug " + trimmed;
            RaiseLog("[JuniGrid] > " + actual);
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
        foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
            foreach (var p in Process.GetProcessesByName(name))
                try { p.CloseMainWindow(); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }

        // 给主界面进程一点写盘时间，再强制回收仍在的
        Task.Delay(300).ContinueWith(_ =>
        {
            foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
                foreach (var p in Process.GetProcessesByName(name))
                    try { p.Kill(true); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
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
        var steamRunning = Process.GetProcessesByName("steam").Length > 0
                        || Process.GetProcessesByName("steamwebhelper").Length > 0;
        if (!steamRunning)
            return PreFlightResult.Warn(
                "Steam 客户端似乎没在运行，将通过 steam:// 协议拉起（可能稍慢）。");
        return PreFlightResult.Ok();
    }

    // ------------------------------------------------------------------
    // Launch
    // ------------------------------------------------------------------
    public LaunchResult LaunchSmapi(string gamePath)
    {
        var check = CheckSmapi(gamePath);
        if (!check.Success) return LaunchResult.Fail(check.Message!);

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        try
        {
            _smapiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = gamePath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // SMAPI 在 LogManager 里把 Console.InputEncoding 设为 UTF-16LE（Windows 固定行为），
                    // 重定向 stdin 时它按 UTF-16 解码管道字节 —— 这里必须用【无 BOM】的 UTF-16LE 写入，
                    // 否则 SMAPI 读到的全是乱码，控制台命令永远无效（与输出侧 NUL 字符是同一机制）。
                    StandardInputEncoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            // 日志视图的内容源是 SMAPI-latest.txt（每行自带级别，颜色分类靠它）；
            // stdout 每行只有 "[SMAPI] 消息"不带级别，但仍要持续读走，防止管道写满阻塞游戏。
            _smapiProcess.OutputDataReceived += (_, _) => { };
            _smapiProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) RaiseLog("[ERR] " + CleanSmapiLine(e.Data));
            };
            _smapiProcess.Exited += (_, _) =>
            {
                OnGameExit();
                RaiseLog($"[JuniGrid] 游戏进程已退出，代码 {_smapiProcess?.ExitCode}");
                // 留 3 秒把退出前的尾部日志读完再停
                var gen = _logTailGen;
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    if (_logTailGen == gen) StopLogTail();
                });
            };

            _smapiProcess.Start();
            _sessionStart = DateTime.Now;
            // 新会话开新视图：清掉上一局的日志，避免新旧内容混在一起
            ClearLog();
            RaiseLog($"[JuniGrid] 已启动 SMAPI 进程 (PID {_smapiProcess.Id})");
            StartLogTail();
            _smapiProcess.BeginOutputReadLine();
            _smapiProcess.BeginErrorReadLine();

            return LaunchResult.Ok(_smapiProcess.Id);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// SMAPI 的重定向输出按 UTF-16LE 写出（每个 ASCII 字符后跟一个 0x00 字节），
    /// 我们按 UTF-8 解码后字符串里就夹了 NUL（\0）—— NUL 不可见，但在等宽字体 +
    /// pre-wrap 下每个都占一个字宽，日志看起来就是「每个字母之间都隔了一个空格」。
    /// SMAPI 控制台输出本身是纯 ASCII，剥掉 NUL/零宽字符即可完全还原。
    /// </summary>
    private static string CleanSmapiLine(string line)
    {
        if (line.IndexOf('\0') < 0) return line;
        return line.Replace("\0", "");
    }

    private async Task TrackSteamExitAsync()
    {
        // 等游戏进程起来，再等它退出
        for (int i = 0; i < 60 && _sessionStart is not null; i++)
        {
            if (System.Diagnostics.Process.GetProcessesByName("Stardew Valley").Length > 0) break;
            await Task.Delay(1000);
        }
        while (_sessionStart is not null &&
               System.Diagnostics.Process.GetProcessesByName("Stardew Valley").Length > 0)
        {
            await Task.Delay(3000);
        }
        OnGameExit();
    }

    public LaunchResult LaunchSteam(string steamAppId)
    {
        // 前置：路径都没有 → 极大概率 Steam 账号未拥有此游戏或未安装
        if (string.IsNullOrWhiteSpace(_cfg.Current.GamePath) || !Directory.Exists(_cfg.Current.GamePath))
            return LaunchResult.Fail(
                "未检测到 Stardew Valley 游戏目录。\n\n" +
                "可能原因：\n" +
                "  · 当前 Steam 账号未拥有本游戏（需先在 Steam 购买）\n" +
                "  · 游戏未安装或路径异常 → 请在「设置」里手动指定目录\n\n" +
                "启动器会尝试用 steam:// 协议拉起，若 Steam 弹\"此账号不拥有该游戏\"即为此因。");

        // Steam 模式也开秒表（Steam 官方模式下我们看不到子进程退出，靠 Stardew Valley.exe 探测）
        _sessionStart = DateTime.Now;
        _ = TrackSteamExitAsync();

        var check = CheckSteam();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{steamAppId}",
                UseShellExecute = true
            });
            return check.Success
                ? LaunchResult.Ok(null)
                : LaunchResult.Ok(null, check.Message);
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

public readonly record struct LaunchResult(bool Success, int? Pid, string? Error, string? Warning = null)
{
    public static LaunchResult Ok(int? pid, string? warning = null) => new(true, pid, null, warning);
    public static LaunchResult Fail(string err) => new(false, null, err);
}
