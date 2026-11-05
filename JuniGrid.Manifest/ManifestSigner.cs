using System.Security.Cryptography;

namespace JuniGrid.Manifest;

/// <summary>给清单签名。只在发版机上跑（私钥在那儿），应用侧只验不签。</summary>
public static class ManifestSigner
{
    /// <summary>用 PKCS#8 PEM 私钥对清单字节签名，返回 IEEE P1363 定长 64 字节（r||s）。</summary>
    public static byte[] Sign(byte[] manifestBytes, string pkcs8Pem)
    {
        if (manifestBytes is null || manifestBytes.Length == 0)
            throw new ArgumentException("待签名的清单是空的", nameof(manifestBytes));
        using var ec = ECDsa.Create();
        ec.ImportFromPem(NormalizePem(pkcs8Pem));
        return ec.SignData(manifestBytes, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// 把 PEM 文本收拾干净再导入。
    ///
    /// <para>为什么需要这一步：私钥备份最常见的形态是"手抄/从文本编辑器里复制"，
    /// 而记事本存 UTF-8 会带 BOM（U+FEFF），有些编辑器还会加零宽字符。
    /// <c>ImportFromPem</c> 遇到 BOM 会直接抛"不是有效的 PEM"——
    /// 于是一份<b>内容完全正确</b>的备份被判成废的，用户只会以为自己抄错了。
    /// 去掉 BOM + 去掉首尾空白，两种最常见的"干净的坏备份"就都能用了。</para>
    /// </summary>
    internal static string NormalizePem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return "";
        return pem.Trim('\uFEFF', '\u200B', '\u2060').Trim();
    }

    /// <summary>用 PKCS#8 DER 私钥签名（CI 里从 Secret 取到的通常是 base64 的 DER/PEM）。</summary>
    public static byte[] Sign(byte[] manifestBytes, byte[] pkcs8Der)
    {
        if (manifestBytes is null || manifestBytes.Length == 0)
            throw new ArgumentException("待签名的清单是空的", nameof(manifestBytes));
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(pkcs8Der, out _);
        return ec.SignData(manifestBytes, HashAlgorithmName.SHA256);
    }

    /// <summary>生成一对新密钥（私钥 PKCS#8 PEM + 公钥 SPKI DER 的 base64）。</summary>
    public static (string PrivatePem, string PublicKeySpkiBase64) GenerateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>从 PKCS#8 PEM 私钥导出公钥（SPKI DER 的 base64）—— keygen 之后把它贴进
    /// <see cref="ManifestTrust.PublicKeySpkiBase64"/>。</summary>
    public static string PublicKeyFromPem(string pkcs8Pem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(NormalizePem(pkcs8Pem));
        return Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
    }
}
