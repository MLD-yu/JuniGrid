using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 进程被杀/断电也不会截断旧文件的文本写入：先写 .tmp 再 File.Move 原子替换
/// （写一半崩溃时磁盘上要么是旧文件要么是完整新文件）。
/// 供 tasks.json / playtime.json / 翻译缓存等高频落盘的 JSON 文件共用。
/// 现有调用方每文件都是单写者（锁内），这里仍对同路径并发写做了防御：
/// 唯一 tmp + Move 短重试，任何线程都不会因并发而抛异常，最后者胜。
/// <para>"谁造垃圾谁认垃圾"：tmp 的命名格式只有这里知道，所以
/// <see cref="SweepStale"/> 也放在本类 —— 别处再写一份"看着像 tmp 就删"的判断，
/// 迟早把别人放在同一个目录里的 <c>.tmp</c> 当垃圾扫掉。</para>
/// </summary>
internal static class AtomicFile
{
    /// <summary>崩溃留下的 tmp 可以安全删的最短年龄。一次正常写盘是毫秒级，
    /// 十分钟之外还在的只可能是死进程留下的 —— 这道闸是给"第二个实例正在写"留的余量
    ///（单实例接管那条路还没堵死，见 <c>TryTakeOverSingleInstance</c>）。</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目标被另一个 Move 的 ReplaceFile 短暂锁住（并发下 UAE/IOException 都有）
                try { File.Delete(tmp); } catch { }
                if (attempt >= 5) throw;      // 放弃前 tmp 已经删掉了：不再往盘上留垃圾
                Thread.Sleep(10 * attempt);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }   // 非并发类失败（磁盘满、路径没了）同样不留
                throw;
            }
        }
    }

    /// <summary>扫掉这些目录里我们留下的死 tmp，返回删掉的个数。
    /// <para>只认<b>三样都对得上</b>的文件：<c>&lt;同目录某个真实文件&gt;.&lt;8位十六进制&gt;.tmp</c>（命名格式来自上面）、
    /// 那个目标文件此刻还在、且 tmp 的写入时间已超过 <see cref="StaleAfter"/>。
    /// 任何一条不满足就不碰 —— 目录里出现别人的 <c>.tmp</c>、或者另一个实例正在写，都不该被我们清掉。</para></summary>
    public static int SweepStale(IEnumerable<string> dirs)
    {
        var n = 0;
        foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.tmp"))
                {
                    try
                    {
                        var name = Path.GetFileName(f);
                        var dot = name.LastIndexOf('.');            // ".tmp"
                        if (dot <= 0) continue;
                        var stem = name[..dot];                     // "<目标>.<8hex>"
                        var sep = stem.LastIndexOf('.');
                        if (sep <= 0) continue;
                        var target = stem[..sep];
                        var rand = stem[(sep + 1)..];
                        if (rand.Length != 8 || !rand.All(Uri.IsHexDigit)) continue;
                        if (!File.Exists(Path.Combine(dir, target))) continue;
                        if (DateTime.Now - File.GetLastWriteTime(f) < StaleAfter) continue;
                        File.Delete(f);
                        n++;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { AppLog.Warn("Storage", $"扫描残留临时文件失败（{dir}）：{ex.Message}"); }
        }
        return n;
    }
}
