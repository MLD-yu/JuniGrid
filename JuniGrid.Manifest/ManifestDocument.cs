using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JuniGrid.Manifest;

/// <summary>
/// 更新清单的内容。<b>字段顺序即序列化顺序</b>（System.Text.Json 按声明顺序输出），
/// 同一份内容永远得到同一串字节 —— 方便比对与复现签名。
/// </summary>
public sealed class ManifestDocument
{
    /// <summary>清单格式版本。现在只有 1；将来改结构时老应用读到别的值直接拒绝。</summary>
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    /// <summary>安装包版本，不带 v 前缀，如 "1.3.1"。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>安装包的 SHA-256（64 位十六进制，小写）。这是清单存在的全部意义。</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>安装包字节数。sha256 对不上时它还能多挡一层（长度都不对就更可疑）。</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>安装包下载地址（https）。</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>发布时间（ISO-8601 UTC）。给人看的，也方便判断清单是不是陈旧的。</summary>
    [JsonPropertyName("published_at")]
    public string PublishedAt { get; set; } = "";

    /// <summary>用哪把密钥签的（对应 <see cref="ManifestTrust.KeyId"/>）。</summary>
    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>解析清单字节。<b>任何一项不合规就返回 null</b>（fail closed）——
    /// 解析不出来的东西不能拿去当凭据。</summary>
    public static ManifestDocument? Parse(byte[] utf8Json)
    {
        if (utf8Json is null || utf8Json.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(utf8Json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var schema = root.TryGetProperty("schema", out var sc)
                && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt32(out var sv) ? sv : -1;
            if (schema != 1) return null;

            var version = Str(root, "version");
            if (version is null) return null;
            version = version.Trim().TrimStart('v', 'V');
            if (!System.Version.TryParse(version, out _)) return null;

            var sha = Str(root, "sha256");
            if (sha is null || !IsHex64(sha)) return null;

            if (!root.TryGetProperty("size", out var sz) || sz.ValueKind != JsonValueKind.Number
                || !sz.TryGetInt64(out var size) || size <= 0) return null;

            var url = Str(root, "url");
            if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;

            var published = Str(root, "published_at") ?? "";
            var keyId = Str(root, "key_id") ?? "";

            return new ManifestDocument
            {
                Schema = 1,
                Version = version,
                Sha256 = sha.ToLowerInvariant(),
                Size = size,
                Url = url,
                PublishedAt = published,
                KeyId = keyId,
            };
        }
        catch { return null; }
    }

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim() : null;

    private static bool IsHex64(string s)
    {
        if (s.Length != 64) return false;
        foreach (var c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>序列化成待签名的字节（无 BOM，UTF-8）。签的就是这份字节。</summary>
    public static byte[] Serialize(ManifestDocument doc)
    {
        var json = JsonSerializer.Serialize(doc, WriteOpts);
        return new UTF8Encoding(false).GetBytes(json);
    }
}
