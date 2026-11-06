namespace JuniGrid.Services;

/// <summary>
/// 用户自定义分类的「多值」读写口径。
/// <para>2026-11-07：安装弹窗改成可多选后，<see cref="ConfigService.ModUserCategory"/> 里
/// 一个键可能对应多个分类（<c>"9,10"</c>）。配置类型仍是
/// <c>Dictionary&lt;string, string&gt;</c>（老配置单值原样可读），多值只是用逗号拼起来。</para>
/// <para>所有拆分/拼接都走这里，别再各处 <c>Split(',')</c> —— 之前就有把 <c>"9,10"</c>
/// 当成一个分类名的坑（列表里凭空多出一个叫「9,10」的段）。</para>
/// </summary>
public static class ModCategoryUtil
{
    private static readonly char[] Sep = [','];

    /// <summary>拆出全部非空分类（去首尾空白、去重、保持书写顺序）。空/纯空白返回空数组。</summary>
    public static string[] Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var parts = raw.Split(Sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return [];
        if (parts.Length == 1) return parts;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
            if (seen.Add(p)) list.Add(p);
        return list.ToArray();
    }

    /// <summary>第一个分类 —— 决定这个 mod 在列表里落在哪一段。"" = 未分类。</summary>
    public static string First(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var i = raw.IndexOf(',');
        return (i < 0 ? raw : raw[..i]).Trim();
    }

    /// <summary>把多个分类拼成配置值：去空白、去重、丢空项。全空返回 ""（= 未分类 / 从表里删掉）。</summary>
    public static string Join(IEnumerable<string?>? cats)
    {
        if (cats is null) return "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var c in cats)
        {
            var t = (c ?? "").Trim();
            if (t.Length == 0) continue;
            // 值里理论上不该再有逗号；真出现了就当场拆开，别把脏数据写回去
            foreach (var one in t.Split(Sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (seen.Add(one)) list.Add(one);
        }
        return string.Join(",", list);
    }
}
