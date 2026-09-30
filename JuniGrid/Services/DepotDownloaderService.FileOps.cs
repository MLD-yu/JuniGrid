using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

public sealed partial class DepotDownloaderService
{
    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string newFileName, string? existingFileName, IntPtr securityAttributes);

    /// <summary>
    /// 同盘硬链接：本体 Content 等只读文件在 staging↔游戏目录之间零拷贝。
    /// 不同盘/失败则返回 false，回落 File.Copy。Mods 不要用硬链接（游戏会写 config，会连带改缓存）。
    /// </summary>
    private static bool TryHardLink(string src, string dest)
    {
        try
        {
            if (!SameVolume(src, dest)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) File.Delete(dest);
            return CreateHardLink(dest, src, IntPtr.Zero);
        }
        catch { return false; }
    }

    private static void CopyGameFileFast(string src, string dest)
    {
        if (TryHardLink(src, dest)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            if (File.Exists(dest)
                && new FileInfo(src).Length == new FileInfo(dest).Length
                && File.GetLastWriteTimeUtc(src) == File.GetLastWriteTimeUtc(dest))
                return;
        }
        catch { }
        File.Copy(src, dest, overwrite: true);
    }

    private static int IoParallelism => Math.Max(4, Environment.ProcessorCount);

    private static bool SameVolume(string a, string b)
    {
        try
        {
            var ra = Path.GetPathRoot(Path.GetFullPath(a));
            var rb = Path.GetPathRoot(Path.GetFullPath(b));
            return !string.IsNullOrEmpty(ra)
                   && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>改名的结果。LastError/Attempts 要带出来：日志里只写「改名失败」判不出是瞬时占用
    /// 还是结构性占用，而这两者的正确处置完全相反（前者值得等，后者等再久也是白等）。</summary>
    private sealed record RelocateOutcome(bool Moved, int Attempts, Exception? LastError);

    /// <summary>同盘目录整棵改名（近似瞬时）；跨盘/占用返回 Moved=false，调用方回落并行拷贝。
    /// ⚠ 退避口径改过（2026-09-23）：原来是 5 次尝试、睡 100+200+500+1000 = 1.8 秒，
    /// 理由是「占用多半是瞬时的」。真机日志否掉了这个前提 —— 连续三次切换里 Mods 与
    /// .junigrid_trash 的改名**每一次都失败**，1.8 秒从来没等到过成功；而搬一个 0 字节的
    /// .junigrid_trash 也要 3.3 秒，全是退避与探测的空转。
    /// 现在压到 3 次尝试、共 300ms：真瞬时（Defender 扫一下、缩略图刚读完）仍然救得回来，
    /// 结构性占用则快速转拷贝。回落路径本身是核对过的，语义仍是「移动」，所以少等不等于少做。
    /// 另外 dest 的整棵删除移出了重试循环 —— 原来每次尝试前都删一遍，等于把同一棵树删 5 次。</summary>
    private static RelocateOutcome TryRelocateDirectory(string src, string dest)
    {
        if (!Directory.Exists(src) || !SameVolume(src, dest)) return new RelocateOutcome(false, 0, null);
        var parent = Path.GetDirectoryName(Path.GetFullPath(dest));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        Exception? last = null;
        var attempt = 0;
        try { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
        catch (Exception ex) { last = ex; }
        for (; attempt < 3; attempt++)
        {
            try
            {
                Directory.Move(src, dest);
                return new RelocateOutcome(true, attempt + 1, null);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 2) try { Thread.Sleep(new int[] { 100, 200 }[attempt]); } catch { }
            }
        }
        AppLog.Warn("DepotDownloader",
            $"改名失败（{attempt} 次尝试）{src} → {dest}：{DescribeIoError(last)}，回落拷贝");
        return new RelocateOutcome(false, attempt, last);
    }

    /// <summary>把异常压成一行可判读的证据：类型 + HRESULT + 原文。
    /// 0x80070005 = ACCESS_DENIED（目录句柄没给 FILE_SHARE_DELETE，或是某进程的当前目录），
    /// 0x80070020 = SHARING_VIOLATION（文件正被人打开）。两者的处置不同，混成一句「被占用」就判不了。</summary>
    private static string DescribeIoError(Exception? ex)
    {
        if (ex is null) return LocService.Tr("(无异常)");
        var hr = ex.HResult;
        var tag = hr switch
        {
            unchecked((int)0x80070005) => "ACCESS_DENIED",
            unchecked((int)0x80070020) => "SHARING_VIOLATION",
            unchecked((int)0x800700B7) => "ALREADY_EXISTS",
            unchecked((int)0x80070091) => "DIR_NOT_EMPTY",
            _ => "0x" + hr.ToString("X8"),
        };
        return $"{ex.GetType().Name}/{tag}: {ex.Message}";
    }


    /// <summary>只读探测：Mods 顶层哪些条目此刻"别人正在用"。
    /// 判据 = 以 FileShare.None 打开一个文件，打得开就说明没人用；打不开（IOException /
    /// 共享冲突）就点名。每个顶层条目最多探 12 个文件就够定位，113 项也就几百次打开、
    /// 几十毫秒，而且只在改名已经失败时才跑。**不写、不改、不删任何东西。**
    /// ⚠ 已知盲区：它只探**文件**句柄。改名失败的常见原因是有人握着**目录本身**的句柄
    /// （FileSystemWatcher、某进程把它当当前目录），那种情况这里永远探不到，
    /// 日志就恒为「(没探到具体项)」—— 真机三次切换全是这个结果。所以判「谁钉住了 Mods」
    /// 不能只看这行，要配合下面那句「我们自己的监听句柄已释放/未释放」一起读。</summary>
    private static string LockedEntries(string dir)
    {
        var hit = new List<string>();
        try
        {
            foreach (var top in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(top.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) continue;
                bool locked;
                try
                {
                    if (Directory.Exists(top))
                    {
                        locked = false;
                        foreach (var f in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).Take(12))
                            if (IsInUse(f)) { locked = true; break; }
                    }
                    else locked = IsInUse(top);
                }
                catch { locked = false; }
                if (!locked) continue;
                hit.Add(name);
                if (hit.Count >= 6) break;
            }
        }
        catch { }
        return hit.Count == 0 ? "(文件级没探到 → 多半是目录句柄，见上面的 ACCESS_DENIED/SHARING_VIOLATION)"
                              : string.Join("、", hit);
    }

    private static bool IsInUse(string file)
    {
        try
        {
            using var fs = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }          // 共享冲突 / 正被人删
        catch (UnauthorizedAccessException) { return false; }   // 只是权限，不算占用
        catch { return false; }
    }

    internal enum DirMove { Renamed, Copied, Failed }

    /// <summary>
    /// 把 src 整棵「移动」到 dest：同盘改名优先，改名失败才拷贝 —— 但拷贝也必须兑现移动的语义。
    /// 旧写法拷完不动源，于是源原地留着变成第二份"权威副本"：实测 2026-09-19 23:27 恢复走拷贝后，
    /// 游戏 Mods 与 1.6.15 抽屉各存一份 113 项（≈661 MB 双份，用户视角＝分不清哪个算数）；
    /// 更坏的是拷一半被打断（关应用/磁盘满/Defender 挂锁），下次切换会拿这份残缺的
    /// 去顶掉抽屉里那份完整的 —— mod 就这么没了。
    /// 所以：拷贝后逐文件核对，全等才删源；核对不过就保留源、把残缺的 dest 改名挪开并报出来。
    /// graveDir 非空时，先把「dest 里有、src 里没有」的顶层条目挪进去 —— 改名快路径会连整个 dest
    /// 一起删掉，而那些条目往往是上一批还没被认领走的 mod，不能跟着蒸发。
    /// </summary>
    internal static DirMove MoveDirectoryVerified(string src, string dest, string? graveDir,
        IProgress<Progress>? progress, string label, string? quarantineRoot = null)
    {
        if (!Directory.Exists(src)) return DirMove.Renamed;   // 没东西可搬，等同于「已不在源侧」

        var t = new PhaseTimer();
        if (graveDir is not null && Directory.Exists(dest))
            MoveAsideForeignEntries(dest, src, graveDir);
        t.Lap(LocService.Tr("让位"));

        var reloc = TryRelocateDirectory(src, dest);
        t.Lap(LocService.Tr("改名"));
        if (reloc.Moved)
        {
            t.Report("DepotDownloader", LocService.Tf("{0}：改名成功（{1}）", label, src));
            return DirMove.Renamed;
        }

        // 转复印之前点一次名：整棵挪不动，一定是 Mods 里某个东西正被人打开着。
        // 异常文本只给一个路径，说不清是谁；把"打不开的顶层条目"列出来才判得出来是
        // 杀软、资源管理器窗口，还是我们自己的线程。
        // 附带报出监听句柄状态：三次真机切换的改名全败在 ACCESS_DENIED，而文件级探测恒为空，
        // 说明钉住它的是**目录**句柄 —— 我们自己的 Mods watcher 是首要嫌疑，把它的状态一起记下来
        // 才能判是不是它（只关 EnableRaisingEvents 不释放句柄，必须 Dispose 才算真放手）。
        AppLog.Warn("DepotDownloader",
            $"{label}：改名不通（{DescribeIoError(reloc.LastError)}，{reloc.Attempts} 次尝试全败 = 非瞬时占用；" +
            $"自家 Mods 监听 {WatcherState()}），文件占用探测 → {LockedEntries(src)}");

        try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
        try
        {
            ParallelCopyDirectory(src, dest, skipTrash: true, progress, label);
        }
        catch (Exception ex)
        {
            // 拷贝中途抛错（磁盘满/源被占用）不能让它冒到调用方 —— 调用方那层 catch 只记一行日志
            // 就继续往下清游戏目录，等于拿一份没拷完的东西去顶现役 Mods。交给下面的核对判 Failed。
            AppLog.Warn("DepotDownloader", $"{label}：拷贝中断（{ex.Message}），转入核对");
        }
        t.Lap(LocService.Tr("拷贝"));

        var bad = CountCopyMismatches(src, dest);
        t.Lap(LocService.Tr("核对"));
        if (bad == 0)
        {
            DeleteTreeParallel(src);
            // 源壳子删不掉多半是瞬时句柄：短等一轮再试。⚠ 别把退避拉太长 —— 占用方是
            // 页面 watcher 这类"不会自己松手"的东西时，长退避只是白等（实测一次多花 8 秒）。
            // 原来是 200+400+800 三轮共 1.4 秒，真机日志显示这 1.4 秒每次都在白等
            // （搬 0 字节的 .junigrid_trash 也照睡），压到一轮 200ms：残留壳子本来就被容忍
            // （下面返回 Copied 并记日志），少等不影响正确性。
            if (Directory.Exists(src))
            {
                try { Directory.Delete(src); } catch { }
                if (Directory.Exists(src))
                {
                    Thread.Sleep(200);
                    DeleteTreeParallel(src);
                }
            }
            t.Lap(LocService.Tr("删源"));
            if (!Directory.Exists(src) || !SourceHasFiles(src))
            {
                // 源已消失，或只剩删不掉的空目录壳子 —— 内容完整落在 dest，移动语义兑现。
                if (Directory.Exists(src))
                    AppLog.Warn("DepotDownloader", $"{label}：拷贝并核对通过，但源空壳删不干净（占用），残留 {src}");
                t.Report("DepotDownloader", Directory.Exists(src)
                    ? LocService.Tf("{0}：走拷贝，源空壳残留", label)
                    : $"{label}：走拷贝（改名不通）");
                return DirMove.Copied;
            }
            // 源里还有真文件（被占用删不掉，常见于硬链接后源名仍被锁）：宁可双份，
            // 也绝不把「源还站着」报成 Copied —— 调用方会拿 Copied 去清游戏目录，
            // 等于用一份没兑现的移动去顶现役 Mods（D14c 钉的就是这个）。
            AppLog.Warn("DepotDownloader",
                $"{label}：拷贝后源仍有文件（占用删不掉），移动未兑现，保留源 {src} 并报 Failed");
            progress?.Report(new Progress(
                LocService.Tf("{0}：移动未完成 —— 源目录里仍有文件被占用删不掉，已保留 {1}。请关闭占用后重试", label, src), null));
            t.Report("DepotDownloader", LocService.Tf("{0}：源残留文件，判 Failed", label));
            return DirMove.Failed;
        }

        // 残缺副本挪哪去：默认挪到 dest 同级。但当 dest 在**现役存档目录**里时，同级 = 游戏读档菜单
        // 会把这个「-incomplete-」壳当成一份可玩存档列出来（实测 2026-09-22 13:35 一次失败拷贝就这么
        // 在 Saves 里留下两份 39M/21M 的完整档，之后每次切换还跟着往返）。所以调用方对存档这类
        // dest 传 quarantineRoot，把残缺副本挪出 Saves，落到备份区的 _incomplete 里，读档菜单就干净了。
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string aside;
        if (quarantineRoot is not null)
        {
            var qdir = Path.Combine(quarantineRoot, "_incomplete");
            aside = Path.Combine(qdir, Path.GetFileName(dest) + "-incomplete-" + stamp);
            try { Directory.CreateDirectory(qdir); } catch { }
        }
        else
        {
            aside = dest + "-incomplete-" + stamp;
        }
        try { Directory.Move(dest, aside); }
        catch { aside = "(挪不开:" + dest + ")"; }
        AppLog.Warn("DepotDownloader",
            $"{label}：拷贝后核对有 {bad} 项不一致，源保留在 {src}，残缺副本已挪到 {aside}");
        progress?.Report(new Progress(
            LocService.Tf("{0}：拷贝后核对有 {1} 项不一致，已保留原目录 {2}，残缺副本挪到 {3} —— 请腾出磁盘/关闭占用后重试", label, bad, src, aside), null));
        t.Report("DepotDownloader", LocService.Tf("{0}：核对不通过（{1} 项）", label, bad));
        return DirMove.Failed;
    }

    /// <summary>把 dest 里「src 没有」的顶层条目挪进 graveDir（改名快路径会连 dest 一起删）。</summary>
    private static void MoveAsideForeignEntries(string dest, string src, string graveDir)
    {
        try
        {
            var srcNames = new HashSet<string>(
                Directory.EnumerateFileSystemEntries(src).Select(Path.GetFileName!),
                StringComparer.OrdinalIgnoreCase);
            var moved = new List<string>();
            foreach (var e in Directory.EnumerateFileSystemEntries(dest))
            {
                var nm = Path.GetFileName(e);
                if (srcNames.Contains(nm)) continue;
                try
                {
                    Directory.CreateDirectory(graveDir);
                    var to = Path.Combine(graveDir, nm);
                    if (Directory.Exists(to) || File.Exists(to))
                        to = Path.Combine(graveDir, nm + "-dup" + DateTime.Now.ToString("HHmmss"));
                    Directory.Move(e, to);
                    moved.Add(nm);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("DepotDownloader", $"dest 独有条目「{nm}」挪进隔离区失败: {ex.Message}");
                }
            }
            if (moved.Count > 0)
                AppLog.Warn("DepotDownloader",
                    $"{dest} 里有 {moved.Count} 项不属于本次移动的源，已挪进 {graveDir}：" + string.Join("、", moved));
        }
        catch { }
    }

    /// <summary>源树里还有没有真文件（空目录壳子不算）—— 决定「源删不干净」是容忍还是判 Failed。</summary>
    private static bool SourceHasFiles(string src)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(src, "*", opts))
            {
                if (Path.GetRelativePath(src, f).Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase))
                    continue;
                return true;
            }
        }
        catch { return true; }   // 连源都数不动 → 当还有文件处理（宁可 Failed）
        return false;
    }

    /// <summary>源里有多少文件在目标缺失或大小不符（回收站不计，与拷贝口径一致）。</summary>
    private static int CountCopyMismatches(string src, string dest)
    {
        int bad = 0;
        try
        {
            // 不用 IgnoreInaccessible：被锁/无权限的源文件必须计入不一致，
            // 否则拷贝跳过它、核对也跳过它，会假报「核对通过」。
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false };
            foreach (var f in Directory.EnumerateFiles(src, "*", opts))
            {
                var rel = Path.GetRelativePath(src, f);
                if (rel.Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                long want;
                try { want = new FileInfo(f).Length; }
                catch { bad++; continue; }
                var to = Path.Combine(dest, rel);
                try
                {
                    if (!File.Exists(to) || new FileInfo(to).Length != want) bad++;
                }
                catch { bad++; }
            }
        }
        catch { return 1; }   // 连源都数不动 → 当核对不过处理（宁可保留源）
        return bad;
    }

    /// <summary>src 和 dest 顶层名字几乎对不上 = 不是「把当前版本存回抽屉」的合法快照
    /// （半截恢复 / 切换竞态 / 放错目录）。这时 Replace 会把抽屉里那份完整的毁掉或扫进孤儿区。
    /// dest 不足 5 项、或看不清 → 不拦（沿用原行为）。</summary>
    private static bool LooksUnlikeSnapshot(string src, string dest)
    {
        try
        {
            if (!Directory.Exists(dest)) return false;
            static IEnumerable<string> RealNames(string dir) =>
                Directory.EnumerateFileSystemEntries(dir)
                    .Select(Path.GetFileName!)
                    .Where(n => !n.StartsWith('.')
                        && !n.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase));
            var destNames = RealNames(dest).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (destNames.Count < 5) return false;
            var srcNames = RealNames(src).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = destNames.Count(srcNames.Contains);
            // 对上不到 1/4 → 不像快照
            return overlap * 4 < destNames.Count;
        }
        catch { return false; }
    }

    /// <summary>Mods 目录里除回收站外还有没有真东西（决定要不要生成一个批次目录）。</summary>
    private static bool HasMovableEntries(string mods)
    {
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(mods))
            {
                var nm = Path.GetFileName(e);
                if (nm.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                    || nm.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>并行删整棵树：先删文件再删目录，比 Directory.Delete(true) 的串行递归快很多。</summary>
    private static void DeleteTreeParallel(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, f =>
            {
                try { File.Delete(f); } catch { }
            });
        }
        catch { }

        try
        {
            var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToList();
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, recursive: false); } catch { try { Directory.Delete(d, true); } catch { } }
            }
        }
        catch { }

        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>清空目录内容（保留目录本身），并行删；回收站子目录可跳过。</summary>
    private static void ClearDirectoryParallel(string dir)
    {
        if (!Directory.Exists(dir)) return;
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch { return; }
        foreach (var e in entries)
        {
            var name = Path.GetFileName(e);
            if (name.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (Directory.Exists(e)) DeleteTreeParallel(e);
                else File.Delete(e);
            }
            catch { }
        }
    }

    /// <summary>robocopy /E /MT：Windows 上大量小文件的最快稳妥拷贝；0–7 为成功。</summary>
    private static bool TryRobocopy(string src, string dest, string extraArgs = "")
    {
        try
        {
            if (!Directory.Exists(src)) return false;
            var exe = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            if (!File.Exists(exe)) return false;
            Directory.CreateDirectory(dest);
            var args = $"\"{src}\" \"{dest}\" /E /MT:{Math.Min(32, IoParallelism)} /R:1 /W:1 " +
                       "/NFL /NDL /NJH /NS /NC /NP /XD junigrid_trash .junigrid_trash" +
                       (string.IsNullOrWhiteSpace(extraArgs) ? "" : " " + extraArgs);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20 * 60 * 1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode is >= 0 and < 8;
        }
        catch { return false; }
    }

    /// <summary>把 staging 目录的总字节数写入 .junigrid-size，列表页免全量扫描。</summary>
    private static void WriteSizeCache(string stagingDir)
    {
        try
        {
            if (!Directory.Exists(stagingDir)) return;
            long size = 0;
            foreach (var f in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f).Equals(".junigrid-size", StringComparison.OrdinalIgnoreCase)) continue;
                try { size += new FileInfo(f).Length; } catch { }
            }
            File.WriteAllText(Path.Combine(stagingDir, ".junigrid-size"), size.ToString());
        }
        catch { }
    }

    private static long ReadSizeCache(string stagingDir)
    {
        try
        {
            var p = Path.Combine(stagingDir, ".junigrid-size");
            if (File.Exists(p) && long.TryParse(File.ReadAllText(p).Trim(), out var v) && v > 0)
                return v;
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// 并行拷贝整棵目录树。Mods 包动辄上千文件，串行递归 File.Copy 是切版本主要卡点。
    /// skipTrash：跳过 Mods 回收站（可达数 GB，切版本不需要跟着搬）。
    /// </summary>
    private static void ParallelCopyDirectory(string src, string dest, bool skipTrash,
        IProgress<Progress>? progress = null, string label = "复制")
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dest);

        var sameVol = string.Equals(Path.GetPathRoot(Path.GetFullPath(src)),
            Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase);
        // 同盘优先硬链接：Mods 整包可达 GB 级，字节拷贝是「直接应用」阶段最慢的一步
        if (sameVol)
        {
            // 目录一次性建好 + 硬链接并行。旧写法每个文件串行做 CreateDirectory(父)+File.Exists+
            // File.Delete+CreateHardLink 四次系统调用，上千文件累积实测 8965ms（2026-09-23 03:25 存入 Mods），
            // 名为 Parallel 实则全串行。改成：目录一遍建完；每文件只试硬链接，失败(多为 dest 已存在)才删了重试。
            try
            {
                foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                {
                    var relD = Path.GetRelativePath(src, d);
                    if (skipTrash && relD.Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.Combine(dest, relD));
                }
            }
            catch { }

            long linked = 0;
            var srcFiles = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
                .Where(f => !(skipTrash && Path.GetRelativePath(src, f)
                    .Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Parallel.ForEach(srcFiles, f =>
            {
                var to = Path.Combine(dest, Path.GetRelativePath(src, f));
                try
                {
                    if (CreateHardLink(to, f, IntPtr.Zero)) { Interlocked.Increment(ref linked); return; }
                    try { File.Delete(to); } catch { }
                    if (CreateHardLink(to, f, IntPtr.Zero)) { Interlocked.Increment(ref linked); return; }
                    File.Copy(f, to, overwrite: true);
                }
                catch
                {
                    try { File.Copy(f, to, overwrite: true); } catch { }
                }
            });
            if (linked > 0)
            {
                progress?.Report(new Progress(LocService.Tf("{0}… 100%（硬链接）", label), 100));
                return;
            }
        }

        // robocopy /MT 对上千小文件明显快于托管 File.Copy
        if (TryRobocopy(src, dest))
        {
            progress?.Report(new Progress($"{label}… 100%", 100));
            return;
        }

        var roots = new Queue<string>();
        roots.Enqueue(src);
        var files = new List<string>();
        while (roots.Count > 0)
        {
            var dir = roots.Dequeue();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (skipTrash && (name.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                               || name.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)))
                    continue;
                roots.Enqueue(d);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var rel = Path.GetRelativePath(src, f);
                if (skipTrash && (rel.Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                               || rel.Contains(".junigrid_trash", StringComparison.OrdinalIgnoreCase)))
                    continue;
                files.Add(f);
            }
        }

        long done = 0;
        var total = files.Count;
        if (total == 0) return;

        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var targetDir = Path.GetDirectoryName(Path.Combine(dest, Path.GetRelativePath(src, f)));
            if (!string.IsNullOrEmpty(targetDir)) parents.Add(targetDir);
        }
        foreach (var d in parents) Directory.CreateDirectory(d);

        var step = Math.Max(1, total / 8);
        long nextReport = step;
        var reportGate = new object();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, file =>
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dest, rel);
            File.Copy(file, target, overwrite: true);
            var n = Interlocked.Increment(ref done);
            var shouldReport = false;
            lock (reportGate)
            {
                if (n == total || n >= nextReport)
                {
                    nextReport = n + step;
                    shouldReport = true;
                }
            }
            if (shouldReport)
                progress?.Report(new Progress($"{label}… {(int)(n * 100.0 / total)}%", (int)(n * 100.0 / total)));
        });
    }

    private static void CopyGameBodyParallel(string staging, string gamePath, IProgress<Progress>? progress)
    {
        var stagingFull = Path.GetFullPath(staging);
        var gameFull = Path.GetFullPath(gamePath);
        var sameVolume = string.Equals(
            Path.GetPathRoot(stagingFull), Path.GetPathRoot(gameFull),
            StringComparison.OrdinalIgnoreCase);

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(staging, file);
            if (rel.StartsWith(".DepotDownloader", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals(ManifestMetaName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(rel).Equals(".junigrid-size", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(rel).Equals(".junigrid-version", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(rel).Equals(CompleteMarkName, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("Mods" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("smapi" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("smapi/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("smapi-installer", StringComparison.OrdinalIgnoreCase))
                continue;
            files.Add(file);
        }

        // 同盘优先硬链接：游戏本体只读使用，链接≈瞬时，切版本从「拷几百 MB」变成「建目录项」。
        // 链接失败（跨盘/权限/不支持）再整树 robocopy，最后才逐文件拷贝。
        if (sameVolume)
        {
            long linked = 0, failed = 0;
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, file =>
            {
                var rel = Path.GetRelativePath(staging, file);
                var target = Path.Combine(gamePath, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target)) File.Delete(target);
                    if (CreateHardLink(target, file, IntPtr.Zero))
                        Interlocked.Increment(ref linked);
                    else
                    {
                        File.Copy(file, target, overwrite: true);
                        Interlocked.Increment(ref failed);
                    }
                }
                catch
                {
                    try { File.Copy(file, Path.Combine(gamePath, rel), overwrite: true); Interlocked.Increment(ref failed); }
                    catch { Interlocked.Increment(ref failed); }
                }
            });
            progress?.Report(new Progress(
                failed == 0
                    ? LocService.Tf("正在写入目标版本文件… 100%（硬链接 {0} 个，几乎不占额外空间）", linked)
                    : $"正在写入目标版本文件… 100%（链接 {linked} / 拷贝 {failed}）",
                100));
            return;
        }

        if (TryRobocopy(staging, gamePath,
                "/XD Mods smapi smapi-installer .DepotDownloader /XF .junigrid-manifest .junigrid-size .junigrid-version .junigrid-complete"))
        {
            progress?.Report(new Progress(LocService.Tr("正在写入目标版本文件… 100%"), 100));
            return;
        }

        long done = 0;
        var total = files.Count;
        var gate = new object();
        var errors = new List<Exception>();
        var step = Math.Max(1, total / 10);
        long nextReport = step;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, file =>
        {
            try
            {
                var rel = Path.GetRelativePath(staging, file);
                var dest = Path.Combine(gamePath, rel);
                CopyGameFileFast(file, dest);
            }
            catch (Exception ex)
            {
                lock (gate) errors.Add(ex);
            }
            var n = Interlocked.Increment(ref done);
            var shouldReport = false;
            lock (gate)
            {
                if (n == total || n >= nextReport)
                {
                    nextReport = n + step;
                    shouldReport = true;
                }
            }
            if (shouldReport)
                progress?.Report(new Progress(LocService.Tf("正在写入目标版本文件… {0}%", (int)(n * 100.0 / total)), (int)(n * 100.0 / total)));
        });

        if (errors.Count > 0)
            throw new DepotException($"写入游戏文件失败（{errors.Count} 个）：{errors[0].Message}");
    }

}
