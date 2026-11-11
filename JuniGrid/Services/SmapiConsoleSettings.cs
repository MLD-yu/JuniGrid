using System.IO;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 「SMAPI 控制台此刻会打印哪些行」—— 日志页要和那扇窗口对齐，就不能自己拍脑袋定级别。
///
/// <para>规则直接抄 SMAPI 源码（<c>Monitor.LogImpl</c>，4.x）：</para>
/// <code>
/// if (WriteToConsole &amp;&amp; (ShowTraceInConsole || level != ConsoleLogLevel.Trace || ...))
///     ConsoleWriter.WriteLine(consoleMessage, level);
/// </code>
/// <para><c>ShowTraceInConsole</c> 全项目只有一处赋值：<c>monitor.ShowTraceInConsole = isDeveloperMode</c>
/// （<c>LogManager.ApplySettings</c>）。所以结论很干脆 —— <b>TRACE 进不进控制台，只由 DeveloperMode 决定</b>；
/// DEBUG / INFO / WARN / ERROR / ALERT / CRITICAL 一律进。配置里的 <c>VerboseLogging</c> 只是让
/// <c>VerboseLog()</c> 真的产出 TRACE（写进<b>文件</b>），并不会把 TRACE 送进控制台。</para>
///
/// <para>于是本类只回答一个问题：这台机器上 SMAPI 的控制台现在会不会打印 TRACE。
/// 答案是「会」（开发者模式）时，日志页也把 TRACE 显示出来 —— 不多给，也不少给。</para>
/// </summary>
public static class SmapiConsoleSettings
{
    private const string ConfigPath = "smapi-internal/config.json";
    private const string UserConfigPath = "smapi-internal/config.user.json";

    /// <summary>SMAPI 控制台会不会打印 TRACE（= 开发者模式）。</summary>
    public static bool ConsoleShowsTrace(string? gamePath) => DeveloperMode(gamePath);

    /// <summary>读 DeveloperMode。读不到就当 false —— 那正是 SMAPI 的出厂默认值。</summary>
    public static bool DeveloperMode(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return false;
        try
        {
            // config.json 是出厂默认（重装 SMAPI 会被覆盖）；config.user.json 是玩家自己建的覆盖文件。
            var fallback = ReadBool(Path.Combine(gamePath, ConfigPath), "DeveloperMode") ?? false;
            return ReadBool(Path.Combine(gamePath, UserConfigPath), "DeveloperMode") ?? fallback;
        }
        catch { return false; }
    }

    /// <summary>从 SMAPI 的配置文件里取一个布尔项；文件不在 / 读不了 / 没这个键 都返回 null。</summary>
    internal static bool? ReadBool(string path, string key)
    {
        try
        {
            return File.Exists(path) ? ParseBool(File.ReadAllText(path), key) : null;
        }
        catch { return null; }   // 被占用、编码怪 —— 一律按「没读到」处理，不让日志页跟着变脸
    }

    /// <summary>在 SMAPI 的「JSON + 注释」里找一个布尔项。
    ///
    /// <para>不做完整 JSON 解析是有意的：那个文件带 <c>/* ... */</c> 说明块，键值里有
    /// <c>"https://smapi.io/api/"</c> 这种含 <c>//</c> 的字符串（照行剥离注释会把它劈坏），
    /// 将来还可能多出嵌套结构。这里只认 <c>"Key": true|false</c> 这一种形状，
    /// 找不到就返回 null 按默认走 —— 配置写得再花，页面也不会因此少显示或多显示。</para></summary>
    internal static bool? ParseBool(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
        var matches = Regex.Matches(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)",
            RegexOptions.IgnoreCase);
        if (matches.Count == 0) return null;
        return string.Equals(matches[^1].Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
