using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Locates the Stardew Valley install and probes SMAPI metadata.
/// </summary>
public sealed class GameService
{
    public string DetectGamePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
            @"C:\Program Files\Steam\steamapps\common\Stardew Valley",
            @"D:\Steam\steamapps\common\Stardew Valley",
            @"D:\SteamLibrary\steamapps\common\Stardew Valley",
            @"E:\Steam\steamapps\common\Stardew Valley",
            @"E:\SteamLibrary\steamapps\common\Stardew Valley",
            @"F:\SteamLibrary\steamapps\common\Stardew Valley",
            Path.Combine(home, @"AppData\Local\Programs\Stardew Valley"),
            @"C:\GOG Games\Stardew Valley"
        };

        foreach (var p in candidates)
        {
            // 只认真正装了游戏的目录（有本体 exe）；
            // 残留的空目录直接跳过，继续找其他盘符下的安装
            if (Directory.Exists(p) && File.Exists(Path.Combine(p, "Stardew Valley.exe")))
                return p;
        }
        return "";
    }

    public string? ProbeSmapiVersion(string gamePath, bool freshRead = false)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return null;

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        if (!File.Exists(exe)) return null;

        // 1) Most reliable: the SMAPI exe carries its own assembly version.
        try
        {
            // v1.3.4：freshRead —— Windows 对版本资源有按路径的进程内缓存，SMAPI
            // 覆盖安装后【立即】重探会拿到旧版本号（实测：装完 4.5.2 列表仍显示
            // 4.5.1 并误报可更新）。freshRead 时拷到临时路径读，绕开缓存。
            var probePath = exe;
            if (freshRead)
            {
                probePath = Path.Combine(Path.GetTempPath(),
                    "jg-smapi-probe-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(exe, probePath, true);
            }
            try
            {
                var info = FileVersionInfo.GetVersionInfo(probePath);
                var raw = info.FileVersion ?? info.ProductVersion;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var parts = raw.Split('.');
                    return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
                }
            }
            catch (Exception vex) { AppLog.Warn("GameService", "读 SMAPI 版本资源失败: " + vex.Message); }
            finally
            {
                if (freshRead) { try { File.Delete(probePath); } catch { } }
            }
        }
        catch (Exception __ex) { AppLog.Warn("GameService", __ex.Message); }

        // 2) Fallback: smapi-internal metadata JSON (name varies by SMAPI build).
        foreach (var metaName in new[] { "SMAPI.metadata.json", "metadata.json" })
        {
            var metaPath = Path.Combine(gamePath, "smapi-internal", metaName);
            if (!File.Exists(metaPath)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("Version", out var v))
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch (Exception __ex) { AppLog.Warn("GameService", __ex.Message); }
        }

        // 3) 兵底：只能确认装了但读不到版本号。
        // 返回一个确定不会被 Version.TryParse 当成有效版本的占位符，
        // 且保证 CheckSmapiAsync 能识别出「本地版本未知但已装」。
        return null;   // 读不到版本 → 视为未识别，避免与 GitHub 版本比较时抖动
    }

    /// <summary>SMAPI 住在游戏根目录（主程序 + smapi-internal/），不在 Mods\ 里 —— 所以
    /// 「扫 Mods 判已装」对它永远为假，浏览页/详情页/依赖栏各自补过一遍特判。这里是一个入口：
    /// 装了没有看主程序文件在不在，版本号能读到就带上（读不到 Installed 仍为 true，
    /// 不能因为抽不出版本就说人没装 —— 那是 ProbeSmapiVersion 的口径，不是"装没装"的口径）。</summary>
    public (bool Installed, string? Version) SmapiState(string? gamePath, bool freshRead = false)
    {
        if (!HasSmapiLoader(gamePath)) return (false, null);
        return (true, ProbeSmapiVersion(gamePath!, freshRead));
    }

    /// <summary>游戏根下有没有 SMAPI 的主程序文件（exe 或 dll 任一）。
    /// 单独抽出来是给安装路由用的：「装了没有」这件事全应用只能有一个问法，
    /// 否则浏览页、详情页、依赖栏、安装路由会各说各话（历史上正是这样补了四遍特判）。</summary>
    public static bool HasSmapiLoader(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath)) return false;
        // exe 与 dll 任一即算装了：正常安装两者都有，但覆盖/中断安装会只剩一个，
        // 命令桥那条判据看的是 dll，依赖栏看的是 exe，两边都不该说假话。
        return File.Exists(Path.Combine(gamePath, "StardewModdingAPI.exe"))
            || File.Exists(Path.Combine(gamePath, "StardewModdingAPI.dll"));
    }

    // ------------------------------------------------------------------
    // SMAPI 自己的图标（从 exe 里抽取，转成 data URI 给界面用）
    // ------------------------------------------------------------------
    private string? _smapiIconCache;
    private string? _smapiIconForPath;

    public string? GetSmapiIconDataUri(string gamePath)
    {
        if (_smapiIconForPath == gamePath) return _smapiIconCache;
        _smapiIconForPath = gamePath;
        _smapiIconCache = null;
        try
        {
            var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
            if (!File.Exists(exe)) return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            _smapiIconCache = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception __ex) { AppLog.Warn("GameService", __ex.Message); }
        return _smapiIconCache;
    }
}
