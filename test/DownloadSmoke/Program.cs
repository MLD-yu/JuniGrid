using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JuniGrid.Services;

// ══════════════════════════════════════════════════════════════
//  JuniGrid 下载 + 分类 冒烟验证（独立进程，直接引用 JuniGrid.dll 调真实代码）
//
//  为什么需要它：xunit 套件里 SegmentDownloadTests 已覆盖分片/轮换/续传，
//  但整套 764 个用例要求测试宿主能在 %TEMP% 建目录；某些机器（安全软件拦截
//  JuniGridTestHarness.exe 的目录创建）会让整套跑不起来。这个冒烟程序不依赖
//  任何测试框架，进程一起来就能跑，用来在那种环境下确认下载与分类没坏。
//
//  跑法：
//    dotnet build JuniGrid/JuniGrid.csproj -c Debug
//    dotnet build test/DownloadSmoke -c Release
//    test/DownloadSmoke/bin/Release/net10.0-windows10.0.17763.0/DownloadSmoke.exe
//  退出码 0 = 全过，1 = 有失败项。
// ══════════════════════════════════════════════════════════════

var pass = 0;
var fail = 0;
var failures = new List<string>();

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  [OK]   {name}"); }
    else { fail++; failures.Add(name + (detail is null ? "" : " :: " + detail)); Console.WriteLine($"  [FAIL] {name}" + (detail is null ? "" : "  → " + detail)); }
}

void Eq<T>(string name, T actual, T expected)
    => Check(name, EqualityComparer<T>.Default.Equals(actual, expected), $"期望 [{expected}]，实际 [{actual}]");

// ─────────────────────────────────────────────
Console.WriteLine("\n═══ 一、分类（多选 / 拆分 / 拼接）═══");

// 拆分
Eq("Split(\"9,10\") 拆成两个", string.Join("|", ModCategoryUtil.Split("9,10")), "9|10");
Eq("Split 单值原样", string.Join("|", ModCategoryUtil.Split("9")), "9");
Eq("Split 空串 → 空", ModCategoryUtil.Split("").Length, 0);
Eq("Split null → 空", ModCategoryUtil.Split(null).Length, 0);
Eq("Split 纯空白 → 空", ModCategoryUtil.Split("   ").Length, 0);
Eq("Split 去首尾空白", string.Join("|", ModCategoryUtil.Split("  9 , 10  ")), "9|10");
Eq("Split 忽略空项", string.Join("|", ModCategoryUtil.Split("9,,10")), "9|10");
Eq("Split 去重（完全相同）", string.Join("|", ModCategoryUtil.Split("9,9,10")), "9|10");
Eq("Split 去重（带空格）", string.Join("|", ModCategoryUtil.Split("9, 9")), "9");
Eq("Split 去重（大小写）", string.Join("|", ModCategoryUtil.Split("Beauty,beauty")), "Beauty");
Eq("Split 保留中文分类名", string.Join("|", ModCategoryUtil.Split("美化,功能")), "美化|功能");

// 取第一个（决定列表落在哪一段）
Eq("First(\"9,10\") = 9", ModCategoryUtil.First("9,10"), "9");
Eq("First(\"10,9\") = 10（不是排序，是书写顺序）", ModCategoryUtil.First("10,9"), "10");
Eq("First(\"\") = 未分类", ModCategoryUtil.First(""), "");
Eq("First(null) = 未分类", ModCategoryUtil.First(null), "");
Eq("First 单值", ModCategoryUtil.First("9"), "9");
Eq("First 去空白", ModCategoryUtil.First(" 9 , 10 "), "9");
Eq("First 前导逗号 → 未分类", ModCategoryUtil.First(",9"), "");

// 拼接（写回配置）
Eq("Join 两个 → 逗号串", ModCategoryUtil.Join(["9", "10"]), "9,10");
Eq("Join 空集合 → 空（= 未分类）", ModCategoryUtil.Join(Array.Empty<string>()), "");
Eq("Join null → 空", ModCategoryUtil.Join(null), "");
Eq("Join 去空白 + 去重", ModCategoryUtil.Join([" 9 ", "9", "10"]), "9,10");
Eq("Join 全空项 → 空", ModCategoryUtil.Join(["", "   "]), "");
Eq("Join 脏数据顺带拆开", ModCategoryUtil.Join(["9,10", "11"]), "9,10,11");
Eq("Join 丢 null 项", ModCategoryUtil.Join(["9", null, "10"]), "9,10");

