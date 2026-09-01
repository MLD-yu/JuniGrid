using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JuniGrid.Services;

/// <summary>
/// Reads the locally signed-in Steam account (persona name + cached avatar)
/// straight from Steam's own config files — no network and no API key needed.
///
/// Data sources:
///   HKCU\Software\Valve\Steam → SteamPath          (install location)
///   &lt;steam&gt;\config\loginusers.vdf             (accounts, MostRecent flag)
///   &lt;steam&gt;\config\avatarcache\&lt;sid64&gt;*.jpg (cached avatar images)
/// </summary>
public sealed class SteamService
{
    private SteamProfile? _cached;

    public SteamProfile GetProfile() => _cached ??= LoadProfile();

    /// <summary>Drop the cache and re-read from disk (e.g. user switched accounts).</summary>
    public void Refresh()
    {
        _cached = null;
        GetProfile();
    }

    private static SteamProfile LoadProfile()
    {
        try
        {
            var steamPath = FindSteamPath();
            if (steamPath is null) return SteamProfile.None;

            var loginUsers = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsers)) return SteamProfile.None;

            var text = File.ReadAllText(loginUsers);

            string? sid = null, persona = null, account = null;
            long bestTs = -1;
            var foundMostRecent = false;

            // loginusers.vdf: blocks keyed by SteamID64, each holding
            // "AccountName" / "PersonaName" / "MostRecent" / "Timestamp".
            foreach (Match m in Regex.Matches(text,
                "\"(?<sid>\\d{17})\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline))
            {
                var body = m.Groups["body"].Value;
                var mostRecent = GetVdfValue(body, "MostRecent") == "1";
                var ts = long.TryParse(GetVdfValue(body, "Timestamp"), out var t) ? t : 0;

                // Prefer the block flagged MostRecent=1; otherwise the newest Timestamp wins.
                if ((mostRecent && !foundMostRecent) || (!foundMostRecent && ts > bestTs))
                {
                    foundMostRecent |= mostRecent;
                    bestTs = Math.Max(bestTs, ts);
                    sid = m.Groups["sid"].Value;
                    persona = GetVdfValue(body, "PersonaName");
                    account = GetVdfValue(body, "AccountName");
                }
            }

            var accountCount = Regex.Matches(text, "\"(?<sid>\\d{17})\"\\s*\\{", RegexOptions.Singleline).Count;

            if (sid is null) return SteamProfile.None;

            return new SteamProfile(persona, account, LoadAvatar(steamPath, sid), true, accountCount);
        }
        catch
        {
            return SteamProfile.None;
        }
    }

    private static string? GetVdfValue(string body, string key)
    {
        var m = Regex.Match(body, $"\"{key}\"\\s+\"(?<v>[^\"]*)\"");
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string? FindSteamPath()
    {
        try
        {
            var p = Registry.CurrentUser
                .OpenSubKey(@"Software\Valve\Steam")?
                .GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(p))
            {
                p = p.Replace('/', Path.DirectorySeparatorChar);
                if (Directory.Exists(p)) return p;
            }
        }
        catch (Exception __ex) { AppLog.Warn("SteamService", __ex.Message); }

        string[] candidates =
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"E:\Steam",
            @"F:\Steam",
        };
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return null;
    }

    /// <summary>
    /// Steam caches avatars as &lt;steamid64&gt;.jpg / _medium.jpg / _full.jpg.
    /// Returned as a data URI so the WebView2 page can show it without file:// access.
    /// </summary>
    private static string? LoadAvatar(string steamPath, string sid)
    {
        try
        {
            var dir = Path.Combine(steamPath, "config", "avatarcache");
            if (!Directory.Exists(dir)) return null;

            var files = Directory.GetFiles(dir, sid + "*");
            if (files.Length == 0) return null;

            // Prefer the "_full" (184px) variant, else the largest file.
            var best = files
                .OrderByDescending(f => f.Contains("_full", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => new FileInfo(f).Length)
                .First();

            var bytes = File.ReadAllBytes(best);
            if (bytes.Length < 8) return null;

            var mime = bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg"
                     : bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png"
                     : "image/jpeg";
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }
}

public sealed record SteamProfile(
    string? PersonaName,
    string? AccountName,
    string? AvatarDataUri,
    bool Found,
    int AccountCount = 0)
{
    public static readonly SteamProfile None = new(null, null, null, false, 0);
}
