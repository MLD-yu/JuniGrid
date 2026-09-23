using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// Maps one SMAPI console line to a CSS class, mirroring SMAPI's own
/// console colors:
///   ERROR → red · "can update" lines → purple · WARN → yellow
///   INFO → blue · TRACE/DEBUG → gray · [JuniGrid] → green
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
        new(@"Loaded \d+ (mods|content packs):", RegexOptions.Compiled);
    private static readonly Regex LoadedEntry =
        new(@"^\[[^\]]*\]\s{2,}\S.*\s\d+(\.\d+)+[\w.-]*\s+by\s+.+\|", RegexOptions.Compiled);

    /// <summary>行是否属于指定筛选分类。err/upd 与着色同一规则；
    /// loaded 为成功加载的 mod / 内容包清单块。</summary>
    public static bool MatchesFilter(string line, string filter) => filter switch
    {
        "err" => Classify(line) == "err",
        "upd" => Classify(line) == "upd",
        "loaded" => LoadedHeader.IsMatch(line) || LoadedEntry.IsMatch(line),
        _ => true,
    };

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
        if (line.Contains(" TRACE ") || line.Contains(" DEBUG ")) return "trace";

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
