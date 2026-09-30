using System.IO;
using System.Reflection;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// 轻量级 i18n —— 「源文本即 key」方案。中文是原文：<see cref="T"/> 直接返回入参；
/// 英文从内嵌资源 i18n/en.json（中文 → 英文 的扁平映射）取，命中则替换、未命中回落中文。
/// 好处：老代码里已有的中文字面量不用改造成 key，可逐页渐进包裹，未包裹的页面照常显示中文。
/// 语言持久化在配置；<see cref="SetLang"/> 变更后触发 <see cref="OnChanged"/>，UI 层热重载。
/// </summary>
public sealed class LocService
{
    private readonly ConfigService _cfg;
    private readonly Dictionary<string, string> _en = new();

    /// <summary>服务层（非 Blazor 组件）没有注入点，用这两个静态入口取词。构造时赋值，未就绪时原样返回。</summary>
    private static LocService? _inst;
    public static string Tr(string zh) => _inst?.T(zh) ?? zh;
    public static string Tf(string zh, params object?[] args) => _inst?.F(zh, args) ?? string.Format(zh, args);

    /// <summary>语言变更事件（切换后 UI 重新取词）。</summary>
    public event Action? OnChanged;

    public LocService(ConfigService cfg)
    {
        _cfg = cfg;
        LoadCatalog();
        _inst = this;
    }

    /// <summary>当前语言："en" 或 "zh"。</summary>
    public string Lang => string.Equals(_cfg.Current.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

    public bool IsEnglish => Lang == "en";

    private void LoadCatalog()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("en.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return;
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return;
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    _en[p.Name] = p.Value.GetString() ?? "";
        }
        catch { /* 目录缺失/损坏 → 回落全中文，不影响运行 */ }
    }

    /// <summary>取当前语言文本。中文原样返回；英文命中则替换。</summary>
    public string T(string zh)
    {
        if (Lang != "en") return zh;
        return _en.TryGetValue(zh, out var en) ? en : zh;
    }

    /// <summary>带占位符的格式化：<c>F("已更新 {0} 个 Mod", n)</c>。英文目录里的串同样保留 {0} 占位。</summary>
    public string F(string zh, params object?[] args)
    {
        var s = T(zh);
        try { return string.Format(s, args); } catch { return s; }
    }

    /// <summary>切换语言并持久化；实际变了才触发 <see cref="OnChanged"/>。</summary>
    public void SetLang(string lang)
    {
        lang = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        var c = _cfg.Current;
        if (string.Equals(c.Language, lang, StringComparison.OrdinalIgnoreCase)) return;
        c.Language = lang;
        _cfg.Save(c);
        OnChanged?.Invoke();
    }
}
