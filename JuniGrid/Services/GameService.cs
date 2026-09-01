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
            if (Directory.Exists(p)) return p;
        return "";
    }

    public string? ProbeSmapiVersion(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return null;

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        if (!File.Exists(exe)) return null;

        // 1) Most reliable: the SMAPI exe carries its own assembly version.
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            var raw = info.FileVersion ?? info.ProductVersion;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw.Split('.');
                return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
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
