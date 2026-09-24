namespace JuniGrid.Services;

/// <summary>
/// 应用自身的基础信息。版本号的唯一来源是 csproj 的 &lt;Version&gt;，
/// 这里在运行时从程序集版本读取 —— 关于页显示、自更新比较和 Nexus
/// Application-Version 头永远与发行构建一致（AUP 要求），不会出现手改遗漏。
/// </summary>
public static class AppInfo
{
    /// <summary>当前应用版本（不带 v 前缀），取自程序集版本（csproj 的 Version）。
    /// Nexus Application-Version 头 / User-Agent / 关于页 / 自更新比较全部用它。</summary>
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public const string RepoOwner = "MLD-yu";
    public const string RepoName  = "JuniGrid";

    /// <summary>Releases 下载页（发现新版本时跳转）。</summary>
    public static string ReleasesUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

    /// <summary>GitHub API：最新稳定版 Release。</summary>
    public static string LatestApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
}
