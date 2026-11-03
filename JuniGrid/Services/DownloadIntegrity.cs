using System.IO;
using System.Security.Cryptography;

namespace JuniGrid.Services;

/// <summary>
/// v1.3.13：下载落盘后的内容校验。
/// <para>
/// 现有的 <see cref="ModService.DetectArchiveFormat"/> 只看文件头 —— 能抓住「直链其实是头像/网页」
/// 这类脏包，抓不住「大小刚好对上但内容坏了」的半截包（服务器提前关流、中间被截断的替身文件）。
/// N 网 files.json 自带 md5，就是补这一格的。
/// </para>
/// <para>
/// <b>刻意做成宽容的</b>：没有 md5、读文件失败、md5 格式不对，一律 <see cref="Md5Verdict.Skipped"/>。
/// 这条校验只在「原本能装、现在装不上」的风险为零的前提下才允许存在 ——
/// 遇到不符先自动重下一次，两次都不符就放行并记警告（可能官方重打包了），绝不因为校验把安装拦死。
/// </para>
/// </summary>
public static class DownloadIntegrity
{
    public enum Md5Verdict
    {
        /// <summary>没有可比对的 md5（API 没给 / 读不到文件）→ 不表态。</summary>
        Skipped,
        Match,
        Mismatch,
    }

    /// <summary>拿文件内容算 md5 与预期值比一次。任何异常都收敛成 <see cref="Md5Verdict.Skipped"/>。</summary>
    public static Md5Verdict CheckMd5(string path, string? expectedMd5)
    {
        if (string.IsNullOrWhiteSpace(expectedMd5) || expectedMd5.Length != 32) return Md5Verdict.Skipped;
        if (!File.Exists(path)) return Md5Verdict.Skipped;
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(MD5.HashData(stream));
            return string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase)
                ? Md5Verdict.Match
                : Md5Verdict.Mismatch;
        }
        catch
        {
            return Md5Verdict.Skipped;
        }
    }
}
