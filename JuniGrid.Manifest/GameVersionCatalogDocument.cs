using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JuniGrid.Manifest;

/// <summary>一条「游戏版本 ↔ Steam depot manifest」对照。</summary>
public sealed class GameVersionEntry
{
    /// <summary>版本号（也用作 Steam 分支名，如 "1.6.15" / "1.051b"）。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>Steam depot manifest 的 GID（十进制字符串，uint64）。</summary>
    [JsonPropertyName("manifest")]
    public string Manifest { get; set; } = "";

    /// <summary>界面上的标签（"正式版" / "经典版" / "历史版" …）。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>备注（踩坑记录、来源说明）。只给人看，程序不读。</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

/// <summary>
/// 游戏历史版本表（<c>game-versions.json</c>）。
///
/// <para>为什么要有它：星露谷每更新一次，这张表就多一行。表写死在代码里意味着
/// <b>为了多显示一个版本号，得重新编译并发一个 120MB 的安装包</b>。挪到签名清单里之后，
/// 加一个版本 = 改一行 json + 重签 + 推静态托管，老客户端立刻能看到，不用发版。</para>
///
/// <para>它和 <see cref="ManifestDocument"/> 是同一套信任机制（ECDSA P-256 + SHA-256，
/// 公钥内置），但<b>不是同一个文件</b>：更新清单描述「安装包在哪、sha256 是多少」，
/// 本文件描述「哪个游戏版本对应哪个 manifest」。分开的好处是两者可以各自发布，
/// 改游戏版本表不会牵动自更新的凭据。</para>
///
/// <para><b>顺序即语义</b>：<c>versions</c> 数组从新到旧，下标就是版本序号
/// （0 = 最新），<c>TryGetVersionRank</c> 直接拿它当序。解析器<b>不会排序</b> ——
/// 手写时把新版本插错位置，表现是「版本比较结果反了」，所以改动后务必跑
/// <c>publish-catalog.ps1</c> 的自检。</para>
/// </summary>
public sealed class GameVersionCatalogDocument
{
    /// <summary>格式版本。将来改结构时老应用读到别的值直接拒绝。</summary>
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    /// <summary>Steam AppID（星露谷 = 413150）。</summary>
    [JsonPropertyName("app_id")]
    public string AppId { get; set; } = "";

    /// <summary>depot id（星露谷 Windows = 413151）。</summary>
    [JsonPropertyName("depot_id")]
    public string DepotId { get; set; } = "";

    /// <summary>最后一次人工核对/更新的时间（ISO-8601 UTC）。给人看的。</summary>
    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";

    /// <summary>用哪把密钥签的（对应 <see cref="ManifestTrust.KeyId"/>）。</summary>
    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "";

    /// <summary>说明性文字（来源、收录口径）。程序不读，但会跟着文件一起被签名。</summary>
    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();

    /// <summary>版本条目，<b>从新到旧</b>。</summary>
    [JsonPropertyName("versions")]
    public List<GameVersionEntry> Versions { get; set; } = new();

    /// <summary>条目上限。真实表只有几十条；给到两千已经荒谬，超过就按投毒处理。</summary>
    public const int MaxVersions = 2000;

    /// <summary>文件体积上限。正常 5KB 上下，512KB 足够宽松。</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>单条 version/label 的长度上限。</summary>
    private const int MaxTextLength = 64;

