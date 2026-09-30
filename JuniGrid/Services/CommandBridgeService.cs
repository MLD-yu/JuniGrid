using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// 命令桥客户端：把日志页输入框里的命令真正交给 SMAPI 执行。
///
/// 为什么必须有这个桥：SMAPI 4 不读重定向进来的 stdin，跨进程往它的控制台注入按键在
/// ConPTY 下也不投递（两条都在本机实测过），公开 API 里也没有"执行一条命令"。唯一的
/// 通路是在游戏进程内反射它自己的 CommandManager —— 那需要一个 mod，于是有了
/// <c>smapi-bridge/</c> 这个 net6 小 mod。
///
/// 发现方式：mod 启动时把管道名和一次性 token 写进 <c>%APPDATA%\JuniGrid\cmdbridge.json</c>，
/// 我们读它并核对里面的 pid 还活着（游戏崩了/被强杀时文件会是旧的）。
/// </summary>
public sealed class CommandBridgeService
{
    /// <summary>装进用户 Mods 的目录名。和肖像覆盖包一样带 ~ 前缀，排在列表末尾好认。</summary>
    public const string ModFolder = "~JuniGrid Command Bridge";

    private const string PipeName = "junigrid-cmdbridge";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly ConfigService _cfg;

    public CommandBridgeService(ConfigService cfg) => _cfg = cfg;

    private static string HandshakePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid", "cmdbridge.json");

    public sealed record Bridge(string Pipe, string Token, int Pid, string Version);

    // ── 开关：状态直接就是文件夹状态，不另存配置字段 ────────────────────
    // 开 = Mods\<ModFolder>（不带点）；关 = Mods\.\<ModFolder>（点前缀 = SMAPI 不加载，
    // 和 Mods 页关包同一套语义）；从没开过 = 目录不存在。
    // 这样设计的好处：① 不需要往 ConfigService 加字段（那个文件此刻正被并行会话改）；
    // ② 用户在 Mods 页或文件管理器里手动改的名，界面上看到的状态也是真的。

    /// <summary>游戏到底装没装 SMAPI —— 没有它 Mods\ 根本没人读，放了也是死文件。</summary>
    public bool SmapiPresent()
    {
        var gp = _cfg.Current.GamePath;
        return !string.IsNullOrWhiteSpace(gp)
            && File.Exists(Path.Combine(gp, "StardewModdingAPI.dll"));
    }

    /// <summary>开关当前是不是"开"。</summary>
    public bool Enabled
    {
        get
        {
            var gp = _cfg.Current.GamePath;
            if (string.IsNullOrWhiteSpace(gp)) return false;
            return Directory.Exists(Path.Combine(gp, "Mods", ModFolder));
        }
    }