// 往返幂等：Join(Split(x)) 再 Split 一次结果不变（防止每次保存都膨胀）
var round1 = ModCategoryUtil.Join(ModCategoryUtil.Split("3, 1 ,3 , 7"));
Eq("往返幂等（规范化）", round1, "3,1,7");
Eq("往返幂等（再跑一次不变）", ModCategoryUtil.Join(ModCategoryUtil.Split(round1)), round1);

// 真实场景：老配置单值不能被改坏
Eq("老配置单值读取", ModCategoryUtil.First(ModCategoryUtil.Join(["9"])), "9");
// 真实场景：勾一个分类 + 空选
Eq("只勾一个 → 单值（不写逗号）", ModCategoryUtil.Join(["9"]), "9");
Eq("一个都不勾 → 空串（从分类表摘掉）", ModCategoryUtil.Join(Array.Empty<string?>()), "");

// ─────────────────────────────────────────────
Console.WriteLine("\n═══ 二、下载（真实分片引擎 + 本地假 CDN）═══");

// 工作目录放在 exe 旁边，不要用 %TEMP%：某些机器上安全软件会按「可执行文件所在
// 位置」判定写盘权限，未签名的程序写跨盘临时目录会直接被拒（实测 OneDrive 里的
// 这份 exe 写 E:\... 就被拒，写自己旁边没事）。
var work = Path.Combine(AppContext.BaseDirectory, "work-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(work);
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(work, true); } catch { } };

// 20MB 主文件 + 3MB 小文件
var bigBytes = new byte[20 * 1024 * 1024];
new Random(20261107).NextBytes(bigBytes);
var smallBytes = new byte[3 * 1024 * 1024];
new Random(42).NextBytes(smallBytes);
var bigMd5 = Convert.ToHexString(MD5.HashData(bigBytes)).ToLowerInvariant();

using var fast = new MiniServer(bigBytes, 0);
using var slow = new MiniServer(bigBytes, 15 * 1024);      // 每连接 ~15KB/s
using var small = new MiniServer(smallBytes, 0);

// 每种 handler 配置都按 NexusService 的真实配置来
static HttpClient MakeClient() => new(new SocketsHttpHandler
{
    EnableMultipleHttp2Connections = true,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    ConnectTimeout = TimeSpan.FromSeconds(10),
})
{ Timeout = TimeSpan.FromMinutes(3) };

string Md5Of(string path) => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

// ① 4 线全速：字节必须完全一致
{
    var dest = Path.Combine(work, "fast4.bin");
    using var http = MakeClient();
    var sw = Stopwatch.StartNew();
    var speeds = new List<double>();
    var ok = await SegmentDownloader.TryRunAsync(http, fast.Url + "/file.bin", dest, 4,
        (_, _, sp) => { if (sp is double d && d > 0) speeds.Add(d); });
    sw.Stop();
    Check("4 线下载返回 true", ok);
    Check("4 线下载字节完全一致（md5）", File.Exists(dest) && Md5Of(dest) == bigMd5);
    Check("下载过程上报过速度", speeds.Count > 0, $"收到 {speeds.Count} 次");
    Check("上报速度在合理区间(>1MB/s)", speeds.Count > 0 && speeds.Max() > 1.0, $"峰值 {speeds.DefaultIfEmpty(0).Max():F2} MB/s");
    Check("全速耗时 < 30s", sw.Elapsed.TotalSeconds < 30, $"{sw.Elapsed.TotalSeconds:F1}s");
    Check("没有残留分片文件", !File.Exists(dest + ".seg") && !File.Exists(dest + ".seg.state"));
    Console.WriteLine($"         （20MB / 4 线 / {sw.Elapsed.TotalSeconds:F1}s = {20 / sw.Elapsed.TotalSeconds:F2} MB/s）");
}

// ② lanes 超上限要被夹到 16，不能炸
{
    var dest = Path.Combine(work, "fast16.bin");
    using var http = MakeClient();
    var ok = await SegmentDownloader.TryRunAsync(http, fast.Url + "/file.bin", dest, 99, (_, _, _) => { });
    Check("lanes=99 不炸且成功", ok);
    Check("lanes=99 结果字节一致", File.Exists(dest) && Md5Of(dest) == bigMd5);
}

