using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// Maps one SMAPI console line to a CSS class, mirroring SMAPI's own
/// console colors:
///   ERROR → red · "can update" lines → purple · WARN → yellow
///   INFO → blue · TRACE/DEBUG → gray · [JuniGrid] → green
///   TRACE/DEBUG 里的「跳过 / 加载失败」→ gold（skip）
/// </summary>
public static class LogLineClassifier
{
    // SMAPI prints update alerts as:
    //   [SMAPI]    DynamicShader 1.0.41: https://… (you have 1.0.39)
    // The redirected stdout has no ALERT level tag, so "(you have …)" is
    // the only stable marker of these lines.
    private static readonly Regex YouHaveVersion =
        new(@"\(\s*you have\s+[\d.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "已加载"筛选：SMAPI 的 "Loaded N mods:" / "Loaded N content packs:" 块及其条目行
    // 条目形如:  [.. INFO SMAPI]    Cloudy Skies 1.9.1 by Khloe Leclair | ...
    private static readonly Regex LoadedHeader =
        new(@"Loaded \d+ (mods|content packs)[:.]", RegexOptions.Compiled);
    private static readonly Regex LoadedEntry =
        new(@"^\[[^\]]*\]\s{2,}\S.*\s\d+(\.\d+)+[\w.-]*\s+by\s+.+\|", RegexOptions.Compiled);

    // 「这个 mod 没进游戏」的证据 SMAPI 常年只写在 TRACE/DEBUG 里：
    //   [.. TRACE SMAPI]   Skipped Mods\.Foo (folder name starts with a dot).
    //   [.. TRACE SMAPI]   Skipped Mods\Foo (it contains files, but none of them are manifest.json).
    //   [.. TRACE SMAPI]       Failed: System.NullReferenceException: ...
    //   [.. TRACE SMAPI]   Ignored loose files: foo.zip, bar.7z
    // 这些行以前随 TRACE 整组丢弃，于是「mod 明明在 Mods 里却没加载」在日志页一个字都没有，
    // 用户只能去看别的管理器（用户实测反馈）。它们数量 = 被跳过的文件夹数（不是刷屏那类
    // TRACE），必须留下来并单独上色。
    // 匹配形状要卡准：只认 SMAPI 的「Skipped <路径> (<原因>)」与 "Ignored loose files"，
    // 不能是光秃秃的 "Skipped 3 tiles" —— 那是 mod 玩游戏时的 TRACE，一放进来就又刷屏了。
    private static readonly Regex LoadSkipNotice = new(
        @"(?:\bSkipped\s+.+?\s+\(.+\)|\bIgnored\s+loose\s+files|\bFailed:\s|starts with a dot"
        + @"|none of them are manifest\.json|isn't a mod\b|it's an empty folder|empty Vortex folder|not a SMAPI mod)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>行是否是一条「mod 被跳过 / 加载失败」的证据（不论级别）。</summary>
    public static bool IsLoadSkipNotice(string line) => LoadSkipNotice.IsMatch(line);

    /// <summary>行是否属于指定筛选分类。err/upd/skip/warn 与着色同一规则；
    /// loaded 为成功加载的 mod / 内容包清单块。</summary>
    public static bool MatchesFilter(string line, string filter) => filter switch
    {
        "err" => Classify(line) == "err",
        "upd" => Classify(line) == "upd",
        "skip" => Classify(line) == "skip",
        "warn" => Classify(line) == "warn",
        "loaded" => LoadedHeader.IsMatch(line) || LoadedEntry.IsMatch(line),
        _ => true,
    };

    // 带级别头且级别正好是 TRACE 的行。文件日志是 verbose 级别，控制台不是 ——
    // 同一次启动，控制台窗口里那句「Loaded 0 mods.」上下干干净净，文件里却夹着几百行 TRACE。
    private static readonly Regex TraceTag =
        new(@"^\[\d{2}:\d{2}:\d{2}\s+TRACE\b", RegexOptions.Compiled);

    /// <summary>只在日志文件里、SMAPI 控制台里看不到的行。
    ///
    /// <para>用户给的判据很干脆：<b>SMAPI 控制台显示什么，这一页就显示什么</b>。控制台默认不显示
    /// TRACE（它只把 TRACE 写进文件），所以默认视图里也不该有 —— 包括我们为了「查案」特意留下的
    /// 跳过/加载失败证据：那几百行「跳过了 Mods\.xxx（文件夹名以点开头）」正是用户抱怨的刷屏，
    /// 而它本来就在控制台里一个字都没有。真要查，点页头「跳过 N 个」进「未加载」筛选，或者看原文件。</para></summary>
    public static bool IsFileOnly(string line) => TraceTag.IsMatch(line);

    // 带级别头的行：[02:14:16 INFO  SMAPI]（文件日志）/ [JuniGrid]（我们自己写的播报）。
    // 只有这两种行能决定颜色；其余都是上一条的续行。
    private static readonly Regex LevelTag =
        new(@"^\[\d{2}:\d{2}:\d{2}[ \]]", RegexOptions.Compiled);

    private static bool HasLevelTag(string line) =>
        LevelTag.IsMatch(line) || line.StartsWith("[JuniGrid]");

    /// <summary>整页着色：带级别头的那行决定颜色，它下面的续行（多行消息、堆栈、
    /// SMAPI 的「直接控制台访问」清单）整段沿用同一条头的颜色 —— 和 SMAPI 控制台一致。
    /// 逐行独立判定会让一段 WARN 的多行正文后半截变白、一段 ERROR 的堆栈花成紫白相间
    /// （用户实测反馈）。<para>
    /// 首行落在一段中间（页面从缓冲中段开始）时没有上文，退回逐行判定。</para></summary>
    public static string[] ClassifyAll(IReadOnlyList<string> lines)
    {
        var result = new string[lines.Count];
        var current = "";
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var own = Classify(line);
            if (HasLevelTag(line))
            {
                current = own;
            }
            else if (current.Length == 0)
            {
                // 页面从缓冲中段开始（首句就是裸的堆栈帧）：没有上文可跟，
                // 就拿这行自己的判定当起点 —— 后面的续行再跟着它。
                current = own;
            }
            result[i] = current;
        }
        return result;
    }

    public static string Classify(string line)
    {
        if (line.StartsWith("[JuniGrid]")) return "sys";

        // Errors first — a failing update check must stay red, not purple.
        if (line.StartsWith("[ERR]")
            || line.Contains(" ERROR ")
            || line.Contains("[ERROR]"))
            return "err";

        // 日志文件的行自带级别标签（[HH:MM:SS LEVEL SMAPI]），标签优先于内容启发式：
        // mod 简介里碰巧含 "update" 的普通 INFO 行（如作者署名 "1.6 update by …"）不再误判成可更新。
        if (line.Contains(" ALERT ")) return "upd";
        if (line.Contains(" WARN ")) return "warn";
        if (line.Contains(" INFO ")) return "info";
        if (line.Contains(" TRACE ") || line.Contains(" DEBUG "))
            return IsLoadSkipNotice(line) ? "skip" : "trace";

        // 异常与堆栈的续行不带级别标签（SMAPI 只在错误块里输出这些行），必须归进错误：
        // 否则它们会掉到下面的内容启发式里 —— 方法名含 "update" 的栈帧
        // （UpdateRandomMovements / updateEvenIfFarmerIsntHere / <UpdateLocations>b__0 /
        // Game1._update）被当成"可更新"染成品红，其余帧没人认领变白色，
        // 一条 ERROR 的堆栈就花成了紫白相间（用户实测）。
        if (StackFrame.IsMatch(line) || ExceptionLine.IsMatch(line)) return "err";

        // 无级别标签的行（如 stderr 转发）才走启发式：SMAPI 更新提示的稳定特征
        // 是 "update" / "(you have x)" 字样（SMAPI 控制台把更新提示渲染为品红）。
        if (line.Contains("update", StringComparison.OrdinalIgnoreCase)
            || YouHaveVersion.IsMatch(line))
            return "upd";

        return "";
    }

    // 「   at Xxx.Yyy(...) in D:\...\Game1.cs:line 6169」—— 缩进 + at + 非空白
    private static readonly Regex StackFrame =
        new(@"^\s+at\s+\S", RegexOptions.Compiled);

    // 「System.NullReferenceException: ...」「InnerException:」「--- End of inner ...」
    private static readonly Regex ExceptionLine =
        new(@"^\s*(---\s*End of|\w+(\.\w+)*Exception\b|InnerException\b)", RegexOptions.Compiled);
}
