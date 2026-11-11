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
        _consoleShowsTrace = SmapiConsoleSettings.ConsoleShowsTrace(cfg.Current.GamePath);
    }

    /// <summary>SMAPI 控制台这一局会不会打印 TRACE（= 开了开发者模式）。见 <see cref="SmapiConsoleSettings"/>。
    /// 每代 tail 开始时重读：开发者模式改了要重启 SMAPI 才生效，重读的时机正好和它对齐。</summary>
    private bool _consoleShowsTrace;

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
    //
    // 2000 行是页面的可读上限。但**先被挤掉的永远是最前面那一段，也就是启动阶段** ——
    // 加载了几个 mod、跳过了哪些、报了什么错，全在那个头上；玩家玩两小时再回日志页，
    // 最想看的那段正好没了（v1.1.6 之前就踩过一次）。所以启动阶段整段护住，只滚后面的；
    // 滚掉多少行如实报出来（页脚），不再假装日志只有这么长。
    internal const int MaxLogLines = 2000;

    /// <summary>启动段的兜底上限：万一这一局根本没走到「加载完成」（卡在加载、直接崩），
    /// 也不能让保护区无限长到把整个缓冲吃光。</summary>
    internal const int MaxHeadLines = 900;

    /// <summary>SMAPI 加载完成的标志行（它之后才是游戏期日志）：
    /// <c>[.. INFO SMAPI] Type 'help' for help, or 'help &lt;cmd&gt;' for a command's usage</c>。
    /// 用它而不是固定行数划保护区 —— 大整合包光「Loaded 400 mods:」那份清单就三百多行。</summary>
    private const string StartupDoneMarker = "Type 'help' for help";

    private readonly List<string> _logBuffer = new();
    private readonly object _logLock = new();
    private int _droppedLines;
    private int _headEnd;   // 启动段结束位置（0 = 还没走到标志行，保护区按 MaxHeadLines 兜底）

    /// <summary>为守住 <see cref="MaxLogLines"/> 而被滚出视图的行数（启动阶段那一整段不计）。</summary>
    public int DroppedLineCount { get { lock (_logLock) return _droppedLines; } }

    /// <summary>保护区实际长度：走到标志行就是那一段，没走到就按上限兜底。</summary>
    internal static int HeadLength(int total, int headEnd) =>
        Math.Min(headEnd > 0 ? headEnd : MaxHeadLines, Math.Max(1, total));

    /// <summary>标志行落定时，启动段该保护多少行。
    /// <para>就是标志行的实际位置，不再压到 <see cref="MaxHeadLines"/> —— 那是「没走到标志行」
    /// 时的兜底，不是启动段的长度上限。压过之后被砍掉的那截启动段会被当成游戏期日志滚掉，
    /// 于是「控制台里往上翻还有、这里已经没了」。上界仍留 <c>MaxLogLines-200</c>：
    /// 启动段再长也得给游戏期留一屏（真到 1800 行以上，页脚会如实报滚掉了多少）。</para></summary>
    internal static int StartupHeadEnd(int count) =>
        Math.Min(count, MaxLogLines - 200);

    /// <summary>缓冲超限时该从保护区之后删掉多少行。
    /// 纯函数，只为让测试台能钉住「启动阶段永远不被挤掉」这条约定。</summary>
    internal static int TrimCount(int total, int head)
    {
        if (total <= MaxLogLines) return 0;
        head = Math.Clamp(head, 0, Math.Max(0, total - 1));
        return Math.Min(total - MaxLogLines, total - head);
    }

    private void RaiseLog(string line)
    {
        lock (_logLock)
        {
            _logBuffer.Add(line);
            if (_headEnd == 0 && line.Contains(StartupDoneMarker, StringComparison.Ordinal))
                // 标志行的实际位置就是启动段的长度（见 StartupHeadEnd 里为什么不再压到 MaxHeadLines）
                _headEnd = StartupHeadEnd(_logBuffer.Count);

            var head = HeadLength(_logBuffer.Count, _headEnd);
            var trim = TrimCount(_logBuffer.Count, head);
            if (trim > 0)
            {
                _logBuffer.RemoveRange(head, trim);
                _droppedLines += trim;
            }
        }
        OnLogLine?.Invoke(line);
    }

    /// <summary>新的一局开始：保护区重新划线。</summary>
    private void ResetStartupMark()
    {
        lock (_logLock) _headEnd = 0;
    }

    /// <summary>Copy of the buffered log lines, oldest first.</summary>
    public IReadOnlyList<string> GetLogSnapshot()
    {
        lock (_logLock) return _logBuffer.ToArray();
    }

    public void ClearLog()
    {
        lock (_logLock)
        {
            _logBuffer.Clear();
            _droppedLines = 0;
            _headEnd = 0;
        }
    }

    /// <summary>把命令桥收回来的输出并进日志视图。
    /// SMAPI 的控制台输出【不】写进 SMAPI-latest.txt（实测：日志里搜不到任何命令回显），
    /// 而日志页只尾随那个文件，所以命令结果只能由桥自己送回来。</summary>
    public void AppendLogLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var t = line.TrimEnd();
            if (t.Trim().Length > 0) RaiseLog(t);
        }
    }

    // ------------------------------------------------------------------
    // SMAPI log-file tailing
    // ------------------------------------------------------------------
    /// <summary>SMAPI 自己写的滚动日志。日志页显示的就是这个文件 —— 也是「完整日志」的出口：
    /// 页面只留最近 2000 行（启动阶段整段护住），更早的要看文件。</summary>
    public static string SmapiLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley", "ErrorLogs", "SMAPI-latest.txt");

    /// <summary>SMAPI 的 ErrorLogs 目录（滚动日志 + 崩溃报告都在这里）。</summary>
    public static string SmapiLogDir => Path.GetDirectoryName(SmapiLogPath)!;

    /// <summary>SMAPI 在游戏崩溃时写的独立报告：Message / InnerException / Stack Trace。
    /// 它<b>不在</b> SMAPI-latest.txt 里，我们原来从不读 —— 而「游戏闪退了」恰恰是玩家最想知道
    /// 原因的时刻，日志页却一片安静。这个类型只带元信息，内容由用户点开文件看。</summary>
    public sealed record CrashReport(string Path, DateTime Modified, string Message);

    /// <summary>ErrorLogs 里最近的一份崩溃报告（只认首行是 <c>Message:</c> 的文件，
    /// 不猜别的 .txt）。默认只找 3 天内的 —— 几个月前的旧崩溃不该天天弹在页头上。</summary>
    public static CrashReport? LatestCrashReport(TimeSpan? within = null)
    {
        try
        {
            var dir = SmapiLogDir;
            if (!Directory.Exists(dir)) return null;
            var since = DateTime.Now - (within ?? TimeSpan.FromDays(3));
            CrashReport? best = null;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.txt"))
            {
                if (f.Name.StartsWith("SMAPI-latest", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.LastWriteTime < since) continue;   // 早于窗口的直接跳过，不必读盘
                var msg = ReadCrashMessage(f.FullName);
                if (msg is null) continue;
                if (best is null || f.LastWriteTime > best.Modified)
                    best = new CrashReport(f.FullName, f.LastWriteTime, msg);
            }
            return best;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Launcher", "扫描 SMAPI 崩溃报告失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>崩溃报告首行是 <c>Message: 未将对象引用设置到对象的实例。</c>。
    /// 不是这个形状的文件一律返回 null（宁可不报，也不把别人的日志当崩溃）。</summary>
    private static string? ReadCrashMessage(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            for (var i = 0; i < 5; i++)
            {
                var line = sr.ReadLine();
                if (line is null) return null;
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (!t.StartsWith("Message", StringComparison.OrdinalIgnoreCase)) return null;
                var idx = t.IndexOfAny([':', '：']);
                return idx >= 0 ? t[(idx + 1)..].Trim() : t;
            }
            return null;
        }
        catch { return null; }
    }

    private CancellationTokenSource? _logTailCts;
    private int _logTailGen;
    private readonly object _logTailGate = new();   // 启动/停止 tail 的互斥：启动路径和 30 秒轮询会撞车

    /// <summary>我们在磁盘上认的 Mods 目录（= 设置里那个游戏目录下的 Mods）。
    /// 供日志页核对 SMAPI 自报的 "Mods go here: …" —— 两边不一致时 mod 装得再对也不会被加载，
    /// 而 SMAPI 对此不会报任何错。</summary>
    public string ConfiguredModsDir
    {
        get
        {
            var game = _cfg.Current.GamePath;
            return string.IsNullOrWhiteSpace(game)
                ? string.Empty
                : Path.Combine(game.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "Mods");
        }
    }

    // SMAPI 文件日志行头形如 [02:14:16 INFO  SMAPI]。非此形态的行是上一条的
    // 续行（多行消息/堆栈），跟随上一条所在组的可见性（SMAPI 控制台同款语义）。
    // 用完整头格式而非 line.StartsWith("[")，避免消息本身以 [ 开头的普通行误判成组头。
    // 级别用捕获组取，不要再对整行 Contains(" TRACE ") —— 消息正文里出现 TRACE 字样
    // 的 INFO/WARN/ERROR 行会把自己和它下面的续行（堆栈）一起当成 TRACE 组吞掉。
    private static readonly Regex LevelHeaderRegex =
        new(@"^\[\d{2}:\d{2}:\d{2}\s+(TRACE|DEBUG|INFO|WARN|ERROR|ALERT)\b", RegexOptions.Compiled);

    /// <summary>尾随时的「这一行留不留」判定，<paramref name="inTrace"/> 是 TRACE 组的跨行状态。
    ///
    /// TRACE 组整组丢弃（见下面循环里的注释），但加载阶段的「跳过/失败」证据例外 ——
    /// SMAPI 把「这个文件夹我没加载」只写在 TRACE 里。抽成纯函数是为了能被测试台钉住：
    /// 这里一旦回退成「TRACE 一律丢」，用户看到的就又是「mod 没进游戏，日志却一片安静」。</summary>
    internal static bool KeepLine(string line, ref bool inTrace, bool keepTrace = false)
    {
        var head = LevelHeaderRegex.Match(line);
        if (head.Success)
            inTrace = !keepTrace
                      && head.Groups[1].Value == "TRACE"
                      && !LogLineClassifier.IsLoadSkipNotice(line);
        return !inTrace;
    }

    /// <summary>本页的显示口径：SMAPI 控制台打印什么，这里就有什么。
    ///
    /// <para>控制台默认不打印 TRACE，所以这里默认也不收 TRACE；只有 SMAPI 自己开了开发者模式
    /// （<c>DeveloperMode: true</c>，此时控制台会打印 TRACE 且带完整时间/级别戳）才跟着收。
    /// 判断交给 <see cref="SmapiConsoleSettings"/>，取值来自 SMAPI 自己的 config.json ——
    /// 不另设开关，免得出现「软件里的口径」和「控制台里的口径」两套。</para></summary>
    public bool ConsoleShowsTrace => _consoleShowsTrace;

    /// <summary>一行日志进缓冲前的最后一道闸：点前缀（= 已禁用）的跳过证据攒起来折成一行，
    /// 其余行先把攒着的落地、自己再落地 —— 顺序不能乱，否则汇总会跑到它后面那些行之后。
    /// 抽成静态方法纯粹是为了让测试台能钉住「300 个禁用 mod 只出一行」这条约定。</summary>
    internal static void RouteLogLine(string line, SmapiSkipRollup rollup, Action<string> emit, bool collapse = true)
    {
        if (collapse && rollup.TryAdd(line)) return;
        rollup.Flush(emit);
        emit(line);
    }

    /// <summary>从一块新读到的字节里切出所有<b>完整</b>的行（以 \n 结尾），返回消费掉的字节数；
    /// 末尾那半行不消费，留到下一轮补齐再切 —— 否则一个中文 UTF-8 字符会被劈成半个。
    ///
    /// TRACE 组整组丢弃（见 KeepLine）。SMAPI 文件日志默认 verbose，游戏期 96% 是 TRACE
    /// （Content Patcher / Event Lookup 刷屏），而视图永远不显示 TRACE 组 —— 进缓冲只会把
    /// MaxLogLines 上限里的可见行挤掉，启动日志就是这么「玩着玩着没了」的。在源头整组丢弃。</summary>
    internal static int TakeCompleteLines(byte[] bytes, ref bool inTrace, Action<string> emit, bool keepTrace = false)
    {
        var lastNl = -1;
        for (var i = bytes.Length - 1; i >= 0; i--)
            if (bytes[i] == (byte)'\n') { lastNl = i; break; }
        if (lastNl < 0) return 0;

        // \n 不会出现在 UTF-8 多字节序列中间，按字节找换行是安全的
        foreach (var raw in Encoding.UTF8.GetString(bytes, 0, lastNl + 1).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (KeepLine(line, ref inTrace, keepTrace)) emit(line);
        }
        return lastNl + 1;
    }

    /// <summary>收尾：把文件末尾<b>没有换行</b>的最后一截也当作一行送出来。
    /// 崩溃、被强杀、被任务管理器结束时，SMAPI 最后一条日志往往没来得及写 \n ——
    /// 而那一句通常就是原因本身。tail 一停它就永远读不到了，所以停止前必须补这一次。</summary>
    internal static string? TakeTrailingLine(byte[] bytes, ref bool inTrace, bool keepTrace = false)
    {
        if (bytes.Length == 0) return null;
        var line = Encoding.UTF8.GetString(bytes).TrimEnd('\r');
        if (line.Trim().Length == 0) return null;
        return KeepLine(line, ref inTrace, keepTrace) ? line : null;
    }

    private void StartLogTail(bool readFromStart = false, (string? First, long Length)? prelaunch = null)
    {
        lock (_logTailGate)
        {
            var old = _logTailCts;
            var cts = new CancellationTokenSource();
            _logTailCts = cts;
            try { old?.Cancel(); old?.Dispose(); } catch { }   // v1.1.6：旧 CTS 释放，不再每次启动泄漏一个
            StartLogTailCore(cts.Token, ++_logTailGen, readFromStart, prelaunch);
        }
    }

    /// <summary>启动进程<b>之前</b>拍下磁盘上那份日志的身份（上一局的首行 + 长度）。
    ///
    /// <para>为什么非要在启动前拍：<c>Process.Start()</c> 到建好 tail 之间隔着几十毫秒，
    /// SMAPI 这期间完全可能已经把开头几行写进文件。若那时才把基线设成「文件当前长度」，
    /// 这几行就被当成「启动前就存在的内容」永远跳过 —— 表现正是<b>控制台里明明有、
    /// 日志页没有</b>。有这份旧身份，就能分辨「文件还没被重写」和「已经重写并写了内容」。</para>
    ///
    /// <para>拍不到（文件不存在/被占用）返回 <c>(null, 0)</c>，调用方按旧行为处理。</para></summary>
    private static (string? First, long Length) SnapshotLogBeforeLaunch()
    {
        try
        {
            var path = SmapiLogPath;
            if (!File.Exists(path)) return (null, 0);
            return (FirstLineOf(path), new FileInfo(path).Length);
        }
        catch { return (null, 0); }
    }

    /// <summary>决定从文件的哪个字节开始读，以及拿什么当「会话首行」。
    /// 抽成纯函数是因为这里是<b>唯一会静默漏掉 SMAPI 已打印内容</b>的地方：
    /// 判定错一次，开头那几行就永远不出现，而页面上没有任何迹象。</summary>
    internal static (long Pos, string? BaseFirstLine) ResolveTailBaseline(
        string? currentFirst, long currentLength, bool readFromStart, (string? First, long Length)? prelaunch)
    {
        // 从头补历史（接续一局已经在跑的游戏、或文件是空的）：不需要别的判断
        if (readFromStart || currentLength == 0) return (0, currentFirst);

        if (prelaunch is { } pre)
        {
            // 启动前根本没有旧日志 → 现在文件里的每一个字节都是这一局写的，必须从头读
            if (pre.First is null) return (0, currentFirst);
            // 首行还是上一局那句 → SMAPI 还没重写：先站到旧末尾，等它重写（首行变了）再从头读
            if (currentFirst == pre.First) return (pre.Length, pre.First);
            // 首行已经变了 → 重写发生了，而且（很可能）已经写了内容：从头读，一行都不落
            return (0, currentFirst);
        }

        // 没有基线（首次尾随一个已在运行的游戏）：保持旧行为，只看新写入的部分
        return (currentLength, currentFirst);
    }

    private void StartLogTailCore(CancellationToken token, int gen, bool readFromStart,
        (string? First, long Length)? prelaunch)
    {
        var path = SmapiLogPath;
        ResetStartupMark();   // 这一代 tail 重新划线；标志行会再出现一次，届时按实际位置定住

        // 显示口径：问 SMAPI 自己的配置，而不是另设一个开关。开发者模式下控制台会打印 TRACE，
        // 这里就跟着收；否则一行 TRACE 都不进缓冲（控制台里本来也没有）。
        _consoleShowsTrace = SmapiConsoleSettings.ConsoleShowsTrace(_cfg.Current.GamePath);

        // 基线：SMAPI 每次启动都重写整个文件，首行带时间戳必然变化，
        // 用「首行变了 / 文件变短」识别新会话，届时从头读。
        // readFromStart：接续已运行的游戏时从文件头读，把本会话历史补进视图。
        // prelaunch：启动进程之前拍下的旧文件身份 —— 有它才能判断「SMAPI 是不是已经写过几行了」。
        long pos = 0;
        string? baseFirstLine = null;
        try
        {
            if (File.Exists(path))
            {
                var curFirst = FirstLineOf(path);
                var len = new FileInfo(path).Length;
                (pos, baseFirstLine) = ResolveTailBaseline(curFirst, len, readFromStart, prelaunch);
            }
        }
        catch { /* 基线读不到就从 0 开始读 */ }

        _ = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            // TRACE 组跨读取轮次持续（半行留给下一轮，组状态同理）：
            // 组头后跟着的续行（多行消息/堆栈）与组头同生共死。
            var inTrace = false;

            // 「已禁用模组被跳过」的几百行折成一行再进缓冲（见 SmapiSkipRollup）：
            // 聚合状态跟着这一代 tail 走，所以要跨读取轮次持有。
            var skipRollup = new SmapiSkipRollup();
            // 口径在 StartLogTailCore 开头就定了（控制台会不会打印 TRACE），这一代 tail 内不再变。
            // 开发者模式下控制台逐条打印 TRACE，所以那时也不折叠 —— 折叠出来的那一行控制台里没有。
            var trace = _consoleShowsTrace;
            void Push(string line) => RouteLogLine(line, skipRollup, RaiseLog, collapse: !trace);
            var readFailed = false;
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
                            skipRollup.Flush(RaiseLog);   // 上一局的残留先交出去，再换轨
                            pos = 0;                    // 文件被新会话重写
                            baseFirstLine = first;
                            inTrace = false;            // 新会话的组状态从零起，别把上一局的 TRACE 组带过来
                            ResetStartupMark();         // 保护区也要重新划线
                        }
                        if (fs.Length > pos)
                        {
                            fs.Seek(pos, SeekOrigin.Begin);
                            using var ms = new MemoryStream();
                            int n;
                            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                                ms.Write(buf, 0, n);
                            pos += TakeCompleteLines(ms.ToArray(), ref inTrace, Push, keepTrace: trace);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 文件被占用等瞬态错误照旧下一轮再试，但别一声不吭 —— 如果是权限/路径
                    // 这类一直好不了的错，日志页会永远停在旧内容，而 juni-grid.log 里至少有一条。
                    if (!readFailed) { readFailed = true; AppLog.Warn("Launcher", "尾随 SMAPI 日志失败，继续重试: " + ex.Message); }
                }

                // SMAPI 扫完 Mods 就往下走了，正常下一行立刻把汇总冲出来；真停在这儿也不能一直不显示
                skipRollup.FlushIfIdle(RaiseLog, TimeSpan.FromSeconds(1));

                try { await Task.Delay(250, token); }
                catch (TaskCanceledException) { break; }
            }

            skipRollup.Flush(RaiseLog);

            // 收尾：把末尾没有换行的最后一截也送出来（崩溃/被强杀时最后一句往往没有 \n）。
            // 只在「还是我这一代 tail」时收尾 —— 否则会把上一局的残句塞进新会话。
            if (gen != _logTailGen) return;
            try
            {
                if (!File.Exists(path)) return;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length <= pos) return;
                fs.Seek(pos, SeekOrigin.Begin);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                var last = TakeTrailingLine(ms.ToArray(), ref inTrace, keepTrace: trace);
                if (last is not null) RaiseLog(last);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Launcher", "收尾读取 SMAPI 日志末行失败: " + ex.Message);
            }
        });
    }

    private void StopLogTail()
    {
        lock (_logTailGate)
        {
            var old = _logTailCts;
            _logTailCts = null;
            try { old?.Cancel(); old?.Dispose(); } catch { }
        }
    }

    /// <summary>直启本体时的进程名（Steam 官方模式）。</summary>
    private const string VanillaProcessName = "Stardew Valley";

    /// <summary>游戏进程名的唯一权威来源：SMAPI 启动时是 StardewModdingAPI，
    /// 直启本体时是 Stardew Valley。判活 / 清理工作集 / 优雅关闭都从这里取。
    /// <para>v1.3.4：启动器可以改名（<see cref="SmapiLauncher"/>），进程名也随之变 ——
    /// 所以这不是常量数组，而是「基础名 + 本机实际用过的启动器名」。</para></summary>
    private static readonly string[] BaseGameProcessNames = ["StardewModdingAPI", VanillaProcessName];

    private static readonly object _procNamesGate = new();
    private static string[] _gameProcessNames = BaseGameProcessNames;

    /// <summary>判活要认的全部进程名。</summary>
    public static string[] GameProcessNames
    {
        get { lock (_procNamesGate) return _gameProcessNames; }
    }

    /// <summary>把一个启动器 exe 的进程名并进判活名单（只为多认一个名字，从不移除已有的）。
    /// 由 ConfigService 在载入/保存自定义启动项时调用，也由本类在解析出改名启动器时调用 ——
    /// 少了这一步，「游戏是否在跑」会永远为假：游玩时长不累计、退出后关不掉进程、
    /// 30 秒轮询也接不上日志。
    /// <para>并进来的名字最多留 4 个：用户来回换启动器时不会无限增长，而多留一个
    /// 已不存在的进程名对判活没有副作用（GetProcessesByName 返回空而已）。</para></summary>
    public static void NoteLauncherProcess(string? exeOrName)
    {
        if (string.IsNullOrWhiteSpace(exeOrName)) return;
        var name = exeOrName.Trim();
        // 传进来的可能是完整路径，也可能只是进程名
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)
            || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(name)) return;

        lock (_procNamesGate)
        {
            if (_gameProcessNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return;
            if (_gameProcessNames.Length >= 4) return;
            _gameProcessNames = [.. _gameProcessNames, name];
            AppLog.Info("Launcher", $"启动器进程名已并入判活名单：{name}");
        }
    }


    /// <summary>系统里是否存在游戏进程（与是否由本程序启动无关）。</summary>
    public static bool IsGameProcessRunning() => AnyProcess(GameProcessNames);

    /// <summary>SMAPI 那个进程在不在（<b>不含</b>直启本体的 Stardew Valley）。
    /// 给「Mods 里的 dll 被进程锁住」这类判定用：纯原版在跑时那些 dll 根本没被加载，
    /// 一律说「请先退出游戏」只会挡人。启动器改名后进程名也跟着变，所以同样走这份名单。</summary>
    public static bool SmapiProcessRunning()
    {
        foreach (var name in GameProcessNames)
        {
            if (string.Equals(name, VanillaProcessName, StringComparison.OrdinalIgnoreCase)) continue;
            if (AnyProcess(name)) return true;
        }
        return false;
    }

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

    /// <summary>
    /// 游戏在运行但不是本程序启动的（例如 JuniGrid 被重启过、或在 Steam 里自己点了开始）→
    /// 接上 SMAPI 日志文件。可以反复调用：已经在尾随就什么都不做。
    /// </summary>
    public void AttachIfGameRunning() => EnsureLogTailAttached();

    /// <summary>兜底接续入口，押在已有的 30 秒进程轮询上（PlayTimeService.Tick）。
    ///
    /// 覆盖的正是「日志页一个字都没有」的另一半场景：玩家从 Steam 客户端自己点开始游戏，
    /// 我们既没启动过它、也没人调 StartLogTail，日志页就永远停在上一局 —— 而他确实在玩。
    /// 已经在尾随就直接返回：每 30 秒重读一遍文件会把整份日志反复灌进页面。</summary>
    public void EnsureLogTailAttached()
    {
        lock (_logTailGate)
        {
            if (_logTailCts is not null) return;          // 已经在尾随（自己启动的也走这条）
            if (!IsGameProcessRunning()) return;

            // 从文件头补历史，但只在文件确实是「这一局」的时候：纯原版启动时 ErrorLogs 里躺着的是
            // 上一次会话的旧文件，把它当本局日志重放出来比空白更误导（最后写入时间旧 = 不是这一局）。
            var replay = false;
            try
            {
                var path = SmapiLogPath;
                replay = File.Exists(path) && File.GetLastWriteTime(path) > DateTime.Now.AddMinutes(-10);
            }
            catch { /* 读不到时间就当不是本局 */ }
            StartLogTail(readFromStart: replay);
        }
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

    /// <summary>读一个 Process 对象「还活着吗」，且保证不抛。
    /// <see cref="Process.HasExited"/> 在两种我们真造得出来的形状下抛
    /// InvalidOperationException("No process is associated with this object.")：Start() 失败后
    /// 没关联上任何进程的对象、以及已 Dispose 的对象。判活在 1.5s 轮询 + 30s 统计 + 取消看门狗
    /// 三条路上跑，一抛就把调用方的循环带走 —— 所以这里宁可退化成「当它不在」。</summary>
    internal static bool IsAlive(Process? p)
    {
        if (p is null) return false;
        try { return !p.HasExited; }
        catch (Exception ex)
        {
            AppLog.Warn("LauncherService", "读 HasExited 抛异常，按「不在」处理: " + ex.Message);
            return false;
        }
    }

    public bool IsGameRunning => IsAlive(_smapiProcess) || AnyProcess(GameProcessNames);

    /// <summary>此刻系统里看到的每个游戏进程（进程名 + PID）。关闭动作做完仍有余留时，
    /// 要能把「是谁没被关掉」报到人眼前，而不是只进 juni-grid.log。</summary>
    public static (string Name, int Pid)[] SnapshotGameProcesses()
    {
        var found = new List<(string, int)>();
        foreach (var name in GameProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try { found.Add((name, p.Id)); }
                    catch (Exception ex) { AppLog.Warn("LauncherService", ex.Message); }
                }
            }
        }
        return found.ToArray();
    }

    /// <summary>谁该被报成「没关掉」。判据必须是<b>第二次看到还在</b>，不是"我杀的时候它在"：
    /// 从我们弹红条到玩家去查，中间几十秒很常见，而崩溃中的游戏恰恰就在这几十秒里被系统回收掉，
    /// 那时红条已经过期，玩家按图去找只会找到一个不存在的 PID，反过来怀疑我们的工具。
    /// 原因也一样只报实测到的：拿到异常就报异常原文，没拿到异常说明结束请求已被系统接受、
    /// 进程只是还挂在列表里 —— 不许写"可能是管理员权限"这种猜的话。</summary>
    internal static (string Name, int Pid, string Why)[] Survivors(
        (string Name, int Pid, string? Why)[] attempted, int[] stillPresent)
    {
        var present = new HashSet<int>(stillPresent);
        var left = new List<(string, int, string)>();
        foreach (var a in attempted)
            if (present.Contains(a.Pid))
                left.Add((a.Name, a.Pid, string.IsNullOrEmpty(a.Why)
                    ? LocService.Tr("结束请求已被系统接受，但它还挂在进程列表里")
                    : a.Why!));
        return left.ToArray();
    }

    /// <summary>再强杀一次此刻还在的游戏进程，等一秒后回看谁还在，把「是谁 + 为什么」带回来。
    /// 空数组 = 都关掉了。给「确认关闭游戏」这条玩家主动等结果的路径用。</summary>
    public static async Task<(string Name, int Pid, string Why)[]> KillAgainAndExplain()
    {
        var attempted = new List<(string, int, string?)>();
        foreach (var name in GameProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    int pid;
                    try { pid = p.Id; }
                    catch (Exception ex) { AppLog.Warn("LauncherService", ex.Message); continue; }
                    string? why = null;
                    try { p.Kill(entireProcessTree: true); }
                    catch (Exception ex) { why = ex.Message; AppLog.Warn("LauncherService", ex.Message); }
                    attempted.Add((name, pid, why));
                }
            }
        }
        if (attempted.Count == 0) return Array.Empty<(string, int, string)>();

        await Task.Delay(1500);
        return Survivors(attempted.ToArray(), SnapshotGameProcesses().Select(x => x.Pid).ToArray());
    }

    // 这里原来有一套「向 SMAPI 发命令」的东西（stdin 管道 + 936 编码 + debug 前缀白名单）——
    // 全部删除：SMAPI 根本不读我们建的管道（带不带控制台窗口两种启动都实测过），跨进程往它的
    // 控制台注入按键在 ConPTY 下也不投递（v1.6.9 试过，2026-09-30 再验一次，结论相同）。
    // 日志页的命令框因此改成「复制到剪贴板」，由用户在游戏内聊天框或 SMAPI 控制台粘贴。

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
        // 优先温和关闭所持有的 SMAPI 子进程。先摘下来再判活：不管它是活着、已退，
        // 还是个没关联上进程的残骸（Start() 失败留下的），这一次关完都不该再把它带进下一轮。
        var own = _smapiProcess;
        if (own is not null)
        {
            _smapiProcess = null;
            if (IsAlive(own))
            {
                try { own.CloseMainWindow(); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
                try { own.Kill(true); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
            }
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
    /// <summary>定这一局该拉起哪个 exe，并把它的进程名并入判活名单。
    /// 顺序与判据见 <see cref="SmapiLauncher.Resolve"/> —— 这里是唯一的调用入口，
    /// 「装了没有」「用哪个启动」「判活认哪些进程名」三件事必须同一口径。</summary>
    public LauncherPick ResolveLauncher(string? gamePath)
    {
        var pick = SmapiLauncher.Resolve(gamePath, _cfg.Current.SmapiLauncherExe);
        // 不管走哪条路都记下进程名：自定义/改名壳的进程名与基础名不同，判活不认它就等于游戏没在跑。
        if (pick.ProcessName is { Length: > 0 } processName) NoteLauncherProcess(processName);
        if (!string.IsNullOrEmpty(pick.Warning)) AppLog.Warn("Launcher", pick.Warning!);
        if (pick.Ok && pick.Origin is not LauncherOrigin.Standard)
            AppLog.Info("Launcher", $"本次启动项 = {pick.ExePath}" +
                $"（{(pick.Origin == LauncherOrigin.Custom ? "设置指定" : "自动识别的改名启动器")}）");
        return pick;
    }

    public PreFlightResult CheckSmapi(string gamePath) => CheckSmapiCore(gamePath, ResolveLauncher(gamePath));

    private static PreFlightResult CheckSmapiCore(string gamePath, LauncherPick pick)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return PreFlightResult.Fail(LocService.Tr("尚未设置游戏路径，请先到设置页选择。"));

        if (!Directory.Exists(gamePath))
            return PreFlightResult.Fail(LocService.Tf("游戏目录不存在：{0}", gamePath));

        if (pick.Origin == LauncherOrigin.Ambiguous)
        {
            // 认出多个候选就不猜：把名字列出来让用户自己指一下，比拉错一个 exe 强。
            var names = string.Join("、", pick.Candidates.Select(Path.GetFileName));
            return PreFlightResult.Fail(
                LocService.Tf("游戏目录里有多个像 SMAPI 启动器的 exe，无法确定用哪个：\n{0}\n\n" +
                              "请到「设置 → 游戏目录 → 启动项」里选中真正的那一个。", names));
        }

        if (!pick.Ok)
        {
            var lines = new List<string>
            {
                LocService.Tf("未找到 SMAPI 启动器：{0}", SmapiLauncher.StandardPath(gamePath)),
            };
            if (!string.IsNullOrEmpty(pick.Warning)) lines.Add(pick.Warning!);
            lines.Add(LocService.Tr("如果你把 StardewModdingAPI.exe 改了名（比如 SMAPI.exe），到「设置 → 游戏目录 → 启动项」点「浏览...」选中它就行。"));
            lines.Add(LocService.Tr("没装的话去 smapi.io 下载安装，或在首页切换到「Steam 官方」启动。"));
            return PreFlightResult.Fail(string.Join("\n", lines));
        }

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
    /// <summary>
    /// v1.6.10：不显示 SMAPI 控制台窗口 —— 日志页（尾随 SMAPI-latest.txt）+ 命令输入框取代它。
    /// 退出时不再自动带窗口重拉、也不再复述报错/版本提示（用户明确要"退出就退出了"）；
    /// SMAPI 自身的报错仍通过日志尾随实时显示，想看就在日志页看。
    /// </summary>
    public LaunchResult LaunchSmapi(string gamePath)
    {
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
        var pick = ResolveLauncher(gamePath);
        var check = CheckSmapiCore(gamePath, pick);
        if (!check.Success) return LaunchResult.Fail(check.Message!);

        // v1.2.4：版本锁定开启 → SMAPI 启动前同步锁状态。虽然不走 steam://，
        // Steam 客户端自己在后台也会自动更新已装游戏，锁的是同一个 manifest。
        // v1.6.8：只在历史版本降级生效时上锁；已回官方最新则解除（见 EnsureGameVersionLock）。
        SteamService.EnsureGameVersionLock(gamePath, _cfg.Current.SteamAppId, IsDowngradeLockNeeded());

        // v1.6.8：上次会话崩溃时 SMAPI 启动会打印「按任意键删除崩溃数据」并用
        // Console.ReadKey() 等键 —— ReadKey 遇到重定向输入直接抛 InvalidOperationException。
        // 启动前清掉崩溃标记，就把这个最常见的触发点消掉了（崩溃日志改名保留一份供排查）。
        ClearSmapiCrashState(gamePath);

        // 启动器可能被改名，也可能被用户指到别处 —— 一律用解析出来的那个，别再拼死文件名。
        var exe = pick.ExePath!;
        // 工作目录跟 exe 走：SMAPI 的壳按「自己所在的目录」找本体，改名的壳不在游戏根目录时
        // 拿 gamePath 当工作目录反而会把它引到错的地方。
        var workDir = Path.GetDirectoryName(exe) is { Length: > 0 } d ? d : gamePath;
        try
        {
            var startedAt = DateTime.Now;
            // 抢在 Start() 之前拍旧身份：进程一起来就会写日志头，晚一拍这几行就永远读不到了
            var prelaunch = SnapshotLogBeforeLaunch();
            _smapiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    // stdin/stdout/stderr 一律不接：以前接 stdin 是为了日志页那个命令框，
                    // 但 SMAPI 不读重定向进来的命令（实测），那条管道只是让我们误以为发出去了。
                    // stdout/stderr 更不能接 —— 没人持续读的话管道缓冲写满会把 SMAPI 卡死，
                    // 而日志页读的是 SMAPI 自己写的 ErrorLogs\SMAPI-latest.txt。
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
                // 退出就退出了：不打退出码、不复述 SMAPI 关键报错、不做版本不匹配提示，
                // 无头崩溃也不再自动带控制台重拉一次（用户明确不要这些）。SMAPI 自身的报错
                // 仍通过日志尾随实时显示，想看就在日志页看，我们不替它总结、也不重启。
                var gen = _logTailGen;
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    if (_logTailGen == gen) StopLogTail();
                });
            };

            proc.Start();
            _sessionStart = startedAt;
            ClearLog();
            // 日志第一行就写清「拉的是哪个 exe」—— 用改名/自定义启动器时，这一行是
            // 「我到底有没有按我指的那个启动」唯一的直接证据。
            RaiseLog($"[JuniGrid] 已启动 SMAPI 进程 ({pick.ExeName} · PID {proc.Id})"
                     + (showConsole ? "，控制台窗口已显示" : "，日志见下方尾随"));
            StartLogTail(prelaunch: prelaunch);

            return LaunchResult.Ok(proc.Id);
        }
        catch (Exception ex)
        {
            // Start() 没成功的对象永远关联不上进程，留在字段里 = 之后每一次判活都要抛一次
            _smapiProcess = null;
            return LaunchResult.Fail(ex.Message);
        }
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
        // 抽屉里暂存的档在这时放回，游戏列表恢复完整
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
    /// 启动前整理存档：放回上次收起的（列表回全）→ 把当前版本读不了的收进抽屉。
    ///
    /// 为什么要动这一步：星露谷只有一个存档目录、所有版本共用，而存档是单向升级的 ——
    /// 被 1.6 存过一次盘的档，1.0 点进去就是 NPC..ctor 里的 NullReferenceException 闪退到桌面；
    /// 更坏的是老版本的存档列表读不懂这些档，把它们显示成「农场主 1 / 农场 1 / 开局第 1 年」，
    /// 和玩家自己的真 1.0 档长得一模一样（1.0 的 SaveGameInfo 没有 createdDate/daysPlayed 可区分），
    /// 玩家没法靠自己点对行。所以不靠提示、直接把读不了的从列表里收走。
    ///
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
    /// 直启游戏本体（不经 steam://）。只在「抽屉里还压着别的档」时用：
    /// steam:// 会在游戏进程起来之前先跑一次 Auto-Cloud 的 `AC Launch,down`，把刚收走的档全数拉回
    /// Saves（06:36:29 实测：Saves 从 5 个目录回到 21 个），等游戏读存档列表时又是闪退现场。
    /// 直启不触发那次下载（22 次会话实测：20/20 经 Steam 的都有 `AC Launch`，2/2 直启的都没有），
    /// 退出时仍触发 `AC Exit` → 这一局的进度照样上传到云。
    ///
    /// 刻意**不**给 _smapiProcess 赋值：这条路上我们并没有真的持有那个子进程（Steam 才是它的
    /// 父进程），赋值会让「游戏是否在跑」之类的状态误判。运行态交给 TrackSteamExitAsync +
    /// AnyProcess，GameProcessNames 里本来就有 "Stardew Valley"。
    /// </summary>
    private LaunchResult LaunchVanillaDirectly(string gamePath, string? warning)
    {
        var exe = Path.Combine(gamePath, "Stardew Valley.exe");
        if (!File.Exists(exe))
            return LaunchResult.Fail(
                LocService.Tf("找不到游戏本体：{0}\n\n", exe) +
                LocService.Tr("这次本来要直启本体（避免 Steam 云把刚收起来的存档拉回来），但目录里没有 Stardew Valley.exe。"));
        try
        {
            _sessionStart = DateTime.Now;
            _ = TrackSteamExitAsync();
            var prelaunch = SnapshotLogBeforeLaunch();
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
            ClearLog();
            RaiseLog(LocService.Tr("（这一局直启游戏本体，未经 Steam、也没经过 SMAPI —— 本次不会产生 SMAPI 日志）"));
            StartLogTail(prelaunch: prelaunch);   // 直启本体不写 SMAPI 日志，但万一 SMAPI 仍被拉起（Steam 启动选项），照样要能接上
            AppLog.Info("Launcher", $"已直启游戏本体 (PID {proc.Id}) —— 未经 Steam，" +
                                    "避免云同步把抽屉里暂存的其它存档拉回来；退出时 Steam 仍会上传这一局的进度");
            return LaunchResult.Ok(proc.Id, warning);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(LocService.Tr("无法直启游戏本体：") + ex.Message);
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

    private LaunchResult LaunchGameCore(string steamAppId, int stashed)
    {
        // 前置：路径都没有 → 极大概率 Steam 账号未拥有此游戏或未安装
        if (string.IsNullOrWhiteSpace(_cfg.Current.GamePath) || !Directory.Exists(_cfg.Current.GamePath))
            return LaunchResult.Fail(
                LocService.Tr("未检测到 Stardew Valley 游戏目录。\n\n") +
                LocService.Tr("可能原因：\n") +
                LocService.Tr("  · 当前 Steam 账号未拥有本游戏（需先在 Steam 购买）\n") +
                LocService.Tr("  · 游戏未安装或路径异常 → 请在「设置」里手动指定目录\n\n") +
                LocService.Tr("启动器会尝试用 steam:// 协议拉起，若 Steam 弹\"此账号不拥有该游戏\"即为此因。"));

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

        // 同样抢在拉起之前拍：Steam 这次启动的是 SMAPI，日志开头不能丢
        var prelaunch = SnapshotLogBeforeLaunch();

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{steamAppId}",
                UseShellExecute = true
            });
            // Steam 模式原来根本不尾随日志：只有本程序启动（LaunchSmapi）才会 StartLogTail，
            // 于是走「Steam 官方」时日志页整局一动不动 —— 玩家以为日志坏了。
            // 游戏是通过 Steam 启动选项拉 SMAPI 的话，SMAPI 照样写同一份 SMAPI-latest.txt。
            ClearLog();
            RaiseLog(LocService.Tr("（已交给 Steam 启动：装了 SMAPI 的话，日志会实时显示在这里；纯原版不会有 SMAPI 日志）"));
            StartLogTail(prelaunch: prelaunch);
            return LaunchResult.Ok(null, warning);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(LocService.Tr("无法通过 Steam 启动：") + ex.Message);
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
