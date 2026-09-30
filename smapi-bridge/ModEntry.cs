using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using StardewModdingAPI;

namespace JuniGrid.CommandBridge
{
    /// <summary>
    /// JuniGrid 的命令桥：让启动器的日志页输入框能把命令真正交给 SMAPI 执行。
    ///
    /// 为什么要这么个东西：SMAPI 4 的公开 API 里【没有】"执行一条命令"这一项
    /// （实测 StardewModdingAPI.xml 里 ICommandHelper 只有 Add；老版的
    /// helper.Console.ExecuteCommand 在 4.x 已经不存在），而另外两条路都实测不通 ——
    /// SMAPI 不读重定向进来的 stdin，跨进程往它的控制台注入按键在 ConPTY 下也不投递。
    /// 所以只能在本进程里反射它自己的内部管道：
    ///   helper.ConsoleCommands → (私有字段)CommandManager
    ///   CommandManager.TryParse(整行, out name, out args, out Command, out errPos)
    ///   Command.Callback(name, args)   ← 就是控制台回车后走的那个委托
    /// 反射点全部按【类型名/方法名】查找而不是写死签名，SMAPI 改名时这里会明确报
    /// "不支持的版本"而不是把游戏搞崩。
    /// </summary>
    public class ModEntry : Mod
    {
        private const string PipeName = "junigrid-cmdbridge";
        private const string BridgeVersion = "1.0.0";
        private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);

        private object _manager = null!;
        private MethodInfo _tryParse = null!;
        private PropertyInfo _callback = null!;

        private string _token = "";
        private readonly ConcurrentQueue<Pending> _queue = new();
        private CancellationTokenSource _cts = new();
        private string _handshakePath = "";

        public override void Entry(IModHelper helper)
        {
            if (!TryResolveCommandManager(helper, out var why))
            {
                // 起不来就安静地退场：桥不可用时启动器那边会退回"复制到剪贴板"，
                // 绝不能因为反射失败而让游戏加载报错。但原因要留下 —— 启动器只有拿到
                // "为什么不行"，才能把「桥不支持这个 SMAPI」和「没装桥」「游戏没开」
                // 在界面上分开说，而不是弹一句莫名其妙的"已复制"。
                WriteBridgeError(why);
                this.Monitor.Log("命令桥未启用：" + why, LogLevel.Warn);
                return;
            }
            TryDeleteBridgeError();

            _token = NewToken();
            _handshakePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "JuniGrid", "cmdbridge.json");
            WriteHandshake();

            _cts = new CancellationTokenSource();
            var thread = new Thread(() => ServePipes(_cts.Token))
            {
                IsBackground = true,
                Name = "JuniGrid.CommandBridge",
            };
            thread.Start();

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.GameLaunched += (_, _) => this.Monitor.Log(
                "命令桥已就绪（命名管道 \\\\.\\pipe\\" + PipeName + "）", LogLevel.Info);
            // 这里原来还订阅了 ReturnedToTitle ⇒ 删握手文件。删掉这条订阅：回标题界面时
            // SMAPI 进程还活着、命令照样能跑，抹掉握手只会让启动器"看起来断了桥"。
            // 游戏被强杀留下的旧文件由启动器那边的 pid 存活校验兜住。

