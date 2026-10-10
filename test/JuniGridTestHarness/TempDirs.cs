using System.Collections.Concurrent;

namespace JuniGridTestHarness;

/// <summary>夹具临时目录的统一登记处：造目录就登记，进程退出时删掉<b>本进程登记过</b>的那些。
/// <para>起因（2026-10-10 实机）：<c>%TEMP%</c> 里堆了 4582 个 <c>jg-*</c> 目录 / 443 MB，
/// 单 <c>jg-pack-*</c> 就 3244 个 —— 造「源包目录」的那几处是一次性的 <c>Path.GetTempPath()</c>
/// 拼接，不属于任何 <c>GameSandbox</c>，所以没有任何 Dispose 负责删它；跑一轮测试就漏一批。</para>
/// <para>只按登记表删、不按前缀扫：<c>%TEMP%</c> 那个目录是整机共享的，另一个会话（并行的夹具进程、
/// 或者用户自己）的同名目录不能被这边顺手清掉。</para></summary>
internal static class TempDirs
{
    private static readonly ConcurrentDictionary<string, byte> Registered = new();

    static TempDirs() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

    /// <summary>造一个 <c>%TEMP%\jg-&lt;prefix&gt;-&lt;8 位十六进制&gt;</c> 目录并登记。
    /// 先登记再造：造的中途抛了也得有人收尾。</summary>
    public static string Create(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jg-{prefix}-{Guid.NewGuid().ToString("N")[..8]}");
        Register(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>登记一个别人已经造好的目录 —— 用于「自己删得掉，但删不动时得留给退出清扫」的沙盒。</summary>
    public static void Register(string dir) => Registered[dir] = 0;

    /// <summary>这个目录是不是登记过（钉「helper 造的目录确实进了登记表」这条接线，防止改回裸拼接）。</summary>
    public static bool IsRegistered(string dir) => Registered.ContainsKey(dir);

    /// <summary>删掉登记过的目录，返回删掉的个数。占用中的原样留下、不抛。</summary>
    public static int Cleanup()
    {
        var n = 0;
        foreach (var dir in Registered.Keys.ToArray())
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                Registered.TryRemove(dir, out _);
                n++;
            }
            catch (IOException) { if (!Directory.Exists(dir)) Registered.TryRemove(dir, out _); }
            catch (UnauthorizedAccessException) { }
        }
        return n;
    }
}
