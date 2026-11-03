using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// v1.3.13 分片下载引擎的回归测试。
/// <para>全部打本地 TcpListener 假 CDN —— 不碰真网络，也不靠 Nexus 账号。
/// 假服务器只实现「读请求头 → 按 Range 回 206/200」这一条，正好覆盖引擎依赖的全部协议面：
/// 探测（bytes=0-0）、多车道 Range、以及"服务器忽略 Range 回 200 时必须放弃"。</para>
/// </summary>
public class SegmentDownloadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "jg-test-segment-" + Guid.NewGuid().ToString("N")[..8]);

    public SegmentDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] MakeBody(int size)
    {
        var buf = new byte[size];
        new Random(20261104).NextBytes(buf);
        return buf;
    }

    [Fact]
    public async Task Downloads_Identical_Bytes_Over_Multiple_Lanes()
    {
        var body = MakeBody(5 * 1024 * 1024);           // > MinTotalBytes(4MB)，能切成多片
        using var server = new FakeCdn(body, supportRange: true);
        var dest = Path.Combine(_dir, "pack.zip");

        var reports = new List<string>();
        var ok = await SegmentDownloader.TryRunAsync(
            new HttpClient(), server.Url + "/file.zip", dest, lanes: 4,
            (msg, _, _) => { lock (reports) reports.Add(msg); });

        Assert.True(ok, "支持 Range 的服务器应当分片成功");
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
        Assert.True(server.RequestCount > 1, $"应当是多条 Range 请求，实际 {server.RequestCount}");
        // 中途不能留下临时文件
        Assert.Empty(Directory.GetFiles(_dir, "*.seg"));
        lock (reports) Assert.Contains(reports, m => m.Contains("线并行"));
    }

    [Fact]
    public async Task Falls_Back_When_Server_Ignores_Range()
    {
        var body = MakeBody(5 * 1024 * 1024);
        using var server = new FakeCdn(body, supportRange: false);   // 永远回 200
        var dest = Path.Combine(_dir, "pack.zip");

        var ok = await SegmentDownloader.TryRunAsync(
            new HttpClient(), server.Url + "/file.zip", dest, lanes: 4, (_, _, _) => { });

        Assert.False(ok, "服务器忽略 Range 时必须交回单连接，不能自己拼");
        Assert.False(File.Exists(dest), "放弃时不该留下半成品");
        Assert.Empty(Directory.GetFiles(_dir, "*.seg"));
    }

    [Fact]
    public async Task Falls_Back_To_Single_Connection_When_A_Lane_Errors()
    {
        var body = MakeBody(5 * 1024 * 1024);
        // 探测（bytes=0-0）正常，但真正的分片请求全 500 → 引擎必须认输
        using var server = new FakeCdn(body, supportRange: true, failNonProbeRange: true);
        var dest = Path.Combine(_dir, "pack.zip");

        var ok = await SegmentDownloader.TryRunAsync(
            new HttpClient(), server.Url + "/file.zip", dest, lanes: 4, (_, _, _) => { });
        Assert.False(ok, "分片请求全挂时必须交回单连接");

        // 调用方（NexusService.DownloadFileAsync）此刻就会走这条单连接路径 —— 验证它确实能下完
        await ResumableDownload.RunAsync(new HttpClient(), server.Url + "/file.zip", dest, (_, _, _) => { });
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Skips_Small_Files()
    {
        var body = MakeBody(512 * 1024);                 // 小于 4MB 阈值
        using var server = new FakeCdn(body, supportRange: true);
        var dest = Path.Combine(_dir, "pack.zip");

        var ok = await SegmentDownloader.TryRunAsync(
            new HttpClient(), server.Url + "/file.zip", dest, lanes: 4, (_, _, _) => { });

        Assert.False(ok, "小文件分片不划算，应交给单连接");
    }

    [Fact]
    public async Task Skips_When_Destination_Already_Exists()
    {
        var body = MakeBody(5 * 1024 * 1024);
        using var server = new FakeCdn(body, supportRange: true);
        var dest = Path.Combine(_dir, "pack.zip");
        await File.WriteAllBytesAsync(dest, new byte[] { 1, 2, 3 });

        var ok = await SegmentDownloader.TryRunAsync(
            new HttpClient(), server.Url + "/file.zip", dest, lanes: 4, (_, _, _) => { });

        Assert.False(ok, "目标已存在 = 续传场景，不该走分片");
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(dest));
    }

    // ── md5 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Md5_Matches_And_Detects_Mismatch()
    {
        var path = Path.Combine(_dir, "a.bin");
        await File.WriteAllTextAsync(path, "junigrid");
        // "junigrid" 的 md5
        var good = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("junigrid")));

        Assert.Equal(DownloadIntegrity.Md5Verdict.Match, DownloadIntegrity.CheckMd5(path, good));
        Assert.Equal(DownloadIntegrity.Md5Verdict.Match, DownloadIntegrity.CheckMd5(path, good.ToLowerInvariant()));
        Assert.Equal(DownloadIntegrity.Md5Verdict.Mismatch,
            DownloadIntegrity.CheckMd5(path, new string('a', 32)));
    }

    [Fact]
    public async Task Md5_Is_Skipped_When_Expected_Is_Missing_Or_Malformed()
    {
        var path = Path.Combine(_dir, "b.bin");
        await File.WriteAllTextAsync(path, "x");
        Assert.Equal(DownloadIntegrity.Md5Verdict.Skipped, DownloadIntegrity.CheckMd5(path, null));
        Assert.Equal(DownloadIntegrity.Md5Verdict.Skipped, DownloadIntegrity.CheckMd5(path, ""));
        Assert.Equal(DownloadIntegrity.Md5Verdict.Skipped, DownloadIntegrity.CheckMd5(path, "not-a-hash"));
        // 文件不存在也不抛
        Assert.Equal(DownloadIntegrity.Md5Verdict.Skipped,
            DownloadIntegrity.CheckMd5(Path.Combine(_dir, "nope.bin"), new string('a', 32)));
    }

    /// <summary>最小假 CDN：接受连接 → 读请求头 → 按 Range 回 206（或不支持就回 200 整包）。</summary>
    private sealed class FakeCdn : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _body;
        private readonly bool _supportRange;
        private readonly bool _failNonProbeRange;
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public int RequestCount => Volatile.Read(ref _requests);
        public string Url { get; }

        public FakeCdn(byte[] body, bool supportRange, bool failNonProbeRange = false)
        {
            _body = body;
            _supportRange = supportRange;
            _failNonProbeRange = failNonProbeRange;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var header = await ReadHeadersAsync(stream);
                    Interlocked.Increment(ref _requests);

                    long from = 0, to = _body.Length - 1;
                    var hasRange = false;
                    var rangeLine = header.Split("\r\n").FirstOrDefault(l =>
                        l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
                    if (rangeLine is not null)
                    {
                        // Range: bytes=0-0
                        var spec = rangeLine["Range:".Length..].Trim();
                        if (spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = spec["bytes=".Length..].Split('-');
                            if (parts.Length == 2
                                && long.TryParse(parts[0], out var a)
                                && (string.IsNullOrEmpty(parts[1]) || long.TryParse(parts[1], out to)))
                            {
                                from = a;
                                if (parts.Length == 2 && string.IsNullOrEmpty(parts[1])) to = _body.Length - 1;
                                hasRange = true;
                            }
                        }
                    }

                    var partial = hasRange && _supportRange;
                    var sb = new StringBuilder();

                    // 模拟"探测能过、分片全 500"的坏节点
                    if (_failNonProbeRange && hasRange && !(from == 0 && to == 0))
                    {
                        sb.Append("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
                        return;
                    }

                    if (partial)
                    {
                        if (from >= _body.Length)
                        {
                            sb.Append("HTTP/1.1 416 Range Not Satisfiable\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
                            return;
                        }
                        to = Math.Min(to, _body.Length - 1);
                        var len = (int)(to - from + 1);
                        sb.Append("HTTP/1.1 206 Partial Content\r\n");
                        sb.Append($"Content-Range: bytes {from}-{to}/{_body.Length}\r\n");
                        sb.Append($"Content-Length: {len}\r\n");
                        sb.Append("Accept-Ranges: bytes\r\n");
                        sb.Append("Content-Type: application/zip\r\n");
                        sb.Append("Connection: close\r\n\r\n");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
                        await stream.WriteAsync(_body.AsMemory((int)from, len));
                    }
                    else
                    {
                        // 不支持 Range：无视请求头，整包 200
                        sb.Append("HTTP/1.1 200 OK\r\n");
                        sb.Append($"Content-Length: {_body.Length}\r\n");
                        sb.Append("Content-Type: application/zip\r\n");
                        sb.Append("Connection: close\r\n\r\n");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
                        await stream.WriteAsync(_body);
                    }
                }
            }
            catch { /* 假服务器只管回包，出错就当这条连接断了 */ }
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            var buf = new byte[4096];
            var sb = new StringBuilder();
            while (sb.ToString().Contains("\r\n\r\n") == false)
            {
                var n = await stream.ReadAsync(buf);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.Length > 16384) break;
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            _stop.Dispose();
        }
    }
}
