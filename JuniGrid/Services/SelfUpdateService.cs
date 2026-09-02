using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>一次自更新检查的结果。</summary>
public sealed record SelfUpdateInfo(string LatestVersion, string DownloadUrl, string SetupUrl, bool HasUpdate);

/// <summary>
/// 应用自更新的唯一通道（检查 + 下载缓存 + 弹出安装）：
///  1) api.github.com /releases/latest —— 信息全，但匿名配额 60 次/小时/IP，容易被限流；
///  2) github.com/.../releases/latest HTML 302 回落 —— 最终跳转 URL 里带 tag，不受 API 配额限制。
/// 安装包下载到 StoragePaths.SelfUpdateDir（断点续传）：
///  · 已完整缓存 → 点击按钮直接弹安装向导，不再下载；
///  · 安装包向导被用户关掉 → 缓存保留，下次点击直接再弹；
///  · 安装成功（应用版本 >= 安装包版本）后，下次启动自动清掉缓存。
/// </summary>
public sealed class SelfUpdateService
{
    private const string TagMarker = "/releases/tag/";
    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient DownloadHttp = CreateClient(minutes: 10);
    private volatile SelfUpdateInfo? _latest;

    /// <summary>最近一次检查结果；null = 还没查到（未检查或失败）。</summary>
    public SelfUpdateInfo? Latest => _latest;

    /// <summary>检查完成后通知（UI 订阅刷新角标/按钮）。</summary>
    public event Action? Changed;

    private static HttpClient CreateClient(double minutes = 0.2)
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        h.Timeout = TimeSpan.FromMinutes(minutes);
        return h;
    }

    /// <summary>启动后台预检查（不阻塞 UI，失败静默），顺带清掉已装完的旧安装包缓存。</summary>
    public void StartBackgroundCheck()
        => _ = Task.Run(async () =>
        {
            CleanupOldInstallers();
            try { await CheckAsync(); } catch { }
        });

    /// <summary>请求 GitHub 最新版本并与当前版本比较（API 失败自动走 HTML 回落）。</summary>
    public async Task<SelfUpdateInfo?> CheckAsync()
    {
        SelfUpdateInfo? result = await TryCheckViaApiAsync() ?? await TryCheckViaHtmlAsync();
        if (result is not null)
        {
            _latest = result;
            try { Changed?.Invoke(); } catch { }
        }
        return _latest;
    }

    /// <summary>安装包是否已完整缓存（下载成功后有 .done 标记）。</summary>
    public string? CachedInstallerPath(SelfUpdateInfo info)
    {
        var dest = InstallerPath(info.LatestVersion);
        return File.Exists(dest) && File.Exists(dest + ".done") ? dest : null;
    }

    public static string InstallerPath(string version)
        => Path.Combine(StoragePaths.SelfUpdateDir, $"JuniGrid-cn-v{version}-setup.exe");

    /// <summary>
    /// 确保安装包已缓存：已完整缓存直接返回路径；否则下载（断点续传）到缓存目录。
    /// </summary>
    public async Task<string> EnsureInstallerAsync(SelfUpdateInfo info,
        Action<string, double?>? progress, CancellationToken ct = default)
    {
        var cached = CachedInstallerPath(info);
        if (cached is not null)
        {
            progress?.Invoke("安装包已就绪", 100);
            return cached;
        }

        Directory.CreateDirectory(StoragePaths.SelfUpdateDir);
        var dest = InstallerPath(info.LatestVersion);
        await ResumableDownload.RunAsync(DownloadHttp, info.SetupUrl, dest,
            (msg, pct, _) => progress?.Invoke(msg, pct), ct: ct);

        // 下载完整结束才写 .done 标记；取消/中断留下的半截文件靠续传接着写
        File.WriteAllText(dest + ".done", info.LatestVersion);
        progress?.Invoke("下载完成", 100);
        return dest;
    }

    /// <summary>弹出安装向导（可见向导，非静默）。旧版应用由调用方自行退出，
    /// 因此不带 /CLOSEAPPLICATIONS —— 不会弹「关闭应用」询问框；
    /// 向导里点「完成」后 Inno 按 [Run] 自动启动新版本；向导被直接关掉也不影响缓存。</summary>
    public void LaunchInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        { UseShellExecute = true });
    }

    /// <summary>启动时清理：缓存安装包的版本 <= 当前应用版本 = 已装完，删掉缓存。</summary>
    private static void CleanupOldInstallers()
    {
        try
        {
            var dir = StoragePaths.SelfUpdateDir;
            if (!Directory.Exists(dir)) return;
            if (!Version.TryParse(AppInfo.Version.Trim(), out var current)) return;

            foreach (var exe in Directory.GetFiles(dir, "JuniGrid-cn-v*-setup.exe"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    Path.GetFileName(exe), @"\d+(?:\.\d+)+");
                if (!m.Success || !Version.TryParse(m.Value, out var v)) continue;
                if (v <= current)
                {
                    File.Delete(exe);
                    if (File.Exists(exe + ".done")) File.Delete(exe + ".done");
                    AppLog.Warn("SelfUpdate", $"已清理旧版安装包缓存：{Path.GetFileName(exe)}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"清理安装包缓存失败：{ex.Message}");
        }
    }

    // 通道 1：GitHub API（信息全，但有限流）
    private async Task<SelfUpdateInfo?> TryCheckViaApiAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(AppInfo.LatestApiUrl);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn("SelfUpdate", $"API 通道失败：HTTP {(int)resp.StatusCode}，改走 HTML 回落");
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            return Build(tag);
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"API 通道异常：{ex.Message}，改走 HTML 回落");
            return null;
        }
    }

    // 通道 2：releases/latest 页面 302 重定向里抠 tag（SMAPI 检查同款方案，无配额限制）
    private async Task<SelfUpdateInfo?> TryCheckViaHtmlAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(AppInfo.ReleasesUrl + "/latest");
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var marker = finalUrl.IndexOf(TagMarker, StringComparison.OrdinalIgnoreCase);
            if (!resp.IsSuccessStatusCode || marker < 0)
            {
                AppLog.Warn("SelfUpdate", $"HTML 回落失败：HTTP {(int)resp.StatusCode}");
                return null;
            }
            var tag = finalUrl[(marker + TagMarker.Length)..];
            return Build(tag);
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"HTML 回落异常：{ex.Message}");
            return null;
        }
    }

    private static SelfUpdateInfo? Build(string tag)
    {
        // tag 允许带 v 前缀（v1.0.2 / 1.0.2 都认）
        var ver = tag.TrimStart('v', 'V');
        if (!Version.TryParse(ver, out var latest)) return null;
        if (!Version.TryParse(AppInfo.Version.Trim(), out var current)) return null;

        // Release 资产固定命名：JuniGrid-cn-vX.Y.Z-setup.exe
        var setupUrl = $"{AppInfo.ReleasesUrl}/latest/download/JuniGrid-cn-v{ver}-setup.exe";
        var hasUpdate = latest > current;
        return new SelfUpdateInfo(ver, $"{AppInfo.ReleasesUrl}/tag/v{ver}", setupUrl, hasUpdate);
    }
}
