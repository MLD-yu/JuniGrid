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
    private static readonly string Dir = StoragePaths.AppDataDir;
    private static readonly string FilePath = Path.Combine(Dir, "juni-grid.log");
    private const long MaxBytes = 1024 * 1024;   // 1MB 就滚动

    /// <summary>记录一条警告（WRN）。不必要不问断调用，调用方应只在确实失败/异常才调。</summary>
    public static void Warn(string source, string message)
        => Write("WRN", source, message);

    /// <summary>记录一条常规信息（INF）。没出错但值得留痕的走这里 —— 阶段耗时汇总等。
    /// 不用 Warn：耗时是常态数据，混进 WRN 会把真正的告警淹掉。</summary>
    public static void Info(string source, string message)
        => Write("INF", source, message);

    /// <summary>记录一条错误（ERR）。报错走即时落盘，不等定时器。</summary>
    public static void Error(string source, string message)
        => Write("ERR", source, message);

    /// <summary>记录异常（ERR + 堆栈）。对 catch 里能抓到 ex 的调用最合适。</summary>
    public static void Error(string source, Exception ex)
        => Write("ERR", source, ex.ToString());

    /// <summary>待写行。旧写法每行都「开文件→追加→关文件」并各抢一次全局锁：实测一次立绘
    /// 全量重扫要写 226 行，12 秒里全是同步 IO，扫描线程被盘拖成串行。现在攒在内存里批量落。</summary>
    private static readonly List<string> Buffer = new();
    private const int BufferCap = 4_000;   // 盘被占用时也不许无限涨，超了丢最旧的
    private static readonly System.Threading.Timer FlushTimer =
        new(_ => Flush(), null, 250, 250);

    static AppLog() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();

    private static void Write(string level, string source, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {message}";
            lock (Buffer)
            {
                Buffer.Add(line);
                if (Buffer.Count > BufferCap) Buffer.RemoveRange(0, Buffer.Count - BufferCap);
            }
            if (level == "ERR") Flush();   // 报错之后程序可能马上就没了，不能等定时器
        }
        catch { /* 日志本身失败也绝不能把程序拖崩 */ }
    }

    /// <summary>把缓冲一次性落盘。定时器每 250ms 走一次，退出与报错时也走。</summary>
    public static void Flush()
    {
        string[] batch;
        lock (Buffer)
        {
            if (Buffer.Count == 0) return;
            batch = Buffer.ToArray();
            Buffer.Clear();
        }
        try
        {
            var text = string.Join(Environment.NewLine, batch) + Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                RollIfNeeded();
                File.AppendAllText(FilePath, text);
            }
        }
        catch { /* 落盘失败丢掉这批，不让日志把业务拖住 */ }
    }

    private static void RollIfNeeded()
    {
        if (!File.Exists(FilePath)) return;
        if (new FileInfo(FilePath).Length < MaxBytes) return;
        File.Copy(FilePath, FilePath + ".old", overwrite: true);
        File.Delete(FilePath);
    }
}
