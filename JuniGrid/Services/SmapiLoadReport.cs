using System.IO;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 拿最近一次 SMAPI 日志对账：「这一次启动，SMAPI 到底把哪些 mod 挡在门外、为什么」。
///
/// <para>为什么光读 manifest 不够：manifest 只能回答「声明的依赖装没装、版本够不够」。
/// 实机 2026-11-15 那份日志里还有两类<b>从磁盘上看不出来</b>的跳过 ——</para>
/// <list type="bullet">
/// <item><c>because it's no longer compatible</c>（AeroCore、Quest Framework、CSharp For Durins Rest）：
/// DLL 是按旧版游戏编的，加载即失败，manifest 一个字都没提；</item>
/// <item><c>because it needs the 'X' mod, which couldn't be loaded</c>（Warp Network、SVE、Durin's Rest C#）：
/// 依赖自己装着、也没被禁用，只是它这次也没加载起来 —— 依赖链上一个人倒下，下游全被带走。</item>
/// </list>
/// <para>这两类只有 SMAPI 自己知道。日志里 ERROR 的「Skipped mods」块逐条写着名字 + 版本 + 原因，
/// 按「名字 + 版本」就能对回列表里的行 —— 于是「我装了却不生效」在面板上终于有话说。</para>
/// </summary>
public sealed class SmapiLoadReport
{
    /// <summary>被跳过的一条。</summary>
    public enum SkipKind
    {
        /// <summary>requires mods which aren't installed —— manifest 能判，日志只是佐证。</summary>
        MissingDeps,
        /// <summary>needs the 'X' mod, which couldn't be loaded —— 依赖链上游失败。</summary>
        BlockedByDep,
        /// <summary>no longer compatible —— 代码层面不兼容（按旧版游戏编译）。</summary>
        Incompatible,
        /// <summary>manifest 说有某个 DLL，文件夹里却没有 —— 装坏了（解压不全 / 被杀软吃掉）。</summary>
        DllMissing,
        /// <summary>其它原因（原样展示 SMAPI 的说法）。</summary>
        Other,
    }

    /// <param name="Name">manifest 里的 Name（SMAPI 打印的就是它，备注改名后也跟着变）。</param>
    /// <param name="Version">manifest 里的 Version（日志里版本常缺省，缺省为空串）。</param>
    /// <param name="Kind">归类后的原因。</param>
    /// <param name="Detail">明细：缺的依赖 UID 列表 / 加载失败的依赖名 / 原文。</param>
    public sealed record Skip(string Name, string Version, SkipKind Kind, string Detail);

    public static readonly SmapiLoadReport Empty = new(Array.Empty<Skip>(), null, default);

    public IReadOnlyList<Skip> Skips { get; }
    public string? LogPath { get; }
    public DateTime LogWriteTime { get; }

    private SmapiLoadReport(IReadOnlyList<Skip> skips, string? path, DateTime writeTime)
    {
        Skips = skips;
        LogPath = path;
        LogWriteTime = writeTime;
    }

    public bool Any => Skips.Count > 0;

    // ------------------------------------------------------------------
    // 读盘（按 mtime + 长度缓存：渲染期每行都要问一次，不能每次都读 50KB 文件）
    // ------------------------------------------------------------------
    private static readonly object Gate = new();
    private static SmapiLoadReport _cached = Empty;
    private static string? _cachedPath;
    private static DateTime _cachedMtime;
    private static long _cachedLen = -1;

    /// <summary>读最近的 SMAPI 日志；文件不存在/读不了 → <see cref="Empty"/>。
    /// 同一份文件（mtime 与长度都没变）只解析一次。</summary>
    public static SmapiLoadReport Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return Empty;
            lock (Gate)
            {
                if (_cachedPath is not null
                    && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase)
                    && _cachedMtime == fi.LastWriteTimeUtc
                    && _cachedLen == fi.Length)
                    return _cached;

