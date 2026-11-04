using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>多连接 Range 下载器。分片状态可在用户暂停后续跑；普通失败会清场并回落单连接。</summary>
public static class SegmentDownloader
{
    private const long MinTotalBytes = 4L * 1024 * 1024;
    private const long MinBytesPerLane = 512L * 1024;
    private const long CheckpointBytes = 1024L * 1024;
    private const int MaxLanes = 6;
    private const int LaneAttempts = 4;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);

    public static Func<bool>? EnabledHook;
    public static bool Enabled => EnabledHook?.Invoke() == true;
    public static Func<int>? LanesHook;
    public static int Lanes => Math.Clamp(LanesHook?.Invoke() ?? 4, 1, MaxLanes);

    private sealed record ProbeInfo(long Total, string? ETag, DateTimeOffset? LastModified);
    private sealed record ResumeState(int Version, long Total, int Lanes, string? ETag,
        string? ExpectedMd5,
        DateTimeOffset? LastModified, long[] Cursors);

    /// <returns>true when the complete file is at destPath; false when caller should use single-stream fallback.</returns>
    public static async Task<bool> TryRunAsync(HttpClient http, string url, string destPath,
        int lanes, Action<string, double?, double?> report, CancellationToken ct = default,
        string? expectedMd5 = null, Func<bool>? preservePartialOnCancellation = null)
    {
        if (lanes < 2) return false;
        lanes = Math.Clamp(lanes, 2, MaxLanes);
        var fullPath = Path.GetFullPath(destPath);
        var pathGate = PathGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await pathGate.WaitAsync(ct).ConfigureAwait(false);
        string? partialDataPath = null;
        string? checkpointPath = null;
        try
        {
            if (File.Exists(destPath)) return false;

            ProbeInfo probe;
            try { probe = await ProbeAsync(http, url, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
            if (probe.Total < MinTotalBytes)
            {
                if (probe.Total > 0)
                    AppLog.Info("Download", $"分片未启用：文件只有 {ResumableDownload.FormatBytes(probe.Total)}，小于 {ResumableDownload.FormatBytes(MinTotalBytes)} host={SafeHost(url)}");
                return false;
            }

            lanes = (int)Math.Min(lanes, Math.Max(1, probe.Total / MinBytesPerLane));
            if (lanes < 2) return false;

            var part = partialDataPath = destPath + ".seg";
            var statePath = checkpointPath = part + ".state";
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var starts = new long[lanes];
            var ends = new long[lanes];
            BuildRanges(probe.Total, lanes, starts, ends);

            ResumeState? state = null;
            if (File.Exists(part) || File.Exists(statePath))
            {
                state = TryReadState(statePath);
                if (!IsCompatible(state, probe, lanes, starts, ends, expectedMd5)
                    || !File.Exists(part)
                    || new FileInfo(part).Length != probe.Total)
                {
                    DeletePartial(part, statePath);
                    state = null;
                }
            }

            if (state is null)
            {
                using (var pre = new FileStream(part, FileMode.Create, FileAccess.Write,
                           FileShare.ReadWrite, 81920, useAsync: true))
                {
                    pre.SetLength(probe.Total);
                    await pre.FlushAsync(ct).ConfigureAwait(false);
                }
                state = new ResumeState(1, probe.Total, lanes, probe.ETag, NormalizeMd5(expectedMd5),
                    probe.LastModified, starts.ToArray());
                SaveState(statePath, state);
            }

            var checkpoint = new Checkpoint(statePath, state);
            var alreadyDone = 0L;
            for (var i = 0; i < lanes; i++) alreadyDone += checkpoint.Cursors[i] - starts[i];
            if (alreadyDone > 0)
                report($"正在下载（{lanes} 线并行）… {ResumableDownload.FormatBytes(alreadyDone)} / {ResumableDownload.FormatBytes(probe.Total)}",
                    Math.Min(100.0, alreadyDone * 100.0 / probe.Total), 0);

            if (Enumerable.Range(0, lanes).All(i => checkpoint.Cursors[i] > ends[i]))
            {
                if (await DownloadIntegrity.CheckMd5Async(part, expectedMd5, ct).ConfigureAwait(false)
                    == DownloadIntegrity.Md5Verdict.Mismatch)
                {
                    DeletePartial(part, statePath);
                    return false;
                }
                File.Move(part, destPath, overwrite: true);
                TryDelete(statePath);
                return true;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            long done = alreadyDone;
            var progressGate = new object();
            var clock = Stopwatch.StartNew();
            var lastAt = TimeSpan.Zero;
            long lastBytes = alreadyDone;

            void OnBytes(long add)
            {
                long snapshot;
                TimeSpan now;
                lock (progressGate)
                {
                    done += add;
                    snapshot = done;
                    now = clock.Elapsed;
                    if (now - lastAt < TimeSpan.FromSeconds(1) && snapshot != probe.Total) return;
                    lastAt = now;
                }
                var speed = (snapshot - lastBytes) / Math.Max(now.TotalSeconds, 0.001) / 1024.0 / 1024.0;
                lastBytes = snapshot;
                var pct = Math.Min(100.0, snapshot * 100.0 / probe.Total);
                report($"正在下载（{lanes} 线并行）… {ResumableDownload.FormatBytes(snapshot)} / {ResumableDownload.FormatBytes(probe.Total)}", pct, speed);
            }

            var tasks = new List<Task>(lanes);
            for (var i = 0; i < lanes; i++)
            {
                var lane = i;
                tasks.Add(LaneAsync(http, url, part, probe, starts[lane], ends[lane], lane,
                    checkpoint, OnBytes, linked.Token));
            }

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch
            {
                try { linked.Cancel(); } catch { }
                // User pause is the only cancellation path that retains the range data.
                // A failed lane, timeout, task removal or replaced worker cleans it before fallback.
                if (!ct.IsCancellationRequested || !ShouldPreserve(preservePartialOnCancellation))
                    DeletePartial(part, statePath);
                throw;
            }

            if (!Enumerable.Range(0, lanes).All(i => checkpoint.Cursors[i] > ends[i]))
            {
                DeletePartial(part, statePath);
                return false;
            }
            if (await DownloadIntegrity.CheckMd5Async(part, expectedMd5, ct).ConfigureAwait(false)
                == DownloadIntegrity.Md5Verdict.Mismatch)
            {
                DeletePartial(part, statePath);
                return false;
            }

            File.Move(part, destPath, overwrite: true);
            TryDelete(statePath);
            report($"正在下载（{lanes} 线并行）… {ResumableDownload.FormatBytes(probe.Total)} / {ResumableDownload.FormatBytes(probe.Total)}", 100, 0);
            AppLog.Info("Download", $"分片下载完成：{lanes} 线 {ResumableDownload.FormatBytes(probe.Total)} host={SafeHost(url)}");
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (partialDataPath is not null && checkpointPath is not null)
                DeletePartial(partialDataPath, checkpointPath);
            return false; // HttpClient timeout is a failed acceleration attempt, not user pause.
        }
        catch (OperationCanceledException)
        {
            if (!ShouldPreserve(preservePartialOnCancellation)
                && partialDataPath is not null && checkpointPath is not null)
                DeletePartial(partialDataPath, checkpointPath);
            throw;
        }
        catch (Exception ex)
        {
            if (partialDataPath is not null && checkpointPath is not null)
                DeletePartial(partialDataPath, checkpointPath);
            AppLog.Warn("Download", $"分片放弃，回落单连接：{ex.Message} host={SafeHost(url)}");
            return false;
        }
        finally { pathGate.Release(); }
    }

    private sealed class Checkpoint
    {
        private readonly string _path;
        private readonly object _sync = new();
        private readonly long[] _cursors;
        private readonly ResumeState _template;
        public long[] Cursors => _cursors;

        public Checkpoint(string path, ResumeState state)
        { _path = path; _template = state; _cursors = state.Cursors.ToArray(); }

        public long GetCursor(int lane) { lock (_sync) return _cursors[lane]; }

        public void Commit(int lane, long cursor)
        {
            lock (_sync)
            {
                if (cursor <= _cursors[lane]) return;
                _cursors[lane] = cursor;
                SaveState(_path, _template with { Cursors = _cursors.ToArray() });
            }
        }
    }

    private static void BuildRanges(long total, int lanes, long[] starts, long[] ends)
    {
        var segment = total / lanes;
        for (var i = 0; i < lanes; i++)
        {
            starts[i] = i * segment;
            ends[i] = i == lanes - 1 ? total - 1 : (i + 1) * segment - 1;
        }
    }

    private static bool IsCompatible(ResumeState? state, ProbeInfo probe, int lanes,
        long[] starts, long[] ends, string? expectedMd5)
    {
        if (state is null || state.Version != 1 || state.Total != probe.Total || state.Lanes != lanes
            || state.Cursors.Length != lanes) return false;
        if (!string.Equals(state.ExpectedMd5, NormalizeMd5(expectedMd5), StringComparison.OrdinalIgnoreCase)) return false;
        if (state.ETag is not null && !string.Equals(state.ETag, probe.ETag, StringComparison.Ordinal)) return false;
        if (state.LastModified is not null && state.LastModified != probe.LastModified) return false;
        for (var i = 0; i < lanes; i++)
            if (state.Cursors[i] < starts[i] || state.Cursors[i] > ends[i] + 1) return false;
        return true;
    }

    private static string? NormalizeMd5(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static ResumeState? TryReadState(string path)
    {
        try { return JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static void SaveState(string path, ResumeState state)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, path, overwrite: true);
    }

    private static void DeletePartial(string part, string state)
    { TryDelete(part); TryDelete(state); TryDelete(state + ".tmp"); }

    /// <summary>安全清理某个下载目标遗留的分片数据。与下载共用路径闸，避免删到新一轮正在续写的文件。</summary>
    public static async Task CleanupPartialArtifactsAsync(string destPath, Func<bool>? preserveIfReferenced = null,
        bool deleteDestination = false)
    {
        if (string.IsNullOrWhiteSpace(destPath)) return;
        string fullPath;
        try { fullPath = Path.GetFullPath(destPath); }
        catch { return; }
        var pathGate = PathGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await pathGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ShouldPreserve(preserveIfReferenced)) return;
            DeletePartial(fullPath + ".seg", fullPath + ".seg.state");
            if (deleteDestination)
            {
                // 单流下载直接写目标 zip，不受分片路径闸保护。移除暂停任务后等取消中的
                // FileStream 关闭再删；若路径已被另一个任务认领，每次重试前都重新检查引用。
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    if (ShouldPreserve(preserveIfReferenced)) return;
                    try { File.Delete(fullPath); return; }
                    catch (IOException) when (attempt < 29)
                    { await Task.Delay(100).ConfigureAwait(false); }
                    catch (UnauthorizedAccessException) when (attempt < 29)
                    { await Task.Delay(100).ConfigureAwait(false); }
                }
                AppLog.Warn("Download", "清理暂停任务半截包失败（文件仍被占用）: " + fullPath);
            }
        }
        catch (Exception ex) { AppLog.Warn("Download", "清理孤儿分片失败: " + ex.Message); }
        finally { pathGate.Release(); }
    }

    private static void TryDelete(string path)
    { try { File.Delete(path); } catch { } }

    private static bool ShouldPreserve(Func<bool>? predicate)
    { try { return predicate?.Invoke() == true; } catch { return false; } }

    private static async Task LaneAsync(HttpClient http, string url, string path, ProbeInfo probe,
        long from, long to, int lane, Checkpoint checkpoint, Action<long> onBytes, CancellationToken ct)
    {
        var cursor = checkpoint.GetCursor(lane);
        for (var attempt = 1; cursor <= to; attempt++)
        {
            try
            {
                cursor = await DownloadRangeOnceAsync(http, url, path, probe, cursor, to,
                    lane, checkpoint, onBytes, ct).ConfigureAwait(false);
                if (cursor <= to) throw new IOException($"分片没下完（{cursor}/{to + 1}）");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (attempt < LaneAttempts)
            {
                cursor = checkpoint.GetCursor(lane); // 未提交的数据允许覆盖，绝不跳过未落盘字节
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> DownloadRangeOnceAsync(HttpClient http, string url, string path,
        ProbeInfo probe, long cursor, long laneEnd, int lane, Checkpoint checkpoint,
        Action<long> onBytes, CancellationToken ct)
    {
        FileStream? dst = null;
        var committed = checkpoint.GetCursor(lane);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(cursor, laneEnd);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (res.StatusCode != HttpStatusCode.PartialContent)
                throw new HttpRequestException($"HTTP {(int)res.StatusCode}（Range 不受支持）");
            var range = res.Content.Headers.ContentRange;
            if (range?.From != cursor || range.To is null || range.To > laneEnd || range.Length != probe.Total)
                throw new InvalidDataException("CDN 返回的 Content-Range 与分片状态不匹配");
            if (probe.ETag is not null && !string.Equals(res.Headers.ETag?.ToString(), probe.ETag, StringComparison.Ordinal))
                throw new InvalidDataException("CDN 文件 ETag 已变化");
            if (probe.LastModified is not null && res.Content.Headers.LastModified != probe.LastModified)
                throw new InvalidDataException("CDN 文件修改时间已变化");

            var responseEnd = range.To.Value;
            await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            dst = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 81920, useAsync: true);
            dst.Seek(cursor, SeekOrigin.Begin);
            var buffer = new byte[81920];
            while (cursor <= responseEnd)
            {
                var want = (int)Math.Min(buffer.Length, responseEnd - cursor + 1);
                var read = await src.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                cursor += read;
                onBytes(read);
                if (cursor - committed >= CheckpointBytes)
                {
                    await dst.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    checkpoint.Commit(lane, cursor);
                    committed = cursor;
                }
            }
            if (cursor != responseEnd + 1) throw new IOException($"分片没下完（{cursor}/{responseEnd + 1}）");
            await dst.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            checkpoint.Commit(lane, cursor);
            return cursor;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (dst is not null)
            {
                await dst.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                checkpoint.Commit(lane, cursor);
            }
            throw;
        }
        finally
        {
            if (dst is not null) await dst.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string SafeHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

    private static async Task<ProbeInfo> ProbeAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (res.StatusCode != HttpStatusCode.PartialContent) return new ProbeInfo(-1, null, null);
            var len = res.Content.Headers.ContentRange?.Length;
            return len is > 0
                ? new ProbeInfo(len.Value, res.Headers.ETag?.ToString(), res.Content.Headers.LastModified)
                : new ProbeInfo(-1, null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new ProbeInfo(-1, null, null); }
    }
}
