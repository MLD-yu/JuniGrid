using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 把「已禁用模组被跳过」的证据折成一行。
///
/// <para>本程序的禁用 = 给文件夹名加点前缀，而 SMAPI 每启动一次就会为<b>每一个</b>禁用目录
/// 单独写一句 <c>[.. TRACE SMAPI]   Skipped Mods\.Foo (folder name starts with a dot).</c>
/// 一个禁用了三百个 mod 的用户，日志页开头三百行全是同一件事，真正有用的
/// 「Loaded N mods」清单、报错、可更新提示全被挤到下面去了（用户实测反馈）。
/// 这些行要说的话其实只有一句：<b>这些是我自己关掉的，不是坏了</b>，所以攒成一行、列出前几个名字。</para>
///
/// <para>聚合放在<b>进缓冲之前</b>（尾随读取的出口），不只是为了好看：启动段的保护区
/// （<see cref="LauncherService.MaxHeadLines"/>）有上限，几百行跳过证据会把真正的加载清单
/// 顶出保护区，随后被当成「游戏期刷屏」裁掉。</para>
///
/// <para>「缺 manifest.json / 空目录 / 不是 SMAPI mod / XNB 老 mod」这类跳过<b>不</b>折叠 ——
/// 每一条都是独立的问题，合并成一个数就查不出是哪个文件夹了。</para>
/// </summary>
public sealed class SmapiSkipRollup
{
    /// <summary>点前缀目录（= 禁用态）。只认这一种原因，其余跳过原样放行。</summary>
    private static readonly Regex DotSkip = new(
        @"^(?<head>\[\d{2}:\d{2}:\d{2}\s+\w+\s+SMAPI\])\s+Skipped\s+(?<path>.+?)\s+\(folder name starts with a dot\)\.?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>汇总行自己的形状（<see cref="SmapiLoadSummary"/> 靠它把计数补回来）。</summary>
    private static readonly Regex RollupLine = new(
        @"Skipped\s+(?<n>\d+)\s+mods?\s+\(folder name starts with a dot",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>点名的上限：再多行就撑成好几行了，而「300 个里的前 6 个」本来也没有信息量。</summary>
    private const int MaxNames = 6;

    /// <summary>超过这个数就只报数量 —— 重度用户禁用几百个时，一行必须说清事、又不能自己变成刷屏。</summary>
    private const int NameListLimit = 12;

    private readonly List<string> _names = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private string _head = "";
    private DateTime _lastAddUtc;

    public bool Pending => _names.Count > 0;

    /// <summary>这一行是不是「已禁用」的跳过证据；是就吞下去（返回 true），由 <see cref="Flush"/> 统一吐出。</summary>
    public bool TryAdd(string line)
    {
        var m = DotSkip.Match(line);
        if (!m.Success) return false;

        if (_names.Count == 0) _head = m.Groups["head"].Value;
        var name = Shorten(m.Groups["path"].Value);
        if (_seen.Add(name)) _names.Add(name);
        _lastAddUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>把攒下的折成一行送出去；没有就什么都不做。</summary>
    public void Flush(Action<string> emit)
    {
        if (_names.Count == 0) return;

        var n = _names.Count;
        var names = "";
        if (n <= NameListLimit)
        {
            names = ": " + string.Join(", ", _names.Take(MaxNames));
            if (n > MaxNames) names += $", … and {n - MaxNames} more";
        }

        emit($"{_head}   Skipped {n} {(n == 1 ? "mod" : "mods")} (folder name starts with a dot — disabled){names}");

        _names.Clear();
        _seen.Clear();
        _head = "";
    }

    /// <summary>扫描期一屏几十行地写，正常会被下一行非跳过日志冲出来；
    /// 万一日志停在这儿（卡在加载、正在进标题画面），不能让汇总行一直憋着不显示。</summary>
    public void FlushIfIdle(Action<string> emit, TimeSpan idle)
    {
        if (_names.Count > 0 && DateTime.UtcNow - _lastAddUtc >= idle) Flush(emit);
    }

    /// <summary>是不是一行汇总（给 <see cref="SmapiLoadSummary"/> 用）。
    /// 这一行带 TRACE 级别头，所以默认视图（控制台口径）里本来就不会出现它 ——
    /// 它留在缓冲里是为了页头数数、悬停名单和「复制全部」。</summary>
    public static bool TryParseRollup(string line, out int count, out IReadOnlyList<string> names)
    {
        count = 0;
        names = Array.Empty<string>();
        var m = RollupLine.Match(line);
        if (!m.Success) return false;

        count = int.Parse(m.Groups["n"].Value);

        // 名字在最后一个 ")：" 之后；截断提示（", … and N more"）不算名字
        var idx = line.LastIndexOf("): ", StringComparison.Ordinal);
        if (idx > 0)
        {
            names = line[(idx + 3)..]
                .Split(", ", StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !s.StartsWith('…'))
                .ToArray();
        }
        return true;
    }

    /// <summary><c>Mods\.Foo</c> → <c>.Foo</c> —— 前缀对所有人都一样，留着只是占宽度。</summary>
    private static string Shorten(string path)
    {
        var p = path.Trim();
        if (p.StartsWith(@"Mods\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.TrimEnd('\\');
    }
}
