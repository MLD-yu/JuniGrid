using System.Security.Cryptography;
using System.Text;

namespace JuniGrid.Manifest;

/// <summary>
/// 清单签名的<b>信任锚</b>：这里放公钥，私钥离线保管、绝不进仓库。
///
/// <para>为什么需要它：自更新要下载一个 120MB 的 setup.exe 并执行。校验它靠官方 sha256，
/// 而官方 sha256 只有 GitHub API 通道给得出；API 被限流（匿名配额 60 次/小时/IP）时
/// 走 HTML 回落，那条通道根本拿不到校验值，于是出现过「零校验直接执行」。
///
/// <para>签一份清单就绕开了这个两难：清单里写 sha256 + 版本 + 下载地址，清单本身用
/// ECDSA P-256 签过名，公钥内置在应用里。于是<b>无论清单从哪取</b>（自家域名 / GitHub Pages /
/// raw / 任何镜像），只要签名验得过，里面那个 sha256 就是我们自己签的，可以拿来当凭据。
///
/// <para>算法选 ECDSA P-256 + SHA-256：.NET 自带（不用引 NuGet、不用外部工具），
/// 签名是 IEEE P1363 定长 64 字节（r||s），换语言重实现时按这个格式对齐即可。
/// </summary>
public static class ManifestTrust
{
    /// <summary>当前签名密钥的标识。写进清单，轮换时用它区分是哪一把签的。</summary>
    public const string KeyId = "2026-01";

    /// <summary>
    /// 签名公钥（SPKI DER 的 base64）。<b>私钥在仓库外，离线保管。</b>
    /// 换密钥：把新公钥加进 <see cref="KnownKeys"/>，等上一版应用基本铺开后再停用旧的那把。
    /// </summary>
    public const string PublicKeySpkiBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIVxWebz5J5ScPKs++fLwZtMmBCHhl+XwgwDhlXwfytzSKqNFxa4twfTh3/DaqFICEBIIAh28GVvpebZmKtF1vA==";

    /// <summary>已信任的公钥集合（key id + SPKI 字节）。轮换期同时认新旧两把。</summary>
    private static readonly (string KeyId, byte[] Spki)[] KnownKeys =
    {
        (KeyId, Decode(PublicKeySpkiBase64)),
    };

    private static byte[] Decode(string b64)
    {
        try { return Convert.FromBase64String(b64); }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>
    /// 验签：对<b>清单的原始字节</b>验，不是对重新序列化后的 JSON 验 ——
    /// 那样会踩「键序/空白/换行算不算进签名」的坑。签名文件与清单文件分开，
    /// 就是为了不用做规范化。
    /// </summary>
    /// <returns>验过 = true；<paramref name="keyId"/> 是用哪把钥匙验的。</returns>
    public static bool Verify(byte[] manifestBytes, byte[] signature, out string keyId)
    {
        keyId = "";
        if (manifestBytes is null || manifestBytes.Length == 0) return false;
        if (signature is null || signature.Length == 0) return false;
        foreach (var (id, spki) in KnownKeys)
        {
            if (VerifyWithKey(manifestBytes, signature, spki, out var used))
            {
                keyId = id;
                return true;
            }
        }
        return false;
    }

    /// <summary>用一把指定的公钥验签。白盒测试用它跑正向路径（临时密钥对）；
    /// 正式路径一律走 <see cref="Verify"/>（只用内置公钥）。</summary>
    internal static bool VerifyWithKey(byte[] manifestBytes, byte[] signature, byte[] spki, out string keyId)
    {
        keyId = "";
        if (manifestBytes is null || manifestBytes.Length == 0) return false;
        if (signature is null || signature.Length == 0) return false;
        if (spki is null || spki.Length == 0) return false;
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(spki, out int _);
            if (ec.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256))
            {
                keyId = "test";
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogSink.Warn($"Manifest", $"验签时出错：{ex.Message}");
        }
        return false;
    }

    /// <summary>验签（文本版：调用方手上已经是字符串时用）。</summary>
    public static bool VerifyText(string manifestText, byte[] signature, out string keyId)
        => Verify(Encoding.UTF8.GetBytes(manifestText ?? ""), signature, out keyId);

    /// <summary>
    /// 验签（签名文件是 base64 文本的那种形态）。
    ///
    /// <para>签名文件刻意做成 base64 文本而不是 64 字节二进制：文本在
    /// GitHub Pages / raw / 各种镜像 / git push / 文本模式传输里都不会被改写
    /// （二进制遇到换行转换、编码猜测就废了）。base64 里的空白
    /// （换行/空格/制表符）在解码时会被忽略，所以带不带尾换行都行。</para>
    /// </summary>
    public static bool VerifyBase64(byte[] manifestBytes, string signatureBase64, out string keyId)
        => VerifyBase64WithKey(manifestBytes, signatureBase64, null, out keyId);

    /// <summary>base64 形态的验签，可指定公钥（null = 用内置公钥集）。</summary>
    internal static bool VerifyBase64WithKey(byte[] manifestBytes, string signatureBase64, byte[]? spki, out string keyId)
    {
        keyId = "";
        byte[] raw;
        try { raw = Convert.FromBase64String((signatureBase64 ?? "").Trim()); }
        catch { return false; }          // 不是合法 base64 —— 不是"验不过"，是"这东西压根不是签名"
        if (spki is null) return Verify(manifestBytes, raw, out keyId);
        return VerifyWithKey(manifestBytes, raw, spki, out keyId);
    }
}

/// <summary>
/// 库里不能直接依赖主程序集（那会形成循环引用），但需要写日志。
/// 主程序集在启动时注册一个实现；没注册时静默丢弃（库不该因为日志而炸）。
/// </summary>
public static class AppLogSink
{
    public static Action<string, string>? InfoWriter { get; set; }
    public static Action<string, string>? WarnWriter { get; set; }

    public static void Warn(string tag, string message)
    {
        try { (WarnWriter ?? InfoWriter)?.Invoke(tag, message); } catch { }
    }

    public static void Info(string tag, string message)
    {
        try { InfoWriter?.Invoke(tag, message); } catch { }
    }
}