    /// <summary>开 = 确保存在且内容等于自带那份；关 = 点前缀禁用（不删，用户随时能开回来）。</summary>
    public string? SetEnabled(bool on)
    {
        var gp = _cfg.Current.GamePath;
        if (string.IsNullOrWhiteSpace(gp)) return LocService.Tr("还没定位游戏目录");
        var mods = Path.Combine(gp, "Mods");
        var on0 = Path.Combine(mods, ModFolder);
        var off0 = Path.Combine(mods, "." + ModFolder);

        if (!SmapiPresent()) return LocService.Tr("这个游戏还没装 SMAPI，命令桥装了也不会生效");
        if (GameProcessRunning()) return LocService.Tr("请先退出游戏，再切换命令桥");

        try
        {
            if (on)
            {
                if (Directory.Exists(off0) && !Directory.Exists(on0)) Directory.Move(off0, on0);
                var err = CopyBridgedInto(on0);
                return err;
            }
            if (Directory.Exists(on0)) Directory.Move(on0, off0);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>启动对齐：只负责"开着但那份是旧的"这一种情况——这就是自动更新，
    /// 不需要单独的功能。游戏在跑时 DLL 被锁，跳过就好，下次启动再补。</summary>
    public void ReconcileOnStartup()
    {
        try
        {
            if (!Enabled || GameProcessRunning() || !SmapiPresent()) return;
            var dst = Path.Combine(_cfg.Current.GamePath, "Mods", ModFolder);
            if (SameCopy(dst)) return;
            var err = CopyBridgedInto(dst);
            if (err is null)
                AppLog.Info("Bridge", "命令桥已随应用版本更新，下次启动游戏生效");
        }
        catch (Exception ex) { AppLog.Warn("Bridge", "命令桥对齐失败：" + ex.Message); }
    }

    private static string BridgeSourceDir => Path.Combine(AppContext.BaseDirectory, "bridge-mod");

    /// <summary>用户那份和我们自带那份是不是同一批字节。用哈希不用版本号：不需要人记得
    /// 每次改完 +1，也不会出现"版本号没动但文件变了"的撒谎情况。自带那份没有哈希文件
    /// （老版本装的）时一律当作"不一样" ⇒ 覆盖一次，正好把哈希补齐。</summary>
    private bool SameCopy(string dst)
    {
        try
        {
            var srcDll = Path.Combine(BridgeSourceDir, "JuniGridCommandBridge.dll");
            var dstDll = Path.Combine(dst, "JuniGridCommandBridge.dll");
            var stamp = Path.Combine(dst, StampFile);
            if (!File.Exists(srcDll) || !File.Exists(dstDll) || !File.Exists(stamp)) return false;
            return string.Equals(File.ReadAllText(stamp).Trim(), Sha256Of(srcDll), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Sha256Of(dstDll), Sha256Of(srcDll), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private const string StampFile = ".junigrid-bridge-sha256";

    private static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    /// <summary>把自带那份复制过去，并留下哈希戳供下次比对。</summary>
    private string? CopyBridgedInto(string dst)
    {
        if (!Directory.Exists(BridgeSourceDir)) return LocService.Tr("安装包里缺少命令桥文件");
        try
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(BridgeSourceDir))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(dst, name), true);
            }
            File.WriteAllText(Path.Combine(dst, StampFile),
                Sha256Of(Path.Combine(BridgeSourceDir, "JuniGridCommandBridge.dll")));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>桥当前可用吗（游戏在跑 + 握手文件对得上）。不碰管道，纯本地判断。</summary>
    public Bridge? Probe()
    {
        try
        {
            if (!File.Exists(HandshakePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(HandshakePath));
            var r = doc.RootElement;
            var pid = r.TryGetProperty("pid", out var p) ? p.GetInt32() : -1;
            // 游戏崩了或被任务管理器杀掉时不会来删这个文件，只认活着的 pid
            if (!ProcessAlive(pid)) return null;
            return new Bridge(
                r.TryGetProperty("pipe", out var x) ? x.GetString() ?? PipeName : PipeName,
                r.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "",
                pid,
                r.TryGetProperty("modVersion", out var v) ? v.GetString() ?? "" : "");
        }
        catch { return null; }
    }

    private static bool ProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try { System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch (ArgumentException) { return false; }   // 没这个 pid
        catch { return false; }
    }

    /// <summary>桥为什么不可用。五种状态必须分开说：混成一句"已复制"，用户只会以为输入框坏了。</summary>
    public enum BridgeState { Off, Ready, GameNotRunning, Incompatible, NoSmapi }

    public (BridgeState State, string Detail) State()
    {
        // 开关（= 目录在不在）是第一位的：关着的时候谈"游不游戏在跑"没有意义
        if (!Enabled) return (BridgeState.Off, "");
        if (Probe() is not null) return (BridgeState.Ready, "");
        var err = ReadBridgeError();
        if (err is not null) return (BridgeState.Incompatible, err.Value.Reason);
        if (!SmapiPresent()) return (BridgeState.NoSmapi, "");
        return (BridgeState.GameNotRunning, "");
    }

    private (string Reason, string SmapiVersion)? ReadBridgeError()
    {
        try
        {
            var p = Path.Combine(Path.GetDirectoryName(HandshakePath)!, "cmdbridge.error.json");
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            string Get(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            var reason = Get("reason");
            return reason.Length > 0 ? (reason, Get("smapiVersion")) : null;
        }
        catch { return null; }
    }

    /// <summary>游戏在跑时 mod 的 DLL 被进程锁住，复制/移动只会抛一句 Win32 文案。先探一下，
    /// 把"先退出游戏"说在人话里。</summary>
    public bool GameProcessRunning()
    {
        try
        {
            if (Probe() is not null) return true;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("StardewModdingAPI"))
            { try { return true; } finally { p.Dispose(); } }
            return false;
        }
        catch { return false; }
    }

    public sealed record Result(bool Ok, string Error);

    /// <summary>发一条命令给 SMAPI 并等它跑完。桥不在/超时都返回 Ok=false，不抛。</summary>
    /// <summary>发一条命令并等结果。传输层失败（管道被占用后释放、对端刚关）重试一次：
    /// 服务端同时只服务有限个连接，第一次撞上"没人接"是瞬时的，用户不该为此看到报错。</summary>
    public async Task<Result> SendAsync(string command, CancellationToken ct = default)
    {
        var first = await SendOnceAsync(command, ct);
        if (first.Ok || !LooksTransport(first.Error)) return first;
        await Task.Delay(400, ct);
        var second = await SendOnceAsync(command, ct);
        return second;
    }

    private static bool LooksTransport(string? err) =>
        err is not null && (err.Contains("Pipe", StringComparison.OrdinalIgnoreCase)
            || err.Contains("管道") || err.Contains("Not connected", StringComparison.OrdinalIgnoreCase)
            || err.Contains("超时"));

    private async Task<Result> SendOnceAsync(string command, CancellationToken ct)
    {
        var bridge = Probe();
        if (bridge is null || bridge.Token.Length == 0)
            return new Result(false, LocService.Tr("命令桥没在跑"));

        try
        {
            using var client = new NamedPipeClientStream(
                ".", bridge.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(2000);
            await client.ConnectAsync(connectCts.Token);

            using var reader = new StreamReader(client, Utf8NoBom);
            using var writer = new StreamWriter(client, Utf8NoBom) { AutoFlush = true };

            await writer.WriteLineAsync("{\"token\":\"" + bridge.Token + "\"}");
            var hello = await ReadLineAsync(reader, 3000, ct);
            if (hello is null) return new Result(false, LocService.Tr("命令桥没有响应"));
            if (!JsonFlag(hello, "ok")) return new Result(false, LocService.Tr("命令桥握手失败"));

            await writer.WriteLineAsync("{\"cmd\":" + Quote(command) + "}");
            // 命令本身可能跑几秒（patch export 要出图），给足 30 秒
            var reply = await ReadLineAsync(reader, 30000, ct);
            if (reply is null) return new Result(false, LocService.Tr("命令没有返回"));
            return new Result(JsonFlag(reply, "ok"), JsonText(reply, "error"));
        }
        catch (OperationCanceledException) { return new Result(false, LocService.Tr("等待命令超时")); }
        catch (Exception ex) { return new Result(false, ex.Message); }
    }

    private static async Task<string?> ReadLineAsync(StreamReader r, int ms, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ms);
        try { return await r.ReadLineAsync(timeout.Token); }
        catch (OperationCanceledException) { return null; }
    }

    private static bool JsonFlag(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static string JsonText(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s);
}
