using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 小像合成前的「运行期换图」补丁层。
///
/// <para>小像是我们自己用游戏本体 <c>Content\Characters\Farmer\*.xnb</c> 拼的，本来只认磁盘上的贴图；
/// 而绝大多数角色外观 mod（优雅农夫身体、贴身发型、瘦身姿态）走的是 Content Patcher 的
/// <c>Load</c> / <c>EditImage</c> —— 只在 SMAPI 的内存里换，磁盘一个字都不动。结果就是
/// 游戏里是 mod 的样子，存档列表里还是原版那个人，用户会当成 bug 报上来。</para>
///
/// <para>这里把 <b>已启用</b>的 CP 包里、目标为 <c>Characters/Farmer/*</c> 的换图动作先叠到内存贴图上，
/// 再交给 <see cref="FarmerAvatarService"/> 拼人。覆盖范围刻意收窄，界面/日志不许超过这里写的：</para>
/// <list type="bullet">
/// <item>只认 <c>Load</c>（整张替换）与 <c>EditImage</c>（矩形替换 / <c>Overlay</c> 叠色）；</item>
/// <item>只认贴图，不碰 <c>EditData</c>（Data/HairData、Data/Shirts 那类数据表仍读本体）；</item>
/// <item>条件只判得出「配置项 / PlayerGender / HasMod / HasFile」，其余（季节、日期、天气、HasValue…）
/// 一律当作「不套这一条」——宁可用原版，也不猜一个可能错的季节贴图；</item>
/// <item>token 只解析配置项、<c>DynamicTokens</c> 的静态值与 <c>{{ModId}}</c>/<c>{{Target}}</c> 等少数内建；
/// 解析不出来就整条丢掉并记一行日志。</item>
/// </list>
///
/// <para>代价是「部分还原」：例如 <c>Load</c> 了发型表、但身体走的是判不出的条件，那小像会是
/// 「新发型 + 原版身体」。这比整张保持原版更接近游戏内，也仍然是可解释的。</para>
///
/// <para>快照按 Mods 目录的写入时间 + 2 秒 TTL 缓存：一次弹窗要给 40 多份存档合成小像，
/// 不能每次都把 content.json 重读一遍；启禁 mod 会改 Mods 目录的 mtime，缓存随之失效。</para>
/// </summary>
public static class FarmerAppearancePacks
{
    internal enum Kind { Load, Replace, Overlay }

