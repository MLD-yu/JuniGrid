using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// v1.4.3：历史版本下载 —— 用 DepotDownloader 按 Manifest ID 拉历史包，
/// 再落到游戏目录（保留 Mods/）。官方分支走 Steam 客户端；这里补官方分支之外的旧版。
/// 工具本体装在程序安装目录 tools\DepotDownloader（插件内容，不进缓存/LocalAppData）。
/// </summary>
public sealed partial class DepotDownloaderService
{
    // 设置页会在后台预热授权组件；合并预热与用户点击，避免重复下载/解压。
    private readonly SemaphoreSlim _depotEnsureGate = new(1, 1);

    /// <summary>
    /// 同一个 Steam 账号允许最多 3 路并发会话。
    /// ⚠ 更正（2026-09-21 19:1x 实测）：先前"两路必互踢"的结论**是错的** —— 那时两个 DD 实例
    /// 用的是同一个默认 LogonID，Steam 把它们当成同一个客户端，后登录的顶掉前一个
    /// （症状就是两边都报「Lost connection to Steam」）。给每路发一个唯一 -loginid 之后
    /// 重测：两路并行 2 分多钟，两边零掉线，其中一路落了 464 MB。
    /// 上限留 3 而不是放开：一是国内到内容服务器本来就时通时不通（日志里成片的
    /// 「Connection timeout downloading chunk」），多路只会互相抢同一条带宽；
    /// 二是扫码授权那条路还要留位置。
    /// </summary>
    private const int MaxSteamSessions = 3;
    private static readonly SemaphoreSlim SessionGate = new(MaxSteamSessions, MaxSteamSessions);
    private static int _sessionHeld;

    /// <summary>是否已有 Steam 会话在飞（正在下载，或正在等人扫码）。</summary>
    public static bool SteamSessionBusy => Volatile.Read(ref _sessionHeld) > 0;

    /// <summary>当前在飞的会话数（测试台与诊断用）。</summary>
    public static int SteamSessionCount => Volatile.Read(ref _sessionHeld);

    private static int _loginIdSeed = Random.Shared.Next(1000, 999_999) * 1000;

    /// <summary>DepotDownloader 的 -loginid：同时跑多个实例时必须各不相同，
    /// 否则 Steam 认为是同一个客户端重复登录，会把先起的那一路顶掉。</summary>
    public static int NextLoginId() => Interlocked.Increment(ref _loginIdSeed);

    /// <summary>占一路 Steam 会话，Dispose 时归还。拿不到就在这儿等。
    /// ⚠ 这把闸不可重入：调用链上**只允许最底层这一处拿**，上层要排队状态就从
    /// <see cref="Progress.Queued"/> 读，别自己再拿一遍 —— 拿两遍会自己等自己，
    /// 第一路永远卡死、后面全部跟着排不上（45 个版本一起 0% 那次就是这么来的）。</summary>
    public static async Task<IDisposable> HoldSteamSessionAsync(CancellationToken ct = default)
    {
        await SessionGate.WaitAsync(ct);
        Interlocked.Increment(ref _sessionHeld);
        return new SessionSlot();
    }

    private sealed class SessionSlot : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref _sessionHeld);
            try { SessionGate.Release(); } catch (SemaphoreFullException) { }
        }
    }

    // P0-1：写游戏目录（ApplyStaging）全局互斥 —— 弹窗切换与后台下载完成后的 apply
    // 都会打同一 GamePath；无此锁时可并发清目录/拷本体/换 Mods，导致文件撕裂。
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);
    private static int _applyActive;

    /// <summary>是否有版本 apply 正在写游戏目录（UI 用来禁用其它版本操作）。</summary>
    public static bool IsApplyInProgress => Volatile.Read(ref _applyActive) > 0;

    /// <summary>
    /// 阶段耗时打点。此前整条切换链路没有任何计时（两个服务里 grep 不到 Stopwatch），
    /// 「哪一步几秒」只能靠日志行时间戳相减倒推，而没有日志输出的阶段完全是黑箱 ——
    /// 实测一次切换约 25 秒，开头 7 秒一行日志都没有，无法归因。
    /// Lap 记录上一段耗时并重新起表；Report 在 finally 里调，抛异常时也能看出停在哪一段。
    /// </summary>
    private sealed class PhaseTimer
    {
        private readonly List<string> _parts = new();
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private Stopwatch _lap = Stopwatch.StartNew();

        public void Lap(string name)
        {
            _parts.Add($"{name} {_lap.ElapsedMilliseconds}ms");
            _lap = Stopwatch.StartNew();
        }

        public void Report(string source, string head)
        {
            Lap(LocService.Tr("收尾"));
            AppLog.Info(source, $"{head}总 {_total.ElapsedMilliseconds}ms —— " + string.Join(" | ", _parts));
        }
    }

    /// <param name="Queued">true = 在等前面那路 Steam 会话；false = 已经轮到本人；
    /// null = 这条进度与排队无关（上层据此清掉排队标记）。</param>
    public sealed record Progress(string State, int? Percent, string? Line = null, bool? Queued = null);

    public sealed class DepotException(string message) : Exception(message);

    public DepotDownloaderService() { }

    private static readonly HttpClient DepotHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    static DepotDownloaderService()
    {
        // .NET Core 起 GBK（936）等代码页编码默认不可用，必须注册 CodePages 提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

}
