using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

public sealed partial class NexusService
{
    /// <summary>Mod metadata (name + current version + cover). null on error.</summary>
    /// <summary>v0.46.0：拉取本游戏的官方分类表（category_id → 名称），调用方缓存进 config。</summary>
    public async Task<Dictionary<int, string>?> GetCategoriesAsync()
    {
        using var res = await SendApiAsync(Http, Base + ".json");
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("categories", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var dict = new Dictionary<int, string>();
        foreach (var c in arr.EnumerateArray())
        {
            var id = c.TryGetProperty("category_id", out var e1) && e1.ValueKind == JsonValueKind.Number ? e1.GetInt32()
                   : c.TryGetProperty("id", out var e2) && e2.ValueKind == JsonValueKind.Number ? e2.GetInt32() : -1;
            var name = c.TryGetProperty("name", out var ne) && ne.ValueKind == JsonValueKind.String ? ne.GetString() : null;
            if (id >= 0 && !string.IsNullOrWhiteSpace(name)) dict[id] = name;
        }
        return dict;
    }

    public async Task<NexusModInfo?> GetModAsync(int modId)
        => await CachedAsync<NexusModInfo>(Key("mod|" + modId), async () =>
        {
            using var res = await SendApiAsync(Http, $"{Base}/mods/{modId}.json");
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return new NexusModInfo(
                modId,
                GetStr(root, "name"),
                GetStr(root, "version"),
                GetStr(root, "picture_url"),
                root.TryGetProperty("category_id", out var cid) && cid.ValueKind == JsonValueKind.Number
                    ? cid.GetInt32() : (int?)null);
        });

    /// <summary>依赖解析要的那三样<b>原始</b>证据：页面名、上传者账号名、未转换的 BBCode 说明。
    /// <para>不复用 <see cref="GetModDetailAsync"/> 的 <c>DescriptionHtml</c> —— 那是
    /// <see cref="SanitizeHtml"/> 为<b>显示</b>转出来的 HTML，BBCode 的 <c>[url=…]</c> 已被改写；
    /// 判据读一份显示变换的产物，等于让改样式的人有权改判据。也不取 <c>author</c>（作者手填的自由文本，
    /// 可能是「A &amp; B」），取 <c>uploaded_by</c> —— 与搜索通道里 <c>uploader { name }
    /// 是同一个字段，两道闸吃的方言才是一致的。</para></summary>
    public sealed record ModPageEvidence(string Name, string UploadedBy, string DescriptionBbCode, string PictureUrl);

    public async Task<ModPageEvidence?> GetModPageEvidenceAsync(int modId)
        => await CachedAsync<ModPageEvidence>(Key("evidence|" + modId), async () =>
        {
            using var res = await SendApiAsync(Http, $"{Base}/mods/{modId}.json");
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return new ModPageEvidence(
                GetStr(root, "name"), GetStr(root, "uploaded_by"),
                GetStr(root, "description"), GetStr(root, "picture_url"));
        });

    /// <summary>父包说明里作者亲笔写下的 N 网 mod 链接（按出现顺序、去重）。
    /// 只认 <c>nexusmods.com/&lt;本游戏域&gt;/mods/&lt;数字&gt;</c>，所以 GitHub、官网文档、
    /// 别的游戏的页面一律不算 —— 实测 50188 的说明里两条链接，只有
    /// <c>[url=https://www.nexusmods.com/stardewvalley/mods/49525]Parchment [/url]</c> 这条算。
    /// internal 只为测试舱能拿真 BBCode 喂它。</summary>
    internal static List<int> DescriptionModLinks(string? bbcode, string gameDomain = "stardewvalley")
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(bbcode)) return ids;
        var seen = new HashSet<int>();
        // 两种写法各自成式：[url=目标]文字[/url] 的 URL 在**尖括号里**，[url]目标[/url] 的在标签**外面**。
        // 合成一条通配会把前者的捕获落到锚文本上（"Parchment"），于是整条链静默失效 —— 用例里
        // 那条真机 50188 的正向对照就是拦这个的。
        const string Pat = @"\[url\s*=\s*(?<attr>[^\]]+)\]|\[url\]\s*(?<bare>[^\s\]<]+)";
        foreach (Match m in Regex.Matches(bbcode, Pat, RegexOptions.IgnoreCase))
        {
            var url = (m.Groups["attr"].Success ? m.Groups["attr"].Value : m.Groups["bare"].Value)
                .Trim().Trim('"', '\'', '<');
            var dm = Regex.Match(url, @"nexusmods\.com/" + Regex.Escape(gameDomain) + @"/mods/(\d+)",
                RegexOptions.IgnoreCase);
            if (dm.Success && int.TryParse(dm.Groups[1].Value, out var id) && id > 0 && seen.Add(id))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>Full mod detail for the in-app detail page (incl. HTML description + cover).</summary>
    public async Task<NexusModDetail?> GetModDetailAsync(int modId)
    {
        using var res = await SendApiAsync(SlowHttp, $"{Base}/mods/{modId}.json");
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var updatedTs = GetInt64(root, "updated_timestamp");
        var endorsements = GetInt64(root, "endorsement_count");
        if (endorsements == 0) endorsements = GetInt64(root, "endorsements");

        var deps = new List<NexusModDependency>();
        if (root.TryGetProperty("dependencies", out var depArr) && depArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in depArr.EnumerateArray())
            {
                var did = d.TryGetProperty("mod_id", out var di) && di.ValueKind == JsonValueKind.Number
                    ? di.GetInt32() : 0;
                var dname = GetStr(d, "name");
                var davail = GetStr(d, "availability");
                if (did > 0)
                    deps.Add(new NexusModDependency(did, dname, davail));
            }
        }

        // 图集：primary picture 打头，其余按 images 数组顺序排。
        // Nexus v1 的 images 每项字段可能是 picture_url / original_url / thumbnail_url，
        // 按清晰度优先取 original_url > picture_url > thumbnail_url。
        var primary = GetStr(root, "picture_url");
        var images = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) images.Add(primary);
        if (root.TryGetProperty("images", out var imgArr) && imgArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in imgArr.EnumerateArray())
            {
                var u = GetStr(img, "original_url");
                if (string.IsNullOrEmpty(u)) u = GetStr(img, "picture_url");
                if (string.IsNullOrEmpty(u)) u = GetStr(img, "thumbnail_url");
                if (!string.IsNullOrEmpty(u) && !images.Contains(u, StringComparer.OrdinalIgnoreCase))
                    images.Add(u);
            }
        }
        if (images.Count == 0 && !string.IsNullOrEmpty(primary)) images.Add(primary);

        return new NexusModDetail(
            modId,
            GetStr(root, "name"),
            GetStr(root, "author"),
            GetStr(root, "version"),
            GetStr(root, "summary"),
            SanitizeHtml(GetStr(root, "description")),
            primary,
            GetInt64(root, "mod_downloads"),
            endorsements,
            updatedTs > 0
                ? DateTimeOffset.FromUnixTimeSeconds(updatedTs).LocalDateTime.ToString("yyyy-MM-dd")
                : "",
            deps,
            images);
    }

    // ---- JSON helpers ----
    private static string GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long GetInt64(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// Nexus 的 description 是 BBCode 风格（[b]…[/b]、[url=…]…[/url]、[list]…[/list]），
    /// 不是 HTML —— 直接 (MarkupString) 会把这些当成字面文本显示。
    /// 这里把常见 BBCode 转成真正的 HTML，让详情页能渲染出加粗/链接/列表/颜色。
    /// </summary>
    private static string SanitizeHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = html;
        // 先剥掉危险的 script（防御）
        s = Regex.Replace(s, "<script.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // 若一段是转义实体（&lt;）则先解一次
        if (s.Contains("&lt;", StringComparison.OrdinalIgnoreCase))
            s = System.Net.WebUtility.HtmlDecode(s);

        // ── BBCode → HTML ──
        s = Regex.Replace(s, @"\[b\](.*?)\[/b\]", "<b>$1</b>", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[i\](.*?)\[/i\]", "<i>$1</i>", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[u\](.*?)\[/u\]", "<u>$1</u>", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[s\](.*?)\[/s\]", "<s>$1</s>", RegexOptions.Singleline);
        // 颜色 / 字号（Nexus 的 size 是 1–7 档位，映射到可读的像素字号）
        s = Regex.Replace(s, @"\[color\s*=\s*([^\]]+)\](.*?)\[/color\]",
            "<span style=\"color:$1\">$2</span>", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[size\s*=\s*([^\]]+)\](?<c>.*?)\[/size\]", m =>
        {
            var n = int.TryParse(m.Groups[1].Value.Trim(), out var v) ? v : 3;
            var px = n switch
            {
                <= 1 => 11,
                2 => 12,
                3 => 14,
                4 => 17,
                5 => 20,
                6 => 24,
                _ => 28
            };
            return $"<span style=\"font-size:{px}px\">{m.Groups["c"].Value}</span>";
        }, RegexOptions.Singleline);
        // 链接：[url=外链]文字[/url] 或 [url]外链[/url]
        s = Regex.Replace(s, @"\[url\s*=\s*[^\]]+?\](.*?)\[/url\]", m =>
        {
            var tag = m.Value;
            var url = Regex.Match(tag, @"\[url\s*=\s*(?<u>[^\]]+?)\]").Groups["u"].Value.Trim('"', '\'', ' ');
            var text = Regex.Replace(m.Value, @"^\[url\s*=[^\]]*\]|\[/url\]$", "");
            return $"<a href=\"{System.Net.WebUtility.HtmlEncode(url)}\" target=\"_blank\" rel=\"noopener\">{text}</a>";
        }, RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[url\](.*?)\[/url\]",
            "<a href=\"$1\" target=\"_blank\" rel=\"noopener\">$1</a>", RegexOptions.Singleline);
        // 图片
        s = Regex.Replace(s, @"\[img\s*(?:=\s*([^\]]+))?\](.*?)\[/img\]",
            "<img src=\"$2\" alt=\"\" loading=\"lazy\" class=\"jg-desc-img\"/>", RegexOptions.Singleline);
        // v0.56.0：HTML <img> 标签也统一加 class，便于 Flip 放大
        s = Regex.Replace(s, @"<img\s+([^>]*?)src=""([^""]+)""([^>]*?)>",
            @"<img src=""$2"" alt="""" loading=""lazy"" class=""jg-desc-img""/>", RegexOptions.IgnoreCase);
        // v0.57.0：裸图片直链（既没包 [img] 也不是 <img> 的）也渲染成图片。
        // 前置否定 lookbehind 排除已生成标签属性里的 URL（href="/src=" 前的引号、> 等）。
        s = Regex.Replace(s, @"(?<![""'>=])(https?://[^\s<""'\]\[]+?\.(?:png|jpe?g|gif|webp)(?:\?[a-zA-Z0-9=&_%\-]*)?)",
            "<img src=\"$1\" alt=\"\" loading=\"lazy\" class=\"jg-desc-img\"/>", RegexOptions.IgnoreCase);
        // 引用 / 代码
        s = Regex.Replace(s, @"\[quote\](.*?)\[/quote\]",
            "<blockquote>$1</blockquote>", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[code\](.*?)\[/code\]",
            "<pre>$1</pre>", RegexOptions.Singleline);

        // 列表与列表项：用栈记录 open 的 <ul>/<ol>，让 [/list] 正确闭合并保持嵌套
        var listStack = new Stack<string>();
        s = Regex.Replace(s, @"\[(/)?list(=([^\]]+))?\]", match =>
        {
            if (match.Value == "[/list]")
            {
                if (listStack.Count == 0) return "";
                return "</" + listStack.Pop() + ">";
            }
            var kindAttr = match.Groups[2].Success ? match.Groups[3].Value.Trim() : "";
            var tag = (!string.IsNullOrEmpty(kindAttr) && kindAttr is "1" or "a" or "o") ? "ol" : "ul";
            listStack.Push(tag);
            return "<" + tag + ">";
        }, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[item\]", "<li>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[\*\]", "<li>", RegexOptions.IgnoreCase);

        // 常用子集：字体、水平线、float 保留辅助样式
        s = Regex.Replace(s, @"\[font\s*=\s*([^\]]+)\](.*?)\[/font\]",
            "<span style=\"font-family:$1\">$2</span>", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[hr\]", "<hr/>", RegexOptions.IgnoreCase);

        // ── v1.2.6：实测 45 个包描述里出现、旧版漏掉的四类（旧版走到下面的兜底被当未知标签剥光）──
        // 块级标签自带换行，作者紧贴着写的这一个 <br /> 会变成多余空行 —— 先各吞掉一个
        s = Regex.Replace(s, @"(?i)(<br\s*/?>\s*)(?=\[center\]|\[spoiler\]|\[line\])", "");
        s = Regex.Replace(s, @"(?i)(?<=\[/center\]|\[/spoiler\]|\[line\])\s*<br\s*/?>", "");
        // [center] 104 处：作者的标题/横幅排版全靠它，剥掉标签 = 整页只剩左对齐
        s = Regex.Replace(s, @"\[center\](.*?)\[/center\]",
            "<div class=\"jg-desc-center\">$1</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // [spoiler] 90 处：剥掉标签会把作者折叠起来的长说明直接摊平进正文
        s = Regex.Replace(s, @"\[spoiler\](.*?)\[/spoiler\]",
            "<details class=\"jg-desc-spoiler\"><summary>显示隐藏内容</summary>$1</details>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // [line] 20 处：Nexus 的分隔线写法（[hr] 之外的那一种）
        s = Regex.Replace(s, @"\[line\]", "<hr/>", RegexOptions.IgnoreCase);
        // [heading] 2 处
        s = Regex.Replace(s, @"\[heading\](.*?)\[/heading\]",
            "<h3>$1</h3>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // [youtube]11位ID[/youtube] 12 处：只给可点链接 —— 内嵌播放器在无代理/墙内环境只剩一块空白，
        // 旧版连标签一起剥掉后正文里剩一串裸 ID
        s = Regex.Replace(s, @"\[youtube\]\s*([A-Za-z0-9_\-]{6,20})\s*\[/youtube\]",
            "<a href=\"https://www.youtube.com/watch?v=$1\" target=\"_blank\" rel=\"noopener\">▶ YouTube 视频</a>",
            RegexOptions.IgnoreCase);

        // 剩下的未知 BBCode 当作纯文本剥掉标签语法，避免方括号毒化
        // v0.58.0：列表闭合标记 [*] 的收尾 [/ *] / [/*] 也要清掉（* 不是字母，原规则匹配不到）
        s = Regex.Replace(s, @"\[/?\*\]", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[/?[a-zA-Z][a-zA-Z0-9_=:,/\.# -]*?\]", "", RegexOptions.Singleline);

        return s;
    }
}