            helper.Events.GameLoop.SaveLoaded += (_, _) => WriteHandshake();
        }

        // ── 反射装配 ────────────────────────────────────────────────────

        private bool TryResolveCommandManager(IModHelper helper, out string why)
        {
            _manager = null!; _tryParse = null!; _callback = null!;
            why = "";

            const BindingFlags anyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var helperObj = helper.ConsoleCommands;
            if (helperObj is null) { why = "helper.ConsoleCommands 为 null"; return false; }

            // 字段名可能随版本变，所以按【类型名】找而不是写死 "CommandManager"
            var type = helperObj.GetType();
            foreach (var f in type.GetFields(anyInstance))
                if (f.FieldType.Name == "CommandManager") { _manager = f.GetValue(helperObj); break; }
            if (_manager is null)
                foreach (var p in type.GetProperties(anyInstance))
                    if (p.PropertyType.Name == "CommandManager") { _manager = p.GetValue(helperObj); break; }
            if (_manager is null)
            {
                why = "在 " + type.FullName + " 里找不到 CommandManager 字段（SMAPI 版本结构变了）";
                return false;
            }

            var mgrType = _manager.GetType();
            _tryParse = mgrType.GetMethod("TryParse", anyInstance);
            if (_tryParse is null) { why = "CommandManager.TryParse 不存在"; _manager = null!; return false; }

            var cmdType = mgrType.Assembly.GetType("StardewModdingAPI.Framework.Command");
            if (cmdType is null) { why = "找不到 Framework.Command 类型"; _manager = null!; return false; }
            _callback = cmdType.GetProperty("Callback", anyInstance);
            if (_callback is null) { why = "Command.Callback 不存在"; _manager = null!; return false; }

            return true;
        }

        /// <summary>把一整行命令交给 SMAPI 执行。返回 null 表示成功，否则是失败原因。</summary>
        private string? Execute(string line)
        {
            var argv = new object?[] { line, null, null, null, null };
            bool parsed;
            try { parsed = (bool)_tryParse.Invoke(_manager, argv)!; }
            catch (TargetInvocationException ex) { return ex.InnerException?.Message ?? ex.Message; }
            catch (Exception ex) { return ex.Message; }

            var command = argv[3];
            if (!parsed || command is null)
            {
                var pos = argv[4] is int i ? i : -1;
                return "SMAPI 没认出这条命令" + (pos >= 0 ? "（第 " + (pos + 1) + " 个字符起）" : "")
                    + "。用 help 看可用命令。";
            }

            var cb = _callback.GetValue(command) as Delegate;
            if (cb is null) return "这条命令没有可执行的回调。";

            try { cb.DynamicInvoke(argv[1], argv[2]); }
            catch (TargetInvocationException ex) { return ex.InnerException?.Message ?? ex.Message; }
            catch (Exception ex) { return ex.Message; }
            return null;
        }

        // ── 游戏线程：队列里的命令在 UpdateTicked 上跑 ──────────────────

        private void OnUpdateTicked(object? sender, EventArgs e)
        {
            while (_queue.TryDequeue(out var p))
            {
                if (p.Done.IsSet) continue;                 // 客户端已经超时走了
                p.Error = Execute(p.Input);
                p.Done.Set();
            }
        }

        // 输出【不】从这里回传：实测 SMAPI 会把控制台里显示的东西一并写进
        // %APPDATA%\StardewValley\ErrorLogs\SMAPI-latest.txt（help 的分节标题在文件里
        // 命中 16 处，patch invalidate 那句 WARN 也在），而启动器的日志页尾随的就是这个
        // 文件。桥再送一份 = 同一条出现两遍。所以这里只回"成没成 + 我们自己的错因"，
        // 也不再临时改 Console.Out（SMAPI 的彩色控制台是它自己 new 出来的 writer，
        // 换掉它有 InvalidCastException 的风险，犯不上）。

        // ── 管道服务 ────────────────────────────────────────────────────

        private void ServePipes(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    // 实例数给到 4：原来只给 1，一个连上却不说话的客户端会把这条唯一管道占满，
                    // 期间启动器连不上，看到的就是一次莫名其妙的失败。
                    // ⚠ 刻意【不带】PipeOptions.Asynchronous：异步模式的 NamedPipeStream
                    // CanTimeout=false，给它设 ReadTimeout 会当场抛
                    // InvalidOperationException("该流不支持超时") —— 会话一个字节都没读到就
                    // 被 Dispose，客户端只看到"管道已损坏"（2026-10-01 实测，日志：
                    // 管道会话异常：InvalidOperationException 该流不支持超时）。
                    // 这条线程本来就是专用阻塞线程，同步读没有代价。
                    pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte);
                    pipe.WaitForConnection();
                    HandleClient(pipe);
                }
                catch (IOException io)
                {
                    // 客户端半路走了 / 对端已关：这是正常现象，但要说出来，
                    // 否则"Pipe is broken"只会留在启动器那一侧，这边一片安静。
                    try { this.Monitor.Log("管道会话中断：" + io.Message, LogLevel.Debug); } catch { }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    try { this.Monitor.Log("管道会话异常：" + ex.GetType().Name + " " + ex.Message, LogLevel.Warn); } catch { }
                }
                finally { try { pipe?.Dispose(); } catch { } }
            }
        }

        private void HandleClient(Stream pipe)
        {
            // ⚠ 这里【不设】ReadTimeout。2026-10-01 实测：NamedPipeServerStream 的
            // CanTimeout 在 .NET（同步、异步两种构造都试了）恒为 false，赋值直接抛
            // InvalidOperationException("Timeouts are not supported on this stream") ——
            // 上一版就是这一行把每个会话打死在读第一个字节之前，启动器只看到"管道已损坏"。
            // 要真空闲回收只能改用 WaitToReadAsync + CancellationToken；眼下实例数已给到 4，
            // 且启动器每次发送都是短连接、发完就 Dispose，占满 4 个的概率极低，不值得为此
            // 把同步读循环翻成异步。宁可没有看门狗，也不能再有"设了必炸"。

            // 不套 using：reader/writer 共用同一个流，谁 dispose 都会把流关掉，
            // 而流的生命周期归 ServePipes 的 finally 管（原来两边都 using = 关三次）。
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            var hello = reader.ReadLine();
            // 握手必须有回音：否则客户端分不清"桥没起来"和"命令跑挂了"，只能干等超时。
            if (hello is null || !TokenOk(hello))
            {
                writer.WriteLine(Json(("ok", false), ("error", "auth-failed")));
                return;
            }
            writer.WriteLine(Json(("ok", true), ("error", ""), ("version", BridgeVersion)));

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var input = JsonRead(line, "cmd");
                if (string.IsNullOrWhiteSpace(input)) { writer.WriteLine(Json(("ok", false), ("error", "空命令"))); continue; }

                var p = new Pending(input);
                _queue.Enqueue(p);
                var finished = p.Done.Wait(RunTimeout);
                writer.WriteLine(Json(
                    ("ok", finished && p.Error is null),
                    ("error", finished ? (p.Error ?? "") : "游戏 20 秒没有处理这条命令（可能卡在读档/存档）")));
            }
        }

        private bool TokenOk(string hello) => JsonRead(hello, "token") == _token && _token.Length > 0;

        // ── 握手文件：启动器靠它发现桥、拿到一次性 token ────────────────

        private void WriteHandshake()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_handshakePath)!);
                File.WriteAllText(_handshakePath,
                    "{\"pipe\":\"" + PipeName + "\",\"token\":\"" + _token
                    + "\",\"pid\":" + Environment.ProcessId
                    + ",\"modVersion\":\"" + BridgeVersion + "\"}");
            }
            catch { /* 写不了握手文件只是发现不了，不影响游戏 */ }
        }

        /// <summary>反射装配失败时留的病历：启动器靠它把"桥不支持这个 SMAPI"讲清楚。</summary>
        private static string BridgeErrorPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JuniGrid", "cmdbridge.error.json");

        private static void WriteBridgeError(string why)
        {
            try
            {
                var dir = Path.GetDirectoryName(BridgeErrorPath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(BridgeErrorPath,
                    "{\"reason\":" + Escape(why)
                    + ",\"smapiVersion\":\"" + (typeof(IModHelper).Assembly.GetName().Version?.ToString() ?? "?")
                    + "\",\"bridgeVersion\":\"" + BridgeVersion + "\"}");
            }
            catch { /* 病历写不了也只是说不清原因，不影响游戏 */ }
        }

        private static void TryDeleteBridgeError()
        {
            try { if (File.Exists(BridgeErrorPath)) File.Delete(BridgeErrorPath); }
            catch { }
        }

        private static string NewToken()
        {
            var b = new byte[16];
            RandomNumberGenerator.Fill(b);
            var sb = new StringBuilder(32);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // ── 极简 JSON（只处理我们自己这几个字段，不引第三方库）──────────

        private static string JsonRead(string json, string key)
        {
            var v = JsonValue(json, key);
            return v ?? "";
        }

        private static string Json(params (string key, object? val)[] pairs)
        {
            var sb = new StringBuilder("{");
            for (var i = 0; i < pairs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(pairs[i].key).Append("\":");
                switch (pairs[i].val)
                {
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case null: sb.Append("\"\""); break;
                    default: sb.Append(Escape(pairs[i].val!.ToString() ?? "")); break;
                }
            }
            return sb.Append('}').ToString();
        }

        private static string? JsonValue(string json, string key)
        {
            var at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0) return null;
            at = json.IndexOf(':', at + key.Length + 2);
            if (at < 0) return null;
            at++;
            while (at < json.Length && (json[at] == ' ' || json[at] == '\t')) at++;
            if (at >= json.Length || json[at] != '"') return null;
            var sb = new StringBuilder();
            at++;
            while (at < json.Length)
            {
                var c = json[at];
                if (c == '\\' && at + 1 < json.Length)
                {
                    var n = json[at + 1];
                    sb.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => n });
                    at += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                at++;
            }
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private sealed class Pending
        {
            public Pending(string input) { Input = input; }
            public string Input { get; }
            public string? Error { get; set; }
            public ManualResetEventSlim Done { get; } = new(false);
        }
    }
}
