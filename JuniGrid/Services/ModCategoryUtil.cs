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

    public static string NexusCategoryName(string? category, string language = "zh")
    {
        var name = category?.Trim() ?? "";
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) return name;
        return name.ToLowerInvariant() switch
        {
            "stardew valley" => "星露谷物语",
            "miscellaneous" => "杂项",
            "gameplay mechanics" => "游戏机制",
            "player" => "角色",
            "characters" => "角色",
            "portraits" => "肖像",
            "livestock and animals" => "牲畜与动物",
            "pets / horses" => "宠物与马匹",
            "modding tools" => "模组开发工具",
            "user interface" => "用户界面",
            "cheats" => "作弊",
            "audio" => "音效",
            "clothing" => "服装",
            "crops" => "作物",
            "items" => "物品",
            "locations" => "地点",
            "buildings" => "建筑",
            "events" => "事件",
            "interiors" => "室内装饰",
            "dialogue" => "对话",
            "maps" => "地图",
            "crafting" => "制作",
            "furniture" => "家具",
            "new characters" => "新增角色",
            "visuals and graphics" => "画面与图像",
            "fishing" => "钓鱼",
            "expansions" => "扩展内容",
            _ => name
        };
    }

    public static string CategoryLabel(JuniGridConfig cfg, string category, string language)
        => cfg.ModOfficialCategoryNames.TryGetValue(category, out var source)
            ? NexusCategoryName(source, language) : category;

    public static string Recommend(string? source, string? display, IEnumerable<string> choices,
        IReadOnlyDictionary<string, string>? officialCategories = null)
    {
        var original = source?.Trim() ?? "";
        var label = string.IsNullOrWhiteSpace(display) ? original : display.Trim();
        if (label.Length == 0) return "";
        var known = choices.ToList();
        return known.FirstOrDefault(c => officialCategories is not null && officialCategories.TryGetValue(c, out var official)
                && string.Equals(official, original, StringComparison.OrdinalIgnoreCase))
            ?? known.FirstOrDefault(c => string.Equals(c, label, StringComparison.OrdinalIgnoreCase))
            ?? known.FirstOrDefault(c => string.Equals(c, original, StringComparison.OrdinalIgnoreCase))
            ?? known.FirstOrDefault(c => string.Equals(c, NexusCategoryName(original), StringComparison.OrdinalIgnoreCase))
            ?? label;
    }

    public static void AssignInstalled(JuniGridConfig cfg, IEnumerable<ModEntry> mods, string? category, string? officialSource = null)
    {
        if (category is null) return;
        var items = mods.ToList();
        if (items.Count == 0) return;
        var existed = cfg.ModUserCategoryOrder.Concat(cfg.ModUserCategory.Values.SelectMany(Split))
            .Contains(category, StringComparer.OrdinalIgnoreCase);
        foreach (var m in items)
        {
            var i = m.Folder.IndexOf('/');
            var key = i > 0 ? m.Folder[..i] : m.Folder;
            if (!string.Equals(key, m.Folder, StringComparison.OrdinalIgnoreCase))
                cfg.ModUserCategory.Remove(m.Folder);
            if (category.Length == 0) cfg.ModUserCategory.Remove(key);
            else cfg.ModUserCategory[key] = category;
        }
        foreach (var one in Split(category))
            if (!cfg.ModUserCategoryOrder.Contains(one, StringComparer.OrdinalIgnoreCase))
                cfg.ModUserCategoryOrder.Add(one);
        if (!existed && category.Length > 0 && !string.IsNullOrWhiteSpace(officialSource))
            cfg.ModOfficialCategoryNames[category] = officialSource;
    }

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