    internal sealed class Patch
    {
        public string Asset = "";
        public Kind Mode;
        public string Source = "";
        public string PackDir = "";
        public int[]? From;                       // x, y, w, h
        public int[]? To;
        public Dictionary<string, string> Config = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Key, string[] Values)> When = new();
        public string Origin = "";                // 「包名 #序号」，日志与去重用
    }

    internal sealed class Snapshot
    {
        public string Signature = "";
        public List<Patch> Patches = new();
        public HashSet<string> ModIds = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Regex TokenRx = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DecodedTexture?> PngCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    private static Snapshot? _snap;
    private static string _snapRoot = "";
    private static DateTime _snapAt;
    private static DateTime _snapModsStamp;

    /// <summary>贴图补丁集指纹：进小像缓存键，启禁/改配置后旧缓存自然失效。</summary>
    public static string Signature(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return "-";
        return SnapshotOf(gamePath).Signature;
    }

    /// <summary>把已启用 CP 包的换图动作叠到一张本体贴图上。出错一律退回原图。</summary>
    public static DecodedTexture Apply(string? gamePath, string asset, DecodedTexture baseTex, bool male)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return baseTex;
        var snap = SnapshotOf(gamePath);
        if (snap.Patches.Count == 0) return baseTex;

        byte[]? px = null;
        var w = baseTex.Width;
        var h = baseTex.Height;

        foreach (var p in snap.Patches)
        {
            if (!p.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Holds(p.When, male, p.Config, p.PackDir, snap.ModIds)) continue;

            var src = LoadPng(p.Source);
            if (src is null)
            {
                LogOnce("png:" + p.Source, $"{Path.GetFileName(p.PackDir)} 的换图找不到或解不开：{p.Source}");
                continue;
            }

            try
            {
                if (p.Mode == Kind.Load)
                {
                    px = (byte[])src.PixelsRgba.Clone();
                    w = src.Width;
                    h = src.Height;
                    continue;
                }

                px ??= (byte[])baseTex.PixelsRgba.Clone();
                var (sx, sy, sw, sh) = p.From is { Length: 4 } f
                    ? (f[0], f[1], f[2], f[3])
                    : (0, 0, src.Width, src.Height);
                var (dx, dy, dw, dh) = p.To is { Length: 4 } t
                    ? (t[0], t[1], t[2], t[3])
                    : (sx, sy, sw, sh);

                var copyW = Math.Min(Math.Min(sw, dw), Math.Min(src.Width - sx, w - dx));
                var copyH = Math.Min(Math.Min(sh, dh), Math.Min(src.Height - sy, h - dy));
                for (var y = 0; y < copyH; y++)
                    for (var x = 0; x < copyW; x++)
                    {
                        var si = ((sy + y) * src.Width + sx + x) * 4;
                        var di = ((dy + y) * w + dx + x) * 4;
                        var a = src.PixelsRgba[si + 3];
                        if (p.Mode == Kind.Overlay && a != 255)
                        {
                            if (a == 0) continue;
                            var sa = a / 255.0;
                            var da = px[di + 3] / 255.0;
                            var oa = sa + da * (1 - sa);
                            if (oa <= 0) continue;
                            px[di] = (byte)((src.PixelsRgba[si] * sa + px[di] * da * (1 - sa)) / oa);
                            px[di + 1] = (byte)((src.PixelsRgba[si + 1] * sa + px[di + 1] * da * (1 - sa)) / oa);
                            px[di + 2] = (byte)((src.PixelsRgba[si + 2] * sa + px[di + 2] * da * (1 - sa)) / oa);
                            px[di + 3] = (byte)(oa * 255);
                            continue;
                        }
                        px[di] = src.PixelsRgba[si];
                        px[di + 1] = src.PixelsRgba[si + 1];
                        px[di + 2] = src.PixelsRgba[si + 2];
                        px[di + 3] = (byte)a;
                    }
            }
            catch (Exception ex)
            {
                LogOnce("apply:" + p.Origin + ex.GetType().Name,
                    $"换图失败（{p.Origin}）：{ex.Message}");
            }
        }

        return px is null ? baseTex : new DecodedTexture(px, w, h);
    }

    /// <summary>测试用：拿到解析结果，核对「哪些包的动作被认了」。</summary>
    internal static Snapshot SnapshotForTests(string gamePath) => SnapshotOf(gamePath);

    // ─────────────────────────── 快照 ───────────────────────────

    private static Snapshot SnapshotOf(string gamePath)
    {
        var modsDir = Path.Combine(gamePath, "Mods");
        DateTime stamp;
        try { stamp = Directory.Exists(modsDir) ? Directory.GetLastWriteTimeUtc(modsDir) : default; }
        catch { stamp = default; }

        lock (Gate)
        {
            var fresh = _snap is not null
                        && string.Equals(_snapRoot, gamePath, StringComparison.OrdinalIgnoreCase)
                        && stamp == _snapModsStamp
                        && (DateTime.UtcNow - _snapAt).TotalSeconds < 2;
            if (fresh) return _snap!;
        }

        var snap = Build(gamePath, modsDir);
        lock (Gate)
        {
            _snap = snap;
            _snapRoot = gamePath;
            _snapModsStamp = stamp;
            _snapAt = DateTime.UtcNow;
        }
        return snap;
    }

    private static Snapshot Build(string gamePath, string modsDir)
    {
        var snap = new Snapshot();
        if (!Directory.Exists(modsDir)) return snap;

        var files = PackContentFiles(modsDir);
        var sig = new System.Text.StringBuilder();
        foreach (var path in files)
        {
            var packDir = Path.GetDirectoryName(path)!;
            var manifest = Path.Combine(packDir, "manifest.json");
            var config = Path.Combine(packDir, "config.json");
            sig.Append(Rel(modsDir, path)).Append('|').Append(FileStamp(path)).Append(';');
            sig.Append(FileStamp(config)).Append(';');
            sig.Append(FileStamp(manifest)).Append(';');
            // 作者改了图但没动 json（原地覆盖 png）时，靠目录时间兜住：包根与一层子目录
            sig.Append(DirStamp(packDir)).Append(';');
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(packDir))
                    sig.Append(Path.GetFileName(sub)).Append(':').Append(DirStamp(sub)).Append(';');
            }
            catch { }

            var uids = ReadUid(manifest);
            if (uids is not null) snap.ModIds.Add(uids);

            try { ParsePack(path, packDir, snap); }
            catch (Exception ex)
            {
                AppLog.Warn("小像", $"读换图包失败（{Path.GetFileName(packDir)}）：{ex.Message}");
            }
        }
        snap.Signature = files.Count == 0 ? "-" : files.Count + ":" + sig.ToString().GetHashCode().ToString("x8");
        if (snap.Patches.Count > 0)
            LogOnce("snap:" + snap.Signature,
                $"角色外观换图：{files.Count} 个 CP 包里认出 {snap.Patches.Count} 条，小像按 mod 的样子画");
        return snap;
    }

    /// <summary>枚举启用中的 CP 包：跳过 <c>.</c> 前缀（禁用）与回收站，最多下潜 3 层（捆绑子包）。</summary>
    private static List<string> PackContentFiles(string modsDir)
    {
        var list = new List<string>();
        void Walk(string dir, int depth)
        {
            var own = Path.Combine(dir, "content.json");
            if (File.Exists(own)) { list.Add(own); return; }
            if (depth >= 3) return;
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { return; }
            foreach (var sub in subs)
            {
                var n = Path.GetFileName(sub);
                if (n.StartsWith('.') || n.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                Walk(sub, depth + 1);
            }
        }

        List<string> tops;
        try { tops = Directory.EnumerateDirectories(modsDir).ToList(); }
        catch { return list; }
        foreach (var top in tops)
        {
            var n = Path.GetFileName(top);
            if (n.StartsWith('.') || n.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
            Walk(top, 1);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static void ParsePack(string contentPath, string packDir, Snapshot snap)
    {
        using var doc = ParseJson(File.ReadAllBytes(contentPath));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        var modId = ReadUid(Path.Combine(packDir, "manifest.json")) ?? Path.GetFileName(packDir);
        var config = ReadConfig(packDir, root);
        var tokens = ReadTokens(root, config);

        if (!TryProp(root, "Changes", out var changes) || changes.ValueKind != JsonValueKind.Array) return;

        var packName = Path.GetFileName(packDir);
        var index = 0;
        foreach (var ch in changes.EnumerateArray())
        {
            index++;
            if (ch.ValueKind != JsonValueKind.Object) continue;
            var origin = $"{packName} #{index}";

            var action = (Str(ch, "Action") ?? "").ToLowerInvariant();
            if (action is not ("load" or "editimage")) continue;

            var targets = StrList(ch, "Target");
            if (targets.Count == 0) continue;

            var mode = action == "load"
                ? Kind.Load
                : (Str(ch, "PatchMode") ?? "").Equals("Overlay", StringComparison.OrdinalIgnoreCase)
                    ? Kind.Overlay
                    : Kind.Replace;

            var when = ReadWhen(ch);

            foreach (var target in targets)
            {
                var asset = NormalizeAsset(target);
                if (asset is null) continue;   // 只做 Farmer 那几张贴图，别的连日志都不记

                // {{Target}} 这类内建要按【当前目标】展开，所以变量解析放在目标归一化之后
                var fromFile = Sub(Str(ch, "FromFile"), tokens, modId, asset);
                if (string.IsNullOrWhiteSpace(fromFile))
                {
                    LogOnce("tok:" + packDir, $"{packName} 有换图动作用了认不出的变量（{origin}），已跳过");
                    continue;
                }

                snap.Patches.Add(new Patch
                {
                    Asset = asset,
                    Mode = mode,
                    Source = Path.GetFullPath(Path.Combine(packDir, fromFile.Replace('/', Path.DirectorySeparatorChar))),
                    PackDir = packDir,
                    From = Rect(ch, "FromArea"),
                    To = Rect(ch, "ToArea"),
                    Config = config,
                    When = when,
                    Origin = origin,
                });
            }
        }
    }

    /// <summary>只接 <c>Characters/Farmer/*</c>（含 Data 表外的全部本体贴图），别的一律不碰。</summary>
    private static string? NormalizeAsset(string target)
    {
        var t = target.Replace('\\', '/').Trim().TrimStart('/');
        const string prefix = "Characters/Farmer/";
        var idx = t.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx != 0) return null;
        var rest = t[prefix.Length..].Trim();
        return rest.Length == 0 ? null : prefix + rest;
    }

    private static Dictionary<string, string> ReadConfig(string packDir, JsonElement root)
    {
        var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryProp(root, "ConfigSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
            foreach (var item in schema.EnumerateObject())
                if (item.Value.ValueKind == JsonValueKind.Object && TryProp(item.Value, "Default", out var def))
                {
                    var v = Literal(def);
                    if (v is not null) cfg[item.Name] = v;
                }

        var path = Path.Combine(packDir, "config.json");
        try
        {
            if (File.Exists(path))
            {
                using var doc = ParseJson(File.ReadAllBytes(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var item in doc.RootElement.EnumerateObject())
                    {
                        var v = Literal(item.Value);
                        if (v is not null) cfg[item.Name] = v;
                    }
            }
        }
        catch (Exception ex) { AppLog.Warn("小像", $"读 mod 配置失败（{Path.GetFileName(packDir)}）：{ex.Message}"); }
        return cfg;
    }

    /// <summary>DynamicTokens 只取「静态值」：带条件的一律不进表（用到它的动作会因解析失败被丢掉）。</summary>
    private static Dictionary<string, string> ReadTokens(JsonElement root, Dictionary<string, string> config)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in config) tokens[kv.Key] = kv.Value;
        if (!TryProp(root, "DynamicTokens", out var dyn) || dyn.ValueKind != JsonValueKind.Array) return tokens;

        foreach (var t in dyn.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            if (TryProp(t, "When", out _)) continue;
            var name = Str(t, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var value = Sub(Str(t, "Value"), tokens, null);
            if (value is null) continue;
            tokens[name] = value;
        }
        return tokens;
    }

    private static List<(string Key, string[] Values)> ReadWhen(JsonElement change)
    {
        var list = new List<(string, string[])>();
        if (!TryProp(change, "When", out var when) || when.ValueKind != JsonValueKind.Object) return list;
        foreach (var item in when.EnumerateObject())
        {
            var values = new List<string>();
            if (item.Value.ValueKind == JsonValueKind.Array)
                foreach (var v in item.Value.EnumerateArray())
                {
                    var s = Literal(v);
                    if (s is not null) values.Add(s);
                }
            else
            {
                var s = Literal(item.Value);
                if (s is not null) values.Add(s);
            }
            if (values.Count > 0) list.Add((item.Name, values.ToArray()));
        }
        return list;
    }

    /// <summary>条件求值：认得出的才判，认不出的一律 false（宁可用原版）。</summary>
    private static bool Holds(List<(string Key, string[] Values)> when, bool male,
        Dictionary<string, string> config, string packDir, HashSet<string> modIds)
    {
        foreach (var (rawKey, values) in when)
        {
            var negate = rawKey.StartsWith('!');
            var key = negate ? rawKey[1..] : rawKey;
            bool? ok = key.ToLowerInvariant() switch
            {
                "playergender" => values.Any(v => v.Equals(male ? "male" : "female", StringComparison.OrdinalIgnoreCase)),
                "hasmod" => values.Any(v => modIds.Contains(BeforeVersion(v))),
                "hasfile" => values.Any(v => File.Exists(Path.Combine(packDir, v.Replace('/', Path.DirectorySeparatorChar)))),
                _ => config.TryGetValue(key, out var cv)
                    ? values.Any(v => v.Equals(cv, StringComparison.OrdinalIgnoreCase))
                    : null,
            };
            if (ok is null) return false;
            if (negate) ok = !ok;
            if (ok != true) return false;
        }
        return true;
    }

    private static string BeforeVersion(string v)
    {
        var i = v.IndexOf('|');
        return (i < 0 ? v : v[..i]).Trim();
    }

    // ─────────────────────────── JSON 小工具 ───────────────────────────

    private static bool TryProp(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        return false;
    }

    private static string? Str(JsonElement obj, string name)
        => TryProp(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> StrList(JsonElement obj, string name)
    {
        var list = new List<string>();
        if (!TryProp(obj, name, out var v)) return list;
        if (v.ValueKind == JsonValueKind.String)
            list.AddRange((v.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (v.ValueKind == JsonValueKind.Array)
            foreach (var e in v.EnumerateArray())
            {
                var s = Literal(e);
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        return list;
    }

    private static int[]? Rect(JsonElement obj, string name)
    {
        if (!TryProp(obj, name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        int Get(string k) => TryProp(v, k, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : 0;
        return new[] { Get("X"), Get("Y"), Get("Width"), Get("Height") };
    }

    private static string? Literal(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => e.TryGetInt64(out var n) ? n.ToString() : e.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// 展开 <c>{{token}}</c>；有一个认不出就返回 null（整条动作丢掉）。
    /// 内建只认 CP 里最常用的几个：<c>{{Target}}</c> / <c>{{TargetWithoutPath}}</c> /
    /// <c>{{TargetWithoutExtension}}</c> / <c>{{TargetDirectory}}</c> / <c>{{ModId}}</c> ——
    /// 「assets/{{Target}}.png」这种写法在 CP 包里非常普遍。
    /// </summary>
    private static string? Sub(string? input, Dictionary<string, string> tokens, string? modId, string? asset = null)
    {
        if (input is null) return null;
        var slash = asset?.LastIndexOf('/') ?? -1;
        var leaf = asset is null ? null : slash < 0 ? asset : asset[(slash + 1)..];
        var dir = asset is null ? null : slash < 0 ? "" : asset[..slash];
        var failed = false;
        var result = TokenRx.Replace(input, m =>
        {
            var name = m.Groups[1].Value.Trim();
            if (leaf is not null)
                switch (name.ToLowerInvariant())
                {
                    case "target": return asset!;
                    case "targetwithoutpath": return leaf;
                    case "targetwithoutextension": return Path.GetFileNameWithoutExtension(asset!);
                    case "targetdirectory": return dir!;
                }
            if (name.Equals("ModId", StringComparison.OrdinalIgnoreCase) && modId is not null) return modId;
            if (tokens.TryGetValue(name, out var v)) return v;
            failed = true;
            return m.Value;
        });
        return failed ? null : result;
    }

    /// <summary>
    /// 容错解析 CP 的 JSON：允许 <c>//</c> 注释与尾逗号（作者常这么写），并跳过 UTF-8 BOM ——
    /// .NET 的 JsonDocument 不认 BOM，Mountain Gardens 那种带 BOM 的 content.json 会整包读失败。
    /// </summary>
    private static JsonDocument ParseJson(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, start, bytes.Length - start),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
    }

    private static string? ReadUid(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            using var doc = ParseJson(File.ReadAllBytes(manifestPath));
            return Str(doc.RootElement, "UniqueID");
        }
        catch { return null; }
    }

    private static string Rel(string root, string path)
    {
        try { return Path.GetRelativePath(root, path); }
        catch { return path; }
    }

    private static string FileStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "-";
        }
        catch { return "-"; }
    }

    private static string DirStamp(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks.ToString() : "-"; }
        catch { return "-"; }
    }

    private static DecodedTexture? LoadPng(string path)
    {
        var stamp = FileStamp(path);
        if (stamp == "-") return null;
        var key = path + "|" + stamp;
        lock (Gate)
        {
            if (PngCache.TryGetValue(key, out var cached)) return cached;
        }
        var tex = PixelKit.DecodePng(path);
        lock (Gate) PngCache[key] = tex;
        return tex;
    }

    private static void LogOnce(string key, string message)
    {
        lock (Gate)
        {
            if (!Logged.Add(key)) return;
        }
        AppLog.Warn("小像", message);
    }
}
