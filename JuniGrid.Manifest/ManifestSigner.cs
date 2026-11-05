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
        ec.ImportFromPem(pkcs8Pem);
        return ec.SignData(manifestBytes, HashAlgorithmName.SHA256);
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
        ec.ImportFromPem(pkcs8Pem);
        return Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
    }
}