                var lines = File.ReadAllLines(path);
                var report = FromLines(lines, fi.LastWriteTime);
                _cached = report;
                _cachedPath = path;
                _cachedMtime = fi.LastWriteTimeUtc;
                _cachedLen = fi.Length;
                return report;
            }
        }
        catch
        {
            return Empty;
        }
    }

    // `[01:15:26 ERROR SMAPI]       - AeroCore 0.9.6 because it's no longer compatible. …`
    private static readonly Regex Entry = new(
        @"^\s*\[\d{2}:\d{2}:\d{2}\s+ERROR\s+SMAPI\]\s*-\s*(?<body>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>名字里允许空格、括号、撇号；版本是「because」之前最后一个像版本号的词。</summary>
    private static readonly Regex TrailingVersion = new(
        @"^(?<name>.+?)\s+(?<ver>[vV]?\d[\w.\-+]*)$", RegexOptions.Compiled);

    public static SmapiLoadReport FromLines(IReadOnlyList<string> lines, DateTime writeTime)
    {
        var list = new List<Skip>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var m = Entry.Match(line);
            if (!m.Success) continue;

            var body = m.Groups["body"].Value.Trim();
            var whyIdx = body.IndexOf(" because ", StringComparison.OrdinalIgnoreCase);
            var head = whyIdx > 0 ? body[..whyIdx].Trim() : body;
            var why = whyIdx > 0 ? body[(whyIdx + 9)..].Trim() : "";

            var name = head;
            var ver = "";
            var vm = TrailingVersion.Match(head);
            if (vm.Success) { name = vm.Groups["name"].Value.Trim(); ver = vm.Groups["ver"].Value.Trim(); }
            if (name.Length == 0) continue;
            // 段落下方那条 `- -------…` 分隔线也带着连字符开头，会被上面这条正则当成一条记录
            // （名字是「-----」，版本空）。留着它「SMAPI 上次跳过 N 个」就永远多 1。
            if (!name.Any(char.IsLetterOrDigit)) continue;

            var (kind, detail) = Classify(why);
            // 同一份日志里同一个子包会被逐条点名（Durin's Rest Spawns 连报 11 次），
            // 按 名字+版本+原因 去重，列表里只留一条。
            if (!seen.Add($"{name}\u0001{ver}\u0001{kind}\u0001{detail}")) continue;
            list.Add(new Skip(name, ver, kind, detail));
        }
        return new SmapiLoadReport(list, null, writeTime);
    }

    /// <summary>把 SMAPI 的英文原因归成几档，并抽出明细。认不出的原样留着 —— 不编。</summary>
    public static (SkipKind Kind, string Detail) Classify(string why)
    {
        var w = why ?? "";
        if (w.Contains("no longer compatible", StringComparison.OrdinalIgnoreCase))
            return (SkipKind.Incompatible, w);

        // `because its DLL 'AntiSocialNPCs.dll' doesn't exist.` —— 装坏了，不是不兼容。
        // 这一档对依赖链很关键：AntiSocial NPCs 就是这么倒的，Durin's Rest 全家被它带走。
        if (w.Contains("DLL", StringComparison.OrdinalIgnoreCase)
            && (w.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
                || w.Contains("does not exist", StringComparison.OrdinalIgnoreCase)))
        {
            var m = QuotedDll.Match(w);
            return (SkipKind.DllMissing, m.Success ? m.Groups["dll"].Value : w);
        }

        if (w.Contains("requires mods which aren't installed", StringComparison.OrdinalIgnoreCase)
            || w.Contains("requires mods which are not installed", StringComparison.OrdinalIgnoreCase))
        {
            var deps = Between(w, "(", ")");
            return (SkipKind.MissingDeps, StripUrls(deps));
        }

        if (w.Contains("couldn't be loaded", StringComparison.OrdinalIgnoreCase)
            || w.Contains("could not be loaded", StringComparison.OrdinalIgnoreCase))
        {
            var dep = QuotedDepName(w);
            return (SkipKind.BlockedByDep, dep);
        }

        return (SkipKind.Other, w);
    }

    private static readonly Regex QuotedDll = new(@"'(?<dll>[^']+\.dll)'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Between(string s, string open, string close)
    {
        var i = s.IndexOf(open, StringComparison.Ordinal);
        var j = s.LastIndexOf(close, StringComparison.Ordinal);
        return i >= 0 && j > i ? s[(i + open.Length)..j].Trim() : s.Trim();
    }

    /// <summary>从 <c>it needs the 'X' mod, which couldn't be loaded</c> 里取 X。
    /// 按「最后一个贴着 <c> mod</c> 的引号」收尾 —— 依赖名自己带撇号时
    /// （<c>'Durin's Rest JA' mod</c>）才不会在第一个撇号处截断。</summary>
    private static readonly Regex QuotedBeforeMod = new(@"'(?<dep>.+)'(?=\s+mod\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string QuotedDepName(string s)
    {
        var m = QuotedBeforeMod.Match(s);
        if (m.Success) return m.Groups["dep"].Value.Trim();
        var i = s.IndexOf('\'');
        var j = i >= 0 ? s.IndexOf('\'', i + 1) : -1;
        return i >= 0 && j > i ? s[(i + 1)..j].Trim() : s.Trim();
    }

    /// <summary>「SpaceCore: https://…, Json Assets: https://…」→「SpaceCore, Json Assets」。</summary>
    private static string StripUrls(string s)
        => Regex.Replace(s, @"\s*:\s*https?://\S+", "").Trim();

    /// <summary>这一行是不是上一次启动就被跳过了。名字按 manifest Name 比（SMAPI 打印的就是它）；
    /// 版本对得上就优先用那一条，对不上（作者改过版本号）仍按名字给结论 —— 跳过这件事本身没变。
    /// <para>名字先按原样比，比不到再按「只留字母数字」比：SMAPI 打印名会把方括号写法
    /// 换成圆括号（manifest 的 <c>[AMF] Durin's Rest</c> 在日志里是 <c>(AMF) Durin's Rest</c>），
    /// 标点差异不该让一整行对不上账。</para></summary>
    public Skip? Match(string? name, string? version)
    {
        var n = name?.Trim();
        if (Skips.Count == 0 || string.IsNullOrEmpty(n)) return null;

        Skip? loose = null;
        foreach (var s in Skips)
        {
            if (!string.Equals(s.Name.Trim(), n, StringComparison.OrdinalIgnoreCase)) continue;
            var v = version?.Trim();
            if (!string.IsNullOrEmpty(s.Version) && !string.IsNullOrEmpty(v)
                && string.Equals(s.Version, v, StringComparison.OrdinalIgnoreCase))
                return s;
            loose ??= s;
        }
        if (loose is not null) return loose;

        var key = NameKey(n);
        foreach (var s in Skips)
            if (NameKey(s.Name) == key) return s;
        return null;
    }

    /// <summary>名字的粗口径：只留字母/数字（含中日韩字符），其余标点、括号、撇号一律不看。</summary>
    internal static string NameKey(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
