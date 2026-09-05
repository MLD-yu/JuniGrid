namespace JuniGrid.Services;

/// <summary>
/// 应用自身的基础信息。发新版本时只改这里的 Version，
/// 关于页显示和自更新比较都从这里取，避免两处版本号对不上。
/// </summary>
public static class AppInfo
{
    /// <summary>当前应用版本（不带 v 前缀）。</summary>
    public const string Version = "1.1.1";

    public const string RepoOwner = "MLD-yu";
    public const string RepoName  = "JuniGrid";

    /// <summary>Releases 下载页（发现新版本时跳转）。</summary>
    public static string ReleasesUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

    /// <summary>GitHub API：最新稳定版 Release。</summary>
    public static string LatestApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
}
