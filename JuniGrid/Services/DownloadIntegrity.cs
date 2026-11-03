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

    /// <summary>
    /// 异步版 —— <b>UI 线程调用必须用这个</b>。
    /// <para>
    /// v1.3.14：同步版是在调用方的继续上下文里跑的，而 <c>Mods.razor</c> 的
    /// <c>DownloadWithTask</c> 是 Blazor 的 async 方法 —— <c>await</c> 之后的续体回到
    /// UI 线程，于是「读完整个安装包算 md5」直接堵在渲染线程上：一个 200MB 的包
    /// 能冻住界面十几秒（用户反馈的「下载时整个界面卡死」就是这个）。
    /// 这里把整段计算甩到线程池，UI 线程只等一个 await。
    /// </para>
    /// </summary>
    public static Task<Md5Verdict> CheckMd5Async(string path, string? expectedMd5, CancellationToken ct = default)
    {
        // 没给 md5 / 文件不存在 —— 连线程池都不用惊动
        if (string.IsNullOrWhiteSpace(expectedMd5) || expectedMd5.Length != 32) return Task.FromResult(Md5Verdict.Skipped);
        if (!File.Exists(path)) return Task.FromResult(Md5Verdict.Skipped);
        return Task.Run(() => CheckMd5(path, expectedMd5, ct), ct);
    }

    /// <summary>拿文件内容算 md5 与预期值比一次。任何异常都收敛成 <see cref="Md5Verdict.Skipped"/>。
    /// <para>⚠ 会同步读完整个文件：在 UI 线程上调用就等于把界面钉死，那里请用
    /// <see cref="CheckMd5Async"/>。</para></summary>
    public static Md5Verdict CheckMd5(string path, string? expectedMd5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedMd5) || expectedMd5.Length != 32) return Md5Verdict.Skipped;
        if (!File.Exists(path)) return Md5Verdict.Skipped;
        try
        {
            using var stream = File.OpenRead(path);
            // 分块算：既能中途响应取消，也不用把整个文件一次性喂给哈希函数
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buf = new byte[81920];
            int read;
            while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buf, 0, read);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            return string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase)
                ? Md5Verdict.Match
                : Md5Verdict.Mismatch;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Md5Verdict.Skipped;
        }
    }
}