// ③ 小于 4MB 的文件拒绝分片（返回 false 让调用方走单连接）
{
    var dest = Path.Combine(work, "small.bin");
    using var http = MakeClient();
    var ok = await SegmentDownloader.TryRunAsync(http, small.Url + "/file.bin", dest, 4, (_, _, _) => { });
    Check("3MB 文件拒绝分片（返回 false）", !ok);
    Check("拒绝时不留半截文件", !File.Exists(dest) && !File.Exists(dest + ".seg"));
}

// ④ 镜像轮换：主节点 15KB/s×4，备用节点全速 → 必须切过去
{
    var dest = Path.Combine(work, "rotate.bin");
    using var http = MakeClient();
    var sw = Stopwatch.StartNew();
    var ok = await SegmentDownloader.TryRunAsync(http, slow.Url + "/file.bin", dest, 4,
        (_, _, _) => { }, default, null, null, [fast.Url + "/file.bin"]);
    sw.Stop();
    Check("慢节点触发轮换后仍成功", ok);
    Check("轮换后字节一致", File.Exists(dest) && Md5Of(dest) == bigMd5);
    Check("备用节点真的收到了请求", fast.GetRequests > 0, $"GET {fast.GetRequests} 次");
    // 纯慢速下 20MB 要 20MB/(4×15KB/s) ≈ 340s；轮换后应远小于它
    Check("比纯慢速快得多（< 90s）", sw.Elapsed.TotalSeconds < 90, $"{sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"         （慢节点起步 → 切快节点，总耗时 {sw.Elapsed.TotalSeconds:F1}s）");
}

// ⑤ 目标已存在：直接返回 false = "不用我再下"，调用方跳过（设计如此，不是失败）
{
    var dest = Path.Combine(work, "retry.bin");
    using var http = MakeClient();
    var ok1 = await SegmentDownloader.TryRunAsync(http, fast.Url + "/file.bin", dest, 4, (_, _, _) => { });
    var ok2 = await SegmentDownloader.TryRunAsync(http, fast.Url + "/file.bin", dest, 4, (_, _, _) => { });
    Check("第一次下载成功", ok1);
    Check("目标已存在时第二次返回 false（不重复下载）", !ok2);
    Check("已有文件内容仍正确", Md5Of(dest) == bigMd5);
}

// ⑥ 断点续传：中途取消 → .seg 必须保留 → 再跑能接上且字节正确
{
    var dest = Path.Combine(work, "resume.bin");
    using var http = MakeClient();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var ok1 = false;
    var threw = false;
    try
    {
        ok1 = await SegmentDownloader.TryRunAsync(http, slow.Url + "/file.bin", dest, 4,
            (_, _, _) => { }, cts.Token, null, () => true);
    }
    catch (OperationCanceledException) { threw = true; }
    Check("中断时抛取消（不是静默成功）", threw || !ok1);
    Check("暂停后保留 .seg（用户暂停不该白下）", File.Exists(dest + ".seg"));
    Check("暂停时没有假装下完", !File.Exists(dest));

    // 换到快节点续传：分片记账与 URL 无关，已下字节不该重来
    var ok2 = await SegmentDownloader.TryRunAsync(http, fast.Url + "/file.bin", dest, 4,
        (_, _, _) => { }, default, null, () => true);
    Check("续传后返回成功", ok2);
    Check("续传后字节完全一致", File.Exists(dest) && Md5Of(dest) == bigMd5);
    Check("续传后清掉 .seg", !File.Exists(dest + ".seg") && !File.Exists(dest + ".seg.state"));
}

// ⑦ md5 对不上时必须拒绝，不能把坏文件当成功
{
    var dest = Path.Combine(work, "badmd5.bin");
    using var http = MakeClient();
    var ok = false;
    var threw = false;
    try
    {
        ok = await SegmentDownloader.TryRunAsync(http, fast.Url + "/file.bin", dest, 4,
            (_, _, _) => { }, default, "00000000000000000000000000000000", null);
    }
    catch { threw = true; }
    Check("md5 不符时不报成功", !ok || threw);
    Check("md5 不符时没有产出成品文件", !File.Exists(dest));
}

// ⑥ 目标地址 404：必须干净失败、不留垃圾
{
    var dest = Path.Combine(work, "notfound.bin");
    using var http = MakeClient();
    var threw = false;
    var ok = false;
    try { ok = await SegmentDownloader.TryRunAsync(http, fast.Url + "/nope.bin", dest, 4, (_, _, _) => { }); }
    catch { threw = true; }
    Check("404 时不误报成功", !ok || threw);
    Check("404 时不留半截文件", !File.Exists(dest + ".seg"));
}

Console.WriteLine($"\n═══ 结果：{pass} 通过，{fail} 失败 ═══");
if (fail > 0)
{
    Console.WriteLine("失败项：");
    foreach (var f in failures) Console.WriteLine("  - " + f);
}
try { Directory.Delete(work, true); } catch { }
return fail == 0 ? 0 : 1;

// ══════════════════════════════════════════════════════════════
//  极简 HTTP 服务：TcpListener 手写（HttpListener 要 URL ACL，非管理员会失败）
//  支持 HEAD / GET / Range / 限速，够分片下载器用
// ══════════════════════════════════════════════════════════════
sealed class MiniServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly byte[] _data;
    private readonly int _bytesPerSecond;
    private readonly CancellationTokenSource _cts = new();
    private int _requests;

    public MiniServer(byte[] data, int bytesPerSecond)
    {
        _data = data;
        _bytesPerSecond = bytesPerSecond;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public int GetRequests => Volatile.Read(ref _requests);

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _c = client;
        try
        {
            var ns = client.GetStream();
            var head = await ReadHead(ns);
            if (head is null) return;
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0];
            var path = parts[1];
            if (!path.Contains("file.bin")) { await WriteHead(ns, "404 Not Found", 0, null, 0); return; }

            long from = 0, to = _data.Length - 1;
            var hasRange = false;
            foreach (var l in lines)
                if (l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = l[6..].Trim();
                    var i = v.IndexOf('=');
                    if (i >= 0) v = v[(i + 1)..];
                    var seg = v.Split('-');
                    if (seg.Length == 2)
                    {
                        if (long.TryParse(seg[0], out var f)) from = f;
                        if (long.TryParse(seg[1], out var t)) to = t;
                        if (to >= _data.Length) to = _data.Length - 1;
                        hasRange = true;
                    }
                }

            if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHead(ns, "200 OK", _data.Length, null, 0, acceptRanges: true);
                return;
            }

            Interlocked.Increment(ref _requests);
            var len = (int)(to - from + 1);
            if (hasRange) await WriteHead(ns, "206 Partial Content", len, $"{from}-{to}/{_data.Length}", _data.Length, true);
            else await WriteHead(ns, "200 OK", len, null, _data.Length, true);

            var chunk = 32 * 1024;
            var pos = (int)from;
            while (pos <= to && !_cts.IsCancellationRequested)
            {
                var n = (int)Math.Min(chunk, to - pos + 1);
                var t0 = DateTime.UtcNow;
                await ns.WriteAsync(_data.AsMemory(pos, n), _cts.Token);
                pos += n;
                if (_bytesPerSecond > 0)
                {
                    var need = n / (double)_bytesPerSecond;
                    var spent = (DateTime.UtcNow - t0).TotalSeconds;
                    if (spent < need) await Task.Delay(TimeSpan.FromSeconds(need - spent), _cts.Token);
                }
            }
        }
        catch { /* 客户端断开：正常 */ }
    }

    private static async Task<string?> ReadHead(NetworkStream ns)
    {
        var buf = new List<byte>(1024);
        var one = new byte[1];
        while (buf.Count < 16384)
        {
            var n = await ns.ReadAsync(one);
            if (n == 0) return null;
            buf.Add(one[0]);
            if (buf.Count >= 4 && buf[^4] == (byte)'\r' && buf[^3] == (byte)'\n' && buf[^2] == (byte)'\r' && buf[^1] == (byte)'\n')
                return Encoding.ASCII.GetString(buf.ToArray());
        }
        return null;
    }

    private static async Task WriteHead(NetworkStream ns, string status, int len, string? contentRange, int total, bool acceptRanges = false)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        sb.Append("Content-Length: ").Append(len).Append("\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n");
        if (acceptRanges) sb.Append("Accept-Ranges: bytes\r\n");
        if (contentRange is not null) sb.Append("Content-Range: bytes ").Append(contentRange).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await ns.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
