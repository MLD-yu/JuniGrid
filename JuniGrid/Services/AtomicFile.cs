using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 进程被杀/断电也不会截断旧文件的文本写入：先写 .tmp 再 File.Move 原子替换
/// （写一半崩溃时磁盘上要么是旧文件要么是完整新文件）。
/// 供 tasks.json / playtime.json / 翻译缓存等高频落盘的 JSON 文件共用。
/// 现有调用方每文件都是单写者（锁内），这里仍对同路径并发写做了防御：
/// 唯一 tmp + Move 短重试，任何线程都不会因并发而抛异常，最后者胜。
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            // v1.1.6：tmp 带随机后缀 —— 固定 ".tmp" 在同路径两个线程并发写时会互撞
            //（A 把 tmp Move 走后 B 的 Move 落空抛 IOException）。
            var tmp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
            try
            {
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                // 目标被另一个 Move 的 ReplaceFile 短暂锁住（并发下 UAE/IOException 都有）
                // → 稍后换新 tmp 重试
                try { File.Delete(tmp); } catch { }
                Thread.Sleep(10 * attempt);
            }
        }
    }
}