    /// <summary>
    /// 解析目录字节。<b>任何一项不合规就返回 null</b>（fail closed）——
    /// 版本表决定「用户点下载时从哪个 manifest 拉本体」，半截或可疑的表不能拿去用。
    /// </summary>
    public static GameVersionCatalogDocument? Parse(byte[] utf8Json)
    {
        if (utf8Json is null || utf8Json.Length == 0) return null;
        if (utf8Json.Length > MaxBytes) return null;
        try
        {
            // BOM 只影响解析，不影响验签（验签用的是原始字节）。人工用 VS 存过的文件
            // 会带 BOM，这里顺手吃掉，避免「内容明明对却解析不了」。
            var span = utf8Json.AsMemory();
            if (span.Length >= 3 && span.Span[0] == 0xEF && span.Span[1] == 0xBB && span.Span[2] == 0xBF)
                span = span[3..];

            using var doc = JsonDocument.Parse(span);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!TryInt(root, "schema", out var schema) || schema != 1) return null;

            var appId = Str(root, "app_id");
            if (appId is null || !IsDecimal(appId, 20)) return null;

            var depotId = Str(root, "depot_id") ?? "";
            if (depotId.Length > 0 && !IsDecimal(depotId, 20)) return null;

            if (!root.TryGetProperty("versions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            if (arr.GetArrayLength() is 0 or > MaxVersions) return null;

            var list = new List<GameVersionEntry>(arr.GetArrayLength());
            var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenManifests = new HashSet<string>(StringComparer.Ordinal);

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) return null;

                var version = Str(el, "version")?.Trim();
                if (string.IsNullOrEmpty(version) || version.Length > MaxTextLength) return null;
                if (!IsSafeVersionText(version)) return null;

                var manifest = Str(el, "manifest")?.Trim();
                // Steam 的 manifest GID 是 uint64：纯十进制、最长 20 位。
                if (string.IsNullOrEmpty(manifest) || !IsDecimal(manifest, 20)) return null;

                var label = (Str(el, "label") ?? "").Trim();
                if (label.Length > MaxTextLength) return null;

                var note = (Str(el, "note") ?? "").Trim();
                if (note.Length > 512) return null;

                // 重复版本号 = 手滑；重复 manifest = 复制上一行忘了改 ID。
                // 两种都会让「按版本号查 manifest」给出错误答案，直接拒。
                if (!seenVersions.Add(version)) return null;
                if (!seenManifests.Add(manifest)) return null;

                list.Add(new GameVersionEntry
                {
                    Version = version,
                    Manifest = manifest,
                    Label = label,
                    Note = note,
                });
            }

            return new GameVersionCatalogDocument
            {
                Schema = 1,
                AppId = appId,
                DepotId = depotId,
                UpdatedAt = Str(root, "updated_at") ?? "",
                KeyId = Str(root, "key_id") ?? "",
                Notes = ReadNotes(root),
                Versions = list,
            };
        }
        catch { return null; }
    }

    private static List<string> ReadNotes(JsonElement root)
    {
        var notes = new List<string>();
        if (!root.TryGetProperty("notes", out var arr) || arr.ValueKind != JsonValueKind.Array) return notes;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var s = (el.GetString() ?? "").Trim();
            if (s.Length > 0) notes.Add(s);
        }
        return notes;
    }

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : null;

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.Number
               && v.TryGetInt32(out value);
    }

    /// <summary>纯十进制、无正负号、位数受限。</summary>
    private static bool IsDecimal(string s, int maxLen)
    {
        if (s.Length is 0 || s.Length > maxLen) return false;
        foreach (var c in s) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// 版本号文本的字面白名单：字母数字和 <c>. - _</c>。
    /// 不接受空白、控制字符、引号、路径分隔符 —— 这个值会<b>直接进缓存目录名</b>
    /// （<c>StagingDirFor</c> 用它拼 staging 路径），放行 <c>..</c> 之外的怪字符
    /// 就是把目录名交给远端决定。版本表能被远端替换，这道门必须在这里关上。
    /// </summary>
    private static bool IsSafeVersionText(string s)
    {
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c)) continue;
            if (c is '.' or '-' or '_') continue;
            return false;
        }
        // ".." 单独成段会被当成上级目录
        return !s.Contains("..", StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>序列化成待签名的字节（无 BOM，UTF-8）。和 ManifestDocument 一样：签的就是这份字节。</summary>
    public static byte[] Serialize(GameVersionCatalogDocument doc)
    {
        var json = JsonSerializer.Serialize(doc, WriteOpts);
        return new UTF8Encoding(false).GetBytes(json);
    }
}
