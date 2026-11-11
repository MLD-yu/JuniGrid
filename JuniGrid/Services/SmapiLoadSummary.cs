using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 从日志缓冲里读出「SMAPI 这一次到底加载了什么、跳过了什么」。
///
/// 为什么要有这个：日志页原来只是把 SMAPI-latest.txt 原样铺出来靠颜色区分级别，
/// 用户问的却是「我装了这个 mod，为什么游戏里没有」—— 那属于**对账**问题，不是着色问题。
/// 别的管理器之所以能一句话说清楚，是因为它们自己扫盘 + 读 SMAPI 的加载小结；我们只当镜子，
/// 镜子里没有的东西就永远看不见。这里把 SMAPI 自己的那两句数出来，供日志页头顶做一条小结。
///
/// 纯字符串解析，不碰磁盘、不依赖 ModService：页面上有什么日志就统计什么。
/// </summary>
public sealed class SmapiLoadSummary
{
    /// <summary>"Loaded 12 mods:" / "Loaded 0 mods." —— 加号版 SMAPI 才带冒号。</summary>
    private static readonly Regex LoadedMods =
        new(@"Loaded (\d+) mods", RegexOptions.Compiled);
    private static readonly Regex LoadedPacks =
        new(@"Loaded (\d+) content packs", RegexOptions.Compiled);

    /// <summary>[.. TRACE SMAPI]   Skipped Mods\.Foo (folder name starts with a dot).
    /// 路径里带空格很常见（Mods\.Ridgeside Village），不能按 \S+ 抓；原因里还会嵌括号
    /// （<c>it's not a SMAPI mod (see https://smapi.io/xnb for info)</c>），所以原因按贪婪取到最后一个右括号。</summary>
    private static readonly Regex SkippedFolder =
        new(@"\]\s+Skipped\s+(?<path>.+?)\s+\((?<why>.+)\)\.?\s*$", RegexOptions.Compiled);

    /// <summary>ERROR 块里的逐条原因：[.. ERROR SMAPI]    - Foo 1.0.0 because it needs SMAPI 5.0 or later.</summary>
    private static readonly Regex FailedEntry =
        new(@"\]\s+-\s+(?<name>.+?)(?:\s+\(ID:.*?\))?\s+because\s+(?<why>.+)$", RegexOptions.Compiled);

    /// <summary>[.. INFO SMAPI] Mods go here: E:\steam\steamapps\common\Stardew Valley\Mods
    /// —— SMAPI 实际扫的那个目录。跟我们设置里的游戏目录对不上时，mod 装得再对也不会被加载，
    /// 而 SMAPI 本身不会为此报任何错。</summary>
    private static readonly Regex ModsGoHere =
        new(@"Mods go here:\s*(?<path>.+?)\s*$", RegexOptions.Compiled);

    public int? Mods { get; private set; }
    public int? Packs { get; private set; }

    /// <summary>SMAPI 自己报的 Mods 目录（日志里 "Mods go here: …" 那句）。</summary>
    public string? ModsPath { get; private set; }

    /// <summary>点前缀文件夹（= 禁用态）被 SMAPI 整棵跳过。SMAPI 只在 TRACE 里说一句，不算错误。</summary>
    public int DotIgnored { get; private set; }

    /// <summary>其它被跳过的文件夹：没有 manifest.json、空文件夹、XNB 老 mod…（SMAPI 多半还会在 ERROR 块里再说一遍）</summary>
    public int OtherSkipped { get; private set; }

    /// <summary>ERROR「Skipped mods」块逐条：名字 + 原因。</summary>
    public List<(string Name, string Why)> Failures { get; } = new();

    /// <summary>被跳过的文件夹/条目名（前若干条，供提示浮层展示）。</summary>
    public List<string> SkippedNames { get; } = new();

    public int Skipped => DotIgnored + OtherSkipped;

    /// <summary>日志里出现过加载小结/跳过证据才值得显示这条小结。</summary>
    public bool Any => Mods is not null || Packs is not null || Skipped > 0 || Failures.Count > 0;

    public static SmapiLoadSummary FromLines(IReadOnlyList<string> lines)
    {
        var s = new SmapiLoadSummary();
        var skippedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (line.Length == 0) continue;

            if (s.Mods is null)
            {
                var m = LoadedMods.Match(line);
                if (m.Success) s.Mods = int.Parse(m.Groups[1].Value);
            }
            if (s.Packs is null)
            {
                var m = LoadedPacks.Match(line);
                if (m.Success) s.Packs = int.Parse(m.Groups[1].Value);
            }
            if (s.ModsPath is null)
            {
                var m = ModsGoHere.Match(line);
                if (m.Success) s.ModsPath = m.Groups["path"].Value;
            }

            // 「已禁用」的一整批跳过在进缓冲前就被折成了一行（SmapiSkipRollup），
            // 单个路径的写法在本地复用日志里仍可能出现，两种形状都要认。
            if (SmapiSkipRollup.TryParseRollup(line, out var rolled, out var rolledNames))
            {
                s.DotIgnored += rolled;
                foreach (var n in rolledNames)
                    if (s.SkippedNames.Count < 8 && skippedSeen.Add(n)) s.SkippedNames.Add(n);
                continue;
            }

            var skip = SkippedFolder.Match(line);
            if (skip.Success)
            {
                var path = skip.Groups["path"].Value;
                var why = skip.Groups["why"].Value;
                if (skippedSeen.Add(path))
                {
                    if (why.Contains("starts with a dot", StringComparison.OrdinalIgnoreCase)) s.DotIgnored++;
                    else s.OtherSkipped++;
                    if (s.SkippedNames.Count < 8) s.SkippedNames.Add(path);
                }
                continue;
            }

            var fail = FailedEntry.Match(line);
            if (fail.Success)
            {
                var name = fail.Groups["name"].Value.Trim();
                if (failedSeen.Add(name)) s.Failures.Add((name, fail.Groups["why"].Value.Trim()));
            }
        }

        return s;
    }
}
