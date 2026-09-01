using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 统一运行日志（%AppData%\JuniGrid\juni-grid.log）。
/// 用于把所有业务层的警告与报错落在文件里，方便排查"哪一步失败了、为什么"。
/// 线程安全；超过 ~1MB 自动滚动到 .old 再开新文件，避免无限膨胀。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid");
    private static readonly string FilePath = Path.Combine(Dir, "juni-grid.log");
    private const long MaxBytes = 1024 * 1024;   // 1MB 就滚动

    /// <summary>记录一条警告（WRN）。不必要不问断调用，调用方应只在确实失败/异常才调。</summary>
    public static void Warn(string source, string message)
        => Write("WRN", source, message);

    /// <summary>记录一条错误（ERR）。</summary>
    public static void Error(string source, string message)
        => Write("ERR", source, message);

    /// <summary>记录异常（ERR + 堆栈）。对 catch 里能抓到 ex 的调用最合适。</summary>
    public static void Error(string source, Exception ex)
        => Write("ERR", source, ex.ToString());

    private static void Write(string level, string source, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                RollIfNeeded();
                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {message}{Environment.NewLine}");
            }
        }
        catch { /* 日志本身失败也绝不能把程序拖崩 */ }
    }

    private static void RollIfNeeded()
    {
        if (!File.Exists(FilePath)) return;
        if (new FileInfo(FilePath).Length < MaxBytes) return;
        File.Copy(FilePath, FilePath + ".old", overwrite: true);
        File.Delete(FilePath);
    }
}