using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using JuniGrid.Services;

// ══════════════════════════════════════════════════════════════════
// JuniGrid 测试台：版本管理（真实环境切换）+ 肖像页（沙箱）+ SMAPI 命令通道
// 用法：dotnet run                     → 全部测试（A 版本 / C 命令 / B 肖像）
//       dotnet run -- --portrait-only  → 只跑肖像页
//       dotnet run -- --smapi-only     → 只跑 SMAPI 命令通道
//       dotnet run -- --fake-smapi <f> → 充当假 SMAPI 子进程（stdin 逐行落文件）
// ══════════════════════════════════════════════════════════════════

// 假 SMAPI 子进程：与 SMAPI 同为 .NET 控制台程序，用同样的方式收 stdin，
// 因此这条管道测的是「我们写出去的东西 + .NET 控制台读进来的东西」，不是 node 的口味。
if (args.Length == 2 && args[0] == "--fake-smapi")
{
    using var sw = new StreamWriter(args[1], append: false, new UTF8Encoding(false)) { AutoFlush = true };
    string? recvLine;
    while ((recvLine = Console.In.ReadLine()) is not null) sw.WriteLine(recvLine);
    Environment.Exit(0);
}

// 同上的原始字节版：不经 Console.In，直接把 stdin 上的字节按十六进制落文件，
// 用来把「谁在用错编码」钉死到字节层面。
if (args.Length == 2 && args[0] == "--fake-smapi-hex")
{
    using var raw = Console.OpenStandardInput();
    using var hw = new StreamWriter(args[1], append: false, new UTF8Encoding(false)) { AutoFlush = true };
    var rb = new byte[256];
    int rn;
    while ((rn = raw.Read(rb, 0, rb.Length)) > 0)
        hw.WriteLine(BitConverter.ToString(rb, 0, rn));
    Environment.Exit(0);
}

// 编码取证：同一句话分别用「默认 / 显式 UTF-8 / 显式 GBK」写进三条同样的管道，
// 看子进程读到的字节和它解码出的文字各是什么。
if (args.Length == 3 && args[0] == "--probe-enc")
{
    Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    const string Probe = "set 技能 10";
    Console.WriteLine("expect utf8 bytes: " + BitConverter.ToString(Encoding.UTF8.GetBytes(Probe)));
    Console.WriteLine("expect gbk   bytes: " + BitConverter.ToString(Encoding.GetEncoding(936).GetBytes(Probe)));

    void EncRun(string tag, string mode, Encoding? enc)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"jg-enc-{tag}.txt");
        try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
        var sp = new ProcessStartInfo
        {
            FileName = args[1],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        if (enc is not null) sp.StandardInputEncoding = enc;
        sp.ArgumentList.Add(mode);
        sp.ArgumentList.Add(outFile);
        var kid = Process.Start(sp)!;
        kid.StandardInput.WriteLine(Probe);
        kid.StandardInput.Flush();
        var deadline = Environment.TickCount64 + 6000;
        string body = "(超时未收到)";
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var fs = new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var t = sr.ReadToEnd();
                if (t.Length > 0) { body = t.TrimEnd(); break; }
            }
            catch { }
            Thread.Sleep(200);
        }
        Console.WriteLine($"  [{tag}] enc={(enc is null ? "default" : enc.WebName)} roundtripOK={(body == Probe)} decodedUtf8={BitConverter.ToString(Encoding.UTF8.GetBytes(body))}");
        try { kid.Kill(true); } catch { }
        try { File.Delete(outFile); } catch { }
    }

    EncRun("bytes-default", "--fake-smapi-hex", null);
    EncRun("bytes-utf8", "--fake-smapi-hex", new UTF8Encoding(false));
    EncRun("bytes-gbk", "--fake-smapi-hex", Encoding.GetEncoding(936));
    EncRun("text-default", "--fake-smapi", null);
    EncRun("text-utf8", "--fake-smapi", new UTF8Encoding(false));
    EncRun("text-gbk", "--fake-smapi", Encoding.GetEncoding(936));
    Environment.Exit(0);
}

// 假 DepotDownloader（扫码路径）见 FakeDepotDownloader.cs —— 那里用 [ModuleInitializer] 拦，
// 因为本文件是 async Main，JIT 一进来就要解析 JuniGrid 程序集，而假 DD 被放在
// tools\DepotDownloader\ 子目录里，那个目录没有 JuniGrid.dll。

// ═══════════════ G. 缓存与 Mods 备份审计（--cache-audit，只碰临时目录） ═══════════════
// 回答两个问题：① SMAPI 安装的 Mods 备份是不是「装完自动移回 + 自动删除」，
// ② 游戏版本缓存那 1.7 GB 里到底有多少是界面上看不见、也删不掉的。
if (args.Contains("--cache-audit"))
{
    Console.WriteLine("\n────── G. 缓存与 Mods 备份审计 ──────");
    var ar = Path.Combine(Path.GetTempPath(), "jg-audit");
    try { if (Directory.Exists(ar)) Directory.Delete(ar, true); } catch { }
    var backupRoot = Path.Combine(ar, "backuproot");
    var gameDir = Path.Combine(ar, "game");
    var gameMods = Path.Combine(gameDir, "Mods");
    int ap = 0, af = 0;
    void A(string name, bool ok, string detail = "")
    { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "   -- " + detail : "")); if (ok) ap++; else af++; }

    // 夹具：一个普通 mod + 一个回收站目录（回收站可达数 GB，绝不该进快照）
    Directory.CreateDirectory(Path.Combine(gameMods, "Existing"));
    File.WriteAllText(Path.Combine(gameMods, "Existing", "x.txt"), "newer");
    Directory.CreateDirectory(Path.Combine(gameMods, ".junigrid_trash", "Gone"));
    File.WriteAllText(Path.Combine(gameMods, ".junigrid_trash", "Gone", "x.txt"), "trash");

    var tUP = typeof(UpdateService);
    var flags = BindingFlags.NonPublic | BindingFlags.Static;
    var backupFn = tUP.GetMethod("BackupMods", flags)!;
    var restoreFn = tUP.GetMethod("RestoreMods", flags)!;
    var delFn = tUP.GetMethod("TryDeleteBackup", flags)!;
    var setCache = typeof(StoragePaths).GetProperty("CacheRoot")!.GetSetMethod(nonPublic: true)!;
    var oldCache = StoragePaths.CacheRoot;
    setCache.Invoke(null, new object?[] { backupRoot });   // ModsBackupDir → backupRoot\mods-backup
    var snapPath = (string?)backupFn.Invoke(null, new object?[] { gameDir });
    try
    {
        A("G1 SMAPI 安装前真的拍了 Mods 快照，且不碰玩家目录、不收回收站",
            snapPath is not null && Directory.Exists(Path.Combine(snapPath!, "Existing"))
            && !Directory.Exists(Path.Combine(snapPath!, ".junigrid_trash"))
            && File.Exists(Path.Combine(gameMods, "Existing", "x.txt")),
            "快照=" + (snapPath is null ? "(null，备份根本没拍)" : Path.GetFileName(snapPath))
            + " 快照条目=" + (snapPath is null ? 0 : Directory.GetFileSystemEntries(snapPath).Length));

        if (snapPath is not null)
        {
            // 装完之后的合并恢复：游戏里那份可能已经比快照更新（安装器/玩家刚改过），
            // 恢复绝不能把旧内容盖回去。
            File.WriteAllText(Path.Combine(gameMods, "Existing", "x.txt"), "newest");
            Directory.CreateDirectory(Path.Combine(snapPath, "OnlyInBackup"));
            File.WriteAllText(Path.Combine(snapPath, "OnlyInBackup", "m.json"), "x");
            restoreFn.Invoke(null, new object?[] { snapPath, gameMods });
            var nowText = File.ReadAllText(Path.Combine(gameMods, "Existing", "x.txt")).Trim();
            A("G2a 恢复不会用旧快照盖掉游戏里更新的文件（备份不污染现役 mod）",
                nowText == "newest", "恢复后内容=" + nowText + "（期望 newest）");
            A("G2b 恢复把快照里独有的 mod 补回游戏",
                Directory.Exists(Path.Combine(gameMods, "OnlyInBackup")),
                "游戏 Mods=" + Directory.GetDirectories(gameMods).Length + " 项");

            delFn.Invoke(null, new object?[] { snapPath });
            // v1.6.11 起快照按版本分桶 → 断言看的是【该版本那一层】+ 旧平铺层
            var smapiBucket = Path.GetDirectoryName(snapPath)!;
            var legacyBucket = Path.Combine(StoragePaths.ModsBackupDir, "smapi");
            A("G2c 安装成功后自动删掉自己那份快照（mods-backup 不会只进不出）",
                !Directory.Exists(snapPath)
                && Directory.GetDirectories(smapiBucket).Length == 0,
                "删后 " + Path.GetFileName(smapiBucket) + " 桶残留=" + Directory.GetDirectories(smapiBucket).Length);

            // 兜底：安装中断留下的旧快照，下一次备份前必须被扫掉（否则越攒越多）
            Directory.CreateDirectory(Path.Combine(smapiBucket, "Mods-20200101_000000"));      // 本版本桶里的残留
            Directory.CreateDirectory(Path.Combine(legacyBucket, "Mods-20190101_000000"));      // 旧平铺布局的残留
            backupFn.Invoke(null, new object?[] { gameDir });
            A("G2d 中断残留的旧快照会在下次备份前清掉（含旧平铺布局，桶里永远最多一份）",
                !Directory.Exists(Path.Combine(smapiBucket, "Mods-20200101_000000"))
                && !Directory.Exists(Path.Combine(legacyBucket, "Mods-20190101_000000")),
                "本版本桶=" + string.Join(",", Directory.GetDirectories(smapiBucket).Select(Path.GetFileName))
                + " ‖ 平铺层=" + string.Join(",", Directory.GetDirectories(legacyBucket).Select(Path.GetFileName)));
        }
    }
    finally { setCache.Invoke(null, new object?[] { oldCache }); try { Directory.Delete(ar, true); } catch { } }

    // ── G3：缓存根 / AppData / LocalAppData 下每个一级条目都必须归属某个界面条目 ──
    static long BytesOf(string dir)
    {
        long t = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        try { foreach (var f in Directory.EnumerateFiles(dir, "*", opts)) { try { t += new FileInfo(f).Length; } catch { } } } catch { }
        return t;
    }
    var auditSvc = new StorageService(new ConfigService(), null!);
    var cache = StoragePaths.CacheRoot ?? oldCache;
    var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid");
    var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid");
    var allRoots = auditSvc.SizeRootsForAudit()
        .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    bool Covered(string entry)
    {
        var e = Path.GetFullPath(entry).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var r in allRoots)
            if (string.Equals(r, e, StringComparison.OrdinalIgnoreCase)
                || r.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || e.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    foreach (var (label, root) in new[] { ("缓存根", cache), ("AppData", appData), ("LocalAppData", localData) })
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        { Console.WriteLine("NOTE  G3/" + label + " 跳过：目录不存在 " + (root ?? "(null)")); continue; }
        var orphans = Directory.GetFileSystemEntries(root)
            .Where(p => !Covered(p))
            .Select(p => Path.GetFileName(p) + "=" + (Directory.Exists(p) ? BytesOf(p) : new FileInfo(p).Length) / 1024 + "KB")
            .ToList();
        A("G3/" + label + " 每个一级条目都归进了界面某一项（看不见就删不掉）",
            orphans.Count == 0, orphans.Count == 0 ? "" : "无主条目: " + string.Join(" ", orphans));
    }

    // ── G4 桶清理只认白名单，且只动那一个目录 ──
    var bRoot = Path.Combine(Path.GetTempPath(), "jg-audit2");
    try { if (Directory.Exists(bRoot)) Directory.Delete(bRoot, true); } catch { }
    var staging = Path.Combine(bRoot, "depot-staging");
    Directory.CreateDirectory(Path.Combine(staging, "_smapi-pool", "modern"));
    File.WriteAllText(Path.Combine(staging, "_smapi-pool", "modern", "a.dat"), "x");
    Directory.CreateDirectory(Path.Combine(staging, "smapi-cache"));
    File.WriteAllText(Path.Combine(staging, "smapi-cache", "b.zip"), "x");
    Directory.CreateDirectory(Path.Combine(staging, "_mods-orphan", "_no-smapi", "1.0.5900", "Mods"));
    File.WriteAllText(Path.Combine(staging, "_mods-orphan", "_no-smapi", "1.0.5900", "Mods", "c.dat"), "x");
    Directory.CreateDirectory(Path.Combine(staging, "413150-413151-1.6.15"));
    File.WriteAllText(Path.Combine(staging, "413150-413151-1.6.15", "keep.dat"), "x");
    var setCache2 = typeof(StoragePaths).GetProperty("CacheRoot")!.GetSetMethod(nonPublic: true)!;
    var oldCache2 = StoragePaths.CacheRoot;
    setCache2.Invoke(null, new object?[] { bRoot });
    try
    {
        var buckets = StorageService.ListStagingBuckets();
        A("G4a 附属目录分行：SMAPI 本体（只剩 _smapi-pool 一个文件夹）与 Mod 暂存各一行，版本包/兜底 zip 不算桶",
            buckets.Length == 2
            && buckets.Single(b => b.Kind.Key == "smapi").Bytes == 1
            && buckets.Single(b => b.Kind.Key == "smapi").Kind.Folders.SequenceEqual(new[] { "_smapi-pool" })
            && buckets.Single(b => b.Kind.Key == "mods").Bytes == 1,
            "列出 " + string.Join(",", buckets.Select(b => b.Kind.Key + ":" + b.Bytes)));
        var noDrawerDir = DepotDownloaderService.GetSmapiInstallerCacheDir("0.0.0-not-a-version");
        A("G4b 桶清理只认白名单；Mod 暂存那条标 Danger=走回收站不硬删；没有版本抽屉时安装包退到通用缓存的 keep（不再藏在 depot-staging 里）",
            StorageService.StagingBucketKinds.All(k => !k.Folders.Contains("413150-413151-1.6.15"))
            && StorageService.StagingBucketKinds.All(k => !k.Folders.Contains("smapi-cache"))
            && StorageService.StagingBucketKinds.Single(k => k.Key == "mods").Danger
            && !StorageService.StagingBucketKinds.Single(k => k.Key == "smapi").Danger
            && noDrawerDir.EndsWith(Path.Combine("smapi-installer", "keep"))
            && !noDrawerDir.StartsWith(StoragePaths.DepotStagingDir, StringComparison.OrdinalIgnoreCase),
            "白名单=" + string.Join(",", StorageService.StagingBucketKinds.Select(k => k.Key + "[" + string.Join("+", k.Folders) + "]"))
            + " ‖ 无抽屉时的缓存目录=" + noDrawerDir);
    }
    finally { setCache2.Invoke(null, new object?[] { oldCache2 }); try { Directory.Delete(bRoot, true); } catch { } }

    // ── G5 mods-backup 按版本分桶：A 版本的快照不会被 B 版本的下一次备份删掉 ──
    var g5 = Path.Combine(Path.GetTempPath(), "jg-audit3");
    try { if (Directory.Exists(g5)) Directory.Delete(g5, true); } catch { }
    var g5Cache = Path.Combine(g5, "cache");
    var g5A = Path.Combine(g5, "gameA"); var g5B = Path.Combine(g5, "gameB");
    foreach (var g in new[] { g5A, g5B })
    {
        Directory.CreateDirectory(Path.Combine(g, "Mods", "M1"));
        File.WriteAllText(Path.Combine(g, "Mods", "M1", "manifest.json"), "{\"Name\":\"M1\"}");
    }
    // 两个"游戏目录"分别放 1.6.15 与 1.4 的本体模板（版本号从文件版本资源读，伪造不了）
    var tmplA = Path.Combine(@"E:\junigrid\depot-staging", "413150-413151-1.6.15", "Stardew Valley.dll");
    var tmplB = Path.Combine(@"E:\junigrid\depot-staging", "413150-413151-1.0", "Stardew Valley.exe");
    var setCache3 = typeof(StoragePaths).GetProperty("CacheRoot")!.GetSetMethod(nonPublic: true)!;
    var oldCache3 = StoragePaths.CacheRoot;
    setCache3.Invoke(null, new object?[] { g5Cache });
    try
    {
        if (!File.Exists(tmplA) || !File.Exists(tmplB))
            Console.WriteLine("NOTE  G5 跳过：缺本体模板（" + (File.Exists(tmplA) ? "" : "1.6.15 ")
                + (File.Exists(tmplB) ? "" : "1.0") + "）—— 在版本管理里重新下载后可跑");
        else
        {
            File.Copy(tmplA, Path.Combine(g5A, Path.GetFileName(tmplA)), true);
            File.Copy(tmplB, Path.Combine(g5B, Path.GetFileName(tmplB)), true);
            var bk5 = typeof(UpdateService).GetMethod("BackupMods", BindingFlags.NonPublic | BindingFlags.Static)!;
            var snapA = (string?)bk5.Invoke(null, new object?[] { g5A });
            var snapB = (string?)bk5.Invoke(null, new object?[] { g5B });
            A("G5 不同游戏版本的备份各存各的桶，互不删除",
                snapA is not null && snapB is not null && Directory.Exists(snapA) && Directory.Exists(snapB)
                && !string.Equals(Path.GetDirectoryName(snapA), Path.GetDirectoryName(snapB), StringComparison.OrdinalIgnoreCase),
                "A=" + (snapA ?? "(null)") + " ‖ B=" + (snapB ?? "(null)"));
        }
    }
    finally { setCache3.Invoke(null, new object?[] { oldCache3 }); try { Directory.Delete(g5, true); } catch { } }

    // ── G6 SMAPI 解压临时目录：成功路径也要删，遗留的要在下次安装前扫掉 ──
    // 旧代码只在两个 catch 里删 temp（取消/失败），成功返回前一行都没碰 → 本机堆出
    // 11 个时间戳目录 / 218 MB。半截续传包 smapi-dl-*.zip 是暂停/继续要用的，绝不能扫掉。
    {
        var g6 = Path.Combine(Path.GetTempPath(), "jg-audit6");
        try { if (Directory.Exists(g6)) Directory.Delete(g6, true); } catch { }
        var setCache6 = typeof(StoragePaths).GetProperty("CacheRoot")!.GetSetMethod(nonPublic: true)!;
        var oldCache6 = StoragePaths.CacheRoot;
        setCache6.Invoke(null, new object?[] { Path.Combine(g6, "cache") });
        try
        {
            var instRoot = StoragePaths.SmapiInstallerDir;
            Directory.CreateDirectory(instRoot);
            var old1 = Path.Combine(instRoot, "4.5.2-20200101_000001");
            var old2 = Path.Combine(instRoot, "latest-20200102_000002");
            var fresh = Path.Combine(instRoot, "4.5.2-20990101_000003");
            foreach (var d in new[] { old1, old2, fresh })
            {
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "payload.dat"), "x");
            }
            Directory.SetLastWriteTimeUtc(old1, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(old2, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
            var keepZip = Path.Combine(instRoot, "smapi-dl-latest.zip");   // 暂停留下的半截包
            File.WriteAllText(keepZip, "PK");
            typeof(UpdateService).GetMethod("SweepSmapiTemps",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            A("G6 遗留解压目录只留最新一份，半截续传 zip 一个字节都不动",
                !Directory.Exists(old1) && !Directory.Exists(old2) && Directory.Exists(fresh)
                && File.Exists(keepZip),
                "old1 还在=" + Directory.Exists(old1) + " ‖ old2 还在=" + Directory.Exists(old2)
                + " ‖ 最新的在=" + Directory.Exists(fresh) + " ‖ 续传包在=" + File.Exists(keepZip));

            // CleanSmapiTemp：删不掉（被占用）要留 WARN，不能静默 —— 静默就是只进不出且没人看得见
            var cleanMi = typeof(UpdateService).GetMethod("CleanSmapiTemp",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            cleanMi.Invoke(null, new object?[] { fresh });
            cleanMi.Invoke(null, new object?[] { (string?)null });   // null 也必须安全
            A("G6b 临时目录删除：成功删干净，传 null 不炸",
                !Directory.Exists(fresh), "fresh 还在=" + Directory.Exists(fresh));
        }
        finally { setCache6.Invoke(null, new object?[] { oldCache6 }); try { Directory.Delete(g6, true); } catch { } }
    }

    Console.WriteLine("\n════════ G 总计：" + ap + " PASS / " + af + " FAIL ════════");
    Environment.Exit(af == 0 ? 0 : 1);
}

var portraitOnly = args.Contains("--portrait-only");
var smapiOnly = args.Contains("--smapi-only");
var guardOnly = args.Contains("--apply-guard");
var qrOnly = args.Contains("--qr-only");
var savesOnly = args.Contains("--saves-guard");
var unitOnly = args.Contains("--unit-only");
// A 段会真实切换游戏目录、真实读写版本缓存 —— 2026-09-22 02:26 那次「构建失败却仍执行了旧
// 可执行文件」把它误跑了一遍，正好撞在用户 Steam 云同步的当口上。
// 从此必须显式 --real-switch；漏参数只会少跑一段，不会动到用户数据。
var realSwitch = args.Contains("--real-switch");

// 诊断用：复现 LauncherService 的 StartInfo 形状，报告 stdin 写侧到底是什么状态
if (args.Length == 3 && args[0] == "--pipe-probe")
{
    var ppsi = new ProcessStartInfo
    {
        FileName = args[1],
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        CreateNoWindow = true,
    };
    ppsi.ArgumentList.Add("--fake-smapi");
    ppsi.ArgumentList.Add(args[2]);
    var kid = Process.Start(ppsi)!;
    var w = kid.StandardInput;
    string[] ReadShared2()
    {
        using var fs = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }
    int Lines() { try { return File.Exists(args[2]) ? ReadShared2().Length : -1; } catch (Exception e) { return -99; } }
    Console.WriteLine($"probe: AutoFlush={w.AutoFlush} NewLine={w.NewLine.Replace("\r", "\\r").Replace("\n", "\\n")} enc={w.Encoding.WebName} baseStream={w.BaseStream.GetType().Name}");
    w.WriteLine("PING-1");
    Thread.Sleep(2500);
    Console.WriteLine("probe: 只 WriteLine，2.5s 后行数=" + Lines() + " 子进程存活=" + !kid.HasExited);
    w.Flush();
    Thread.Sleep(2000);
    Console.WriteLine("probe: StreamWriter.Flush 后行数=" + Lines());
    try { w.BaseStream.Flush(); } catch (Exception e) { Console.WriteLine("probe: BaseStream.Flush 抛 " + e.Message); }
    Thread.Sleep(2000);
    Console.WriteLine("probe: BaseStream.Flush 后行数=" + Lines());
    Console.WriteLine("probe: 文件内容=" + (File.Exists(args[2]) ? string.Join("|", ReadShared2()) : "(无)"));
    try { kid.Kill(true); } catch { }
    Environment.Exit(0);
}
// 单独算画面哈希（--hash-art <文件>[,<文件>…]）：判「两张卡到底是不是同一张画」用，
// 与 PortraitSkinService.ArtHash 同一个函数，避免外面用别的解码器得出不同结论。
if (args.Contains("--hash-art"))
{
    var spec = args.SkipWhile(a => a != "--hash-art").Skip(1).FirstOrDefault() ?? "";
    var artMi2 = typeof(PortraitSkinService).GetMethod("ArtHash",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var decoded = new List<(string Path, DecodedTexture? Tex)>();
    foreach (var f in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var path = f.Trim();
        var h = artMi2.Invoke(null, new object?[] { path });
        DecodedTexture? tex = null;
        try
        {
            tex = path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
                ? XnbDecoder.TryDecode(path) : PixelKit.DecodePng(path);
        }
        catch { }
        decoded.Add((path, tex));
        string dim = tex is null ? "?" : tex.Width + "x" + tex.Height;
        Console.WriteLine((h is null ? "NULL  " : "HASH  ") + dim + "  " + (h ?? "(解不动)") + "  " + path);
    }
    // 两张都解出来就给个像素差：判「用户看到的重复」到底是同一张画还是两张相近的画
    if (decoded.Count == 2 && decoded[0].Tex is not null && decoded[1].Tex is not null)
    {
        var a = decoded[0].Tex!; var b = decoded[1].Tex!;
        if (a.Width != b.Width || a.Height != b.Height)
            Console.WriteLine($"DIFF  画布不同：{a.Width}x{a.Height} vs {b.Width}x{b.Height}（不是同一张画）");
        else
        {
            int diffPx = 0, maxDelta = 0;
            long sumDelta = 0;
            for (int i = 0; i < a.PixelsRgba.Length; i += 4)
            {
                int d = 0;
                for (int c = 0; c < 4; c++)
                {
                    int dd = Math.Abs(a.PixelsRgba[i + c] - b.PixelsRgba[i + c]);
                    if (dd > d) d = dd;
                }
                if (d > 0) { diffPx++; sumDelta += d; if (d > maxDelta) maxDelta = d; }
            }
            int total = a.Width * a.Height;
            Console.WriteLine($"DIFF  同尺寸 {a.Width}x{a.Height}：差异像素 {diffPx}/{total} = "
                + (100.0 * diffPx / total).ToString("0.00") + "%，最大通道差 " + maxDelta
                + "，平均差 " + (diffPx > 0 ? (sumDelta / diffPx).ToString() : "0"));
        }
    }
    Environment.Exit(0);
}

// ═══════════════ 前置身体解析方差诊断（--diag-prereq <角色id> [次数]）═══════════════
// 同代码同数据连扫 N 次，看 ResolvePrereqBody 结果稳不稳，并打印解析链路各环节。
if (args.Contains("--diag-prereq"))
{
    var kwP = args.SkipWhile(a => a != "--diag-prereq").Skip(1).FirstOrDefault() ?? "Wizard";
    var nP = int.TryParse(args.SkipWhile(a => a != "--diag-prereq").Skip(2).FirstOrDefault(), out var nn) ? nn : 3;
    var cfgP = new ConfigService();
    var psP = new PortraitSkinService(new ModService(), cfgP);
    for (int round = 0; round < nP; round++)
    {
        var scanP = psP.Scan(cfgP.Current.GamePath);
        if (round == 0)
        {
            Console.WriteLine($"[PROBE] PackDeps.Count={scanP.PackDeps.Count} PackFolderByUid.Count={scanP.PackFolderByUid.Count}");
            var rrrrKey = scanP.PackDeps.Keys.FirstOrDefault(k => k.Contains("RRRR", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"[PROBE] any RRRR key in PackDeps: {(rrrrKey ?? "(none)")}");
            var byUidRRRR = scanP.PackFolderByUid.FirstOrDefault(kv => kv.Key.Contains("RasmodiaRRRR", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"[PROBE] PackFolderByUid[Nom0ri.RasmodiaRRRR-ish]: {(byUidRRRR.Key ?? "(none)")} → {byUidRRRR.Value}");
        }
        var chP = scanP.Characters.FirstOrDefault(c =>
            string.Equals(c.Id, kwP, StringComparison.OrdinalIgnoreCase));
        if (chP is null) { Console.WriteLine($"round {round}: char {kwP} not found"); continue; }
        var selP = cfgP.Current.PortraitSkins.TryGetValue(chP.Id, out var sp) ? sp : null;
        var optP = chP.AllOptions.FirstOrDefault(o =>
            string.Equals(o.PackName, selP, StringComparison.OrdinalIgnoreCase))
            ?? chP.AllOptions.FirstOrDefault(o =>
                string.Equals(o.PackFolder, selP, StringComparison.OrdinalIgnoreCase));
        var pre = optP is null ? null
            : PortraitSkinService.ResolvePrereqBody(scanP, chP, optP);
        Console.WriteLine($"round {round}: sel={selP ?? "(null)"} opt.Pack={(optP?.PackName ?? "(no-opt)")} opt.Folder={(optP?.PackFolder ?? "-")} opt.Sprite={(optP?.SpriteFile ?? "(null)")}");
        if (optP is not null)
        {
            var hasDeps = scanP.PackDeps.TryGetValue(optP.PackFolder ?? "", out var dl);
            Console.WriteLine($"   PackDeps[{optP.PackFolder}]={(hasDeps ? string.Join(",", dl!) : "(none)")}");
            if (hasDeps)
                foreach (var du in dl!)
                {
                    var okF = scanP.PackFolderByUid.TryGetValue(du, out var df);
                    var optInDep = chP.AllOptions.FirstOrDefault(o =>
                        string.Equals(o.PackFolder, df, StringComparison.OrdinalIgnoreCase)
                        && o.SpriteFile is not null);
                    Console.WriteLine($"   dep uid={du} → folder={(okF ? df : "(unresolved)")} depOptionSprite={(optInDep?.SpriteFile ?? "(none)")}");
                }
        }
        Console.WriteLine($"   ResolvePrereqBody → {(pre?.PackName ?? "(null)")} sprite={(pre?.SpriteFile ?? "-")}");
        Console.WriteLine($"   AllOptions(with sprite) for {kwP}: {string.Join(" | ", chP.AllOptions.Where(o => o.SpriteFile is not null).Select(o => o.PackFolder + ":" + Path.GetFileName(o.SpriteFile!)))}");
    }
    return;
}

// ═══════════════ 默认行来源盘点（--default-audit）═══════════════
// 用户 2026-09-29 拍板"默认行永远锁死原版"。改之前先量清影响面：现在有多少原版角色的
// 「默认」卡指向的其实是 mod 的文件（被扩展包增强过），改完就会变成原版 xnb。
if (args.Contains("--default-audit"))
{
    var cfgD = new ConfigService();
    var psD = new PortraitSkinService(new ModService(), cfgD);
    var scanD = psD.Scan(cfgD.Current.GamePath);
    int modded = 0, total = 0;
    foreach (var c in scanD.Characters.Where(c => !c.Hidden && c.IsVanilla))
    {
        total++;
        var vf = c.Vanilla?.SourceFile;
        var vs = c.Vanilla?.SpriteFile;
        bool faceXnb = vf is { Length: > 0 } && vf.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase);
        bool sprXnb = vs is { Length: > 0 } && vs.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase);
        if (faceXnb && sprXnb) continue;
        modded++;
        Console.WriteLine($"  {c.Id,-14} 脸={(vf is null ? "(无)" : Path.GetFileName(vf))}" +
            $" 身={(vs is null ? "(无)" : Path.GetFileName(vs))}  包={c.Vanilla?.PackFolder ?? "—"}" +
            $"  娘家={c.NativePackFolder ?? "—"}");
    }
    Console.WriteLine($"◆ 原版角色 {total} 个，其中默认行指向 mod 文件的 {modded} 个（锁死原版会改变这些）");
    Environment.Exit(0);
}

// ═══════════════ 往运行中的 SMAPI 控制台塞命令（--smapi-send "<命令>"）═══════════════
// 背景：JuniGrid 用 CreateNoWindow + 重定向 stdin 启动 SMAPI，而 SMAPI 在没有控制台窗口时
// 根本不读那根管道 ⇒ 日志页的「命令输入框」写进去就石沉大海（2026-09-30 用户实测：
// 输入后只有 [JuniGrid] > 回显，SMAPI 毫无反应）。
// 备选通道：AttachConsole(游戏PID) 拿到它的控制台输入缓冲区，用 WriteConsoleInput 直接投
// 键入事件 —— 这条路不经过 Windows Terminal 界面，也就绕开了中文输入法吃空格/改引号的问题。
// 本 flag 就是来验这条通道到底通不通的（成功的话 SMAPI 日志里会留下这条命令的执行痕迹）。
if (args.Contains("--smapi-send"))
{
    var cmdSend = args.SkipWhile(a => a != "--smapi-send").Skip(1).FirstOrDefault() ?? "help";
    var target = System.Diagnostics.Process.GetProcessesByName("StardewModdingAPI")
        .FirstOrDefault(p => !p.HasExited);
    if (target is null) { Console.WriteLine("NO-SMAPI-RUNNING"); Environment.Exit(1); }
    Console.WriteLine("target pid=" + target.Id);

    var okAttach = SmapiConsole.Attach(target.Id);
    Console.WriteLine("attach=" + okAttach + " err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    if (!okAttach) Environment.Exit(2);
    var sent = SmapiConsole.SendLine(cmdSend);
    Console.WriteLine("sent=" + sent);
    SmapiConsole.Detach();
    Environment.Exit(sent ? 0 : 3);
}

// ═══════════════ 实目录重钉（--resync-real）═══════════════
// 与「进肖像页自愈」同一条 SyncToDisk 路径，但打在真实 GamePath 上：
// 本地复现写盘问题不用反复发布换装。会重写覆盖包与各包 config.json（= 生产操作本身）。
if (args.Contains("--resync-real"))
{
    var cfgR = new ConfigService();
    var psR = new PortraitSkinService(new ModService(), cfgR);
    var scanR = psR.Scan(cfgR.Current.GamePath);
    psR.SyncToDisk(cfgR.Current.GamePath, scanR);
    Console.WriteLine("resync done: " + cfgR.Current.GamePath);
    return;
}

// ═══════════════ 单角色钉图取证（--why <角色id>）═══════════════
// 对账说"这一格不对"，但说不清【为什么】：选中项解析成哪张卡、分季表哪来的、
// 每个变体资产最后领到哪个文件。这里把落盘端那条决策链原样摊开（同一个 MatchOption /
// SeasonFilesFor / 同一份 scan），不再靠猜。
if (args.Contains("--why"))
{
    var whoW = args.SkipWhile(a => a != "--why").Skip(1).FirstOrDefault() ?? "";
    if (string.IsNullOrWhiteSpace(whoW))
    {
        Console.WriteLine("用法：--why <角色id>");
        Environment.Exit(2);
    }
    var cfgW = new ConfigService();
    var psW = new PortraitSkinService(new ModService(), cfgW);
    var scanW = psW.Scan(cfgW.Current.GamePath);
    var tyW = typeof(PortraitSkinService);
    var miMatch = tyW.GetMethod("MatchOption", BindingFlags.NonPublic | BindingFlags.Static)!;
    var miVar = tyW.GetMethod("GetMemberVariant", BindingFlags.NonPublic | BindingFlags.Static)!;
    var miGameAsset = tyW.GetMethod("GameAssetId", BindingFlags.NonPublic | BindingFlags.Static)!;
    var miPackSeason = tyW.GetMethod("PackSeasonFiles", BindingFlags.NonPublic | BindingFlags.Static)!;
    var miArtW = tyW.GetMethod("ArtHash", BindingFlags.NonPublic | BindingFlags.Static)!;
    // 尺寸必须一起打：走路表混的是「同宽不同高」那一类 bug（64×192 顶进 64×480 的底图 =
    // 残行是别人家的身子），只看哈希分不出谁矮。
    string Sz(string? p)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) return "";
            using var fs = File.OpenRead(p);
            var b = new byte[24];
            if (fs.Read(b, 0, 24) < 24 || b[1] != 'P' || b[2] != 'N' || b[3] != 'G') return "[非png]";
            return $" {b[19] | b[18] << 8}×{b[23] | b[22] << 8}";
        }
        catch { return ""; }
    }
    string HW(string? p) => string.IsNullOrWhiteSpace(p) || !File.Exists(p) ? "(缺)"
        : ((string?)miArtW.Invoke(null, new object?[] { p }) ?? "(解不动)")[..16] + Sz(p);
    var chW = scanW.Characters.FirstOrDefault(c => c.Id.Equals(whoW, StringComparison.OrdinalIgnoreCase));
    if (chW is null)
    {
        Console.WriteLine("扫描结果里没有这个角色：" + whoW);
        Environment.Exit(2);
    }
    var assetW = (string)miGameAsset.Invoke(null, new object?[] { chW.Id })!;
    Console.WriteLine($"◆ {chW.Id}「{chW.DisplayName}」资产名={assetW} 娘家={chW.NativePackFolder ?? "—"}");
    var globW = cfgW.Current.PortraitSkins.TryGetValue(chW.Id, out var gw) ? gw : null;
    Console.WriteLine($"  配置全局选中 = {(globW is null ? "(没选)" : globW)}");
    if (cfgW.Current.PortraitSeasonSkins.TryGetValue(chW.Id, out var sw2))
        Console.WriteLine("  配置按季选中 = " + sw2.Replace("␟", "\n                   "));
    Console.WriteLine($"  显式默认名单里 = {cfgW.Current.PortraitVanillaDefaults.Contains(chW.Id)}");
    var lkW = PortraitSkinService.GetLock(cfgW.Current, chW.Id);
    Console.WriteLine($"  锁定 = {(lkW is null ? "无" : $"{lkW.PackFolder} / {lkW.Season ?? "四季"}")}");
    Console.WriteLine($"  该角色共 {chW.AllOptions.Count()} 张卡：");
    foreach (var o in chW.AllOptions)
        Console.WriteLine($"     [{(o.IsVanilla ? "官方" : o.IsNative ? "默认" : "皮肤")}] {o.PackName}" +
            $" 画风={o.Variant ?? "—"}{(o.IsCurrentVariant ? "·本档" : "")}  包={o.PackFolder}\n" +
            $"        脸={Path.GetFileName(o.SourceFile ?? "—")} {HW(o.SourceFile)}  " +
            $"身={Path.GetFileName(o.SpriteFile ?? "—")} {HW(o.SpriteFile)}");
    if (globW is not null)
    {
        var wantW = miVar.Invoke(psW, new object?[] { cfgW.Current, chW, null });
        var optW = (PortraitSkinOption?)miMatch.Invoke(null,
            new object?[] { chW.AllOptions, globW, wantW, chW.Id });
        Console.WriteLine($"  MatchOption(包={globW}, 记录画风={wantW ?? "—"}) → " +
            (optW is null ? "解析不到卡 ⇒ 整角色不钉"
                : $"{optW.PackName} 画风={optW.Variant ?? "—"} 脸={Path.GetFileName(optW.SourceFile ?? "")} {HW(optW.SourceFile)}"));
        if (optW is not null)
        {
            var byNameW = PortraitSkinService.GetSeasonFilesForChar(optW.SourceFile, chW.Id);
            var declW = (System.Collections.IDictionary)miPackSeason.Invoke(null,
                new object?[] { scanW, optW.PackFolder, "Portraits", assetW, chW.Id })!;
            var finalW = psW.SeasonFilesFor(scanW, optW.SourceFile, chW.Id, "Portraits", optW.PackFolder);
            Console.WriteLine("     按文件名 = " + string.Join(",", byNameW.Select(kv => kv.Key + ":" + Path.GetFileName(kv.Value))));
            Console.WriteLine("     包声明表 = " + (declW.Count == 0 ? "(空)"
                : string.Join(",", declW.Keys.Cast<string>().Select(k => k + ":" + Path.GetFileName((string)declW[k]!)))));
            Console.WriteLine("     落盘会用 = " + string.Join(",", finalW.Select(kv => kv.Key + ":" + Path.GetFileName(kv.Value))));
        }
    }

    // ── 走路表取证 ──────────────────────────────────────────────
    // 对账只会说"这一格比底图矮"，说不出是哪条链放行的。走路表有四个出口（皮肤自己那张 /
    // 身体链的前置档 / 默认档 / 按季指定），每个出口都得单独摊开看，否则同样的 bug 会换个
    // 入口再来一次（2026-09-29：法师合并卡走前置链、奥莉薇亚走锁定、Haley 走按季）。
    var miSheets = tyW.GetMethod("BodySheetsOf", BindingFlags.Public | BindingFlags.Static)!;
    var miResolveB = tyW.GetMethod("ResolveBody", BindingFlags.Public | BindingFlags.Static)!;
    int H_(string? p)
    {
        try
        {
            using var fs = File.OpenRead(p!);
            var b = new byte[24];
            return fs.Read(b, 0, 24) < 24 ? 0 : b[23] | b[22] << 8;
        }
        catch { return 0; }
    }
    var sheetsW = (List<string>)miSheets.Invoke(null, new object?[] { chW })!;
    // 画布高 = 这条资产在游戏里最终有多高（同宽各家图里最高的那张）。
    // 落盘端现在不再"矮就不钉"，而是把选中那张【纵向拉伸铺满画布】（CopyBody）——
    // 实机证明 CP 先跑 Load 再跑 EditImage、且没有改小画布的字段，拉伸是保住"我选的那包"的唯一解。
    int W_(string? p)
    {
        try
        {
            using var fs = File.OpenRead(p!);
            var b = new byte[24];
            return fs.Read(b, 0, 24) < 24 ? 0 : b[17] | b[16] << 8;
        }
        catch { return 0; }
    }
    int CanvasOf(string? f)
    {
        var w = W_(f); var h = H_(f);
        if (w <= 0 || h <= 0) return 0;
        var best = h;
        foreach (var s in sheetsW) if (W_(s) == w && H_(s) > best) best = H_(s);
        return best;
    }
    string StretchOf(string? f)
    {
        var c = CanvasOf(f); var h = H_(f);
        if (c <= 0 || h <= 0) return "〔量不到尺寸〕";
        return c == h ? "〔已是画布高，原样钉〕" : $"〔画布 {c} ⇒ 纵向拉伸 {(double)c / h:0.##}×〕";
    }
    Console.WriteLine($"  ◆ 走路表画布高清单（{sheetsW.Count} 张，只比同宽度）：");
    foreach (var s in sheetsW.Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderByDescending(x => H_(x)))
        Console.WriteLine($"       {Path.GetFileName(s)}{Sz(s)}  {StretchOf(s)}");
    PortraitSkinOption? optOf(string? pack, string? season) => pack is null ? null
        : pack.Length == 0 ? chW.Vanilla ?? chW.Native
        : (PortraitSkinOption?)miMatch.Invoke(null, new object?[]
            { chW.AllOptions, pack, miVar.Invoke(null, new object?[] { cfgW.Current, chW, season }), chW.Id });
    string? SeasonSibling(string? f, string season) => f is null ? null
        : PortraitSkinService.GetSeasonFilesForChar(f, chW.Id).TryGetValue(season, out var g) ? g : f;
    // 反射拿 ValueTuple：字段是 Item1/Item2，直接强命名元组在这条语法上绕不过去
    string? ResolveBodyFile(PortraitSkinOption o)
    {
        var r = miResolveB.Invoke(null, new object?[] { cfgW.Current.GamePath, scanW, chW, o });
        return r is null ? null : (string?)r.GetType().GetField("Item1")!.GetValue(r);
    }
    string? ResolveBodyPack(PortraitSkinOption o)
    {
        var r = miResolveB.Invoke(null, new object?[] { cfgW.Current.GamePath, scanW, chW, o });
        return r is null ? null : (string?)r.GetType().GetField("Item2")!.GetValue(r);
    }
    if (lkW is not null)
        Console.WriteLine($"  ⚠ 该角色在【锁定】中 ⇒ 落盘走锁定档（身子来自锁定那张卡的精灵），" +
            "下面给的是未锁定链，只作对照。");
    var optG = optOf(globW, null);
    if (optG is not null)
    {
        Console.WriteLine($"  ◆ 出口1 皮肤自己那张 = {Path.GetFileName(optG.SpriteFile ?? "—")}{Sz(optG.SpriteFile)}" +
            (optG.SpriteFile is null ? " 〔这包没有走路表 ⇒ 走链〕" : " 〔钉这张 " + StretchOf(optG.SpriteFile) + "〕"));
        var rbF = ResolveBodyFile(optG);
        Console.WriteLine($"  ◆ 出口2 身体链给出 = {Path.GetFileName(rbF ?? "—")}{Sz(rbF)} 包={ResolveBodyPack(optG) ?? "—"}" +
            (rbF is null ? " 〔整条链都没有 ⇒ 身子不钉〕" : " 〔钉这张 " + StretchOf(rbF) + "〕"));
    }
    if (cfgW.Current.PortraitSeasonSkins.TryGetValue(chW.Id, out var ssW)
        && ssW.Trim().Length > 0)
    {
        Console.WriteLine("  ◆ 出口3 按季指定：");
        foreach (var seg in ssW.Split('␟'))
        {
            var ci = seg.IndexOf(':');
            if (ci <= 0) continue;
            var season = seg[..ci].Trim();
            var oS = optOf(seg[(ci + 1)..].Trim(), season);
            if (oS is null) { Console.WriteLine($"       {season}: 解析不到卡 ⇒ 这季不钉"); continue; }
            var ownS = SeasonSibling(oS.SpriteFile, season);
            var chainF = ownS is null ? ResolveBodyFile(oS) : ownS;
            var finalS = ownS is null ? SeasonSibling(chainF, season) : ownS;
            Console.WriteLine($"       {season}: 自己={Path.GetFileName(ownS ?? "—")}{Sz(ownS)}" +
                (ownS is null ? "〔没身子→走链〕" : "") +
                $" 最终={Path.GetFileName(finalS ?? "—")}{Sz(finalS)}" +
                (finalS is null ? "  ⇒ 这季身子不钉" : "  ⇒ 钉这张 " + StretchOf(finalS)));
        }
    }
    Console.WriteLine("  该角色的变体资产（游戏按场合读的就是这些）：");

    var miRs = tyW.GetMethod("ResolveSeasonalVariantFile", BindingFlags.NonPublic | BindingFlags.Static)!;
    var optF = globW is null ? null : (PortraitSkinOption?)miMatch.Invoke(null,
        new object?[] { chW.AllOptions, globW,
            miVar.Invoke(null, new object?[] { cfgW.Current, chW, null }), chW.Id });
    var faceTblW = optF is null ? new Dictionary<string, string>()
        : psW.SeasonFilesFor(scanW, optF.SourceFile, chW.Id, "Portraits", optF.PackFolder);
    foreach (var v in scanW.VariantAssets.Where(v =>
                 string.Equals(v.BaseId, chW.Id, StringComparison.OrdinalIgnoreCase)))
    {
        var ownW = optF is not null && string.Equals(v.Pack, optF.PackFolder, StringComparison.OrdinalIgnoreCase)
            && File.Exists(v.File) ? v.File : null;
        var resolvedW = (string?)miRs.Invoke(null, new object?[]
            { optF?.SourceFile, v.VariantId, chW.Id, faceTblW });
        Console.WriteLine($"     {v.Kind}/{v.VariantId} ← 包={(v.Pack ?? "—")[..Math.Min(30, (v.Pack ?? "—").Length)]} " +
            $"登记={Path.GetFileName(v.File)} {(File.Exists(v.File) ? HW(v.File) : "〔文件不存在〕")}" +
            (ownW is null ? "" : "  〔本包命中〕") +
            (ownW is null ? $"\n           回落解析→ {Path.GetFileName(resolvedW ?? "—")} {HW(resolvedW)}"
                + (resolvedW is null || !File.Exists(resolvedW) ? "  〔落盘会被幽灵补丁终检丢掉 → 这条场合没人钉〕" : "") : ""));
    }
    Environment.Exit(0);
}

// ═══════════════ 落盘对账（--audit-apply）═══════════════
// 用户 2026-09-28 明确要求："测试现在肖像页所有 npc 的所有肖像，替换的大头照/精灵图到底
// 有没有生效，和肖像页设置的到底一一对应不"。
// 这里逐角色核三件事：① 肖像页选中的那张卡（含按季指定）② 覆盖包真正写进游戏的字节
// ③ 该卡到底有没有身体。任何一格对不上就点名，并顺手列出【还有谁在改同一条资产】——
// 因为"我们钉对了但游戏里不是它"只可能是别人盖过来（更高优先级 / 别的资产名 / Load vs EditImage）。
// ⚠ 它只保证"我们写对了"，不保证"游戏最终显示对"：Portraiture / Alternative Textures 这类
// 在绘制期改图的通道，盘面上看不出来，得靠游戏内截图。
if (args.Contains("--audit-apply"))
{
    var kwA = args.SkipWhile(a => a != "--audit-apply").Skip(1).FirstOrDefault() ?? "";
    var cfgA = new ConfigService();
    var psA = new PortraitSkinService(new ModService(), cfgA);
    var scanA = psA.Scan(cfgA.Current.GamePath);
    var tyPS = typeof(PortraitSkinService);
    var miArt = tyPS.GetMethod("ArtHash", BindingFlags.NonPublic | BindingFlags.Static)!;
    var miLook = tyPS.GetMethod("AssetLookupIds", BindingFlags.NonPublic | BindingFlags.Static)!;
    string HA(string? p) => string.IsNullOrWhiteSpace(p) || !File.Exists(p) ? "(缺文件)"
        : (string?)miArt.Invoke(null, new object?[] { p }) ?? "(解不动)";
    string[] LookIds(string id) => ((IEnumerable<string>)miLook.Invoke(null, new object?[] { id })!).ToArray();
    // 立绘现在会被纵向平铺补满底图（盖住别的包多出来的行）⇒ 整张哈希必然不等。
    // 对账改比"源图那么大的一块"是否逐像素一致，用同一个解码器，不引入第二套口径。
    var decMiA = typeof(PixelKit).GetMethod("DecodePng", BindingFlags.Public | BindingFlags.Static)!;
    bool Covers(string? pinPath, string? srcPath)
    {
        if (string.IsNullOrWhiteSpace(pinPath) || string.IsNullOrWhiteSpace(srcPath)) return false;
        if (!File.Exists(pinPath) || !File.Exists(srcPath)) return false;
        var src = decMiA.Invoke(null, new object?[] { srcPath });
        var pin = decMiA.Invoke(null, new object?[] { pinPath });
        if (src is null || pin is null) return false;
        int W(object o) => (int)o.GetType().GetProperty("Width")!.GetValue(o)!;
        int H(object o) => (int)o.GetType().GetProperty("Height")!.GetValue(o)!;
        byte[] Px(object o) => (byte[])o.GetType().GetProperty("PixelsRgba")!.GetValue(o)!;
        var sw = W(src); var sh = H(src);
        if (W(pin) < sw || H(pin) < sh) return false;
        var pp = Px(pin); var pw = W(pin);
        var buf = new byte[sw * sh * 4];
        for (var y = 0; y < sh; y++)
            Array.Copy(pp, (long)y * pw * 4, buf, (long)y * sw * 4, sw * 4);
        using var sha = System.Security.Cryptography.SHA1.Create();
        var head = BitConverter.GetBytes(sw).Concat(BitConverter.GetBytes(sh)).ToArray();
        sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformFinalBlock(buf, 0, buf.Length);
        return Convert.ToHexString(sha.Hash!) == HA(srcPath);
    }
    var modsDirA = Path.Combine(cfgA.Current.GamePath, "Mods");
    var ovDirA = Path.Combine(modsDirA, PortraitSkinService.OverrideFolder);
    var ovAssetsA = Path.Combine(ovDirA, "assets");
    var seasonsA = new[] { "spring", "summer", "fall", "winter" };

    // ① 覆盖包真正落盘的字节。一条钉入对游戏生效的季，来自三种写法之一：
    //    When:{Season}／变体资产名（Portraits/Caroline_Spring，1.6 的 Appearance 就是这个）／无条件。
    //    对账必须三种都看得懂，否则会把"钉在变体名上"误报成"没钉"。
    var pinW = new List<(string kind, string id, string season, string target, string hash, string file)>();
    var pinU = new List<(string kind, string id, string target, string hash, string file)>();
    foreach (var f in Directory.Exists(ovDirA) ? Directory.GetFiles(ovDirA, "content.json") : Array.Empty<string>())
    {
        var ovJo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(f));
        foreach (var c in ovJo["Changes"]!)
        {
            var tgt = ((string?)c["Target"] ?? "").Trim();
            var slash = tgt.IndexOf('/');
            if (slash <= 0) continue;
            var kind = tgt[..slash];
            var seg = tgt[(slash + 1)..];
            var abs = Path.Combine(ovAssetsA, ((string?)c["FromFile"] ?? "").Replace("assets/", "")
                .Replace('/', Path.DirectorySeparatorChar));
            var hash = HA(abs);
            var wsn = (string?)c["When"]?["Season"];
            if (wsn is { Length: > 0 })
                foreach (var sn in wsn.Split(',', StringSplitOptions.TrimEntries))
                    pinW.Add((kind, seg, sn.ToLowerInvariant(), tgt, hash, abs));
            else pinU.Add((kind, seg, tgt, hash, abs));
        }
    }
    // v1.7.29：HD 肖像通道 —— 选的是 HD 卡时脸【不走 Portraits/<角色>】，钉的是
    // Mods/HDPortraits/<角色> → 该包自己的高清资产（512 宽的表当普通立绘钉，CP 会整条拒绝）。
    // 对账必须认这条，否则"Portraits/Wizard 没钉"会被报成不一致（假红）。
    var hdOwnedFace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in Directory.Exists(ovDirA) ? Directory.GetFiles(ovDirA, "content.json") : Array.Empty<string>())
        foreach (var c in Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(f))["Changes"]!)
            if (string.Equals((string?)c["Action"], "EditData", StringComparison.OrdinalIgnoreCase)
                && ((string?)c["Target"] ?? "").StartsWith("Mods/HDPortraits/", StringComparison.OrdinalIgnoreCase)
                && !((string?)c["Entries"]?["Portrait"] ?? "").StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase))
                hdOwnedFace.Add((((string?)c["Target"]) ?? "")["Mods/HDPortraits/".Length..]);

    // 某个资产名（含 1.6 变体名）在某一季【可能被游戏读到】的所有钉入。
    // 返回 (这一季能出现的画面集合, 说明)。集合为空 = 这一季我们什么都没钉。
    // ⚠ 必须返回"集合"而不是第一条：冬天常年拆「室内 / 室外」两张（都算这一季、都合法），
    //    只比第一条会把合法的双钉误报成 bug（2026-09-28 第一版对账就是这么虚报了潘姆）。
    (List<string> hashes, List<string> files, string note) Candidates(string kind, IEnumerable<string> ids, string season)
    {
        var idset = ids.ToList();
        var vs = season[..1].ToUpperInvariant() + season[1..];
        var byWhen = pinW.Where(p => p.kind == kind && p.season == season
            && idset.Any(i => p.id.Equals(i, StringComparison.OrdinalIgnoreCase)))
            .Select(p => (p.hash, p.file, tag: p.target + "〔When:" + season + "〕")).ToList();
        var byVariant = pinU.Where(p => p.kind == kind && idset.Any(i =>
                p.id.Equals(i + "_" + vs, StringComparison.OrdinalIgnoreCase)
                || p.id.StartsWith(i + "_" + vs + "_", StringComparison.OrdinalIgnoreCase)))
            .Select(p => (p.hash, p.file, tag: p.target + "〔场合资产〕")).ToList();
        // 基资产那条【无条件】补丁任何时候都在候选里：游戏这一季读的就是它（场合资产是另一条
        // 独立资产，1.6 Appearance 才走）。以前只在"别的都没有"时才兜底 ⇒ 法师基资产钉的
        // 64×480 明明在包里，对账却只看见 Characters/Wizard_Spring 那条 64×192 ⇒ 假红。
        var uncond = pinU.Where(p => p.kind == kind
            && idset.Any(i => p.id.Equals(i, StringComparison.OrdinalIgnoreCase)))
            .Select(u => (u.hash, u.file, tag: u.target + "〔无条件〕")).ToList();
        var got = byWhen.Concat(byVariant).Concat(uncond).ToList();
        return got.Count > 0 ? (got.Select(g => g.hash).ToList(), got.Select(g => g.file).ToList(),
            string.Join(" / ", got.Select(g => g.tag).Distinct()))
            : (new List<string>(), new List<string>(), "(没钉)");
    }

    static string Short(string h) => h.Length > 12 ? h[..12] : h;
    var miMatchA = tyPS.GetMethod("MatchOption", BindingFlags.NonPublic | BindingFlags.Static)!;
    var miVarA = tyPS.GetMethod("GetMemberVariant", BindingFlags.NonPublic | BindingFlags.Static)!;
    Console.WriteLine($"◆ 覆盖包格式={PortraitSkinService.OverrideFormat} 落盘钉入={pinW.Count + pinU.Count} 条" +
        $"（分季 {pinW.Count} / 其余 {pinU.Count}）");
    int collide = 0;
    // 落盘文件撞名：两条补丁的 FromFile 只差大小写 ⇒ Windows 上是同一个物理文件，
    // 后写的顶掉先写的 ⇒ 两条补丁读到同一份字节（"半生效"的一类成因，2026-09-28）。
    var byFile = new Dictionary<string, List<(string raw, string hash)>>(StringComparer.OrdinalIgnoreCase);
    foreach (var pf in pinW.Select(p => p.file).Concat(pinU.Select(p => p.file)))
    {
        // 键必须是【完整路径】的小写：assets/Portraits/Evelyn.png 与 assets/Characters/Evelyn.png
        // 只是同名不同目录，不是撞车；真撞车是同一路径只差大小写（Windows 视为同一文件）。
        var keyF = pf.ToLowerInvariant();
        if (!byFile.TryGetValue(keyF, out var lst)) byFile[keyF] = lst = new();
        lst.Add((pf, HA(pf)));
    }
    foreach (var kv in byFile)
    {
        var cases = kv.Value.Select(v => v.raw).Distinct(StringComparer.Ordinal).Count();
        var hashes = kv.Value.Select(v => v.hash).Distinct().Count();
        if (cases <= 1 || hashes <= 1) continue;   // 字节相同就不算顶掉
        collide++;
        Console.WriteLine($"    落盘撞名 {kv.Key}：{cases} 种大小写写法指向同一个物理文件 → 后写的顶掉先写的");
    }
    var flagged = new List<(string ch, string tag, string kind, string assetFamily)>();
    // 每条场合资产"自己的底图"有多大（各家登记的图，按宽度分组取高）
    var natSizes = new Dictionary<string, List<(int w, int h)>>(StringComparer.OrdinalIgnoreCase);
    foreach (var v in scanA.VariantAssets)
    {
        if (string.IsNullOrWhiteSpace(v.File) || !File.Exists(v.File)) continue;
        // 两种场合资产都要登记：走路表也一样有"各家给这个名字的图有多高"（Characters/Magnus_Winter
        // 各家 480、所选包只有 192 —— 2026-09-29 法师那条漏网就是因为这里只看立绘）。
        var t = decMiA.Invoke(null, new object?[] { v.File });
        if (t is null) continue;
        var key = v.Kind + "/" + v.VariantId;
        if (!natSizes.TryGetValue(key, out var lst)) natSizes[key] = lst = new();
        lst.Add(((int)t.GetType().GetProperty("Width")!.GetValue(t)!,
            (int)t.GetType().GetProperty("Height")!.GetValue(t)!));
    }
    int bad = 0, rows = 0, missingOcc = 0, shortCover = 0;
    foreach (var ch in scanA.Characters.Where(c => !c.Hidden))
    {
        if (kwA.Length > 0 && !ch.Id.Contains(kwA, StringComparison.OrdinalIgnoreCase)
            && !ch.DisplayName.Contains(kwA, StringComparison.OrdinalIgnoreCase)) continue;
        // 锁定 = 用户主动要求"四季都是这一张" ⇒ 不能再拿按季表当期望（否则把设计判成 bug，
        // 2026-09-28 第一版对账把皮埃尔/奥利维亚的锁定误报成"四季抹平"缺陷）。
        var lkA = PortraitSkinService.GetLock(cfgA.Current, ch.Id);
        string? selPk = cfgA.Current.PortraitSkins.TryGetValue(ch.Id, out var sp) ? sp : null;
        var seasonPk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfgA.Current.PortraitSeasonSkins.TryGetValue(ch.Id, out var rawS))
            foreach (var seg in rawS.Split('␟'))
            {
                var i = seg.IndexOf(':');
                if (i > 0) seasonPk[seg[..i].Trim()] = seg[(i + 1)..].Trim();
            }
        if (selPk is null && seasonPk.Count == 0 && lkA is null) continue;
        PortraitSkinOption? Find(string? pk, string? forSeason)
        {
            if (pk is null) return null;
            if (pk.Length == 0) return ch.Vanilla ?? ch.Native;
            // ⚠ 必须和落盘用同一张卡：配置里记了画风（portraitSkinVariants）就得按画风取。
            //    第一版对账只按包名取第一条 ⇒ Miku 被当成"Official-like"判，而界面/落盘用的是
            //    他真选的 SD 那张 —— 对账自己造了个假 bug。
            var wantV = (string?)miVarA.Invoke(null, new object?[] { cfgA.Current, ch, forSeason });
            return (PortraitSkinOption?)miMatchA.Invoke(null,
                       new object?[] { ch.AllOptions, pk, wantV, ch.Id })
                   ?? ch.AllOptions.FirstOrDefault(o => string.Equals(o.PackFolder ?? "", pk,
                       StringComparison.OrdinalIgnoreCase));
        }
        var ids = LookIds(ch.Id);
        // 这张卡没身体、覆盖包却钉了身体时，允许的合法来源：本角色任何一张卡的身体 +
        // 【前置依赖包】给这个 NPC 配的当季身体（v1.7.13 拍板的"自己→前置包→不钉"链）。
        var anyBody = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddBodies(string? f)
        {
            if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) return;
            anyBody.Add(HA(f));
            foreach (var sfile in PortraitSkinService.GetSeasonFilesForChar(f, ch.Id).Values)
                anyBody.Add(HA(sfile));
        }
        foreach (var o in ch.AllOptions)
        {
            AddBodies(o.SpriteFile);
            AddBodies(PortraitSkinService.ResolvePrereqBody(scanA, ch, o)?.SpriteFile);
        }
        var line = new List<string>();
        var occReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 这张角色的走路表清单 = 基资产的「画布高」候选。落盘端现在把矮表【纵向拉伸铺满画布】
        // 再钉（CopyBody），所以对账核的是"钉出来的高度 == 画布高"，不再是"矮就不钉"。
        var sheetsA = PortraitSkinService.BodySheetsOf(ch);
        // 四季选的包【都有走路图】才要求把走路图的场合也钉满：自己带、或身体链（祖先前置→默认行）
        // 能补到一具都算有；整条链都补不到身体的，场合自然跟着不钉 ⇒ 不算漏。
        var allSeasonsHaveBody = seasonsA.All(s =>
        {
            var pk3 = lkA is not null ? (lkA.PackFolder.Length == 0 ? "" : lkA.PackFolder)
                : seasonPk.TryGetValue(s, out var sp3) ? sp3 : selPk;
            var o3 = pk3 is null ? null : Find(pk3, lkA is null ? s : null);
            return o3?.SpriteFile is { Length: > 0 }
                || PortraitSkinService.ResolveBody(cfgA.Current.GamePath, scanA, ch, o3!).File is { Length: > 0 };
        });
        Console.WriteLine($"◆ {ch.Id}「{ch.DisplayName}」全局={(selPk ?? "—")}" +
            (seasonPk.Count > 0 ? " 按季=" + string.Join(",", seasonPk.Select(kv => kv.Key + ":" + kv.Value)) : "") +
            (lkA is null ? "" : $" 〔锁定={(lkA.PackFolder.Length == 0 ? "默认行" : lkA.PackFolder)}" +
                $"·{lkA.Season ?? "四季"}〕"));
        foreach (var season in seasonsA)
        {
            var fromSeason = seasonPk.TryGetValue(season, out var sp2);
            var pk = lkA is not null ? (lkA.PackFolder.Length == 0 ? "" : lkA.PackFolder)
                : fromSeason ? sp2 : selPk;
            if (pk is null) continue;
            var opt = Find(pk, lkA is null && fromSeason ? season : null);
            if (opt is null)
            {
                bad++; line.Add($"{season}✗找不到卡");
                Console.WriteLine($"    [{season}] 肖像页指向的包在扫描结果里没有：{pk}（包被禁用/识别不到 ⇒ 游戏里必然还是原样）");
                continue;
            }
            var faceTbl = psA.SeasonFilesFor(scanA, opt.SourceFile, ch.Id, "Portraits", opt.PackFolder);
            var bodyTbl = psA.SeasonFilesFor(scanA, opt.SpriteFile, ch.Id, "Characters", opt.PackFolder);
            // v1.7.29：这张卡自己没身体时，落盘端会沿「祖先前置 → 默认行」补一档 ⇒ 对账必须用
            // 同一条链算期望值，否则这些格子只能标成"借来就算了"，等于没核。
            var (fbFile, fbPack) = PortraitSkinService.ResolveBody(
                cfgA.Current.GamePath, scanA, ch, opt);
            var fbTbl = fbFile is null ? new Dictionary<string, string>()
                : psA.SeasonFilesFor(scanA, fbFile, ch.Id, "Characters", fbPack);
            var wantP = lkA is not null && lkA.PinFile is { Length: > 0 } lpf && File.Exists(lpf) ? lpf
                : faceTbl.TryGetValue(season, out var fp) ? fp : opt.SourceFile;
            var wantS = lkA is not null ? (bodyTbl.TryGetValue(lkA.Season ?? season, out var ls) ? ls : opt.SpriteFile)
                : bodyTbl.TryGetValue(season, out var fs) ? fs : opt.SpriteFile;
            wantS ??= fbTbl.TryGetValue(lkA?.Season ?? season, out var fbs) ? fbs : fbFile;
            rows++;
            var (gotP, filesP, srcP) = Candidates("Portraits", ids, season);
            // 选的是 HD 通道的卡 ⇒ 脸本来就不走 Portraits/<角色>，核那条 EditData 指没指回本包资产
            if (opt.HdCell is > 0)
            {
                if (hdOwnedFace.Contains(ids[0])) line.Add($"{season}脸○HD");
                else
                {
                    bad++; line.Add($"{season}脸✗HD");
                    flagged.Add((ch.Id, season, "Portraits", ids[0]));
                    Console.WriteLine($"    [{season}] 脸 选的是 HD 卡（{opt.PackName}），但覆盖包里" +
                        $"没有把 Mods/HDPortraits/{ids[0]} 指回该包自己的高清资产");
                }
            }
            else if (gotP.Count == 0 || (!gotP.Contains(HA(wantP)) && !filesP.Any(fp => Covers(fp, wantP))))
            {
                bad++; line.Add($"{season}脸✗");
                flagged.Add((ch.Id, season, "Portraits", ids[0]));
                Console.WriteLine($"    [{season}] 脸 期望={HA(wantP)}（{Path.GetFileName(wantP ?? "—")}，卡={opt.PackName}）" +
                    $" 实际={(gotP.Count > 0 ? string.Join("|", gotP.Select(Short)) : "—")} 来源={srcP}");
            }
            else line.Add($"{season}脸○");
            rows++;
            var (gotC, filesC, srcC) = Candidates("Characters", ids, season);
            if (wantS is null)
            {
                if (gotC.Count == 0) line.Add($"{season}身—");
                else if (gotC.Any(anyBody.Contains)) line.Add($"{season}身借");
                else
                {
                    bad++; line.Add($"{season}身✗陌生");
                    flagged.Add((ch.Id, season, "Characters", ids[0]));
                    Console.WriteLine($"    [{season}] 身 这张卡没有身体，覆盖包却钉了一张不属于本角色任何卡的身体 → 走路小人被强行改掉（{srcC}）");
                }
            }
            else
            {
                if (gotC.Count == 0 || (!gotC.Contains(HA(wantS)) && !filesC.Any(fp => Covers(fp, wantS))))
                {
                    bad++; line.Add($"{season}身✗");
                    flagged.Add((ch.Id, season, "Characters", ids[0]));
                    Console.WriteLine($"    [{season}] 身 期望={HA(wantS)}（{Path.GetFileName(wantS)}，卡={opt.PackName}）" +
                        $" 实际={(gotC.Count > 0 ? string.Join("|", gotC.Select(Short)) : "—")} 来源={srcC}");
                }
                else line.Add($"{season}身○");
            }
            // 场合资产全覆盖：1.6 的 Appearance 按场合读的是【独立资产名】（Portraits/Wizard_Beach、
            // Portraits/Rasmodia_FlowerDance…，名字甚至可以不带角色 id）。漏钉一条 = 那一场合游戏
            // 读别人家的图，而按基资产的对账完全看不出来（卡罗琳四季不换、法师花舞节脸都是这一类）。
            foreach (var v in scanA.VariantAssets.Where(v =>
                         string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase)
                         // 走路图的场合【本来就没得选】时不算漏：卡没身体 ⇒ 落盘按设计不钉，
                         // 游戏里那一场合的身体本来就该由 mod 栈决定（v1.7.12 定的口径）。
                         && (v.Kind != "Characters" || allSeasonsHaveBody)
                         // 选的是 HD 卡 ⇒ 立绘场合本来就不走 Portraits/ 通道（那条 512 宽的表
                         // 由 HD 渲染端整张读，包自己按场合切资产），不能算漏钉。
                         && !(opt.HdCell is > 0 && v.Kind == "Portraits")))
            {
                if (!occReported.Add(v.Kind + "/" + v.VariantId)) continue;
                if (pinU.Any(p => string.Equals(p.kind + "/" + p.id, v.Kind + "/" + v.VariantId,
                        StringComparison.OrdinalIgnoreCase))
                    || pinW.Any(p => string.Equals(p.kind + "/" + p.id, v.Kind + "/" + v.VariantId,
                        StringComparison.OrdinalIgnoreCase))) continue;
                missingOcc++; line.Add("缺场合");
                Console.WriteLine($"    场合资产 {v.Kind}/{v.VariantId} 我们没钉 → 游戏这一场合读的是别人的图" +
                    $"（登记来自 {(v.Pack ?? "—")}）");
            }
        }
        // 覆盖不全检查：我们钉的图必须【盖满】同宽度下最高的那张底图，否则多出来的行仍是别的包的画
        //（2026-09-28 法师立绘：我方 128×256 vs 底图 128×320 ⇒ 第 5 行永远是 RomRas 的脸；
        //  2026-09-29 法师走路表：我方 64×192 vs 底图 64×480 ⇒ 下面 288 行一直是 SVE 的身子，
        //  而这条检查以前【只量立绘、不量身子】，所以一直报"覆盖不全 0 张"骗人）。
        int Hw(string? file, out int w)
        {
            w = 0;
            if (file is not { Length: > 0 } || !File.Exists(file)) return 0;
            var t = decMiA.Invoke(null, new object?[] { file });
            if (t is null) return 0;
            w = (int)t.GetType().GetProperty("Width")!.GetValue(t)!;
            return (int)t.GetType().GetProperty("Height")!.GetValue(t)!;
        }
        var tallestFaceByW = new Dictionary<int, int>();
        var tallestBodyByW = new Dictionary<int, int>();
        foreach (var o in ch.AllOptions)
        {
            if (o.SourceFile is not { Length: > 0 } faceSrc) continue;
            var fh = Hw(faceSrc, out var fw2);
            if (fh > tallestFaceByW.GetValueOrDefault(fw2)) tallestFaceByW[fw2] = fh;
            if (o.SpriteFile is not { Length: > 0 } bodySrc) continue;
            var bh = Hw(bodySrc, out var bw2);
            if (bh > tallestBodyByW.GetValueOrDefault(bw2)) tallestBodyByW[bw2] = bh;
        }
        void CoverCheck(string kind, Dictionary<int, int> tallest)
        {
            // 走路表【不再做"盖满"检查】：2026-09-29 墨迹剖面量出各家走路表一律按 32 像素行距
            // 铺画（Baechu 192=6 行、SCC 416=13 行、SCC-SVE 480 同），我原先"行高=高÷4、矮了会
            // 露出别家行"的前提不成立，为它做的拉伸实机把法师拉成一个长头。⇒ 走路表只按
            // "钉的是不是所选那具原图"核（上面 身○/身✗ 那一路），不比高度。
            if (kind != "Portraits") return;
            foreach (var (pk, pid, pfile) in pinU.Where(p => p.kind == kind)
                         .Select(p => (p.kind, p.id, p.file))
                         .Concat(pinW.Where(p => p.kind == kind).Select(p => (p.kind, p.id, p.file)))
                         .Where(p => ids.Any(i => p.id.Equals(i, StringComparison.OrdinalIgnoreCase)
                             || p.id.StartsWith(i + "_", StringComparison.OrdinalIgnoreCase))))
            {
                if (!occReported.Add("SIZE/" + pk + "/" + pid + "/" + Path.GetFileName(pfile))) continue;
                var pw = 0;
                var ph = Hw(pfile, out pw);
                if (ph == 0) continue;
                // 底图高度按【这条资产自己的登记】算：场合资产（Portraits/Wizard_Beach）的底图是
                // 各家给这个名字提供的图，跟同一个 NPC 其它资产的尺寸无关 —— 拿"整人最高的卡"比
                // 会把合法钉法误报成覆盖不全（2026-09-28 第一版就误报了 45 张）。
                var isBase = ids.Any(i => pid.Equals(i, StringComparison.OrdinalIgnoreCase));
                var need = 0;
                if (isBase) need = tallest.GetValueOrDefault(pw);
                else if (natSizes.TryGetValue(kind + "/" + pid, out var lst))
                    foreach (var (nw, nh) in lst) if (nw == pw && nh > need) need = nh;
                if (need == 0) continue;
                if (need > ph)
                {
                    shortCover++;
                    // 立绘：矮 = 多出来的行是别家的脸（补白没做到位）。
                    // 走路表：矮 = 行高是别人的 ⇒ 我们那四行被错位裁切，下面几行仍是别家身子。
                    // v1.7.32 起走路表应当被【纵向拉伸铺满】，还矮就是 CopyBody 没生效。
                    Console.WriteLine((kind == "Characters"
                        ? $"    走路表没铺满 {pid} 我方 {pw}×{ph} < 画布 {pw}×{need}" +
                          $" → 下面 {need - ph} 行仍是别的包的画，且行高是别人的（{Path.GetFileName(pfile)}）"
                        : $"    覆盖不全 {pid} 我方 {pw}×{ph} < 底图 {pw}×{need}" +
                          $" → 多出来的行还是别的包的画（{Path.GetFileName(pfile)}）"));
                }
            }
        }
        CoverCheck("Portraits", tallestFaceByW);
        CoverCheck("Characters", tallestBodyByW);
        Console.WriteLine("    " + string.Join(" ", line));
    }
    Console.WriteLine($"◆ 对账完成：核 {rows} 格（每格=一季×脸或身），不一致 {bad} 格，场合资产漏钉 {missingOcc} 条，覆盖不全 {shortCover} 张，落盘撞名 {collide} 处");

    // ② 钉对了但游戏里不是它 ⇒ 只有别人盖过来。列出【同一条资产上所有竞争者】及其优先级/条件，
    //    这把"谁赢"从猜变成读得出来的数字（Early=-100 / Normal=0 / Late=100 + 偏移；同分=加载顺序掷硬币）。
    if (flagged.Count > 0)
    {
        var wantAssets = flagged.Select(f => f.kind + "/" + f.assetFamily).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        int PriOf(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            if (int.TryParse(s, out var n)) return n;
            var basePri = s.StartsWith("Early", StringComparison.OrdinalIgnoreCase) ? -100
                : s.StartsWith("Late", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
            var i = s.IndexOfAny(new[] { '-', '+' });
            var rest = i < 0 ? "" : s[(i + 1)..].Trim();
            return basePri + (int.TryParse(rest, out var off) ? off : 0);
        }
        foreach (var cj in Directory.EnumerateFiles(modsDirA, "content.json", SearchOption.AllDirectories))
        {
            if (cj.StartsWith(ovDirA, StringComparison.OrdinalIgnoreCase)) continue;
            string raw;
            try { raw = File.ReadAllText(cj); } catch { continue; }
            if (!wantAssets.Any(a => raw.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;
            Newtonsoft.Json.Linq.JObject joC;
            try { joC = Newtonsoft.Json.Linq.JObject.Parse(raw); } catch { continue; }
            var rel = Path.GetRelativePath(modsDirA, Path.GetDirectoryName(cj)!);
            foreach (var c in joC["Changes"] ?? Enumerable.Empty<Newtonsoft.Json.Linq.JToken>())
            {
                var tgt = ((string?)c["Target"] ?? "").Trim();
                if (!wantAssets.Any(a => tgt.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;
                var pr = (string?)c["Priority"];
                var when = c["When"] is null ? "" : c["When"]!.ToString(Newtonsoft.Json.Formatting.None);
                when = when.Length > 60 ? when[..60] + "…" : when;
                Console.WriteLine($"  ⚔ {tgt} ← {rel}  Action={c["Action"]}  Pri={(string.IsNullOrWhiteSpace(pr) ? "Normal(0)" : $"{pr}({PriOf(pr)})")}  When={when}");
            }
        }
    }
    Environment.Exit(0);
}

// ═══════════════ 肖像候选明细（--dump-portraits <关键字>）═══════════════
// 界面上一张卡背后有四个闸门（立绘哈希 / 精灵表哈希 / config 键 / 是否选中项），
// 光看图猜不出为什么没并掉。这里把每个候选的来源、精灵表、config 键、画面哈希全打出来。
if (args.Contains("--dump-portraits"))
{
    var kwD = args.SkipWhile(a => a != "--dump-portraits").Skip(1).FirstOrDefault() ?? "";
    var cfgD = new ConfigService();
    var psD = new PortraitSkinService(new ModService(), cfgD);
    var scanD = psD.Scan(cfgD.Current.GamePath);
    var artMi = typeof(PortraitSkinService).GetMethod("ArtHash",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    string HashOf(string? p) => p is null ? "(无)"
        : (string?)artMi.Invoke(null, new object?[] { p }) ?? "(解不动)";
    foreach (var ch in scanD.Characters)
    {
        if (kwD.Length > 0 && !ch.Id.Contains(kwD, StringComparison.OrdinalIgnoreCase)
            && !ch.DisplayName.Contains(kwD, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"◆ {ch.Id} 「{ch.DisplayName}」" +
            (ch.Hidden ? $"〔已并入 {ch.AliasOf}，不上屏〕" : "") +
            (ch.Members.Count > 1 ? " 成员=" + string.Join("+", ch.Members) : "") + " 选中=" +
            (cfgD.Current.PortraitSkins.TryGetValue(ch.Id, out var sel) ? sel : "(未选))"));
        foreach (var e in scanD.BaseSeasonPatches.Where(e =>
                     e.Asset.Contains(kwD.Length > 0 ? kwD : ch.Id, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"    [分季表] {e.Pack} | {e.Asset} | {e.Season} ← {Path.GetFileName(e.File)}");
        // 诊断：选中那一行「按文件名找的分季表」和「包声明的分季表」各自是什么 ——
        // 卡罗琳线上就是前者返回了"四季同一张"的退化表，把兜底挡在外面。
        {
            var soD = ch.AllOptions.FirstOrDefault(o =>
                cfgD.Current.PortraitSkins.TryGetValue(ch.Id, out var s2)
                && string.Equals(o.PackFolder ?? "", s2, StringComparison.OrdinalIgnoreCase));
            if (soD?.SourceFile is not null)
            {
                var byNameD = PortraitSkinService.GetSeasonFilesForChar(soD.SourceFile, ch.Id);
                var bothD = psD.SeasonFilesFor(scanD, soD.SourceFile, ch.Id, "Portraits", soD.PackFolder);
                Console.WriteLine("    [按文件名] " + (byNameD.Count == 0 ? "空"
                    : string.Join(",", byNameD.Select(kv => kv.Key + ":" + Path.GetFileName(kv.Value)))));
                Console.WriteLine("    [最终用]   " + (bothD.Count == 0 ? "空"
                    : string.Join(",", bothD.Select(kv => kv.Key + ":" + Path.GetFileName(kv.Value)))));
            }
            // 弹窗右侧精灵窗走的是这条（razor 的 PickSeasonSprite）：类别 Characters + packFolder 传 null。
            // 打出来才能判断"切季节 tab 图不变"是数据没解析到，还是 UI 侧没接上。
            if (soD?.SpriteFile is not null)
            {
                var sprMap = psD.SeasonFilesFor(scanD, soD.SpriteFile, ch.Id, "Characters", soD.PackFolder);
                Console.WriteLine("    [精灵窗]   底图=" + Path.GetFileName(soD.SpriteFile) + " 分季="
                    + (sprMap.Count == 0 ? "空" : string.Join(",", sprMap.Select(kv => kv.Key + ":" + Path.GetFileName(kv.Value)))));
            }
        }
        foreach (var o in ch.AllOptions)
            Console.WriteLine($"    [{(o.IsVanilla ? "官方" : o.IsNative ? "默认" : "皮肤")}] " +
                $"{o.PackName}  folder={o.PackFolder}  HasSprite={o.HasSprite}" +
                (o.DefaultArtPack is { Length: > 0 } dap ? $"  默认行图来自={dap}" : "") +
                // v1.7.7：画风卡一眼能认出来（同包多画风时 PackFolder 是重复的）
                (o.Variant is { Length: > 0 } vv ? $"\n        画风={vv}  配置键={o.VariantConfigKey}" +
                    (cfgD.Current.PortraitSkinVariants.TryGetValue(ch.Id, out var curv)
                        && string.Equals(curv, vv, StringComparison.OrdinalIgnoreCase) ? "  ←当前生效" : "") : "") +
                $"\n        立绘={o.SourceFile}\n        立绘哈希={HashOf(o.SourceFile)}\n" +
                $"        精灵={o.SpriteFile ?? "(无)"}\n        精灵哈希={(o.SpriteFile is null ? "(无)" : HashOf(o.SpriteFile))}\n" +
                $"        configKeys=[{string.Join(",", o.ConfigKeys)}]");
        // v1.7.13：选中皮肤没精灵时，身体解析链落到哪一格（前置包 / 默认）直接打出来
        if (cfgD.Current.PortraitSkins.TryGetValue(ch.Id, out var selD))
        {
            var selOpt = ch.AllOptions.FirstOrDefault(o =>
                string.Equals(o.PackFolder, selD, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"    → 选中行精灵={(selOpt?.SpriteFile ?? "(无)")}  deps=["
                + string.Join(",", scanD.PackDeps.TryGetValue(selD, out var dd) ? dd : Array.Empty<string>())
                + "]  前置身体=" + (PortraitSkinService.ResolvePrereqBody(scanD, ch, selOpt)?.PackFolder ?? "(无→默认)"));
        }
        foreach (var (cid, msg) in scanD.Diagnostics.Where(d => d.Item1 == ch.Id))
            Console.WriteLine($"        诊断: {msg}");
    }
    // 同脸别名 NPC 一眼看清（立绘页卡片上那行「与 X 同一张脸」就是这张表）
    var aliasD = PortraitSkinService.AliasMap(scanD.Characters);
    Console.WriteLine($"◆ 尚未并走的同脸别名 {aliasD.Count} 条（正常应为 0，合并后别名卡不再参与判定）：" +
        string.Join("、", aliasD.Select(kv => kv.Key + "→" + kv.Value)));
    var mergedD = scanD.Characters.Where(c => c.Members.Count > 1).ToList();
    Console.WriteLine($"◆ 合并卡 {mergedD.Count} 张：" + string.Join("、", mergedD.Select(c =>
        c.Id + "〔" + string.Join("+", c.Members) + "〕页签:" + string.Join("/", c.TabKeys))));
    Console.WriteLine($"◆ 上屏卡 {scanD.Characters.Count(c => !c.Hidden)} / 全部条目 {scanD.Characters.Count}");
    Environment.Exit(0);
}

// ═══════════════ 已装 mod 的 Nexus 关联与依赖明细（--dump-mods <关键字>）═══════════════
// 「详情页说没装」「列表报缺少依赖」这类判定同时吃 manifest 的 UpdateKeys、安装边车、
// 依赖数组的 IsRequired 写法、config 安装快照四处数据，光看界面猜不出哪一处断了。
if (args.Contains("--dump-mods"))
{
    var kwM = args.SkipWhile(a => a != "--dump-mods").Skip(1).FirstOrDefault() ?? "";
    var cfgM = new ConfigService();
    var gpM = cfgM.Current.GamePath;
    var listM = new ModService().Scan(gpM);
    Console.WriteLine($"◆ GamePath={gpM}  扫描条目={listM.Count}");
    var uidsM = new HashSet<string>(listM.Where(x => !string.IsNullOrWhiteSpace(x.UniqueID))
        .Select(x => x.UniqueID.Trim()), StringComparer.OrdinalIgnoreCase);
    foreach (var m in listM)
    {
        if (kwM.Length > 0 && !m.Folder.Contains(kwM, StringComparison.OrdinalIgnoreCase)
            && !(m.Name ?? "").Contains(kwM, StringComparison.OrdinalIgnoreCase)
            && !(m.UniqueID ?? "").Contains(kwM, StringComparison.OrdinalIgnoreCase)) continue;
        var dirM = Path.Combine(gpM, "Mods", m.Folder.Replace('/', Path.DirectorySeparatorChar));
        var mfM = Path.Combine(dirM, "manifest.json");
        string ukRaw = "(无 manifest)  depsRaw=(无)", sideM = "(无边车)";
        try
        {
            if (File.Exists(mfM))
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(mfM));
                ukRaw = (jo["UpdateKeys"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "(无)");
                sideM = File.Exists(Path.Combine(dirM, ".junigrid.json"))
                    ? File.ReadAllText(Path.Combine(dirM, ".junigrid.json")).Trim() : "(无边车)";
                if (jo["Dependencies"] is Newtonsoft.Json.Linq.JArray depArr)
                {
                    var sb = new StringBuilder();
                    foreach (var it in depArr.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        sb.Append(it["UniqueID"]?.ToString() ?? "?").Append(" ← IsRequired ");
                        var tk = it["IsRequired"] ?? it["Required"];
                        if (tk is null) { sb.Append("(无)"); }
                        else
                        {
                            sb.Append(tk.Type).Append('=')
                              .Append(tk.ToString());
                            try { sb.Append(" → (bool)=").Append((bool)tk); }
                            catch (Exception cex) { sb.Append(" → (bool)抛错=").Append(cex.GetType().Name); }
                        }
                        sb.Append("   ");
                    }
                    ukRaw += "  ||  depsRaw: " + sb;
                }
            }
        }
        catch (Exception ex) { ukRaw = "manifest 解析失败: " + ex.Message; }
        var recM = NexusUpdateTruth.GetInstallRecord(cfgM.Current, m);
        var missM = m.Dependencies.Concat(m.ContentPackIds)
            .Where(d => !string.IsNullOrWhiteSpace(d) && !uidsM.Contains(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"◆ {m.Folder} 「{m.Name}」 v{m.Version} uid={m.UniqueID} " +
            $"NexusModId={(m.NexusModId?.ToString() ?? "null")} 禁用={m.Disabled}");
        Console.WriteLine($"    UpdateKeys={ukRaw}  边车={sideM}");
        Console.WriteLine($"    必需依赖=[{string.Join("、", m.Dependencies)}] 宿主=[{string.Join("、", m.ContentPackIds)}] " +
            $"→ 判缺失=[{string.Join("、", missM)}]");
        var missOptM = m.OptionalDependencies
            .Where(d => !string.IsNullOrWhiteSpace(d) && !uidsM.Contains(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"    可选依赖=[{string.Join("、", m.OptionalDependencies)}] → 未装提示=[{string.Join("、", missOptM)}]");
        Console.WriteLine($"    安装快照=" + (recM is null ? "(无)"
            : $"N{recM.NexusModId}/file{recM.FileId}/v{recM.RemoteVersion}"));
    }
    Environment.Exit(0);
}

// ═══════════════ 全部皮肤的精灵图体检（--audit-sprites）═══════════════
// 用户报：有的皮肤右侧渲染出来的根本不是走路表（立绘被当精灵用），
// 规则要的是「皮肤自己的真精灵 → 原皮精灵 → 都没有就空白」。先把现状数清楚。
if (args.Contains("--audit-sprites"))
{
    var cfgA = new ConfigService();
    var gpA = cfgA.Current.GamePath;
    var scanA = new PortraitSkinService(new ModService(), cfgA).Scan(gpA);
    void PngSizeA(string? p, out int w, out int h)
    {
        w = h = 0;
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) return;
        if (p.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) { w = h = -1; return; }   // xnb 不解析
        try
        {
            var all = File.ReadAllBytes(p);
            var b = all[..Math.Min(24, (int)all.Length)];
            if (b.Length >= 24) { w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19]; h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]; }
        }
        catch { }
    }
    int nOpt = 0, nNoSprite = 0, nSameAsPortrait = 0, nTooWide = 0, nSingleCell = 0, nRealSheet = 0, nXnb = 0, nShort = 0;
    var bad = new List<string>();
    foreach (var ch in scanA.Characters.Where(c => !c.Hidden))
    {
        // 这个角色的"原皮"到底有没有真精灵 —— 决定回落后是空白还是有图
        var nat = ch.AllOptions.FirstOrDefault(o => o.IsNative) ?? ch.AllOptions.FirstOrDefault(o => o.IsVanilla);
        PngSizeA(nat?.SpriteFile, out var natW, out var natH);
        var natHasReal = nat?.SpriteFile is { Length: > 0 }
            && (natW == -1 || (natW <= 64 && natH >= 128));
        foreach (var o in ch.Skins)
        {
            nOpt++;
            PngSizeA(o.SpriteFile, out var sw, out var sh);
            PngSizeA(o.SourceFile, out var pw, out var ph);
            var tag = $"{ch.Id}「{ch.DisplayName}」{(o.Variant is { Length: > 0 } v ? $"·{v}" : "")} ← {o.PackName}";
            if (o.SpriteFile is null or { Length: 0 }) { nNoSprite++; continue; }
            var sameFile = string.Equals(o.SpriteFile, o.SourceFile, StringComparison.OrdinalIgnoreCase);
            if (sw == -1) { nXnb++; continue; }
            // 1.6 村民走路表：宽 64（每帧 16）× 高 ≥128（4 方向 × 32 行）。
            // 64×64 只有 2 行 = 摆不出走路动画 ⇒ 那是单格立绘，不是精灵表。
            if (sameFile) { nSameAsPortrait++; bad.Add($"[精灵=立绘同一文件{(natHasReal ? "，可回落原皮" : "，原皮也没有→该空白")}] {tag}  {sw}x{sh}"); }
            else if (sw > 64) { nTooWide++; bad.Add($"[精灵宽 {sw} 不是走路表] {tag}  ← {Path.GetFileName(o.SpriteFile)}"); }
            else if (sh < 128)
            {
                if (sh == sw) { nSingleCell++; bad.Add($"[64×64 单格当精灵{(natHasReal ? "，可回落原皮" : "，原皮也没有→该空白")}] {tag}  ← {Path.GetFileName(o.SpriteFile)}"); }
                else { nShort++; bad.Add($"[精灵只有 {sw}x{sh}（不足 4 行）] {tag}  ← {Path.GetFileName(o.SpriteFile)}"); }
            }
            else nRealSheet++;
            _ = pw; _ = ph;
        }
    }
    Console.WriteLine($"◆ 皮肤卡共 {nOpt} 张：无精灵={nNoSprite} 真走路表={nRealSheet} xnb={nXnb} " +
        $"可疑={nSameAsPortrait + nTooWide + nSingleCell + nShort}（其中 精灵=立绘同文件={nSameAsPortrait} " +
        $"宽>64={nTooWide} 64×64单格={nSingleCell} 不足4行={nShort}）");
    foreach (var b in bad.Take(60)) Console.WriteLine("   " + b);
    if (bad.Count > 60) Console.WriteLine($"   …还有 {bad.Count - 60} 条");
    Environment.Exit(0);
}

// ═══════════════ 转换包自愈预演（--heal-check [游戏目录]）═══════════════
// 把实机上我们自己转出来的包（UID = JuniGrid.PortraitPack.*）逐个【拷进临时沙箱】跑一遍自愈，
// 打印目标名的增删 —— 只读实机、不写实机。改转换器/自愈规则后必须跑一轮：
// 验收线是"只有真生病的包被改，其余一个都不动"（带 Priority 的老包、平铺多角色的包都不能碰）。
if (args.Contains("--heal-check"))
{
    var cfgH = new ConfigService();
    var argH = args.SkipWhile(a => a != "--heal-check").Skip(1).FirstOrDefault();
    var gpH = argH is { Length: > 0 } a0 && !a0.StartsWith("--") ? a0 : cfgH.Current.GamePath ?? "";
    var modsH = Path.Combine(gpH, "Mods");
    if (!Directory.Exists(modsH)) { Console.WriteLine("没有 Mods 目录: " + modsH); Environment.Exit(1); }

    var sandH = Path.Combine(Path.GetTempPath(), "jg-healcheck-" + DateTime.Now.ToString("MMdd-HHmmss"));
    Directory.CreateDirectory(Path.Combine(sandH, "Mods"));
    static void CopyTreeH(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyTreeH(d, Path.Combine(dst, Path.GetFileName(d)));
    }
    var namesH = new List<string>();
    foreach (var dir in Directory.EnumerateDirectories(modsH))
    {
        var mfH = Path.Combine(dir, "manifest.json");
        if (!File.Exists(mfH)) continue;
        if (!File.ReadAllText(mfH).Contains("JuniGrid.PortraitPack.", StringComparison.OrdinalIgnoreCase)) continue;
        namesH.Add(Path.GetFileName(dir));
        CopyTreeH(dir, Path.Combine(sandH, "Mods", Path.GetFileName(dir)));
    }

    static List<string> TargetsH(string packDir)
    {
        var p = Path.Combine(packDir, "content.json");
        var outp = new List<string>();
        if (!File.Exists(p)) return outp;
        try
        {
            var jo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(p));
            foreach (var c in jo["Changes"] ?? new Newtonsoft.Json.Linq.JArray())
            {
                var t = (string?)c["Target"] ?? "";
                var w = c["When"]?["Season"]?.ToString();
                foreach (var one in t.Split(','))
                    outp.Add(one.Trim() + (w is null ? "" : "  [季:" + w + "]"));
            }
        }
        catch { outp.Add("(content.json 解析失败)"); }
        return outp;
    }

    var beforeH = namesH.ToDictionary(n => n, n => TargetsH(Path.Combine(sandH, "Mods", n)));
    var healedH = ModService.HealConvertedPortraitPacks(sandH);
    Console.WriteLine($"◆ 转换包 {namesH.Count} 个，自愈改写了 {healedH} 个（沙箱 {sandH}，实机未动）");
    foreach (var n in namesH)
    {
        var after = TargetsH(Path.Combine(sandH, "Mods", n));
        var b = beforeH[n];
        var gone = b.Where(x => !after.Contains(x)).ToList();
        var added = after.Where(x => !b.Contains(x)).ToList();
        var changed = gone.Count > 0 || added.Count > 0;
        Console.WriteLine($"  [{(changed ? "改写" : "不动")}] {n}  补丁 {b.Count} → {after.Count}"
            + $"  标记={(File.Exists(Path.Combine(sandH, "Mods", n, ModService.ConvertFormatFile))
                ? File.ReadAllText(Path.Combine(sandH, "Mods", n, ModService.ConvertFormatFile)).Trim() : "(无)")}");
        foreach (var g in gone) Console.WriteLine("      - " + g);
        foreach (var a in added) Console.WriteLine("      + " + a);
    }
    Environment.Exit(0);
}

// ── 接线冒烟：自愈必须挂在 PortraitSkinService.Scan 上 ──
// 「打开立绘页」是用户唯一的触发点，而 B43 那组直接调 ModService.HealConvertedPortraitPacks，
// 绕过了这条线 —— 实测把 Scan 里那句调用注释掉，B43 仍然 72 全绿。所以这条线单独测。
// 必须独立进程跑：Scan 的自愈是每进程一次（Interlocked 闸门），别的用例已经先扫过一轮。
if (args.Contains("--heal-wiring"))
{
    static void FakePng(string p, int w, int h)
    {
        static byte[] Be(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        var b = new List<byte>();
        b.AddRange(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        b.AddRange(Be(13));
        b.AddRange(System.Text.Encoding.ASCII.GetBytes("IHDR"));
        b.AddRange(Be(w)); b.AddRange(Be(h));
        b.AddRange(new byte[] { 8, 6, 0, 0, 0 });
        b.AddRange(new byte[4]);   // CRC 占位：我们只读 IHDR 里的宽高
        File.WriteAllBytes(p, b.ToArray());
    }
    var sandW = Path.Combine(Path.GetTempPath(), "jg-wiring-" + DateTime.Now.ToString("MMdd-HHmmss"));
    var packW = Path.Combine(sandW, "Mods", "JGWire Broken");
    Directory.CreateDirectory(Path.Combine(packW, "assets", "Sprites"));
    FakePng(Path.Combine(packW, "assets", "Sprites", "Abigail_Vanilla.png"), 64, 416);
    FakePng(Path.Combine(packW, "assets", "Sprites", "Spring.png"), 64, 416);
    File.WriteAllText(Path.Combine(packW, "manifest.json"),
        """{"Name":"JGWire Broken","UniqueID":"JuniGrid.PortraitPack.WIRE000001","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(packW, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditImage","Target":"Characters/Abigail_Vanilla","FromFile":"assets/Sprites/Abigail_Vanilla.png","PatchMode":"Replace"},{"Action":"EditImage","Target":"Characters/Spring","FromFile":"assets/Sprites/Spring.png","PatchMode":"Replace"}]}""");

    var psW = new PortraitSkinService(new ModService(), new ConfigService());
    psW.Scan(sandW);                       // 只做"进立绘页"这一个动作，不直接碰 ModService
    var txtW = File.ReadAllText(Path.Combine(packW, "content.json"));
    var mkW = Path.Combine(packW, ModService.ConvertFormatFile);
    var okW = txtW.Contains("\"Characters/Abigail\"", StringComparison.OrdinalIgnoreCase)
        && !txtW.Contains("Characters/Spring", StringComparison.OrdinalIgnoreCase)
        && File.Exists(mkW)
        && File.ReadAllText(mkW).Trim() == ModService.ConvertFormat.ToString();
    Console.WriteLine((okW ? "PASS" : "FAIL")
        + "  W1 只调 Scan（模拟进立绘页）就改写坏转换包并打标记"
        + "  -- 含真资产=" + txtW.Contains("\"Characters/Abigail\"")
        + " ‖ 残留假目标=" + txtW.Contains("Characters/Spring")
        + " ‖ 标记=" + (File.Exists(mkW) ? File.ReadAllText(mkW).Trim() : "(没写)")
        + " ‖ 留底=" + File.Exists(Path.Combine(packW, "content.json.bak-junigrid")));
    var stableW = true;
    for (var iW = 0; iW < 2; iW++) psW.Scan(sandW);   // 同进程再扫两次：每进程一次的闸门不该重复改写
    stableW = File.ReadAllText(Path.Combine(packW, "content.json")) == txtW;
    Console.WriteLine((stableW ? "PASS" : "FAIL") + "  W2 同进程内后续扫描不重复改写");
    try { Directory.Delete(sandW, true); } catch { }
    Environment.Exit(okW && stableW ? 0 : 1);
}

// ═══════════════ 肖像包识别对账（--audit-packs）═══════════════
// 把每个包 content.json 里**自己声明**的 Portraits/* 补丁独立解析一遍（不借 PortraitSkinService
// 的解析器 —— 自己考自己考不出东西），再和 Scan 的产出对账：哪个包声明了却没出卡、缺的那条
// 到底卡在哪。改完扫描语义必须跑这个，**缺口数当验收线**，别靠肉眼看界面。
if (args.Contains("--audit-packs"))
{
    var cfgP = new ConfigService();
    var gpP = cfgP.Current.GamePath ?? "";
    var modsP = Path.Combine(gpP, "Mods");
    if (!Directory.Exists(modsP)) { Console.WriteLine("没有 Mods 目录: " + modsP); Environment.Exit(1); }
    var psP = new PortraitSkinService(new ModService(), cfgP);
    var scanP = psP.Scan(gpP);
    var onPage = new HashSet<string>(scanP.Characters.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

    static string NormRel(string rel) => string.Join('/', rel.Replace('\\', '/').Split('/')
        .Select(seg => seg.TrimStart('.')));
    static string StripComments(string s)
    {
        var sb = new StringBuilder(); bool inStr = false, esc = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr) { sb.Append(c); if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') { inStr = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            sb.Append(c);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @",(\s*[}\]])", "$1");
    }
    static Newtonsoft.Json.Linq.JObject? LoadJson(string path)
    {
        try
        {
            var raw = File.ReadAllText(path);
            try { return Newtonsoft.Json.Linq.JObject.Parse(raw); }
            catch { return Newtonsoft.Json.Linq.JObject.Parse(StripComments(raw)); }
        }
        catch { return null; }
    }
    // CP 的 Target 可以逗号分隔多目标："Portraits/A, Characters/A"
    static List<string> SplitTargets(string t)
    {
        var outp = new List<string>(); var depth = 0; var cur = new StringBuilder();
        foreach (var c in t)
        {
            if (c == '{') depth++; else if (c == '}') depth--;
            if (c == ',' && depth == 0) { outp.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(c);
        }
        if (cur.Length > 0) outp.Add(cur.ToString().Trim());
        return outp.Where(x => x.Length > 0).ToList();
    }

    // 已知角色（页面上的条目）：变体资产名要先归到本体再对账 —— abigail_flowerdance
    // 是阿比盖尔的节日差分，不是第 45 个角色；不归并就会数出几百个假缺口。
    var knownIds = new HashSet<string>(scanP.Characters.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
    string BaseOf(string stem)
    {
        var parts = stem.Split('_');
        for (var i = parts.Length - 1; i > 0; i--)
            if (knownIds.Contains(string.Join("_", parts.Take(i))))
                return string.Join("_", parts.Take(i));
        return parts[0];
    }
    string CanonAlias(string id) => id.Equals("parrotboy", StringComparison.OrdinalIgnoreCase) ? "leo"
        : id.Equals("gilsprite", StringComparison.OrdinalIgnoreCase) ? "gil" : id;

    var seasons = new[] { "Spring", "Summer", "Fall", "Winter" };
    var declared = new Dictionary<string, Dictionary<string, (bool AnyFile, bool AnyCond, List<string> Cands)>>(
        StringComparer.OrdinalIgnoreCase);          // 包 → 角色裸名 → 证据
    var packCondKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var badPacks = new List<string>();

    foreach (var mf in Directory.GetFiles(modsP, "manifest.json", SearchOption.AllDirectories))
    {
        var root = Path.GetDirectoryName(mf)!;
        var content = Path.Combine(root, "content.json");
        if (!File.Exists(content)) continue;
        var rawRel = Path.GetRelativePath(modsP, root).Replace('\\', '/');
        var rel = NormRel(rawRel);
        // 回收站 / 禁用包（点号开头）不参与对账 —— 产品本来就不扫它们
        if (rel.StartsWith("~JuniGrid Portrait Overrides")
            || rawRel.Split('/').Any(seg => seg.StartsWith('.'))) continue;
        var man = LoadJson(mf);
        if (!string.Equals(man?["ContentPackFor"]?["UniqueID"]?.ToString(), "Pathoschild.ContentPatcher",
                StringComparison.OrdinalIgnoreCase)) continue;
        var uid = man?["UniqueID"]?.ToString() ?? "";
        var cfgDoc = LoadJson(Path.Combine(root, "config.json")) ?? new Newtonsoft.Json.Linq.JObject();
        var tbl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cfgDoc.Properties()) tbl[kv.Name] = kv.Value.ToString().Trim();
        tbl["ModId"] = uid;
        var schema = new Dictionary<string, (List<string> Vals, string Def)>(StringComparer.OrdinalIgnoreCase);
        var dts = new List<(string Name, string Val, string K, string V)>();
        var changes = new List<(Newtonsoft.Json.Linq.JObject c, string file)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string cf)
        {
            var full = Path.GetFullPath(cf);
            if (!seen.Add(full) || !File.Exists(full)) return;
            var d = LoadJson(full);
            if (d is null) { badPacks.Add(rel + " ← " + Path.GetFileName(cf)); return; }
            if (d["ConfigSchema"] is Newtonsoft.Json.Linq.JObject sc)
                foreach (var p in sc.Properties())
                {
                    var av = p.Value?["AllowValues"]?.ToString() ?? "";
                    var vs = av.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (vs.Count > 0) schema[p.Name] = (vs, p.Value?["Default"]?.ToString()?.Trim() ?? "");
                }
            if (d["DynamicTokens"] is Newtonsoft.Json.Linq.JArray dtsA)
                foreach (var t in dtsA.OfType<Newtonsoft.Json.Linq.JObject>())
                {
                    var nm = t["Name"]?.ToString()?.Trim() ?? "";
                    var vv = t["Value"]?.ToString()?.Trim() ?? "";
                    if (nm.Length == 0) continue;
                    var w = t["When"] as Newtonsoft.Json.Linq.JObject;
                    if (w is null) { dts.Add((nm, vv, "", "")); continue; }
                    foreach (var p in w.Properties()) dts.Add((nm, vv, p.Name.Trim(), p.Value.ToString().Trim()));
                }
            foreach (var c in (d["Changes"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                         .OfType<Newtonsoft.Json.Linq.JObject>())
            {
                var act = c["Action"]?.ToString() ?? "Load";
                if (act.Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    // ⚠ CP 的 Include 路径写在 FromFile（Donut's 实测），老写法才放 Include 字段
                    var inc = c["FromFile"]?.ToString() ?? c["Include"]?.ToString() ?? "";
                    foreach (var part in inc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        Walk(Path.Combine(root, part.Replace('/', Path.DirectorySeparatorChar)));
                    continue;
                }
                changes.Add((c, Path.GetFileName(cf)));
            }
        }
        Walk(content);

        string Sub(string s)
        {
            for (var round = 0; round < 6 && s.Contains("{"); round++)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\{\{\s*([^{}]+?)\s*\}\}", m =>
                {
                    var tk = m.Groups[1].Value.Trim();
                    if (tk.Equals("Season", StringComparison.OrdinalIgnoreCase)) return m.Value;
                    if (tbl.TryGetValue(tk, out var v))
                        return v.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || v.Equals("false", StringComparison.OrdinalIgnoreCase) ? m.Value : v;
                    if (schema.TryGetValue(tk, out var so))
                        return so.Def.Length == 0 || so.Def.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || so.Def.Equals("false", StringComparison.OrdinalIgnoreCase) ? m.Value : so.Def;
                    foreach (var d in dts)
                    {
                        if (!d.Name.Equals(tk, StringComparison.OrdinalIgnoreCase)) continue;
                        if (d.K.Length == 0) return d.Val;
                        // 探针只认"单条件、键是本包开关"的 DynamicToken；多条件/运行时条件算解不出
                        var cur2 = tbl.TryGetValue(d.K, out var cv) ? cv : (schema.TryGetValue(d.K, out var s2) ? s2.Def : "");
                        if (cur2.Equals(d.V, StringComparison.OrdinalIgnoreCase)) return d.Val;
                    }
                    return m.Value;
                });
            return s;
        }

        var per = new Dictionary<string, (bool AnyFile, bool AnyCond, List<string> Cands)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (c, file) in changes)
        {
            var act = c["Action"]?.ToString() ?? "Load";
            if (!act.Equals("Load", StringComparison.OrdinalIgnoreCase)
                && !act.Equals("EditImage", StringComparison.OrdinalIgnoreCase)) continue;
            var tgt = (c["Target"]?.ToString() ?? "").Replace('\\', '/');
            var ff = (c["FromFile"]?.ToString() ?? "").Replace('\\', '/');
            foreach (var one in SplitTargets(tgt))
            {
                if (!one.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase)) continue;
                var baseTok = Sub(one["Portraits/".Length..]);
                foreach (var asset in seasons.Select(sn => baseTok.Contains("{Season}",
                             StringComparison.OrdinalIgnoreCase)
                             ? baseTok.Replace("{Season}", sn, StringComparison.OrdinalIgnoreCase) : baseTok).Distinct())
                {
                    var stem = System.Text.RegularExpressions.Regex.Replace(asset,
                        @"_(Spring|Summer|Fall|Winter)$", "", RegexOptions.IgnoreCase);
                    if (stem.Length == 0) continue;
                    var hasCond = c["When"] is Newtonsoft.Json.Linq.JObject w2 && w2.Count > 0;
                    var candList = new List<string>();
                    var anyFile = false;
                    if (ff.Length > 0 && !asset.Contains("{"))
                    {
                        var t2 = new Dictionary<string, string>(tbl, StringComparer.OrdinalIgnoreCase)
                        { ["TargetName"] = stem, ["TargetWithoutPath"] = stem, ["Target"] = asset };
                        var probe = System.Text.RegularExpressions.Regex.Replace(
                            ff.Replace("{Season}", "Spring", StringComparison.OrdinalIgnoreCase),
                            @"\{\{\s*([^{}]+?)\s*\}\}", m => t2.TryGetValue(m.Groups[1].Value.Trim(), out var r) ? r : m.Value);
                        probe = Sub(probe);
                        if (!probe.Contains("{"))
                        {
                            var abs = Path.GetFullPath(Path.Combine(root, probe.Replace('/', Path.DirectorySeparatorChar)));
                            anyFile = File.Exists(abs);
                            candList.Add(Path.GetRelativePath(modsP, abs).Replace('\\', '/') + (anyFile ? "" : " (缺)"));
                        }
                        else candList.Add("探针解不出: " + probe);
                    }
                    else candList.Add(asset.Contains("{") ? "目标 token 解不出" : "无 FromFile");;
                    var key = CanonAlias(BaseOf(stem)).ToLowerInvariant();
                    if (!per.TryGetValue(key, out var old)) per[key] = (anyFile, hasCond, candList);
                    else per[key] = (old.AnyFile || anyFile, old.AnyCond || hasCond, old.Cands);
                }
            }
        }
        if (per.Count > 0) declared[rel] = per;
        packCondKinds[rel] = scanP.UnknownConditions
            .Where(x => NormRel(x.Pack).Equals(rel, StringComparison.OrdinalIgnoreCase)).Select(x => x.Cond).Distinct().Count();
    }

    // Scan 的产出：包 → 出了哪些角色（哈希去重已删，每张皮肤都单列，不再折叠进 Dupes）
    var produced = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    void AddOut(string folder, string charId)
    {
        folder = NormRel(folder);
        if (folder.Length == 0) return;
        if (!produced.TryGetValue(folder, out var s)) produced[folder] = s = new(StringComparer.OrdinalIgnoreCase);
        s.Add(charId);
    }
    foreach (var ch in scanP.Characters)
        foreach (var o in ch.AllOptions)
            AddOut(o.PackFolder ?? "", ch.Id);
    // 官方/默认行的 PackFolder 是空串（SVE 的法师脸就是"默认行"），按包名归不到账 ⇒
    // 会把它误报成"无门控却没出卡的真缺陷"。按文件真实所在目录补记一次。
    foreach (var ch in scanP.Characters)
        foreach (var f in new[] { ch.Vanilla?.SourceFile, ch.Native?.SourceFile })
        {
            if (string.IsNullOrEmpty(f)) continue;
            var norm = f.Replace('\\', '/');
            foreach (var pack0 in declared.Keys)
                if (norm.Contains(modsP.Replace('\\', '/') + pack0 + "/", StringComparison.OrdinalIgnoreCase))
                    AddOut(pack0, ch.Id);
        }

    Console.WriteLine($"◆ 已声明立绘补丁的内容包 {declared.Count} 个" +
        $"（探针解析失败 {badPacks.Count}：{string.Join("、", badPacks.Take(3))}）");
    Console.WriteLine($"◆ 页面角色条目 {scanP.Characters.Count}，上屏 {scanP.Characters.Count(c => !c.Hidden)}，" +
        $"判不了的条件 {scanP.UnknownConditions.Select(x => x.Pack + "|" + x.Cond).Distinct().Count()} 类");
    var tot = 0; var totNoChar = 0; var totManual = 0;
    foreach (var (pack, per) in declared.OrderByDescending(x => x.Value.Keys.Except(
                 produced.TryGetValue(x.Key, out var ps0) ? ps0 : new HashSet<string>()).Count()))
    {
        var got = produced.TryGetValue(pack, out var gotSet) ? gotSet : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = per.Keys.Where(k => !got.Contains(k) && !got.Contains(CanonAlias(k))).ToList();
        if (missing.Count == 0) continue;
        tot += missing.Count;
        totNoChar += missing.Count(m => !onPage.Contains(m) && !onPage.Contains(CanonAlias(m)));
        Console.WriteLine($"  ⚠ {pack[..Math.Min(pack.Length, 46)]}  声明 {per.Count} 出卡 {per.Count - missing.Count} 缺 {missing.Count}" +
            $"（该包判不了条件 {(packCondKinds.TryGetValue(pack, out var ck) ? ck : 0)} 类）");
        foreach (var m in missing.Take(10))
        {
            var e = per[m];
            var why = !onPage.Contains(m) && !onPage.Contains(CanonAlias(m)) ? "角色没上屏"
                : !e.AnyFile ? e.Cands.FirstOrDefault() ?? "文件不存在"
                : e.AnyCond ? "有运行时条件（探针也不知道用哪条）" : "探针没归上账 ← 人工看";
            if (why.StartsWith("探针没归上账")) totManual++;
            Console.WriteLine($"       · {m,-22} {why}");
        }
        if (missing.Count > 10) Console.WriteLine($"       …另外 {missing.Count - 10} 个");
    }
    Console.WriteLine($"◆ 合计缺 {tot}：角色整个没上屏 {totNoChar}（事件演员/商店界面，按设计不出卡）" +
        $"，探针没归上账 {totManual}（人工看），其余是作者侧（文件缺、笔误、运行时条件）");
    Environment.Exit(0);
}

var realMode = args.Contains("--real");

var checkOnly = args.Contains("--check-portraits");
var checkSeasons = args.Contains("--check-seasons");
var thumbTest = args.FirstOrDefault(a => a.StartsWith("--thumb:"));
var results = new List<(string Name, bool Pass, string Detail)>();
int pass = 0, fail = 0;
void Check(string name, bool pass_, string detail = "")
{
    results.Add((name, pass_, detail));
    if (pass_) pass++; else fail++;
    Console.WriteLine((pass_ ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "   -- " + detail : ""));
}
void Note(string name, string detail = "")
    => Console.WriteLine("NOTE  " + name + (detail.Length > 0 ? "   -- " + detail : ""));

// 只读诊断：拿真实存档目录跑一遍「云存档下到一半」判定（不改任何东西）
if (args.Contains("--half-sync"))
{
    var bad = SaveVersionService.HalfSyncedSaves();
    Console.WriteLine("存档目录 = " + (SaveVersionService.SavesDir() ?? "(没有)"));
    Console.WriteLine("判成半同步的档数 = " + bad.Count);
    foreach (var b in bad) Console.WriteLine("  · " + b);
    if (bad.Count > 0) Console.WriteLine(SaveVersionService.DescribeHalfSynced(bad));
    var cfgHs = new ConfigService().Current;
    var cloudHs = SteamService.ReadCloudSyncState(cfgHs.GamePath, cfgHs.SteamAppId);
    Console.WriteLine($"Steam 云存档 = {cloudHs}（判据：userdata\\<账号>\\7\\remote\\sharedconfig.vdf 的 "
                      + $"apps/{cfgHs.SteamAppId}/cloudenabled，读不到才退回 cloud_log）");
    Console.WriteLine("收档闸门 = " + (bad.Count > 0 ? "拦（半同步现场，不给出口）" : "不拦，可以收档 —— 云开着也照收"));
    Environment.Exit(0);
}


// ── 合成 PNG 夹具 ──
// 肖像管线按「宽高均为 64 的倍数」校验（1.6 可变高度肖像表），1x1 会被当无效资产筛掉，
// 所以这里现造一张 64x64 的合法 PNG，而不是内联一段 1x1 字节。
static uint Crc32(byte[] data)
{
    uint c = 0xFFFFFFFF;
    foreach (var b in data)
    {
        c ^= b;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
    }
    return c ^ 0xFFFFFFFF;
}
static byte[] Be(int v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); return b; }
static byte[] PngChunk(string type, byte[] data)
{
    var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
    return Be(data.Length).Concat(body).Concat(Be((int)Crc32(body))).ToArray();
}
static byte[] BuildPng(int w, int h, int changed = 0)
{
    var raw = new byte[h * (1 + w * 4)];
    for (var y = 0; y < h; y++)
    {
        var o = y * (1 + w * 4);
        for (var x = 0; x < w; x++)
        {
            var p = o + 1 + x * 4;
            raw[p] = (byte)((x * 4) & 0xFF); raw[p + 1] = (byte)((y * 4) & 0xFF);
            raw[p + 2] = 0x80; raw[p + 3] = 0xFF;
        }
    }
    // 前 changed 个像素改蓝通道：造「同一张画被重导了一遍、只有零星像素不同」的近似样本
    for (var i = 0; i < changed && i < w * h; i++)
        raw[1 + i * 4 + 2] = 0x7F;
    using var ms = new MemoryStream();
    ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    var ihdr = Be(w).Concat(Be(h)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();
    ms.Write(PngChunk("IHDR", ihdr));
    using (var z = new MemoryStream())
    {
        using (var zl = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true))
            zl.Write(raw, 0, raw.Length);
        ms.Write(PngChunk("IDAT", z.ToArray()));
    }
    ms.Write(PngChunk("IEND", Array.Empty<byte>()));
    return ms.ToArray();
}
byte[] TinyPng = BuildPng(64, 64);

// ═══════════════ 缩略图生成单点测试（--thumb:<png路径>） ═══════════════
if (thumbTest is not null)
{
    var path0 = thumbTest["--thumb:".Length..];
    var ps1 = new PortraitSkinService(new ModService(), new ConfigService());
    Console.WriteLine($"文件: {path0}");
    Console.WriteLine("存在: " + File.Exists(path0));
    var t = Stopwatch.StartNew();
    var uri = ps1.GetPortraitThumbByPath(path0);
    Console.WriteLine($"耗时 {t.ElapsedMilliseconds} ms | dataURI 长度: {(uri is null ? "NULL" : uri.Length)}");
    if (uri is not null) Console.WriteLine("前 60 字: " + uri[..Math.Min(60, uri.Length)]);
    Environment.Exit(0);
}

// ═══════════════ 季节映射全量审计（--check-seasons） ═══════════════
if (checkSeasons)
{
    Console.WriteLine("────── 季节映射审计（全部角色 × 全部启用包） ──────");
    var ps2 = new PortraitSkinService(new ModService(), new ConfigService());
    var gamePath0 = new ConfigService().Current.GamePath;
    var sw3 = Stopwatch.StartNew();
    var scanS = ps2.Scan(gamePath0);
    Console.WriteLine($"扫描 {sw3.ElapsedMilliseconds} ms，角色 {scanS.Characters.Count}");

    var seasons = new[] { "spring", "summer", "fall", "winter" };
    int optsChecked = 0, optsWithSeason = 0, mismatches = 0;
    var packProblems = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    void AddProblem(string pack, string msg)
    {
        mismatches++;
        if (!packProblems.TryGetValue(pack, out var l)) packProblems[pack] = l = new();
        if (l.Count < 4) l.Add(msg);
    }

    foreach (var ch in scanS.Characters)
    {
        var idLow = ch.Id.ToLowerInvariant();
        foreach (var o in ch.AllOptions)
        {
            if (o.IsVanilla || o.SourceFile is null || !File.Exists(o.SourceFile)) continue;
            if (o.SourceFile.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) continue;
            optsChecked++;
            var dir = Path.GetDirectoryName(o.SourceFile)!;

            // 磁盘真值：该目录里属于此角色的季节文件（含 Winter Indoor/Outdoor 拆分）
            var disk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? baseFile = null;
            foreach (var f in Directory.GetFiles(dir, "*.png"))
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                var low = stem.ToLowerInvariant();
                if (low == idLow) { baseFile ??= f; continue; }
                if (!low.StartsWith(idLow + "_")) continue;
                var suf = low[(idLow.Length + 1)..];
                if (suf == "spring") disk["spring"] = f;
                else if (suf == "summer") disk["summer"] = f;
                else if (suf == "fall") disk["fall"] = f;
                // 冬季：裸 _winter 与 Indoor/Outdoor 拆分算冬季；_winter_2 这类
                // 编号变体是独立变体资产（由变体整族钉住负责），不进季节桶
                else if (suf == "winter" || suf.StartsWith("winter_indoor")
                         || suf.StartsWith("winter_outdoor")) disk["winter"] = f;
            }

            // 程序映射（与弹窗预览/覆盖包同一条管线）
            var mapped = PortraitSkinService.GetSeasonFilesForChar(o.SourceFile, ch.Id);

            foreach (var s in seasons)
            {
                var hasDisk = disk.TryGetValue(s, out var dFile);
                var hasMap = mapped.TryGetValue(s, out var mFile);
                // hasMap && !hasDisk：映射到的文件确实存在（可能是子文件夹布局，
                // 磁盘真值只扫了同级目录）→ 不算问题
                if (hasDisk && !hasMap)
                    AddProblem(o.PackFolder, $"[{ch.Id}/{s}] 磁盘有 {Path.GetFileName(dFile!)} 但映射未收集（该季会显示基础图）");
                else if (hasDisk && hasMap
                    && !string.Equals(dFile, mFile, StringComparison.OrdinalIgnoreCase)
                    && !s.Equals("winter", StringComparison.OrdinalIgnoreCase))
                    AddProblem(o.PackFolder, $"[{ch.Id}/{s}] 映射与磁盘不一致: {Path.GetFileName(mFile!)} vs {Path.GetFileName(dFile!)}");
            }
            // 不同季节映射到同一张图（= 游戏里季节不轮换，Baechu 报告的同款问题）
            var distinct = mapped.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (mapped.Count >= 2 && distinct < mapped.Count)
                AddProblem(o.PackFolder, $"[{ch.Id}] {mapped.Count} 个季节映射到同一张图（季节不轮换）");
            if (mapped.Count > 0) optsWithSeason++;
        }
    }

    Console.WriteLine($"检查选项 {optsChecked} 个，带季节映射 {optsWithSeason} 个，发现疑似问题 {mismatches} 处");
    foreach (var (pack, list) in packProblems)
    {
        Console.WriteLine($"▸ {pack}");
        foreach (var m in list) Console.WriteLine("   ⚠ " + m);
    }
    Environment.Exit(0);
}

// ═══════════════ NPC 肖像全量体检（--check-portraits） ═══════════════
if (checkOnly)
{
    var cfgSvc0 = new ConfigService();
    var gamePath0 = cfgSvc0.Current.GamePath;
    var ps0 = new PortraitSkinService(new ModService(), cfgSvc0);

    var swCold = Stopwatch.StartNew();
    var scan = ps0.Scan(gamePath0);
    var coldMs = swCold.ElapsedMilliseconds;
    var swWarm = Stopwatch.StartNew();
    ps0.Scan(gamePath0);
    var warmMs = swWarm.ElapsedMilliseconds;
    Console.WriteLine($"扫描：冷 {coldMs} ms / 热 {warmMs} ms，角色 {scan.Characters.Count}");

    int files = 0, ok = 0, xnb = 0, missing = 0, undecodable = 0, blank = 0, aspect = 0;
    var problems = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var ch in scan.Characters)
    {
        foreach (var o in ch.AllOptions)
        {
            var src = o.SourceFile;
            if (src is null || !seen.Add(src)) continue;
            files++;
            if (!File.Exists(src)) { missing++; problems.Add($"[{ch.Id}] 文件缺失: {src}"); continue; }
            if (src.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) { xnb++; continue; }
            try
            {
                var tex = PixelKit.DecodePng(src);
                if (tex is null) { undecodable++; problems.Add($"[{ch.Id}] 无法解码: {src}"); continue; }
                var blankPx = true;
                for (var i = 3; i < tex.PixelsRgba.Length; i += 4)
                    if (tex.PixelsRgba[i] > 16) { blankPx = false; break; }
                if (blankPx) { blank++; problems.Add($"[{ch.Id}] 全透明/空白: {src}"); continue; }
                // SDV 1.6 支持可变高度肖像表：合法尺寸 = 宽高均为 64 的倍数且 ≥64
                //（128x320=2×5 格、64x64 单帧都合法 —— Nyapu/Baechu/SVE 实测全部如此）
                if (tex.Width < 64 || tex.Height < 64 || tex.Width % 64 != 0 || tex.Height % 64 != 0)
                { aspect++; problems.Add($"[{ch.Id}] 尺寸非法 {tex.Width}x{tex.Height}（宽高应为 64 的倍数）: {src}"); continue; }
                ok++;
            }
            catch (Exception ex) { undecodable++; problems.Add($"[{ch.Id}] 解码异常 {src}: {ex.Message}"); }
        }
    }
    Console.WriteLine($"体检文件 {files} 个（原版 xnb {xnb} 跳过）：正常 {ok}，缺失 {missing}，无法解码 {undecodable}，空白 {blank}，宽高比异常 {aspect}");
    foreach (var p in problems.Take(40)) Console.WriteLine("  ⚠ " + p);
    if (problems.Count > 40) Console.WriteLine($"  …另有 {problems.Count - 40} 条");
    Environment.Exit(0);
}

var cfgSvc = new ConfigService();
var cfg = cfgSvc.Current;
var gamePath = cfg.GamePath;
var depot = new DepotDownloaderService();

// 测试会写真实配置文件（肖像选择、托管包清单等）→ 开跑前整份备份，退出时必还原。
// 旧版清理块是无条件 Remove("Abigail")/Remove("Emily")，会把用户真实的肖像选择抹掉。
var cfgFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "JuniGrid", "junigrid.config.json");
byte[] cfgBefore = File.Exists(cfgFile) ? File.ReadAllBytes(cfgFile) : Array.Empty<byte>();
// 个人数据一并备份：playtime.json / tasks.json 不归测试管，测完必须原样还原
var personalFiles = new[] {
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid", "playtime.json"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid", "playtime.json.bak"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid", "tasks.json"),
};
var personalBefore = personalFiles.ToDictionary(
    f => f,
    f => File.Exists(f) ? File.ReadAllBytes(f) : Array.Empty<byte>());

bool ConfigIsPristine()
{
    try
    {
        var now = File.ReadAllBytes(cfgFile);
        return now.Length == cfgBefore.Length && now.AsSpan().SequenceEqual(cfgBefore);
    }
    catch { return false; }
}

// ConfigService.Save() 是 dirty 标记 + 250ms 防抖的后台写盘（v0.72.6 持久化协调器）：
// 直接覆盖文件会被随后落盘的脏快照盖掉 —— 实测 Emily 的选择和 JGTest Emily Pack
// 就是这么留在用户配置里的。所以先等防抖跑完，再写回，再复验，必要时重试。
void RestoreConfig()
{
    if (cfgBefore.Length == 0) return;
    for (var attempt = 1; attempt <= 4 && !ConfigIsPristine(); attempt++)
    {
        try
        {
            Thread.Sleep(400);
            File.WriteAllBytes(cfgFile, cfgBefore);
        }
        catch (Exception ex) { Console.WriteLine("WARN  配置还原失败，请手工核对 " + cfgFile + " : " + ex.Message); return; }
    }
    // 个人数据（游玩时长 / 任务）同步还原 —— 不许测试把它清零
    foreach (var (path, bytes) in personalBefore)
    {
        try
        {
            if (bytes.Length > 0) File.WriteAllBytes(path, bytes);
            else if (File.Exists(path)) File.Delete(path);   // 开跑前就没有 → 不留测试垃圾
        }
        catch (Exception ex) { Console.WriteLine("WARN  个人数据还原失败 " + path + " : " + ex.Message); }
    }
}
AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreConfig();
AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreConfig();   // 用例抛了也要还原（实测崩过一次留下临时 cacheRoot）

string BodyVersion()
{
    var dll = Path.Combine(gamePath, "Stardew Valley.dll");
    var p = File.Exists(dll) ? dll : Path.Combine(gamePath, "Stardew Valley.exe");
    return File.Exists(p) ? FileVersionInfo.GetVersionInfo(p).FileVersion ?? "?" : "(无本体)";
}
int CountFiles(string d) => Directory.Exists(d) ? Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length : -1;
// 与应用 TryReadGameVersion 同语义：3 段版本号才能匹配 staging 目录
string Ver3() { var p = BodyVersion().Split('.'); return p.Length >= 3 ? string.Join('.', p.Take(3)) : BodyVersion(); }

var staged = depot.ListStagedPackages();
string? FindPackDir(string name) =>
    new[] { gamePath }.Concat(staged.Select(s => s.Path))
        .Select(p => Path.Combine(p, "Mods", name))
        .FirstOrDefault(Directory.Exists);

void CopyTree(string src, string destMods, string name)
{
    var d = Path.Combine(destMods, name);
    foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(src, f);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(d, rel))!);
        File.Copy(f, Path.Combine(d, rel), true);
    }
}

// ═══════════════ U. 白盒纯函数单测（--unit-only，不碰磁盘/网络/进程） ═══════════════
if (unitOnly)
{
    Console.WriteLine("\n────── U. 白盒纯函数单测 ──────");
    Check("U1 版本比较：同号/预发布/四段/前缀 v 都不误报更新",
        !JuniGrid.Services.VersionUtil.IsNewer("1.6.4", "1.6.4")
        && !JuniGrid.Services.VersionUtil.IsNewer("1.6.4-beta", "1.6.4")
        && !JuniGrid.Services.VersionUtil.IsNewer("1.6.4.0", "1.6.4")
        && !JuniGrid.Services.VersionUtil.IsNewer("v1.6.4", "1.6.4")
        && JuniGrid.Services.VersionUtil.IsNewer("1.6.7", "1.6.4")
        && JuniGrid.Services.VersionUtil.IsNewer("1.10", "1.9")
        && JuniGrid.Services.VersionUtil.IsNewer("2.0", "1.99.99"),
        "1.6.4=1.6.4 不更新 ✓ · 1.6.7>1.6.4 ✓ · 1.10>1.9 ✓");

    Check("U2 日志分类：级别标签优先于内容启发式",
        LogLineClassifier.Classify("[12:00:00 ERROR SMAPI] update check failed") == "err"
        && LogLineClassifier.Classify("[12:00:00 INFO  SMAPI] 1.6 update by MLD") == "info"
        && LogLineClassifier.Classify("[12:00:00 ALERT SMAPI] Mod 1.0.41: https://n (you have 1.0.39)") == "upd"
        && LogLineClassifier.Classify("[JuniGrid] > help") == "sys"
        && LogLineClassifier.Classify("System.NullReferenceException: x") == "err"
        && LogLineClassifier.Classify("   at Game1.Update() in D:\\a.cs:line 1") == "err",
        "ERROR 盖过 update ✓ · INFO 不误判 ✓ · 异常首行/栈帧归 err ✓");

    Check("U3 XNA / SMAPI 适配判定边界",
        XnaRedistService.GameNeedsXna("1.0.5900") && XnaRedistService.GameNeedsXna("1.4")
        && !XnaRedistService.GameNeedsXna("1.5") && !XnaRedistService.GameNeedsXna(null)
        && UpdateService.IsSmapiUnsupported("1.0") && UpdateService.IsSmapiUnsupported("1.01")
        && !UpdateService.IsSmapiUnsupported("1.2.30") && !UpdateService.IsSmapiUnsupported(null),
        "1.0–1.4 要 XNA ✓ · 1.5+ 不要 ✓ · 仅 1.0/1.01 无 SMAPI ✓");

    // 安装器路径穿越（白盒打 SafePath 私有方法）
    var safeMi = typeof(JuniGridInstaller.InstallerEngine).GetMethod("SafePath",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    string? Safe(string root, string rel) => (string?)safeMi.Invoke(null, new object?[] { root, rel });
    var rootU = Path.Combine(Path.GetTempPath(), "jg-unit-safe");
    Directory.CreateDirectory(rootU);
    string? pOk = null, pUp = null, pAbs = null, pDot = null;
    try
    {
        pOk = Safe(rootU, "tools/DepotDownloader/DepotDownloader.exe");
        pDot = Safe(rootU, "./JuniGrid.exe");
        try { pUp = Safe(rootU, "../evil.exe"); } catch (TargetInvocationException ex) { pUp = "THROW:" + (ex.InnerException?.GetType().Name ?? ex.GetType().Name); }
        try { pAbs = Safe(rootU, "C:/Windows/System32/evil.dll"); } catch (TargetInvocationException ex) { pAbs = "THROW:" + (ex.InnerException?.GetType().Name ?? ex.GetType().Name); }
    }
    finally { try { Directory.Delete(rootU, true); } catch { } }
    Check("U4 安装器路径穿越：正常相对路径放行，../ 与绝对路径拒绝",
        pOk is not null && pOk.EndsWith("DepotDownloader.exe")
        && pDot is not null && pDot.EndsWith("JuniGrid.exe")
        && pUp is not null && pUp.StartsWith("THROW:")
        && pAbs is null or "THROW:IOException" or "THROW:InvalidOperationException",
        "ok=" + pOk + " ‖ ./=" + pDot + " ‖ ../=" + pUp + " ‖ abs=" + pAbs);

    Check("U5 版本号与发行一致：AppInfo.Version = csproj Version（Nexus AUP 头同源）",
        AppInfo.Version == "1.2.3",
        "AppInfo.Version=" + AppInfo.Version + "（期望 1.2.3，由 JuniGrid.csproj <Version> 注入；升版本时这里要一起改）");

    // 回归：旧 PascalCase 配置不得在 CamelCase 策略下被静默读成空（封面/合集清零事故）
    {
        var optsIns = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        var optsStrict = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var pascal = "{\"ModCovers\":{\"ModA\":\"http://cover\"},\"ModProfiles\":[{\"Name\":\"合集A\",\"EnabledModUids\":[\"uid1\"]}]}";
        var camel = "{\"modCovers\":{\"ModA\":\"http://cover\"},\"modProfiles\":[{\"Name\":\"合集A\",\"EnabledModUids\":[\"uid1\"]}]}";
        var a = System.Text.Json.JsonSerializer.Deserialize<JuniGridConfig>(pascal, optsIns)!;
        var b = System.Text.Json.JsonSerializer.Deserialize<JuniGridConfig>(camel, optsIns)!;
        var lost = System.Text.Json.JsonSerializer.Deserialize<JuniGridConfig>(pascal, optsStrict)!;
        Check("U6 配置反序列化兼容 PascalCase（旧文件不得静默丢封面/合集）",
            a.ModCovers.Count == 1 && a.ModProfiles.Count == 1
            && b.ModCovers.Count == 1 && b.ModProfiles.Count == 1
            && lost.ModCovers.Count == 0,   // 钉住事故根因：不加 ignore-case 就是 0
            $"ignore-case Pascal covers={a.ModCovers.Count}/profiles={a.ModProfiles.Count}"
            + " ‖ camel covers=" + b.ModCovers.Count
            + " ‖ 严格模式 Pascal covers=" + lost.ModCovers.Count + "（应为 0=旧 bug）");
    }

    // ── U7：manifest 依赖的 IsRequired 写法（2026-09-26 用户报「非必须依赖报得强硬」）──
    // 真实现场：[CP] Miku Mod Plus 的 manifest 把可选依赖写成字符串 "false"，
    // 旧代码只认布尔 token ⇒ 可选依赖被当成硬缺失，Mods 行刷一条红字「缺少依赖：ChaseXavier.Miku」。
    {
        var isReqMi = typeof(ModService).GetMethod("DepIsRequired",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        bool ReqOf(string inner) => (bool)isReqMi.Invoke(null,
            new object[] { Newtonsoft.Json.Linq.JObject.Parse("{" + inner + "}") })!;
        Check("U7 依赖 IsRequired：布尔/字符串/数字/旧字段名/大小写都要落到 SMAPI 同一口径",
            ReqOf("\"UniqueID\":\"a.b\"")                                   // 没写 → 必需
            && ReqOf("\"UniqueID\":\"a.b\",\"IsRequired\":true")            // 布尔 true
            && !ReqOf("\"UniqueID\":\"a.b\",\"IsRequired\":false")          // 布尔 false
            && !ReqOf("\"UniqueID\":\"a.b\",\"IsRequired\":\"false\"")      // 字符串 "false" ← 现场那种
            && ReqOf("\"UniqueID\":\"a.b\",\"IsRequired\":\"true\"")
            && !ReqOf("\"UniqueID\":\"a.b\",\"isrequired\":false")          // 键名大小写不一
            && !ReqOf("\"UniqueID\":\"a.b\",\"Required\":false")            // 旧字段名
            && !ReqOf("\"UniqueID\":\"a.b\",\"IsRequired\":0")              // 数字 0
            && ReqOf("\"UniqueID\":\"a.b\",\"IsRequired\":\"maybe\""),      // 读不懂 → 按必需（宁可多报）
            "缺省/true/\"true\" → 必需 · false/\"false\"/0/小写键/Required → 可选 · \"maybe\" → 必需");

        // ── U8：整条扫描链路（BuildModEntry）——必需/可选分家 + UpdateKeys 取 N 网 id ──
        var uDir = Path.Combine(Path.GetTempPath(), "jg-deps-test");
        try
        {
            Directory.CreateDirectory(uDir);
            var uMods = Path.Combine(uDir, "Mods");
            void WriteManifest(string folder, string json)
            {
                var d = Path.Combine(uMods, folder);
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "manifest.json"), json);
            }
            // 逐字抄用户机器上 E:\Steam\...\Mods\[CP] Miku Mod Plus\manifest.json
            WriteManifest("[CP] Miku Mod Plus", """
            {
              "Name": "[CP] Miku Mod Plus",
              "Author": "Tikamin557",
              "Version": "2.4",
              "Description": "add [CP] Miku Mod Plus. (The original creator was ChaseXavier.)",
              "UniqueID": "Tikamin557.CP.MikuModPlus",
              "UpdateKeys": [ "Nexus:29201" ],
              "ContentPackFor": { "UniqueID": "Pathoschild.ContentPatcher" },
              "Dependencies": [
                { "UniqueID": "spacechase0.GenericModConfigMenu" },
                { "UniqueID": "CF.FarmhouseFixes" },
                { "UniqueID": "ChaseXavier.Miku", "IsRequired": "false" },
                { "UniqueID": "FlashShifter.StardewValleyExpandedCP", "IsRequired": "false" }
              ]
            }
            """);
            // 另一条现场：Gotoubun Miku 的 UpdateKeys 冒号后带空格（详情页「已装」判定吃这个 id）
            WriteManifest("[CP] Gotoubun Miku", """
            {
              "Name": "Gotoubun Miku", "Author": "lucasedu11", "Version": "3.1.0",
              "UniqueID": "lucasedu11.Miku",
              "UpdateKeys": [ "Nexus: 4291" ],
              "ContentPackFor": { "UniqueID": "Pathoschild.ContentPatcher" }
            }
            """);
            var uScan = new ModService().Scan(uDir);
            var plus = uScan.FirstOrDefault(x => x.UniqueID == "Tikamin557.CP.MikuModPlus");
            var gotu = uScan.FirstOrDefault(x => x.UniqueID == "lucasedu11.Miku");
            Check("U8 扫描分家：字符串 \"false\" 的依赖进 OptionalDependencies，不污染必需清单",
                plus is not null
                && string.Join(",", plus.Dependencies) == "spacechase0.GenericModConfigMenu,CF.FarmhouseFixes"
                && string.Join(",", plus.OptionalDependencies)
                     == "ChaseXavier.Miku,FlashShifter.StardewValleyExpandedCP",
                "必需=[" + (plus is null ? "(条目没扫出来)" : string.Join("、", plus.Dependencies)) + "] "
                + "可选=[" + (plus is null ? "" : string.Join("、", plus.OptionalDependencies)) + "]");
            Check("U8b UpdateKeys 带空格（\"Nexus: 4291\"）也要取出 N 网 id —— 详情页「已装」全靠它",
                gotu is not null && gotu.NexusModId == 4291,
                "Gotoubun Miku NexusModId=" + (gotu?.NexusModId?.ToString() ?? "(null)"));
        }
        finally { try { Directory.Delete(uDir, true); } catch { } }
    }

    // ── U9：详情页 Requirements 的「可选」识别（官网只有 notes 文字可依据）──
    {
        var optMi = typeof(JuniGrid.Components.Pages.ModDetail).GetMethod("IsReqOptional",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        bool OptOf(string notes) => (bool)optMi.Invoke(null, new object[] {
            new JuniGrid.Services.NexusRequirementEx("X", notes, "", 1, false, null) })!;
        Check("U9 官网需求：notes 以 Optional 开头 → 浅色「可选未装」，其余仍按红标「未安装」",
            OptOf("Optional") && OptOf(" optional if you want")
            && !OptOf("Required.") && !OptOf("") && !OptOf(null)
            // 实测 29201 的那条是"This is necessary if you are using an options file."
            // —— 条件式说法，不认成可选（宁可多提示）
            && !OptOf("This is necessary if you are using an options file."),
            "\"Optional\"/\" optional…\" → 可选 · \"Required.\"/空/条件式长句 → 必需");
    }

    // ── U10：依赖候选按「作者亲和度」排序（2026-09-26 实测错认：ChaseXavier.Miku → 别人的 4291）──
    {
        int Aff(string uid, string? up) => InstallService.AuthorAffinity(uid, up);
        Check("U10 作者亲和度：相等 3 / 包含 2 / 首字母缩写 1 / 无关 0 / 无作者段 0",
            Aff("ChaseXavier.Miku", "ChaseXavier") == 3
            && Aff("Pathoschild.ContentPatcher", "Pathoschild") == 3
            && Aff("Wildflour.AtelierGoods", "Wildflourmods") == 2
            && Aff("CF.FarmhouseFixes", "CyanFire") == 1
            && Aff("ChaseXavier.Miku", "lucasedu11") == 0
            && Aff("ChaseXavier.Miku", "tikamin557") == 0
            && Aff("ChaseXavier.Miku", null) == 0
            && Aff("a.b", "ChaseXavier") == 0        // 作者段太短，不拿来判
            && Aff("NoDotInIt", "ChaseXavier") == 0, // 没有作者段
            "ChaseXavier↔ChaseXavier=3 · Wildflour↔Wildflourmods=2 · CF↔CyanFire=1 · lucasedu11=0");

        // 真机现场：搜 "Miku" 按下载数返回 4291(22077) → 29201(11192) → 4373(11151)，
        // 旧写法取第一个 ⇒ 把 lucasedu11 的皮肤包当成 ChaseXavier.Miku 指给用户装。
        var mikuHits = new List<NexusModListEntry>
        {
            new(4291, "Miku skin for Abigail", "", "3.1.0", 22077, "", null, "lucasedu11"),
            new(29201, "Miku Mod Plus", "", "2.4", 11192, "", null, "tikamin557"),
            new(4373, "CP_Miku NPC", "", "1.0", 11151, "", null, "ChaseXavier"),
        };
        var rankedU10 = InstallService.RankByAuthor("ChaseXavier.Miku", mikuHits);
        Check("U10b 候选排序：作者对上的那条（4373 CP_Miku NPC）排到第一，下载数高的不再赢",
            rankedU10[0].Id == 4373 && rankedU10.Count == 3,
            "排序后=" + string.Join(" → ", rankedU10.Select(h => $"{h.Id}({h.Uploader})")));
    }

    // U11：扫描结果进磁盘快照会走一次 Newtonsoft 序列化往返（SaveScanCache/TryLoadScanCache）。
    // v1.7.13 的前置身体解析依赖 PackDeps/PackFolderByUid —— 若把它们标 JsonIgnore，快照命中后
    // 这两个字典恒空，写盘路径（常在命中时跑）就永远解析不出前置身体（实测法师身体没钉）。
    // 这条用例复现那次往返，钉住"依赖图必须活过缓存"。
    {
        var src = new PortraitScanResult();
        src.PackFolderByUid["Nom0ri.RomRas"] = "[CP] Romanceable Rasmodia";
        src.PackDeps["[CP] Romanceable Rasmodia - RRRR Patch"] =
            new List<string> { "Nom0ri.RomRas", "Jellonip.RRRR" };
        var tok = Newtonsoft.Json.Linq.JToken.FromObject(src);
        var back = tok.ToObject<PortraitScanResult>(new Newtonsoft.Json.JsonSerializer
        {
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver(),
            TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto,
            ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
        })!;
        // 大小写不敏感比较器也要活过往返（只读字典属性：Newtonsoft 取回已初始化实例再填充）
        var folderOk = back.PackFolderByUid.TryGetValue("nom0ri.romras", out var f)
            && f == "[CP] Romanceable Rasmodia";
        var depsOk = back.PackDeps.TryGetValue("[cp] romanceable rasmodia - rrrr patch", out var dl)
            && dl.Count == 2 && dl[0] == "Nom0ri.RomRas";
        Check("U11 前置依赖图必须活过扫描快照的序列化往返（否则缓存命中后前置身体恒解析不出）",
            folderOk && depsOk,
            $"PackFolderByUid={back.PackFolderByUid.Count} PackDeps={back.PackDeps.Count} 大小写不敏感={(folderOk && depsOk)}");
    }

    Console.WriteLine($"\n════════ 总计：{pass} PASS / {fail} FAIL ════════");
    Environment.Exit(fail == 0 ? 0 : 1);
}

if (!portraitOnly && !smapiOnly && !guardOnly && !qrOnly && !savesOnly && !unitOnly && !realSwitch)
    Note("A 段跳过：它真实切换游戏目录，必须显式加 --real-switch（09-22 02:26 误跑过一次）");
if (!portraitOnly && !smapiOnly && !guardOnly && !qrOnly && !savesOnly && realSwitch)
{
    // ═══════════════ A. 版本管理（真实环境） ═══════════════
    Console.WriteLine("\n────── A. 版本管理 ──────");
    var expect = new Dictionary<string, (string Body, string SmapiExe)>
    {
        ["1.6.15"] = ("1.6.15", "4.5"),
        ["1.4"]    = ("1.3.7269", "3.7.3"),
        ["1.2.30"] = ("1.2.6338", "2.5"),
        ["1.0"]    = ("1.0.5900", ""),
    };

    void CheckSmapi(string label)
    {
        var smapi = Path.Combine(gamePath, "StardewModdingAPI.exe");
        var (body, smapiExe) = expect[label];
        if (smapiExe.Length == 0)
        {
            Check($"A* {label} 无适配 SMAPI → 不装加载器", !File.Exists(smapi));
            return;
        }
        if (!File.Exists(smapi)) { Check($"A* {label} SMAPI 文件集", false, "StardewModdingAPI.exe 不存在"); return; }
        var fv = FileVersionInfo.GetVersionInfo(smapi).FileVersion ?? "";
        switch (label)
        {
            case "1.6.15":
            {
                var ok = fv.StartsWith("4.5")
                    && File.Exists(Path.Combine(gamePath, "StardewModdingAPI.dll"))
                    && File.Exists(Path.Combine(gamePath, "StardewModdingAPI.deps.json"))
                    && File.Exists(Path.Combine(gamePath, "StardewModdingAPI.runtimeconfig.json"))
                    && Directory.Exists(Path.Combine(gamePath, "smapi-internal"));
                Check("A* 1.6.15 SMAPI 4.5.2 文件集完整", ok, "exe=" + fv);
                break;
            }
            case "1.4":
            {
                var nj = Path.Combine(gamePath, "smapi-internal", "Newtonsoft.Json.dll");
                var ok = fv.StartsWith("3.7.3")
                    && File.Exists(Path.Combine(gamePath, "StardewModdingAPI.exe.config"))
                    && File.Exists(nj)
                    && File.Exists(Path.Combine(gamePath, "steam_appid.txt"))
                    && !File.Exists(Path.Combine(gamePath, "Newtonsoft.Json.dll"));
                Check("A* 1.4 SMAPI 3.7.3 文件集完整（依赖在 smapi-internal）", ok, "exe=" + fv);
                break;
            }
            case "1.2.30":
            {
                var nj = Path.Combine(gamePath, "Newtonsoft.Json.dll");
                var njOk = false;
                try { njOk = File.Exists(nj) && System.Reflection.AssemblyName.GetAssemblyName(nj).Version.Major == 11; }
                catch { }
                var ok = fv.StartsWith("2.5") && njOk
                    && File.Exists(Path.Combine(gamePath, "StardewModdingAPI.AssemblyRewriters.dll"))
                    && File.Exists(Path.Combine(gamePath, "StardewModdingAPI.config.json"))
                    && !Directory.Exists(Path.Combine(gamePath, "smapi-internal"));
                Check("A* 1.2.30 SMAPI 2.5 文件集完整（顶层 Newtonsoft 11）——最初崩溃场景回归", ok,
                    "exe=" + fv + " nj=" + (File.Exists(nj) ? "有" : "无"));
                break;
            }
        }
    }

    // 「Mods 随版本走」的账本：记下每次切走时该版本在 gamePath/Mods 里的文件数，
    // 供切回来时对比（版本隔离的本义：出去多少、回来还是多少）。
    var modsAtLeave = new Dictionary<string, int>();
    var currentLabel = "1.6.15";

    void SwitchTo(string label)
    {
        var sp = staged.FirstOrDefault(s => s.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        if (sp is null) { Check($"A* 切换到 {label}", false, "staging 里没有这个版本"); return; }
        var modsBefore = CountFiles(Path.Combine(gamePath, "Mods"));
        modsAtLeave[currentLabel] = Math.Max(modsBefore, 0);
        // 快照计数必须在切换「之前」取：切换本身可能把整棵 Mods 搬空
        var snapDir = Path.Combine(sp.Path, "Mods");
        var hasSnap = Directory.Exists(snapDir);
        var snapCount = Math.Max(CountFiles(snapDir), 0);
        var ok = depot.TryApplyStaged(gamePath, "413150", "413151", sp.ManifestId, null, Ver3());
        var bodyOk = BodyVersion().StartsWith(expect[label].Body);
        Check($"A* 切换到 {label}", ok && bodyOk, $"body={BodyVersion()}");
        CheckSmapi(label);
        // staging 没有 Mods 目录 = 该版本快照本就没有 mod → 游戏侧必须为空；
        // 本轮从这里切走过则按切走时的数量核对。旧写法是目录不存在就整条跳过，
        // 断言会静默消失（实测：昨天跑过 4 条，今天 0 条而报告依然全 PASS）。
        var modsAfter = Math.Max(CountFiles(Path.Combine(gamePath, "Mods")), 0);
        var basis = hasSnap ? "staging 快照" : modsAtLeave.ContainsKey(label) ? "本轮切走时的计数" : "无快照且未切走过 → 应为空";
        var want = hasSnap ? snapCount : modsAtLeave.TryGetValue(label, out var m) ? m : 0;
        Check($"A* {label} Mods 按版本恢复（依据：{basis}）", modsAfter == want,
            $"游戏 {modsAfter} vs 期望 {want}（切换前 {Math.Max(modsBefore, 0)}）");
        currentLabel = label;
    }

    if (!BodyVersion().StartsWith("1.6.15"))
    {
        Console.WriteLine("A0 上次中断在中间版本（" + BodyVersion() + "），先归一到 1.6.15…");
        SwitchTo("1.6.15");
    }
    else Console.WriteLine("A0 基线 1.6.15 ✓");

    SwitchTo("1.4");
    SwitchTo("1.2.30");
    SwitchTo("1.0");
    SwitchTo("1.6.15");   // 回基线

    foreach (var (bucket, tag, probe) in new[]
    {
        ("legacy-2", "2.5", "Newtonsoft.Json.dll"),
        ("legacy-3", "3.7.3", "smapi-internal/Newtonsoft.Json.dll"),
        ("modern", "latest", "smapi-internal"),
    })
    {
        var dir = Path.Combine(@"E:\junigrid\depot-staging\_smapi-pool", bucket);
        var meta = Path.Combine(dir, "_junigrid-smapi.meta");
        var metaOk = File.Exists(meta) && File.ReadLines(meta).FirstOrDefault()?.Trim() == tag;
        var probePath = Path.Combine(dir, probe.Replace('/', Path.DirectorySeparatorChar));
        var probeOk = Directory.Exists(probePath) || File.Exists(probePath);
        Check($"A* 共享池 {bucket} 完整（meta={tag} + 依赖在）", metaOk && probeOk);
    }
}

// ═══════════════ C. SMAPI 命令通道 ═══════════════
// 不真起游戏：拿同为 .NET 控制台程序的假子进程顶进 LauncherService._smapiProcess，
// StartInfo 形状与 LaunchSmapiCore 一致（RedirectStandardInput=true + CreateNoWindow=true、
// stdout/stderr 不接）。输出侧用本机真实 SMAPI-latest.txt 当黄金语料。
if (!portraitOnly && !guardOnly && !qrOnly && !savesOnly)
{
    Console.WriteLine("\n────── C. SMAPI 命令通道 ──────");
    var np = BindingFlags.NonPublic | BindingFlags.Instance;
    var st = BindingFlags.NonPublic | BindingFlags.Static;
    var fldProc = typeof(LauncherService).GetField("_smapiProcess", np)!;
    var tailRegex = (Regex)typeof(LauncherService).GetField("LevelHeaderRegex", st)!.GetValue(null)!;
    var clearCrash = typeof(LauncherService).GetMethod("ClearSmapiCrashState", st)!;
    var raiseLog = typeof(LauncherService).GetMethod("RaiseLog", np)!;

    // 子进程的日志文件由它自己持有写句柄：必须像 LauncherService 尾随那样用
    // FileShare.ReadWrite 打开 —— File.ReadAllLines 默认 FileShare.Read，
    // 与写句柄冲突会抛「正在被另一进程使用」（实测把这条真实缺陷藏成了「0 行」假故障）。
    // 另外只在末行带换行时才计数，否则半行会被当成一行。
    List<string>? PipeLines(string f)
    {
        try
        {
            if (!File.Exists(f)) return null;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var txt = sr.ReadToEnd();
            if (!txt.EndsWith("\n")) return null;
            return txt[..^1].Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        }
        catch { return null; }
    }
    int CountLines(string f) => PipeLines(f)?.Count ?? 0;
    List<string> WaitForLines(string f, int want, int ms = 8000)
    {
        var deadline = Environment.TickCount64 + ms;
        List<string> last = new();
        while (Environment.TickCount64 < deadline)
        {
            var l = PipeLines(f);
            if (l is not null) { last = l; if (l.Count >= want) return l; }
            Thread.Sleep(100);
        }
        return last;
    }
    string Q(string s) => "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    // C0 输入框的可用性必须说实话
    var idle = new LauncherService(cfgSvc);
    Check("C0 未启动游戏 → CanSendCommand=false 且 SendCommand 不假装成功",
        !idle.CanSendCommand && !idle.SendCommand("help"));

    // C0b 日志上限：RaiseLog 连灌 2500 行只留最后 2000
    var capped = new LauncherService(cfgSvc);
    for (var i = 1; i <= 2500; i++) raiseLog.Invoke(capped, new object[] { "line" + i });
    var csnap = capped.GetLogSnapshot();
    Check("C0b 日志缓冲上限 2000 行，淘汰最旧而非最新",
        csnap.Count == 2000 && csnap[0] == "line501" && csnap[^1] == "line2500",
        "缓冲 " + csnap.Count + " 行，首=" + csnap.FirstOrDefault() + " 末=" + csnap.LastOrDefault());

    var fakeOut = Path.Combine(Path.GetTempPath(), $"jg-fake-smapi-{Guid.NewGuid():N}.txt");
    var psi = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add("--fake-smapi");
    psi.ArgumentList.Add(fakeOut);
    // 用产品自己选定的管道编码，而不是测试进程默认的 —— 否则测的是「dotnet run 的口味」
    var pipeEnc = typeof(LauncherService).GetField("SmapiPipeEncoding", st).GetValue(null) as Encoding;
    if (pipeEnc is not null) psi.StandardInputEncoding = pipeEnc;
    Process fake;
    try { fake = Process.Start(psi)!; }
    catch (Exception ex) { Check("C1 假 SMAPI 子进程能起来", false, ex.Message); fake = null!; }

    if (fake is not null)
    {
        // 预检：完全绕开 LauncherService，直接往同一条管道写一行。
        // 预检通 → 问题在 LauncherService 的写法；预检不通 → 这个 StartInfo 形状本身收不到。
        try
        {
            fake.StandardInput.WriteLine("PREFLIGHT");
            fake.StandardInput.Flush();
        }
        catch (Exception ex) { Note("预检直接写 StandardInput 抛异常", ex.Message); }
        var pre = WaitForLines(fakeOut, 1, 5000);
        Check("C1a 预检：不经 LauncherService 直接写管道，子进程收得到",
            pre.Count >= 1 && pre[0] == "PREFLIGHT",
            $"文件存在={File.Exists(fakeOut)} 子进程存活={!fake.HasExited} 收到 {pre.Count} 行 {Q(pre.FirstOrDefault() ?? "")}");
        var nBase = pre.Count;

        var lc = new LauncherService(cfgSvc);
        fldProc.SetValue(lc, fake);
        Check("C1 游戏进程存活 → CanSendCommand=true", lc.CanSendCommand);

        const string c1 = "player_add Abigail";
        const string c2 = "  time 1200  ";
        const string c3 = "set 技能 10";
        var s1 = lc.SendCommand(c1);
        var s2 = lc.SendCommand(c2);
        var s3 = lc.SendCommand(c3);
        var got = WaitForLines(fakeOut, nBase + 3);
        string At(int i) => got.Count > nBase + i ? got[nBase + i] : "(没收到)";
        Check("C1b 三条命令全部写进管道并被子进程读到",
            s1 && s2 && s3 && got.Count >= nBase + 3,
            "SendCommand 返回 " + s1 + "/" + s2 + "/" + s3 + "，预检后共 " + got.Count + " 行（应有 " + (nBase + 3) + "）");
        Check("C2 原样转发，不加 debug 前缀（v1.3.4 语义）", At(0) == c1, "第1行=" + Q(At(0)));
        Check("C3 前后空白裁掉后写入", At(1) == "time 1200", "第2行=" + Q(At(1)));
        Check("C4 中文参数原样往返（管道编码已与解码端对齐）", At(2) == c3,
            "第3行=" + Q(At(2)) + " | 期望=" + Q(c3)
            + " | 实际字节=" + (At(2) != "(没收到)" ? BitConverter.ToString(Encoding.UTF8.GetBytes(At(2))) : ""));
        // 中文参数能不能原样回来，取决于两端编码是否一致；不钉死的话父端会随启动方式
        // 在 utf-8 / 936 之间漂（有无控制台），子端恒按系统 ANSI 码页解 → 必须显式对齐。
        Note("C4a 管道两端编码", "产品指定 StandardInputEncoding = " + (pipeEnc?.WebName ?? "(未指定，退回 .NET 默认)")
            + "，子进程侧实测按 " + fake.StandardInput.Encoding.WebName + " 收（--probe-enc 取证：不钉编码时父进程会随启动方式在 utf-8/936 之间漂，而子进程恒按系统 ANSI 码页解）");

        var snap = lc.GetLogSnapshot();
        Check("C5 每条命令在日志里留 [JuniGrid] > 回显（面板看得见自己发过什么）",
            snap.Contains("[JuniGrid] > player_add Abigail") && snap.Contains("[JuniGrid] > set 技能 10")
            && snap.Last().StartsWith("[JuniGrid] > "),
            "缓冲 " + snap.Count + " 行，末行=" + Q(snap.LastOrDefault() ?? ""));

        var nBefore = CountLines(fakeOut);
        Check("C6 空串/纯空白被拦下，不往管道写空行",
            !lc.SendCommand("") && !lc.SendCommand("   ") && nBefore == nBase + 3,
            "管道行数 " + nBefore + "（预期 " + (nBase + 3) + "）");

        lc.SendCommand("help\nplayer_remove Haley");
        var gotNl = WaitForLines(fakeOut, nBefore + 2, 3000);
        Note("C7 一条输入里含换行 = SMAPI 收到 2 条命令（UI 是单行 input、浏览器粘贴会剥换行，实际不可达，仅记行为）",
            "行数 " + nBefore + " → " + gotNl.Count);

        try { fake.Kill(true); } catch { }
        try { fake.WaitForExit(5000); } catch { }
        Check("C8 游戏退出 → CanSendCommand=false、SendCommand=false",
            !lc.CanSendCommand && !lc.SendCommand("help"));
        fldProc.SetValue(lc, null);
        Check("C9 游戏由外部启动（Steam 直接进 / 别的启动器）→ 没有我们这条管道，输入框置灰",
            !lc.CanSendCommand);
        try { if (File.Exists(fakeOut)) File.Delete(fakeOut); } catch { }
    }

    // C10 无窗口 + 重定向 stdin 的前提：SMAPI 的「按任意键」崩溃恢复提示必须先清掉
    var tmpGame = Path.Combine(Path.GetTempPath(), "jg-crash-" + Guid.NewGuid().ToString("N")[..8]);
    var marker = Path.Combine(tmpGame, "smapi-internal", "StardewModdingAPI.crash.marker");
    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
    File.WriteAllText(marker, "crash");
    var crashLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StardewValley", "ErrorLogs", "SMAPI-crash.txt");
    var crashHold = crashLog + ".jgtest-hold";
    var hadCrashLog = File.Exists(crashLog);
    if (hadCrashLog) File.Move(crashLog, crashHold, true);   // 先把用户真实崩溃日志挪开，不拿它做实验
    try { clearCrash.Invoke(null, new object[] { tmpGame }); } catch (Exception ex) { Note("ClearSmapiCrashState 抛异常", ex.Message); }
    Check("C10 启动前清掉 SMAPI 崩溃标记（否则 ReadKey 在没有控制台时必炸）", !File.Exists(marker));
    if (hadCrashLog)
    {
        Check("C10b SMAPI-crash.txt 改名 .prev.txt 保留一份供排查",
            !File.Exists(crashLog) && File.Exists(crashHold + ".prev.txt"));
        try { File.Delete(crashHold + ".prev.txt"); File.Move(crashHold, crashLog, true); }
        catch (Exception ex) { Note("用户崩溃日志还原失败，请手工检查 " + crashHold, ex.Message); }
    }
    else
    {
        Note("C10b 当前不存在 SMAPI-crash.txt，改名分支跳过（不往用户 ErrorLogs 里造文件）");
        try { if (File.Exists(crashHold)) File.Delete(crashHold); } catch { }
    }
    try { Directory.Delete(tmpGame, true); } catch { }

    // C11 尾随过滤的行头识别
    Check("C11 级别行头识别：SMAPI 头命中，[JuniGrid] 与以 [ 开头的普通消息不误判",
        tailRegex.IsMatch("[02:14:16 INFO  SMAPI] Steam") && tailRegex.IsMatch("[02:14:16 TRACE SMAPI] x")
        && tailRegex.IsMatch("[02:14:16 ALERT SMAPI] y")
        && !tailRegex.IsMatch("[JuniGrid] 已启动 SMAPI 进程")
        && !tailRegex.IsMatch("[CP] 这条消息本身以方括号开头")
        && !tailRegex.IsMatch("[ERROR] 没有时刻前缀的 stderr 行"));

    // C12 输出着色黄金用例（含三条历史回归）
    var golden = new (string Line, string Want, string Why)[]
    {
        ("[12:00:00 ERROR SMAPI] Something failed", "err", "ERROR 级"),
        ("[ERR] boom", "err", "裸 stderr"),
        ("x contains [ERROR] tag", "err", "内嵌 [ERROR]"),
        ("[12:00:00 ALERT SMAPI] Mod 1.0.41: https://n (you have 1.0.39)", "upd", "更新提示"),
        ("[12:00:00 WARN  SMAPI] 慢", "warn", "WARN 级"),
        ("[12:00:00 INFO  SMAPI] 载入完成", "info", "INFO 级"),
        ("[12:00:00 TRACE SMAPI] 刷屏", "trace", "TRACE 级"),
        ("[12:00:00 INFO  SMAPI]    Nyapu's Portraits 1.6 update by MLD | desc", "info", "回归：简介含 update 的 INFO 行不得变品红"),
        ("   at StardewValley.Game1.UpdateLocations() in D:\\a\\Game1.cs:line 6169", "err", "回归：栈帧不得掉进 upd/白色"),
        ("System.NullReferenceException: Object reference not set", "err", "异常首行"),
        ("--- End of inner exception stack trace ---", "err", "inner 异常"),
        ("[JuniGrid] > help", "sys", "启动器自身回显"),
        ("check for update please", "upd", "无级别标签才走内容启发式"),
    };
    var wrong = new List<string>();
    foreach (var g in golden)
    {
        var gotCls = LogLineClassifier.Classify(g.Line);
        if (gotCls != g.Want) wrong.Add($"{g.Why}: 期望 {g.Want} 实得 \"{gotCls}\" ← {g.Line[..Math.Min(50, g.Line.Length)]}");
    }
    Check("C12 输出着色分类黄金用例 13 条", wrong.Count == 0, string.Join(" ; ", wrong.Take(3)));

    // C15 启动模式默认页的判据：哪些版本「装不了 SMAPI」（UI 靠它决定置灰与归位）
    Check("C15 只有 1.0/1.01 判为「无可用 SMAPI」，1.2+ 都能装",
        UpdateService.IsSmapiUnsupported("1.0") && UpdateService.IsSmapiUnsupported("1.01")
        && !UpdateService.IsSmapiUnsupported("1.2.6338") && !UpdateService.IsSmapiUnsupported("1.3.7269")
        && !UpdateService.IsSmapiUnsupported("1.6.15") && !UpdateService.IsSmapiUnsupported("1.5.5"),
        "1.0/1.01 = 装不了；读不出版本时不算（返回 false，不误伤）"
        + " | IsSmapiUnsupported(null)=" + UpdateService.IsSmapiUnsupported(null));

    // C17 更新提示的「不同编号体系」护栏
    var cfg17 = new JuniGridConfig();
    var m17 = new ModEntry { Folder = "Haley Anime Portrait", Version = "0.0.1" };
    bool Shows(string local, string? remote, long? fid = null)
    {
        m17.Version = local;
        return NexusUpdateTruth.ShouldShowUpdate(cfg17, m17, remote, fid);
    }
    Check("C17 远端只有一段数字而本地多段 → 判为不可比不亮 ⇧；同段数/本地单段仍正常判",
        !Shows("0.0.1", "1") && !Shows("1.6.4", "2")
        && Shows("1.6.4", "1.6.7") && Shows("1", "2") && Shows("0.0.1", "0.0.2"),
        "0.0.1 ⇧ 1 屏蔽 ✓ ‖ 1.6.4 ⇧ 2 屏蔽 ✓ ‖ 1.6.4→1.6.7 仍提示 ✓ ‖ 本地单段 1→2 仍提示 ✓");

    // C17b smapi.io 快道只给版本号、不给 fileId，而 .nxm 安装记录里的 remoteVersion 是空串
    // —— 实测 mod 30482：16:16 装的正是最新 MAIN 125074（N 网文件版本 "1.4"），
    //    16:31 快道回 "1.4.0"，"1.4" 与 "1.4.0" 字符串不等 → ⇧ 永远消不掉，显示还停在包内 1.3.0
    var cfg17b = new JuniGridConfig();
    var m17b = new ModEntry { Folder = "Donut SVE", UniqueID = "DonutSteelPeas.SVESeasonalAnimePortraits", Version = "1.3.0", NexusModId = 30482 };
    cfg17b.ModNexusInstalls[m17b.UniqueID] = new NexusInstallRecord { NexusModId = 30482, FileId = 125074, RemoteVersion = "1.4" };
    var already14 = NexusUpdateTruth.AlreadyHasRemoteFile(cfg17b, m17b, "1.4.0", null);
    var newerVerNoFileId = NexusUpdateTruth.ShouldShowUpdate(cfg17b, m17b, "1.5.0", null);
    Check("C17b 无 fileId 时按语义比版本：已装 1.4 认得远端 1.4.0；真更高版仍亮",
        already14 && newerVerNoFileId,
        "1.4 vs 1.4.0 认成同一版=" + already14 + " ‖ 1.4⇧1.5.0 提示=" + newerVerNoFileId
        + " ‖ EffectiveLocalVersion=" + NexusUpdateTruth.EffectiveLocalVersion(cfg17b, m17b));

    // C17c 一个 mod 有多条 MAIN 时（mod 1839：CP 主包 + 散 xnb 素材包同日发布），
    // 「最新 MAIN」按上传时间会选中散图包 → 点更新永远装不到真正的那个包
    var mains1839 = new (NexusFileInfo F, long Ts)[] {
        (new NexusFileInfo(182306, "Oho Davi Portrait xnb 1.6.7", "1.6.7", "MAIN", 721586), 1789100040),
        (new NexusFileInfo(182304, "CP Portrait Anime Mods OhoDavi", "1.6.7", "MAIN", 860123), 1789100000),
    };
    var withHint = NexusService.PickMainForInstall(mains1839, "[CP] Portrait Anime Mods OhoDavi");
    var noHint = NexusService.PickMainForInstall(mains1839, null);
    var singleMain = NexusService.PickMainForInstall(
        new (NexusFileInfo, long)[] { (new NexusFileInfo(125074, "Seasonal_Anime_Portraits", "1.4", "MAIN", 500000), 1739499000) },
        "[CP] Donut's Seasonal Anime Characters SVE");
    Check("C17c 多条 MAIN 时优先选与已装包同名的那条；没有名字对得上的仍按最新 MAIN（不改变现状）",
        withHint?.FileId == 182304 && noHint?.FileId == 182306 && singleMain?.FileId == 125074,
        "带目录名提示→" + withHint?.FileId + " ‖ 无提示→" + noHint?.FileId + " ‖ 只有一条 MAIN→" + singleMain?.FileId);

    Check("C12b 「已加载」筛选认得清单块与条目，不认普通 INFO",
        LogLineClassifier.MatchesFilter("[12:00:00 INFO  SMAPI] Loaded 21 mods:", "loaded")
        && LogLineClassifier.MatchesFilter("[12:00:00 INFO  SMAPI]    Cloudy Skies 1.9.1 by Khloe Leclair | desc", "loaded")
        && !LogLineClassifier.MatchesFilter("[12:00:00 INFO  SMAPI] Game loaded", "loaded"));

    // C13 真实语料：把尾随循环的「TRACE 整组丢弃」在真日志上重放
    var smapiLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StardewValley", "ErrorLogs", "SMAPI-latest.txt");
    if (File.Exists(smapiLogPath))
    {
        // SMAPI/游戏可能正持有写句柄 —— 必须 FileShare.ReadWrite，否则本段整崩（实测）
        string[] all;
        try
        {
            using var fs = new FileStream(smapiLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            all = sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (Exception ex) { Note("C13 日志被占用，真实语料用例跳过", ex.Message); all = Array.Empty<string>(); }
        var visible = new List<string>();
        var inTrace = false;
        foreach (var line in all)
        {
            if (line.Length == 0) continue;
            if (tailRegex.IsMatch(line)) inTrace = line.Contains(" TRACE ");
            if (!inTrace) visible.Add(line);
        }
        var traceInFile = all.Count(l => tailRegex.IsMatch(l) && l.Contains(" TRACE "));
        var traceLeaked = visible.Count(l => tailRegex.IsMatch(l) && l.Contains(" TRACE "));
        var debugKept = visible.Count(l => tailRegex.IsMatch(l) && l.Contains(" DEBUG "));
        var errInFile = all.Count(l => l.Contains(" ERROR "));
        var errKept = visible.Count(l => l.Contains(" ERROR "));
        Check("C13 真实日志重放：TRACE 组不进缓冲，ERROR 一条不丢",
            traceLeaked == 0 && errKept == errInFile,
            $"文件 {all.Length} 行 / TRACE 头 {traceInFile} / 进缓冲 {visible.Count}（省 {100 - (all.Length > 0 ? visible.Count * 100 / all.Length : 100)}%）/ ERROR {errInFile}→{errKept}");
        Note("C13a DEBUG 级不属被丢弃的 TRACE 组，仍会进缓冲并占用 2000 行上限",
            "本次语料里 DEBUG 头 " + debugKept + " 行进缓冲（分类为 trace 灰）");

        var misErr = visible.Count(l => tailRegex.IsMatch(l) && l.Contains(" INFO ") && LogLineClassifier.Classify(l) == "err");
        Check("C13b 进缓冲的行里没有 INFO 级被内容启发式误染成红色",
            misErr == 0, $"INFO→err 共 {misErr} 行" + (misErr > 0 ? "，例：" + Q(visible.First(l => tailRegex.IsMatch(l) && l.Contains(" INFO ") && LogLineClassifier.Classify(l) == "err")) : ""));
        Note("C13c 语料来源", smapiLogPath + " 最后写入 " + File.GetLastWriteTime(smapiLogPath));
    }
    else Note("C13 本机没有 SMAPI-latest.txt，真实语料用例跳过");

    // C14 版本锁判定：官方最新版 + 无降级记录 → 不能上锁（上了 Steam 就报磁盘写入错误）
    var lockMethod = typeof(LauncherService).GetMethod("IsDowngradeLockNeeded", np)!;
    var l2 = new LauncherService(cfgSvc);
    var saveLock = cfg.LockGameVersion; var saveMan = cfg.LastHistoricalManifest;
    bool NeedLock(bool lockSw, string? man) { cfg.LockGameVersion = lockSw; cfg.LastHistoricalManifest = man; return (bool)lockMethod.Invoke(l2, null)!; }
    Check("C14 版本锁只在「真降级」时生效（关锁/无记录/记录=官方最新 → 都不锁；记录=历史版 → 锁）",
        !NeedLock(false, "8462477710223862747") && !NeedLock(true, null) && !NeedLock(true, "")
        && !NeedLock(true, "4278718763097142923") && NeedLock(true, "8462477710223862747"),
        "无记录时旧行为会返回 true（= 给官方最新版上只读锁）");
    cfg.LockGameVersion = saveLock; cfg.LastHistoricalManifest = saveMan;
}

// ═══════════════ F. Steam 扫码：出码后掉线的处置（假 DD，离线） ═══════════════
// 治的是 2026-09-19 03:45 那次：码出来了 → 15 秒后 CM 掉线 → DD 退出 → challenge 作废，
// 界面上那张死码还在，用户扫了手机只报「加载二维码失败」，旁边还挂着"连不上"三个字。
if (qrOnly)
{
    Console.WriteLine("\n────── F. Steam 扫码死码重取 ──────");
    var ddDir = Path.Combine(AppContext.BaseDirectory, "tools", "DepotDownloader");
    try { Directory.CreateDirectory(ddDir); } catch { }
    // 把测试台自己改名顶包成 DD：apphost 里嵌的是「自己的 dll 名」（实测改名后它仍去找
    // JuniGridTestHarness.dll），所以 exe 改名、其余三件套按原名一起拷进同一个目录。
    // 启动器里 DepotDownloaderExe = AppContext.BaseDirectory\tools\DepotDownloader\DepotDownloader.exe，
    // 且 EnsureDepotDownloaderAsync 见文件即返回 → 不会去 GitHub 下载真的。
    var selfBase = Path.Combine(AppContext.BaseDirectory, "JuniGridTestHarness");
    var missing = new[] { ".exe", ".dll", ".runtimeconfig.json", ".deps.json" }
        .FirstOrDefault(e => !File.Exists(selfBase + e));
    if (missing is not null)
    {
        Note("F 段跳过：测试台产物不全，缺 " + selfBase + missing);
        Environment.Exit(fail == 0 ? 0 : 1);
    }
    foreach (var e in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
        File.Copy(selfBase + e, Path.Combine(ddDir, "JuniGridTestHarness" + e), true);
    File.Copy(selfBase + ".exe", Path.Combine(ddDir, "DepotDownloader.exe"), true);
    Console.WriteLine("  假 DD 已就位：" + ddDir);

    var svcF = new DepotDownloaderService();
    var nFileF = Path.Combine(Path.GetTempPath(), "jg-fake-dd-attempt.txt");

    // 码行夹具：自己按 DD 的画法（每模块 2 字符、静区留白）画两张真码。
    // 从 juni-grid.log 里捞现成的码行试过 —— 日志把行尾空白截了，还原出来解不动。
    var artFiles = new List<string>();
    var artUrls = new[] { "https://s.team/q/1/1234567890123456789", "https://s.team/q/1/9876543210987654321" };
    try
    {
        for (var ai = 0; ai < artUrls.Length; ai++)
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(artUrls[ai], QRCoder.QRCodeGenerator.ECCLevel.M);
            var rows = data.ModuleMatrix.Select(line =>
                string.Concat(line.Cast<bool>().Select(m => m ? "██" : "  "))).ToArray();
            var f = Path.Combine(Path.GetTempPath(), $"jg-fake-dd-art-{ai}.txt");
            File.WriteAllLines(f, rows, new UTF8Encoding(false));
            artFiles.Add(f);
        }
    }
    catch (Exception ex) { Console.WriteLine("  码行夹具画不出来：" + ex.Message); }
    Environment.SetEnvironmentVariable("JG_FAKE_DD_ART", string.Join(";", artFiles));

    async Task<(int emits, int stales, int attempts, string? err, string? acct)> RunFake(string script)
    {
        try { File.Delete(nFileF); } catch { }
        Environment.SetEnvironmentVariable("JG_FAKE_DD", script);
        int emits = 0, stales = 0;
        string? err = null, acct = null;
        try { acct = await svcF.QrLoginOnlyAsync(_ => { emits++; return Task.CompletedTask; },
                null, CancellationToken.None, null, () => stales++); }
        catch (Exception ex) { err = ex.Message; }
        finally { Environment.SetEnvironmentVariable("JG_FAKE_DD", null); }
        int.TryParse(File.Exists(nFileF) ? File.ReadAllText(nFileF) : "0", out var at);
        return (emits, stales, at, err, acct);
    }

    // F1 出码后掉线 → 自动撤码、重取一张、这一张登录成功
    var r1 = await RunFake("recover");
    Check("F1 死码自动换成新码并登录成功（旧行为：停在死码上让用户自己关窗重开）",
        r1.emits == 2 && r1.stales == 1 && r1.err is null && r1.acct == "fakster",
        $"出码 {r1.emits} 次 / 撤码 {r1.stales} 次 / 账号 {r1.acct ?? "-"} / 报错 {r1.err ?? "无"}");

    // F2 张张都掉线 → 文案说真话（是码死了，不是连不上），且只额外刷一次码不骚扰人
    var r2 = await RunFake("dead");
    Check("F2 出过码后的失败说「二维码已失效」，不再甩「CM 握手已重试 3 次」",
        r2.err is not null && r2.err.Contains("二维码已失效") && !r2.err.Contains("CM 握手已重试"),
        "err=" + (r2.err ?? "(没报错)"));
    Check("F3 死码只额外重取一次（第 2 张仍死就收手，不无限刷码）",
        r2.emits == 2 && r2.stales == 1 && r2.attempts == 2,
        $"出码 {r2.emits} / 撤码 {r2.stales} / 起进程 {r2.attempts} 次");

    // F4 压根没出码 → 老的「连不上」文案与 3 路重试原样保留（不能被 F2 的新分支吃掉）
    var r4 = await RunFake("noqr");
    Check("F4 没出码时仍是「连不上 Steam 服务器」并跑满 3 路",
        r4.emits == 0 && r4.stales == 0 && r4.attempts == 3
        && r4.err is not null && r4.err.Contains("连不上 Steam 服务器"),
        $"出码 {r4.emits} / 进程 {r4.attempts} 次 / err=" + (r4.err ?? "(没报错)"));

    // F5/F6 用真码行（从日志里捞的），验的是「码什么时候才被推到界面上」
    if (artFiles.Count >= 1)
    {
        var r5 = await RunFake("art");
        Check("F5 一张码画完就立刻推给界面（旧代码要等 DD 的下一行文字，而那行常常已是「码换了」）",
            r5.emits == 1 && r5.err is null,
            $"出码 {r5.emits} 次（夹具 {artFiles.Count} 张码行）/ err={r5.err ?? "无"}");
    }
    else Note("F5 跳过：日志里没捞到可用的码行夹具");

    if (artFiles.Count >= 2)
    {
        var r6 = await RunFake("art2");
        Check("F6 DD 换第二张 challenge 时界面跟着换（停在上一张 = 让人扫废码）",
            r6.emits == 2 && r6.err is null,
            $"出码 {r6.emits} 次 / err={r6.err ?? "无"}");
    }
    else Note("F6 跳过：日志里不足两张不同的码行夹具");

    // F7 治「扫码成功后 10 万年才有反应」：DD 打完 Success! 就去枚举 license，
    // 卡住时按 300 秒静默上限干等（07:25:48 那次实测把弹窗挂死，用户只能再点一次）
    var sw7 = Stopwatch.StartNew();
    var r7 = await RunFake("authhang");
    sw7.Stop();
    Check("F7 Success! 之后 DD 挂住不退出 ⇒ 几秒内自己收工（旧的要等 300 秒静默上限）",
        r7.acct == "fakster" && r7.err is null && sw7.ElapsedMilliseconds < 20000,
        $"耗时 {sw7.ElapsedMilliseconds} ms / 账号 {r7.acct ?? "-"} / err={r7.err ?? "无"}");

    // F8 一路 DD 闷头退避（连不上 CM 时它自己烧 60 秒）⇒ 到点没进展就重开一路，
    // 别让人对着 0% 干等。测试台把截止临时调到 1.5 秒（真机是 20 秒）。
    var deadlineF = typeof(DepotDownloaderService).GetField("KickoffDeadlineMs",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var realDeadline = (int)deadlineF.GetValue(null)!;
    deadlineF.SetValue(null, 1500);
    var sw8 = Stopwatch.StartNew();
    var r8 = await RunFake("hang");
    sw8.Stop();
    deadlineF.SetValue(null, realDeadline);
    Check("F8 到点没出码也没登录成功 ⇒ 掐掉重开一路，且文案给出网络解法",
        r8.attempts == 3 && r8.emits == 0 && r8.err is not null
        && r8.err.Contains("连不上 Steam 服务器") && r8.err.Contains("系统代理")
        && sw8.ElapsedMilliseconds < 30000,
        $"起进程 {r8.attempts} 次 / 耗时 {sw8.ElapsedMilliseconds} ms / err=" + (r8.err ?? "(没报错)"));

    // F9–F11 登录代理注入。为什么必须端到端验：DD 的 HttpClient 是 Proxy=null + UseProxy=true，
    // 这种组合下 .NET 只查环境变量、**不读 Windows 系统代理** —— 所以「用户开了 Clash 系统代理
    // 却照样连不上」是必然的。我们的修法就是替它把地址翻译成 HTTPS_PROXY 塞进子进程。
    Check("F9 代理地址归一化：缺 scheme 补 http://、socks 一律拒绝、空值不注入",
        DepotDownloaderService.NormalizeProxyUrl("127.0.0.1:7890") == "http://127.0.0.1:7890"
        && DepotDownloaderService.NormalizeProxyUrl("http://127.0.0.1:7890") == "http://127.0.0.1:7890"
        && DepotDownloaderService.NormalizeProxyUrl("socks5://127.0.0.1:7891") is null
        && DepotDownloaderService.NormalizeProxyUrl("   ") is null,
        "127.0.0.1:7890 → " + (DepotDownloaderService.NormalizeProxyUrl("127.0.0.1:7890") ?? "null"));

    // 喂固定地址测注入本身。不拿「读注册表」那条链路做断言：那样测的是这台机器的注册表，
    // 换台机器就退化成空断言。注册表那条交给 F11 端到端兜。
    static (string? https, string? http) Injected(string? proxy)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd") { UseShellExecute = false };
        // 先清掉测试台自己继承来的值，否则「无代理」那条会被环境里的残留假通过
        psi.Environment.Remove("HTTPS_PROXY");
        psi.Environment.Remove("HTTP_PROXY");
        DepotDownloaderService.ApplyDdProxyEnv(psi, proxy);
        // 这个字典的索引器在键不存在时是抛 KeyNotFoundException，不是返回 null —— 必须 TryGetValue
        return (Env(psi.Environment, "HTTPS_PROXY"), Env(psi.Environment, "HTTP_PROXY"));
    }
    static string? Env(IDictionary<string, string?> e, string k) => e.TryGetValue(k, out var v) ? v : null;
    var on = Injected("http://127.0.0.1:7890");
    var off = Injected(null);
    Check("F10 注入同时写 HTTPS_PROXY 与 HTTP_PROXY；没代理时两个都不写",
        on.https == "http://127.0.0.1:7890" && on.http == "http://127.0.0.1:7890"
        && off.https is null && off.http is null,
        $"有代理 → {on.https ?? "-"} / 无代理 → {(off.https is null && off.http is null ? "两个都空" : off.https ?? off.http)}");

    // 端到端：真起一次子进程，让它自己转储拿到的环境，比对这台机器的注册表值。
    // 撤掉 RunDepotDownloaderAsync 里的 ApplyDdProxyEnv(psi) 那一行，这条立刻转红。
    var proxyDump = Path.Combine(Path.GetTempPath(), "jg-fake-dd-proxy.txt");
    try { File.Delete(proxyDump); } catch { }
    _ = await RunFake("recover");
    var sysProxy = DepotDownloaderService.ReadSystemProxy();
    var dumped = File.Exists(proxyDump) ? File.ReadAllText(proxyDump) : "(假 DD 没写文件)";
    Check("F11 子进程实际拿到的 HTTPS_PROXY == 本机系统代理（目录请求与 WebSocket CM 都走它）",
        dumped == (sysProxy ?? "") + "|" + (sysProxy ?? ""),
        $"系统代理={sysProxy ?? "(未开启)"} / 子进程看到={(dumped == "|" ? "两个都空" : dumped)}");
    try { File.Delete(proxyDump); } catch { }

    try { File.Delete(nFileF); } catch { }
    // 收摊：把顶包的假 DD 清掉，免得日后有人把它当成真组件。
    // 刚退出的子进程 exe 常被系统多攥一会儿（实测 Access denied），所以要带退避重试。
    if (File.Exists(Path.Combine(ddDir, "JuniGridTestHarness.dll")))
    {
        var fakeExe = Path.Combine(ddDir, "DepotDownloader.exe");
        for (var i = 0; i < 10; i++)
        {
            try { Directory.Delete(ddDir, true); break; }
            catch { Thread.Sleep(400); }
        }
        if (File.Exists(fakeExe))
            Note("假 DD 未清理干净（不影响结论，只在测试台 bin 里）", fakeExe);
    }
    Console.WriteLine($"\n════════ 总计：{pass} PASS / {fail} FAIL ════════");
    RestoreConfig();
    Environment.Exit(fail == 0 ? 0 : 1);
}

// ═══════════════ D. 版本切换兜底（沙箱，只碰临时目录） ═══════════════
if (guardOnly)
{
    Console.WriteLine("\n────── D. 版本切换兜底（沙箱） ──────");
    // 沙箱会把真实配置的 cacheRoot 临时改指到 Temp 区。若 JuniGrid 正在运行，
    // 它一旦重启/重新载入配置，就会对着这个没有 Mods 的残缺沙箱执行 apply ——
    // 判「目标版本无缓存 Mods」→ 清空游戏 Mods（2026-09-19 实测清掉 113 个）。
    if (System.Diagnostics.Process.GetProcessesByName("JuniGrid").Length > 0)
    {
        Note("D 段跳过：检测到 JuniGrid 正在运行。沙箱劫持 cacheRoot 期间应用重启会读到被劫持的配置并清空游戏 Mods，请先退出应用再跑。",
             "可先关闭 JuniGrid（含托盘），跑完本段后再启动。");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
    var root = Path.Combine(Path.GetTempPath(), "jg-apply-guard");
    try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    var cache = Path.Combine(root, "cache");
    var stageRoot = Path.Combine(cache, "depot-staging");
    var game = Path.Combine(root, "game");
    var pack = Path.Combine(stageRoot, "413150-413151-1.6.15");
    // 模板兜底：1.6.15 那份可能被删了/切走归档了。D 段只要一份「1.6 时代、FileVersion 读得出来、
    // 锚点文件齐」的本体（各用例里的版本号都是显式传的）。
    // ⚠ 必须逐条验：后台正在下载的那些包里躺着**预分配的零填充 dll**，读不出版本也缺 Content，
    //   拿它当模板会让整段 D 用例一起报「版本缓存不完整」（09-21 18:4x 就这么翻过一轮）。
    bool TmplUsable(string dll)
    {
        try
        {
            var dir = Path.GetDirectoryName(dll) ?? "";
            var fi = new FileInfo(dll);
            var anchor = new FileInfo(Path.Combine(dir, @"Content\Data\Achievements.xnb"));
            // ⚠ 只判 File.Exists 不够：正在下载的包里 dll 是预分配的空壳（6 MB 但没有版本资源），
            // 而包内那个原生壳 exe 还能读出 4.5.1 → 走"exe 兜底"会被误当成版本读得出来。
            // 锚点按 CheckStagingAnchors 的口径取 1.6 时代的那份（1.6+ 已重构 Content，
            // 官方包里根本没有 NPCDispositions.xnb —— 要求它会把所有真包都判成不可用）。
            var fv = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "";
            return fi.Exists && fi.Length > 1_000_000 && fv.StartsWith("1.6", StringComparison.Ordinal)
                && anchor.Exists && anchor.Length > 1024;
        }
        catch { return false; }
    }
    var stagingRoot0 = @"E:\junigrid\depot-staging";
    // 模板必须是 1.6.15 那一份：D 段多处用例（D12 的导入落点、E3 的 4 段命名孤儿目录认领）
    // 认的就是这个号，换一份 1.6.x 会让它们假失败。读不出来就老实跳过整段，别硬跑。
    var tmplCandidates = new List<string>
    {
        Path.Combine(stagingRoot0, "413150-413151-1.6.15", "Stardew Valley.dll"),
        Path.Combine(new ConfigService().Current.GamePath ?? "", "Stardew Valley.dll"),
    };
    // 缓存里 1.6.15 那份可能还在下（半成品读不出版本），退而求其次用任意一份完整 1.6.x；
    // 但 D12 的导入落点、E3 的「4 段命名孤儿目录」必须跟着**实际用的那一版**走，
    // 否则缓存里只剩 1.6.13 时这两条会假失败（09-22 00:35 就这么跳过了一整段）。
    if (Directory.Exists(stagingRoot0))
        tmplCandidates.AddRange(Directory.GetDirectories(stagingRoot0, "413150-413151-1.6*")
            .OrderByDescending(d => d).Select(d => Path.Combine(d, "Stardew Valley.dll")));
    var tmplDll = tmplCandidates.FirstOrDefault(TmplUsable) ?? "";
    var tmplVer = string.IsNullOrEmpty(tmplDll)
        ? "" : UpdateService.ReadLocalGameVersion(Path.GetDirectoryName(tmplDll)!) ?? "";

    // 把统一缓存目录改指到临时区：StagingRoot / _smapi-pool / _mods-orphan 全落这里，
    // 真实 E:\junigrid 不碰。StoragePaths.CacheRoot 只由 ConfigService 载入时赋值，
    // 所以「改配置 → 重建 ConfigService」是唯一入口；Save 是 250ms 防抖的，必须等落盘。
    var c2 = cfgSvc.Current; c2.CacheRoot = cache; cfgSvc.Save(c2);
    var settle = Environment.TickCount64 + 4000;
    while (Environment.TickCount64 < settle && !File.ReadAllText(cfgFile).Contains("jg-apply-guard"))
        Thread.Sleep(100);
    var cfgSvc2 = new ConfigService();
    Check("D0 缓存目录已改指临时区（否则下面会写真缓存）", StoragePaths.CacheRoot == cache,
        "StoragePaths.CacheRoot = " + (StoragePaths.CacheRoot ?? "(null)"));
    if (StoragePaths.CacheRoot != cache) { RestoreConfig(); Environment.Exit(1); }

    // D 段每次都真走 ApplyStagingExclusive，而那里现在带存档留底一步 —— 不指到沙箱就会
    // 把玩家 %APPDATA% 里的真存档复制几十 MB 进临时区（沙箱只准碰临时区，见 S 段）。
    SaveVersionService.SavesDirOverride = Path.Combine(root, "Saves");
    Directory.CreateDirectory(SaveVersionService.SavesDirOverride);
    // 09-23 起收起抽屉落 LocalAppData\saves-hidden（与 Saves 同盘求瞬时改名）。
    // 沙箱必须覆写，否则会写进用户真抽屉；断言也走 HiddenRootOf，不再认旧的 pack\Saves-hidden。
    SaveVersionService.DrawerRootOverride = Path.Combine(root, "saves-hidden");

    if (!File.Exists(tmplDll))
    {
        Note("D0b 没有可用的本体模板（要读得出版本号且 Content 锚点齐；正在下载的半成品 dll 是零填充的，不能用）→ D 段跳过", tmplDll);
        RestoreConfig(); Environment.Exit(fail == 0 ? 0 : 1);
    }

    // 本体夹具必须满足 CheckStagingAnchors 的锚点（否则前置校验就会拦下，测不到后面的分支）
    void MkBody(string dir)
    {
        Directory.CreateDirectory(dir);
        File.Copy(tmplDll, Path.Combine(dir, "Stardew Valley.dll"), true);
        File.WriteAllText(Path.Combine(dir, "Stardew Valley.exe"), "xna host");
        foreach (var rel in new[] { @"Content\Characters\Abigail.xnb", @"Content\Portraits\Abigail.xnb", @"Content\Data\Achievements.xnb" })
        {
            var f = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "xnb");
        }
        File.WriteAllText(Path.Combine(dir, "steam_appid.txt"), "413150");
    }
    void MkMods(string parent, params string[] names)
    {
        var mods = Path.Combine(parent, "Mods");
        Directory.CreateDirectory(mods);
        foreach (var n in names)
        {
            var p = Path.Combine(mods, n);
            Directory.CreateDirectory(p);
            File.WriteAllText(Path.Combine(p, "manifest.json"), "{\"Name\":\"" + n + "\",\"UniqueID\":\"jg." + n + "\"}");
        }
    }
    void MkSmapiBare(string dir) => File.WriteAllText(Path.Combine(dir, "StardewModdingAPI.exe"), "bare");
    void MkSmapiFull(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "StardewModdingAPI.exe"), "exe");
        File.WriteAllText(Path.Combine(dir, "StardewModdingAPI.dll"), "dll");
        Directory.CreateDirectory(Path.Combine(dir, "smapi-internal"));
        File.WriteAllText(Path.Combine(dir, "smapi-internal", "Newtonsoft.Json.dll"), "nj");
    }
    int ModsCount(string parent)
    {
        var mods = Path.Combine(parent, "Mods");
        return Directory.Exists(mods) ? Directory.GetDirectories(mods).Length : -1;
    }
    bool HasModsFile(string parent, string n) => File.Exists(Path.Combine(parent, "Mods", n, "manifest.json"));

    var applyMi = typeof(DepotDownloaderService).GetMethod("ApplyStagingExclusive",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var lastReport = new List<string>();
    string? lastError = null;
    void DoApply(string target, string? currentVer)
    {
        lastReport.Clear(); lastError = null;
        if (!game.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new Exception("保险：游戏目录不在临时区，拒绝 apply");
        var pr = new Progress<DepotDownloaderService.Progress>(p => lastReport.Add(p.State));
        try { applyMi.Invoke(null, new object[] { target, game, pr, currentVer }); }
        catch (TargetInvocationException ex) { lastError = ex.InnerException?.Message ?? ex.Message; }
    }
    bool Warned(params string[] keys) => lastReport.Any(r => keys.All(k => r.Contains(k, StringComparison.OrdinalIgnoreCase)));

    // ── D1 目标 == 当前版本：手装 mod 不能被清掉（① 刻意跳过搬运，③ 却照样清空）──
    Directory.CreateDirectory(stageRoot);
    MkBody(pack);
    MkBody(game);
    MkMods(game, "My Mod A", "My Mod B", "My Mod C");
    DoApply(pack, "1.6.15");
    Check("D1 同版本 apply：游戏里手装的 3 个 mod 全部还在",
        ModsCount(game) == 3 && HasModsFile(game, "My Mod A") && HasModsFile(game, "My Mod C"),
        "切换后游戏 Mods 顶层=" + ModsCount(game) + (lastError is null ? "" : " 异常=" + lastError));

    // ── D2 目标 == 当前版本：快照里那份 Mods 不该反过来覆盖掉现役的 ──
    MkMods(pack, "Snapshot Pack");
    MkMods(game, "My Mod A", "My Mod B", "My Mod C");
    DoApply(pack, "1.6.15");
    Check("D2 同版本 apply：现役 Mods 保持原样，快照那棵不被搬空",
        ModsCount(game) == 3 && HasModsFile(game, "My Mod B") && ModsCount(pack) == 1,
        "游戏=" + ModsCount(game) + " 快照=" + ModsCount(pack));

    // ── D3 手装的「裸 exe SMAPI」：② 会把整个本体删掉，必须留得下备份 ──
    MkBody(game);
    MkMods(game, "My Mod A", "My Mod B", "My Mod C");
    MkSmapiBare(game);
    DoApply(pack, "1.6.15");
    var poolRoot = Path.Combine(stageRoot, "_smapi-pool");
    var asFound = Directory.Exists(poolRoot)
        ? Directory.GetDirectories(poolRoot, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "StardewModdingAPI.exe"))
                        || Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any())
            .ToList()
        : new List<string>();
    Check("D3 手装的残缺 SMAPI（只有裸 exe）在清盘前被保底留存",
        asFound.Count > 0,
        "池内可找回的备份目录 " + asFound.Count + " 个" + (asFound.Count > 0 ? "，例：" + Path.GetFileName(asFound[0]) : ""));

    // ── D4 该有 SMAPI 却没恢复出来时，必须当场说，而不是等用户点启动 ──
    Check("D4 缺 SMAPI 当场告警（该版本有适配 SMAPI 但缓存/池都给不出）",
        Warned("SMAPI"),
        "progress 末 3 条: " + string.Join(" | ", lastReport.TakeLast(3)));

    // ── D5 同版本 apply 仍要能修本体 + 恢复完整 SMAPI（别把兜底做成「什么都不干」）──
    MkSmapiFull(Path.Combine(pack, "smapi"));            // ④ 认的是 <staging>/smapi
    MkBody(game);
    MkMods(game, "My Mod A", "My Mod B", "My Mod C");
    File.Delete(Path.Combine(game, "steam_appid.txt"));      // 造一个「本体缺文件」的待修状态
    DoApply(pack, "1.6.15");
    Check("D5 同版本 apply 仍然重写本体并带回完整 SMAPI，且不动 Mods",
        File.Exists(Path.Combine(game, "steam_appid.txt"))
        && File.Exists(Path.Combine(game, "StardewModdingAPI.exe"))
        && Directory.Exists(Path.Combine(game, "smapi-internal"))
        && ModsCount(game) == 3,
        "本体回补=" + File.Exists(Path.Combine(game, "steam_appid.txt"))
        + " SMAPI=" + File.Exists(Path.Combine(game, "StardewModdingAPI.exe"))
        + " Mods=" + ModsCount(game));

    Check("D6 Mods 全程没有进过 _mods-orphan 黑洞（同版本不该产生孤儿）",
        !Directory.Exists(Path.Combine(stageRoot, "_mods-orphan"))
        || Directory.GetFileSystemEntries(Path.Combine(stageRoot, "_mods-orphan"), "*", SearchOption.AllDirectories).Length == 0,
        "孤儿区条目=" + (Directory.Exists(Path.Combine(stageRoot, "_mods-orphan"))
            ? Directory.GetFileSystemEntries(Path.Combine(stageRoot, "_mods-orphan"), "*", SearchOption.AllDirectories).Length : 0));

    // ── D7 池里那份与游戏里不是同一构建时，用户手装的那份不能被丢 ──
    var poolModern = Path.Combine(poolRoot, "modern");
    Directory.CreateDirectory(Path.Combine(poolModern, "smapi-internal"));
    File.WriteAllText(Path.Combine(poolModern, "StardewModdingAPI.exe"), "POOL-BUILD-v1");
    File.WriteAllText(Path.Combine(poolModern, "_junigrid-smapi.meta"), "latest\n1.6.15\n");
    var smapiSrc = Path.Combine(root, "smapi-holder");
    Directory.CreateDirectory(Path.Combine(smapiSrc, "smapi-internal"));
    File.WriteAllText(Path.Combine(smapiSrc, "StardewModdingAPI.exe"), "GAME-BUILD-v2-longer");
    var saved1 = DepotDownloaderService.SaveSmapiToPool(smapiSrc, "1.6.15");
    var poolExe = Path.Combine(poolModern, "StardewModdingAPI.exe");
    var poolNow = File.Exists(poolExe) ? File.ReadAllText(poolExe) : "(无)";
    Check("D7 桶 tag 相同但不是同一构建 → 以游戏目录那份为准覆盖桶",
        saved1 && poolNow == "GAME-BUILD-v2-longer", "池内 exe 现在是: " + poolNow);

    // ── D7b 同一构建仍走快路径（别反复重拷几百 MB）──
    File.WriteAllText(Path.Combine(smapiSrc, "StardewModdingAPI.exe"), poolNow);
    var sentinel = Path.Combine(poolModern, "smapi-internal", "sentinel.txt");
    File.WriteAllText(sentinel, "keep");
    var saved2 = DepotDownloaderService.SaveSmapiToPool(smapiSrc, "1.6.15");
    Check("D7b 同一构建仍走快路径（不重删重拷桶）",
        saved2 && File.Exists(sentinel), "桶内 sentinel 存活=" + File.Exists(sentinel));

    // ── D8 unknown 黑洞：至少报「有 N 项待认领」，不自动并回 ──
    var blindMods = Path.Combine(stageRoot, "_mods-orphan", "unknown", "Mods", "Leftover Pack");
    Directory.CreateDirectory(blindMods);
    File.WriteAllText(Path.Combine(blindMods, "manifest.json"), "{}");
    try { Directory.Delete(Path.Combine(pack, "Mods"), true); } catch { }
    MkBody(game);
    MkMods(game, "My Mod A");
    DoApply(pack, null);          // 故意让「当前版本」读不出来 → 复现 08:31 那条 unknown 路径
    Check("D8 版本读不出来时，unknown 区的 mod 当场提示待认领",
        Warned("unknown"), "progress 末 2 条: " + string.Join(" | ", lastReport.TakeLast(2)));

    // ── D9 磁盘空间前置 ──
    var ensureMi = typeof(DepotDownloaderService).GetMethod("EnsureDiskSpace",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    void TryEnsure(long needBytes, out string? err)
    {
        err = null;
        File.WriteAllText(Path.Combine(pack, ".junigrid-size"), needBytes.ToString());
        try { ensureMi.Invoke(null, new object[] { pack, game }); }
        catch (TargetInvocationException ex) { err = ex.InnerException?.Message; }
    }
    TryEnsure(1L, out var errLo);
    Check("D9a 空间足够时不拦（size cache = 1 B）", errLo is null, errLo ?? "未抛错");
    TryEnsure(400_000_000_000_000L, out var errHi);
    Check("D9b 空间不足时中止并说清怎么办", errHi is not null && errHi.Contains("中止"),
        errHi ?? "(没抛错)");
    try { File.Delete(Path.Combine(pack, ".junigrid-size")); } catch { }

    // ── D11 往 Mods 里复制的半路切版本：在途文件会被错记成目标版本的 Mods ──
    // 实测 2026-09-19 09:41：1.6.15 正在往里拷 mod，中途切到 1.0 → 10 个还没拷完的
    // mod 被记到 1.0 名下（1.0 没有 SMAPI，永远不会加载），切回 1.6.15 时它们没回来。
    MkBody(pack); MkBody(game); MkMods(game, "My Mod A");
    var bodyStamp = File.GetLastWriteTimeUtc(Path.Combine(game, "Stardew Valley.dll"));
    var writing = false;
    var writer = new Thread(() =>
    {
        var i = 0;
        while (writing)
        {
            try { File.WriteAllText(Path.Combine(game, "Mods", "My Mod A", $"copy-{i++}.dat"), new string('x', 4096)); }
            catch { }
            Thread.Sleep(250);
        }
    }) { IsBackground = true };
    writing = true; writer.Start();
    DoApply(pack, "1.6.15");
    Volatile.Write(ref writing, false); writer.Join(4000);
    Check("D11 Mods 正在被写入时拒绝切换，且本体一秒都没被碰过",
        lastError is not null && lastError.Contains("Mods 目录一直在变化")
        && File.GetLastWriteTimeUtc(Path.Combine(game, "Stardew Valley.dll")) == bodyStamp
        && HasModHere(game, "My Mod A"),
        "err=" + (lastError ?? "(没抛错)") + " 游戏 Mods=" + ModsCount(game));

    // 停笔之后同一个切换必须正常完成 —— 闸门不能焊死正常路径
    DoApply(pack, "1.6.15");
    Check("D11b 复制结束后同样的切换正常通过（闸门不误伤）",
        lastError is null && HasModHere(game, "My Mod A"),
        "err=" + (lastError ?? "无") + " 游戏 Mods=" + ModsCount(game));

    // ── D11c 闸门只保证「1.2 秒静默」：复制暂停后又恢复，文件会落进新版本的 Mods。
    // 直接单测回收函数（时序竞态用真实切换测必抖），验三件事：外来的挪走、本版本的留下、
    // 抽屉里同名的前半截不被覆盖掉。──
    var rcvGame = Path.Combine(root, "race", "game");
    var rcvLive = Path.Combine(rcvGame, "Mods");
    var rcvSink = Path.Combine(root, "race", "drawer", "Mods");
    Directory.CreateDirectory(rcvLive);
    Directory.CreateDirectory(rcvSink);
    void MkAt(string mods, string n)
    {
        var p = Path.Combine(mods, n);
        Directory.CreateDirectory(p);
        File.WriteAllText(p + "\\manifest.json", "{\"Name\":\"" + n + "\"}");
    }
    MkAt(rcvLive, "Target Pack");
    MkAt(rcvLive, "Late A");
    MkAt(rcvLive, "Late B");
    MkAt(rcvSink, "Late A");
    File.WriteAllText(Path.Combine(rcvSink, "Late A", "manifest.json"), "OLD-PARTIAL");
    var allowed11c = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Target Pack" };
    typeof(DepotDownloaderService).GetMethod("ReclaimInFlightMods",
        BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { rcvLive, allowed11c, rcvSink, "1.6.15", null });
    var sinkNames = Directory.GetDirectories(rcvSink).Select(Path.GetFileName).ToList();
    Check("D11c 在途后落的 mod 挪回源抽屉：新版本的留下、抽屉里同名前半截不被覆盖",
        HasModsFile(rcvGame, "Target Pack") && !Directory.Exists(Path.Combine(rcvLive, "Late A"))
        && !Directory.Exists(Path.Combine(rcvLive, "Late B"))
        && sinkNames.Count(n => n!.StartsWith("Late A")) == 2 && sinkNames.Contains("Late B")
        && File.ReadAllText(Path.Combine(rcvSink, "Late A", "manifest.json")) == "OLD-PARTIAL",
        "游戏 Mods=" + ModsCount(rcvGame) + " 抽屉=" + string.Join(",", sinkNames));

    // ── D12 离线导入本地版本包（CM 彻底连不上时的活路）──
    // 故意用一个"不像我们命名"的文件夹名，逼它走「读本体版本 → 查版本表拿 manifest 号」这条路。
    // 导入落点名就是模板那一版（tmplVer）的包，而上面所有 D 用例都用 1.6.15 同名夹具当目标包 ——
    // 产品在这里拒覆盖已有包是对的，所以先把夹具腾掉（导入成功后包会重新出现在同一路径，E3 照用）。
    var mf1615 = depot.GetKnownVersions("413150").First(k => k.Version == tmplVer).ManifestId;
    try { if (Directory.Exists(pack)) Directory.Delete(pack, true); } catch { }
    var offlineSrc = Path.Combine(root, "offline-src", "SDV-1615-backup");
    MkBody(offlineSrc);
    string? imported = null, importErr = null;
    try
    {
        imported = (string?)typeof(DepotDownloaderService)
            .GetMethod("ImportLocalPackage")!.Invoke(depot, new object[] { offlineSrc, "413150", "413151" });
    }
    catch (TargetInvocationException ex) { importErr = ex.InnerException?.Message; }
    var seen = depot.ListStagedPackages().FirstOrDefault(x => x.ManifestId == mf1615);
    Check("D12 导入本地包：认出版本 → 搬进缓存 → 立刻出现在版本列表（全程不连 Steam）",
        imported == tmplVer && seen is not null && !Directory.Exists(offlineSrc)
        && File.Exists(Path.Combine(seen.Path, ".junigrid-manifest")),
        "返回=" + (imported ?? "(null)") + " 列表里=" + (seen is null ? "没找到" : seen.Label)
        + " 源目录已搬走=" + !Directory.Exists(offlineSrc) + (importErr is null ? "" : " 异常=" + importErr));

    // 导进来的包必须真的能离线切换（TryApplyStaged 不碰网络）
    var appliedOffline = depot.TryApplyStaged(game, "413150", "413151", mf1615, null, "1.6.15");
    try { Directory.Delete(pack, true); } catch { }   // 先清干净：D11 系列在里头留过 Mods
    MkBody(pack);   // D12 腾掉了同名夹具，后面 D25/D26/E3 还要拿它当目标包
    Check("D12b 导入的包能直接离线切换（不发起任何 Steam 连接）",
        appliedOffline && File.Exists(Path.Combine(game, "Stardew Valley.dll")),
        "TryApplyStaged=" + appliedOffline);
    if (!Directory.Exists(pack)) MkBody(pack);   // E3 仍按路径引用这个包

    // ── D13 删版本包的确认文案：抽屉里存着 mod 必须报出来 ──
    // 那个文件夹里除了本体还躺着「该版本的 Mods 抽屉」，而删除是 Directory.Delete(dir,true) 递归硬删。
    // 只说"下次要重下"会让人以为大不了重下，实际把这一版的 mod 一起删了（2026-09-19 抽屉里 113 项）。
    var pkgD13 = new DepotDownloaderService.StagedPackage("1.6.15", "m-d13", pack, 1_700_000_000, DateTime.UtcNow, Complete: true);
    MkMods(pack, "Mod One", "Mod Two", "Mod Three");
    Directory.CreateDirectory(Path.Combine(pack, "Mods", ".junigrid_trash", "Gone"));
    var txtD13 = DepotDownloaderService.DeletePackConfirmText(pkgD13);
    Check("D13 文案报出「3 个 mod 文件夹」：回收站不算进去，也没说成 0 个",
        txtD13.Contains("3 个 mod 文件夹") && !txtD13.Contains("4 个") && !txtD13.Contains("0 个"),
        txtD13);
    var txtD13b = DepotDownloaderService.DeletePackConfirmText(
        pkgD13 with { Path = Path.Combine(root, "no-such-pack") });
    Check("D13b 抽屉不存在时回到短文案（不凭空喊有 mod，硬删警告仍然保留）",
        !txtD13b.Contains("mod 文件夹") && txtD13b.Contains("不进回收站"), txtD13b);

    // ── D14 改名失败退回拷贝时，必须兑现"移动"的语义 ──
    // 旧写法拷完不动源 → 游戏 Mods 与版本抽屉各存一份 113 项（实测 661 MB 双份）；
    // 更坏的是拷一半被打断，下次切换会拿残缺那份顶掉抽屉里完整那份。
    var mvMi = typeof(DepotDownloaderService).GetMethod("MoveDirectoryVerified",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    string DoMove(string s, string d, string? g) =>
        mvMi.Invoke(null, new object?[] { s, d, g, null, "测试移动", null })?.ToString() ?? "(null)";
    var mvARoot = Path.Combine(root, "mv", "a");
    var mvBRoot = Path.Combine(root, "mv", "b");
    var mvA = Path.Combine(mvARoot, "Mods");
    var mvB = Path.Combine(mvBRoot, "Mods");
    MkMods(mvARoot, "Keep Me");
    MkMods(mvBRoot, "OnlyInDest");          // dest 独有 → 不许被无提示删掉
    var graveD14 = Path.Combine(root, "mv", "copyleft");
    var rD14 = DoMove(mvA, mvB, graveD14);
    Check("D14 改名优先：源整棵落到目标；dest 里独有的条目先挪进隔离区而不是被删",
        rD14 == "Renamed" && !Directory.Exists(mvA) && HasModsFile(mvBRoot, "Keep Me")
        && Directory.Exists(Path.Combine(graveD14, "OnlyInDest")),
        "结果=" + rD14 + " 隔离区=" + (Directory.Exists(graveD14)
            ? string.Join(",", Directory.GetDirectories(graveD14).Select(Path.GetFileName)) : "(无)")
            + " 目标 Mods=" + ModsCount(mvBRoot));

    var vSrc = Path.Combine(root, "mv", "vsrc");
    var vDst = Path.Combine(root, "mv", "vdst");
    Directory.CreateDirectory(vSrc); Directory.CreateDirectory(vDst);
    File.WriteAllText(Path.Combine(vSrc, "one.txt"), "aaaa");
    File.WriteAllText(Path.Combine(vSrc, "two.txt"), "bbbb");
    File.WriteAllText(Path.Combine(vDst, "one.txt"), "zz");        // 大小不符
    var badD14 = (int)typeof(DepotDownloaderService).GetMethod("CountCopyMismatches",
        BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { vSrc, vDst })!;
    Check("D14b 核对口径：目标缺 1 个 + 大小不符 1 个 → 报 2 项不一致", badD14 == 2, "bad=" + badD14);

    var lkRoot = Path.Combine(root, "mv", "lksrc");
    var lkSrc = Path.Combine(lkRoot, "Mods");
    var lkDst = Path.Combine(root, "mv", "lkdst", "Mods");
    MkMods(lkRoot, "Locked Pack");
    var rLk = "(没跑)";
    using (new FileStream(Path.Combine(lkSrc, "Locked Pack", "manifest.json"),
               FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        rLk = DoMove(lkSrc, lkDst, null);
    Check("D14c 移动没通过核对时源必须还在（宁可双份，也不让残缺副本冒充唯一权威）",
        rLk == "Failed" && Directory.Exists(Path.Combine(lkSrc, "Locked Pack")),
        "结果=" + rLk + " 源还在=" + Directory.Exists(Path.Combine(lkSrc, "Locked Pack")));

    // 只剩回收站的 Mods 不该生成一个 0 项批次（23:27 那个 Mods-20260919_232740 就是这么来的）
    var trashOnlyMods = Path.Combine(root, "mv", "trashonly", "Mods");
    var trashOnly = Path.Combine(trashOnlyMods, ".junigrid_trash", "Gone");
    Directory.CreateDirectory(trashOnly);
    File.WriteAllText(Path.Combine(trashOnly, "x.txt"), "x");
    var movableMi = typeof(DepotDownloaderService).GetMethod("HasMovableEntries",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    Check("D14d 只剩回收站的 Mods 判定为「没东西可移」→ 不再生成空批次目录",
        !(bool)movableMi.Invoke(null, new object[] { trashOnlyMods })!,
        "trash-only 目录被判成「有东西可移」= 会建一个 0 项空桶");
    Directory.CreateDirectory(Path.Combine(trashOnlyMods, "Real Pack"));
    Check("D14e 加了真条目后判定为可移动（别把该搬的漏掉）",
        (bool)movableMi.Invoke(null, new object[] { trashOnlyMods })!,
        "含 .junigrid_trash + 真条目的目录被判定为「没东西可移」");

    // ── D15 立绘扫描签名要对「纯 PNG 素材包」的增删敏感 ──
    // 旧签名只哈希 manifest/content/config.json，而 Portraiture 素材包一个 json 都没有 →
    // 手放/手删 PNG 完全不动签名，立绘页长时间吃旧快照（2026-09-19 实测：TP's Emily 搬进搬出界面不变）
    var sigMi = typeof(PortraitSkinService).GetMethod("ModsSignature",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var sigA = Path.Combine(root, "sig", "Mods");
    Directory.CreateDirectory(sigA);
    File.WriteAllText(Path.Combine(sigA, "manifest.json"), "{}");
    var sigBase = (string)sigMi.Invoke(null, new object[] { sigA })!;
    File.WriteAllText(Path.Combine(sigA, "Emily.png"), "AAAA");
    var sigPng = (string)sigMi.Invoke(null, new object[] { sigA })!;
    Directory.CreateDirectory(Path.Combine(sigA, "TP Pack"));
    File.WriteAllText(Path.Combine(sigA, "TP Pack", "Emily.png"), "BBBBBBBB");
    var sigPack = (string)sigMi.Invoke(null, new object[] { sigA })!;
    Directory.Delete(Path.Combine(sigA, "TP Pack"), true);
    var sigBack = (string)sigMi.Invoke(null, new object[] { sigA })!;
    Check("D15 纯 PNG 素材包的新增/删除都会改变立绘扫描签名（不再吃旧快照）",
        sigBase.Length == 40 && sigPng != sigBase && sigPack != sigPng && sigBack == sigPng,
        "加图变=" + (sigPng != sigBase) + " ‖ 加素材包变=" + (sigPack != sigPng)
        + " ‖ 删素材包精确回到上一步=" + (sigBack == sigPng));

    // ── D16 版本切换期间立绘扫描要让路 ──
    // 我们自己就是 Mods 目录最大的读者；扫描/预热开着文件句柄时 Directory.Move 会失败，
    // 切换就退化成整棵拷贝（实测一次 4.3 秒、两趟全拷贝，还留下删不掉的旧目录）。
    var gateDir = Path.Combine(root, "gate");
    Directory.CreateDirectory(Path.Combine(gateDir, "Mods", "JGTest Gate Pack"));
    File.WriteAllText(Path.Combine(gateDir, "Mods", "JGTest Gate Pack", "manifest.json"),
        """{"Name":"JGTest Gate Pack","UniqueID":"JuniGrid.Test.Gate","Version":"1.0.0"}""");
    var gateDone = new int[1];
    PortraitSkinService.SuspendReads();
    var gateThread = new Thread(() =>
    {
        new PortraitSkinService(new ModService(), cfgSvc).Scan(gateDir);
        gateDone[0] = 1;
    });
    gateThread.Start();
    Thread.Sleep(900);
    var blockedWhileSuspended = gateDone[0] == 0;
    PortraitSkinService.ResumeReads();
    gateThread.Join(20000);
    Check("D16 切换挂起期间立绘扫描排队等，恢复后立刻跑完（并有 30 秒兜底不会锁死）",
        blockedWhileSuspended && gateDone[0] == 1,
        "挂起时未开跑=" + blockedWhileSuspended + " ‖ 恢复后跑完=" + (gateDone[0] == 1));

    // ── D18 标题不能把引擎内部号直接甩给用户 ──
    // 原先只有「上次切换记录」命中时才显示 1.0；记录一失效（重启、Steam 校验改写内部号）
    // 标题就掉回 1.0.5900（用户："有时候版本识别还是这样"）。兜底走内部号→版本族映射，不靠记录。
    var dispA = UpdateService.DisplayGameVersion("1.0.5900", "", "");
    var dispB = UpdateService.DisplayGameVersion("1.3.7269", "", "");
    var dispC = UpdateService.DisplayGameVersion("1.6.15", "", "");
    var dispD = UpdateService.DisplayGameVersion("1.0.5900", "1.0", "1.0.5900");
    Check("D18 无切换记录时也显示「1.0 / 1.4」而不是内部号 1.0.5900 / 1.3.7269（正常号原样）",
        dispA == "1.0" && dispB == "1.4" && dispC == "1.6.15" && dispD == "1.0",
        $"1.0.5900→{dispA} ‖ 1.3.7269→{dispB} ‖ 1.6.15→{dispC} ‖ 有记录→{dispD}");

    // ── D19 失败提示要按"实际断在哪一段"说，别一律甩锅 CM ──
    // 实测：登录一路成功（"授权成功""已连上 Steam"都打了），断的是 CDN 分片，
    // 旧文案却回"CM 握手失败 + 三条 Clash 建议"，把人往错方向带。
    var hintMi = typeof(DepotDownloaderService).GetMethod("NetworkHint",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var cdnHint = (string)hintMi.Invoke(null,
        new object?[] { "Encountered unexpected error downloading chunk bbf299dc: An error occurred while sending the request." })!;
    var cmHint = (string)hintMi.Invoke(null, new object?[] { "Connection to Steam failed. Trying again (#3)..." })!;
    // 真机 09-21 03:44 漏的那一类：下载跑到 90% 才报「Failed to find any server with chunk …」，
    // 前面几千行 Validating 把早先的超时行挤出判定窗口 → 被甩锅成 CM 连不上。
    var chunkHint = (string)hintMi.Invoke(null,
        new object?[] { "Failed to find any server with chunk 59753556f471c5bf1dfef46806cb02cf87590c5c for depot 413151. Aborting." })!;
    Check("D19 失败提示按实际断点分类：CDN 分片失败不再教人改 Clash 的 CM 分流",
        cdnHint.Contains("CDN") && cdnHint.Contains("改 Clash 的 CM 分流没用") && !cdnHint.Contains("走 DIRECT")
        && chunkHint.Contains("CDN") && !chunkHint.Contains("走 DIRECT")
        && cmHint.Contains("CM") && cmHint.Contains("DIRECT"),
        "分片失败→" + cdnHint + " ‖ 找不到分片→" + chunkHint + " ‖ 握手失败→" + cmHint);

    // ── D20 Steam 会话闸：3 路并行，第 4 路排队 ──
    // 更正记录：先前这里钉的是"一次只许一路"，依据是两路并发 4/4 互踢。09-21 重测发现那是
    // 两个 DepotDownloader 实例用了同一个 LogonID（Steam 当成同一个客户端，后登录顶掉前一个）。
    // 每路发唯一 -loginid 后两路并行 2 分多钟零掉线 → 闸门放宽到 3，本用例跟着改判据。
    var sess1 = DepotDownloaderService.HoldSteamSessionAsync().GetAwaiter().GetResult();
    var sessA = DepotDownloaderService.HoldSteamSessionAsync().GetAwaiter().GetResult();
    var sessB = DepotDownloaderService.HoldSteamSessionAsync().GetAwaiter().GetResult();
    var busyWhileHeld = DepotDownloaderService.SteamSessionBusy;
    var threeInFlight = DepotDownloaderService.SteamSessionCount == 3;
    bool fourthBlocked;
    using (var wait2 = new CancellationTokenSource(400))
    {
        try { DepotDownloaderService.HoldSteamSessionAsync(wait2.Token).GetAwaiter().GetResult(); fourthBlocked = false; }
        catch (OperationCanceledException) { fourthBlocked = true; }
    }
    sess1.Dispose();
    bool fourthPassed = false, releasedClean = false;
    var sess3 = DepotDownloaderService.HoldSteamSessionAsync().GetAwaiter().GetResult();
    fourthPassed = DepotDownloaderService.SteamSessionCount == 3;
    sess3.Dispose(); sessA.Dispose(); sessB.Dispose();
    releasedClean = !DepotDownloaderService.SteamSessionBusy;
    Check("D20 会话闸：3 路并行时第 4 路被挡，撒手后立刻进得去且计数归零",
        busyWhileHeld && threeInFlight && fourthBlocked && fourthPassed && releasedClean,
        "持有中busy=" + busyWhileHeld + " ‖ 三路在飞=" + threeInFlight
        + " ‖ 第四路被挡=" + fourthBlocked + " ‖ 撒手后又进得去=" + fourthPassed
        + " ‖ 全撒手归零=" + releasedClean);

    // ── D20b 每一路必须拿到互不相同的 -loginid（相同就会被 Steam 当成重复登录而互踢）──
    var lid1 = DepotDownloaderService.NextLoginId();
    var lid2 = DepotDownloaderService.NextLoginId();
    var lid3 = DepotDownloaderService.NextLoginId();
    Check("D20b 每路 loginid 互不相同",
        lid1 != lid2 && lid2 != lid3 && lid1 != lid3,
        lid1 + " / " + lid2 + " / " + lid3);

    // ── D21 关客户端不该把"没下完的版本下载"判成失败 ──
    // 用户 2026-09-20 排了 47 个版本下载，关掉客户端再开全部变成「失败 · 0%」——
    // 但版本下载是能重发起的（任务标题带着 manifest），恢复时应标「已暂停」让他点得到「继续」。
    var rsVer = TaskCenterService.RestoreStatus("gameversion", "running");
    var rsOther = TaskCenterService.RestoreStatus("install", "running");
    var rsDone = TaskCenterService.RestoreStatus("gameversion", "done");
    Check("D21 恢复：版本下载中断标「已暂停」可继续；其它 kind 仍标失败；已完成的原样",
        rsVer == "paused" && rsOther == "failed" && rsDone == "done",
        "版本下载→" + rsVer + " ‖ 其它→" + rsOther + " ‖ 已完成→" + rsDone);

    // ── D24 孤儿区自动归位：判得出唯一归属才搬，判不出的一律不动，全程只移动不删除 ──
    // 用户 2026-09-21 问"mod 不是都进版本抽屉了吗，为什么还有 mods 缓存"——_mods-orphan 是
    // 归属失败的收件箱，攒着不管就是死空间。这里验四条分支各走对各的：
    // 归位 / 重复件回收 / 无候选留下 / 同档多候选留下。
    var orph24 = Path.Combine(stageRoot, "_mods-orphan");
    string MkOrphanMod(string batch, string modName, string smapiVer)
    {
        var d = Path.Combine(orph24, batch, "Mods", modName);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "manifest.json"),
            "{\"Name\":\"" + modName + "\",\"UniqueID\":\"JGTest." + modName + "\",\"Version\":\"1.0.0\",\"MinimumApiVersion\":\"" + smapiVer + "\"}");
        File.WriteAllText(Path.Combine(d, "marker.txt"), modName);
        return d;
    }
    var drawerModern = Path.Combine(stageRoot, "jg-d24-packA", "Mods");
    var drawerModern2 = Path.Combine(stageRoot, "jg-d24-packB", "Mods");
    var liveDup24 = Path.Combine(game, "Mods", "DupMod");
    Directory.CreateDirectory(liveDup24);
    File.WriteAllText(Path.Combine(liveDup24, "manifest.json"),
        """{"Name":"DupMod","UniqueID":"JGTest.DupMod","Version":"1.0.0","MinimumApiVersion":"4.0.0"}""");
    var mHome = MkOrphanMod("b1", "HomeMod", "4.0.0");
    var mBase = MkOrphanMod("b2", "ConsoleCommands", "4.0.0");
    var mOld = MkOrphanMod("b3", "OldMod", "3.2.0");
    var mDup = MkOrphanMod("b4", "DupMod", "4.0.0");
    var sum24 = DepotDownloaderService.RehomeOrphanMods(game,
        new Dictionary<int, List<string>> { [4] = new() { drawerModern } });
    // 歧义那条要在第一轮之后才造：否则"唯一候选"那轮会把它一起归位掉
    var mAmb = MkOrphanMod("b5", "AmbMod", "4.0.0");
    var amb24 = DepotDownloaderService.RehomeOrphanMods(game,
        new Dictionary<int, List<string>> { [4] = new() { drawerModern, drawerModern2 } });
    var trash24 = Path.Combine(game, "Mods", ".junigrid_trash");
    var trashedNames = Directory.Exists(trash24)
        ? Directory.EnumerateDirectories(trash24).Select(Path.GetFileName).ToList() : new List<string?>();
    Check("D24 孤儿归位：唯一候选才搬回抽屉；重复件与 SMAPI 自带件进回收站；无候选/多候选一律留下",
        File.Exists(Path.Combine(drawerModern, "HomeMod", "marker.txt")) && !Directory.Exists(mHome)
        && !Directory.Exists(mBase) && trashedNames.Any(n => n?.StartsWith("ConsoleCommands-orphan-") == true)
        && Directory.Exists(mOld) && Directory.Exists(mAmb)
        && !Directory.Exists(mDup) && trashedNames.Any(n => n?.StartsWith("DupMod-orphan-") == true)
        && sum24 is not null && amb24 is null,
        "摘要=" + (sum24 ?? "(null)") + " ‖ 多候选那轮=" + (amb24 ?? "(null=没动东西，符合预期)")
        + " ‖ 回收站=" + string.Join("/", trashedNames) + " ‖ 留下 old=" + Directory.Exists(mOld) + " amb=" + Directory.Exists(mAmb));

    // ── D25 官方原版用户第一次切走：当前本体整份收进它自己的版本包 ──
    // 以前 ② 直接把旧本体删掉，想回官方最新版只能回 Steam 校验（用户的原话是
    // "不应该上来就把用户下载的内容自动缓存起来吗"）。
    // 夹具注意：本体模板的 dll 读出来就是 1.6.15，所以「当前版本」必须用一个和它
    // 不同的号（1.5.5），否则 FindStagingByVersionLabel 会把目标包认成当前版本。
    var adoptAcf = Path.Combine(root, "steamapps");          // FindAppManifest 从 game 逐级上溯找这里
    Directory.CreateDirectory(adoptAcf);
    File.WriteAllText(Path.Combine(adoptAcf, "appmanifest_413150.acf"),
        "\"AppState\"\n{\n\t\"appid\"\t\t\"413150\"\n\t\"InstalledDepots\"\n\t{\n\t\t\"413151\"\n\t\t{\n"
        + "\t\t\t\"manifest\"\t\t\"1234567890123456789\"\n\t\t}\n\t}\n}\n");
    var adopted = Path.Combine(stageRoot, "413150-413151-1.5.5");
    try { Directory.Delete(adopted, true); } catch { }
    try { Directory.Delete(Path.Combine(pack, "Mods"), true); } catch { }
    MkBody(pack);
    MkBody(game);
    MkMods(game, "Legacy Mod A", "Legacy Mod B");
    DoApply(pack, "1.5.5");
    Check("D25 切走时把当前本体收进版本包：本体+Mods+manifest 元数据一次到位",
        lastError is null
        && File.Exists(Path.Combine(adopted, "Stardew Valley.dll"))
        && HasModsFile(adopted, "Legacy Mod A") && HasModsFile(adopted, "Legacy Mod B")
        && File.ReadAllText(Path.Combine(adopted, ".junigrid-manifest")).Trim() == "1234567890123456789"
        && File.Exists(Path.Combine(game, "Stardew Valley.dll"))
        && !HasModsFile(game, "Legacy Mod A"),
        "包内 dll=" + File.Exists(Path.Combine(adopted, "Stardew Valley.dll"))
        + " ‖ manifest=" + (File.Exists(Path.Combine(adopted, ".junigrid-manifest"))
            ? File.ReadAllText(Path.Combine(adopted, ".junigrid-manifest")).Trim() : "(无)")
        + " ‖ 归档包 Mods=" + ModsCount(adopted) + " ‖ 游戏 Mods=" + ModsCount(game)
        + (lastError is null ? "" : " ‖ 异常=" + lastError));

    // ── D26 从那份归档切回来：全程不联网，本体和那两个 mod 原样回来 ──
    DoApply(adopted, "1.6.15");
    Check("D26 从归档包切回原版本：本体与那 2 个 mod 原样回来（不重复归档）",
        lastError is null && HasModsFile(game, "Legacy Mod A") && HasModsFile(game, "Legacy Mod B")
        && File.Exists(Path.Combine(game, "Stardew Valley.dll"))
        && !File.Exists(Path.Combine(game, ".junigrid-manifest")),
        "游戏 Mods=" + ModsCount(game) + " ‖ 归档包 Mods 剩=" + ModsCount(adopted)
        + (lastError is null ? "" : " ‖ 异常=" + lastError));

    // ── D27 续传判据：只有「同一个 manifest 且已有本体」的半截包才留着接着下 ──
    // 治的是"每次重试都从 0 开始、永远卡在同一段尾部 chunk"（国内到 Steam CDN 成片超时）。
    var rsDir = Path.Combine(stageRoot, "413150-413151-resume-me");
    try { Directory.Delete(rsDir, true); } catch { }
    Directory.CreateDirectory(rsDir);
    File.WriteAllText(Path.Combine(rsDir, "Stardew Valley.dll"), "body");
    File.WriteAllText(Path.Combine(rsDir, ".junigrid-manifest"), "1234567890123456789\n");
    var d27Ok = DepotDownloaderService.CanResumeStagedPackage(rsDir, "1234567890123456789");
    var d27Other = DepotDownloaderService.CanResumeStagedPackage(rsDir, "999888777666555444");
    var d27Shell = DepotDownloaderService.CanResumeStagedPackage(
        Path.Combine(stageRoot, "413150-413151-no-such"), "1234567890123456789");
    File.Delete(Path.Combine(rsDir, "Stardew Valley.dll"));
    var d27NoBody = DepotDownloaderService.CanResumeStagedPackage(rsDir, "1234567890123456789");
    Check("D27 续传判据：同 manifest+有本体才续；换 manifest / 空壳 / 目录不存在一律清空重下",
        d27Ok && !d27Other && !d27NoBody && !d27Shell,
        "同 manifest=" + d27Ok + " ‖ 换 manifest=" + d27Other + " ‖ 没本体=" + d27NoBody + " ‖ 无目录=" + d27Shell);
    try { Directory.Delete(rsDir, true); } catch { }

    // ── D28 版本识别：XNA 时代的 exe 自报 1.0.61xx，得认「我们把它铺成哪个版本」的记录 ──
    // 真机 09-21 18:19：切到 1.2.x 后日志写「游戏 1.0.6124 无适配 SMAPI，切换后不装加载器」，
    // 界面显示未定位 —— 因为 1.2.19/1.2.26/1.2.30 的 exe 版本资源不跟发行号走。
    var verDir = Path.Combine(root, "ver-probe");
    try { Directory.Delete(verDir, true); } catch { }
    MkBody(verDir);
    var rawVer = UpdateService.ReadLocalGameVersion(verDir);
    UpdateService.RecordDeployedVersion(verDir, "1.2.26", "7276192310056789702");
    var recVer = UpdateService.ResolveCurrentVersion(verDir);
    File.AppendAllText(Path.Combine(verDir, "Stardew Valley.dll"), "steam-touched");
    var afterVer = UpdateService.ResolveCurrentVersion(verDir);
    Check("D28 版本识别优先认部署记录；本体被换过后记录自动作废、退回读文件",
        recVer == "1.2.26" && afterVer == rawVer && !string.IsNullOrEmpty(rawVer) && afterVer != recVer,
        "文件自报=" + rawVer + " ‖ 有记录时=" + recVer + " ‖ 本体被改后=" + afterVer);
    try { Directory.Delete(verDir, true); } catch { }

    // ── D29 半截包不许铺进游戏目录（09-22 00:04 进档 NRE 闪退的根治）──
    // WER 实录：NullReferenceException at StardewValley.NPC..ctor ← Game1.loadForNewGame ← SaveGame
    // —— 分片超时留下的包能骗过 4 个锚点的校验，缺的是别的角色的贴图，切过去必崩。
    var halfDir = Path.Combine(stageRoot, "413150-413151-9900112233");
    try { Directory.Delete(halfDir, true); } catch { }
    MkBody(halfDir);
    var halfSeen = depot.ListStagedPackages().FirstOrDefault(x => x.ManifestId == "9900112233");
    var refused = depot.TryApplyStaged(game, "413150", "413151", "9900112233", null, null);
    var bodyBefore = File.Exists(Path.Combine(game, "Stardew Valley.dll"));
    File.WriteAllText(Path.Combine(halfDir, ".junigrid-complete"), DateTime.Now.ToString("O"));
    var fullSeen = depot.ListStagedPackages().FirstOrDefault(x => x.ManifestId == "9900112233");
    var accepted = depot.TryApplyStaged(game, "413150", "413151", "9900112233", null, null);
    Check("D29 没确认下完的包被拒切换；写了完成标记后同一个包才允许铺",
        halfSeen is { Complete: false } && !refused && fullSeen is { Complete: true } && accepted,
        "无标记 Complete=" + (halfSeen?.Complete.ToString() ?? "(没列出)") + " 被拒=" + !refused
        + " ‖ 有标记 Complete=" + (fullSeen?.Complete.ToString() ?? "(没列出)") + " 放行=" + accepted
        + " ‖ 拒绝时游戏本体在不在=" + bodyBefore);
    try { Directory.Delete(halfDir, true); } catch { }

    // ── D30 切版本只做「放回暂存 + 留底」，**不再按版本隐藏**（列表始终完整，防点错靠择档/聚焦）──
    // 沙箱里放两份档：A=1.6.8（切到 1.6.15 会被单向升上去 → 该留底）、B=1.7.0（1.6.15 读不了，但列表里必须还在）。
    var dSaves = SaveVersionService.SavesDirOverride!;
    Directory.CreateDirectory(dSaves);
    void MkSlot(string name, string ver)
    {
        var d = Path.Combine(dSaves, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SaveGameInfo"),
            "<SaveGameInfo><player><name>闸门农场主</name></player><farmName>" + name +
            "</farmName><gameVersion>" + ver + "</gameVersion></SaveGameInfo>");
        File.WriteAllText(Path.Combine(d, name), "档体");
    }
    MkSlot("闸门档A_900000001", "1.6.8");
    MkSlot("闸门档B_900000002", "1.7.0");
    var dSlotA = Path.Combine(dSaves, "闸门档A_900000001");
    var dSlotB = Path.Combine(dSaves, "闸门档B_900000002");
    var dHiddenB = Path.Combine(SaveVersionService.HiddenRootOf(pack), "闸门档B_900000002");
    // 现行语义（PrepareSavesFor）：切换只做「放回 + 收起读不了的」，**不做升级前留底**
    // （注释写明「用高版本存盘后回不去，后果用户自担」）。切到 1.6.15：
    // A=1.6.8 读得了 → 留在列表；B=1.7.0 读不了 → 收起。
    DoApply(pack, "1.6.15");
    Check("D30 切版本收起读不了的档（游戏列表只显示读得了的；不再自动留底）",
        Directory.Exists(dSlotA) && !Directory.Exists(dSlotB) && Directory.Exists(dHiddenB),
        "A 还在列表=" + Directory.Exists(dSlotA)
        + " ‖ B 收进抽屉=" + Directory.Exists(dHiddenB) + (lastError is null ? "" : " 异常=" + lastError));
    // 抽屉里先放一份档：切换开头必须放回，再按新目标收一遍。
    var dSlotC = Path.Combine(dSaves, "闸门档C_900000003");
    var dHiddenC = Path.Combine(SaveVersionService.HiddenRootOf(pack), "闸门档C_900000003");
    Directory.CreateDirectory(dHiddenC);
    File.WriteAllText(Path.Combine(dHiddenC, "SaveGameInfo"),
        "<SaveGameInfo><player><name>闸门农场主</name></player><farmName>闸门档C_900000003" +
        "</farmName><gameVersion>1.5.6</gameVersion></SaveGameInfo>");
    File.WriteAllText(Path.Combine(dHiddenC, "闸门档C_900000003"), "档体");
    DoApply(pack, "1.6.15");
    Check("D30b 下一次切换开头先全部放回、再按新目标收一遍",
        Directory.Exists(dSlotC) && !Directory.Exists(dHiddenC)
        && !Directory.Exists(dSlotB) && Directory.Exists(dHiddenB)
        && Directory.Exists(dSlotA),
        "C 放回并留下=" + Directory.Exists(dSlotC)
        + " ‖ B 又被收回=" + Directory.Exists(dHiddenB)
        + " ‖ A 还在=" + Directory.Exists(dSlotA));

    // ── D31 重下不能静默删掉版本缓存里的 Mods 抽屉（09-21 18:30 那次把 113 个 mod 包删了，日志零记录）──
    var orphanRoot = Path.Combine(stageRoot, "_mods-orphan");
    string[] OrphanBatches() => Directory.Exists(orphanRoot)
        ? Directory.GetDirectories(orphanRoot, "Mods-*", SearchOption.AllDirectories) : Array.Empty<string>();
    var before31 = OrphanBatches().Length;
    var wipeDir = Path.Combine(stageRoot, "413150-413151-1.6.99");
    MkMods(wipeDir, "Mod One", "Mod Two");
    Directory.CreateDirectory(Path.Combine(wipeDir, "Mods", ".junigrid_trash"));
    var preserveMi = typeof(DepotDownloaderService).GetMethod("PreserveStagedModsBeforeWipe",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    preserveMi.Invoke(null, new object[] { wipeDir });
    var after31 = OrphanBatches();
    var landed = after31.FirstOrDefault(b => Directory.Exists(Path.Combine(b, "Mod One")));
    Check("D31 重下前把该版本缓存里的 mod 整棵挪进孤儿区暂存（源侧清空、两份都在孤儿区）",
        after31.Length == before31 + 1 && landed is not null
        && Directory.Exists(Path.Combine(landed, "Mod Two"))
        && !Directory.Exists(Path.Combine(wipeDir, "Mods", "Mod One")),
        "孤儿区批次 " + before31 + " → " + after31.Length + (landed is null ? "（没找到落点）" : " 落点=" + landed));
    var wipeDir2 = Path.Combine(stageRoot, "413150-413151-1.6.98");
    Directory.CreateDirectory(Path.Combine(wipeDir2, "Mods", ".junigrid_trash"));
    preserveMi.Invoke(null, new object[] { wipeDir2 });
    Check("D31b 抽屉里只剩回收站时不建空批次目录", OrphanBatches().Length == after31.Length,
        "批次数仍=" + OrphanBatches().Length);

    // ═══ E. 四刀根因修复的回归用例 ═══
    bool HasModHere(string parent, string n) => File.Exists(Path.Combine(parent, "Mods", n, "manifest.json"));
    var guardFn = typeof(UpdateService).GetMethod("GuardSmapiVersionMatchesGame",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var tryVerFn = typeof(DepotDownloaderService).GetMethod("TryReadGameVersion",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    string? GateErr(string dir, string? installVer)
    {
        try { guardFn.Invoke(null, new object?[] { dir, installVer }); return null; }
        catch (TargetInvocationException ex) { return ex.InnerException?.Message ?? ex.Message; }
    }
    string MkVerDir(string name, string srcBody)
    {
        var d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        File.Copy(srcBody, Path.Combine(d, Path.GetFileName(srcBody)), true);
        return d;
    }
    var packRoot = @"E:\junigrid\depot-staging";
    // 本体模板可能已经被用户从「游戏版本缓存」里删干净（版本号取自文件版本资源，伪造不了）。
    // 缺哪个就点名 SKIP 哪个 —— 绝不当通过，也不让整个套件崩在这里。
    string? MkVerDirOpt(string name, string srcBody) => File.Exists(srcBody) ? MkVerDir(name, srcBody) : null;
    bool TemplatesOk(params (string? Dir, string What)[] need)
    {
        var miss = need.Where(n => n.Dir is null).Select(n => n.What).ToList();
        if (miss.Count == 0) return true;
        Note("跳过（缺本体模板：" + string.Join("、", miss) + "）", "在版本管理里重新下载该版本后即可跑");
        return false;
    }
    var v1615 = MkVerDirOpt("body-1615", tmplDll);          // 用实际选中的模板，不硬指 1.6.15
    var v14 = MkVerDirOpt("body-14", Path.Combine(packRoot, "413150-413151-1.4", "Stardew Valley.exe"));
    // 1.01 与 1.0 一样都属「没有适配 SMAPI」的那一档，缺 1.01 时用 1.0 顶替不影响判据
    var v101 = MkVerDirOpt("body-101", Path.Combine(packRoot, "413150-413151-1.01", "Stardew Valley.exe"))
               ?? MkVerDirOpt("body-10", Path.Combine(packRoot, "413150-413151-1.0", "Stardew Valley.exe"));

    // E1 SMAPI 安装闸门（「1.01 上被装 4.5.2」那次的根治）
    if (TemplatesOk((v101, "1.01/1.0"), (v14, "1.4"), (v1615, "1.6.15")))
    {
        var e1a = GateErr(v101!, "4.5.2");
        var e1b = GateErr(v14!, "4.5.2");
        var e1c = GateErr(v14!, "3.7.3");
        var e1d = GateErr(v1615!, "4.5.2");
        Check("E1 SMAPI 闸门：1.01 拒装 4.5.2、1.4 拒装 4.5.2 但放行 3.7.3、1.6.15 放行 4.5.2",
            e1a is not null && e1a.Contains("没有适配") && e1b is not null && e1b.Contains("不匹配")
            && e1c is null && e1d is null,
            "1.01+4.5.2 → " + (e1a ?? "放行") + " ‖ 1.4+4.5.2 → " + (e1b ?? "放行")
            + " ‖ 1.4+3.7.3 → " + (e1c ?? "放行") + " ‖ 1.6.15+4.5.2 → " + (e1d ?? "放行"));
        Note("E1b 已知宽松处：1.6+ 的 tag 恒为 latest，所以往 1.6.15 上装更旧的 SMAPI 不拦 —— "
            + "故意留的降级口子；必须拦的是「跨版本残留」那一类");
    }

    // E2 版本读取口径：ProductVersion 带逗号会污染查表与孤儿目录名
    if (TemplatesOk((v1615, "1.6.15"), (v14, "1.4"), (v101, "1.01/1.0")))
    {
        var tv1615 = (string?)tryVerFn.Invoke(null, new object[] { v1615! });
        var tv14 = (string?)tryVerFn.Invoke(null, new object[] { v14! });
        var tv101 = (string?)tryVerFn.Invoke(null, new object[] { v101! });
        Check("E2 TryReadGameVersion 与 UpdateService 口径一致（FileVersion 优先，不再吐逗号串）",
            tv1615 == tmplVer && tv14 == "1.3.7269" && (tv101 == "1.0.5900" || tv101 == "1.0.610"),
            tmplVer + " 包 → " + tv1615 + " ‖ 1.4 包 → " + tv14 + " ‖ 1.01/1.0 包 → " + tv101);
    }

    // E3 孤儿 Mods 按「同版本 + 更长构建号」前缀认领（历史上 4 段命名的目录永远回不来）
    var legacyOrphanMods = Path.Combine(stageRoot, "_mods-orphan", tmplVer + ".24356", "Mods");
    var legacyPack = Path.Combine(legacyOrphanMods, "Leftover Pack");
    Directory.CreateDirectory(legacyPack);
    File.WriteAllText(Path.Combine(legacyPack, "manifest.json"), "{\"Name\":\"Leftover Pack\"}");
    try { Directory.Delete(Path.Combine(pack, "Mods"), true); } catch { }
    MkBody(game);
    MkMods(game, "My Mod A");
    DoApply(pack, "1.4");          // 当前版本 1.4 在临时 staging 里没有对应目录 → 走孤儿恢复分支
    Check("E3 按 3 段版本能认领 4 段命名的孤儿目录（mod 不再一去不回）",
        HasModHere(game, "Leftover Pack"),
        "游戏 Mods 顶层=" + ModsCount(game) + " ‖ 孤儿源目录还在=" + Directory.Exists(legacyOrphanMods)
        + " ‖ 剩余条目=" + (Directory.Exists(legacyOrphanMods)
            ? Directory.GetFileSystemEntries(legacyOrphanMods).Length : 0));

    // E4 XNA 判定（P4 下沉后所有 apply 路径都会走它）
    Check("E4 XNA 需求判定：1.0–1.4 要装、1.5+ 不装",
        XnaRedistService.GameNeedsXna("1.0.5900") && XnaRedistService.GameNeedsXna("1.3.7269")
        && !XnaRedistService.GameNeedsXna("1.6.15") && !XnaRedistService.GameNeedsXna("1.5.5"),
        "本机 XNA 已装=" + XnaRedistService.IsInstalled() + "（已装时下沉的调用是 no-op，MSI 动作本身不进自动化）");

    // E5 本地留着「正是这条远端文件」的下载包 → 认领为已装（治 .nxm 路径漏写记录的历史遗留）
    // 必须用干净配置：真实配置里 1839/182306 早就有 ModFileLastDownload 记录，
    // 「无包也该提示」那一步会被真实记录判成「已拥有」→ 测试假失败（2026-09-19 实测）。
    var dlDir = StoragePaths.DownloadsDir;      // cacheRoot 已被指到临时区，不碰真实缓存
    Directory.CreateDirectory(dlDir);
    var cfgE5 = new JuniGridConfig { GamePath = cfgSvc.Current.GamePath };
    var mE5 = new ModEntry { Folder = "OhoDavi Anime", Version = "1.6.4", NexusModId = 1839 };
    var noZip = NexusUpdateTruth.ShouldShowUpdate(cfgE5, mE5, "1.6.7", 182306);
    var zipHere = Path.Combine(dlDir, "nxm-1839-182306.zip");
    File.WriteAllText(zipHere, "PK");
    var withZip = NexusUpdateTruth.ShouldShowUpdate(cfgE5, mE5, "1.6.7", 182306);
    try { File.Delete(zipHere); } catch { }
    var otherFileId = NexusUpdateTruth.ShouldShowUpdate(cfgE5, mE5, "1.6.7", 999999);
    Check("E5 有当前 fileId 的本地下载包 → 认领为已装；没有包/只有别的 fileId 的包 → 仍提示",
        noZip && !withZip && otherFileId,
        "无包→提示=" + noZip + " ‖ 有当前 fileId 包→提示=" + withZip
        + " ‖ 只有别的 fileId→提示=" + otherFileId);

    // ── D10 扫码提速的新旧策略 A/B 实测（成功率是网络属性，只测量不断言）──
    var tfm = new DirectoryInfo(AppContext.BaseDirectory).Name;
    var ddDir = Path.Combine(AppContext.BaseDirectory, "tools", "DepotDownloader");
    for (var up = new DirectoryInfo(AppContext.BaseDirectory); up is not null && !File.Exists(Path.Combine(ddDir, "DepotDownloader.dll")); up = up.Parent)
    {
        var cand = Path.Combine(up.FullName, "JuniGrid", "bin", "Debug", tfm, "tools", "DepotDownloader");
        if (File.Exists(Path.Combine(cand, "DepotDownloader.dll"))) ddDir = cand;
    }
    var ddDll = Path.Combine(ddDir, "DepotDownloader.dll");
    if (!File.Exists(ddDll)) Note("D10 找不到 DepotDownloader 插件，扫码提速 A/B 跳过");
    else
    {
        async Task<(bool got, int ms, int tries)> Race(string label, int perAttemptMs, int attempts)
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            for (var a = 1; a <= attempts; a++)
            {
                var psiD = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "DepotDownloader.dll -app 413150 -qr -remember-password -manifest-only -max-downloads 4",
                    WorkingDirectory = ddDir,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                var dd = Process.Start(psiD)!;
                var got = false;
                var deadline = perAttemptMs > 0 ? perAttemptMs : 70000;   // 旧策略：让 DD 自己退避到底
                var read = Task.Run(async () =>
                {
                    string? l;
                    while ((l = await dd.StandardOutput.ReadLineAsync()) is not null)
                        if (l.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase) || l.Contains('█'))
                        { got = true; break; }
                });
                await Task.WhenAny(read, Task.Delay(deadline));
                var hit = got;
                try { dd.Kill(entireProcessTree: true); } catch { }
                if (hit) return (true, (int)total.ElapsedMilliseconds, a);
            }
            return (false, (int)total.ElapsedMilliseconds, attempts);
        }

        var oldWay = await Race("旧", 0, 1);
        var newWay = await Race("新", 20000, 3);
        Note("D10 扫码出码 A/B —— 旧策略（单路放 DD 自己退避 10 轮） vs 新策略（20 秒没码就重开，最多 3 路）",
            $"旧：{(oldWay.got ? "出码 " + oldWay.ms + "ms" : "未出码 " + oldWay.ms + "ms")}   |   " +
            $"新：{(newWay.got ? "出码 " + newWay.ms + "ms（第 " + newWay.tries + " 路）" : "未出码 " + newWay.ms + "ms")}" +
            "（DD 连 Steam CM 单次成功率实测约 1/3，故单次结果有随机性）");
    }

    RestoreConfig();
    try { Directory.Delete(root, true); } catch { Console.WriteLine("沙箱保留供诊断: " + root); }
    Console.WriteLine($"\n════════ 总计：{pass} PASS / {fail} FAIL ═════════");
    Environment.Exit(fail == 0 ? 0 : 1);
}

// ═══════════════ S. 存档留底 / 按版本收起（沙箱，只碰临时目录） ═══════════════
// 只改 StoragePaths.CacheRoot（反射，不落配置文件）+ SaveVersionService.SavesDirOverride，
// 所以不需要像 D 段那样要求应用退出：真实 %APPDATA%\StardewValley\Saves 与 E:\junigrid 都不碰。
if (savesOnly)
{
    Console.WriteLine("\n────── S. 存档留底 / 按版本收起（沙箱） ──────");
    var sroot = Path.Combine(Path.GetTempPath(), "jg-saves-guard");
    try { if (Directory.Exists(sroot)) Directory.Delete(sroot, true); } catch { }
    var scache = Path.Combine(sroot, "cache");
    var sstage = Path.Combine(scache, "depot-staging");
    var ssaves = Path.Combine(sroot, "Saves");
    var setCacheS = typeof(StoragePaths).GetProperty("CacheRoot")!.GetSetMethod(nonPublic: true)!;
    var oldCacheS = StoragePaths.CacheRoot;
    setCacheS.Invoke(null, new object?[] { scache });
    SaveVersionService.SavesDirOverride = ssaves;
    SaveVersionService.DrawerRootOverride = Path.Combine(sroot, "saves-hidden");
    Directory.CreateDirectory(ssaves);
    var spkg = Path.Combine(sstage, "413150-413151-1.0");     // 「切到 1.0」时那个版本包
    Directory.CreateDirectory(spkg);
    var sBackup = Path.Combine(sstage, "_saves-backup");
    var slogs = new List<string>();
    void SLog(string m) { lock (slogs) slogs.Add(m); }

    void EndS()
    {
        setCacheS.Invoke(null, new object?[] { oldCacheS });
        SaveVersionService.SavesDirOverride = null;
        SaveVersionService.DrawerRootOverride = null;
        try { Directory.Delete(sroot, true); } catch { Console.WriteLine("沙箱保留供诊断: " + sroot); }
        Console.WriteLine($"\n════════ 总计：{pass} PASS / {fail} FAIL ═════════");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    Check("S0 存档目录与缓存都指到临时区（否则下面会动玩家的档）",
        SaveVersionService.SavesDir() == ssaves && SaveVersionService.BackupRoot == sBackup,
        "Saves=" + ssaves + "   留底=" + SaveVersionService.BackupRoot);
    if (SaveVersionService.SavesDir() != ssaves) EndS();

    var t0 = new DateTime(2026, 9, 20, 8, 0, 0);
    // 一份存档 = Saves 下一个目录（档体 + SaveGameInfo）。mtime 钉死：留底去重判的就是「大小 + 落笔时间」。
    void MkSave(string name, string farmer, string farm, string? ver, int minute, string body = "本体")
    {
        var d = Path.Combine(ssaves, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SaveGameInfo"),
            "<SaveGameInfo><player><name>" + farmer + "</name></player><farmName>" + farm + "</farmName>" +
            (ver is null ? "" : "<gameVersion>" + ver + "</gameVersion>") + "<mailReceived>hi</mailReceived></SaveGameInfo>");
        File.WriteAllText(Path.Combine(d, name), body + "|" + name);
        var when = t0.AddMinutes(minute);
        foreach (var f in Directory.GetFiles(d)) File.SetLastWriteTime(f, when);
    }
    void Touch(string name, string body)
    {
        var f = Path.Combine(ssaves, name, name);
        File.WriteAllText(f, body + "|" + name);
        File.SetLastWriteTime(f, new FileInfo(f).LastWriteTime.AddMinutes(20));
    }
    string Body(string name) => File.ReadAllText(Path.Combine(ssaves, name, name));
    SaveVersionService.Slot? Find(string name) => SaveVersionService.Scan().FirstOrDefault(x => x.Name == name);

    MkSave("鹈鹕_386431688", "镇委书记", "鹈鹕", "1.6.8", 0);
    MkSave("新手村_449687585", "1", "1", null, 1);          // 1.0 建的档：根本没有 gameVersion 字段
    MkSave("大号_428825770", "老玩家", "大号", "1.6.15", 2);
    Directory.CreateDirectory(Path.Combine(ssaves, "XIAO_332073160"));   // Steam 云留下的空壳

    var s1 = SaveVersionService.Scan();
    Check("S1 扫描读出农场名/农场主名/存档内版本，空壳目录不算一份档",
        s1.Count == 3 && Find("鹈鹕_386431688")!.FarmName == "鹈鹕"
        && Find("鹈鹕_386431688")!.FarmerName == "镇委书记" && Find("鹈鹕_386431688")!.GameVersion == "1.6.8"
        && Find("新手村_449687585")!.GameVersion is null,
        "扫到 " + s1.Count + " 份：" + string.Join("，", s1.Select(x => x.Name + "/" + (x.GameVersion ?? "无字段"))));

    Check("S2 读得了/读不了/会被升级 的口径（只有写着更高版本的才判死；无 SaveGameInfo 的残缺档不给旧版本；无字段的老格式不动）",
        !SaveVersionService.ReadableBy(Find("鹈鹕_386431688")!, "1.0")
        && SaveVersionService.ReadableBy(Find("鹈鹕_386431688")!, "1.6.15")
        && SaveVersionService.ReadableBy(Find("鹈鹕_386431688")!, "1.6.8")
        && SaveVersionService.ReadableBy(Find("新手村_449687585")!, "1.0")
        && SaveVersionService.UpgradedBy(Find("新手村_449687585")!, "1.6.15")
        && SaveVersionService.UpgradedBy(Find("鹈鹕_386431688")!, "1.6.15")
        && !SaveVersionService.UpgradedBy(Find("大号_428825770")!, "1.6.15")
        && !SaveVersionService.ReadableBy(
            new SaveVersionService.Slot("残缺", "残缺", null, null, null, 1, DateTime.MinValue, MetaComplete: false), "1.0")
        && SaveVersionService.ReadableBy(
            new SaveVersionService.Slot("残缺", "残缺", null, null, null, 1, DateTime.MinValue, MetaComplete: false), "1.6.15")
        && SaveVersionService.CompareVersions("1.6.8", "1.6.15") < 0
        // 以前这条写的是 == 0 —— 那是把「1.0.1 和 1.1 一样新」当预期，正是 1.0x/1.1x 判错的根。
        && SaveVersionService.CompareVersions("1.01", "1.1") < 0
        && SaveVersionService.CompareVersions("1.11", "1.2.30") < 0
        && SaveVersionService.CompareVersions("1.2.30", "1.2") > 0);

    var n3 = SaveVersionService.BackupAboutToUpgrade("1.6.15", SLog);
    Check("S3 切到 1.6.15 前只给「会被升上去」的档留底（无字段的 1.0 档 + 1.6.8 档；本身就是 1.6.15 的不留）",
        n3 == 2 && Directory.Exists(Path.Combine(sBackup, "鹈鹕_386431688"))
        && Directory.Exists(Path.Combine(sBackup, "新手村_449687585"))
        && !Directory.Exists(Path.Combine(sBackup, "大号_428825770")), "新建留底 " + n3 + " 份");
    Check("S3b 留底只是副本：存档原件一直留在存档目录里",
        Find("鹈鹕_386431688") is not null && File.Exists(Path.Combine(ssaves, "鹈鹕_386431688", "鹈鹕_386431688")));

    var stamps0 = SaveVersionService.StampsOf("鹈鹕_386431688").Count;
    var n4 = SaveVersionService.BackupAboutToUpgrade("1.6.15", SLog);
    Check("S4 内容没动过就不重复留底（一天切十次版本也不会堆出十份）",
        n4 == 0 && SaveVersionService.StampsOf("鹈鹕_386431688").Count == stamps0, "第二次调用新建 " + n4 + " 份");

    Touch("鹈鹕_386431688", "玩过一天");
    var n5 = SaveVersionService.BackupAboutToUpgrade("1.6.15", SLog);
    Check("S5 档真的改过才补一份新底",
        n5 == 1 && SaveVersionService.StampsOf("鹈鹕_386431688").Count == stamps0 + 1, "第三次调用新建 " + n5 + " 份");

    var halfDir = Path.Combine(sBackup, "新手村_449687585", "20200101_000000.tmp");
    Directory.CreateDirectory(halfDir);
    File.WriteAllText(Path.Combine(halfDir, "x"), "复制中断留下的半份");
    Check("S6 没有完成标记的半成品不算一份底",
        SaveVersionService.StampsOf("新手村_449687585").Count == 1);
    var n7 = SaveVersionService.BackupAboutToUpgrade(null, SLog);
    Check("S7 认不出要切去的版本号时整段不做（不猜、也不留一半底）",
        n7 == 0 && SaveVersionService.StampsOf("大号_428825770").Count == 0);

    for (var i = 0; i < 6; i++)
    {
        Touch("鹈鹕_386431688", "第" + i + "次改动");
        SaveVersionService.BackupAboutToUpgrade("1.6.15", SLog);
        Thread.Sleep(1100);   // 留底时间戳精确到秒，不给它撞在一起
    }
    var st8 = SaveVersionService.StampsOf("鹈鹕_386431688");
    Check("S8 同一份档最多留 3 份底，超了清最旧的（清的全是我们的副本）", st8.Count == 3, "实际 " + st8.Count + " 份");
    Check("S8b 半成品目录在下次留底时被顺手收掉", !Directory.Exists(halfDir));

    var h9 = SaveVersionService.HideUnreadable("1.0", spkg, SLog);
    Check("S9 切到 1.0 时只收走「档里写着 1.6.x」的，无字段的 1.0 档原地不动",
        h9 == 2 && Find("鹈鹕_386431688") is null && Find("大号_428825770") is null
        && Find("新手村_449687585") is not null
        && Directory.Exists(Path.Combine(SaveVersionService.HiddenRootOf(spkg), "鹈鹕_386431688")), "收走 " + h9 + " 份");
    Check("S10 收起来的档从存档列表里消失（点不到就不会闪退），抽屉里内容完整",
        SaveVersionService.Scan().Count == 1 && SaveVersionService.HiddenSaves().Count == 2
        && File.Exists(Path.Combine(SaveVersionService.HiddenRootOf(spkg), "鹈鹕_386431688", "鹈鹕_386431688")));
    var put11 = SaveVersionService.RestoreHidden(SLog);
    Check("S11 每次切换开头先把收起来的档放回列表，抽屉壳子跟着删掉",
        put11 == 2 && SaveVersionService.Scan().Count == 3 && !Directory.Exists(Path.Combine(SaveVersionService.HiddenRootOf(spkg))),
        "放回 " + put11 + " 份");

    MkSave("禺哥_354100331", "禺哥", "禺哥", "1.6.15", 3, "云端那份");
    SaveVersionService.HideUnreadable("1.0", spkg, SLog);
    var hiddenG = Path.Combine(SaveVersionService.HiddenRootOf(spkg), "禺哥_354100331", "禺哥_354100331");
    MkSave("禺哥_354100331", "禺哥", "禺哥", "1.6.15", 4, "云又同步回来的一份");   // Steam 云把同名档补回来了
    // 抽屉里这时收着 3 份（鹈鹕/大号/禺哥），只有禺哥被云补回了同名档 → 该放回的是另外 2 份
    var r12 = SaveVersionService.RestoreHidden(SLog);
    Check("S12 同名冲突当场裁决：更新的留在 Saves，旧的降级成留底，抽屉清空（不再永久压着导致闸门被跳过）",
        r12 == 2 && !Directory.Exists(Path.Combine(SaveVersionService.HiddenRootOf(spkg), "禺哥_354100331"))
        && Body("禺哥_354100331").Contains("云又同步回来")
        && SaveVersionService.StampsOf("禺哥_354100331").Count >= 1
        && slogs.Any(m => m.Contains("同名冲突")), "放回 " + r12 + " 份");

    var before13 = SaveVersionService.StampsOf("鹈鹕_386431688").Count;
    Touch("鹈鹕_386431688", "现在的样子");
    var err13 = SaveVersionService.Restore("鹈鹕_386431688", SaveVersionService.StampsOf("鹈鹕_386431688")[0].Dir, SLog);
    var st13 = SaveVersionService.StampsOf("鹈鹕_386431688");
    Check("S13 退回旧底：现有的那份先改名成一份新底，没有东西被覆盖或删掉",
        err13 is null && st13.Count == before13 + 1
        && st13.Count(x => Path.GetFileName(x.Dir).Contains("换下来的")) == 1
        && !Body("鹈鹕_386431688").Contains("现在的样子"),
        "结果 " + (err13 ?? "成功") + " ‖ 底数 " + before13 + "→" + st13.Count
        + " ‖ 换下来=" + st13.Count(x => Path.GetFileName(x.Dir).Contains("换下来的"))
        + " ‖ 正文含现在的样子=" + Body("鹈鹕_386431688").Contains("现在的样子")
        + " ‖ 留底名=" + string.Join(",", st13.Select(x => Path.GetFileName(x.Dir))));
    Check("S13b 放回存档目录的那份不带我们的内部标记文件",
        !File.Exists(Path.Combine(ssaves, "鹈鹕_386431688", ".jg-src")));
    Check("S13c 「换下来的」那份不参与自动回收（那是盘上只剩一份的状态）",
        st13.Any(x => Path.GetFileName(x.Dir).Contains("换下来的")));

    var err14 = SaveVersionService.Restore("新手村_449687585", ssaves, SLog);
    Check("S14 退回只认留底区里的路径，别的目录一律拒绝（不把存档指到任意目录）",
        err14 is not null && Directory.Exists(Path.Combine(ssaves, "新手村_449687585")), err14 ?? "(没报错)");
    Check("S15 版本号认不出时不收档", SaveVersionService.HideUnreadable(null, spkg, SLog) == 0);

    SaveVersionService.SavesDirOverride = Path.Combine(sroot, "压根没有这个目录");
    Check("S16 从没玩过游戏（没有存档目录）时三步全是空动作、不抛",
        SaveVersionService.Scan().Count == 0 && SaveVersionService.BackupAboutToUpgrade("1.6.15", SLog) == 0
        && SaveVersionService.RestoreHidden(SLog) == 0 && SaveVersionService.HideUnreadable("1.0", spkg, SLog) == 0);
    SaveVersionService.SavesDirOverride = ssaves;

    Check("S17 留底总占用与「几份档有底」报得出来（存储页那一行、界面数字都靠它）",
        SaveVersionService.BackupBytes() > 0 && SaveVersionService.BackedUpSaveCount() >= 2,
        SaveVersionService.BackupBytes() + " 字节 / " + SaveVersionService.BackedUpSaveCount() + " 份档");
    Note("S18 每个动作都往日志通道说了话（切换任务的明细靠它）", slogs.Count + " 条");

    // ── 云存档「下到一半」的判定（2026-09-22 02:26 那次超时的现场：目录在、只剩索引件、没有档体）──
    var hs = Path.Combine(sroot, "Saves2");
    SaveVersionService.SavesDirOverride = hs;
    Directory.CreateDirectory(hs);
    void MkHalf(string name, bool withBody)
    {
        var d = Path.Combine(hs, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SaveGameInfo"), "<SaveGameInfo><farmName>" + name + "</farmName></SaveGameInfo>");
        File.WriteAllText(Path.Combine(d, "SaveGameInfo_old"), "索引件旧副本");
        File.WriteAllText(Path.Combine(d, "spacecore-serialization.json"), "{}");
        if (withBody) File.WriteAllText(Path.Combine(d, name), "档体");
    }
    MkHalf("完好档_1", true);
    MkHalf("断在半路_2", false);
    Directory.CreateDirectory(Path.Combine(hs, "空壳档_3"));   // 云留下的空目录，不是半同步现场
    var junk = Path.Combine(hs, "不是存档");
    Directory.CreateDirectory(junk);
    File.WriteAllText(Path.Combine(junk, "readme.txt"), "x");  // 只有带扩展名的文件 → 不算存档槽位
    var half = SaveVersionService.HalfSyncedSaves();
    Check("S19 只剩索引件、没有档体的目录被判成「云存档下到一半」（完好档/空壳/非存档目录都不误报）",
        half.Count == 1 && half[0] == "断在半路_2", "判出来：" + string.Join("、", half));
    var desc = SaveVersionService.DescribeHalfSynced(half);
    Check("S20 拦启动那段话说清了后果与操作（会覆盖云端 + 让 Steam 完全退出重跑同步）",
        desc.Contains("断在半路_2") && desc.Contains("覆盖云端") && desc.Contains("完全退出"), desc.Replace("\n", " / "));
    var s21Ret = SaveVersionService.HideUnreadable("1.0", spkg, SLog);
    Check("S21 半同步的那几份不搬（避免搬走半份档）",
        Directory.Exists(Path.Combine(hs, "断在半路_2"))
        && !Directory.Exists(Path.Combine(SaveVersionService.HiddenRootOf(spkg), "断在半路_2")),
        $"返回={s21Ret} 半截档还在={Directory.Exists(Path.Combine(hs, "断在半路_2"))}");
    var blockMi = typeof(LauncherService).GetMethod("HalfSyncedBlock",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var first = blockMi.Invoke(null, null);
    var second = blockMi.Invoke(null, null);
    Check("S22 半同步现场第一次点启动被拦下，90 秒内再点一次放行（不给出口他会改用 Steam 直接开，更不可控）",
        first is not null && second is null
        && ((LaunchResult)first!).Error!.Contains("再点一次"),
        "第一次=" + (first is null ? "没拦" : "拦了") + " ‖ 第二次=" + (second is null ? "放行" : "还拦"));
    Check("S22b 横幅那句话比日志版短（不会被截断成半句）",
        SaveVersionService.DescribeHalfSyncedShort(half).Length < SaveVersionService.DescribeHalfSynced(half).Length,
        SaveVersionService.DescribeHalfSyncedShort(half).Length + " 字 vs " + SaveVersionService.DescribeHalfSynced(half).Length + " 字");
    // 抽屉里留着一份、Saves 里又出现同名的一份（= 云把它下载回来了）→ 两份都留着，开关不再自己停用
    var hs2 = Path.Combine(sroot, "Saves3");
    SaveVersionService.SavesDirOverride = hs2;
    Directory.CreateDirectory(hs2);
    void MkCloudBack()
    {
        var sd = Path.Combine(hs2, "被云拉回_5");
        Directory.CreateDirectory(sd);
        File.WriteAllText(Path.Combine(sd, "SaveGameInfo"),
            "<SaveGameInfo><player><name>老玩家</name></player><farmName>被云拉回</farmName><gameVersion>1.6.15</gameVersion></SaveGameInfo>");
        File.WriteAllText(Path.Combine(sd, "被云拉回_5"), "档体");
    }
    MkCloudBack();
    var pkg2 = Path.Combine(sstage, "413150-413151-1.0-b");
    Directory.CreateDirectory(pkg2);
    var h24 = SaveVersionService.HideUnreadable("1.0", pkg2, SLog);
    Directory.CreateDirectory(Path.Combine(hs2, "被云拉回_5"));           // 云只补回一个空壳
    var back24 = SaveVersionService.RestoreHidden(SLog);
    Check("S23 同名冲突当场裁决：有档体的那份回 Saves，空壳清掉，抽屉清空（不再永久压档）",
        h24 == 1
        && File.Exists(Path.Combine(hs2, "被云拉回_5", "被云拉回_5"))
        && !Directory.Exists(Path.Combine(SaveVersionService.HiddenRootOf(pkg2), "被云拉回_5"))
        && SaveVersionService.StashedCountAll() == 0,
        $"先收走 {h24} 份 → 裁决后暂存={SaveVersionService.StashedCountAll()} 档体在={File.Exists(Path.Combine(hs2, "被云拉回_5", "被云拉回_5"))}");
    // 同名冲突是云开着时的常态。Hide 再遇到抽屉已有同名时：Saves 那份进抽屉，抽屉原来那份降级成留底。
    MkCloudBack();
    SaveVersionService.HideUnreadable("1.0", pkg2, SLog);
    MkCloudBack();   // 云又补回一份完整的
    var stampsBefore24 = SaveVersionService.StampsOf("被云拉回_5").Count;
    var h24b = SaveVersionService.HideUnreadable("1.0", pkg2, SLog);
    var stamps24 = SaveVersionService.StampsOf("被云拉回_5");
    Check("S23b 抽屉里已有同名的一份时再收：Saves 那份进抽屉，抽屉原来那份降级成留底（没有一份被删）",
        h24b >= 1 && !Directory.Exists(Path.Combine(hs2, "被云拉回_5"))
        && File.Exists(Path.Combine(SaveVersionService.HiddenRootOf(pkg2), "被云拉回_5", "被云拉回_5"))
        && stamps24.Count == stampsBefore24 + 1
        && stamps24.Any(x => x.Note.Contains("抽屉里原来那份"))
        && slogs.Any(m => m.Contains("降级为留底")),
        $"又收走 {h24b} 份 → 「被云拉回_5」在 Saves 里还剩={(Directory.Exists(Path.Combine(hs2, "被云拉回_5")) ? "有" : "没有")}，" +
        $"留底 {stamps24.Count} 份（{(stamps24.Count > 0 ? stamps24[0].Note : "无")}）");

    // ── S24–S32：收档的云/半同步边界 + 启动前那道存档版本闸门 ──
    // 收档不再看云是开是关（移档对云端无损，见 S24 上面那段实测），只剩「半同步现场」这一条硬拦（S27）；
    // 读不了的档还留在列表里时，启动前必须把话说清楚并给出路（S30–S32，全程只读）。
    var gsaves = Path.Combine(sroot, "Saves4");
    SaveVersionService.SavesDirOverride = gsaves;
    Directory.CreateDirectory(gsaves);
    var gpkg = Path.Combine(sstage, "413150-413151-1.0-gate");
    Directory.CreateDirectory(gpkg);
    void GSave(string name, string farmer, string? ver)
    {
        var d = Path.Combine(gsaves, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SaveGameInfo"),
            "<SaveGameInfo><player><name>" + farmer + "</name></player><farmName>" + name.Split('_')[0] + "</farmName>"
            + (ver is null ? "" : "<gameVersion>" + ver + "</gameVersion>") + "</SaveGameInfo>");
        File.WriteAllText(Path.Combine(d, name), "档体|" + name);
    }
    bool GBody(string name) => File.Exists(Path.Combine(gsaves, name, name));
    GSave("鹈鹕_386431688", "镇委书记", "1.6.8");
    GSave("新手村_449687585", "1", null);          // 1.0 建的档：没有 gameVersion 字段 → 判不准就不动
    var gHidden = Path.Combine(SaveVersionService.HiddenRootOf(gpkg), "鹈鹕_386431688");

    // 云状态不再参与「能不能收档」的判定：实测 2026-09-22 04:57:45，本地缺 83 个存档文件时
    // Steam 的 up 同步选择「下载 83、上传 2」—— 移出去的档被判成该恢复，不是玩家删档；06:36:11 又复现一次。
    // 所以一次点击就动手，「看过说明再点一次」那道确认连同它叫玩家去关云的文案一起删掉
    // （理由是假的，留着就是一条假提示 + 催促）。
    slogs.Clear();
    var g24 = SaveVersionService.HideUnreadable("1.0", gpkg, SLog);
    Check("S24 云开着也照收：一次动手、没有第二次确认、日志不再叫玩家去关云；判不准的档一律不动",
        g24 == 1 && !GBody("鹈鹕_386431688") && Directory.Exists(gHidden)
        && File.Exists(Path.Combine(gHidden, "鹈鹕_386431688"))
        && GBody("新手村_449687585")                       // 1.0 建的档没有 gameVersion → 判不准就不动
        && !slogs.Any(m => m.Contains("取消勾选「Steam 云」"))
        && !slogs.Any(m => m.Contains("第二次点击")),
        $"收走={g24} 1.6.8 那份进抽屉={Directory.Exists(gHidden)} 判不准那份还在={GBody("新手村_449687585")}");

    var gHalf = Path.Combine(gsaves, "断在半路_9");
    Directory.CreateDirectory(gHalf);
    File.WriteAllText(Path.Combine(gHalf, "SaveGameInfo"),
        "<SaveGameInfo><player><name>x</name></player><farmName>断在半路</farmName><gameVersion>1.6.8</gameVersion></SaveGameInfo>");
    slogs.Clear();
    var g27 = SaveVersionService.HideUnreadable("1.0", gpkg, SLog);
    // 现行语义：半同步的残档也一并收起（留在列表只会被点到闪退，云会再下完整版）——
    // HideUnreadable 内对 half 只记日志、照样收走。S21 那条夹具没写 gameVersion，
    // 本就「读得了」所以根本进不了收起集合，验的是另一件事。
    Check("S27 半同步的残档也收起（不在游戏列表里等着被点闪退）",
        !Directory.Exists(gHalf)
        && Directory.Exists(Path.Combine(SaveVersionService.HiddenRootOf(gpkg), "断在半路_9")),
        $"返回={g27} 半截档还在Saves={Directory.Exists(gHalf)} 收进抽屉={Directory.Exists(Path.Combine(SaveVersionService.HiddenRootOf(gpkg), "断在半路_9"))}");
    try { if (Directory.Exists(gHalf)) Directory.Delete(gHalf, true); } catch { }

    // `Sync Disabled` 这行是真的会出现的：云关掉之后再经 Steam 启动一次游戏，cloud_log 里就有
    // `(AC Launch,Sync Disabled,)` 和 `(AC Exit,Sync Disabled,)`（2026-09-22 06:25:52 / 06:26:35 实测）。
    // 它只是滞后到「下次经 Steam 启动」，所以 sharedconfig.vdf 的 cloudenabled 排在它前面。
    // 我一度数出 0 次就断言「Steam 关云时不写任何一行」—— 那次观测没错、前提错了：数的时刻云还开着。
    var gLog = Path.Combine(sroot, "cloud_log.txt");
    File.WriteAllText(gLog,
        "[2026-09-22 04:02:11] [AppID 413150] Starting sync (AC Launch,Sync Disabled,)\n"
      + "[2026-09-22 04:05:00] [AppID 730] Starting sync (AC Launch,,)\n");
    var g28a = SteamService.CloudSyncFromLog(gLog, "413150");
    var g28b = SteamService.CloudSyncFromLog(gLog, "730");
    var g28c = SteamService.CloudSyncFromLog(gLog, "413151");
    File.AppendAllText(gLog, "[2026-09-22 09:00:00] [AppID 413150] Starting sync (AC Launch,,)\n");
    var g28d = SteamService.CloudSyncFromLog(gLog, "413150");
    Check("S28 云状态只认 Steam 自己的日志：最后一条本 app 的 Starting sync 说了算，别的 app 不串台，判不出就是判不出",
        g28a == SteamService.CloudSync.Disabled && g28b == SteamService.CloudSync.Enabled
        && g28c == SteamService.CloudSync.Unknown
        && SteamService.CloudSyncFromLog(Path.Combine(sroot, "没有这个.log"), "413150") == SteamService.CloudSync.Unknown
        && g28d == SteamService.CloudSync.Enabled,   // 玩家把云开回来之后必须立刻判准，否则闸门形同虚设
        $"413150={g28a} 730={g28b} 413151={g28c} 开回云后={g28d}");

    // ① 号判据：Steam 漫游配置 userdata\<账号>\7\remote\sharedconfig.vdf 里的 apps/<appId>/cloudenabled
    // 就是「属性 → 通用 → Steam 云」那个勾选，取消勾选立刻落盘（本机 2026-09-22 06:01 实测：
    // 文件 mtime 06:01，同一秒 cloud_log 出现 [AppID 7] Need to upload file sharedconfig.vdf）。
    // 这是唯一能证明「现在云是关的」的信号 —— 日志那条只能证明「那一刻在同步」。
    var cfgRoot = Path.Combine(sroot, "steamroot");
    string SharedFile(string acct, string body)
    {
        var dir = Path.Combine(cfgRoot, "userdata", acct, "7", "remote");
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "sharedconfig.vdf");
        File.WriteAllText(f, body);
        return f;
    }
    // 注释里故意塞引号和大括号，验解析器不会被带跑
    string Wrap(string apps) =>
        "\"UserRoamingConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n"
        + "\t\t\t\t\"SurveyDate\"\t\t\"2022-01-09\"\n"
        + "\t\t\t\t// 注释里也有 \"引号\" 和 { 大括号\n"
        + "\t\t\t\t\"apps\"\n\t\t\t\t{\n" + apps + "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";
    string App(string id, string val) =>
        $"\t\t\t\t\t\"{id}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"cloudenabled\"\t\t\"{val}\"\n\t\t\t\t\t}}\n";

    var f1 = SharedFile("111", Wrap(App("413150", "0")));
    var s33off = SteamService.CloudSyncFromSharedConfig(cfgRoot, "413150");
    File.WriteAllText(f1, Wrap(App("413150", "1")));
    var s33on = SteamService.CloudSyncFromSharedConfig(cfgRoot, "413150");
    File.WriteAllText(f1, Wrap(App("730", "1")));                       // 只有别的 app
    var s33other = SteamService.CloudSyncFromSharedConfig(cfgRoot, "413150");
    File.WriteAllText(f1, Wrap("\t\t\t\t\t\"413150\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"language\"\t\t\"english\"\n\t\t\t\t\t}\n"));
    var s33nokey = SteamService.CloudSyncFromSharedConfig(cfgRoot, "413150");
    File.WriteAllText(f1, Wrap(App("413150", "0")));
    SharedFile("222", Wrap(App("413150", "1")));                       // 两个账号：一个关一个开
    var s33two = SteamService.CloudSyncFromSharedConfig(cfgRoot, "413150");
    File.WriteAllText(f1, "\"413150\"\n{\n\t\"cloudenabled\"\t\t\"0\"\n}\n");   // 同名键在 apps 之外
    File.WriteAllText(Path.Combine(cfgRoot, "userdata", "222", "7", "remote", "sharedconfig.vdf"),
        "\"x\"\n{\n\t\"apps\"\n\t{\n\t}\n}\n");
    var s33outside = SteamService.CloudSyncFromSharedConfig(cfgRoot, "413150");
    Check("S33 云开关认 Steam 漫游配置里的 apps/<appId>/cloudenabled：0=关 1=开、没条目/没这个键=判不出、多账号往「开着」倒、apps 之外的同名键不认",
        s33off == SteamService.CloudSync.Disabled
        && s33on == SteamService.CloudSync.Enabled
        && s33other == SteamService.CloudSync.Unknown
        && s33nokey == SteamService.CloudSync.Unknown
        && s33two == SteamService.CloudSync.Enabled
        && s33outside == SteamService.CloudSync.Unknown
        && SteamService.CloudSyncFromSharedConfig(Path.Combine(sroot, "没有这个根"), "413150") == SteamService.CloudSync.Unknown
        && SteamService.CloudSyncFromSharedConfig(null, "413150") == SteamService.CloudSync.Unknown
        && SteamService.CloudSyncFromSharedConfig(cfgRoot, "") == SteamService.CloudSync.Unknown,
        $"关={s33off} 开={s33on} 只有别的app={s33other} 没这个键={s33nokey} 两账号={s33two} apps之外={s33outside}");

    GSave("鹈鹕_386431688", "镇委书记", "1.6.8");   // 云把刚收走的档又拉回来了
    var gGame = Path.Combine(sroot, "game10");
    Directory.CreateDirectory(gGame);
    File.WriteAllBytes(Path.Combine(gGame, "Stardew Valley.exe"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
    UpdateService.RecordDeployedVersion(gGame, "1.0", null);
    var gateMi = typeof(LauncherService).GetMethod("SaveCompatibilityBlock",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var fpFld = typeof(LauncherService).GetField("_saveGateFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;
    var wtFld = typeof(LauncherService).GetField("_saveGateWarnedAt", BindingFlags.NonPublic | BindingFlags.Static)!;
    LaunchResult? Gate(string? gp = null) => (LaunchResult?)gateMi.Invoke(null, new object?[] { gp ?? gGame });

    Check("S29 沙箱游戏目录能被判成 1.0（否则下面几条闸门用例全是空跑）",
        UpdateService.ResolveCurrentVersion(gGame) == "1.0"
        && SaveVersionService.UnreadableBy("1.0").Count == 1,
        "版本=" + (UpdateService.ResolveCurrentVersion(gGame) ?? "null")
        + " 读不了的档=" + SaveVersionService.UnreadableBy("1.0").Count + " 份");

    fpFld.SetValue(null, ""); wtFld.SetValue(null, DateTime.MinValue);
    var g30 = Gate();
    var g30b = Gate();
    Check("S30 读不了的档还在列表里 → 启动前拦一次；90 秒内再点放行（同一批档不反复问）",
        g30 is not null && !g30.Value.Success && g30.Value.GateKind == "saves" && g30b is null,
        "第一次=" + (g30 is null ? "没拦" : "拦了/" + g30.Value.GateKind)
        + " 第二次=" + (g30b is null ? "放行" : "还拦"));

    GSave("大号_428825770", "老玩家", "1.6.15");
    var g31 = Gate();
    Check("S30b 档的集合变了（又多一份读不了的）→ 重新拦一次",
        g31 is not null && g31.Value.GateKind == "saves"
        && g31.Value.Error!.Contains("2 份"),
        g31 is null ? "没拦" : g31.Value.Error!.Split('\n')[0]);

    Check("S31 拦下来那段话说清了两条出路（切到读得了的版本 / 仍然启动）和要切到哪个版本",
        g31!.Value.Error!.Contains("版本管理") && g31.Value.Error!.Contains("仍然启动")
        && g31.Value.Error!.Contains("1.6.15"),
        g31!.Value.Error!.Replace("\n", " / "));

    Check("S31a 这段走闸门通道（GateKind=\"saves\"）而不是错误通道 —— 错误通道会被 AccountPanel 截到 60 字，出口那半句就没了",
        g31.Value.GateKind == "saves" && g31.Value.Error!.Length > 60
        && SaveVersionService.DescribeUnreadable("1.0", SaveVersionService.UnreadableBy("1.0"))
               .Contains("NPC..ctor"),   // 日志那份留完整版：逐份列目录名 + 崩在哪
        "闸门文案 " + g31.Value.Error!.Length + " 字，日志版 "
        + SaveVersionService.DescribeUnreadable("1.0", SaveVersionService.UnreadableBy("1.0")).Length + " 字");

    Check("S31b 闸门全程只读：存档目录一份没少、一个文件都没动",
        Directory.GetDirectories(gsaves).Length == 3 && GBody("鹈鹕_386431688")
        && GBody("新手村_449687585") && GBody("大号_428825770"),
        "存档目录里有 " + Directory.GetDirectories(gsaves).Length + " 份");

    var gBad = Path.Combine(sroot, "game-none");
    Directory.CreateDirectory(gBad);
    fpFld.SetValue(null, ""); wtFld.SetValue(null, DateTime.MinValue);
    Check("S31c 认不出当前版本时不拦也不猜（宁可留一行会闪退的档，也不能凭猜测挡住启动）",
        UpdateService.ResolveCurrentVersion(gBad) is null && Gate(gBad) is null);

    var gEmpty = Path.Combine(sroot, "Saves5");
    Directory.CreateDirectory(gEmpty);
    SaveVersionService.SavesDirOverride = gEmpty;
    fpFld.SetValue(null, ""); wtFld.SetValue(null, DateTime.MinValue);
    Check("S32 列表里没有读不了的档 → 闸门一声不响放行（不打扰只玩一个版本的玩家）", Gate() is null);

    // ── S34–S38：启动路由（启动前收档 + 该不该叫 Steam 帮忙开）+ 版本新旧的判法 ──
    // 这一段是「低版本一点存档就闪退」的止血路径。闸门只负责拦和解释；真正消掉闪退的是
    // ①启动前那次收档 ②抽屉里压着档时自己开本体、不叫 Steam 开（Steam 开场那次同步会把收走的档全数拉回）
    // ③版本新旧必须判对 —— 判错了 ①② 根本不会被触发。
    SaveVersionService.SavesDirOverride = gsaves;
    var bypassMi = typeof(LauncherService).GetMethod("ShouldBypassSteam",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    bool Bypass(int stashed, SteamService.CloudSync cloud)
        => (bool)bypassMi.Invoke(null, new object?[] { stashed, cloud })!;
    Check("S34 判据是「抽屉里压着几份」这个持久量，不是「这次搬了没有」",
        Bypass(15, SteamService.CloudSync.Enabled) && Bypass(15, SteamService.CloudSync.Unknown)
        && Bypass(1, SteamService.CloudSync.Enabled)
        && !Bypass(15, SteamService.CloudSync.Disabled) && !Bypass(0, SteamService.CloudSync.Enabled)
        && !Bypass(0, SteamService.CloudSync.Unknown),
        "压着档 + 云开/判不出 = 自己开；压着档 + 云确认关 = 叫 Steam 开；抽屉空 = 一律叫 Steam 开");

    var prepMi = typeof(LauncherService).GetMethod("PrepareSavesForLaunch",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    int Prep(string gp) => (int)prepMi.Invoke(null, new object?[] { gp })!;
    var gFocus = SaveVersionService.FocusRootOf();
    void WipeDrawers()
    {
        try { if (Directory.Exists(gFocus)) Directory.Delete(gFocus, true); } catch { }
        if (Directory.Exists(sstage))
        {
            foreach (var dir in Directory.GetDirectories(sstage))
            {
                var h = SaveVersionService.HiddenRootOf(dir);
                try { if (Directory.Exists(h)) Directory.Delete(h, true); } catch { }
            }
        }
        // 新抽屉根（LocalAppData\saves-hidden 或沙箱覆写）整棵清掉
        try { if (Directory.Exists(SaveVersionService.DrawerRoot)) Directory.Delete(SaveVersionService.DrawerRoot, true); } catch { }
    }
    // 启动前收起：游戏列表只显示当前版本读得了的档
    SaveVersionService.RestoreHidden(SLog);
    WipeDrawers();
    foreach (var d in Directory.GetDirectories(gsaves))
    {
        try { Directory.Delete(d, true); } catch { }
    }
    GSave("鹈鹕_386431688", "镇委书记", "1.6.8");
    GSave("新手村_449687585", "1", null);
    GSave("大号_428825770", "老玩家", "1.6.15");
    fpFld.SetValue(null, ""); wtFld.SetValue(null, DateTime.MinValue);
    var s35 = Prep(gGame);   // gGame = 1.0
    Check("S35 启动前收起读不了的档：1.0 列表只剩老档，1.6.x 收进抽屉",
        s35 == 2 && GBody("新手村_449687585")
        && !GBody("鹈鹕_386431688") && !GBody("大号_428825770")
        && Directory.Exists(Path.Combine(SaveVersionService.HiddenRootOf(spkg), "鹈鹕_386431688")),
        $"收起后暂存={s35} 列表={SaveVersionService.Scan().Count} 份");

    var s36 = Prep(gGame);   // 第二次：先放回再收，状态稳定
    var g36gate = Gate();
    Check("S36 第二次启动：放回再收一遍，列表仍只剩读得了的，闸门不再拦",
        s36 == 2 && g36gate is null && GBody("新手村_449687585")
        && !GBody("鹈鹕_386431688"),
        $"暂存={s36} 闸门={(g36gate is null ? "放行" : "还拦")}");

    var gGame2 = Path.Combine(sroot, "game1230");
    Directory.CreateDirectory(gGame2);
    File.WriteAllBytes(Path.Combine(gGame2, "Stardew Valley.exe"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
    UpdateService.RecordDeployedVersion(gGame2, "1.2.30", null);
    fpFld.SetValue(null, ""); wtFld.SetValue(null, DateTime.MinValue);
    SaveVersionService.RestoreHidden(SLog);
    var s37 = Prep(gGame2);
    var g37gate = Gate(gGame2);
    Check("S37 没有版本包抽屉时收进聚焦抽屉，游戏列表干净，不再弹闸门",
        s37 >= 1 && !GBody("鹈鹕_386431688") && g37gate is null,
        $"暂存={s37} 闸门={(g37gate is null ? "放行（预期）" : "还拦/" + g37gate.Value.GateKind)}");

    // 拼接式分支名：Steam 那边 1.0.1 的分支叫 "1.01"、1.1.1 叫 "1.11"（表里显示名「1.1 修补」）、
    // 1.0.5.1 叫 "1.051"。按数字段比大小会得出 1.11 > 1.6 > 1.2，于是"用 1.11 启动"时
    // 1.2–1.6.15 的档全被判成读得了 → 一份都不收 → 点哪个都闪退。这正是玩家报的那条。
    var badLabels = new List<string>();
    var probe = new SaveVersionService.Slot("探针_1", "探针_1", "农", "探针", "1.6.15", 10, DateTime.MinValue);
    foreach (var lab in new[] { "1.01", "1.02", "1.03", "1.04", "1.05", "1.051", "1.051b", "1.06", "1.07", "1.11" })
        if (SaveVersionService.ReadableBy(probe, lab)) badLabels.Add(lab);
    Check("S38 版本新旧先换算拼接式分支名：一份 1.6.15 的档对这 10 个标签一律「读不了」",
        badLabels.Count == 0, badLabels.Count == 0 ? "10/10 判对" : "还判错：" + string.Join("、", badLabels));
    Check("S38b 相邻次序没被改坏：1.11>1.1、1.051b>1.051>1.05、1.07>1.06、1.01<1.1",
        SaveVersionService.CompareVersions("1.11", "1.1") > 0
        && SaveVersionService.CompareVersions("1.051b", "1.051") > 0
        && SaveVersionService.CompareVersions("1.051", "1.05") > 0
        && SaveVersionService.CompareVersions("1.07", "1.06") > 0
        && SaveVersionService.CompareVersions("1.01", "1.1") < 0
        && SaveVersionService.CompareVersions("1.0", "1.01") < 0,
        "1.0<1.01<…<1.05<1.051<1.051b<1.06<1.07<1.1<1.11<1.2.26");
    Check("S38c 表里没收的版本号（如 1.5.7）按段比仍然判得对",
        SaveVersionService.CompareVersions("1.5.7", "1.5.6") > 0
        && SaveVersionService.CompareVersions("1.5.7", "1.6") < 0
        && SaveVersionService.CompareVersions("1.5.7", "1.11") > 0,
        "1.5.7 vs 1.11 = " + SaveVersionService.CompareVersions("1.5.7", "1.11"));
    var rank111 = DepotDownloaderService.TryGetVersionRank("1.11", out var r111);
    var rank168 = DepotDownloaderService.TryGetVersionRank("1.6.8", out var r168);
    var rank1051b = DepotDownloaderService.TryGetVersionRank("1.051b", out var r1051b);
    Check("S38d 表内标签用表序（下标），不再拿分支名做数字段比较（1.11 绝不能 > 1.6）",
        rank111 && rank168 && rank1051b
        && r111 > r168
        && SaveVersionService.CompareVersions("1.11", "1.6.8") < 0
        && SaveVersionService.CompareVersions("1.6.8.0", "1.6.8") == 0
        && SaveVersionService.CompareVersions("1.11", "1.1") > 0,
        $"rank(1.11)={r111} rank(1.6.8)={r168} rank(1.051b)={r1051b}");

    // 聚焦暂存 + 同名云补回：RestoreHidden 绝不覆盖 Saves 里已有的那份，抽屉副本原地留着
    SaveVersionService.RestoreHidden(SLog);
    WipeDrawers();
    foreach (var d in Directory.GetDirectories(gsaves))
    {
        try { Directory.Delete(d, true); } catch { }
    }
    GSave("新手村_449687585", "1", null);
    GSave("大号_428825770", "老玩家", "1.6.15");
    GSave("禺哥_354100331", "禺哥", "1.6.15");
    File.WriteAllText(Path.Combine(gsaves, "禺哥_354100331", "禺哥_354100331"), "云端那份|禺哥_354100331");
    SaveVersionService.FocusHideExcept("新手村_449687585", SLog);
    var fHiddenG = Path.Combine(gFocus, "禺哥_354100331", "禺哥_354100331");
    GSave("禺哥_354100331", "禺哥", "1.6.15");
    File.WriteAllText(Path.Combine(gsaves, "禺哥_354100331", "禺哥_354100331"), "云又同步回来的一份|禺哥_354100331");
    var fBack = SaveVersionService.RestoreHidden(SLog);
    Check("S39 同名云补回后 RestoreHidden 当场裁决，抽屉不再永久压档",
        !File.Exists(fHiddenG)
        && GBody("禺哥_354100331")
        && File.ReadAllText(Path.Combine(gsaves, "禺哥_354100331", "禺哥_354100331")).Contains("云又同步回来")
        && GBody("大号_428825770")
        && SaveVersionService.StashedCountAll() == 0,
        $"放回={fBack} 抽屉那份还在={File.Exists(fHiddenG)} 暂存={SaveVersionService.StashedCountAll()}");

    // S40：人工隔离区（<staging>\_quarantine\<批次>）里的档也必须被放回。
    // 真机成因：2026-09-22 去重把 16 份 / 438 MB 挪进隔离区，而放回逻辑只枚举抽屉，
    // 于是切到读得了的版本也永远不回列表 —— 玩家视角就是"存档凭空少了"。
    // 同一条用例还要钉住反向：隔离区里的 mod 批次（1.0-stray-mods-…）绝不能被当存档搬进 Saves。
    var qzRoot = Path.Combine(sstage, "_quarantine");
    SaveVersionService.SavesDirOverride = ssaves;   // 上一组用例把覆写切到 gsaves 了，不切回来放回的就不是本沙箱
    var qzBatch = Path.Combine(qzRoot, "Saves-dedup-test");
    var qzModBatch = Path.Combine(qzRoot, "1.0-stray-mods-test");
    var qzMod = Path.Combine(qzModBatch, "Mods", "某个mod");
    Directory.CreateDirectory(qzBatch);
    Directory.CreateDirectory(qzMod);
    File.WriteAllText(Path.Combine(qzMod, "manifest.json"), "{\"Name\":\"某个mod\"}");
    MkSave("隔离区档_900000401", "隔离", "隔离农场", "1.6.15", 40);
    Directory.Move(Path.Combine(ssaves, "隔离区档_900000401"), Path.Combine(qzBatch, "隔离区档_900000401"));
    var qzHits = SaveVersionService.QuarantineSaveBatches();
    var qBack = SaveVersionService.RestoreHidden(SLog);
    Check("S40 人工隔离区的存档批次也被放回；按结构认批，mod 批次一律不碰",
        qzHits.Contains(qzBatch)
        && !qzHits.Any(p => p.EndsWith("1.0-stray-mods-test", StringComparison.Ordinal))
        && qBack >= 1
        && File.Exists(Path.Combine(ssaves, "隔离区档_900000401", "隔离区档_900000401"))
        && !Directory.Exists(qzBatch)
        && Directory.Exists(qzMod),
        $"认出批次={qzHits.Count} 放回={qBack} 隔离区已排空={!Directory.Exists(qzBatch)} mod批次未动={Directory.Exists(qzMod)}");

    SaveVersionService.SavesDirOverride = ssaves;
    EndS();
}

// ═══════════════ B. 肖像页 ═══════════════
if (smapiOnly) { Console.WriteLine($"\n════════ 总计：{pass} PASS / {fail} FAIL ═════════"); Environment.Exit(fail == 0 ? 0 : 1); }
// --real：直接扫真实游戏目录（诊断沙箱扫描异常用；选择会写真实配置，结束自动清理）
Console.WriteLine("\n────── B. 肖像页" + (realMode ? "（真实游戏目录）" : "（沙箱）") + " ──────");
string bDir;
string? fwDir = null;
if (realMode)
{
    bDir = gamePath;
}
else
{
    bDir = Path.Combine(Path.GetTempPath(), "jg-portrait-test");
    try { if (Directory.Exists(bDir)) Directory.Delete(bDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(bDir, "Mods"));

    // 假 Content：原版行的立绘/精灵来源走 Content/<dir>/<id>.xnb 存在性检查，
    // 沙箱没有游戏 Content 会导致所有角色行被「无精灵表」过滤删光（实测）
    foreach (var (dir, id) in new[] { ("Portraits", "Abigail"), ("Characters", "Abigail"), ("Portraits", "Emily"), ("Characters", "Emily") })
    {
        var xp = Path.Combine(bDir, "Content", dir, id + ".xnb");
        Directory.CreateDirectory(Path.GetDirectoryName(xp)!);
        File.WriteAllBytes(xp, new byte[] { 0x58, 0x4E, 0x42, 0x58 });
    }

    // 合成 Emily CP 肖像包（真实 Emily 包不在任何版本快照里；PNG 现造 64x64 合法尺寸）
    var emilyPack = Path.Combine(bDir, "Mods", "JGTest Emily Pack");
    Directory.CreateDirectory(Path.Combine(emilyPack, "assets"));
    File.WriteAllBytes(Path.Combine(emilyPack, "assets", "Emily.png"), TinyPng);
    File.WriteAllText(Path.Combine(emilyPack, "manifest.json"),
        """{"Name":"JGTest Emily Pack","UniqueID":"JuniGrid.Test.Emily","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(emilyPack, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Emily","FromFile":"assets/Emily.png"}]}""");

    var fw = Path.Combine(bDir, "Mods", "Portraiture");
    Directory.CreateDirectory(Path.Combine(fw, "Portraits"));
    File.WriteAllText(Path.Combine(fw, "manifest.json"),
        """{"Name":"Portraiture","UniqueID":"Platonymous.Portraiture","Version":"1.0.0","EntryDll":"Portraiture.dll"}""");
    File.WriteAllText(Path.Combine(fw, "config.json"), """{"active":"Vanilla"}""");
    fwDir = fw;
}

// ── B16 手工拖进 Mods\ 的裸素材目录：显式转成 CP 包（C 方案），并且不许误伤别人的包 ──
// 转换原本只挂在"走启动器下载安装"那一刻，手放的一直没人管 → 现在给一个入口 + 三条判定闸门。
if (!realMode)
{
    // 留底现在落 StoragePaths.ModsBackupDir（缓存根下）→ 必须先把缓存根指进临时区，
    // 否则这条用例会往真实 E:\junigrid\mods-backup 里写东西。
    var setCacheB16 = typeof(StoragePaths).GetProperty("CacheRoot")!.GetSetMethod(nonPublic: true)!;
    var prevCacheB16 = StoragePaths.CacheRoot;
    var bCache = Path.Combine(Path.GetTempPath(), "jg-portrait-cache");
    try { if (Directory.Exists(bCache)) Directory.Delete(bCache, true); } catch { }
    Directory.CreateDirectory(bCache);
    setCacheB16.Invoke(null, new object?[] { bCache });

    var loose = Path.Combine(bDir, "Mods", "JGTest Loose Emily");
    Directory.CreateDirectory(loose);
    File.WriteAllBytes(Path.Combine(loose, "Emily.png"), TinyPng);
    var looksB16 = PortraitSkinService.LooksLikeLoosePortraitFolder(bDir, loose);
    var errB16 = new ModService().ConvertLoosePortraitFolder(bDir, "JGTest Loose Emily", out var convNameB16);
    // 留底：缓存根\mods-backup\<原名>-raw-<时间戳>；Mods 根下不许再留任何副本
    var bakB16 = Directory.Exists(StoragePaths.ModsBackupDir)
        ? Directory.GetDirectories(StoragePaths.ModsBackupDir)
            .FirstOrDefault(d => Path.GetFileName(d).StartsWith("JGTest Loose Emily-raw-", StringComparison.Ordinal))
        : null;
    var legacyInMods = Directory.GetDirectories(Path.Combine(bDir, "Mods"))
        .Any(d => Path.GetFileName(d).EndsWith("-raw-backup", StringComparison.OrdinalIgnoreCase));
    Check("B16 手放的裸 PNG 目录转成 CP 肖像包，留底挪到 mods-backup（不再留在 Mods 里冒充 mod）",
        looksB16 && errB16 is null
        && File.Exists(Path.Combine(loose, "manifest.json"))
        && File.Exists(Path.Combine(loose, "content.json"))
        && bakB16 is not null && File.Exists(Path.Combine(bakB16, "Emily.png"))
        && !legacyInMods,
        "识别=" + looksB16 + " ‖ err=" + (errB16 ?? "无") + " ‖ 留底=" + (bakB16 is null ? "(没找到)" : Path.GetFileName(bakB16))
        + " ‖ Mods 里还留着留底=" + legacyInMods);

    var inner = Path.Combine(bDir, "Mods", "JGTest Inner Pack", "assets");
    Directory.CreateDirectory(inner);
    File.WriteAllBytes(Path.Combine(inner, "Emily.png"), TinyPng);
    File.WriteAllText(Path.Combine(bDir, "Mods", "JGTest Inner Pack", "manifest.json"),
        """{"Name":"JGTest Inner Pack","UniqueID":"JuniGrid.Test.Inner","Version":"1.0.0"}""");
    Check("B16b 别人包里的 assets/Emily.png 与 Portraiture 框架本体都不算裸素材目录",
        !PortraitSkinService.LooksLikeLoosePortraitFolder(bDir, Path.Combine(bDir, "Mods", "JGTest Inner Pack"))
        && !PortraitSkinService.LooksLikeLoosePortraitFolder(bDir, Path.Combine(bDir, "Mods", "Portraiture")),
        "内嵌素材=" + PortraitSkinService.LooksLikeLoosePortraitFolder(bDir, Path.Combine(bDir, "Mods", "JGTest Inner Pack"))
        + " ‖ Portraiture=" + PortraitSkinService.LooksLikeLoosePortraitFolder(bDir, Path.Combine(bDir, "Mods", "Portraiture")));

    var scanB16 = new PortraitSkinService(new ModService(), cfgSvc).Scan(bDir);
    var emilyB16 = scanB16.Characters.FirstOrDefault(c =>
        c.Id.Equals("Emily", StringComparison.OrdinalIgnoreCase));
    // 转换包与沙箱自带的 JGTest Emily Pack 用的是同一张合成 PNG ⇒ 逐像素相同。
    // v1.7.15：哈希去重已删，转换包现在【单列一条皮肤】（以前会被并进同画面的那条）。
    var looseSeen = emilyB16 is not null
        && emilyB16.Skins.Any(s => s.PackFolder == "JGTest Loose Emily");
    Check("B16c 转出来的包被立绘扫描认到（单列一条皮肤），UID 走 JuniGrid.PortraitPack.*",
        looseSeen && File.ReadAllText(Path.Combine(loose, "manifest.json")).Contains("JuniGrid.PortraitPack."),
        "Emily 皮肤=" + (emilyB16 is null ? "(无角色行)" : string.Join(",", emilyB16.Skins.Select(s => s.PackFolder))));

    // 重转必须拒绝（转完目录里已经有 manifest.json 了）。
    // 老式留底（-raw-backup，历史上就留在 Mods 里）永不再被当成素材目录 —— 新留底在缓存根下，
    // 靠"不在 Mods 里"天然安全，这条守的是历史遗留。
    var legacy = Path.Combine(bDir, "Mods", "JGTest Loose Emily-raw-backup");
    Directory.CreateDirectory(legacy);
    File.WriteAllBytes(Path.Combine(legacy, "Emily.png"), TinyPng);
    var legacyJudged = PortraitSkinService.LooksLikeLoosePortraitFolder(bDir, legacy);
    var againErr = new ModService().ConvertLoosePortraitFolder(bDir, "JGTest Loose Emily", out _);
    Check("B16d 已转过的目录不会被二次转换；老式 -raw-backup 留底不再被当成素材目录",
        againErr is not null && againErr.Contains("不符合转换条件") && !legacyJudged,
        "第二次返回=" + (againErr ?? "(null，居然又转了一次)").Split('\n')[0] + " ‖ 留底被判可转=" + legacyJudged);

    // ── B23 自动转换 + 老留底不再冒充 mod ──
    // 用户 2026-09-20 实测：手动转完，Mods 里自己冒出一行「TP's Emily Portrait-raw-backup · 无清单」。
    // 现在 ① 扫描跳过 *-raw-backup（上面 B16d 建的那个就是样本）；② 扫描时自动把裸图目录转成
    // CP 包（手动入口照旧保留）。
    var autoSrc = Path.Combine(bDir, "Mods", "JGTest Auto Emily");
    Directory.CreateDirectory(autoSrc);
    File.WriteAllBytes(Path.Combine(autoSrc, "Emily.png"), TinyPng);
    new ModService().AutoConvertLoosePortraits(bDir);
    IReadOnlyList<ModEntry> scanB23;
    try { scanB23 = new ModService().Scan(bDir); }
    catch (Exception ex) { scanB23 = Array.Empty<ModEntry>(); AppLog.Warn("Test", "B23 扫描异常: " + ex.Message); }
    var legacyRows = scanB23.Count(m => m.Folder.EndsWith("-raw-backup", StringComparison.OrdinalIgnoreCase));
    Check("B23 扫描自动把裸图目录转成 CP 包；老留底目录不再作为「无清单」条目出现",
        File.Exists(Path.Combine(autoSrc, "manifest.json")) && legacyRows == 0,
        "自动转换后带清单=" + File.Exists(Path.Combine(autoSrc, "manifest.json")) + " ‖ 列表里的留底行=" + legacyRows);

    setCacheB16.Invoke(null, new object?[] { prevCacheB16 });   // 缓存根还原

    // （B17/B17b 近似/精确合并用例已随哈希去重一起删除 —— v1.7.15：同脸不同 mod 的卡不再自动合并，
    //   没有"并成一条"这回事了，故无需再测。独立沙箱目录 jg-nearart-test 一并去掉。）

    // ── B18 皮肤选择指向已消失的包（Emily/Kent 实机就这样：包被搬走，游戏里其实是默认脸）──
    static PortraitSkinOption OptB18(string folder, string name, string? src = null,
        bool vanilla = false, bool native = false) =>
        new(folder, name, false, vanilla, native, false, src, null, Array.Empty<string>());
    static PortraitCharacter ChB18(string id, PortraitSkinOption? vanilla, PortraitSkinOption? native) =>
        new(id, id, vanilla is not null, vanilla, native, Array.Empty<PortraitSkinOption>(), native?.PackFolder);
    var optsB18 = new[] { OptB18("", "默认", vanilla: true), OptB18("JGTest Emily Pack", "Emily 包") };
    Check("B18 选过的包不在候选里 → 报出包名；包还在（含大小写不同）或选的是默认 → 不报",
        PortraitSkinService.StalePack(optsB18, "JGTest Gone Pack") == "JGTest Gone Pack"
        && PortraitSkinService.StalePack(optsB18, "jgtest emily pack") is null
        && PortraitSkinService.StalePack(optsB18, "") is null
        && PortraitSkinService.StalePack(optsB18, null) is null,
        "消失包=" + (PortraitSkinService.StalePack(optsB18, "JGTest Gone Pack") ?? "(null)")
        + " ‖ 在的包=" + (PortraitSkinService.StalePack(optsB18, "jgtest emily pack") ?? "null"));

    // ── B19 同脸别名 NPC（SVE 把 Scarlett 的立绘也挂给 ScarlettFake）──
    // 折叠会丢一个真角色，不标又像是我们去重漏了 —— 只标注、不折叠。
    const string faceA = @"C:\x\Content\Portraits\Scarlett.xnb";
    const string faceB = @"C:\x\Content\Portraits\Harvey.xnb";
    var aliasB19 = PortraitSkinService.AliasMap(new[]
    {
        ChB18("Scarlett", OptB18("", "默认", faceA, vanilla: true), null),
        ChB18("ScarlettFake", null, OptB18("SVE", "mod 默认外观", faceA, native: true)),
        ChB18("Harvey", OptB18("", "默认", faceB, vanilla: true), null),
        ChB18("Marlon", OptB18("", "默认", faceB, vanilla: true), null),
    });
    Check("B19 同脸 + 名字互为前缀才算别名（ScarlettFake→Scarlett）；同脸但名字不相干的两 NPC 不标",
        aliasB19.Count == 1 && aliasB19["ScarlettFake"] == "Scarlett"
        && !aliasB19.ContainsKey("Harvey") && !aliasB19.ContainsKey("Marlon"),
        "别名表=" + (aliasB19.Count == 0 ? "(空)" : string.Join(",", aliasB19.Select(kv => kv.Key + "→" + kv.Value))));

    // ── B20 mod 新增角色的裸素材包也认得（旧版只查原版角色表，SVE 的 Andy 一律漏判）──
    // 立绘 + 精灵两张都给：产品规则是"没有走贴图来源的角色整个不上"，
    // 只给立绘的 Andy 会被正当过滤掉，那是夹具不对不是代码不对。
    // ⚠ Andy 之所以不用注册 NPC 就能上页：它在内置 SVE 阵容表里。mod 角色进立绘页的
    // 门票是「有 Data/Characters 注册」或「在内置表里」—— 自造名字要照 B21 那样注册。
    var nDir = Path.Combine(Path.GetTempPath(), "jg-nearart-test");
    try { if (Directory.Exists(nDir)) Directory.Delete(nDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(nDir, "Mods"));
    var npcDir = Path.Combine(nDir, "Mods", "JGTest Mod NPC");
    Directory.CreateDirectory(Path.Combine(npcDir, "assets"));
    File.WriteAllBytes(Path.Combine(npcDir, "assets", "Andy.png"), TinyPng);
    File.WriteAllBytes(Path.Combine(npcDir, "assets", "AndySprite.png"), TinyPng);
    File.WriteAllText(Path.Combine(npcDir, "manifest.json"),
        """{"Name":"JGTest Mod NPC","UniqueID":"JuniGrid.Test.ModNpc","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(npcDir, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Andy","FromFile":"assets/Andy.png"},{"Action":"Load","Target":"Characters/Andy","FromFile":"assets/AndySprite.png"}]}""");
    var scanB20 = new PortraitSkinService(new ModService(), cfgSvc).Scan(nDir);
    var andyB20 = scanB20.Characters.FirstOrDefault(c => c.Id.Equals("Andy", StringComparison.OrdinalIgnoreCase));
    void MkLooseB20(string folderName, string artName)
    {
        var d = Path.Combine(nDir, "Mods", folderName);
        Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, artName + ".png"), TinyPng);
    }
    MkLooseB20("JGTest Loose Andy", "Andy");
    MkLooseB20("JGTest Loose Nobody", "Nobody");
    var andyLoose = PortraitSkinService.LooksLikeLoosePortraitFolder(nDir, Path.Combine(nDir, "Mods", "JGTest Loose Andy"));
    var nobodyLoose = PortraitSkinService.LooksLikeLoosePortraitFolder(nDir, Path.Combine(nDir, "Mods", "JGTest Loose Nobody"));
    Check("B20 扫描认出的 mod 角色（Andy）其裸素材目录能转；对不上任何角色的目录仍不认",
        andyB20 is not null && andyLoose && !nobodyLoose,
        "Andy 角色行=" + (andyB20 is null ? "(没扫出来)" : "有")
        + " ‖ Loose Andy=" + andyLoose + " ‖ Loose Nobody=" + nobodyLoose);

    // ── B21 同脸别名并成一张卡，且"点一次两个条目一起换"（用户：这本来就属于同一个 NPC）──
    // 夹具照 SVE 的真实形状：一个包用 EditData 注册两个 NPC（Data/Characters 是 mod 角色
    // 进立绘页的门票，没有它扫描按"资产碎片"滤掉），并让两个条目共用同一张立绘/精灵文件。
    // 没装汉化时两边显示名不同 ⇒ 旧的"同名才并"永不命中，必须靠同脸别名这条信号。
    var kDir = Path.Combine(Path.GetTempPath(), "jg-kid-test");
    try { if (Directory.Exists(kDir)) Directory.Delete(kDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(kDir, "Mods"));
    var kidPack = Path.Combine(kDir, "Mods", "JGTest Kid Family");
    Directory.CreateDirectory(Path.Combine(kidPack, "assets"));
    File.WriteAllBytes(Path.Combine(kidPack, "assets", "Kid.png"), TinyPng);
    File.WriteAllBytes(Path.Combine(kidPack, "assets", "KidSprite.png"), TinyPng);
    File.WriteAllText(Path.Combine(kidPack, "manifest.json"),
        """{"Name":"JGTest Kid Family","UniqueID":"JuniGrid.Test.KidFamily","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(kidPack, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JGTestKid":{"DisplayName":"JGTestKid","HomeRegion":"Other"},"JGTestKidClone":{"DisplayName":"JGTestKidClone","HomeRegion":"Other"}}},{"Action":"Load","Target":"Portraits/JGTestKid","FromFile":"assets/Kid.png"},{"Action":"Load","Target":"Characters/JGTestKid","FromFile":"assets/KidSprite.png"},{"Action":"Load","Target":"Portraits/JGTestKidClone","FromFile":"assets/Kid.png"},{"Action":"Load","Target":"Characters/JGTestKidClone","FromFile":"assets/KidSprite.png"}]}""");
    var psB21 = new PortraitSkinService(new ModService(), cfgSvc);
    var scanB21 = psB21.Scan(kDir);
    var kidB21 = scanB21.Characters.FirstOrDefault(c => c.Id == "JGTestKid");
    var cloneB21 = scanB21.Characters.FirstOrDefault(c => c.Id == "JGTestKidClone");
    Check("B21 同脸别名不再单列卡片：并进本尊那张（Members 两条），别名卡只留着给写盘解析",
        kidB21 is not null && kidB21.Members.Count == 2
        && kidB21.Members.Contains("JGTestKidClone")
        && cloneB21 is { Hidden: true }
        && scanB21.Characters.Count(c => !c.Hidden) == 1,
        "本尊 Members=" + (kidB21 is null ? "(没扫出来)" : string.Join(",", kidB21.Members))
        + " ‖ 别名卡 Hidden=" + (cloneB21?.Hidden.ToString() ?? "(卡没了)")
        + " ‖ 全部卡=" + string.Join(" / ", scanB21.Characters.Select(c =>
            c.Id + (c.Hidden ? "[隐]" : "") + "(成员" + c.Members.Count + ")"))
        + " ‖ 诊断=" + string.Join(" ; ", scanB21.Diagnostics
            .Where(d => d.Item1.Contains("Kid", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Item1 + ":" + d.Item2)));

    psB21.SelectSkin(kDir, scanB21, "JGTestKid", "JGTest Kid Family");
    var ovTextB21 = File.Exists(Path.Combine(kDir, "Mods", PortraitSkinService.OverrideFolder, "content.json"))
        ? File.ReadAllText(Path.Combine(kDir, "Mods", PortraitSkinService.OverrideFolder, "content.json")) : "";
    Check("B21b 在本尊那张上选皮肤 ⇒ 两个 NPC 条目都落配置、覆盖包两份资产都钉住（不半生效）",
        cfgSvc.Current.PortraitSkins.TryGetValue("JGTestKid", out var k1) && k1 == "JGTest Kid Family"
        && cfgSvc.Current.PortraitSkins.TryGetValue("JGTestKidClone", out var k2) && k2 == "JGTest Kid Family"
        && ovTextB21.Contains("Portraits/JGTestKid\"") && ovTextB21.Contains("Portraits/JGTestKidClone"),
        "配置=" + string.Join("、", cfgSvc.Current.PortraitSkins
            .Where(kv => kv.Key.StartsWith("JGTestKid", StringComparison.Ordinal)).Select(kv => kv.Key + "→" + kv.Value))
        + " ‖ 覆盖包条目: " + System.Text.RegularExpressions.Regex.Matches(ovTextB21, "\"Target\": *\"[^\"]*\"")
            .Select(m => m.Value).Aggregate("", (a, b) => a + " " + b));
    psB21.SelectSkin(kDir, scanB21, "JGTestKidClone", null);   // 从别名卡回默认：整组一起清
    Check("B21c 从别名卡回默认 ⇒ 整组的选择一起清掉（不留半条）",
        !cfgSvc.Current.PortraitSkins.ContainsKey("JGTestKid")
        && !cfgSvc.Current.PortraitSkins.ContainsKey("JGTestKidClone"),
        "残留=" + string.Join("、", cfgSvc.Current.PortraitSkins.Keys
            .Where(k => k.StartsWith("JGTestKid", StringComparison.Ordinal))));

    // ── B22 与默认像同文件的皮肤行：折叠必须真的写回卡上 ──
    // 回归的是"日志打了 [视觉折叠] 但格子上还在"：写回条件当时比的是折叠**之后**的条数，
    // 只折叠掉一条、第二轮没再动 ⇒ 两数相等 ⇒ 整轮折叠白做（冈瑟 5 格，用户："一模一样还不给去重"）。
    var sameAsDefault = Path.Combine(kidPack, "assets", "Kid.png");
    var otherArt = Path.Combine(kidPack, "assets", "Other.png");
    File.WriteAllBytes(otherArt, BuildPng(64, 64, 410));
    var chB22 = ChB18("Gunther", OptB18("", "默认", sameAsDefault, vanilla: true), null) with
    {
        Skins = new List<PortraitSkinOption>
        {
            OptB18("Pack Same", "与默认同文件的包", sameAsDefault),
            OptB18("Pack Other", "另一张画", otherArt),
        }
    };
    var listB22 = new List<PortraitCharacter> { chB22 };
    var diagB22 = new List<(string, string)>();
    PortraitSkinService.FoldSkinsAgainstDefault(listB22, diagB22);
    Check("B22 与默认像同文件的皮肤行折叠后要写回卡上（日志折了 ≠ 卡上没了）",
        listB22[0].Skins.Count == 1 && listB22[0].Skins[0].PackFolder == "Pack Other",
        "剩下=" + string.Join("、", listB22[0].Skins.Select(s => s.PackFolder))
        + " ‖ 诊断=" + string.Join("；", diagB22.Select(d => d.Item2)));

    // ── B23 v1.7 锁定 / 一键恢复 / 批量应用 ──
    {
        var lockInfo = new PortraitLockInfo("JGTest Kid Family", @"C:\pin\Kid_summer.png", "summer");
        var raw = PortraitSkinService.SerializeLock(lockInfo);
        var parsed = PortraitSkinService.ParseLock(raw);
        Check("B23a 锁定条目序列化往返一致（pack/pin/season）",
            parsed is not null
            && parsed.PackFolder == "JGTest Kid Family"
            && parsed.PinFile == @"C:\pin\Kid_summer.png"
            && parsed.Season == "summer",
            "raw=" + raw + " ‖ parsed=" + (parsed is null ? "null" : $"{parsed.PackFolder}|{parsed.PinFile}|{parsed.Season}"));

        Check("B23b 损坏锁定 JSON 解析为 null（不当异常炸）",
            PortraitSkinService.ParseLock("{not json") is null
            && PortraitSkinService.ParseLock("") is null
            && PortraitSkinService.ParseLock(null) is null,
            "坏JSON=" + (PortraitSkinService.ParseLock("{not json") is null ? "null" : "有值"));

        // 锁定后 SelectSkin / SetSeasonSkin 必须拒绝改写
        psB21.SelectSkin(kDir, scanB21, "JGTestKid", "JGTest Kid Family");
        var pinPath = Path.Combine(kidPack, "assets", "Kid.png");
        psB21.SetLocked(kDir, scanB21, "JGTestKid", true,
            kidB21?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Kid Family"),
            pinPath, "summer");
        Check("B23c 锁定写入配置（合并卡全员）且清掉季节分配",
            PortraitSkinService.IsLocked(cfgSvc.Current, "JGTestKid")
            && PortraitSkinService.IsLocked(cfgSvc.Current, "JGTestKidClone")
            && !cfgSvc.Current.PortraitSeasonSkins.ContainsKey("JGTestKid"),
            "锁=" + string.Join(",", cfgSvc.Current.PortraitLocks.Keys)
            + " ‖ 季节残留=" + string.Join(",", cfgSvc.Current.PortraitSeasonSkins.Keys));

        cfgSvc.Current.PortraitSkins["JGTestKid"] = "SHOULD_NOT_STICK";
        psB21.SelectSkin(kDir, scanB21, "JGTestKid", "Other Pack Never");
        Check("B23d 锁定中点皮肤卡 = 换成这张并顺手解锁（旧语义把单击静默吞掉，实机报成「单击没用」）",
            cfgSvc.Current.PortraitSkins.TryGetValue("JGTestKid", out var afterClick)
            && afterClick == "Other Pack Never"
            && !PortraitSkinService.IsLocked(cfgSvc.Current, "JGTestKid")
            && !PortraitSkinService.IsLocked(cfgSvc.Current, "JGTestKidClone"),
            "当前=" + (cfgSvc.Current.PortraitSkins.TryGetValue("JGTestKid", out var t) ? t : "(无)")
            + " ‖ 锁残留=" + string.Join(",", cfgSvc.Current.PortraitLocks.Keys));

        psB21.SetLocked(kDir, scanB21, "JGTestKid", true,
            kidB21?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Kid Family"), pinPath, "summer");
        psB21.SetSeasonSkin(kDir, scanB21, "JGTestKid", "winter", "Other Pack");
        Check("B23e 锁定中长按「固定到当前季」= 季节分配写进去并解锁（与单击同口径）",
            PortraitSkinService.IsLocked(cfgSvc.Current, "JGTestKid") is false
            && cfgSvc.Current.PortraitSeasonSkins.TryGetValue("JGTestKid", out var winterSeg)
            && winterSeg.Contains("winter:Other Pack"),
            "季节=" + string.Join(",", cfgSvc.Current.PortraitSeasonSkins.Keys)
            + " ‖ 锁=" + string.Join(",", cfgSvc.Current.PortraitLocks.Keys));

        psB21.SetLocked(kDir, scanB21, "JGTestKid", true,
            kidB21?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Kid Family"), pinPath, "summer");
        var ovLock = File.Exists(Path.Combine(kDir, "Mods", PortraitSkinService.OverrideFolder, "content.json"))
            ? File.ReadAllText(Path.Combine(kDir, "Mods", PortraitSkinService.OverrideFolder, "content.json")) : "";
        Check("B23f 锁定后覆盖包钉的是 PinFile（无 Season 条件轮换）",
            ovLock.Contains("Portraits/JGTestKid") && !ovLock.Contains("\"When\": { \"Season\""),
            "含Season条件=" + ovLock.Contains("Season")
            + " ‖ 目标=" + System.Text.RegularExpressions.Regex.Matches(ovLock, "\"Target\": *\"[^\"]*\"")
                .Select(m => m.Value).Aggregate("", (a, b) => a + " " + b));

        psB21.SetLocked(kDir, scanB21, "JGTestKid", false, null, null, null);
        Check("B23g 解锁后可继续换肤，锁定标记清空",
            !PortraitSkinService.IsLocked(cfgSvc.Current, "JGTestKid")
            && !PortraitSkinService.IsLocked(cfgSvc.Current, "JGTestKidClone"),
            // 只验测试自己的 id —— cfgSvc 与真实用户配置同源，用户机器上可能已有锁
            "锁残留=" + string.Join(",", cfgSvc.Current.PortraitLocks.Keys));

        // 批量应用：有皮肤的角色全部切换；锁定的跳过
        var applyDir = Path.Combine(Path.GetTempPath(), "jg-apply-test");
        try { if (Directory.Exists(applyDir)) Directory.Delete(applyDir, true); } catch { }
        Directory.CreateDirectory(Path.Combine(applyDir, "Mods"));
        void MkCharPack(string root, string pack, string uid, params string[] charIds)
        {
            var p = Path.Combine(root, "Mods", pack);
            Directory.CreateDirectory(Path.Combine(p, "assets"));
            File.WriteAllText(Path.Combine(p, "manifest.json"),
                "{\"Name\":\"" + pack + "\",\"UniqueID\":\"" + uid + "\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
            var changes = new System.Text.StringBuilder();
            foreach (var id in charIds)
            {
                File.WriteAllBytes(Path.Combine(p, "assets", id + ".png"), TinyPng);
                File.WriteAllBytes(Path.Combine(p, "assets", id + "Sprite.png"), TinyPng);
                changes.Append("{\"Action\":\"Load\",\"Target\":\"Portraits/" + id + "\",\"FromFile\":\"assets/" + id + ".png\"},");
                changes.Append("{\"Action\":\"Load\",\"Target\":\"Characters/" + id + "\",\"FromFile\":\"assets/" + id + "Sprite.png\"},");
            }
            var chJson = string.Join(",", charIds.Select(id =>
                "\"" + id + "\":{\"DisplayName\":\"" + id + "\",\"HomeRegion\":\"Other\"}"));
            // 每个包都带 Data/Characters 注册（真娘家），否则会被当皮肤包
            File.WriteAllText(Path.Combine(p, "content.json"),
                "{\"Format\":\"2.5\",\"Changes\":[{\"Action\":\"EditData\",\"Target\":\"Data/Characters\",\"Entries\":{" + chJson + "}}," + changes.ToString().TrimEnd(',') + "]}");
        }
        MkCharPack(applyDir, "JGTest Skin Pack A", "JuniGrid.Test.SkinA", "JGApp1", "JGApp2", "JGApp3");
        // 第二个包给 JGApp1/JGApp2 换脸（JGApp1 稍后锁定，用来验证批量跳过）
        var extra = Path.Combine(applyDir, "Mods", "JGTest Skin Pack B");
        Directory.CreateDirectory(Path.Combine(extra, "assets"));
        foreach (var id in new[] { "JGApp1", "JGApp2" })
        {
            File.WriteAllBytes(Path.Combine(extra, "assets", id + ".png"), TinyPng);
            File.WriteAllBytes(Path.Combine(extra, "assets", id + "Sprite.png"), TinyPng);
        }
        File.WriteAllText(Path.Combine(extra, "manifest.json"),
            """{"Name":"JGTest Skin Pack B","UniqueID":"JuniGrid.Test.SkinB","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
        File.WriteAllText(Path.Combine(extra, "content.json"),
            """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JGApp1","FromFile":"assets/JGApp1.png"},{"Action":"Load","Target":"Characters/JGApp1","FromFile":"assets/JGApp1Sprite.png"},{"Action":"Load","Target":"Portraits/JGApp2","FromFile":"assets/JGApp2.png"},{"Action":"Load","Target":"Characters/JGApp2","FromFile":"assets/JGApp2Sprite.png"}]}""");

        var psApply = new PortraitSkinService(new ModService(), cfgSvc);
        var scanApply = psApply.Scan(applyDir);
        // 先锁 JGApp1
        var app1 = scanApply.Characters.FirstOrDefault(c => c.Id == "JGApp1");
        if (app1 is not null)
        {
            var opt = app1.Skins.FirstOrDefault(s => s.PackFolder == "JGTest Skin Pack B")
                ?? app1.Native ?? app1.Skins.FirstOrDefault();
            psApply.SetLocked(applyDir, scanApply, "JGApp1", true, opt, opt?.SourceFile, "spring");
        }
        var packs = PortraitSkinService.ListAppliablePacks(scanApply);
        Check("B23h ListAppliablePacks 列出有皮肤的包（含角色数）",
            packs.Any(p => p.PackFolder == "JGTest Skin Pack B" && p.CharCount >= 1),
            "包=" + string.Join(" / ", packs.Select(p => $"{p.PackName}({p.CharCount})")));

        var (applied, skippedLocked) = psApply.ApplyPackToAll(applyDir, scanApply, "JGTest Skin Pack B");
        var app1StillLocked = PortraitSkinService.IsLocked(cfgSvc.Current, "JGApp1");
        var app2Sel = cfgSvc.Current.PortraitSkins.TryGetValue("JGApp2", out var a2) ? a2 : "(无)";
        var app1Sel = cfgSvc.Current.PortraitSkins.TryGetValue("JGApp1", out var a1v) ? a1v : "(无)";
        // JGApp3 只有 Pack A 当娘家，没有 Pack B 皮肤 → 不应被批量应用
        var app3Sel = cfgSvc.Current.PortraitSkins.TryGetValue("JGApp3", out var a3) ? a3 : "(无)";
        Check("B23i 批量应用：有该皮肤的角色切换、锁定角色跳过、无该皮肤的角色不动",
            skippedLocked >= 1
            && applied >= 1
            && app2Sel == "JGTest Skin Pack B"
            && app3Sel == "(无)"
            && app1StillLocked,
            $"applied={applied} skipped={skippedLocked} ‖ JGApp1锁={app1StillLocked} 选={app1Sel} ‖ JGApp2选={app2Sel} ‖ JGApp3选={app3Sel}");

        // 一键恢复默认：清选择/季节/锁，原版角色进 TrueVanilla
        psApply.SetLocked(applyDir, scanApply, "JGApp1", false, null, null, null);
        psApply.RestoreAllDefaults(applyDir, scanApply);
        Check("B23k 一键恢复默认：选择/季节/锁全清（全局字典，不止当前扫描角色）",
            cfgSvc.Current.PortraitSkins.Count == 0
            && cfgSvc.Current.PortraitLocks.Count == 0
            && cfgSvc.Current.PortraitSeasonSkins.Count == 0,
            "选择残留=" + string.Join(",", cfgSvc.Current.PortraitSkins.Keys)
            + " ‖ 锁残留=" + string.Join(",", cfgSvc.Current.PortraitLocks.Keys)
            + " ‖ 季节残留=" + string.Join(",", cfgSvc.Current.PortraitSeasonSkins.Keys));

        // TrueVanilla：合成一个带扩展默认的角色（Gunther）验证原版强制
        var tvDir = Path.Combine(Path.GetTempPath(), "jg-tv-test");
        try { if (Directory.Exists(tvDir)) Directory.Delete(tvDir, true); } catch { }
        Directory.CreateDirectory(Path.Combine(tvDir, "Content", "Portraits"));
        Directory.CreateDirectory(Path.Combine(tvDir, "Content", "Characters"));
        Directory.CreateDirectory(Path.Combine(tvDir, "Mods"));
        // 原版 xnb：内容是 PNG 字节（CopyAsPng 解码失败时按 PNG 魔数兜底拷贝）——
        // 同时验证「扩展名 xnb、内容 png」这条兜底
        File.WriteAllBytes(Path.Combine(tvDir, "Content", "Portraits", "Gunther.xnb"), TinyPng);
        File.WriteAllBytes(Path.Combine(tvDir, "Content", "Characters", "Gunther.xnb"), TinyPng);
        var exp = Path.Combine(tvDir, "Mods", "JGTest SVE Lite");
        Directory.CreateDirectory(Path.Combine(exp, "assets"));
        File.WriteAllBytes(Path.Combine(exp, "assets", "Gunther.png"), TinyPng);
        File.WriteAllBytes(Path.Combine(exp, "assets", "GuntherSprite.png"), TinyPng);
        File.WriteAllText(Path.Combine(exp, "manifest.json"),
            """{"Name":"JGTest SVE Lite","UniqueID":"JuniGrid.Test.SVELite","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
        // 扩展包：≥3 个 mod NPC 当娘家才会被识别为 expansionFolders，再给 Gunther 默认像
        File.WriteAllText(Path.Combine(exp, "content.json"),
            """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"SveA":{"DisplayName":"SveA","HomeRegion":"Other"},"SveB":{"DisplayName":"SveB","HomeRegion":"Other"},"SveC":{"DisplayName":"SveC","HomeRegion":"Other"}}},{"Action":"Load","Target":"Portraits/SveA","FromFile":"assets/Gunther.png"},{"Action":"Load","Target":"Characters/SveA","FromFile":"assets/GuntherSprite.png"},{"Action":"Load","Target":"Portraits/SveB","FromFile":"assets/Gunther.png"},{"Action":"Load","Target":"Characters/SveB","FromFile":"assets/GuntherSprite.png"},{"Action":"Load","Target":"Portraits/SveC","FromFile":"assets/Gunther.png"},{"Action":"Load","Target":"Characters/SveC","FromFile":"assets/GuntherSprite.png"},{"Action":"Load","Target":"Portraits/Gunther","FromFile":"assets/Gunther.png"},{"Action":"Load","Target":"Characters/Gunther","FromFile":"assets/GuntherSprite.png"}]}""");
        var psTv = new PortraitSkinService(new ModService(), cfgSvc);
        var scanTv = psTv.Scan(tvDir);
        // 先随便选个皮肤再恢复
        var gunther = scanTv.Characters.FirstOrDefault(c => c.Id == "Gunther");
        Check("B23l 双默认角色（冈瑟）扫描仍在，默认行可解析",
            gunther is not null,
            "卡=" + (gunther is null ? "没扫到" : $"{gunther.DisplayName} vanillaSrc={gunther.Vanilla?.SourceFile}"));
        psTv.RestoreAllDefaults(tvDir, scanTv);
        Check("B23m 一键恢复默认把原版角色写入 PortraitTrueVanilla（统一用原版而非 SVE 默认）",
            cfgSvc.Current.PortraitTrueVanilla.Contains("Gunther"),
            "TrueVanilla=" + string.Join(",", cfgSvc.Current.PortraitTrueVanilla));
        var ovTv = File.Exists(Path.Combine(tvDir, "Mods", PortraitSkinService.OverrideFolder, "content.json"))
            ? File.ReadAllText(Path.Combine(tvDir, "Mods", PortraitSkinService.OverrideFolder, "content.json")) : "";
        Check("B23n TrueVanilla 时覆盖包钉原版来源（Target=Portraits/Gunther，且 FromFile 不是扩展包路径）",
            ovTv.Contains("Portraits/Gunther")
            && !ovTv.Contains("JGTest SVE Lite"),
            "覆盖包=" + (ovTv.Length > 0 ? System.Text.RegularExpressions.Regex.Matches(ovTv, "\"Target\": *\"[^\"]*\"|\"FromFile\": *\"[^\"]*\"").Select(m => m.Value).Aggregate("", (a, b) => a + " " + b) : "(空)"));

        try { Directory.Delete(applyDir, true); } catch { }
        try { Directory.Delete(tvDir, true); } catch { }
    }

    try { Directory.Delete(kDir, true); } catch { }

    try { Directory.Delete(nDir, true); } catch { }
}

var convSrc = FindPackDir("BaZhua's Marriable Role Portrait");
Note("B0 可选真语料（BaZhua 转换包）是否装着", convSrc ?? "本机已无此包 —— 选择链路改用沙箱自带合成包，不再因此整段停摆");
if (!realMode && convSrc is not null) CopyTree(convSrc, Path.Combine(bDir, "Mods"), "BaZhua's Marriable Role Portrait");

var mods = new ModService();
var ps = new PortraitSkinService(mods, cfgSvc);

var scan1 = ps.Scan(bDir);
Check("B1 扫描完成，角色数=" + scan1.Characters.Count, scan1.Characters.Count > 0);
if (!realMode) Check("B1b 无框架时…（真实模式跳过）", true);

// ── B24 游戏目录还没有 Mods（Steam 刚重装完那一会儿）：原版角色不许整页消失 ──
// 旧实现在枚举 Mods 之前直接 return 空结果，而原版角色来自 VanillaNames 表 + Content，
// 跟 mod 一点关系没有 —— 用户看到的就是「0 个角色 + 让你确认游戏目录」。
if (!realMode)
{
    var noMods = Path.Combine(Path.GetTempPath(), "jg-portrait-nomods");
    try { if (Directory.Exists(noMods)) Directory.Delete(noMods, true); } catch { }
    foreach (var (dir, id) in new[] { ("Portraits", "Abigail"), ("Characters", "Abigail") })
    {
        var xp = Path.Combine(noMods, "Content", dir, id + ".xnb");
        Directory.CreateDirectory(Path.GetDirectoryName(xp)!);
        File.WriteAllBytes(xp, new byte[] { 0x58, 0x4E, 0x42, 0x58 });
    }
    var nmScan = new PortraitSkinService(new ModService(), cfgSvc).Scan(noMods);
    Check("B24 没有 Mods 目录时原版角色仍然列出（扫描不许在 Mods 缺失时提前返回空）",
        !Directory.Exists(Path.Combine(noMods, "Mods"))
        && nmScan.Characters.Any(c => string.Equals(c.Id, "Abigail", StringComparison.OrdinalIgnoreCase)),
        "角色数=" + nmScan.Characters.Count);
    try { Directory.Delete(noMods, true); } catch { }
}

// B2-B4 选择 → 覆盖包生成 → 回默认解除引用。
// 主体用沙箱自带的合成 CP 包（JGTest Emily Pack），这样不依赖用户装了哪个肖像包；
// 装了 BaZhua 时再拿 Abigail 复验一遍真实语料。旧版缺 BaZhua 就 Environment.Exit，
// 连带 B5-B8 和总计行一起没了（实测：今天肖像选择链路 0 覆盖而报告仍显示「全绿」）。
void SelectionChain(PortraitScanResult scan, string charId, string? packFolder, string label)
{
    if (packFolder is null)
    {
        var chX = scan.Characters.FirstOrDefault(c => c.Id == charId);
        Console.WriteLine("SKIP  " + label + " 该角色没有可用的非原版选项 —— 现有选项: "
            + string.Join(" / ", (chX?.AllOptions ?? Array.Empty<PortraitSkinOption>()).Select(o => o.PackFolder ?? "(null)")).Trim());
        return;
    }
    ps.SelectSkin(bDir, scan, charId, packFolder);
    var ovRoot = Path.Combine(bDir, "Mods", PortraitSkinService.OverrideFolder);   // 产品常量，别再写死遗留名
    var ovContent = Path.Combine(ovRoot, "content.json");
    var png = Path.Combine(ovRoot, "assets", "Portraits", charId + ".png");
    var ovText = File.Exists(ovContent) ? File.ReadAllText(ovContent) : "";
    Check($"{label} 选中 {packFolder} → 覆盖包含 assets/Portraits/{charId}.png + 条目",
        File.Exists(png) && ovText.Contains(charId),
        "png=" + (File.Exists(png) ? "有" : "无")
        + " | content.json " + (File.Exists(ovContent) ? new FileInfo(ovContent).Length + " B" : "(无)")
        + " | 头200字: " + (ovText.Length > 0 ? ovText[..Math.Min(200, ovText.Length)].Replace("\n", " ") : "(空)"));

    ps.SelectSkin(bDir, scan, charId, null);
    ovText = File.Exists(ovContent) ? File.ReadAllText(ovContent) : "";
    Check($"{label} 回默认 → 不再引用该包", !ovText.Contains(packFolder, StringComparison.Ordinal));
}

var emilyCh = scan1.Characters.FirstOrDefault(c => c.Id == "Emily");
var emilyOpt = emilyCh?.AllOptions.FirstOrDefault(o => !o.IsVanilla && !o.IsNative && o.SourceFile is not null);
SelectionChain(scan1, "Emily", emilyOpt?.PackFolder, "B2-4");

var abigailOpt = scan1.Characters.FirstOrDefault(c => c.Id == "Abigail")?
    .AllOptions.FirstOrDefault(o => !o.IsVanilla && !o.IsNative && o.SourceFile is not null);
SelectionChain(scan1, "Abigail", abigailOpt?.PackFolder, "B4b");

// B5-B8 Portraiture 仲裁（仅沙箱；真实模式当前没装框架）
if (!realMode)
{
    var scan2 = ps.Scan(bDir);
    Check("B5 框架可被扫描识别（PortraitureRoot 非空）", scan2.PortraitureRoot is not null);

    ps.SelectSkin(bDir, scan2, "Emily", "JGTest Emily Pack");
    fwDir = Path.Combine(bDir, "Mods", "Portraiture");
    var fwConfig = File.ReadAllText(Path.Combine(fwDir, "config.json"));
    Check("B6 框架为纯依赖（托管目录空）→ 配置不被触碰", fwConfig.Contains("Vanilla"), fwConfig.Trim());

    var fakePack = Path.Combine(fwDir, "Portraits", "FakePack");
    Directory.CreateDirectory(fakePack);
    File.WriteAllBytes(Path.Combine(fakePack, "Abigail.png"), TinyPng);
    File.WriteAllBytes(Path.Combine(fakePack, "Emily.png"), TinyPng);
    var scan3 = ps.Scan(bDir);
    ps.SelectSkin(bDir, scan3, "Emily", "JGTest Emily Pack");
    fwConfig = File.ReadAllText(Path.Combine(fwDir, "config.json"));
    Check("B7 托管目录有包但未选 Portraiture → Vanilla", fwConfig.Contains("Vanilla"));

    // v1.7.29：素材包【不再】是打开 HDP 的理由。旧写法把"给艾米丽点一下素材包"变成
    // "全局开启 HD Portraits 桥接模块" ⇒ 任何写了 Mods/HDPortraits/<角色> 的包立刻接管那个
    // NPC 的脸（实机：法师被 [CP] Dacar Rasmodia Portraits 抢走，而元凶在艾米丽那一页）。
    ps.SelectSkin(bDir, scan3, "Emily", "Portraiture/Portraits/FakePack");
    fwConfig = File.ReadAllText(Path.Combine(fwDir, "config.json"));
    Check("B8 选中 Portraiture 素材包 ⇒ 不再把 active 切成 HDP（那条路会劫持别人的 HD 脸）",
        !fwConfig.Contains("HDP"), fwConfig.Trim());

    // 别人/用户自己设的非 HDP 值（素材包名、预设名）—— 我们不猜、不覆盖
    File.WriteAllText(Path.Combine(fwDir, "config.json"), """{"active":"SomeoneElsesPack"}""");
    ps.SelectSkin(bDir, scan3, "Emily", "JGTest Emily Pack");
    fwConfig = File.ReadAllText(Path.Combine(fwDir, "config.json"));
    Check("B8c active 是别人设的第三个值 ⇒ 原样不动（只在 HDP↔Vanilla 之间来回切）",
        fwConfig.Contains("SomeoneElsesPack"), fwConfig.Trim());

    // 素材包真正的出路：转成 CP 肖像包，由覆盖包直接钉 —— 转换判据与手工拖进来的裸图目录同一条
    var setCacheB8 = typeof(StoragePaths).GetProperty("CacheRoot")!.GetSetMethod(nonPublic: true)!;
    var prevCacheB8 = StoragePaths.CacheRoot;
    var b8Cache = Path.Combine(Path.GetTempPath(), "jg-portraiture-convert");
    try { if (Directory.Exists(b8Cache)) Directory.Delete(b8Cache, true); } catch { }
    Directory.CreateDirectory(b8Cache);
    setCacheB8.Invoke(null, new object?[] { b8Cache });
    try
    {
        new ModService().AutoConvertLoosePortraits(bDir);
        var converted = !Directory.Exists(fakePack);
        var backmedUp = Directory.Exists(StoragePaths.ModsBackupDir)
            && Directory.GetDirectories(StoragePaths.ModsBackupDir)
                .Any(d => Path.GetFileName(d).StartsWith("FakePack-raw-", StringComparison.Ordinal));
        // 新包必须落在 Mods\ 顶层：SMAPI 不会再认 Portraiture 子目录里的清单，就地转 = 死包
        var newPack = Path.Combine(bDir, "Mods", "FakePack");
        Check("B8b 素材包目录被转成 CP 肖像包（原目录移走 + 留底进 mods-backup + 新包落在 Mods 顶层）",
            converted && backmedUp
            && File.Exists(Path.Combine(newPack, "manifest.json"))
            && File.Exists(Path.Combine(newPack, "content.json"))
            && File.ReadAllText(Path.Combine(newPack, "manifest.json"))
                .Contains("JuniGrid.PortraitPack.", StringComparison.OrdinalIgnoreCase),
            "原目录还在=" + Directory.Exists(fakePack)
            + " ‖ 留底=" + backmedUp
            + " ‖ 顶层新包清单=" + File.Exists(Path.Combine(newPack, "manifest.json"))
            + " ‖ 顶层新包补丁=" + File.Exists(Path.Combine(newPack, "content.json")));
    }
    finally { setCacheB8.Invoke(null, new object?[] { prevCacheB8 }); }
}

// ── B25 性转包：立绘挂 Wizard、精灵只挂 Magnus / 直铺 Characters/Wizard.png ──
// 用户实测：Romanceable Rasmodia - RRRR Patch 选中后头像换了、右侧仍是男巫师。
// 根因是精灵图没被登记到该皮肤行 → 回退原版精灵。三条防线要一起成立。
{
    var swDir = Path.Combine(Path.GetTempPath(), "jg-rasmodia-test");
    try { if (Directory.Exists(swDir)) Directory.Delete(swDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(swDir, "Mods"));
    byte[] SpritePng = BuildPng(64, 192);   // 走路表形状，稳过 LooksLikeSprite
    byte[] FacePng = BuildPng(128, 128);    // 立绘形状

    // 包 A：立绘 Portraits/Wizard，精灵只写 Characters/Magnus（别名错位）
    var packA = Path.Combine(swDir, "Mods", "JGTest Rasmodia A");
    Directory.CreateDirectory(Path.Combine(packA, "assets", "Characters"));
    File.WriteAllBytes(Path.Combine(packA, "assets", "Wizard.png"), FacePng);
    File.WriteAllBytes(Path.Combine(packA, "assets", "Characters", "Magnus.png"), SpritePng);
    File.WriteAllText(Path.Combine(packA, "manifest.json"),
        """{"Name":"JGTest Rasmodia A","UniqueID":"JuniGrid.Test.RasmodiaA","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(packA, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Wizard","FromFile":"assets/Wizard.png"},{"Action":"Load","Target":"Characters/Magnus","FromFile":"assets/Characters/Magnus.png"}]}""");

    // 包 B：FromFile 写错/带 token，但 assets/Characters/Wizard.png 直铺在目录里
    // （哈希去重已删，包 B 总单列；这里用不同尺寸只是让两张明显区分）
    var packB = Path.Combine(swDir, "Mods", "JGTest Rasmodia B");
    Directory.CreateDirectory(Path.Combine(packB, "assets", "Characters"));
    File.WriteAllBytes(Path.Combine(packB, "assets", "Wizard.png"), BuildPng(256, 256));
    File.WriteAllBytes(Path.Combine(packB, "assets", "Characters", "Wizard.png"), SpritePng);
    File.WriteAllText(Path.Combine(packB, "manifest.json"),
        """{"Name":"JGTest Rasmodia B","UniqueID":"JuniGrid.Test.RasmodiaB","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(packB, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Wizard","FromFile":"assets/Wizard.png"},{"Action":"Load","Target":"Characters/Wizard","FromFile":"assets/NoSuchDir/{Style}/Wizard.png"}]}""");

    var psSw = new PortraitSkinService(new ModService(), cfgSvc);
    var scanSw = psSw.Scan(swDir);
    var wizSw = scanSw.Characters.FirstOrDefault(c => c.Id.Equals("Wizard", StringComparison.OrdinalIgnoreCase));
    var optSwA = wizSw?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Rasmodia A");
    var optSwB = wizSw?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Rasmodia B");
    Check("B25a 精灵只挂 Characters/Magnus → Wizard 皮肤行仍带 SpriteFile（别名兜底）",
        optSwA is not null && optSwA.SpriteFile is not null
        && PortraitSkinService.LooksLikeSprite(optSwA.SpriteFile),
        "包=" + (optSwA is null ? "(没扫到)" : "有")
        + " ‖ Sprite=" + (optSwA?.SpriteFile ?? "(无)")
        + " ‖ HasSprite=" + (optSwA?.HasSprite.ToString() ?? "-"));
    Check("B25b FromFile 失败但 assets/Characters/Wizard.png 直铺 → 兜底搜到精灵",
        optSwB is not null && optSwB.SpriteFile is not null
        && PortraitSkinService.LooksLikeSprite(optSwB.SpriteFile),
        "包=" + (optSwB is null ? "(没扫到)" : "有")
        + " ‖ Sprite=" + (optSwB?.SpriteFile ?? "(无)"));

    // 落盘：选中包 A 后覆盖包必须钉住 Characters/Wizard（不能只写头像）
    if (optSwA is not null && wizSw is not null)
    {
        psSw.SelectSkin(swDir, scanSw, "Wizard", "JGTest Rasmodia A");
        var ovText = File.Exists(Path.Combine(swDir, "Mods", PortraitSkinService.OverrideFolder, "content.json"))
            ? File.ReadAllText(Path.Combine(swDir, "Mods", PortraitSkinService.OverrideFolder, "content.json"))
            : "";
        Check("B25c 选中性转皮 → 覆盖包同时钉住 Portraits/Wizard 与 Characters/Wizard",
            ovText.Contains("Portraits/Wizard", StringComparison.OrdinalIgnoreCase)
            && ovText.Contains("Characters/Wizard", StringComparison.OrdinalIgnoreCase),
            "含Portraits=" + ovText.Contains("Portraits/Wizard", StringComparison.OrdinalIgnoreCase)
            + " 含Characters=" + ovText.Contains("Characters/Wizard", StringComparison.OrdinalIgnoreCase));
    }
    else
    {
        Check("B25c 选中性转皮 → 覆盖包同时钉住 Portraits/Wizard 与 Characters/Wizard", false,
            "前置 B25a 未过，跳过落盘断言");
    }
    try { Directory.Delete(swDir, true); } catch { }
}

// ── B26 同一个内容包里多画风 → 每个画风一张卡 ──
// 实机缺陷：Donut's Seasonal Anime Portraits SVE（Nexus 30482）的 assets/ 下有 11 个画风子目录，
// Alesia 一个人就有 Dawn / Donut / Donut(OLD) 三套画，选哪套由 CP 配置 token
// assets/{AlesiaPortrait}/{TargetWithoutPath}.png 决定，而 config.json 给的是布尔 true。
// 旧版 ResolveFromFileTokens 见布尔值故意不代换 → FindCharacterAsset 兜底取字母序第一个（Dawn）
// → 整包对 Alesia 只产出 1 张卡，另外两套画在界面上根本不存在、永远选不到
//（实测覆盖包 ~JuniGrid Portrait Overrides/assets/Portraits/Alesia_spring.png 的
//  sha256 = 9b5b4746… 正是 Dawn 那张，Donut=9bac8ccf…、Donut(OLD)=c2c4de4d…）。
{
    var stDir = Path.Combine(Path.GetTempPath(), "jg-style-test");
    try { if (Directory.Exists(stDir)) Directory.Delete(stDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(stDir, "Mods"));

    // 娘家包：mod 角色进立绘页的门票是包里有 EditData Data/Characters 注册（照 B21 的夹具写法）。
    // 画风包故意只做替换、不注册 NPC —— 与 Donut's 对 SVE 角色的真实关系一致
    //（娘家是 SVE 时那张卡才是可选的「皮肤」，娘家行不参与拆画风）。
    var hostPack = Path.Combine(stDir, "Mods", "JGTest Style Host");
    Directory.CreateDirectory(Path.Combine(hostPack, "assets"));
    File.WriteAllBytes(Path.Combine(hostPack, "assets", "JgTestNpc.png"), BuildPng(128, 128, 0));
    File.WriteAllBytes(Path.Combine(hostPack, "assets", "JgTestSolo.png"), BuildPng(128, 128, 0));
    File.WriteAllBytes(Path.Combine(hostPack, "assets", "JgTestNpcSprite.png"), BuildPng(64, 192));
    File.WriteAllBytes(Path.Combine(hostPack, "assets", "JgTestSoloSprite.png"), BuildPng(64, 192));
    File.WriteAllText(Path.Combine(hostPack, "manifest.json"),
        """{"Name":"JGTest Style Host","UniqueID":"JuniGrid.Test.StyleHost","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(hostPack, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestNpc":{"DisplayName":"JgTestNpc","HomeRegion":"Other"},"JgTestSolo":{"DisplayName":"JgTestSolo","HomeRegion":"Other"}}},{"Action":"Load","Target":"Portraits/JgTestNpc","FromFile":"assets/JgTestNpc.png"},{"Action":"Load","Target":"Characters/JgTestNpc","FromFile":"assets/JgTestNpcSprite.png"},{"Action":"Load","Target":"Portraits/JgTestSolo","FromFile":"assets/JgTestSolo.png"},{"Action":"Load","Target":"Characters/JgTestSolo","FromFile":"assets/JgTestSoloSprite.png"}]}""");

    // 三画风包：每个画风一张「明显不同尺寸」的立绘 + 一张走路表精灵（名字带 _Walk_ 前缀段，
    // 免得被 GetSeasonFiles 当成基础立绘的季节兄弟，把立绘解析劫持成 64×192 那张）。
    void MkStyle(string packName, string uid, string charId, string cfgKey,
        (string Dir, int H)[] styles)
    {
        var p = Path.Combine(stDir, "Mods", packName);
        Directory.CreateDirectory(Path.Combine(p, "assets", "Code"));
        // Code/ 是放 CP 子补丁 json 的目录，不是画风 —— 没有该角色的图就该被自然排除
        File.WriteAllText(Path.Combine(p, "assets", "Code", charId + ".json"), "[]");
        foreach (var (dir, h) in styles)
        {
            Directory.CreateDirectory(Path.Combine(p, "assets", dir));
            File.WriteAllBytes(Path.Combine(p, "assets", dir, charId + ".png"), BuildPng(128, h, h / 64));
            File.WriteAllBytes(Path.Combine(p, "assets", dir, charId + "_Walk_Spring.png"),
                BuildPng(64, 192, h / 64));
        }
        File.WriteAllText(Path.Combine(p, "manifest.json"),
            "{\"Name\":\"" + packName + "\",\"UniqueID\":\"" + uid + "\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
        File.WriteAllText(Path.Combine(p, "config.json"), "{\"" + cfgKey + "\": true}");
        File.WriteAllText(Path.Combine(p, "content.json"),
            "{\"Format\":\"2.5\",\"ConfigSchema\":{\"" + cfgKey + "\":{\"AllowValues\":\"a, b, c\",\"Default\":\"a\"}},\"Changes\":"
            + "[{\"Action\":\"EditImage\",\"Target\":\"Portraits/" + charId + "\",\"FromFile\":\"assets/{{"
            + cfgKey + "}}/{{TargetWithoutPath}}.png\"}]}");
    }
    MkStyle("JGTest Style Pack", "JuniGrid.Test.StylePack", "JgTestNpc", "JgTestPortrait",
        new[] { ("Dawn", 256), ("Donut", 320), ("Old", 384) });
    MkStyle("JGTest Solo Style Pack", "JuniGrid.Test.SoloStylePack", "JgTestSolo", "JgTestSoloPortrait",
        new[] { ("Only", 256) });

    var psSt = new PortraitSkinService(new ModService(), cfgSvc);
    var scanSt = psSt.Scan(stDir);
    var npcSt = scanSt.Characters.FirstOrDefault(c => c.Id == "JgTestNpc");
    var soloSt = scanSt.Characters.FirstOrDefault(c => c.Id == "JgTestSolo");
    var styleOpts = npcSt?.Skins.Where(s => s.PackFolder == "JGTest Style Pack").ToList() ?? new();

    Check("B26 一个包内 3 个画风目录 → 扫出 3 张卡，卡名各带画风、Variant 记目录名、精灵跟画风走",
        npcSt is not null && styleOpts.Count == 3
        && string.Join(",", styleOpts.Select(o => o.Variant)) == "Dawn,Donut,Old"
        && styleOpts.All(o => o.PackName.EndsWith(" · " + o.Variant, StringComparison.Ordinal))
        && styleOpts.All(o => o.SpriteFile is not null && PortraitSkinService.LooksLikeSprite(o.SpriteFile)
            && Path.GetDirectoryName(o.SourceFile) == Path.GetDirectoryName(o.SpriteFile))
        // Code/（放子补丁 json、没有该角色图）没被当成画风
        && styleOpts.All(o => Path.GetFileName(Path.GetDirectoryName(o.SourceFile)) != "Code")
        // 娘家行不受影响：仍是宿主包那一条、不拆画风
        && npcSt.Native is { IsNative: true, Variant: null } && npcSt.Native.PackFolder == "JGTest Style Host",
        "卡=" + string.Join(" / ", styleOpts.Select(o => o.PackName + "[" + (o.Variant ?? "null") + "] 立绘="
            + Path.GetFileName(Path.GetDirectoryName(o.SourceFile)) + " 精灵="
            + (o.SpriteFile is null ? "(无)" : Path.GetFileName(o.SpriteFile))))
        + " ‖ 娘家=" + (npcSt?.Native?.PackFolder ?? "(无)")
        + " ‖ 配置键=" + string.Join("|", styleOpts.Select(o => string.Join(",", o.ConfigKeys))));

    Check("B26b 只有一个画风目录的包：仍 1 张卡、Variant 为空、卡名不追加任何后缀（不许漂移）",
        soloSt is not null && soloSt.Skins.Count(s => s.PackFolder == "JGTest Solo Style Pack") == 1
        && soloSt.Skins.Single(s => s.PackFolder == "JGTest Solo Style Pack").Variant is null
        && soloSt.Skins.Single(s => s.PackFolder == "JGTest Solo Style Pack").PackName == "JGTest Solo Style Pack"
        && soloSt.Skins.Single(s => s.PackFolder == "JGTest Solo Style Pack").ConfigKeys
            .Contains("JgTestSoloPortrait"),
        "卡=" + string.Join(" / ", soloSt?.Skins.Select(o => o.PackName + "[" + (o.Variant ?? "null") + "]") ?? Array.Empty<string>()));

    // 选第二张（Donut）：源包 config.json 要写字符串目录名，覆盖包要钉 Donut 那张图
    var artMiSt = typeof(PortraitSkinService).GetMethod("ArtHash",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    string HashOfSt(string? p) => p is null ? "(无)"
        : (string?)artMiSt.Invoke(null, new object?[] { p }) ?? "(解不动)";

    // 立绘可能被纵向平铺补满底图 ⇒ 断言比"源图那么大的一块"：钉的到底是不是这张画，
    // 而不是文件字节是否逐字相同（补高是刻意的，见 CopyPortraitFull 上方注释）。
    bool CoversSt(string? pinPath, string? srcPath)
    {
        if (string.IsNullOrWhiteSpace(pinPath) || string.IsNullOrWhiteSpace(srcPath)) return false;
        if (!File.Exists(pinPath) || !File.Exists(srcPath)) return false;
        var decMi = typeof(PixelKit).GetMethod("DecodePng", BindingFlags.Public | BindingFlags.Static)!;
        var s0 = decMi.Invoke(null, new object?[] { srcPath });
        var p0 = decMi.Invoke(null, new object?[] { pinPath });
        if (s0 is null || p0 is null) return false;
        int W(object o) => (int)o.GetType().GetProperty("Width")!.GetValue(o)!;
        int H(object o) => (int)o.GetType().GetProperty("Height")!.GetValue(o)!;
        byte[] Px(object o) => (byte[])o.GetType().GetProperty("PixelsRgba")!.GetValue(o)!;
        var sw = W(s0); var sh = H(s0);
        if (W(p0) < sw || H(p0) < sh) return false;
        var pp = Px(p0); var pw = W(p0);
        var buf = new byte[(long)sw * sh * 4];
        for (var y = 0; y < sh; y++) Array.Copy(pp, (long)y * pw * 4, buf, (long)y * sw * 4, sw * 4);
        using var sha = System.Security.Cryptography.SHA1.Create();
        var head = BitConverter.GetBytes(sw).Concat(BitConverter.GetBytes(sh)).ToArray();
        sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformFinalBlock(buf, 0, buf.Length);
        return Convert.ToHexString(sha.Hash!) == HashOfSt(srcPath);
    }

    var stylePackDir = Path.Combine(stDir, "Mods", "JGTest Style Pack");
    psSt.SelectSkin(stDir, scanSt, "JgTestNpc", "JGTest Style Pack", "Donut");
    var cfgJsonSt = File.Exists(Path.Combine(stylePackDir, "config.json"))
        ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(stylePackDir, "config.json")))
        : new Newtonsoft.Json.Linq.JObject();
    var tokSt = cfgJsonSt["JgTestPortrait"];
    Check("B26c 选第二张画风卡 → 源包 config.json 里那个键被写成字符串「Donut」（CP 要的是目录名，不是布尔）",
        cfgSvc.Current.PortraitSkins.TryGetValue("JgTestNpc", out var selSt) && selSt == "JGTest Style Pack"
        && cfgSvc.Current.PortraitSkinVariants.TryGetValue("JgTestNpc", out var varSt) && varSt == "Donut"
        && tokSt is not null && tokSt.Type == Newtonsoft.Json.Linq.JTokenType.String
        && (string)tokSt == "Donut",
        "选择=" + cfgSvc.Current.PortraitSkins.GetValueOrDefault("JgTestNpc")
        + " ‖ 画风=" + cfgSvc.Current.PortraitSkinVariants.GetValueOrDefault("JgTestNpc")
        + " ‖ config.json JgTestPortrait=" + tokSt + "(" + tokSt?.Type + ")");

    var ovAssetsSt = Path.Combine(stDir, "Mods", PortraitSkinService.OverrideFolder, "assets");
    var ovPortraitSt = Path.Combine(ovAssetsSt, "Portraits", "JgTestNpc.png");
    Check("B26d 覆盖包用的就是对应画风那张图（不是字母序第一个 Dawn）",
        File.Exists(ovPortraitSt)
        && CoversSt(ovPortraitSt, Path.Combine(stylePackDir, "assets", "Donut", "JgTestNpc.png"))
        && !CoversSt(ovPortraitSt, Path.Combine(stylePackDir, "assets", "Dawn", "JgTestNpc.png")),
        "覆盖包=" + (File.Exists(ovPortraitSt) ? HashOfSt(ovPortraitSt) : "(没写出来)")
        + " ‖ Donut=" + HashOfSt(Path.Combine(stylePackDir, "assets", "Donut", "JgTestNpc.png"))
        + " ‖ Dawn=" + HashOfSt(Path.Combine(stylePackDir, "assets", "Dawn", "JgTestNpc.png")));

    // 作者更新后把 Donut 目录删了 → 回落现有第一张，且**不清用户配置**（对齐 StalePack 做法）
    var donutDirSt = Path.Combine(stylePackDir, "assets", "Donut");
    try { Directory.Delete(donutDirSt, true); } catch { }
    // 换一个新的服务实例再扫：进程内快照 15 秒内直接信任（切页来回不重扫），
    // 复用 psSt 拿到的是删目录**之前**的结果，测不到回落这条路径
    var psSt2 = new PortraitSkinService(new ModService(), cfgSvc);
    var scanSt2 = psSt2.Scan(stDir);
    var npcSt2 = scanSt2.Characters.FirstOrDefault(c => c.Id == "JgTestNpc");
    psSt2.SyncToDisk(stDir, scanSt2, new[] { "JgTestNpc" });
    Check("B26e 选中的画风目录被作者删了 → 回落该包现有第一张，配置与源包 config.json 都不动",
        !Directory.Exists(donutDirSt)
        && npcSt2 is not null && npcSt2.Skins.Count(s => s.PackFolder == "JGTest Style Pack") == 2
        && cfgSvc.Current.PortraitSkinVariants.GetValueOrDefault("JgTestNpc") == "Donut"
        && File.Exists(ovPortraitSt)
        && CoversSt(ovPortraitSt, Path.Combine(stylePackDir, "assets", "Dawn", "JgTestNpc.png"))
        && (string?)Newtonsoft.Json.Linq.JObject
            .Parse(File.ReadAllText(Path.Combine(stylePackDir, "config.json")))["JgTestPortrait"] == "Donut",
        "目录还在=" + Directory.Exists(donutDirSt)
        + " ‖ 剩下画风=" + string.Join(",", npcSt2?.Skins.Where(s => s.PackFolder == "JGTest Style Pack")
            .Select(s => s.Variant) ?? Array.Empty<string>())
        + " ‖ 配置画风=" + cfgSvc.Current.PortraitSkinVariants.GetValueOrDefault("JgTestNpc")
        + " ‖ 覆盖包存在=" + File.Exists(ovPortraitSt)
        + " ‖ 覆盖包哈希=" + (File.Exists(ovPortraitSt) ? HashOfSt(ovPortraitSt) : "-")
        + " ‖ Dawn=" + HashOfSt(Path.Combine(stylePackDir, "assets", "Dawn", "JgTestNpc.png"))
        + " ‖ Old=" + HashOfSt(Path.Combine(stylePackDir, "assets", "Old", "JgTestNpc.png"))
        + " ‖ config=" + Newtonsoft.Json.Linq.JObject
            .Parse(File.ReadAllText(Path.Combine(stylePackDir, "config.json")))["JgTestPortrait"]);

    cfgSvc.Current.PortraitSkinVariants.Remove("JgTestNpc");
    cfgSvc.Current.PortraitSkins.Remove("JgTestNpc");
    cfgSvc.Current.PortraitSkinVariants.Remove("JgTestSolo");
    cfgSvc.Current.PortraitSkins.Remove("JgTestSolo");
    cfgSvc.Current.PortraitVanillaDefaults.Remove("JgTestNpc");
    cfgSvc.Current.PortraitVanillaDefaults.Remove("JgTestSolo");
    try { Directory.Delete(stDir, true); } catch { }
}

// ── B27 互斥 When 分支 = 两套画：立绘与精灵必须来自同一条分支 ──
// 实机缺陷（2026-09-26 用户报"法师乱套了：大头照匹配了另一个 mod 的精灵图"）：
// [CP] Donut's Seasonal Anime Characters SVE/assets/Code/Wizard.json 三条补丁 ——
//   Portraits/Wizard  ← assets/fifadog/…      When {"Rasmodia Patch":"false"}
//   Portraits/Wizard  ← assets/KlevLovins/…   When {"Rasmodia Patch":"true"}
//   Characters/Wizard ← assets/KlevLovins/Sprites/…  When {"Rasmodia Patch":"true"}
// 游戏里只有两种合法组合，我们却把「第一条能解析的脸」和「SpriteFiles 里第一条身体」
// 缝成一张卡 ⇒ fifadog 的脸 + KlevLovins 的身体，覆盖包还把它真钉进游戏。
// 另外那个开关的值是**字符串 "false"**、键名**带空格**，两道过滤都把它踢掉 ⇒
// ConfigKeys 里没有它 ⇒ 点这张卡也不会翻开关。
{
    var brDir = Path.Combine(Path.GetTempPath(), "jg-branch-test");
    try { if (Directory.Exists(brDir)) Directory.Delete(brDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(brDir, "Mods"));

    var brHost = Path.Combine(brDir, "Mods", "JGTest Branch Host");
    Directory.CreateDirectory(Path.Combine(brHost, "assets"));
    File.WriteAllBytes(Path.Combine(brHost, "assets", "JgTestBranch.png"), BuildPng(128, 128, 0));
    File.WriteAllBytes(Path.Combine(brHost, "assets", "JgTestGlow.png"), BuildPng(128, 128, 0));
    File.WriteAllText(Path.Combine(brHost, "manifest.json"),
        """{"Name":"JGTest Branch Host","UniqueID":"JuniGrid.Test.BranchHost","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(brHost, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestBranch":{"DisplayName":"JgTestBranch","HomeRegion":"Other"},"JgTestGlow":{"DisplayName":"JgTestGlow","HomeRegion":"Other"}}},{"Action":"Load","Target":"Portraits/JgTestBranch","FromFile":"assets/JgTestBranch.png"},{"Action":"Load","Target":"Portraits/JgTestGlow","FromFile":"assets/JgTestGlow.png"}]}""");

    var brPack = Path.Combine(brDir, "Mods", "JGTest Branch Pack");
    // 目录名故意取 zFifa / aKlev：作者默认那档（config=false → zFifa）**排在字母序后面**，
    // 这样"没记画风就取第一条"的写法会被测出来（卡片列表按包名重排过，顺序靠不住）。
    Directory.CreateDirectory(Path.Combine(brPack, "assets", "zFifa"));
    Directory.CreateDirectory(Path.Combine(brPack, "assets", "aKlev", "Sprites"));
    File.WriteAllBytes(Path.Combine(brPack, "assets", "zFifa", "JgTestBranch.png"), BuildPng(128, 256, 1));
    File.WriteAllBytes(Path.Combine(brPack, "assets", "aKlev", "JgTestBranch.png"), BuildPng(128, 320, 2));
    File.WriteAllBytes(Path.Combine(brPack, "assets", "aKlev", "Sprites", "JgTestBranch.png"), BuildPng(64, 192, 3));
    // 第二个角色：同一个包也给它画了脸，而且挂的是**同一个包级装饰开关** ——
    // 只有"一个角色选、另一个不选"才会走到"替没选的那个把开关关掉"那条路。
    File.WriteAllBytes(Path.Combine(brPack, "assets", "aKlev", "JgTestGlow.png"), BuildPng(128, 384));
    File.WriteAllText(Path.Combine(brPack, "manifest.json"),
        """{"Name":"JGTest Branch Pack","UniqueID":"JuniGrid.Test.BranchPack","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    // ⚠ 字符串 "false" + 带空格的键名：就是 Donut's 实测那个写法
    File.WriteAllText(Path.Combine(brPack, "config.json"), """{"Ras Patch": "false"}""");
    File.WriteAllText(Path.Combine(brPack, "content.json"),
        """{"Format":"2.5","ConfigSchema":{"Ras Patch":{"AllowValues":"true, false","Default":"false"},"Glow Toggle":{"AllowValues":"true, false","Default":"true"}},"Changes":["""
        + """{"Action":"EditImage","Target":"Portraits/JgTestBranch","FromFile":"assets/zFifa/{{TargetWithoutPath}}.png","When":{"Ras Patch":"false"}},"""
        + """{"Action":"EditImage","Target":"Portraits/JgTestBranch","FromFile":"assets/aKlev/{{TargetWithoutPath}}.png","When":{"Ras Patch":"true","Glow Toggle":true}},"""
        + """{"Action":"EditImage","Target":"Characters/JgTestBranch","FromFile":"assets/aKlev/Sprites/{{TargetWithoutPath}}.png","When":{"Ras Patch":"true"}},"""
        + """{"Action":"EditImage","Target":"Portraits/JgTestGlow","FromFile":"assets/aKlev/{{TargetWithoutPath}}.png","When":{"Ras Patch":"true","Glow Toggle":true}}]}""");

    var psBr = new PortraitSkinService(new ModService(), cfgSvc);
    var scanBr = psBr.Scan(brDir);
    var npcBr = scanBr.Characters.FirstOrDefault(c => c.Id == "JgTestBranch");
    var brOpts = npcBr?.Skins.Where(s => s.PackFolder == "JGTest Branch Pack").ToList() ?? new();
    var artMiBr = typeof(PortraitSkinService).GetMethod("ArtHash",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    string HashBr(string? p) => p is null ? "(无)"
        : (string?)artMiBr.Invoke(null, new object?[] { p }) ?? "(解不动)";
    string DirOf(string? p) => p is null ? "(无)" : Path.GetFileName(Path.GetDirectoryName(p) ?? "");

    Check("B27 一条布尔开关的两条互斥分支 → 两张卡（脸+身体同分支，卡名带母目录名）",
        brOpts.Count == 2
        && string.Join(",", brOpts.Select(o => o.Variant).OrderBy(x => x, StringComparer.Ordinal)) == "false,true"
        && brOpts.All(o => o.VariantConfigKey == "Ras Patch")
        && brOpts.Any(o => o.Variant == "false" && DirOf(o.SourceFile) == "zFifa" && o.SpriteFile is null)
        && brOpts.Any(o => o.Variant == "true" && DirOf(o.SourceFile) == "aKlev"
            && DirOf(o.SpriteFile) == "Sprites")
        && brOpts.All(o => o.PackName.EndsWith(" · zFifa", StringComparison.Ordinal)
            || o.PackName.EndsWith(" · aKlev", StringComparison.Ordinal))
        // 作者默认档（config=false → zFifa）必须被标出来：卡片按包名重排，顺序靠不住
        && brOpts.Single(o => o.Variant == "false").IsCurrentVariant
        && !brOpts.Single(o => o.Variant == "true").IsCurrentVariant,
        "卡=" + string.Join(" / ", brOpts.Select(o => o.PackName + "[值=" + (o.Variant ?? "null")
            + "] 脸=" + DirOf(o.SourceFile) + " 身=" + DirOf(o.SpriteFile)))
        + " ‖ 角色=" + (npcBr is null ? "没上屏" : string.Join(",", npcBr.Skins.Select(s => s.PackFolder)))
        + " ‖ 全部选项=" + (npcBr is null ? "-" : string.Join(",", npcBr.AllOptions.Select(o =>
            (o.IsVanilla ? "官方" : o.IsNative ? "娘家" : "皮肤") + ":" + o.PackFolder + "/"
            + Path.GetFileNameWithoutExtension(o.SourceFile))))
        + " ‖ 配置键=" + string.Join("|", brOpts.Select(o => string.Join(",", o.ConfigKeys))));

    // 选「true」那套 → 源包 config.json 的 "Ras Patch" 要翻成 true，覆盖包钉 aKlev 那张脸
    // 记账写入不该改包目录的修改时间：Mods 页默认按"目录最后修改时间"排，
    // 换肤一次就重排一次 = 用户眼里的"我什么都没干列表自己跳"（实测缺陷）。
    // 先预热一次把覆盖包建出来 —— 第一次创建目录本来就该改时间，不是这个用例要测的。
    psBr.SelectSkin(brDir, scanBr, "JgTestBranch", "JGTest Branch Pack", "false");
    var brPackTimeBefore = Directory.GetLastWriteTime(brPack);
    var brOvDir = Path.Combine(brDir, "Mods", PortraitSkinService.OverrideFolder);
    var brOvTimeBefore = Directory.GetLastWriteTime(brOvDir);
    Thread.Sleep(60);   // 时间戳粒度：不等一下，"没改"可能只是"没到下一格"
    psBr.SelectSkin(brDir, scanBr, "JgTestBranch", "JGTest Branch Pack", "true");
    var brCfg = Newtonsoft.Json.Linq.JObject.Parse(
        File.ReadAllText(Path.Combine(brPack, "config.json")))["Ras Patch"];
    var brOv = Path.Combine(brDir, "Mods", PortraitSkinService.OverrideFolder,
        "assets", "Portraits", "JgTestBranch.png");
    Check("B27b 选某条分支 → 源包 config.json 那个开关被翻成该分支的值（字符串键保持字符串写法），覆盖包用同分支的脸",
        cfgSvc.Current.PortraitSkinVariants.GetValueOrDefault("JgTestBranch") == "true"
        && brCfg is not null && brCfg.ToString() == "true"
        && File.Exists(brOv)
        && HashBr(brOv) == HashBr(Path.Combine(brPack, "assets", "aKlev", "JgTestBranch.png"))
        && HashBr(brOv) != HashBr(Path.Combine(brPack, "assets", "zFifa", "JgTestBranch.png")),
        "画风=" + cfgSvc.Current.PortraitSkinVariants.GetValueOrDefault("JgTestBranch")
        + " ‖ config Ras Patch=" + brCfg + "(" + brCfg?.Type + ")"
        + " ‖ 覆盖包=" + HashBr(brOv)
        + " ‖ aKlev=" + HashBr(Path.Combine(brPack, "assets", "aKlev", "JgTestBranch.png"))
        + " ‖ zFifa=" + HashBr(Path.Combine(brPack, "assets", "zFifa", "JgTestBranch.png")));

    // 记账写入不该改包目录的修改时间：Mods 页默认按"目录最后修改时间"排，
    // 换肤一次就重排一次 = 用户眼里的"我什么都没干列表自己跳"（实测缺陷）。
    Check("B28 换肤不改动包目录/覆盖包目录的修改时间（列表排序不该跟着跳），但 config.json 内容确实写了",
        Directory.GetLastWriteTime(brPack) == brPackTimeBefore
        && Directory.GetLastWriteTime(brOvDir) == brOvTimeBefore
        && brCfg is not null && brCfg.ToString() == "true",
        "源包目录 " + brPackTimeBefore.ToString("HH:mm:ss.fff") + " → "
        + Directory.GetLastWriteTime(brPack).ToString("HH:mm:ss.fff")
        + " ‖ 覆盖包目录 " + brOvTimeBefore.ToString("HH:mm:ss.fff") + " → "
        + (Directory.Exists(brOvDir) ? Directory.GetLastWriteTime(brOvDir).ToString("HH:mm:ss.fff") : "(无)")
        + " ‖ config=" + brCfg);

    // 老配置：只记了包、没记画风（用户选的时候还没有画风功能）⇒ 必须钉"包当前生效那一档"，
    // 不能钉字母序第一条：这里作者默认档 zFifa 恰好排在 aKlev 后面，钉错就会被抓住。
    cfgSvc.Current.PortraitSkinVariants.Remove("JgTestBranch");
    cfgSvc.Current.PortraitSkins["JgTestBranch"] = "JGTest Branch Pack";
    // 把包自己的 config 拨回 false（B27b 刚把它写成 true）⇒ "当前生效那档"= zFifa
    File.WriteAllText(Path.Combine(brPack, "config.json"), """{"Ras Patch": "false"}""");
    var psBr2 = new PortraitSkinService(new ModService(), cfgSvc);
    var scanBr2 = psBr2.Scan(brDir);
    psBr2.SyncToDisk(brDir, scanBr2, new[] { "JgTestBranch" });

    // 立绘可能被纵向平铺补满底图 ⇒ 断言比"源图那么大的一块"：钉的到底是不是这张画，
    // 而不是文件字节是否逐字相同（补高是刻意的，见 CopyPortraitFull 上方注释）。
    bool CoversBr(string? pinPath, string? srcPath)
    {
        if (string.IsNullOrWhiteSpace(pinPath) || string.IsNullOrWhiteSpace(srcPath)) return false;
        if (!File.Exists(pinPath) || !File.Exists(srcPath)) return false;
        var decMi = typeof(PixelKit).GetMethod("DecodePng", BindingFlags.Public | BindingFlags.Static)!;
        var s0 = decMi.Invoke(null, new object?[] { srcPath });
        var p0 = decMi.Invoke(null, new object?[] { pinPath });
        if (s0 is null || p0 is null) return false;
        int W(object o) => (int)o.GetType().GetProperty("Width")!.GetValue(o)!;
        int H(object o) => (int)o.GetType().GetProperty("Height")!.GetValue(o)!;
        byte[] Px(object o) => (byte[])o.GetType().GetProperty("PixelsRgba")!.GetValue(o)!;
        var sw = W(s0); var sh = H(s0);
        if (W(p0) < sw || H(p0) < sh) return false;
        var pp = Px(p0); var pw = W(p0);
        var buf = new byte[(long)sw * sh * 4];
        for (var y = 0; y < sh; y++) Array.Copy(pp, (long)y * pw * 4, buf, (long)y * sw * 4, sw * 4);
        using var sha = System.Security.Cryptography.SHA1.Create();
        var head = BitConverter.GetBytes(sw).Concat(BitConverter.GetBytes(sh)).ToArray();
        sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformFinalBlock(buf, 0, buf.Length);
        return Convert.ToHexString(sha.Hash!) == HashBr(srcPath);
    }

    Check("B27c 只记了包、没记画风（老配置）→ 钉作者默认档 zFifa，不是字母序第一条 aKlev",
        File.Exists(brOv)
        && CoversBr(brOv, Path.Combine(brPack, "assets", "zFifa", "JgTestBranch.png"))
        && !CoversBr(brOv, Path.Combine(brPack, "assets", "aKlev", "JgTestBranch.png")),
        "覆盖包=" + HashBr(brOv)
        + " ‖ zFifa=" + HashBr(Path.Combine(brPack, "assets", "zFifa", "JgTestBranch.png"))
        + " ‖ aKlev=" + HashBr(Path.Combine(brPack, "assets", "aKlev", "JgTestBranch.png")));

    // 包级装饰开关（名字里不含角色名）不许被"另一个角色没选这个包"顺手关掉 ——
    // 实测：切一次肖像把用户 config.json 里的 Nose Overlay Toggle / SCA Overwrite 写成了 false。
    // 这里 JgTestBranch 选这个包、JgTestGlow 不选 ⇒ 写 config 时 JgTestGlow 那组键 want=false。
    psBr2.SelectSkin(brDir, scanBr2, "JgTestBranch", "JGTest Branch Pack", "true");
    var glowCfg = Newtonsoft.Json.Linq.JObject.Parse(
        File.ReadAllText(Path.Combine(brPack, "config.json")));
    Check("B29 只关「名字里带这个角色」的开关；包级装饰开关（Glow Toggle）不因别的角色没选而被关掉",
        glowCfg["Glow Toggle"] is not null && glowCfg["Glow Toggle"]!.ToString() == "True",
        "Glow Toggle = " + glowCfg["Glow Toggle"] + "（应为 True，被误关则是 False）"
        + " ‖ Ras Patch=" + glowCfg["Ras Patch"]);

    cfgSvc.Current.PortraitSkinVariants.Remove("JgTestBranch");
    cfgSvc.Current.PortraitSkins.Remove("JgTestBranch");
    cfgSvc.Current.PortraitVanillaDefaults.Remove("JgTestBranch");
    try { Directory.Delete(brDir, true); } catch { }
}

// v1.7.11：只往 Characters 上贴一小块差分的补丁包（RRR 补丁的花舞节身子）不许被算成
// "这个包换了这个角色的走路表"。旧写法把包级 SpriteChars 当角标依据 ⇒ 卡片写着"含精灵图"、
// 右侧却只能去借别家 mod 的身体（实测法师）。
{
    var poDir = Path.Combine(Path.GetTempPath(), "jg-patchonly-test");
    try { if (Directory.Exists(poDir)) Directory.Delete(poDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(poDir, "Mods"));

    var poHost = Path.Combine(poDir, "Mods", "JGTest Patch Host");
    Directory.CreateDirectory(Path.Combine(poHost, "assets"));
    File.WriteAllBytes(Path.Combine(poHost, "assets", "JgTestPatch.png"), BuildPng(128, 128, 0));
    File.WriteAllText(Path.Combine(poHost, "manifest.json"),
        """{"Name":"JGTest Patch Host","UniqueID":"JuniGrid.Test.PatchHost","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(poHost, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestPatch":{"DisplayName":"JgTestPatch","HomeRegion":"Other"}}},{"Action":"Load","Target":"Portraits/JgTestPatch","FromFile":"assets/JgTestPatch.png"}]}""");

    var poPack = Path.Combine(poDir, "Mods", "JGTest Patch Only");
    Directory.CreateDirectory(Path.Combine(poPack, "assets"));
    File.WriteAllBytes(Path.Combine(poPack, "assets", "Face32.png"), BuildPng(128, 640));
    File.WriteAllBytes(Path.Combine(poPack, "assets", "sprite_patch.png"), BuildPng(64, 64, 5));
    File.WriteAllText(Path.Combine(poPack, "manifest.json"),
        """{"Name":"JGTest Patch Only","UniqueID":"JuniGrid.Test.PatchOnly","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(poPack, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditImage","Target":"Characters/JgTestPatch","FromFile":"assets/sprite_patch.png","ToArea":{"X":0,"Y":288,"Width":64,"Height":64}},"""
        + """{"Action":"EditImage","Target":"Portraits/JgTestPatch","FromFile":"assets/Face32.png"}]}""");

    var psPo = new PortraitSkinService(new ModService(), cfgSvc);
    var scanPo = psPo.Scan(poDir);
    var poNpc = scanPo.Characters.FirstOrDefault(c => c.Id == "JgTestPatch");
    var poOpt = poNpc?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Patch Only");
    Check("B30 只贴一小块差分的补丁包 → 这张卡不含精灵图（角标不撒谎、右侧不去借别家的身体）",
        poOpt is not null && poOpt.SpriteFile is null && !poOpt.HasSprite
        && poOpt.SourceFile is not null,
        "卡=" + (poOpt is null ? "(没扫到)" : $"身={poOpt.SpriteFile ?? "(无)"} 含精灵图={poOpt.HasSprite}")
        + " ‖ 角色=" + (poNpc is null ? "没上屏" : string.Join(",", poNpc.AllOptions.Select(o =>
            (o.IsVanilla ? "官方" : o.IsNative ? "娘家" : "皮肤") + ":" + o.PackFolder))));

    RestoreConfig();
    try { Directory.Delete(poDir, true); } catch { }
}

// v1.7.12：只换脸的包选中后，覆盖包不许再去借"娘家/原版"的走路表钉 Characters。
// 旧写法：皮肤没精灵 → 回落 VanillaSpriteXnb / Native.SpriteFile，而覆盖包是
// EditImage+Replace+Priority "Late + 10" ⇒ 压过所有 mod，把前置包配套的身体顶掉。
// 实测法师选 [CP] Romanceable Rasmodia - RRRR Patch：游戏里钉的是原版男身、
// 弹窗预览画的是 SVE 马格努斯、前置包 RomRas 给的却是女性 Witch —— 三方各说一套。
{
    var fbDir = Path.Combine(Path.GetTempPath(), "jg-noborrow-test");
    try { if (Directory.Exists(fbDir)) Directory.Delete(fbDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(fbDir, "Mods"));

    // 娘家包：声明角色 + 立绘 + 走路表（这张走路表就是"前置包配套的身体"）
    var fbBase = Path.Combine(fbDir, "Mods", "JGTest Face Base");
    Directory.CreateDirectory(Path.Combine(fbBase, "assets"));
    File.WriteAllBytes(Path.Combine(fbBase, "assets", "JgTestFace.png"), BuildPng(128, 128, 1));
    File.WriteAllBytes(Path.Combine(fbBase, "assets", "JgTestFace_body.png"), BuildPng(64, 192, 2));
    File.WriteAllText(Path.Combine(fbBase, "manifest.json"),
        """{"Name":"JGTest Face Base","UniqueID":"JuniGrid.Test.FaceBase","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(fbBase, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestFace":{"DisplayName":"JgTestFace","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/JgTestFace","FromFile":"assets/JgTestFace.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestFace","FromFile":"assets/JgTestFace_body.png"}]}""");

    // 只换脸的包：整包就一条 Portraits 补丁，没有任何精灵表
    var fbFace = Path.Combine(fbDir, "Mods", "JGTest Face Only");
    Directory.CreateDirectory(Path.Combine(fbFace, "assets"));
    File.WriteAllBytes(Path.Combine(fbFace, "assets", "NewFace.png"), BuildPng(128, 640, 3));
    File.WriteAllText(Path.Combine(fbFace, "manifest.json"),
        """{"Name":"JGTest Face Only","UniqueID":"JuniGrid.Test.FaceOnly","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(fbFace, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestFace","FromFile":"assets/NewFace.png"}]}""");

    // 只换脸 + 声明前置=娘家包 ⇒ 身体应解析到前置包那张走路表（用户拍板的链第二步）
    var fbDep = Path.Combine(fbDir, "Mods", "JGTest Face Only Dep");
    Directory.CreateDirectory(Path.Combine(fbDep, "assets"));
    // 图尺寸故意各不相同：与 JGTest Face Only 的图只差几像素会被扫描按
    // 「近似同款 ≤5%」折叠进那张卡，用例就空了（实测 Deps 三个包全被折过）
    File.WriteAllBytes(Path.Combine(fbDep, "assets", "DepFace.png"), BuildPng(128, 320, 4));
    File.WriteAllText(Path.Combine(fbDep, "manifest.json"),
        """{"Name":"JGTest Face Only Dep","UniqueID":"JuniGrid.Test.FaceOnlyDep","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"},"Dependencies":[{"UniqueID":"JuniGrid.Test.FaceBase"}]}""");
    File.WriteAllText(Path.Combine(fbDep, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestFace","FromFile":"assets/DepFace.png"}]}""");

    // 只换脸 + 前置没装 ⇒ 链断在第一步，不许钉任何身体（沿用默认）
    var fbDepNone = Path.Combine(fbDir, "Mods", "JGTest Face Only DepNone");
    Directory.CreateDirectory(Path.Combine(fbDepNone, "assets"));
    File.WriteAllBytes(Path.Combine(fbDepNone, "assets", "NoneFace.png"), BuildPng(128, 384, 5));
    File.WriteAllText(Path.Combine(fbDepNone, "manifest.json"),
        """{"Name":"JGTest Face Only DepNone","UniqueID":"JuniGrid.Test.FaceOnlyDepNone","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"},"Dependencies":[{"UniqueID":"JuniGrid.Test.NotInstalled"}]}""");
    File.WriteAllText(Path.Combine(fbDepNone, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestFace","FromFile":"assets/NoneFace.png"}]}""");

    // 只换脸 + 前置是另一个只换脸的包（再往前一跳才是娘家）⇒ 传递解析
    var fbDep2 = Path.Combine(fbDir, "Mods", "JGTest Face Only Dep2");
    Directory.CreateDirectory(Path.Combine(fbDep2, "assets"));
    File.WriteAllBytes(Path.Combine(fbDep2, "assets", "Dep2Face.png"), BuildPng(128, 448, 6));
    File.WriteAllText(Path.Combine(fbDep2, "manifest.json"),
        """{"Name":"JGTest Face Only Dep2","UniqueID":"JuniGrid.Test.FaceOnlyDep2","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"},"Dependencies":[{"UniqueID":"JuniGrid.Test.FaceOnlyDep"}]}""");
    File.WriteAllText(Path.Combine(fbDep2, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestFace","FromFile":"assets/Dep2Face.png"}]}""");

    var psFb = new PortraitSkinService(new ModService(), cfgSvc);
    var scanFb = psFb.Scan(fbDir);
    var fbNpc = scanFb.Characters.FirstOrDefault(c => c.Id == "JgTestFace");
    var fbSkin = fbNpc?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Face Only");
    var fbOv = Path.Combine(fbDir, "Mods", PortraitSkinService.OverrideFolder, "content.json");
    string FbOv() => File.Exists(fbOv) ? File.ReadAllText(fbOv) : "";
    // 前置：这张皮肤自己确实没精灵，而娘家确实有一张可借 —— 否则用例是空的
    var fbPre = fbSkin is not null && fbSkin.SpriteFile is null
        && fbNpc!.Native?.SpriteFile is not null;
    if (fbPre) psFb.SelectSkin(fbDir, scanFb, "JgTestFace", "JGTest Face Only");
    var fbTxt = FbOv();
    // v1.7.29：身体链 = 自己的精灵 → 祖先前置链的精灵 → 默认行的精灵。
    // 前置那一档逐字节对账要靠这三个，先声明（B31/B33/B34/B35 都用）。
    bool SameBytes(string a, string b) =>
        File.Exists(a) && File.Exists(b) && File.ReadAllBytes(a).AsSpan()
            .SequenceEqual(File.ReadAllBytes(b));
    var fbOvBody = Path.Combine(fbDir, "Mods", PortraitSkinService.OverrideFolder,
        "assets", "Characters", "JgTestFace.png");
    var fbBaseBody = Path.Combine(fbBase, "assets", "JgTestFace_body.png");

    Check("B31 选中只换脸的包、又没声明前置 → 覆盖包钉【默认行】的身体（逐字节=娘家那张，不再让 mod 栈随机顶）",
        fbPre && fbTxt.Contains("Portraits/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && fbTxt.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && SameBytes(fbOvBody, fbBaseBody),
        "皮肤无精灵+娘家有精灵=" + fbPre
        + " ‖ 含Portraits=" + fbTxt.Contains("Portraits/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 含Characters=" + fbTxt.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 钉的身体=娘家那张=" + SameBytes(fbOvBody, fbBaseBody)
        + " ‖ 皮肤行精灵=" + (fbSkin?.SpriteFile ?? "(无)")
        + " ‖ 娘家精灵=" + (fbNpc?.Native?.SpriteFile ?? "(无)"));

    if (fbPre) psFb.SelectSkin(fbDir, scanFb, "JgTestFace", "JGTest Face Only Dep");
    var fbTxtDep = FbOv();
    Check("B33 只换脸的包声明了前置、前置有精灵表 → 覆盖包钉【前置包】的身体（逐字节=前置那张）",
        fbPre && fbTxtDep.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && SameBytes(fbOvBody, fbBaseBody),
        "含Characters=" + fbTxtDep.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 钉的身体=前置那张=" + SameBytes(fbOvBody, fbBaseBody)
        + " ‖ 选项=" + string.Join("|", fbNpc!.AllOptions.Select(o => o.PackFolder))
        + " ‖ Deps=" + string.Join(",", scanFb.PackDeps.Keys)
        + " ‖ 诊断=" + string.Join(" / ", scanFb.Diagnostics.Select(d => d.Id + ":" + d.Reason)));

    if (fbPre) psFb.SelectSkin(fbDir, scanFb, "JgTestFace", "JGTest Face Only DepNone");
    var fbTxtNone = FbOv();
    Check("B34 前置没装 → 链断在第一档，仍钉【默认行】那张（而不是什么都不钉、让别家 mod 顶上去）",
        fbPre && fbTxtNone.Contains("Portraits/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && fbTxtNone.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && SameBytes(fbOvBody, fbBaseBody),
        "含Portraits=" + fbTxtNone.Contains("Portraits/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 含Characters=" + fbTxtNone.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 钉的身体=娘家那张=" + SameBytes(fbOvBody, fbBaseBody));

    if (fbPre) psFb.SelectSkin(fbDir, scanFb, "JgTestFace", "JGTest Face Only Dep2");
    var fbTxtDep2 = FbOv();
    Check("B35 前置自己也没精灵表 → 沿依赖链再往前一跳（传递解析到娘家那张）",
        fbPre && fbTxtDep2.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && SameBytes(fbOvBody, fbBaseBody),
        "含Characters=" + fbTxtDep2.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 钉的身体=前置那张=" + SameBytes(fbOvBody, fbBaseBody));

    // 回默认仍要钉身体：这条不是"不覆盖"，是用户显式要回原版/娘家外观
    if (fbPre) psFb.SelectSkin(fbDir, scanFb, "JgTestFace", null);
    var fbTxt2 = FbOv();
    Check("B31b 回默认 → 覆盖包照旧钉住 Characters（\"不借\"只针对皮肤行，回默认语义没变）",
        fbPre && fbTxt2.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase),
        "含Characters=" + fbTxt2.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase));

    // B32：老用户机器上那份 content.json 是按【旧语义】钉的（Characters/Wizard 还在里面），
    // 而进肖像页不会重写覆盖包、启动自检原来只看"条目在不在" ⇒ 改了规则也不自愈。
    // 落盘格式标记就是为这个：版本不符 → 启动前全量重建。
    if (fbPre) psFb.SelectSkin(fbDir, scanFb, "JgTestFace", "JGTest Face Only");
    var fbOvDir = Path.Combine(fbDir, "Mods", PortraitSkinService.OverrideFolder);
    var fbStamp = Path.Combine(fbOvDir, PortraitSkinService.OverrideFormatFile);
    var fbStampAfterWrite = File.Exists(fbStamp) ? File.ReadAllText(fbStamp).Trim() : "(没写)";
    // 伪造成"旧版本留下的现场"：塞一条 Characters 补丁 + 抹掉格式标记
    var fbJo = Newtonsoft.Json.Linq.JObject.Parse(FbOv());
    ((Newtonsoft.Json.Linq.JArray)fbJo["Changes"]!).Add(new Newtonsoft.Json.Linq.JObject
    {
        ["Action"] = "EditImage", ["Target"] = "Characters/JgTestFace",
        ["FromFile"] = "assets/Characters/JgTestFace.png", ["PatchMode"] = "Replace",
        ["Priority"] = "Late + 10"
    });
    File.WriteAllText(fbOv, fbJo.ToString(Newtonsoft.Json.Formatting.None));
    Directory.CreateDirectory(Path.Combine(fbOvDir, "assets", "Characters"));
    File.WriteAllBytes(Path.Combine(fbOvDir, "assets", "Characters", "JgTestFace.png"), BuildPng(64, 192, 7));
    try { if (File.Exists(fbStamp)) File.Delete(fbStamp); } catch { }
    // ⚠ 启动自检会遍历配置里【所有】已选角色，要求每个都在 content.json 里有条目。
    // 真实配置里那些角色在这个沙箱覆盖包里根本不存在 ⇒ 不管格式标记对不对都会触发全量
    // 重建，用例就成了空的（实测：抹掉标记后 B32 照样 PASS）。这里只留本夹具这一个角色。
    var fbSkins = new Dictionary<string, string>(cfgSvc.Current.PortraitSkins, StringComparer.OrdinalIgnoreCase);
    var fbLocks = new Dictionary<string, string>(cfgSvc.Current.PortraitLocks, StringComparer.OrdinalIgnoreCase);
    cfgSvc.Current.PortraitSkins.Clear();
    cfgSvc.Current.PortraitSkins["JgTestFace"] = "JGTest Face Only";
    cfgSvc.Current.PortraitLocks.Clear();
    psFb.EnsureOverridePackHealthy(fbDir, scanFb);
    foreach (var kv in fbSkins) cfgSvc.Current.PortraitSkins[kv.Key] = kv.Value;
    foreach (var kv in fbLocks) cfgSvc.Current.PortraitLocks[kv.Key] = kv.Value;
    var fbTxt3 = FbOv();
    Check("B32 旧语义留下的现场（伪造的 Characters 条目 + 抹掉格式标记）→ 启动自检按新语义全量重建、重钉成默认行那张",
        fbPre && fbStampAfterWrite == PortraitSkinService.OverrideFormat.ToString()
        && File.Exists(fbStamp) && File.ReadAllText(fbStamp).Trim() == PortraitSkinService.OverrideFormat.ToString()
        && fbTxt3.Contains("Portraits/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && fbTxt3.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && SameBytes(fbOvBody, fbBaseBody),
        "写盘后标记=" + fbStampAfterWrite
        + " ‖ 自愈后标记=" + (File.Exists(fbStamp) ? File.ReadAllText(fbStamp).Trim() : "(没写)")
        + " ‖ 含Portraits=" + fbTxt3.Contains("Portraits/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 含Characters=" + fbTxt3.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 重钉成默认行=" + SameBytes(fbOvBody, fbBaseBody) + "（伪造条目应被覆盖掉）");

    // B32b：格式标记是最新的 ⇒ 进肖像页那次对账必须【什么都不做】。
    // 否则每次进页都全量重建一遍（实测一次几十秒：几百个原版 xnb 要解码 + 上千张图重新对账）。
    var fbJo2 = Newtonsoft.Json.Linq.JObject.Parse(FbOv());
    ((Newtonsoft.Json.Linq.JArray)fbJo2["Changes"]!).Add(new Newtonsoft.Json.Linq.JObject
    {
        ["Action"] = "EditImage", ["Target"] = "Characters/JgTestFace",
        ["FromFile"] = "assets/Characters/JgTestFace.png", ["PatchMode"] = "Replace"
    });
    File.WriteAllText(fbOv, fbJo2.ToString(Newtonsoft.Json.Formatting.None));
    psFb.EnsureOverridePackHealthy(fbDir, scanFb, onlyWhenFormatStale: true);
    Check("B32b 落盘格式没换过 → 进页对账直接返回、不重建（不许每次进肖像页都重钉一遍）",
        FbOv().Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase),
        "塞进去的条目还在=" + FbOv().Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 标记=" + (File.Exists(fbStamp) ? File.ReadAllText(fbStamp).Trim() : "(没写)"));

    // B41：锁定「只换脸、身体在前置依赖包里」的包 → 覆盖包要按前置包的身子钉（锁定=未锁定同口径）。
    // 只在"自身无走路图"时触发 ⇒ 自带身子的包（艾米丽/Baechu）不受影响。撤回锁定分支里那段
    // ResolvePrereqBody 借身 → 锁定后 Characters 消失（= 游戏掉回别家/红裙），此用例即 FAIL。
    var fbDepOpt = fbNpc?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Face Only Dep");
    if (fbPre && fbDepOpt is not null)
        psFb.SetLocked(fbDir, scanFb, "JgTestFace", true, fbDepOpt, fbDepOpt.SourceFile, null);
    var fbTxtLock = FbOv();
    Check("B41 锁定「只换脸+前置有身体」的包 → 覆盖包钉【前置包】的身子（锁定与未锁定同口径）",
        fbPre && fbDepOpt is not null
        && fbTxtLock.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        && SameBytes(fbOvBody, fbBaseBody),
        "含Characters=" + fbTxtLock.Contains("Characters/JgTestFace", StringComparison.OrdinalIgnoreCase)
        + " ‖ 身子=前置那张=" + SameBytes(fbOvBody, fbBaseBody)
        + " ‖ 锁定标记=" + (cfgSvc.Current.PortraitLocks.ContainsKey("JgTestFace") ? "有" : "无"));

    RestoreConfig();
    try { Directory.Delete(fbDir, true); } catch { }
}

// ── B42 场合资产尺寸闸（v1.7.17）──
// 实机缺陷（2026-09-28）：覆盖包往 Characters/维克托_冬季、Portraits/卡罗琳_夏季 这类【场合资产】上
// 钉图时从不比对尺寸，一次就扫出 76 条比本尊小的钉法 —— 游戏按固定行距取格会越界，画出来就是
// 说话框空白 / 整人隐身（用户报"变成隐身的了，锁定又能正常显示"）。
// 撤掉 SubstitutedSheetTooSmall 的调用即 FAIL；把【基础资产】也纳入闸同样 FAIL（换肤包的脸本来就
// 比娘家小，那条必须照钉）。
{
    var sgDir = Path.Combine(Path.GetTempPath(), "jg-sizegate-test");
    try { if (Directory.Exists(sgDir)) Directory.Delete(sgDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(sgDir, "Mods"));

    static bool HasTarget(string txt, string target) =>
        System.Text.RegularExpressions.Regex.IsMatch(txt,
            "\"Target\":\\s*\"" + System.Text.RegularExpressions.Regex.Escape(target) + "\"");

    // 娘家：基础立绘/走路 + 一张 64×480 的冬季场合资产（= 闸要比对的"本尊"）
    var sgBase = Path.Combine(sgDir, "Mods", "JGSize Base");
    Directory.CreateDirectory(Path.Combine(sgBase, "assets"));
    File.WriteAllBytes(Path.Combine(sgBase, "assets", "Lance.png"), BuildPng(128, 384, 11));
    File.WriteAllBytes(Path.Combine(sgBase, "assets", "Lance_body.png"), BuildPng(64, 480, 12));
    File.WriteAllBytes(Path.Combine(sgBase, "assets", "Lance_Winter_body.png"), BuildPng(64, 480, 13));
    File.WriteAllText(Path.Combine(sgBase, "manifest.json"),
        """{"Name":"JGSize Base","UniqueID":"JuniGrid.Test.SizeBase","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(sgBase, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"Lance":{"DisplayName":"Lance","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/Lance","FromFile":"assets/Lance.png"},"""
        + """{"Action":"Load","Target":"Characters/Lance","FromFile":"assets/Lance_body.png"},"""
        + """{"Action":"Load","Target":"Characters/Lance_Winter","FromFile":"assets/Lance_Winter_body.png"}]}""");

    // 换肤包甲：只有 64×128 的走路表，没有冬季那张 ⇒ 顶上去比本尊小 = 越界
    var sgSmall = Path.Combine(sgDir, "Mods", "JGSize Small");
    Directory.CreateDirectory(Path.Combine(sgSmall, "assets"));
    File.WriteAllBytes(Path.Combine(sgSmall, "assets", "SmallFace.png"), BuildPng(128, 256, 14));
    File.WriteAllBytes(Path.Combine(sgSmall, "assets", "SmallBody.png"), BuildPng(64, 128, 15));
    File.WriteAllText(Path.Combine(sgSmall, "manifest.json"),
        """{"Name":"JGSize Small","UniqueID":"JuniGrid.Test.SizeSmall","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(sgSmall, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Lance","FromFile":"assets/SmallFace.png"},"""
        + """{"Action":"Load","Target":"Characters/Lance","FromFile":"assets/SmallBody.png"}]}""");

    // 换肤包乙：自己也登记了冬季那张场合资产（尺寸与娘家不同）⇒ 那是本尊，闸不许拦
    var sgOwn = Path.Combine(sgDir, "Mods", "JGSize Own");
    Directory.CreateDirectory(Path.Combine(sgOwn, "assets"));
    File.WriteAllBytes(Path.Combine(sgOwn, "assets", "OwnFace.png"), BuildPng(128, 320, 16));
    File.WriteAllBytes(Path.Combine(sgOwn, "assets", "OwnBody.png"), BuildPng(64, 224, 17));
    File.WriteAllBytes(Path.Combine(sgOwn, "assets", "OwnBody_Winter.png"), BuildPng(64, 224, 18));
    File.WriteAllText(Path.Combine(sgOwn, "manifest.json"),
        """{"Name":"JGSize Own","UniqueID":"JuniGrid.Test.SizeOwn","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(sgOwn, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Lance","FromFile":"assets/OwnFace.png"},"""
        + """{"Action":"Load","Target":"Characters/Lance","FromFile":"assets/OwnBody.png"},"""
        + """{"Action":"Load","Target":"Characters/Lance_Winter","FromFile":"assets/OwnBody_Winter.png"}]}""");

    var psSg = new PortraitSkinService(new ModService(), cfgSvc);
    var scanSg = psSg.Scan(sgDir);
    var sgNpc = scanSg.Characters.FirstOrDefault(c => c.Id == "Lance");
    string SgOv()
    {
        var p = Path.Combine(sgDir, "Mods", PortraitSkinService.OverrideFolder, "content.json");
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }
    var sgWinterNat = scanSg.VariantAssets.Where(v => v.VariantId == "Lance_Winter")
        .Select(v => v.Kind + "/" + v.VariantId + "←" + Path.GetFileName(v.File)).ToList();
    Check("B42a 扫描登记到冬季场合资产（尺寸闸的前提）",
        sgNpc is not null && sgWinterNat.Count > 0,
        "角色=" + (sgNpc?.Id ?? "(无)") + " ‖ 变体=" + string.Join(",", sgWinterNat));

    psSg.SelectSkin(sgDir, scanSg, "Lance", "JGSize Small");
    var sgT1 = SgOv();
    static int PngH(string f)
    {
        if (!File.Exists(f)) return 0;
        using var fs = File.OpenRead(f);
        var b2 = new byte[24];
        return fs.Read(b2, 0, 24) < 24 ? 0 : b2[20] << 24 | b2[21] << 16 | b2[22] << 8 | b2[23];
    }
    static int PinnedH(string dir, string id) =>
        PngH(Path.Combine(dir, "Mods", PortraitSkinService.OverrideFolder, "assets", "Characters", id + ".png"));
    int sgWinterTall = 0;
    foreach (var nf in sgWinterNat) { var h = PngH(nf); if (h > sgWinterTall) sgWinterTall = h; }
    // v1.7.30：换肤包的走路表比底图矮 ⇒ 现在当作"这个包没有身子"，交给身体链拿娘家那具
    //（见 WalkSheetTooShort）。所以场合那条【可以钉】—— 钉的是娘家自己登记的全尺寸表，
    // 本来就是本尊，不会越界。断言从"不钉"改成"钉的必须是全尺寸那张"，防的还是同一件事。
    Check("B42b 替身比本尊小 → 矮的那张绝不钉（场合/基础都只能用全尺寸表），换肤照旧生效",
        (!HasTarget(sgT1, "Characters/Lance_Winter") || PinnedH(sgDir, "Lance_Winter") >= sgWinterTall)
        && HasTarget(sgT1, "Characters/Lance") && PinnedH(sgDir, "Lance") >= sgWinterTall,
        "含Winter=" + HasTarget(sgT1, "Characters/Lance_Winter")
        + " ‖ 含基础=" + HasTarget(sgT1, "Characters/Lance")
        + " ‖ 选项=" + string.Join("|", sgNpc?.AllOptions.Select(o => o.PackFolder) ?? Array.Empty<string>()));

    psSg.SelectSkin(sgDir, scanSg, "Lance", "JGSize Own");
    var sgT2 = SgOv();
    Check("B42c 钉的是本包自己登记的那张 → 闸放行（尺寸不同也算本尊）",
        HasTarget(sgT2, "Characters/Lance_Winter"),
        "含Winter=" + HasTarget(sgT2, "Characters/Lance_Winter")
        + " ‖ 该资产原生=" + string.Join(",", sgWinterNat));

    RestoreConfig();
    try { Directory.Delete(sgDir, true); } catch { }
}

// ── B43 转换包自愈（v1.7.18）──
// 实机缺陷（2026-09-28）：裸图包转 CP 时，精灵那组因为唯一带角色名的文件叫 Caroline_Vanilla.png
// 认不出角色 ⇒ 整组不补前缀 ⇒ 写出 Characters/Spring、Characters/Caroline_Vanilla 这种游戏里
// 不存在的资产名（真身是 Characters/Caroline）。转换只发生在安装那一刻，所以规则修好之后
// 已装的包一直是坏的 —— 用户看到的就是"代码都改了为什么还是全错"。
// 自愈必须【只治病包】：撤掉 HealConvertedPortraitPacks 的调用 → B43a/b FAIL；
// 把"只在裸名被改名时才动"那道闸放开（无条件重算）→ B43c FAIL（带 Priority 的老包被削掉优先级）；
// 把补前缀的第二道闸（裸名整串都是外观后缀）拿掉 → B43a/B43d FAIL（平铺多角色包里的
// Bear / AnsweringMachine 被改挂成 Abigail_Bear / Abigail_AnsweringMachine）。
{
    var chDir = Path.Combine(Path.GetTempPath(), "jg-convheal-test");
    try { if (Directory.Exists(chDir)) Directory.Delete(chDir, true); } catch { }
    var chMods = Path.Combine(chDir, "Mods");
    Directory.CreateDirectory(chMods);

    // 病包：立绘那半已经是带角色名的（上一版转换器修的），精灵那半还是裸名 + Caroline_Vanilla
    var chBroken = Path.Combine(chMods, "JGConv Broken");
    Directory.CreateDirectory(Path.Combine(chBroken, "assets", "Portraits"));
    Directory.CreateDirectory(Path.Combine(chBroken, "assets", "Sprites"));
    foreach (var n in new[] { "Caroline", "Spring", "Summer", "Fall", "Aerobics" })
        File.WriteAllBytes(Path.Combine(chBroken, "assets", "Portraits", n + ".png"), BuildPng(128, 384, 21));
    foreach (var n in new[] { "Caroline_Vanilla", "Spring", "Summer", "Fall", "Beach" })
        File.WriteAllBytes(Path.Combine(chBroken, "assets", "Sprites", n + ".png"), BuildPng(64, 416, 22));
    File.WriteAllText(Path.Combine(chBroken, "manifest.json"),
        """{"Name":"JGConv Broken","UniqueID":"JuniGrid.PortraitPack.AAAA000001","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    static string ChEdit(string target, string from, string? season = null, string? priority = null) =>
        "{\"Action\":\"EditImage\",\"Target\":\"" + target + "\",\"FromFile\":\"" + from + "\",\"PatchMode\":\"Replace\""
        + (priority is null ? "" : ",\"Priority\":\"" + priority + "\"")
        + (season is null ? "" : ",\"When\":{\"Season\":\"" + season + "\"}") + "}";
    var chBrokenJson = "{\"Format\":\"2.5\",\"Changes\":["
        + ChEdit("Portraits/Caroline", "assets/Portraits/Caroline.png", "winter") + ","
        + ChEdit("Portraits/Caroline", "assets/Portraits/Spring.png", "spring") + ","
        + ChEdit("Portraits/Caroline_Aerobics", "assets/Portraits/Aerobics.png") + ","
        + ChEdit("Characters/Caroline_Vanilla", "assets/Sprites/Caroline_Vanilla.png") + ","
        + ChEdit("Characters/Spring", "assets/Sprites/Spring.png") + ","
        + ChEdit("Characters/Beach", "assets/Sprites/Beach.png") + "]}";
    File.WriteAllText(Path.Combine(chBroken, "content.json"), chBrokenJson);

    // 老包甲：同样有裸名，但每条补丁带 Priority（老版转换器写的）—— 重算会丢优先级，不许碰
    var chPrio = Path.Combine(chMods, "JGConv Priority");
    Directory.CreateDirectory(Path.Combine(chPrio, "assets", "Portraits"));
    File.WriteAllBytes(Path.Combine(chPrio, "assets", "Portraits", "Wizard.png"), BuildPng(128, 384, 23));
    File.WriteAllBytes(Path.Combine(chPrio, "assets", "Portraits", "Spring.png"), BuildPng(128, 384, 24));
    File.WriteAllText(Path.Combine(chPrio, "manifest.json"),
        """{"Name":"JGConv Priority","UniqueID":"JuniGrid.PortraitPack.BBBB000002","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    var chPrioJson = "{\"Format\":\"2.5\",\"Changes\":["
        + ChEdit("Portraits/Wizard", "assets/Portraits/Wizard.png", null, "Late + 10") + ","
        + ChEdit("Portraits/Spring", "assets/Portraits/Spring.png", null, "Late + 10") + "]}";
    File.WriteAllText(Path.Combine(chPrio, "content.json"), chPrioJson);

    // 老包乙：一个文件夹平铺多个角色 + 非角色肖像（stardewvalley anime mods 的形态）—— 一个都不许改挂
    var chMixed = Path.Combine(chMods, "JGConv Mixed");
    Directory.CreateDirectory(Path.Combine(chMixed, "assets"));
    foreach (var n in new[] { "Abigail", "Abigail_Winter", "Bear", "AnsweringMachine" })
        File.WriteAllBytes(Path.Combine(chMixed, "assets", n + ".png"), BuildPng(128, 384, 25));
    File.WriteAllText(Path.Combine(chMixed, "manifest.json"),
        """{"Name":"JGConv Mixed","UniqueID":"JuniGrid.PortraitPack.CCCC000003","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    var chMixedJson = "{\"Format\":\"2.5\",\"Changes\":["
        + ChEdit("Portraits/Abigail", "assets/Abigail.png", "spring, summer, fall") + ","
        + ChEdit("Portraits/Abigail", "assets/Abigail_Winter.png", "winter") + ","
        + ChEdit("Portraits/Bear", "assets/Bear.png") + ","
        + ChEdit("Portraits/AnsweringMachine", "assets/AnsweringMachine.png") + "]}";
    File.WriteAllText(Path.Combine(chMixed, "content.json"), chMixedJson);

    // 老包丙：manifest 是 CP 常见的 JSONC（注释 + 尾逗号）—— 严格解析会抛，
    // 于是每次启动给几十个根本不是肖像包的目录刷"[转换包自愈] X 失败"（实机 24 条），
    // 而且这种写法的【真肖像包】会被漏治。它和 JGConv Broken 同病，必须一起被改写。
    var chJsonc = Path.Combine(chMods, "JGConv Jsonc");
    Directory.CreateDirectory(Path.Combine(chJsonc, "assets", "Sprites"));
    foreach (var n in new[] { "Abigail_Vanilla", "Spring" })
        File.WriteAllBytes(Path.Combine(chJsonc, "assets", "Sprites", n + ".png"), BuildPng(64, 416, 26));
    File.WriteAllText(Path.Combine(chJsonc, "manifest.json"),
        "// 作者手写的注释\r\n"
        + """{"Name":"JGConv Jsonc","UniqueID":"JuniGrid.PortraitPack.DDDD000004","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"},}"""
        + "\r\n");
    var chJsoncJson = "{\"Format\":\"2.5\",\"Changes\":["
        + ChEdit("Characters/Abigail_Vanilla", "assets/Sprites/Abigail_Vanilla.png") + ","
        + ChEdit("Characters/Spring", "assets/Sprites/Spring.png") + "]}";
    File.WriteAllText(Path.Combine(chJsonc, "content.json"), chJsoncJson);

    string Txt(string p) => File.ReadAllText(Path.Combine(p, "content.json"));
    var healedN = ModService.HealConvertedPortraitPacks(chDir);
    var chT = Txt(chBroken);
    var jsoncT = Txt(chJsonc);
    var chJo = Newtonsoft.Json.Linq.JObject.Parse(chT);
    var baseSprites = chJo["Changes"]!.Where(c => (string?)c["Target"] == "Characters/Caroline").ToList();
    var vanillaBase = baseSprites.FirstOrDefault(c =>
        ((string?)c["FromFile"] ?? "").EndsWith("Caroline_Vanilla.png", StringComparison.OrdinalIgnoreCase));
    Check("B43a 只重写有病的那一个包（带 Priority 的、平铺多角色的都不动；JSONC manifest 的照样治）",
        healedN == 2,
        "重写包数=" + healedN);
    Check("B45 manifest 带注释/尾逗号（CP 常见 JSONC）的转换包照样被治，裸名精灵改成真资产",
        jsoncT.Contains("\"Characters/Abigail\"", StringComparison.OrdinalIgnoreCase)
        && !jsoncT.Contains("Characters/Spring", StringComparison.OrdinalIgnoreCase)
        && !jsoncT.Contains("Characters/Abigail_Vanilla", StringComparison.OrdinalIgnoreCase)
        && File.ReadAllText(Path.Combine(chJsonc, ModService.ConvertFormatFile)).Trim()
           == ModService.ConvertFormat.ToString(),
        "现内容=" + jsoncT[..Math.Min(150, jsoncT.Length)]
        + " ‖ 标记=" + (File.Exists(Path.Combine(chJsonc, ModService.ConvertFormatFile))
            ? File.ReadAllText(Path.Combine(chJsonc, ModService.ConvertFormatFile)).Trim() : "(没写)"));
    Check("B43b 裸名精灵改成真资产：Characters/Caroline（基准带 When 冬天），假目标全清",
        chT.Contains("\"Characters/Caroline\"", StringComparison.OrdinalIgnoreCase)
        && !chT.Contains("Characters/Caroline_Vanilla", StringComparison.OrdinalIgnoreCase)
        && !chT.Contains("\"Characters/Spring\"", StringComparison.OrdinalIgnoreCase)
        && vanillaBase is not null && (string?)vanillaBase["When"]?["Season"] == "winter"
        && chT.Contains("\"Characters/Caroline_Beach\"", StringComparison.OrdinalIgnoreCase),
        "含基础=" + chT.Contains("\"Characters/Caroline\"")
        + " ‖ 基准When=" + (string?)vanillaBase?["When"]?["Season"]
        + " ‖ 残留假目标=" + string.Join(",", new[] { "Characters/Caroline_Vanilla", "Characters/Spring", "Characters/Beach" }
            .Where(t => chT.Contains("\"" + t + "\"", StringComparison.OrdinalIgnoreCase)))
        + " ‖ 留底=" + File.Exists(Path.Combine(chBroken, "content.json.bak-junigrid")));
    Check("B43c 带 Priority 的老包原样不动（重算会削掉优先级），只补标记",
        Txt(chPrio) == chPrioJson
        && File.ReadAllText(Path.Combine(chPrio, ModService.ConvertFormatFile)).Trim()
           == ModService.ConvertFormat.ToString(),
        "内容未变=" + (Txt(chPrio) == chPrioJson)
        + " ‖ 标记=" + (File.Exists(Path.Combine(chPrio, ModService.ConvertFormatFile))
            ? File.ReadAllText(Path.Combine(chPrio, ModService.ConvertFormatFile)).Trim() : "(没写)"));
    Check("B43d 平铺多角色包原样不动（Bear/AnsweringMachine 不该挂角色名，冬天那条已是 base+When）",
        Txt(chMixed) == chMixedJson,
        "内容未变=" + (Txt(chMixed) == chMixedJson) + " ‖ 现内容=" + Txt(chMixed)[..Math.Min(120, Txt(chMixed).Length)]);
    var healed2 = ModService.HealConvertedPortraitPacks(chDir);
    Check("B43e 自愈可重复跑：第二轮一个包都不改（标记是闸门）",
        healed2 == 0 && Txt(chBroken) == chT,
        "第二轮重写=" + healed2 + " ‖ 内容稳定=" + (Txt(chBroken) == chT));

    try { Directory.Delete(chDir, true); } catch { }
}

// ── B44 CP 原生 PatchMode:"Overlay" 的叠加层不得当整张走路表登记 ──
// 实机缺陷（2026-09-28）：Seasonal Cute Characters 用一条
//   {"Action":"EditImage","Target":"Characters/Emily_Spring, …, Characters/Emily_Winter_Indoor, …",
//    "FromFile":"assets/{TargetPathOnly}/Emily/Emily_Nose.png","PatchMode":"Overlay"}
// 往 13 个季节资产上叠鼻子。FromFile 是固定路径（不含 {TargetWithoutPath}），旧扫描只挡
// Portraiture 的 `Overlay: true`、没挡 CP 的 PatchMode ⇒ 那张 448 行只有 8 行有像素的鼻子图
// 被登记成 Emily_Winter_Indoor 的「本尊」，落盘按 Replace 钉上去 ⇒ 艾米丽/维克托冬天室内室外
// 整个人隐身（覆盖包里那 6 张图与 Emily_Nose.png 字节完全相同，实测对上）。
{
    var nsDir = Path.Combine(Path.GetTempPath(), "jg-nose-test");
    try { if (Directory.Exists(nsDir)) Directory.Delete(nsDir, true); } catch { }
    var nsMods = Path.Combine(nsDir, "Mods");
    Directory.CreateDirectory(Path.Combine(nsMods, "JGTest Nose", "assets"));

    // 走路表：真图 64×480（changed 不同 ⇒ 字节可分辨），鼻子叠加层同尺寸但内容不同
    File.WriteAllBytes(Path.Combine(nsMods, "JGTest Nose", "assets", "Lance_Spring.png"), BuildPng(64, 480, 11));
    File.WriteAllBytes(Path.Combine(nsMods, "JGTest Nose", "assets", "Lance_Winter_Indoor.png"), BuildPng(64, 480, 22));
    File.WriteAllBytes(Path.Combine(nsMods, "JGTest Nose", "assets", "Lance_Nose.png"), BuildPng(64, 480, 33));
    File.WriteAllBytes(Path.Combine(nsMods, "JGTest Nose", "assets", "Lance.png"), BuildPng(128, 256, 44));
    File.WriteAllText(Path.Combine(nsMods, "JGTest Nose", "manifest.json"),
        """{"Name":"JGTest Nose","UniqueID":"JuniGrid.Test.NoseOverlay","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(nsMods, "JGTest Nose", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Lance","FromFile":"assets/Lance.png"},"""
        + """{"Action":"Load","Target":"Characters/Lance_Spring, Characters/Lance_Winter_Indoor","FromFile":"assets/{TargetWithoutPath}.png"},"""
        + """{"Action":"EditImage","Target":"Characters/Lance_Spring, Characters/Lance_Winter_Indoor","FromFile":"assets/Lance_Nose.png","PatchMode":"Overlay"}]}""");

    var psNS = new PortraitSkinService(new ModService(), cfgSvc);
    var scanNS = psNS.Scan(nsDir);
    var winVar = scanNS.VariantAssets.FirstOrDefault(v =>
        v.Kind == "Characters" && v.VariantId == "Lance_Winter_Indoor");
    var noseUsed = scanNS.VariantAssets
        .Where(v => (v.File ?? "").EndsWith("Lance_Nose.png", StringComparison.OrdinalIgnoreCase))
        .Select(v => v.Kind + "/" + v.VariantId).ToList();

    Check("B44 PatchMode:Overlay 的鼻子图不当走路表登记（场合资产指向真图）",
        winVar.File is not null && winVar.File.EndsWith("Lance_Winter_Indoor.png", StringComparison.OrdinalIgnoreCase)
        && noseUsed.Count == 0,
        "Lance_Winter_Indoor 的本尊=" + Path.GetFileName(winVar.File ?? "(空)")
        + " ‖ 被当本尊登记的鼻子图=" + (noseUsed.Count == 0 ? "无" : string.Join(",", noseUsed)));

    RestoreConfig();
    try { Directory.Delete(nsDir, true); } catch { }
}

// ── B47 包用「基资产 + When:{Season}」表达四季 ⇒ 覆盖包必须逐季钉，不许静态钉一张 ──
// 实机缺陷（用户从 09-27 就报"春夏秋冬都是裸体"，09-28 定位）：Caroline (Overhaul) 的四季
// 全是 Target: Portraits/Caroline + When:{Season:...}，文件只叫 Spring.png/Summer.png/Fall.png
// （不带角色名）⇒ 按文件名找季节图的 GetSeasonFilesForChar 找不到 ⇒ 走"静态钉一张 + Late+10"，
// 把包自己的四季压成同一张（那张还是冬天那张）。撤掉本条修复 ⇒ B47 变红。
{
    var bsDir = Path.Combine(Path.GetTempPath(), "jg-baseseason-test");
    try { if (Directory.Exists(bsDir)) Directory.Delete(bsDir, true); } catch { }
    var bsPack = Path.Combine(bsDir, "Mods", "JGTest BaseSeason");
    Directory.CreateDirectory(Path.Combine(bsPack, "assets"));
    foreach (var (n, seed) in new[] { ("Caroline", 41), ("Spring", 42), ("Summer", 43), ("Fall", 44), ("Winter", 45) })
        File.WriteAllBytes(Path.Combine(bsPack, "assets", n + ".png"), BuildPng(128, 256, seed));
    foreach (var (n, seed) in new[] { ("bCaroline", 51), ("bSpring", 52), ("bSummer", 53), ("bFall", 54), ("bWinter", 55) })
        File.WriteAllBytes(Path.Combine(bsPack, "assets", n + ".png"), BuildPng(64, 480, seed));
    File.WriteAllText(Path.Combine(bsPack, "manifest.json"),
        """{"Name":"JGTest BaseSeason","UniqueID":"JuniGrid.Test.BaseSeason","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    var bsPairs = new[] { ("spring", "Spring", "bSpring"), ("summer", "Summer", "bSummer"),
        ("fall", "Fall", "bFall"), ("winter", "Winter", "bWinter") };
    static string BsOne(string target, string file, string season) =>
        "{\"Action\":\"EditImage\",\"Target\":\"" + target + "\",\"FromFile\":\"assets/" + file
        + ".png\",\"PatchMode\":\"Replace\",\"When\":{\"Season\":\"" + season + "\"}}";
    File.WriteAllText(Path.Combine(bsPack, "content.json"),
        "{\"Format\":\"2.5\",\"Changes\":["
        + string.Join(",", bsPairs.SelectMany(s => new[]
            { BsOne("Portraits/Caroline", s.Item2, s.Item1), BsOne("Characters/Caroline", s.Item3, s.Item1) }))
        // ⚠ 这条是【碰撞源】：另一个资产名 Portraits/Caroline_Spring 的变体补丁，写盘用的文件名
        // 和分季钉的那张只差大小写（Windows 不分）⇒ 单下划线命名时变体那条会把春季钉图
        // 覆盖成基础图（卡罗琳线上实测四条 MD5 全一样）。
        + ",{\"Action\":\"EditImage\",\"Target\":\"Portraits/Caroline_Spring\",\"FromFile\":\"assets/Caroline.png\",\"PatchMode\":\"Replace\"}"
        + "]}");

    var psBS = new PortraitSkinService(new ModService(), cfgSvc);
    var scanBS = psBS.Scan(bsDir);
    psBS.SelectSkin(bsDir, scanBS, "Caroline", "JGTest BaseSeason");
    var bsTxt = File.Exists(Path.Combine(bsDir, "Mods", PortraitSkinService.OverrideFolder, "content.json"))
        ? File.ReadAllText(Path.Combine(bsDir, "Mods", PortraitSkinService.OverrideFolder, "content.json")) : "";
    var bsJo = bsTxt.Length > 0 ? Newtonsoft.Json.Linq.JObject.Parse(bsTxt) : null;
    var facePins = bsJo?["Changes"]!.Where(x => (string?)x["Target"] == "Portraits/Caroline").ToList() ?? new();
    var bodyPins = bsJo?["Changes"]!.Where(x => (string?)x["Target"] == "Characters/Caroline").ToList() ?? new();
    var faceSeasons = string.Join(",", facePins.Select(x => (string?)x["When"]?["Season"] ?? "(无条件)"));
    var noLooseFace = facePins.All(x => (string?)x["When"]?["Season"] is { Length: > 0 });
    // 断言的是【每季钉的字节 == 包为那一季声明的那张图】，不是"四张互不相同"——
    // 有的包四季本来就画同一张，那种包钉成同一张才是对的（上一版断言写错了）。
    var bsAssetDir = Path.Combine(bsDir, "Mods", PortraitSkinService.OverrideFolder, "assets");
    string BsHash(string rel)
    {
        var fp = Path.Combine(bsAssetDir, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fp) ? Convert.ToHexString(System.Security.Cryptography.SHA1
            .HashData(File.ReadAllBytes(fp)))[..12] : "(缺)";
    }
    string SrcHash(string name) => Convert.ToHexString(System.Security.Cryptography.SHA1
        .HashData(File.ReadAllBytes(Path.Combine(bsPack, "assets", name))))[..12];
    var bsWantFace = new[] { ("spring", "Spring.png"), ("summer", "Summer.png"),
        ("fall", "Fall.png"), ("winter", "Winter.png") };
    var bsFaceOk = bsWantFace.All(p => BsHash($"Portraits/Caroline__{p.Item1}.png") == SrcHash(p.Item2));
    var bsBodyOk = bsWantFace.All(p => BsHash($"Characters/Caroline__{p.Item1}.png")
        == SrcHash("b" + p.Item2[..(p.Item2.Length - 4)] + ".png"));
    Check("B47 基资产+When:Season 的包 ⇒ 每季钉的就是包为那一季声明的那张图（变体资产同名也不许覆盖）",
        faceSeasons.Contains("spring") && faceSeasons.Contains("summer")
        && faceSeasons.Contains("fall") && faceSeasons.Contains("winter")
        && noLooseFace && bodyPins.Count >= 4 && bsFaceOk && bsBodyOk,
        "脸=" + faceSeasons + " ‖ 身子条数=" + bodyPins.Count
        + " ‖ 脸四季字节对=" + bsFaceOk + " ‖ 身子四季字节对=" + bsBodyOk
        + " ‖ 春钉=" + BsHash("Portraits/Caroline__spring.png")
        + " 应=" + SrcHash("Spring.png")
        + " ‖ 变体那条写的=" + BsHash("Portraits/Caroline_Spring.png"));

    // 同一批数据喂给【预览端】：弹窗卡片/大图在春 tab 上必须画春季那张，而不是包的基础图
    //（基础图 = Caroline.png = 冬季那张 ⇒ 用户看到的"春 tab 高亮裸体"就是这么来的）。
    var bsOpt = scanBS.Characters.FirstOrDefault(c => c.Id == "Caroline")?.AllOptions
        .FirstOrDefault(o => o.PackFolder == "JGTest BaseSeason");
    var bsPrev = psBS.SeasonFilesFor(scanBS, bsOpt?.SourceFile, "Caroline", "Portraits", bsOpt?.PackFolder);
    Check("B47b 弹窗预览与落盘同一条分季判据（四季各画各的，不再一套图用四季）",
        bsOpt?.SourceFile is not null && bsPrev.Count == 4
        && bsPrev["spring"].EndsWith("Spring.png", StringComparison.OrdinalIgnoreCase)
        && bsPrev["winter"].EndsWith("Winter.png", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(bsPrev["spring"], bsPrev["winter"], StringComparison.OrdinalIgnoreCase),
        "皮肤卡=" + (bsOpt?.SourceFile is null ? "没找到" : Path.GetFileName(bsOpt.SourceFile))
        + " ‖ 预览四季=" + string.Join(",", bsPrev.Select(kv => kv.Key + ":" + Path.GetFileName(kv.Value))));

    RestoreConfig();
    try { Directory.Delete(bsDir, true); } catch { }
}

// ── B48 转换包：冬季拆「室内 / 室外」两张 ⇒ 那是【冬季那张】，不是独立场合资产 ──
// 实机缺陷（2026-09-28，用户指着 assets/assets 目录问的）：卡罗琳 Overhaul 有
// Winter_Indoor.png / Winter_Outdoor.png（穿外套的冬季正图），旧转换器把它们当成
// Portraits/Caroline_Winter_Indoor 这种独立资产发出去 —— 游戏里没那资产 ⇒ 四条死补丁，
// 而基础资产 Portraits/Caroline 的冬季被兜底的 Caroline.png（该包的默认像=裸的）填走
// ⇒ 用户看到的"冬 tab 还是裸体"。
{
    var wtDir = Path.Combine(Path.GetTempPath(), "jg-winterpairs-test");
    try { if (Directory.Exists(wtDir)) Directory.Delete(wtDir, true); } catch { }
    var wtPack = Path.Combine(wtDir, "Mods", "JGConv Winter");
    Directory.CreateDirectory(Path.Combine(wtPack, "assets", "Portraits"));
    Directory.CreateDirectory(Path.Combine(wtPack, "assets", "Sprites"));
    foreach (var n in new[] { "Caroline", "Spring", "Winter_Indoor", "Winter_Outdoor" })
        File.WriteAllBytes(Path.Combine(wtPack, "assets", "Portraits", n + ".png"), BuildPng(128, 384, 61));
    foreach (var n in new[] { "Caroline_Vanilla", "Spring", "Winter_Indoor", "Winter_Outdoor" })
        File.WriteAllBytes(Path.Combine(wtPack, "assets", "Sprites", n + ".png"), BuildPng(64, 416, 62));
    File.WriteAllText(Path.Combine(wtPack, "manifest.json"),
        """{"Name":"JGConv Winter","UniqueID":"JuniGrid.PortraitPack.EEEE000004","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    // 旧版（ConvertFormat=2）写出来的样子：冬季挂在独立资产上，基础像的冬季用兜底那张
    File.WriteAllText(Path.Combine(wtPack, "content.json"),
        """{"Format":"2.5","Changes":["""
        + """{"Action":"EditImage","Target":"Portraits/Caroline","FromFile":"assets/Portraits/Caroline.png","PatchMode":"Replace","When":{"Season":"winter"}}, """
        + """{"Action":"EditImage","Target":"Portraits/Caroline","FromFile":"assets/Portraits/Spring.png","PatchMode":"Replace","When":{"Season":"spring"}}, """
        + """{"Action":"EditImage","Target":"Portraits/Caroline_Winter_Indoor","FromFile":"assets/Portraits/Winter_Indoor.png","PatchMode":"Replace"}, """
        + """{"Action":"EditImage","Target":"Portraits/Caroline_Winter_Outdoor","FromFile":"assets/Portraits/Winter_Outdoor.png","PatchMode":"Replace"}, """
        + """{"Action":"EditImage","Target":"Characters/Caroline_Winter_Indoor","FromFile":"assets/Sprites/Winter_Indoor.png","PatchMode":"Replace"}, """
        + """{"Action":"EditImage","Target":"Characters/Caroline_Winter_Outdoor","FromFile":"assets/Sprites/Winter_Outdoor.png","PatchMode":"Replace"}]}""");

    var healedW = ModService.HealConvertedPortraitPacks(wtDir);
    var wtJo = Newtonsoft.Json.Linq.JObject.Parse(
        File.ReadAllText(Path.Combine(wtPack, "content.json")));
    var wtCh = wtJo["Changes"]!.ToList();
    string? Targ(string name) => wtCh.FirstOrDefault(x => (string?)x["Target"] == name) is { } c
        ? Path.GetFileName((string?)c["FromFile"] ?? "") : null;
    var winterFace = wtCh.Where(x => (string?)x["Target"] == "Portraits/Caroline"
        && (string?)x["When"]?["Season"] == "winter").FirstOrDefault();
    // 基础像那条不是"无条件"，而是带【没被单独画的季节】（本夹具里是夏、秋）——
    // 这才是对的：已单独画的季节用正图，其余季节才用包的默认像兜底。
    var baseFallback = wtCh.FirstOrDefault(x => (string?)x["Target"] == "Portraits/Caroline"
        && ((string?)x["When"]?["Season"] ?? "").Contains("summer", StringComparison.OrdinalIgnoreCase));
    var deadPairs = wtCh.Where(x => ((string?)x["Target"] ?? "").Contains("_Winter_", StringComparison.OrdinalIgnoreCase))
        .Select(x => (string?)x["Target"]!).ToList();
    Check("B48 自愈把误发的冬季室内/室外资产折回基础像的冬季（死补丁清掉、冬季用正图）",
        healedW == 1 && deadPairs.Count == 0
        && (string?)winterFace?["FromFile"] != null
        && Path.GetFileName((string?)winterFace!["FromFile"]!) == "Winter_Outdoor.png"
        && Targ("Portraits/Caroline_Winter_Indoor") is null
        && baseFallback is not null
        && Path.GetFileName((string?)baseFallback["FromFile"]!) == "Caroline.png"
        && ((string?)baseFallback["When"]!["Season"])!.Contains("fall", StringComparison.OrdinalIgnoreCase)
        && wtCh.Any(x => (string?)x["Target"] == "Characters/Caroline"
            && (string?)x["When"]?["Season"] == "winter"),
        "重写包数=" + healedW + " ‖ 残留冬季独立资产=" + (deadPairs.Count == 0 ? "无" : string.Join(",", deadPairs))
        + " ‖ 冬季脸来源=" + (winterFace is null ? "(没有)" : Path.GetFileName((string?)winterFace["FromFile"]!))
        + " ‖ 默认像兜底的季节=" + (baseFallback is null ? "(没有)" : (string?)baseFallback["When"]!["Season"]!));

    try { Directory.Delete(wtDir, true); } catch { }
}

// ── B49 封面只能代表「基资产本身」，不许被场合差分/可选画风顶掉 ──
// 实机缺陷（2026-09-28，用户看着朱丽叶的卡问"我记得这个不是朱丽叶的默认皮吧？"）：
// 上一版为了修卡罗琳，给 PickSource 加了「文件名以季节结尾就大幅提权」。但卡罗琳的真信号是
// 【同一条基资产 + When:{Season}】（记在 BaseSeasonPatches 里），而 East Scarp 的朱丽叶是另一种形态：
//   Load Portraits/Juliet ← assets/Portraits/Juliet/Juliet_base.png   ← 真默认像
//   Load Portraits/Juliet_Winter ← …/Juliet_Winter.png               ← 另一个资产，1.6 Appearance 按季挂
// 两条被 MergeByResolved 并成同一张卡的候选 ⇒ 按文件名提权后冬季像当封面（全机 469 张卡里
// 152 张封面、71 张精灵跟着漂成冬季图）。同时 NyapuPortraits/Juliet.png 因"文件名==角色名"
// 拿满加分，把 base 又顶了一次（旧罚分 800 < 加分 1000）。撤掉本条修复 ⇒ B49 变红。
{
    var daDir = Path.Combine(Path.GetTempPath(), "jg-defaultart-test");
    try { if (Directory.Exists(daDir)) Directory.Delete(daDir, true); } catch { }
    var daPack = Path.Combine(daDir, "Mods", "JGTest DefaultArt");
    Directory.CreateDirectory(Path.Combine(daPack, "assets", "Portraits", "Caroline"));
    Directory.CreateDirectory(Path.Combine(daPack, "assets", "Characters", "Caroline"));
    Directory.CreateDirectory(Path.Combine(daPack, "assets", "NyapuPortraits"));
    Directory.CreateDirectory(Path.Combine(daPack, "assets", "Season"));
    foreach (var (rel, seed) in new[]
    {
        (@"Portraits\Caroline\Caroline_base.png", 61), (@"Portraits\Caroline\Caroline_Winter.png", 62),
        (@"NyapuPortraits\Caroline.png", 63),
        (@"Season\Abigail.png", 64), (@"Season\Abigail_Spring.png", 65), (@"Season\Abigail_Summer.png", 66),
        (@"Season\Abigail_Fall.png", 67), (@"Season\Abigail_Winter.png", 68),
    })
        File.WriteAllBytes(Path.Combine(daPack, "assets", rel), BuildPng(128, 256, seed));
    foreach (var (rel, seed) in new[]
    {
        (@"Characters\Caroline\Caroline_base.png", 71), (@"Characters\Caroline\Caroline_Winter.png", 72),
    })
        File.WriteAllBytes(Path.Combine(daPack, "assets", rel), BuildPng(64, 480, seed));
    File.WriteAllText(Path.Combine(daPack, "manifest.json"),
        """{"Name":"JGTest DefaultArt","UniqueID":"JuniGrid.Test.DefaultArt","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    static string DaLoad(string target, string file) =>
        "{\"Action\":\"Load\",\"Target\":\"" + target + "\",\"FromFile\":\"assets/" + file + ".png\"}";
    File.WriteAllText(Path.Combine(daPack, "content.json"),
        "{\"Format\":\"2.5\",\"Changes\":["
        // ① 朱丽叶形态：基资产无条件 Load，冬季是【另一个资产】
        + DaLoad("Portraits/Caroline", "Portraits/Caroline/Caroline_base") + ","
        + DaLoad("Characters/Caroline", "Characters/Caroline/Caroline_base") + ","
        + DaLoad("Portraits/Caroline_Winter", "Portraits/Caroline/Caroline_Winter") + ","
        + DaLoad("Characters/Caroline_Winter", "Characters/Caroline/Caroline_Winter") + ","
        // ② 作者关掉的可选画风目录，文件名正好等于角色名
        + "{\"Action\":\"EditImage\",\"Target\":\"Portraits/Caroline\",\"FromFile\":"
        + "\"assets/NyapuPortraits/Caroline.png\",\"PatchMode\":\"Replace\"},"
        // ③ 卡罗琳形态：同一条基资产按季分支，另有一条无条件的"原版服装"占位
        + DaLoad("Portraits/Abigail", "Season/Abigail") + ","
        + string.Join(",", new[] { ("spring", "Spring"), ("summer", "Summer"), ("fall", "Fall"), ("winter", "Winter") }
            .Select(s => "{\"Action\":\"EditImage\",\"Target\":\"Portraits/Abigail\",\"FromFile\":\"assets/Season/Abigail_"
                + s.Item2 + ".png\",\"PatchMode\":\"Replace\",\"When\":{\"Season\":\"" + s.Item1 + "\"}}"))
        + "]}");

    var psDA = new PortraitSkinService(new ModService(), cfgSvc);
    var scanDA = psDA.Scan(daDir);
    var daCar = scanDA.Characters.FirstOrDefault(c => c.Id == "Caroline")?.AllOptions
        .FirstOrDefault(o => o.PackFolder == "JGTest DefaultArt");
    var daAbi = scanDA.Characters.FirstOrDefault(c => c.Id == "Abigail")?.AllOptions
        .FirstOrDefault(o => o.PackFolder == "JGTest DefaultArt");
    var daCover = daCar?.SourceFile is null ? "(没出卡)" : Path.GetFileName(daCar.SourceFile);
    var daSpr = daCar?.SpriteFile is null ? "(无)" : Path.GetFileName(daCar.SpriteFile);
    var daAbiCover = daAbi?.SourceFile is null ? "(没出卡)" : Path.GetFileName(daAbi.SourceFile);
    // 出口：冬季那张没被丢掉，仍作为【变体资产】登记着（按季切换/整族钉图还要用它）
    var daWinterKept = scanDA.VariantAssets.Any(va => va.Kind == "Portraits"
        && va.BaseId.Equals("Caroline", StringComparison.OrdinalIgnoreCase)
        && va.VariantId.Equals("Caroline_Winter", StringComparison.OrdinalIgnoreCase));
    Check("B49 封面=基资产的默认像（季节差分资产与同名可选画风都不许顶掉它），且差分资产仍被登记",
        daCover == "Caroline_base.png" && daSpr == "Caroline_base.png"
        && daAbiCover == "Abigail_Spring.png" && daWinterKept,
        "卡萝封面=" + daCover + " ‖ 卡萝精灵=" + daSpr
        + " ‖ 阿比封面(基资产+When:Season)=" + daAbiCover
        + " ‖ 冬季差分仍登记=" + daWinterKept);

    RestoreConfig();
    try { Directory.Delete(daDir, true); } catch { }
}

// ── B50 覆盖包的补丁优先级必须【严格高于】它要覆盖的包，不许并列 ──
// 实机缺陷（2026-09-28 用户截图）：法师四季各锁了一个包，游戏里却始终显示 Female Wizard 的脸
//（截图那张紫发宽檐帽与 assets/Female Wizard/FemWizard.png 逐像素一致）。那个包是
// 我们自己早期版本转出来的裸图包（UniqueID=JuniGrid.PortraitPack.DAB63FB592），它对
// Portraits/Wizard 写的是【无条件 + Priority "Late + 10"】—— 和覆盖包当年那条一模一样。
// CP 里同优先级并列时胜负只看加载顺序（~ 前缀是否真排最后并没有保证），
// 于是"用户在界面上选的那张"会随机输给一个他根本没选的包。
// 自愈护栏规定带 Priority 的老转换包不碰 ⇒ 闸门只能放在选择器这一侧：抬到 Late + 100。
// 撤掉这条修复（把 OverridePriority 改回 "Late + 10"）⇒ B50 变红。
{
    var prDir = Path.Combine(Path.GetTempPath(), "jg-priority-test");
    try { if (Directory.Exists(prDir)) Directory.Delete(prDir, true); } catch { }
    var prMods = Path.Combine(prDir, "Mods");
    Directory.CreateDirectory(Path.Combine(prMods, "JGTest Legacy Conv", "assets"));
    Directory.CreateDirectory(Path.Combine(prMods, "JGTest Alt Skin", "assets"));
    File.WriteAllBytes(Path.Combine(prMods, "JGTest Legacy Conv", "assets", "legacy.png"), BuildPng(128, 64, 81));
    File.WriteAllBytes(Path.Combine(prMods, "JGTest Alt Skin", "assets", "alt.png"), BuildPng(128, 64, 82));
    foreach (var (fold, uid) in new[] { ("JGTest Legacy Conv", "JuniGrid.Test.LegacyConv"), ("JGTest Alt Skin", "JuniGrid.Test.AltSkin") })
        File.WriteAllText(Path.Combine(prMods, fold, "manifest.json"),
            "{\"Name\":\"" + fold + "\",\"UniqueID\":\"" + uid
            + "\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
    // 老转换包的写法：无条件 + Late + 10
    File.WriteAllText(Path.Combine(prMods, "JGTest Legacy Conv", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditImage","Target":"Portraits/Caroline","FromFile":"assets/legacy.png","PatchMode":"Replace","Priority":"Late + 10"}]}""");
    File.WriteAllText(Path.Combine(prMods, "JGTest Alt Skin", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditImage","Target":"Portraits/Caroline","FromFile":"assets/alt.png","PatchMode":"Replace"}]}""");

    var psPR = new PortraitSkinService(new ModService(), cfgSvc);
    var scanPR = psPR.Scan(prDir);
    psPR.SelectSkin(prDir, scanPR, "Caroline", "JGTest Alt Skin");
    var prPath = Path.Combine(prMods, PortraitSkinService.OverrideFolder, "content.json");
    var prArr = File.Exists(prPath)
        ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(prPath))["Changes"]!.ToList() : new();
    // CP 的优先级：Early=-100 / Normal=0 / Late=100，"+N" 再往上加
    static int PrRank(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return 0;
        var s = p.Trim();
        var baseRank = s.StartsWith("Early", StringComparison.OrdinalIgnoreCase) ? -100
            : s.StartsWith("Late", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
        var plus = s.IndexOf('+');
        if (plus >= 0 && int.TryParse(s[(plus + 1)..].Trim(), out var off)) baseRank += off;
        return baseRank;
    }
    const int legacyRank = 110;   // "Late + 10"
    var carPins = prArr.Where(x => (string?)x["Target"] == "Portraits/Caroline").ToList();
    var allAbove = carPins.Count > 0 && carPins.All(x => PrRank((string?)x["Priority"]) > legacyRank);
    var pinBytes = carPins.All(x =>
    {
        var rel = ((string?)x["FromFile"] ?? "").Replace("assets/", "").Replace('/', Path.DirectorySeparatorChar);
        var fp = Path.Combine(prMods, PortraitSkinService.OverrideFolder, "assets", rel);
        return File.Exists(fp) && new FileInfo(fp).Length == new FileInfo(Path.Combine(prMods, "JGTest Alt Skin", "assets", "alt.png")).Length;
    });
    Check("B50 覆盖包补丁优先级严格高于自家转换包的 Late + 10（并列=掷硬币，用户选的那张会随机输）",
        PortraitSkinService.OverridePriority == "Late + 100" && allAbove && pinBytes,
        "常量=" + PortraitSkinService.OverridePriority
        + " ‖ 卡萝补丁数=" + carPins.Count
        + " ‖ 全部 >Late+10=" + allAbove
        + " ‖ 钉的是所选包的字节=" + pinBytes
        + " ‖ 实际优先级=" + string.Join(",", carPins.Select(x => (string?)x["Priority"] ?? "(无)")));

    RestoreConfig();
    try { Directory.Delete(prDir, true); } catch { }
}

// ── B51 变体资产要按【包声明的分季表】逐季钉，不许整族钉成封面 ──
// 实机缺陷（2026-09-28，用户："游戏内的精灵图还是原版默认的 大头照是固定春季大头照啊"）：
// [CP] Childhood Sweetheart Caroline 给卡洛琳写了 1.6 的 Data/Characters → Appearance：
//   春 → Portraits/Caroline_Spring、冬室内 → …_Winter_Indoor、冬室外 → …_Winter_Outdoor
// ⇒ 游戏【根本不读】Portraits/Caroline，读的是这些变体资产。
// 而选中的卡罗琳 Overhaul 用"基资产 + When:{Season}"表达四季，文件只叫 Spring.png/Summer.png…
// （不带角色名）⇒ ResolveSeasonalVariantFile 那条"按文件名猜季节"永远猜不出 ⇒ 五张变体
// 全被钉成同一张：覆盖包里 Caroline_Spring/Summer/Fall/Winter_Indoor/Winter_Outdoor 五张 MD5 全等，
// 身体五张全是 Caroline_Vanilla（aff7e697）⇒ 冬天显示春脸 + 四季不换衣（截图脸与
// Portraits/Spring.png 像素距 26.0，存档 currentSeason=winter）。撤掉修复 ⇒ B51 变红。
{
    var vaDir = Path.Combine(Path.GetTempPath(), "jg-variant-season-test");
    try { if (Directory.Exists(vaDir)) Directory.Delete(vaDir, true); } catch { }
    var vaMods = Path.Combine(vaDir, "Mods");
    // A 包：制造"变体资产存在"的事实（等价于青梅竹马包的 Appearance 引用）
    var vaA = Path.Combine(vaMods, "JGTest Appear");
    // B 包：真正选中的皮肤包，四季写在基资产上、文件名不带角色名
    var vaB = Path.Combine(vaMods, "JGTest Unprefixed Season");
    foreach (var d in new[] { vaA, vaB }) Directory.CreateDirectory(Path.Combine(d, "assets"));
    foreach (var (n, seed) in new[] { ("AF_Spring", 91), ("AF_Summer", 92), ("AF_Fall", 93), ("AF_Winter", 94),
        ("AB_Spring", 95), ("AB_Summer", 96), ("AB_Fall", 97), ("AB_Winter", 98) })
        File.WriteAllBytes(Path.Combine(vaA, "assets", n + ".png"),
            BuildPng(n.StartsWith("AF_") ? 128 : 64, n.StartsWith("AF_") ? 64 : 448, seed));
    foreach (var (n, seed) in new[] { ("Spring", 101), ("Summer", 102), ("Fall", 103), ("Winter", 104),
        ("bSpring", 105), ("bSummer", 106), ("bFall", 107), ("bWinter", 108) })
        File.WriteAllBytes(Path.Combine(vaB, "assets", n + ".png"),
            BuildPng(n.StartsWith("b") ? 64 : 128, n.StartsWith("b") ? 448 : 64, seed));
    foreach (var (fold, uid) in new[] { ("JGTest Appear", "JuniGrid.Test.Appear"), ("JGTest Unprefixed Season", "JuniGrid.Test.Unprefixed") })
        File.WriteAllText(Path.Combine(vaMods, fold, "manifest.json"),
            "{\"Name\":\"" + fold + "\",\"UniqueID\":\"" + uid
            + "\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
    static string VaEdit(string target, string file) =>
        "{\"Action\":\"EditImage\",\"Target\":\"" + target + "\",\"FromFile\":\"assets/" + file
        + ".png\",\"PatchMode\":\"Replace\"}";
    var vaSeasons = new[] { ("spring", "Spring", "AF_Spring", "AB_Spring"), ("summer", "Summer", "AF_Summer", "AB_Summer"),
        ("fall", "Fall", "AF_Fall", "AB_Fall"), ("winter", "Winter", "AF_Winter", "AB_Winter") };
    // A 包把四个季节登记成【独立变体资产】（冬季再拆室内/室外，与实机一致）
    File.WriteAllText(Path.Combine(vaA, "content.json"), "{\"Format\":\"2.5\",\"Changes\":["
        + string.Join(",", vaSeasons.Select(s => VaEdit("Portraits/Caroline_" + s.Item2, s.Item3)
            + "," + VaEdit("Characters/Caroline_" + s.Item2, s.Item4)))
        + "," + VaEdit("Portraits/Caroline_Winter_Indoor", "AF_Winter")
        + "," + VaEdit("Characters/Caroline_Winter_Indoor", "AB_Winter")
        + "," + VaEdit("Portraits/Caroline_Winter_Outdoor", "AF_Winter")
        + "," + VaEdit("Characters/Caroline_Winter_Outdoor", "AB_Winter") + "]}");
    // B 包：同一条基资产按 When:{Season} 分支（文件名不带角色名）
    File.WriteAllText(Path.Combine(vaB, "content.json"), "{\"Format\":\"2.5\",\"Changes\":["
        + string.Join(",", vaSeasons.Select(s =>
            "{\"Action\":\"EditImage\",\"Target\":\"Portraits/Caroline\",\"FromFile\":\"assets/" + s.Item2
            + ".png\",\"PatchMode\":\"Replace\",\"When\":{\"Season\":\"" + s.Item1 + "\"}},"
            + "{\"Action\":\"EditImage\",\"Target\":\"Characters/Caroline\",\"FromFile\":\"assets/b" + s.Item2
            + ".png\",\"PatchMode\":\"Replace\",\"When\":{\"Season\":\"" + s.Item1 + "\"}}"))
        + "]}");

    var psVA = new PortraitSkinService(new ModService(), cfgSvc);
    var scanVA = psVA.Scan(vaDir);
    psVA.SelectSkin(vaDir, scanVA, "Caroline", "JGTest Unprefixed Season");
    var vaAssetDir = Path.Combine(vaMods, PortraitSkinService.OverrideFolder, "assets");
    string VaHash(string rel)
    {
        var fp = Path.Combine(vaAssetDir, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fp) ? Convert.ToHexString(System.Security.Cryptography.SHA1
            .HashData(File.ReadAllBytes(fp)))[..12] : "(缺)";
    }
    string VaSrc(string pack, string name) => Convert.ToHexString(System.Security.Cryptography.SHA1
        .HashData(File.ReadAllBytes(Path.Combine(pack, "assets", name))))[..12];
    var vaFacePairs = new[] { ("Spring", "Spring.png"), ("Summer", "Summer.png"), ("Fall", "Fall.png"),
        ("Winter", "Winter.png"), ("Winter_Indoor", "Winter.png"), ("Winter_Outdoor", "Winter.png") };
    var vaFaceOk = vaFacePairs.All(p => VaHash($"Portraits/Caroline_{p.Item1}.png") == VaSrc(vaB, p.Item2));
    var vaBodyPairs = new[] { ("Spring", "bSpring.png"), ("Summer", "bSummer.png"), ("Fall", "bFall.png"),
        ("Winter_Outdoor", "bWinter.png") };
    var vaBodyOk = vaBodyPairs.All(p => VaHash($"Characters/Caroline_{p.Item1}.png") == VaSrc(vaB, p.Item2));
    var vaDistinct = new[] { "Spring", "Summer", "Fall", "Winter" }
        .Select(s => VaHash($"Portraits/Caroline_{s}.png")).Distinct().Count();
    Check("B51 变体资产逐季取【包声明的那一季】（文件名不带角色名也要能对上），不再整族同一张",
        vaFaceOk && vaBodyOk && vaDistinct == 4,
        "脸字节对=" + vaFaceOk + " ‖ 身字节对=" + vaBodyOk + " ‖ 变体脸互不相同=" + vaDistinct + "/4"
        + " ‖ 脸 " + string.Join(" ", vaFacePairs.Select(p => $"{p.Item1}:{VaHash($"Portraits/Caroline_{p.Item1}.png")}/{VaSrc(vaB, p.Item2)}"))
        + " ‖ 身 " + string.Join(" ", vaBodyPairs.Select(p => $"{p.Item1}:{VaHash($"Characters/Caroline_{p.Item1}.png")}/{VaSrc(vaB, p.Item2)}")));

    RestoreConfig();
    try { Directory.Delete(vaDir, true); } catch { }
}

// ── B36 娘家包自己声明多画风（ConfigSchema.AllowValues × DynamicTokens）+ 已有 config.json ──
// 实机缺陷（2026-09-27）：[CP] Miku Mod Plus 的 Miku 是它自带的新 NPC，五套画风全在这一个包里，
// 靠 ConfigSchema "VanillaPortraitChange"（AllowValues 5 档）+ DynamicTokens DT_PortraitFile
// 的互斥 When 分支挑图。用户机器上这个包**有 config.json**（VanillaPortraitChange=SD）⇒
// 立绘补丁 assets/png/{DT_PortraitFile}.png 能按当前 config 解析出单张（src 非空）。
// v1.7.9 拆卡条件原来带 `src is null &&`：一旦 src 解析得出来就跳过拆分，五套画风被压回一张卡
//（实测弹窗只显示"[CP] Miku Mod Plus 默认外观"一条）。声明信号本身足够精确，不该再看 src。
{
    var mkDir = Path.Combine(Path.GetTempPath(), "jg-miku-test");
    try { if (Directory.Exists(mkDir)) Directory.Delete(mkDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(mkDir, "Mods"));

    var mkPack = Path.Combine(mkDir, "Mods", "JGTest Miku Like");
    Directory.CreateDirectory(Path.Combine(mkPack, "assets", "png"));
    // 三档画风立绘：尺寸各不相同，免得被扫描按「近似同款 ≤5%」折叠（实测折叠会让用例空转）
    File.WriteAllBytes(Path.Combine(mkPack, "assets", "png", "MikuPortraits_Official.png"), BuildPng(128, 256, 11));
    File.WriteAllBytes(Path.Combine(mkPack, "assets", "png", "MikuPortraits_SD.png"), BuildPng(128, 320, 12));
    File.WriteAllBytes(Path.Combine(mkPack, "assets", "png", "MikuPortraits_Teitoku.png"), BuildPng(128, 384, 13));
    File.WriteAllBytes(Path.Combine(mkPack, "assets", "png", "JgTestMiku_Sprites.png"), BuildPng(64, 192, 14));
    File.WriteAllText(Path.Combine(mkPack, "manifest.json"),
        """{"Name":"JGTest Miku Like","UniqueID":"JuniGrid.Test.MikuLike","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    // ⚠ 回归触发器：config.json 存在且设为【非默认】值（默认是 Official-like，这里写 SD）。
    // 有它 src 才解析得出来 —— 这正是老条件 `src is null` 把多画风压回一张卡的场景。
    File.WriteAllText(Path.Combine(mkPack, "config.json"), """{"VanillaPortraitChange":"SD"}""");
    File.WriteAllText(Path.Combine(mkPack, "content.json"),
        """{"Format":"2.5","ConfigSchema":{"VanillaPortraitChange":{"AllowValues":"Official-like, SD, Teitoku","Default":"Official-like"}},"DynamicTokens":[{"Name":"DT_PortraitFile","Value":"MikuPortraits_Official","When":{"VanillaPortraitChange":"Official-like"}},"""
        + """{"Name":"DT_PortraitFile","Value":"MikuPortraits_SD","When":{"VanillaPortraitChange":"SD"}},"""
        + """{"Name":"DT_PortraitFile","Value":"MikuPortraits_Teitoku","When":{"VanillaPortraitChange":"Teitoku"}}],"Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestMiku":{"DisplayName":"JgTestMiku","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/JgTestMiku","FromFile":"assets/png/{{DT_PortraitFile}}.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestMiku","FromFile":"assets/png/JgTestMiku_Sprites.png"}]}""");

    var psMk = new PortraitSkinService(new ModService(), cfgSvc);
    var scanMk = psMk.Scan(mkDir);
    var mkChar = scanMk.Characters.FirstOrDefault(c => c.Id == "JgTestMiku");
    var mkSkins = mkChar?.Skins.Where(s => s.PackFolder == "JGTest Miku Like").ToList() ?? new();
    // 前置：这个包确实被认成 JgTestMiku 的娘家、且立绘补丁按当前 config 解析得出源文件（src 非空）——
    // 否则用例测不到"有 config 也要拆卡"这条路径
    var mkSrcResolved = mkChar?.Native?.SourceFile is not null;
    Check("B36 娘家包声明多画风 + 已有 config.json（非默认值）→ 仍拆成 默认外观行 + ≥2 张画风卡（src 解析得出来也不许压回一张）",
        mkChar is not null && mkChar.Native is { IsNative: true } && mkSrcResolved
        && mkChar.Native.PackFolder == "JGTest Miku Like"
        && string.Equals(mkChar.Native.Variant, "Official-like", StringComparison.OrdinalIgnoreCase)
        && mkSkins.Count >= 2
        && mkSkins.Select(s => s.Variant).Contains("SD", StringComparer.OrdinalIgnoreCase)
        && mkSkins.Select(s => s.Variant).Contains("Teitoku", StringComparer.OrdinalIgnoreCase),
        "娘家行=" + (mkChar?.Native is null ? "(无)" : mkChar.Native.PackName + "[Variant=" + (mkChar.Native.Variant ?? "null") + "]")
        + " ‖ src解析出来=" + mkSrcResolved + "(" + (mkChar?.Native?.SourceFile is null ? "(无)" : Path.GetFileName(mkChar.Native.SourceFile)) + ")"
        + " ‖ 画风卡=" + string.Join("|", mkSkins.Select(s => s.Variant ?? "null"))
        + " ‖ 诊断=" + string.Join(" / ", scanMk.Diagnostics.Select(d => d.Id + ":" + d.Reason)));

    RestoreConfig();
    try { Directory.Delete(mkDir, true); } catch { }
}

// ── B37 同一个人被 SVE 开了第二个 id（Henchman / SVE_Henchman）→ 合并成一张卡 ──
// 实机缺陷（2026-09-27）：SVE 的 code/NPCs/Henchman.json 把打手登记成独立 NPC「SVE_Henchman」，
// 与原版「Henchman」是同一张脸。但 DisplayName 一个是 "Henchman"、一个是没解析的
// {i18n:Name.Henchman} 回落 id "SVE_Henchman"（不同名），默认立绘一个原版 xnb 一个 SVE png
//（不同文件），id 也不互为前缀（SVE_ 前缀）⇒ VariantGroups 三道分组信号全不命中，页上两张同脸卡。
// 夹具故意让两张立绘**构图差很多**（尺寸/像素都不同）：若靠 dHash 相似(E4)也能并，用例就验不到
// 「同义词表当分组信号」这条新路径是否承重。
{
    var hmDir = Path.Combine(Path.GetTempPath(), "jg-hm-test");
    try { if (Directory.Exists(hmDir)) Directory.Delete(hmDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(hmDir, "Mods"));

    var hmA = Path.Combine(hmDir, "Mods", "JGTest Hm Base");
    Directory.CreateDirectory(Path.Combine(hmA, "assets"));
    File.WriteAllBytes(Path.Combine(hmA, "assets", "HmA.png"), BuildPng(128, 256, 21));
    File.WriteAllBytes(Path.Combine(hmA, "assets", "HmA_body.png"), BuildPng(64, 192, 22));
    File.WriteAllText(Path.Combine(hmA, "manifest.json"),
        """{"Name":"JGTest Hm Base","UniqueID":"JuniGrid.Test.HmBase","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(hmA, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"Henchman":{"DisplayName":"Henchman","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/Henchman","FromFile":"assets/HmA.png"},"""
        + """{"Action":"Load","Target":"Characters/Henchman","FromFile":"assets/HmA_body.png"}]}""");

    var hmB = Path.Combine(hmDir, "Mods", "JGTest Hm SVE");
    Directory.CreateDirectory(Path.Combine(hmB, "assets"));
    File.WriteAllBytes(Path.Combine(hmB, "assets", "HmB.png"), BuildPng(128, 640, 23));
    File.WriteAllBytes(Path.Combine(hmB, "assets", "HmB_body.png"), BuildPng(64, 192, 24));
    File.WriteAllText(Path.Combine(hmB, "manifest.json"),
        """{"Name":"JGTest Hm SVE","UniqueID":"JuniGrid.Test.HmSve","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    // DisplayName 故意与本体不同（模拟 i18n token 没解析、回落成 id 的实机情形）
    File.WriteAllText(Path.Combine(hmB, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"SVE_Henchman":{"DisplayName":"SVE_Henchman","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/SVE_Henchman","FromFile":"assets/HmB.png"},"""
        + """{"Action":"Load","Target":"Characters/SVE_Henchman","FromFile":"assets/HmB_body.png"}]}""");

    var psHm = new PortraitSkinService(new ModService(), cfgSvc);
    var scanHm = psHm.Scan(hmDir);
    var hmChars = scanHm.Characters.Where(c =>
        c.Id.Equals("Henchman", StringComparison.OrdinalIgnoreCase)
        || c.Id.Equals("SVE_Henchman", StringComparison.OrdinalIgnoreCase)).ToList();
    var hmHidden = hmChars.Where(c => c.Hidden).ToList();
    var hmShown = hmChars.Where(c => !c.Hidden).ToList();
    var hmWinner = hmShown.FirstOrDefault();
    // 前置：两张立绘确实不相似（dHash 判不过）⇒ 若仍合并，只能是同义词表的功劳
    Check("B37 同一人被开第二个 id（Henchman/SVE_Henchman，DisplayName 与立绘都不同）→ 合并成一张卡、被并者不上屏、其立绘变成本体的一张皮肤",
        hmChars.Count == 2 && hmHidden.Count == 1 && hmShown.Count == 1
        && hmWinner!.Members.Count == 2
        && hmWinner.Members.Contains("Henchman", StringComparer.OrdinalIgnoreCase)
        && hmWinner.Members.Contains("SVE_Henchman", StringComparer.OrdinalIgnoreCase)
        && hmWinner.AllOptions.Any(o => (o.SourceFile ?? "").EndsWith("HmB.png", StringComparison.OrdinalIgnoreCase))
        && hmWinner.AllOptions.Any(o => (o.SourceFile ?? "").EndsWith("HmA.png", StringComparison.OrdinalIgnoreCase)),
        "条数=" + hmChars.Count + " 隐藏=" + string.Join(",", hmHidden.Select(c => c.Id))
        + " 上屏=" + string.Join(",", hmShown.Select(c => c.Id))
        + " 成员=" + string.Join("+", hmWinner?.Members ?? Array.Empty<string>())
        + " 皮肤源=" + string.Join("|", hmWinner?.AllOptions.Select(o => Path.GetFileName(o.SourceFile ?? "")) ?? Array.Empty<string>()));

    RestoreConfig();
    try { Directory.Delete(hmDir, true); } catch { }
}
// ── B38 同一张肖像的「室内/室外」运行时分支 → 收成一张卡、冬天两张都钉 ──
// 实机缺陷（2026-09-27）：Donut's 的 Claire 冬天有 _Winter_Indoor / _Outdoor 两张（游戏按
// 进屋/出屋切换），但 BranchVariants 把"同一个布尔 When 的两个互斥取值"一律当画风拆卡
// ⇒ 页上两张几乎一样的卡，而一角色只能选一张 → 另一地点冬天没被钉。判据：只有作者
// ConfigSchema 声明过的开关才算画风轴；CP 内置状态 token（IsOutdoors 等）不拆，改由落盘端
// 按文件名把 indoor+outdoor 一起钉。⚠ 皮肤必须放在【非娘家】的独立包里（娘家行不走季节双钉
// 那条写盘路径）—— 与实机一致：SVE 注册 Claire（娘家），Donut 只是打补丁的皮肤包。
// "声明过的布尔开关仍该拆"由 B27 覆盖（B27 用独立皮肤包 + Ras Patch，本次改动后仍 PASS）。
{
    var ioDir = Path.Combine(Path.GetTempPath(), "jg-io-test");
    try { if (Directory.Exists(ioDir)) Directory.Delete(ioDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(ioDir, "Mods"));

    // 娘家包：注册角色 + 给立绘与走路表（角色能上屏的门票）
    var ioHost = Path.Combine(ioDir, "Mods", "JGTest IO Host");
    Directory.CreateDirectory(Path.Combine(ioHost, "assets"));
    File.WriteAllBytes(Path.Combine(ioHost, "assets", "Host.png"), BuildPng(128, 256, 31));
    File.WriteAllBytes(Path.Combine(ioHost, "assets", "Host_body.png"), BuildPng(64, 128, 32));
    File.WriteAllText(Path.Combine(ioHost, "manifest.json"),
        """{"Name":"JGTest IO Host","UniqueID":"JuniGrid.Test.IOSplitHost","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(ioHost, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestIO":{"DisplayName":"JGTest IO","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/JgTestIO","FromFile":"assets/Host.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestIO","FromFile":"assets/Host_body.png"}]}""");

    // 皮肤包：只给立绘 + 冬天室内/室外两条 IsOutdoors 分支（不注册角色 ⇒ 非娘家）
    var ioPack = Path.Combine(ioDir, "Mods", "JGTest IO");
    Directory.CreateDirectory(Path.Combine(ioPack, "assets"));
    File.WriteAllBytes(Path.Combine(ioPack, "assets", "IO_Base.png"), BuildPng(128, 256, 33));
    File.WriteAllBytes(Path.Combine(ioPack, "assets", "JgTestIO_Winter_Outdoor.png"), BuildPng(128, 256, 34));
    File.WriteAllBytes(Path.Combine(ioPack, "assets", "JgTestIO_Winter_Indoor.png"), BuildPng(128, 256, 35));
    File.WriteAllText(Path.Combine(ioPack, "manifest.json"),
        """{"Name":"JGTest IO","UniqueID":"JuniGrid.Test.IOSplit","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    // CP 会把每条 When 键（含 IsOutdoors）自动写进包 config.json —— 夹具照抄这个现实，
    // 否则用例挡不住"判据误用 ConfigValues"这种回归（真·Donut's 的 config.json 里就有 IsOutdoors）。
    File.WriteAllText(Path.Combine(ioPack, "config.json"), """{"IsOutdoors": "true"}""");
    File.WriteAllText(Path.Combine(ioPack, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestIO","FromFile":"assets/IO_Base.png"},"""
        + """{"Action":"EditImage","Target":"Portraits/JgTestIO","FromFile":"assets/JgTestIO_Winter_Outdoor.png","When":{"Season":"winter","IsOutdoors":"true"}},"""
        + """{"Action":"EditImage","Target":"Portraits/JgTestIO","FromFile":"assets/JgTestIO_Winter_Indoor.png","When":{"Season":"winter","IsOutdoors":"false"}}]}""");

    var psIO = new PortraitSkinService(new ModService(), cfgSvc);
    var scanIO = psIO.Scan(ioDir);
    var ioChar = scanIO.Characters.FirstOrDefault(c => c.Id == "JgTestIO");
    var ioOpts = ioChar?.AllOptions.Where(o => o.PackFolder == "JGTest IO").ToList() ?? new();

    var ioOv = Path.Combine(ioDir, "Mods", PortraitSkinService.OverrideFolder, "content.json");
    if (ioChar is not null) psIO.SelectSkin(ioDir, scanIO, "JgTestIO", "JGTest IO");
    var ioTxt = File.Exists(ioOv) ? File.ReadAllText(ioOv) : "";
    bool pinOut = ioTxt.Contains("\"IsOutdoors\":\"true\"");
    bool pinIn = ioTxt.Contains("\"IsOutdoors\":\"false\"");

    Check("B38 冬天室内/室外（CP 内置 IsOutdoors 分支，独立皮肤包）→ 收成一张卡，且覆盖包把两张都钉（各带 IsOutdoors 条件）",
        ioOpts.Count == 1 && pinOut && pinIn,
        "皮肤卡数=" + ioOpts.Count + " ‖ 钉室外=" + pinOut + " ‖ 钉室内=" + pinIn
        + " ‖ 选项源=" + string.Join("|", ioOpts.Select(o => Path.GetFileName(o.SourceFile ?? ""))));

    RestoreConfig();
    try { Directory.Delete(ioDir, true); } catch { }
}

// ── B39：非角色肖像黑名单 —— AnsweringMachine（电话机）不出卡，同法注册的正常角色照常出 ──
{
    var npDir = Path.Combine(Path.GetTempPath(), "jg-nonperson-test");
    try { if (Directory.Exists(npDir)) Directory.Delete(npDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(npDir, "Mods"));

    // 把"电话机"AnsweringMachine 当 NPC 注册（Data/Characters）并画立绘 —— 这是唯一能进立绘页的形态
    // （裸 Load 会被 804「资产碎片」门滤掉，测不到闸门）。它经"包提供立绘"这条路径进 allCharIds，
    // 而 VanillaPortraitIds 过滤管不到这条路径 ⇒ AnsweringMachine 的缺席只可能来自 allCharIds.ExceptWith
    // (NonPersonPortraits)（撤回那道 ExceptWith 即 FAIL）。同法注册 JgTestGuy 当正对照，证明包确实被解析、
    // 普通自造 NPC 照样出卡 —— 缺席只因黑名单，不是夹具坏了。
    var npPack = Path.Combine(npDir, "Mods", "JGTest NonPerson");
    Directory.CreateDirectory(Path.Combine(npPack, "assets"));
    File.WriteAllBytes(Path.Combine(npPack, "assets", "Phone.png"), BuildPng(128, 256, 41));
    File.WriteAllBytes(Path.Combine(npPack, "assets", "Guy.png"), BuildPng(128, 256, 42));
    File.WriteAllText(Path.Combine(npPack, "manifest.json"),
        """{"Name":"JGTest NonPerson","UniqueID":"JuniGrid.Test.NonPerson","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(npPack, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"AnsweringMachine":{"DisplayName":"Answering Machine","HomeRegion":"Other"},"JgTestGuy":{"DisplayName":"JGTest Guy","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/AnsweringMachine","FromFile":"assets/Phone.png"},"""
        + """{"Action":"Load","Target":"Portraits/JgTestGuy","FromFile":"assets/Guy.png"}]}""");

    var psNP = new PortraitSkinService(new ModService(), cfgSvc);
    var scanNP = psNP.Scan(npDir);
    bool phoneGone = !scanNP.Characters.Any(c => c.Id.Equals("AnsweringMachine", StringComparison.OrdinalIgnoreCase));
    bool guyShown = scanNP.Characters.Any(c => c.Id == "JgTestGuy");
    Check("B39 包把 AnsweringMachine（电话机，非角色）注册成 NPC 并画立绘 → 不出卡；同法注册的 JgTestGuy 照常出卡",
        phoneGone && guyShown,
        "电话机已隐藏=" + phoneGone + " ‖ 正常角色出卡=" + guyShown
        + " ‖ 卡=" + string.Join(",", scanNP.Characters.Select(c => c.Id)));

    RestoreConfig();
    try { Directory.Delete(npDir, true); } catch { }
}

// ── B40：只有变体资产（无 base 立绘）的整修包 → 代表图挑普通季节(春)，不挑排最前的事件换装 ──
{
    var vrDir = Path.Combine(Path.GetTempPath(), "jg-variant-rep-test");
    try { if (Directory.Exists(vrDir)) Directory.Delete(vrDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(vrDir, "Mods"));
    var vrPack = Path.Combine(vrDir, "Mods", "JGTest VarPack");
    Directory.CreateDirectory(Path.Combine(vrPack, "assets"));
    // 故意把"事件换装"放列表最前 —— 复现 [CP] Childhood Sweetheart Caroline 的 Caroline_Aerobics
    // 被当封面的 bug（无 base、无修复时 PickSource 按 idx 选到第一条 Aerobics）。
    File.WriteAllBytes(Path.Combine(vrPack, "assets", "JgTestVar_Aerobics.png"), BuildPng(128, 256, 51));
    File.WriteAllBytes(Path.Combine(vrPack, "assets", "JgTestVar_Beach.png"), BuildPng(128, 256, 52));
    File.WriteAllBytes(Path.Combine(vrPack, "assets", "JgTestVar_Summer.png"), BuildPng(128, 256, 53));
    File.WriteAllBytes(Path.Combine(vrPack, "assets", "JgTestVar_Spring.png"), BuildPng(128, 256, 54));
    File.WriteAllBytes(Path.Combine(vrPack, "assets", "JgTestVar_body.png"), BuildPng(64, 192, 55));
    File.WriteAllText(Path.Combine(vrPack, "manifest.json"),
        """{"Name":"JGTest VarPack","UniqueID":"JuniGrid.Test.VarRep","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(vrPack, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestVar":{"DisplayName":"JGTest Var","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/JgTestVar_Aerobics","FromFile":"assets/JgTestVar_Aerobics.png"},"""
        + """{"Action":"Load","Target":"Portraits/JgTestVar_Beach","FromFile":"assets/JgTestVar_Beach.png"},"""
        + """{"Action":"Load","Target":"Portraits/JgTestVar_Summer","FromFile":"assets/JgTestVar_Summer.png"},"""
        + """{"Action":"Load","Target":"Portraits/JgTestVar_Spring","FromFile":"assets/JgTestVar_Spring.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestVar","FromFile":"assets/JgTestVar_body.png"}]}""");

    var psVR = new PortraitSkinService(new ModService(), cfgSvc);
    var scanVR = psVR.Scan(vrDir);
    var vrChar = scanVR.Characters.FirstOrDefault(c => c.Id == "JgTestVar");
    var rep = Path.GetFileNameWithoutExtension(vrChar?.Native?.SourceFile
        ?? vrChar?.Skins.FirstOrDefault()?.SourceFile ?? "");
    Check("B40 只有变体资产(无 base)的包 → 代表图选普通季节(JgTestVar_Spring)，不选排最前的事件换装(Aerobics/Beach)",
        rep == "JgTestVar_Spring",
        "代表图=" + rep + " ‖ 变体列表(合并后)=" + string.Join("|",
            (vrChar?.AllOptions ?? Enumerable.Empty<PortraitSkinOption>()).Select(o => Path.GetFileNameWithoutExtension(o.SourceFile ?? ""))));

    RestoreConfig();
    try { Directory.Delete(vrDir, true); } catch { }
}
// ── B52 HD 肖像通道（Mods/HDPortraits/*）：别的包用数据资产把 Portraits/<npc> 整个绕开 ──
// 实机（2026-09-29 法师）：四季钉的都是用户选的那套、像素哈希逐张对得上，游戏对话框里
// 却始终是 [CP] Dacar Rasmodia Portraits 那张 512 宽高清脸 —— Portraiture 的 HDP 模式画的是
// Mods/HDPortraits/Wizard 里 Portrait 指向的资产，【完全不读】Portraits/Wizard。
// 覆盖包必须把那条数据资产指回我们钉的图，否则用户"选什么都不生效"。
{
    var hdDir = Path.Combine(Path.GetTempPath(), "jg-hdp-test");
    try { if (Directory.Exists(hdDir)) Directory.Delete(hdDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(hdDir, "Mods"));

    // 渲染端：装了它，Mods/HDPortraits/* 才真会被画出来（实机就是 Portraiture 的 HDP 模式）
    var hdFw = Path.Combine(hdDir, "Mods", "Portraiture");
    Directory.CreateDirectory(Path.Combine(hdFw, "Portraits", "JGTest Empty Material"));
    File.WriteAllText(Path.Combine(hdFw, "manifest.json"),
        """{"Name":"Portraiture","UniqueID":"Platonymous.Portraiture","Version":"1.0.0","EntryDll":"Portraiture.dll"}""");
    File.WriteAllText(Path.Combine(hdFw, "config.json"), """{"active":"Vanilla"}""");
    string HdActive() => (string?)Newtonsoft.Json.Linq.JObject
        .Parse(File.ReadAllText(Path.Combine(hdFw, "config.json")))["active"] ?? "(无)";

    // 娘家包：注册两个 NPC —— 第二个用来验"用户没选就不许动别人家的 HD 资产"
    var hdHost = Path.Combine(hdDir, "Mods", "JGTest HD Host");
    Directory.CreateDirectory(Path.Combine(hdHost, "assets"));
    File.WriteAllBytes(Path.Combine(hdHost, "assets", "JgTestHd.png"), BuildPng(128, 256, 70));
    File.WriteAllBytes(Path.Combine(hdHost, "assets", "JgTestHd2.png"), BuildPng(128, 256, 71));
    // 娘家带一具走路表：选 HD 包（它自己没有身子）时，身体链必须落到【默认行这具】上并真的钉住
    File.WriteAllBytes(Path.Combine(hdHost, "assets", "JgTestHd_body.png"), BuildPng(64, 192, 74));
    File.WriteAllText(Path.Combine(hdHost, "manifest.json"),
        """{"Name":"JGTest HD Host","UniqueID":"JuniGrid.Test.HdHost","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(hdHost, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestHd":{"DisplayName":"JgTestHd","HomeRegion":"Other"},"JgTestHd2":{"DisplayName":"JgTestHd2","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/JgTestHd","FromFile":"assets/JgTestHd.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestHd","FromFile":"assets/JgTestHd_body.png"},"""
        + """{"Action":"Load","Target":"Portraits/JgTestHd2","FromFile":"assets/JgTestHd2.png"}]}""");

    // HD 提供方：逐条照 [CP] Dacar Rasmodia Portraits 的真实写法 ——
    //   Load  Mods/DacarRasmodia/Wizard ← 512 宽高清表
    //   Load  Mods/HDPortraits/Wizard   ← assets/data/size.json（{"Size":256}）
    //   EditData Mods/HDPortraits/Wizard → Entries.Portrait = Mods/DacarRasmodia/Wizard
    var hdProv = Path.Combine(hdDir, "Mods", "JGTest HD Provider");
    Directory.CreateDirectory(Path.Combine(hdProv, "assets", "data"));
    File.WriteAllBytes(Path.Combine(hdProv, "assets", "HdFace.png"), BuildPng(512, 1024, 72));
    File.WriteAllText(Path.Combine(hdProv, "assets", "data", "size.json"), """{"Size": 256}""");
    File.WriteAllText(Path.Combine(hdProv, "manifest.json"),
        """{"Name":"JGTest HD Provider","UniqueID":"JuniGrid.Test.HdProvider","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(hdProv, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Mods/JgTestHdArt/Face","FromFile":"assets/HdFace.png"},"""
        + """{"Action":"Load","Target":"Mods/HDPortraits/JgTestHd","FromFile":"assets/data/size.json"},"""
        + """{"Action":"Load","Target":"Mods/HDPortraits/JgTestHd2","FromFile":"assets/data/size.json"},"""
        + """{"Action":"EditData","Priority":"Late","Target":"Mods/HDPortraits/JgTestHd, Mods/HDPortraits/JgTestHd2","Entries":{"Portrait":"Mods/JgTestHdArt/Face"}}]}""");

    // 用户真正选的那个皮肤包：128 宽（与原版同格），只换脸
    var hdSkin = Path.Combine(hdDir, "Mods", "JGTest HD Skin");
    Directory.CreateDirectory(Path.Combine(hdSkin, "assets"));
    File.WriteAllBytes(Path.Combine(hdSkin, "assets", "JgTestHd.png"), BuildPng(128, 256, 73));
    File.WriteAllText(Path.Combine(hdSkin, "manifest.json"),
        """{"Name":"JGTest HD Skin","UniqueID":"JuniGrid.Test.HdSkin","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(hdSkin, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestHd","FromFile":"assets/JgTestHd.png"}]}""");

    var psHd = new PortraitSkinService(new ModService(), cfgSvc);
    var scanHd = psHd.Scan(hdDir);
    var hdChar = scanHd.Characters.FirstOrDefault(c => c.Id == "JgTestHd");
    var hdSkinOpt = hdChar?.Skins.FirstOrDefault(o => o.PackFolder == "JGTest HD Skin");
    psHd.SelectSkin(hdDir, scanHd, "JgTestHd", "JGTest HD Skin", null);

    var hdOvRoot = Path.Combine(hdDir, "Mods", PortraitSkinService.OverrideFolder);
    var hdOvPath = Path.Combine(hdOvRoot, "content.json");
    var hdJo = File.Exists(hdOvPath) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(hdOvPath)) : null;
    Newtonsoft.Json.Linq.JToken? HdTake(string npc) => hdJo?["Changes"]?
        .FirstOrDefault(x => string.Equals((string?)x["Action"], "EditData", StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)x["Target"], "Mods/HDPortraits/" + npc, StringComparison.OrdinalIgnoreCase));
    var hdPin = Path.Combine(hdOvRoot, "assets", "Portraits", "JgTestHd.png");
    var tk = HdTake("JgTestHd");
    Check("B52 别的包用 Mods/HDPortraits/<npc> 绕开立绘 ⇒ 覆盖包把那条数据资产指回我们钉的图（Portrait+Size，优先级压过它）",
        scanHd.HdPortraitRendererInstalled
        && scanHd.HdPortraitEntries.Any(e => e.Pack == "JGTest HD Provider"
            && e.DataAsset == "Mods/HDPortraits/JgTestHd" && e.Npc == "JgTestHd")
        && hdSkinOpt is not null
        && File.Exists(hdPin)
        && tk is not null
        && (string?)tk["Entries"]?["Portrait"] == "Portraits/JgTestHd"
        && (int?)tk["Entries"]?["Size"] == 64
        && (string?)tk["Priority"] == "Late + 100",
        "渲染端装着=" + scanHd.HdPortraitRendererInstalled
        + " ‖ 登记=" + (scanHd.HdPortraitEntries.Count == 0 ? "(空)"
            : string.Join("|", scanHd.HdPortraitEntries.Select(e => e.Pack + ":" + e.DataAsset)))
        + " ‖ 皮肤卡=" + (hdSkinOpt is null ? "(没出卡)" : hdSkinOpt.PackName)
        + " ‖ 钉图在=" + File.Exists(hdPin)
        + " ‖ 接管条=" + (tk is null ? "(没写)" : tk.ToString(Newtonsoft.Json.Formatting.None)));

    Check("B52b 用户没给这个角色选皮肤 ⇒ 不许动别人家的 HD 资产（那条通道本来就该归它）",
        HdTake("JgTestHd2") is null,
        "JgTestHd2 的接管条=" + (HdTake("JgTestHd2") is null ? "(没写，符合预期)"
            : HdTake("JgTestHd2")!.ToString(Newtonsoft.Json.Formatting.None)));

    // ── B52c/d/e：HD 通道那个包要能【当皮肤选】，且选中它走的是另一条落盘路 ──
    // 实机报障（2026-09-29）：[CP] Dacar Rasmodia Portraits 在肖像页里根本找不到 ⇒ 用户只能
    // 看着"无论选什么都还是那张脸"。找到它还不够：512 宽的表当普通立绘钉会被 CP 以
    // "target area extends past the right edge" 整条拒绝，必须改指回它自己的高清资产。
    var hdProvOpt = hdChar?.Skins.FirstOrDefault(o => o.PackFolder == "JGTest HD Provider");
    Check("B52c 只写 Mods/HDPortraits/<角色> 的包也出卡，卡带 HD 格宽、脸就是它自己那张高清表",
        hdProvOpt is not null && hdProvOpt.HdCell == 256
        && Path.GetFileName(hdProvOpt.SourceFile ?? "") == "HdFace.png"
        && !hdProvOpt.IsNative && !hdProvOpt.HasSprite,
        "卡=" + (hdProvOpt is null ? "(没出卡)" : hdProvOpt.PackName)
        + " ‖ HdCell=" + (hdProvOpt?.HdCell?.ToString() ?? "(无)")
        + " ‖ 脸=" + Path.GetFileName(hdProvOpt?.SourceFile ?? "—")
        + " ‖ HdSkins=" + string.Join("|", scanHd.HdSkins.Select(h => h.Pack + ":" + h.Npc + ":" + h.Cell)));

    psHd.SelectSkin(hdDir, scanHd, "JgTestHd", "JGTest HD Provider", null);
    hdJo = File.Exists(hdOvPath)
        ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(hdOvPath)) : null;
    var tkHd = HdTake("JgTestHd");
    // 判"没钉普通立绘"要看解析后的补丁表：content.json 是 Newtonsoft 写的，键值冒号后带空格，
    // 拿 "\"Target\":\"..." 去 substring 永远匹配不上（= 断言恒真，抓不到东西）。
    var pinsFace = hdJo?["Changes"]?.Any(x =>
        string.Equals((string?)x["Action"], "EditImage", StringComparison.OrdinalIgnoreCase)
        && string.Equals((string?)x["Target"], "Portraits/JgTestHd", StringComparison.OrdinalIgnoreCase)) == true;
    var bodyPin = hdJo?["Changes"]?.FirstOrDefault(x =>
        string.Equals((string?)x["Action"], "EditImage", StringComparison.OrdinalIgnoreCase)
        && string.Equals((string?)x["Target"], "Characters/JgTestHd", StringComparison.OrdinalIgnoreCase));
    var hdBodyFile = Path.Combine(hdOvRoot, "assets", "Characters", "JgTestHd.png");
    Check("B52d 选中 HD 包 ⇒ EditData 指回【本包的高清资产】+它自己的格宽；脸不钉 Portraits/，身体照钉默认行那具",
        tkHd is not null
        && (string?)tkHd["Entries"]?["Portrait"] == "Mods/JgTestHdArt/Face"
        && (int?)tkHd["Entries"]?["Size"] == 256
        && !pinsFace
        && bodyPin is not null && File.Exists(hdBodyFile)
        && File.ReadAllBytes(hdBodyFile).AsSpan()
            .SequenceEqual(File.ReadAllBytes(Path.Combine(hdHost, "assets", "JgTestHd_body.png"))),
        "接管条=" + (tkHd is null ? "(没写)" : tkHd.ToString(Newtonsoft.Json.Formatting.None))
        + " ‖ 还在钉普通立绘=" + pinsFace
        + " ‖ 身体补丁=" + (bodyPin is null ? "(没钉 → 小人会掉回别家 mod)" : "有")
        + " ‖ 链子给的身子=" + (PortraitSkinService.ResolveBody(hdDir, scanHd, hdChar!, hdProvOpt).File
            is { Length: > 0 } hdBody ? Path.GetFileName(hdBody) : "(链上没身子)")
        + " ‖ 娘家精灵=" + Path.GetFileName(hdChar?.Native?.SpriteFile ?? "(无)")
        + " ‖ 身体=娘家那张=" + (File.Exists(hdBodyFile) && File.ReadAllBytes(hdBodyFile).AsSpan()
            .SequenceEqual(File.ReadAllBytes(Path.Combine(hdHost, "assets", "JgTestHd_body.png")))));

    // 换回普通皮肤：接管条必须回到"指回 Portraits/<角色>"，否则选了别的包游戏里仍是 HD 那张
    psHd.SelectSkin(hdDir, scanHd, "JgTestHd", "JGTest HD Skin", null);
    hdJo = File.Exists(hdOvPath)
        ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(hdOvPath)) : null;
    var tkBack = HdTake("JgTestHd");
    Check("B52e 从 HD 包换回普通皮肤 ⇒ 接管条改指回 Portraits/<角色>（旧的高清指向必须被顶掉，不能留在包里）",
        tkBack is not null && (string?)tkBack["Entries"]?["Portrait"] == "Portraits/JgTestHd"
        && (int?)tkBack["Entries"]?["Size"] == 64,
        "接管条=" + (tkBack is null ? "(没写)" : tkBack.ToString(Newtonsoft.Json.Formatting.None)));

    // v1.7.29：选了走 HD 通道的皮肤，Portraiture 的 HDP 模式【必须】开着 ——
    // 关了那条通道不画，游戏退回读 Portraits/<角色>，而这张卡故意不钉它 ⇒ 脸掉回别家 mod。
    // 实机：用户把艾米丽从 Portraiture 素材包换成 CP 包 ⇒ active 被写回 Vanilla ⇒
    // 法师选了 Dacar 的高清脸，游戏里却是 Romanceable Rasmodia 的绿发脸。
    // ⚠ 整份配置是全测试台共用的（B8 那类用例会留下一条 Portraiture 素材包选择），
    // 不清空的话"换回普通皮肤"也还是 HDP —— 那是别的包要求的，不是这条用例要测的。
    var b52Skins = new Dictionary<string, string>(cfgSvc.Current.PortraitSkins, StringComparer.OrdinalIgnoreCase);
    cfgSvc.Current.PortraitSkins.Clear();
    cfgSvc.Current.PortraitSkins["JgTestHd"] = "JGTest HD Provider";
    psHd.SelectSkin(hdDir, scanHd, "JgTestHd", "JGTest HD Provider", null);
    var actHdp = HdActive();
    cfgSvc.Current.PortraitSkins.Clear();
    cfgSvc.Current.PortraitSkins["JgTestHd"] = "JGTest HD Skin";
    psHd.SelectSkin(hdDir, scanHd, "JgTestHd", "JGTest HD Skin", null);
    var actVan = HdActive();
    // 再验一遍【自愈路径】：不改配置、只走一次 SyncToDisk（= 启动自检 / 进肖像页对账那条路），
    // 也必须把模式纠正过来 —— 以前它只在四个"用户动作"方法里各调一次，这条路从不校正。
    cfgSvc.Current.PortraitSkins.Clear();
    cfgSvc.Current.PortraitSkins["JgTestHd"] = "JGTest HD Provider";
    psHd.SyncToDisk(hdDir, scanHd);
    var actHeal = HdActive();
    cfgSvc.Current.PortraitSkins.Clear();
    cfgSvc.Current.PortraitSkins["JgTestHd"] = "JGTest HD Skin";
    psHd.SyncToDisk(hdDir, scanHd);
    var actHealOff = HdActive();
    foreach (var kv in b52Skins) cfgSvc.Current.PortraitSkins[kv.Key] = kv.Value;
    Check("B52f 选中 HD 包 ⇒ Portraiture active 自动切到 HDP；换回普通皮肤 ⇒ 切回 Vanilla；" +
        "且【只走 SyncToDisk 的自愈路径】也要纠正（启动自检/进肖像页不经过用户动作）",
        actHdp == "HDP" && actVan == "Vanilla" && actHeal == "HDP" && actHealOff == "Vanilla",
        "选 HD 包 active=" + actHdp + " ‖ 换回普通 active=" + actVan
        + " ‖ 自愈切回 HDP=" + actHeal + " ‖ 自愈切回 Vanilla=" + actHealOff);

    RestoreConfig();
    try { Directory.Delete(hdDir, true); } catch { }
}
// ── B53 HD 卡的文件名挂在【跨包动态 token】上（Dacar 的真实写法）──
// Dacar 的法师脸写的是 assets/Portraits/Witch_{{Spiderbuttons.CMCT/Dynamic: Nom0ri.RomRas,PortraitSVE}}.png
// —— 值由 Romanceable Rasmodia 的 DynamicTokens 现算。这种 token 解不开时，同一资产上
// 【互斥 Include 的另一支】（RRRRportraits.json 的 Witch_32.png）会顶上来 ⇒ 卡片显示一张
// 游戏里根本没生效的图（2026-09-29 实测就是这个坑）。
{
    var tkDir = Path.Combine(Path.GetTempPath(), "jg-hdtok-test");
    try { if (Directory.Exists(tkDir)) Directory.Delete(tkDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(tkDir, "Mods"));
    void TkManifest(string fold, string uid) => File.WriteAllText(
        Path.Combine(tkDir, "Mods", fold, "manifest.json"),
        "{\"Name\":\"" + fold + "\",\"UniqueID\":\"" + uid
        + "\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");

    var tkFw = Path.Combine(tkDir, "Mods", "Portraiture");
    Directory.CreateDirectory(tkFw);
    File.WriteAllText(Path.Combine(tkFw, "manifest.json"),
        """{"Name":"Portraiture","UniqueID":"Platonymous.Portraiture","Version":"1.0.0","EntryDll":"Portraiture.dll"}""");

    var tkHost = Path.Combine(tkDir, "Mods", "JGTest Tok Host");
    Directory.CreateDirectory(Path.Combine(tkHost, "assets"));
    File.WriteAllBytes(Path.Combine(tkHost, "assets", "JgTestTok.png"), BuildPng(128, 256, 80));
    TkManifest("JGTest Tok Host", "JuniGrid.Test.TokHost");
    File.WriteAllText(Path.Combine(tkHost, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestTok":{"DisplayName":"JgTestTok","HomeRegion":"Other"}}},{"Action":"Load","Target":"Portraits/JgTestTok","FromFile":"assets/JgTestTok.png"}]}""");

    // token 提供方：自己的 config 开关决定 Face = A 还是 B（实机是 HasMod 门控，等价）
    var tkSrc = Path.Combine(tkDir, "Mods", "JGTest Tok Source");
    Directory.CreateDirectory(tkSrc);
    TkManifest("JGTest Tok Source", "JuniGrid.Test.TokSrc");
    File.WriteAllText(Path.Combine(tkSrc, "config.json"), """{"Mode": "B"}""");
    File.WriteAllText(Path.Combine(tkSrc, "content.json"),
        """{"Format":"2.5","ConfigSchema":{"Mode":{"AllowValues":"A, B","Default":"A"}},"DynamicTokens":["""
        + """{"Name":"Face","Value":"A","When":{"Mode":"A"}},"""
        + """{"Name":"Face","Value":"B","When":{"Mode":"B"}}],"Changes":[]}""");

    // HD 提供方：文件名挂跨包动态 token；A/B 两张都是 512 宽、像素不同
    var tkProv = Path.Combine(tkDir, "Mods", "JGTest Tok Provider");
    Directory.CreateDirectory(Path.Combine(tkProv, "assets", "data"));
    File.WriteAllBytes(Path.Combine(tkProv, "assets", "JgTestTok_A.png"), BuildPng(512, 1024, 81));
    File.WriteAllBytes(Path.Combine(tkProv, "assets", "JgTestTok_B.png"), BuildPng(512, 1024, 82));
    File.WriteAllText(Path.Combine(tkProv, "assets", "data", "size.json"), """{"Size": 256}""");
    TkManifest("JGTest Tok Provider", "JuniGrid.Test.TokProvider");
    File.WriteAllText(Path.Combine(tkProv, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Mods/JgTestTokArt/Face","FromFile":"assets/JgTestTok_{{Spiderbuttons.CMCT/Dynamic: JuniGrid.Test.TokSrc,Face}}.png"},"""
        // 第二条 = 互斥 Include 里【没生效那一支】对同一资产的另一条 Load（Dacar 的
        // RRRRportraits.json 就是这么撞在 Mods/DacarRasmodia/Wizard 上的）。按"后者覆盖前者"
        // 记账就会拿到这张游戏里根本没显示的图 ⇒ 必须按补丁顺序取第一条真解得出文件的。
        + """{"Action":"Load","Target":"Mods/JgTestTokArt/Face","FromFile":"assets/JgTestTok_A.png"},"""
        + """{"Action":"Load","Target":"Mods/HDPortraits/JgTestTok","FromFile":"assets/data/size.json"},"""
        + """{"Action":"EditData","Target":"Mods/HDPortraits/JgTestTok","Entries":{"Portrait":"Mods/JgTestTokArt/Face"}}]}""");

    var psTk = new PortraitSkinService(new ModService(), cfgSvc);
    var scanTk = psTk.Scan(tkDir);
    var tkCard = scanTk.Characters.FirstOrDefault(c => c.Id == "JgTestTok")?.Skins
        .FirstOrDefault(o => o.PackFolder == "JGTest Tok Provider");
    Check("B53 HD 卡的文件名挂在跨包动态 token 上时，解出来的就是对方包当前配置那一档那张（解不开会被互斥 Include 顶成假图）",
        tkCard?.SourceFile is not null
        && Path.GetFileName(tkCard.SourceFile) == "JgTestTok_B.png"
        && tkCard.HdCell == 256,
        "卡=" + (tkCard is null ? "(没出卡)" : Path.GetFileName(tkCard.SourceFile ?? "—"))
        + " ‖ HdCell=" + (tkCard?.HdCell?.ToString() ?? "(无)")
        + " ‖ HdSkins=" + string.Join("|", scanTk.HdSkins.Select(h => h.Npc + "→" + Path.GetFileName(h.File)))
        + " ‖ 盲区=" + string.Join(" / ", scanTk.UnknownConditions.Select(u => u.Pack + ":" + u.Cond)));

    RestoreConfig();
    try { Directory.Delete(tkDir, true); } catch { }
}


// ── B54 选的包走路表比游戏底图矮 ⇒ 不钉身子 ──
// 尺寸闸以前只装在"变体资产"那条循环里，基资产/按季两条根本没走闸 ⇒ 把 64×192 钉到 64×480
// 的底图上，下面 288 行仍是别人家 mod 的身子（2026-09-29 实机：选了 Seasonal Baechu，
// 法师脸对了、小人还是 SVE 的，用户报"和之前那些 NPC 一样的毛病"）。
{
    var shDir = Path.Combine(Path.GetTempPath(), "jg-shortsheet-test");
    try { if (Directory.Exists(shDir)) Directory.Delete(shDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(shDir, "Mods"));
    void ShManifest(string fold, string uid) => File.WriteAllText(
        Path.Combine(shDir, "Mods", fold, "manifest.json"),
        "{\"Name\":\"" + fold + "\",\"UniqueID\":\"" + uid
        + "\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");

    // 底图提供方（相当于 SVE）：完整走路表 64×448
    var shBase = Path.Combine(shDir, "Mods", "JGTest Tall Base");
    Directory.CreateDirectory(Path.Combine(shBase, "assets"));
    File.WriteAllBytes(Path.Combine(shBase, "assets", "JgTestShort.png"), BuildPng(128, 256, 90));
    File.WriteAllBytes(Path.Combine(shBase, "assets", "JgTestShort_body.png"), BuildPng(64, 448, 91));
    ShManifest("JGTest Tall Base", "JuniGrid.Test.TallBase");
    File.WriteAllText(Path.Combine(shBase, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestShort":{"DisplayName":"JgTestShort","HomeRegion":"Other"}}},{"Action":"Load","Target":"Portraits/JgTestShort","FromFile":"assets/JgTestShort.png"},{"Action":"Load","Target":"Characters/JgTestShort","FromFile":"assets/JgTestShort_body.png"}]}""");

    // 换肤包：脸正常，走路表只有 64×192
    var shSkin = Path.Combine(shDir, "Mods", "JGTest Short Skin");
    Directory.CreateDirectory(Path.Combine(shSkin, "assets"));
    File.WriteAllBytes(Path.Combine(shSkin, "assets", "JgTestShort.png"), BuildPng(128, 256, 92));
    File.WriteAllBytes(Path.Combine(shSkin, "assets", "JgTestShort_body.png"), BuildPng(64, 192, 93));
    ShManifest("JGTest Short Skin", "JuniGrid.Test.ShortSkin");
    File.WriteAllText(Path.Combine(shSkin, "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestShort","FromFile":"assets/JgTestShort.png"},{"Action":"Load","Target":"Characters/JgTestShort","FromFile":"assets/JgTestShort_body.png"}]}""");

    static string PngDims(string f)
    {
        using var fs = File.OpenRead(f);
        var b = new byte[24];
        if (fs.Read(b, 0, 24) < 24) return "?";
        return (b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19]) + "x"
             + (b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23]);
    }
    var psSh = new PortraitSkinService(new ModService(), cfgSvc);
    var scanSh = psSh.Scan(shDir);
    psSh.SelectSkin(shDir, scanSh, "JgTestShort", "JGTest Short Skin", null);
    var shOv = Path.Combine(shDir, "Mods", PortraitSkinService.OverrideFolder, "content.json");
    var shJo = File.Exists(shOv) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(shOv)) : null;
    bool ShPins(string kind, string id) => shJo?["Changes"]?.Any(x =>
        string.Equals((string?)x["Action"], "EditImage", StringComparison.OrdinalIgnoreCase)
        && string.Equals((string?)x["Target"], kind + "/" + id, StringComparison.OrdinalIgnoreCase)) == true;
    var shBody = Path.Combine(shDir, "Mods", PortraitSkinService.OverrideFolder, "assets",
        "Characters", "JgTestShort.png");
    var shBodySize = File.Exists(shBody) ? PngDims(shBody) : "(没钉)";
    // 归属核对：钉的必须【就是这张卡的走路表原图】。曾经有两种错法：
    // ① 换成底图包那具全尺寸的（用户："他应该是用前置mod的走路图，你用了默认的"）；
    // ② 纵向拉伸铺满画布（用户："只显示一个被拉长的头" —— 各家表都是 32 行距，拉伸必坏）。
    var shIsSkinSheet = File.Exists(shBody)
        && File.ReadAllBytes(shBody).AsSpan().SequenceEqual(
            File.ReadAllBytes(Path.Combine(shSkin, "assets", "JgTestShort_body.png")));
    Check("B54 选的包走路表比别家矮 ⇒ 仍原样钉【这包的】那张，既不换成底图那具也不拉伸",
        ShPins("Portraits", "JgTestShort") && ShPins("Characters", "JgTestShort")
        && shBodySize == "64x192" && shIsSkinSheet,
        "钉了脸=" + ShPins("Portraits", "JgTestShort") + " ‖ 钉了身子=" + ShPins("Characters", "JgTestShort")
        + " ‖ 身子尺寸=" + shBodySize + "（这张卡 64×192，别家那具 64×448）"
        + " ‖ 就是这包的原图=" + shIsSkinSheet);

    RestoreConfig();
    try { Directory.Delete(shDir, true); } catch { }
}

// ── B55 走路表矮于画布时，六个出口都要【纵向拉伸铺满】而不是换人/漏钉 ──
// 六个出口 = 皮肤自己那张 / 身体链前置档 / 身体链默认档 / 按季指定 / 锁定 / 四季表档。
// 只在一个出口上拉伸都不算修完（2026-09-29 对账点名：马格努斯←前置链、奥莉薇亚←锁定、
// Haley/卡罗琳/索菲亚←按季；而法师冬天读的 Characters/Magnus_Winter 是【场合资产漏钉】）。
{
    var exDir = Path.Combine(Path.GetTempPath(), "jg-shortexit-test");
    try { if (Directory.Exists(exDir)) Directory.Delete(exDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(exDir, "Mods"));
    void ExManifest(string fold, string uid, string deps = "") => File.WriteAllText(
        Path.Combine(exDir, "Mods", fold, "manifest.json"),
        "{\"Name\":\"" + fold + "\",\"UniqueID\":\"" + uid
        + "\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}"
        + (deps.Length > 0 ? ",\"Dependencies\":[" + deps + "]" : "") + "}");
    string ExP(string fold, params string[] rel) =>
        Path.Combine(exDir, "Mods", fold, string.Join(Path.DirectorySeparatorChar.ToString(), rel));
    static string ExDims(string f)
    {
        using var fs = File.OpenRead(f);
        var b = new byte[24];
        if (fs.Read(b, 0, 24) < 24) return "?";
        return (b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19]) + "x"
             + (b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23]);
    }
    bool SameB(string a, string b) =>
        File.Exists(a) && File.Exists(b)
        && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));

    // 娘家包 = 默认档，走路表 64×448（全场最高 ⇒ 它是唯一合法的落盘身子）
    Directory.CreateDirectory(Path.Combine(exDir, "Mods", "JGTest Exit Base", "assets"));
    File.WriteAllBytes(ExP("JGTest Exit Base", "assets", "JgTestExit.png"), BuildPng(128, 256, 111));
    File.WriteAllBytes(ExP("JGTest Exit Base", "assets", "JgTestExit_body.png"), BuildPng(64, 448, 112));
    ExManifest("JGTest Exit Base", "JuniGrid.Test.ExitBase");
    File.WriteAllText(ExP("JGTest Exit Base", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"EditData","Target":"Data/Characters","Entries":{"JgTestExit":{"DisplayName":"JgTestExit","HomeRegion":"Other"}}},"""
        + """{"Action":"Load","Target":"Portraits/JgTestExit","FromFile":"assets/JgTestExit.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestExit","FromFile":"assets/JgTestExit_body.png"}]}""");

    // 前置包（娘家的下游）：走路表 64×224 —— 比 448 矮 ⇒ 链必须跳过它、再往前一跳
    Directory.CreateDirectory(Path.Combine(exDir, "Mods", "JGTest Exit Dep", "assets"));
    File.WriteAllBytes(ExP("JGTest Exit Dep", "assets", "JgTestExit.png"), BuildPng(128, 320, 113));
    File.WriteAllBytes(ExP("JGTest Exit Dep", "assets", "JgTestExit_body.png"), BuildPng(64, 224, 114));
    ExManifest("JGTest Exit Dep", "JuniGrid.Test.ExitDep",
        """{"UniqueID":"JuniGrid.Test.ExitBase"}""");
    File.WriteAllText(ExP("JGTest Exit Dep", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestExit","FromFile":"assets/JgTestExit.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestExit","FromFile":"assets/JgTestExit_body.png"}]}""");

    // 换肤包：走路表 64×192，另有春秋两张同样矮的季节兄弟
    Directory.CreateDirectory(Path.Combine(exDir, "Mods", "JGTest Exit Short", "assets"));
    File.WriteAllBytes(ExP("JGTest Exit Short", "assets", "JgTestExit.png"), BuildPng(128, 384, 115));
    File.WriteAllBytes(ExP("JGTest Exit Short", "assets", "JgTestExit_body.png"), BuildPng(64, 192, 116));
    File.WriteAllBytes(ExP("JGTest Exit Short", "assets", "JgTestExit_body_Spring.png"), BuildPng(64, 192, 117));
    File.WriteAllBytes(ExP("JGTest Exit Short", "assets", "JgTestExit_body_Fall.png"), BuildPng(64, 192, 118));
    ExManifest("JGTest Exit Short", "JuniGrid.Test.ExitShort");
    File.WriteAllText(ExP("JGTest Exit Short", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestExit","FromFile":"assets/JgTestExit.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestExit","FromFile":"assets/JgTestExit_body.png"}]}""");

    // 只换脸、前置=ExitDep 的包：链第一档命中前置那张 224（矮）⇒ 必须继续走到默认档 448
    Directory.CreateDirectory(Path.Combine(exDir, "Mods", "JGTest Exit FaceOnly", "assets"));
    File.WriteAllBytes(ExP("JGTest Exit FaceOnly", "assets", "OnlyFace.png"), BuildPng(128, 448, 119));
    ExManifest("JGTest Exit FaceOnly", "JuniGrid.Test.ExitFaceOnly",
        """{"UniqueID":"JuniGrid.Test.ExitDep"}""");
    File.WriteAllText(ExP("JGTest Exit FaceOnly", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestExit","FromFile":"assets/OnlyFace.png"}]}""");

    // 混合包：本尊走路表 448 合格，但【冬季那张】只有 192 ⇒ 冬季那一条按季补丁必须被丢掉，
    // 由基础补丁的 When 把冬季兜上（钉半截 = 冬天小人下半身在错的位置）。
    // 脸也要配齐四季兄弟，否则 selectedSeasonFiles 为空、整段四季表钉法根本不进入（用例是空的）。
    Directory.CreateDirectory(Path.Combine(exDir, "Mods", "JGTest Exit Mix", "assets"));
    File.WriteAllBytes(ExP("JGTest Exit Mix", "assets", "JgTestExit.png"), BuildPng(128, 512, 121));
    File.WriteAllBytes(ExP("JGTest Exit Mix", "assets", "JgTestExit_Spring.png"), BuildPng(128, 512, 124));
    File.WriteAllBytes(ExP("JGTest Exit Mix", "assets", "JgTestExit_Summer.png"), BuildPng(128, 512, 125));
    File.WriteAllBytes(ExP("JGTest Exit Mix", "assets", "JgTestExit_Fall.png"), BuildPng(128, 512, 126));
    File.WriteAllBytes(ExP("JGTest Exit Mix", "assets", "JgTestExit_Winter.png"), BuildPng(128, 512, 127));
    File.WriteAllBytes(ExP("JGTest Exit Mix", "assets", "JgTestExit_body.png"), BuildPng(64, 448, 122));
    File.WriteAllBytes(ExP("JGTest Exit Mix", "assets", "JgTestExit_body_Spring.png"), BuildPng(64, 448, 128));
    File.WriteAllBytes(ExP("JGTest Exit Mix", "assets", "JgTestExit_body_Winter.png"), BuildPng(64, 192, 123));
    ExManifest("JGTest Exit Mix", "JuniGrid.Test.ExitMix");
    File.WriteAllText(ExP("JGTest Exit Mix", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/JgTestExit","FromFile":"assets/JgTestExit.png"},"""
        + """{"Action":"Load","Target":"Characters/JgTestExit","FromFile":"assets/JgTestExit_body.png"}]}""");

    var psEx = new PortraitSkinService(new ModService(), cfgSvc);
    var scanEx = psEx.Scan(exDir);
    var exNpc = scanEx.Characters.FirstOrDefault(c => c.Id == "JgTestExit");
    var exShortOpt = exNpc?.AllOptions.FirstOrDefault(o => o.PackFolder == "JGTest Exit Short");
    var exOvDir = Path.Combine(exDir, "Mods", PortraitSkinService.OverrideFolder, "assets", "Characters");
    var exBaseBody = ExP("JGTest Exit Base", "assets", "JgTestExit_body.png");
    var exDepBody = ExP("JGTest Exit Dep", "assets", "JgTestExit_body.png");
    var exShortBody = ExP("JGTest Exit Short", "assets", "JgTestExit_body.png");
    var exOvBody = Path.Combine(exOvDir, "JgTestExit.png");
    var exPre = exNpc is not null && exShortOpt?.SpriteFile is not null && exNpc.Native?.SpriteFile is not null;

    // 新语义（2026-09-29 墨迹剖面推翻拉伸模型后定）：走路表【原样钉】——
    // 各家表都按 32 像素行距铺画，拉伸会把行距撑坏（实机法师被拉成一个长头）。
    // 这里每个出口都核"钉的字节 == 该出口应当选中的那张原图"。
    bool Ok(string pin, string srcRaw) => SameB(pin, srcRaw);

    // 出口1（皮肤自己那张）：矮也照钉这包的，不许换成默认行
    if (exPre) psEx.SelectSkin(exDir, scanEx, "JgTestExit", "JGTest Exit Short", null);
    var c1 = File.Exists(exOvBody) ? ExDims(exOvBody) : "(没钉)";
    var ok1 = Ok(exOvBody, exShortBody);

    // 出口2（前置档）：前置那张 224 也矮 ⇒ 仍用前置的，不许多走一档拿默认行 448
    if (exPre) psEx.SelectSkin(exDir, scanEx, "JgTestExit", "JGTest Exit FaceOnly", null);
    var c2 = File.Exists(exOvBody) ? ExDims(exOvBody) : "(没钉)";
    var ok2 = Ok(exOvBody, exDepBody);

    // 出口3（默认档）：把前置包的身子 temporarily 抽掉 ⇒ 链只能落到默认行 ⇒
    // 钉的必须正是默认行那张原图（448），既不是空、也不是别家。
    var exDepBak = exDepBody + ".bak";
    File.Move(exDepBody, exDepBak);
    var scanEx2 = psEx.Scan(exDir);
    if (File.Exists(exOvBody)) File.Delete(exOvBody);
    psEx.SelectSkin(exDir, scanEx2, "JgTestExit", "JGTest Exit FaceOnly", null);
    var c3 = File.Exists(exOvBody) ? ExDims(exOvBody) : "(没钉)";
    var ok3 = Ok(exOvBody, exBaseBody);
    File.Move(exDepBak, exDepBody);

    // 出口4（按季档）：秋季指定矮包 ⇒ JgTestExit__fall.png 就是这包秋季那张原图
    if (exPre)
    {
        psEx.SelectSkin(exDir, scanEx, "JgTestExit", "JGTest Exit Base", null);
        psEx.SetSeasonSkin(exDir, scanEx, "JgTestExit", "fall", "JGTest Exit Short");
    }
    var exFall = Path.Combine(exOvDir, "JgTestExit__fall.png");
    var c4 = File.Exists(exFall) ? ExDims(exFall) : "(没钉)";
    var ok4 = Ok(exFall, ExP("JGTest Exit Short", "assets", "JgTestExit_body_Fall.png"));

    // 出口5（锁定档）：锁定矮包 ⇒ 仍是这包那张
    if (exPre)
    {
        psEx.SetSeasonSkin(exDir, scanEx, "JgTestExit", "fall", null);
        psEx.SetLocked(exDir, scanEx, "JgTestExit", true, exShortOpt, exShortOpt?.SourceFile, null);
    }
    var c5 = File.Exists(exOvBody) ? ExDims(exOvBody) : "(没钉)";
    var ok5 = Ok(exOvBody, exShortBody);

    // 出口6（四季表档）：本尊 448 + 冬季 192 ⇒ 两条都按各自原图钉（冬季不再被丢掉）
    if (exPre)
    {
        psEx.SetLocked(exDir, scanEx, "JgTestExit", false, null, null, null);
        psEx.SelectSkin(exDir, scanEx, "JgTestExit", "JGTest Exit Mix", null);
    }
    var exWinter = Path.Combine(exOvDir, "JgTestExit__winter.png");
    var exSpring = Path.Combine(exOvDir, "JgTestExit__spring.png");
    var c6 = (File.Exists(exOvBody) ? ExDims(exOvBody) : "(没钉身子)")
        + " ‖ 春季=" + (File.Exists(exSpring) ? ExDims(exSpring) : "(没钉)")
        + " ‖ 冬季=" + (File.Exists(exWinter) ? ExDims(exWinter) : "(没钉)");
    var ok6 = Ok(exOvBody, ExP("JGTest Exit Mix", "assets", "JgTestExit_body.png"))
        && Ok(exSpring, ExP("JGTest Exit Mix", "assets", "JgTestExit_body_Spring.png"))
        && Ok(exWinter, ExP("JGTest Exit Mix", "assets", "JgTestExit_body_Winter.png"));

    Check("B55 走路表六个出口一律【原样钉所选/链上那具】（不拉伸、不换人、场合资产也不许漏钉）",
        exPre && ok1 && ok2 && ok3 && ok4 && ok5 && ok6,
        "皮肤档 " + c1 + (ok1 ? "✓" : "✗") + " ‖ 前置档 " + c2 + (ok2 ? "✓" : "✗")
        + " ‖ 默认档 " + c3 + (ok3 ? "✓" : "✗") + " ‖ 按季档 " + c4 + (ok4 ? "✓" : "✗")
        + " ‖ 锁定档 " + c5 + (ok5 ? "✓" : "✗") + " ‖ 四季表档 " + c6 + (ok6 ? "✓" : "✗")
        + " ‖ 皮肤=192 前置=224 默认=192 春季=448 冬季=192（每张都必须原样钉）");

    RestoreConfig();
    try { Directory.Delete(exDir, true); } catch { }
}
// ── B56 场合资产（1.6 Appearance 注册的独立资产名）矮于画布 ⇒ 拉伸铺满，不许漏钉 ──
// 真机形状：法师冬天读的是 Characters/Magnus_Winter（SCC-SVE 给了 64×480），用户选的 Baechu
// 只有 64×192 ⇒ 旧代码嫌矮【整条不钉】⇒ 那条资产永远显示别家身子（用户三次报"精灵图还是错的"）。
// 场合资产靠 Data/Characters 的 TargetField:[npc,"Appearance"] 登记（PackParse:676-705），
// 且角色名必须在 VanillaNames/ModNames 名单里 ⇒ 这里只能用真·原版角色 Haley 复现。
{
    var ocDir = Path.Combine(Path.GetTempPath(), "jg-occasion-test");
    try { if (Directory.Exists(ocDir)) Directory.Delete(ocDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(ocDir, "Mods"));
    // 原版角色的存在性靠 Content/<目录>/<id>.xnb（扩展名是 xnb、内容给 PNG 即可，产品有兜底）
    Directory.CreateDirectory(Path.Combine(ocDir, "Content", "Characters"));
    Directory.CreateDirectory(Path.Combine(ocDir, "Content", "Portraits"));
    File.WriteAllBytes(Path.Combine(ocDir, "Content", "Characters", "Haley.xnb"), BuildPng(64, 128, 145));
    File.WriteAllBytes(Path.Combine(ocDir, "Content", "Portraits", "Haley.xnb"), BuildPng(128, 256, 146));
    void OcManifest(string fold, string uid) => File.WriteAllText(
        Path.Combine(ocDir, "Mods", fold, "manifest.json"),
        "{\"Name\":\"" + fold + "\",\"UniqueID\":\"" + uid
        + "\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
    string OcP(string fold, string rel) =>
        Path.Combine(ocDir, "Mods", fold, rel.Replace('/', Path.DirectorySeparatorChar));
    static string OcD(string f)
    {
        using var fs = File.OpenRead(f);
        var b = new byte[24];
        if (fs.Read(b, 0, 24) < 24) return "?";
        return (b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19]) + "x"
             + (b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23]);
    }
    static bool OcSame(string a, string b) =>
        File.Exists(a) && File.Exists(b)
        && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    // 用户要选的包：只有 64×192 的整套身子，没有 Characters/Haley_Winter 这条资产
    Directory.CreateDirectory(Path.Combine(ocDir, "Mods", "JGTest Occ Skin", "assets"));
    File.WriteAllBytes(OcP("JGTest Occ Skin", "assets/Haley.png"), BuildPng(128, 384, 141));
    File.WriteAllBytes(OcP("JGTest Occ Skin", "assets/Haley_body.png"), BuildPng(64, 192, 142));
    OcManifest("JGTest Occ Skin", "JuniGrid.Test.OccSkin");
    File.WriteAllText(OcP("JGTest Occ Skin", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Haley","FromFile":"assets/Haley.png"},"""
        + """{"Action":"Load","Target":"Characters/Haley","FromFile":"assets/Haley_body.png"}]}""");

    // 别家：把冬天那条场合资产注册出去，并给了 64×448（= 那条资产的画布高来源）
    Directory.CreateDirectory(Path.Combine(ocDir, "Mods", "JGTest Occ Other", "assets"));
    File.WriteAllBytes(OcP("JGTest Occ Other", "assets/Haley_Winter.png"), BuildPng(64, 448, 143));
    File.WriteAllBytes(OcP("JGTest Occ Other", "assets/HaleyFace.png"), BuildPng(128, 448, 144));
    OcManifest("JGTest Occ Other", "JuniGrid.Test.OccOther");
    File.WriteAllText(OcP("JGTest Occ Other", "content.json"),
        """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Haley","FromFile":"assets/HaleyFace.png"},"""
        + """{"Action":"Load","Target":"Characters/Haley_Winter","FromFile":"assets/Haley_Winter.png"},"""
        + """{"Action":"EditData","Target":"Data/Characters","TargetField":["Haley","Appearance"],"""
        + """ "Entries":{"[Append]":[{"Season":"winter","Portrait":"Portraits/Haley","Sprite":"Characters/Haley_Winter"}]}}]}""");

    var psOc = new PortraitSkinService(new ModService(), cfgSvc);
    var scanOc = psOc.Scan(ocDir);
    var ocNpc = scanOc.Characters.FirstOrDefault(c => c.Id.Equals("Haley", StringComparison.OrdinalIgnoreCase));
    var ocRegistered = scanOc.VariantAssets.Any(v =>
        v.Kind == "Characters" && v.VariantId.Equals("Haley_Winter", StringComparison.OrdinalIgnoreCase));
    if (ocNpc is not null) psOc.SelectSkin(ocDir, scanOc, "Haley", "JGTest Occ Skin", null);
    var ocOcc = Path.Combine(ocDir, "Mods", PortraitSkinService.OverrideFolder,
        "assets", "Characters", "Haley_Winter.png");
    var ocOther = OcP("JGTest Occ Other", "assets/Haley_Winter.png");
    Check("B56 场合资产（Appearance 注册的独立资产名）所选包没有同名图 ⇒ 拿它自己那具身子原样钉上，不许漏钉",
        ocRegistered && ocNpc is not null && File.Exists(ocOcc)
        && OcD(ocOcc) == "64x192" && OcSame(ocOcc, OcP("JGTest Occ Skin", "assets/Haley_body.png")),
        "场合资产登记上=" + ocRegistered + " ‖ 角色出卡=" + (ocNpc is not null)
        + " ‖ 钉了=" + (File.Exists(ocOcc) ? OcD(ocOcc) : "(没钉)") + "（别家给这条 64×448、所选包只有 64×192）"
        + " ‖ 就是所选包那张=" + OcSame(ocOcc, OcP("JGTest Occ Skin", "assets/Haley_body.png")));

    RestoreConfig();
    try { Directory.Delete(ocDir, true); } catch { }
}

// ── B57 「默认」行锁死原版：内容扩展包给原版 NPC 画的重绘只能当皮肤卡 ──
// 用户 2026-09-29 报的具体样本：装了 zLewdDewValley 之后，贾斯/马尼/莱纳斯的「默认」卡
// 直接变成它的重绘图。机制是 v1.3.9 那条"给 ≥3 个 mod NPC 当娘家的包 = 扩展包，
// 它给原版 NPC 画的新默认像就当默认用"（当年是为了 SVE 的马龙/冈瑟）。
// 现在改成：原版有资产的一侧绝不被顶掉；只有原版缺那一侧（吉尔）才回落。
// 本机实测影响面：47 个原版角色里 28 个的默认卡原本指着 mod，改完只剩吉尔 1 个。
{
    var dlDir = Path.Combine(Path.GetTempPath(), "jg-deflock-test");
    try { if (Directory.Exists(dlDir)) Directory.Delete(dlDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(dlDir, "Mods"));
    foreach (var (dir, id) in new[] { ("Portraits", "Abigail"), ("Characters", "Abigail"),
                                      // 桑迪这种：原版只有脸、走路表要 mod 提供（吉尔实测同款）
                                      // ⇒ 锁死只能锁【有原版的那一侧】，另一侧仍要回落，
                                      //    但扩展包不许顺手把已有的原版脸也顶掉
                                      ("Portraits", "Sandy"),
                                      // B64 用：法师的形状 —— 原版只有 Wizard，SVE 另开一个
                                      // Magnus 当同一个人的 id（Content 里没有 Magnus.xnb）
                                      ("Portraits", "Wizard"), ("Characters", "Wizard"),
                                      // B62 用：三位字母的本体（Sam），旧门槛 4 挡在外面
                                      ("Portraits", "Sam"), ("Characters", "Sam") })
    {
        var xp = Path.Combine(dlDir, "Content", dir, id + ".xnb");
        Directory.CreateDirectory(Path.GetDirectoryName(xp)!);
        File.WriteAllBytes(xp, new byte[] { 0x58, 0x4E, 0x42, 0x58 });   // 只查存在性
    }
    string DlP(string rel) => Path.Combine(dlDir, "Mods", "JGTest Expansion",
        rel.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.Combine(dlDir, "Mods", "JGTest Expansion", "assets"));
    File.WriteAllBytes(DlP("assets/ExpAbigail.png"), BuildPng(128, 256, 151));
    File.WriteAllBytes(DlP("assets/ExpAbigail_body.png"), BuildPng(64, 128, 152));
    File.WriteAllBytes(DlP("assets/ExpSandy.png"), BuildPng(128, 256, 153));
    File.WriteAllBytes(DlP("assets/ExpSandy_body.png"), BuildPng(64, 128, 154));
    // B59 用：与本体【没有任何共用文件】的剧情变体条目（LewDew 的 AbigailLewd、
    // SVE 的 GuntherSilvian 就是这个形状 —— 本体的卡列表里根本不存在变体那张图）
    File.WriteAllBytes(DlP("assets/ExpAbigailOther.png"), BuildPng(128, 256, 155));
    // B62 用：三位字母本体 Sam 的剧情变体（LewDew 真机就是 SamLewd）
    File.WriteAllBytes(DlP("assets/ExpSamLewd.png"), BuildPng(128, 256, 156));
    // B64 用：SVE 给法师（Magnus 这个 id）画的女巫相 —— 本体 Wizard 有原版，Magnus 没有
    File.WriteAllBytes(DlP("assets/SveMagnus.png"), BuildPng(128, 256, 157));
    File.WriteAllBytes(DlP("assets/SveMagnus_body.png"), BuildPng(64, 128, 158));
    File.WriteAllText(DlP("manifest.json"),
        """{"Name":"JGTest Expansion","UniqueID":"JuniGrid.Test.Expansion","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    // 给 3 个 mod NPC 当娘家 ⇒ 满足"扩展包"判定；同时重绘原版阿比盖尔
    File.WriteAllText(DlP("content.json"), """
        {"Format":"2.5","Changes":[
          {"Action":"EditData","Target":"Data/Characters","Entries":{
            "JgTestExpA":{"DisplayName":"ExpA","HomeRegion":"Other"},
            "JgTestExpB":{"DisplayName":"ExpB","HomeRegion":"Other"},
            "JgTestExpC":{"DisplayName":"ExpC","HomeRegion":"Other"},
            "AbigailExtra":{"DisplayName":"ExtraAbigail","HomeRegion":"Other"},
            "AbigailOther":{"DisplayName":"OtherAbigail","HomeRegion":"Other"},
            "SamLewd":{"DisplayName":"LewdSam","HomeRegion":"Other"},
            "Magnus":{"DisplayName":"Rasmodia","HomeRegion":"Other"}}},
          {"Action":"Load","Target":"Portraits/Magnus","FromFile":"assets/SveMagnus.png"},
          {"Action":"Load","Target":"Characters/Magnus","FromFile":"assets/SveMagnus_body.png"},
          {"Action":"Load","Target":"Portraits/SamLewd","FromFile":"assets/ExpSamLewd.png"},
          {"Action":"Load","Target":"Portraits/Abigail","FromFile":"assets/ExpAbigail.png"},
          {"Action":"Load","Target":"Characters/Abigail","FromFile":"assets/ExpAbigail_body.png"},
          {"Action":"Load","Target":"Portraits/AbigailExtra","FromFile":"assets/ExpAbigail.png"},
          {"Action":"Load","Target":"Portraits/AbigailOther","FromFile":"assets/ExpAbigailOther.png"},
          {"Action":"Load","Target":"Characters/AbigailExtra","FromFile":"assets/ExpAbigail_body.png"},
          {"Action":"Load","Target":"Portraits/Sandy","FromFile":"assets/ExpSandy.png"},
          {"Action":"Load","Target":"Characters/Sandy","FromFile":"assets/ExpSandy_body.png"}]}
        """);

    var psDl = new PortraitSkinService(new ModService(), cfgSvc);
    var scanDl = psDl.Scan(dlDir);
    var ab = scanDl.Characters.FirstOrDefault(c => c.Id == "Abigail");
    var defFace = ab?.Vanilla?.SourceFile ?? "";
    var stillCard = ab?.Skins.Any(o => string.Equals(o.PackFolder ?? "", "JGTest Expansion",
        StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(o.SourceFile ?? "") == "ExpAbigail.png") == true;
    Check("B57 内容扩展包给原版 NPC 画的重绘不再顶掉「默认」（默认=原版 xnb），但它仍作为皮肤卡可选",
        ab is not null && defFace.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
        && ab.Vanilla?.DefaultArtPack is null && stillCard,
        "默认行的脸=" + (defFace.Length == 0 ? "(无)" : Path.GetFileName(defFace))
        + " ‖ 默认行还标着扩展包=" + (ab?.Vanilla?.DefaultArtPack ?? "(没标)")
        + " ‖ 扩展包那张仍在皮肤列表里=" + stillCard);

    // 桑迪形状：原版只有脸、走路表得由 mod 提供（吉尔实测同款）。
    // 「锁死原版」只能锁【有原版资产的那一侧】—— 脸必须还是 Sandy.xnb，
    // 身子仍要回落扩展包那张，否则默认行就是半张空白。
    // 这条是 M22 变异（把 `if (defPortrait is null)` 那道逐侧闸门撤掉）唯一能变红的形状：
    // 阿比盖尔两侧都有原版，靠循环底部的 break 就挡住了，撤掉逐侧闸门她不会红。
    var sd = scanDl.Characters.FirstOrDefault(c => c.Id == "Sandy");
    var sdFace = sd?.Vanilla?.SourceFile ?? "";
    var sdBody = sd?.Vanilla?.SpriteFile ?? "";
    Check("B57b 原版只缺走路表的那一侧仍回落扩展包，但有原版的一侧绝不被顶掉（脸=原版 xnb）",
        sd is not null && Path.GetFileName(sdFace) == "Sandy.xnb"
        && Path.GetFileName(sdBody) == "ExpSandy_body.png"
        && sd.Vanilla?.DefaultArtPack == "JGTest Expansion",
        "默认行的脸=" + (sdFace.Length == 0 ? "(无)" : Path.GetFileName(sdFace))
        + " ‖ 默认行的身子=" + (sdBody.Length == 0 ? "(无)" : Path.GetFileName(sdBody))
        + " ‖ 标注的默认像来源包=" + (sd?.Vanilla?.DefaultArtPack ?? "(没标)"));

    // ── B64 硬同义词那一侧没有原版资产时，默认行必须复用主 id 的原版图 ──
    // 真机（2026-09-30 用户报"我选的是男法师，进游戏还是女巫师"）：覆盖包里
    // Portraits/Wizard* 钉的是原版 d40f98a609b1，Portraits/Magnus* 钉的却是 SVE 的
    // f6f735633f6c（走路表同样分裂：d7a5db8f0a9b vs 789e2e96e37c）。原因是
    // Content\Portraits\Magnus.xnb 根本不存在 ⇒ 逐侧守卫①那句"原版缺这一侧才回落"
    // 就放行了扩展包。可游戏读的正是 Portraits/Magnus（SVE 把法师登记成 Magnus）⇒
    // 卡片是男法师、进游戏是女巫，弹窗右侧的身子预览也是女巫，界面自己都对不上。
    var wizB = scanDl.Characters.FirstOrDefault(c => c.Id == "Wizard");
    var magB = scanDl.Characters.FirstOrDefault(c => c.Id == "Magnus");
    var wizFaceB = wizB?.Vanilla?.SourceFile;
    var magFaceB = magB?.Vanilla?.SourceFile;
    var magSprB = magB?.Vanilla?.SpriteFile;
    Check("B64 硬同义词（法师=Magnus）自己没有原版资产时，默认行复用主 id 的原版图，不许落到扩展包那张脸",
        magB is not null && Path.GetFileName(wizFaceB ?? "") == "Wizard.xnb"
        && string.Equals(magFaceB, wizFaceB, StringComparison.OrdinalIgnoreCase)
        && string.Equals(magSprB, wizB?.Vanilla?.SpriteFile, StringComparison.OrdinalIgnoreCase)
        && magB.Vanilla?.DefaultArtPack is null,
        "Wizard 默认行脸=" + Path.GetFileName(wizFaceB ?? "(无)")
        + " ‖ Magnus 默认行脸=" + Path.GetFileName(magFaceB ?? "(无)")
        + " ‖ Magnus 默认行身子=" + Path.GetFileName(magSprB ?? "(无)")
        + " ‖ Magnus 默认行标注来源包=" + (magB?.Vanilla?.DefaultArtPack ?? "(没标)")
        + "（该是 Wizard 的原版 xnb、且不标来源包）");

    // ── B58 锁死默认行的副作用：同脸别名不能再靠「默认行=扩展包那张」来配对 ──
    // 真机成因：AliasMap 的①号判据取的是 Native?.SourceFile ?? Vanilla?.SourceFile ——
    // 本体一侧在 v1.3.9 之后就是扩展包的文件，于是 Morris / MorrisTod 共用同一路径 ⇒ 并成一张卡。
    // v1.7.32 把默认行锁回原版后这条路径不再相等 ⇒ MorrisTod/MarlonFay/GuntherSilvian/
    // AbigailLewd/EmilyLewd 全裂成裸 id 卡，本体还丢掉扩展包那款皮肤（对账 0→20 格）。
    // 这里用 AbigailExtra（与 Abigail 共用 assets/ExpAbigail.png）钉住：本体默认行仍是原版 xnb，
    // 变体卡照旧并回本体、扩展包那张同时留在本体页上。
    var ex = scanDl.Characters.FirstOrDefault(c => c.Id == "AbigailExtra");
    Check("B58 锁死默认行后，与本体共用同一张脸的变体仍并回本体（AliasMap 取任一来源，不只默认行）",
        ex is not null && ex.Hidden && string.Equals(ex.AliasOf, "Abigail", StringComparison.OrdinalIgnoreCase)
        && ab is not null && ab.Members.Any(m => string.Equals(m, "AbigailExtra",
            StringComparison.OrdinalIgnoreCase)),
        "变体卡出卡=" + (ex is not null) + " ‖ 已并入=" + (ex?.AliasOf ?? "(没并，页面上是一张裸 id 卡)")
        + " ‖ 本体管的条目=[" + string.Join("+", ab?.Members ?? Array.Empty<string>()) + "]");

    // ── B59 变体那张图在本体的卡列表里【根本不存在】时也要并回本体 ──
    // B58 的形状靠"共用同一个文件"（AliasMap 的①）就能并上；真机另一半不是这样：
    // zLewdDewValley 把阿比盖尔的变体画成独立条目 AbigailLewd，SVE 把冈瑟画成 GuntherSilvian，
    // 本体任何一张卡都不指向那个文件 ⇒ ①②全打不中，只剩 DisplayName 也不同（"Abigail Lewd"）
    // ⇒ 靠第三条分组信号并上：本体 id + 大写开头的剧情后缀。
    // ⚠ 当天量过并保留的已知代价：这些条目在 Data/NPCDispositions 里其实有自己的生日与出生点
    //（"AbigailLewd": "…/fall 13//Town 231 12 1/AbigailLewd"），游戏里是真独立 NPC。用户 2026-09-29
    // 明确拍板仍按「一张卡管两份数据」摆（撤掉这条 ⇒ 实机对账 0 → 76 格、本体丢掉那款皮肤、
    // 他点过的选择悬空）。要改成「独立页 + 本体页共享该皮肤」得先把合并的前后半拆开，
    // 别直接删这条信号。
    var ex2 = scanDl.Characters.FirstOrDefault(c => c.Id == "AbigailOther");
    Check("B59 既不共用文件、显示名也不同，只有 id 是本体前缀的剧情变体仍并回本体（用户拍板）",
        ex2 is not null && ex2.Hidden
        && string.Equals(ex2.AliasOf, "Abigail", StringComparison.OrdinalIgnoreCase)
        && ab is not null && ab.Members.Any(m => string.Equals(m, "AbigailOther",
            StringComparison.OrdinalIgnoreCase)),
        "变体卡出卡=" + (ex2 is not null) + " ‖ 已并入=" + (ex2?.AliasOf ?? "(没并 ⇒ 页上多一张裸 id 卡)")
        + " ‖ 它自己的默认外观=" + Path.GetFileName(ex2?.Native?.SourceFile ?? "")
        + " ‖ 本体管的条目=[" + string.Join("+", ab?.Members ?? Array.Empty<string>()) + "]");

    // ── B62 三位字母的本体也要并（Sam / SamLewd）──
    // 真机症状（用户 2026-09-30 截图）：别的 …Lewd 都并回本体了，只有「Sam Lewd」单独挂在页面上。
    // 根因：分组信号③与佐证 E1 都照抄了"本体名至少 4 个字母"，Sam 三个字母 ⇒ 前面全对、只差门槛。
    // 把任一处改回 4 就红。降到 3 后本机实测只多这一对，Gil/Gus/Ian/Jas/Jio/Leo/Pam 无新配对。
    var samLewd = scanDl.Characters.FirstOrDefault(c => c.Id == "SamLewd");
    var sam = scanDl.Characters.FirstOrDefault(c => c.Id == "Sam");
    Check("B62 三位字母的本体（Sam）其剧情变体照样并回本体，不许单独挂一张裸 id 卡",
        samLewd is not null && samLewd.Hidden
        && string.Equals(samLewd.AliasOf, "Sam", StringComparison.OrdinalIgnoreCase)
        && sam is not null && sam.Members.Any(m => string.Equals(m, "SamLewd",
            StringComparison.OrdinalIgnoreCase)),
        "SamLewd 出卡=" + (samLewd is not null) + " ‖ 已并入="
        + (samLewd?.AliasOf ?? "(没并 ⇒ 页面上是一张裸 id 卡)") + " ‖ Sam 管的条目=["
        + string.Join("+", sam?.Members ?? Array.Empty<string>()) + "]");

    RestoreConfig();
    try { Directory.Delete(dlDir, true); } catch { }
}

// ── B60 覆盖包被禁用（Mods 页那个开关）⇒ 真的禁得掉 ──
// 用户 2026-09-29 要「一键停用肖像、按 mod 自己的来」。禁用 = 目录改名 .前缀（CP 不加载点开头目录），
// 但旧实现有三处会把它偷偷改回来：启动自检的"无条件保证：清单 + 启用"、落盘末尾的"覆盖包自身必须
// 启用"、以及启动弹窗的自动启用。前两处现在让路给禁用状态。
// 撤掉任一处让路（把 _mods.SetDisabled(gamePath, OverrideFolder, false) 放回去）这条立刻红。
{
    var offDir = Path.Combine(Path.GetTempPath(), "jg-offpack-test");
    try { if (Directory.Exists(offDir)) Directory.Delete(offDir, true); } catch { }
    var offMods = Path.Combine(offDir, "Mods");
    var offDisabled = Path.Combine(offMods, "." + PortraitSkinService.OverrideFolder);
    Directory.CreateDirectory(Path.Combine(offDisabled, "assets"));
    File.WriteAllText(Path.Combine(offDisabled, "manifest.json"),
        """{"Name":"JuniGrid Portrait Overrides","Author":"JuniGrid","Version":"1.0.0","UniqueID":"JuniGrid.PortraitOverrides","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(offDisabled, "content.json"), """{"Format":"2.5.0","Changes":[]}""");
    File.WriteAllText(Path.Combine(offDisabled, "marker.txt"), "keep");

    var psOff = new PortraitSkinService(new ModService(), cfgSvc);
    var scanOff = psOff.Scan(offDir);
    psOff.SyncToDisk(offDir, scanOff);                    // 选皮肤走的那条落盘
    psOff.EnsureOverridePackHealthy(offDir, scanOff);     // 启动前那条自检

    var reEnabled = Directory.Exists(Path.Combine(offMods, PortraitSkinService.OverrideFolder));
    var markerKept = File.Exists(Path.Combine(offDisabled, "marker.txt"));
    var contentKept = File.ReadAllText(Path.Combine(offDisabled, "content.json")).Contains("\"Changes\":[]");
    Check("B60 覆盖包被禁用 ⇒ 落盘与启动自检都不许把它改回启用、也不许重建补丁清单",
        PortraitSkinService.OverridePackDisabled(offDir) && !reEnabled && markerKept && contentKept,
        "判成禁用=" + PortraitSkinService.OverridePackDisabled(offDir)
        + " ‖ 被改回启用=" + reEnabled + " ‖ 原目录完好=" + markerKept
        + " ‖ content.json 未被动=" + contentKept);

    try { Directory.Delete(offDir, true); } catch { }
}

// ── B61 场合资产登记只认「季节/场合白名单」后缀 ──
// 真机成因：RecordVariant 只看"第一个下划线之前是不是已知角色名"，于是 zLewdDewValley 给剧情
// 事件画的 Abigail_LewDew / Marnie_LewDewExtra / Jas_Collar…（本机实测 88 条越界，78 条是 LewDew 的）
// 全被当成"阿比盖尔的某个场合差分"，用户在肖像页选谁，这些过场立绘就变成谁。
// 这些资产没有任何 1.6 Appearance 引用，只有画它的那个 mod 自己的代码/事件会去取。
// 撤掉 PackParse.RecordVariant 里那道白名单循环，这条立刻红。
{
    var sgDir = Path.Combine(Path.GetTempPath(), "jg-suffix-gate-test");
    try { if (Directory.Exists(sgDir)) Directory.Delete(sgDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(sgDir, "Content", "Portraits"));
    Directory.CreateDirectory(Path.Combine(sgDir, "Content", "Characters"));
    File.WriteAllBytes(Path.Combine(sgDir, "Content", "Portraits", "Haley.xnb"), BuildPng(128, 256, 161));
    File.WriteAllBytes(Path.Combine(sgDir, "Content", "Characters", "Haley.xnb"), BuildPng(64, 128, 162));
    string SgP(string rel) => Path.Combine(sgDir, "Mods", "JGTest Suffix Pack",
        rel.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.Combine(sgDir, "Mods", "JGTest Suffix Pack", "assets"));
    File.WriteAllBytes(SgP("assets/Haley.png"), BuildPng(128, 256, 163));
    File.WriteAllBytes(SgP("assets/Haley_body.png"), BuildPng(64, 128, 164));
    File.WriteAllBytes(SgP("assets/Haley_Winter.png"), BuildPng(128, 256, 165));
    File.WriteAllBytes(SgP("assets/Haley_LewDew.png"), BuildPng(128, 256, 166));
    File.WriteAllText(SgP("manifest.json"),
        """{"Name":"JGTest Suffix Pack","UniqueID":"JuniGrid.Test.SuffixPack","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    // 同一个包、同样的"本体名_后缀"形状，只有后缀一个在白名单里一个不在
    File.WriteAllText(SgP("content.json"), """
        {"Format":"2.5","Changes":[
          {"Action":"Load","Target":"Portraits/Haley","FromFile":"assets/Haley.png"},
          {"Action":"Load","Target":"Characters/Haley","FromFile":"assets/Haley_body.png"},
          {"Action":"Load","Target":"Portraits/Haley_Winter","FromFile":"assets/Haley_Winter.png"},
          {"Action":"Load","Target":"Portraits/Haley_LewDew","FromFile":"assets/Haley_LewDew.png"}]}
        """);

    var psSg = new PortraitSkinService(new ModService(), cfgSvc);
    var scanSg = psSg.Scan(sgDir);
    psSg.SelectSkin(sgDir, scanSg, "Haley", "JGTest Suffix Pack", null);
    var sgCj = File.Exists(Path.Combine(sgDir, "Mods", PortraitSkinService.OverrideFolder, "content.json"))
        ? File.ReadAllText(Path.Combine(sgDir, "Mods", PortraitSkinService.OverrideFolder, "content.json"))
        : "";
    var hasWinter = sgCj.Contains("\"Portraits/Haley_Winter\"");
    var hasLewDew = sgCj.Contains("Haley_LewDew");
    Check("B61 场合资产只跟季节/场合白名单走；别人自编的剧情后缀（_LewDew）绝不代钉",
        hasWinter && !hasLewDew,
        "冬季差分钉了=" + hasWinter + "（白名单内，该钉） ‖ _LewDew 被钉=" + hasLewDew
        + "（该是 False：那是别人剧情事件用的图）");

    RestoreConfig();
    try { Directory.Delete(sgDir, true); } catch { }
}

// ── B63 原版「默认」行不许借别家包的分季图当封面 ──
// 用户 2026-09-30 实测：法师页停在「春」tab，「默认」那张卡的封面是动漫脸；但覆盖包钉进
// 游戏的字节与原版 Content\Portraits\Wizard.xnb 解码结果 sha1 全等 ⇒ 游戏是对的、封面是错的。
// 泄漏点：Portraits.razor 的 seasonCardSrc 对默认行传的是 packFolder=""，而 PackSeasonFiles
// 在没给包名时"只接受唯一一个包声明了这个资产"⇒ 机子上只有一个包给法师声明了四季，就被它借走。
// 这条用例先复现（当前应当【红】），复现出来才证明诊断对，再改判据。
{
    var cvDir = Path.Combine(Path.GetTempPath(), "jg-cover-borrow-test");
    try { if (Directory.Exists(cvDir)) Directory.Delete(cvDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(cvDir, "Mods"));
    Directory.CreateDirectory(Path.Combine(cvDir, "Content", "Portraits"));
    Directory.CreateDirectory(Path.Combine(cvDir, "Content", "Characters"));
    var cvVanillaFace = Path.Combine(cvDir, "Content", "Portraits", "Abigail.xnb");
    File.WriteAllBytes(cvVanillaFace, BuildPng(128, 256, 171));
    File.WriteAllBytes(Path.Combine(cvDir, "Content", "Characters", "Abigail.xnb"), BuildPng(64, 128, 172));
    string CvP(string rel) => Path.Combine(cvDir, "Mods", "JGTest Cover Borrow",
        rel.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.Combine(cvDir, "Mods", "JGTest Cover Borrow", "assets"));
    foreach (var (nm, seed) in new[] { ("Spring", 173), ("Summer", 174), ("Fall", 175), ("Winter", 176) })
        File.WriteAllBytes(CvP("assets/" + nm + ".png"), BuildPng(128, 256, seed));
    File.WriteAllText(CvP("manifest.json"),
        """{"Name":"JGTest Cover Borrow","UniqueID":"JuniGrid.Test.CoverBorrow","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    // 「基资产 + When:{Season}」这种写法（四季各自一张图）—— 只有这一个包声明，正好踩中借道条件
    File.WriteAllText(CvP("content.json"), """
        {"Format":"2.5","Changes":[
          {"Action":"Load","Target":"Portraits/Abigail","FromFile":"assets/Spring.png","When":{"Season":"spring"}},
          {"Action":"Load","Target":"Portraits/Abigail","FromFile":"assets/Summer.png","When":{"Season":"summer"}},
          {"Action":"Load","Target":"Portraits/Abigail","FromFile":"assets/Fall.png","When":{"Season":"fall"}},
          {"Action":"Load","Target":"Portraits/Abigail","FromFile":"assets/Winter.png","When":{"Season":"winter"}}]}
        """);

    var psCv = new PortraitSkinService(new ModService(), cfgSvc);
    var scanCv = psCv.Scan(cvDir);
    var cvMap = psCv.SeasonFilesFor(scanCv, cvVanillaFace, "Abigail", "Portraits", "");
    var borrowed = cvMap.Values.Any(v => v.Contains("Cover Borrow", StringComparison.OrdinalIgnoreCase));
    Check("B63 原版「默认」行取分季封面时只准用原版自己目录里的图，不许借别家包的",
        !borrowed,
        "默认行春季取到=" + (cvMap.TryGetValue("spring", out var sp) ? Path.GetFileName(sp) : "(无)")
        + " ‖ 借了别家包=" + borrowed + "（源文件在 Content\\Portraits，那包的四季图在 Mods 里）");

    RestoreConfig();
    try { Directory.Delete(cvDir, true); } catch { }
}

// ── B65 默认行的钉图必须【盖满整张底图】：原版那侧是 .xnb，量尺寸必须是真尺寸 ──
// 2026-09-30 用 CP 的 `patch export "Portraits/Wizard"` 把游戏真正在用的资产导出来看：
// 128×1024（16 行表情帧），第 0 行是我们的原版脸，第 1–15 行全是 OhoDavi 的女巫。
// 原因：CopyPortraitFull 里"比底图矮就纵向平铺补满"那段依赖 PngSize(src)，而 PngSize 只读
// PNG 头（buf[0]!=0x89 直接返回 0）⇒ 原版 .xnb 量出来是 0×0 ⇒ 整段平铺被跳过 ⇒
// 默认行永远只钉一格，游戏取到哪一行就露哪张脸（用户报的"两句话变三次脸"）。
// 这条用例打在【当前】代码上应当是红的。
{
    var tsDir = Path.Combine(Path.GetTempPath(), "jg-tallpin-test");
    try { if (Directory.Exists(tsDir)) Directory.Delete(tsDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(tsDir, "Mods"));
    foreach (var (dir, id) in new[] { ("Portraits", "Wizard"), ("Characters", "Wizard") })
        Directory.CreateDirectory(Path.Combine(tsDir, "Content", dir));
    // 夹具用真机原版 xnb（我们的解码器只认游戏自己那种格式，手搓一个假 xnb 量不出尺寸）
    var realGame = cfgSvc.Current.GamePath ?? "";
    var realXnb = Path.Combine(realGame, "Content", "Portraits", "Wizard.xnb");
    var tallPng = Path.Combine(tsDir, "Mods", "JGTest Tall Sheet", "assets", "Tall.png");
    Directory.CreateDirectory(Path.Combine(tsDir, "Mods", "JGTest Tall Sheet", "assets"));
    File.WriteAllBytes(tallPng, BuildPng(128, 1024, 161));                       // 别家 16 行表情表
    File.WriteAllText(Path.Combine(tsDir, "Mods", "JGTest Tall Sheet", "manifest.json"),
        """{"Name":"JGTest Tall Sheet","UniqueID":"JuniGrid.Test.TallSheet","Version":"1.0.0","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"}}""");
    File.WriteAllText(Path.Combine(tsDir, "Mods", "JGTest Tall Sheet", "content.json"), """
        {"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Wizard","FromFile":"assets/Tall.png"}]}
        """);
    var haveXnb = File.Exists(realXnb);
    if (haveXnb)
        File.WriteAllBytes(Path.Combine(tsDir, "Content", "Portraits", "Wizard.xnb"), File.ReadAllBytes(realXnb));
    var realSpr = Path.Combine(realGame, "Content", "Characters", "Wizard.xnb");
    if (haveXnb && File.Exists(realSpr))
        File.WriteAllBytes(Path.Combine(tsDir, "Content", "Characters", "Wizard.xnb"), File.ReadAllBytes(realSpr));

    var pngH = -1;
    var dbg = "";
    if (haveXnb)
    {
        var psTs = new PortraitSkinService(new ModService(), cfgSvc);
        var scanTs = psTs.Scan(tsDir);
        // 走真实用户动作：点「默认」= SelectSkin(..., null)，它负责写配置 + 同步落盘
        psTs.SelectSkin(tsDir, scanTs, "Wizard", null);
        var ovRootTs = Path.Combine(tsDir, "Mods", PortraitSkinService.OverrideFolder);
        var pinDir = Path.Combine(ovRootTs, "assets", "Portraits");
        var chTs = scanTs.Characters.FirstOrDefault(c => c.Id == "Wizard");
        var pinnedTs = Path.Combine(pinDir, "Wizard.png");
        if (File.Exists(pinnedTs))
        {
            var head = new byte[24];
            using (var fs = File.OpenRead(pinnedTs)) fs.Read(head, 0, 24);
            pngH = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        }
        dbg = "卡数=" + (chTs?.AllOptions.Count() ?? -1)
            + " 默认行脸=" + Path.GetFileName(chTs?.Vanilla?.SourceFile ?? "(无)")
            + " 覆盖包Portraits=[" + (Directory.Exists(pinDir)
                ? string.Join(",", Directory.GetFiles(pinDir, "*.png").Select(Path.GetFileName)!) : "(无目录)") + "]";
        RestoreConfig();
    }
    Check("B65 默认行（原版 .xnb）钉出去的立绘必须纵向铺满同宽度里最高的那张底图",
        !haveXnb || pngH >= 1024,
        haveXnb
            ? "钉出的 PNG 高=" + pngH + "px（别家那张底图是 1024px=16 行表情帧，只钉 64px 的话"
              + "游戏取到别的行就是别人家的脸） ‖ " + dbg
            : "跳过：找不到真机原版 xnb（" + realXnb + "），无法构造可解码的 .xnb 夹具");

    try { Directory.Delete(tsDir, true); } catch { }
}

// ── DP 依赖解析两处修正：同包子模块判定 + 依赖搜索不再被成人过滤掐掉 ──
{
    var inst = new[] { "shurmash.LewdDew_Valley", "Pathoschild.ContentPatcher", "A.Mod" };
    Check("DP1 同包子模块命中（作者段相同 + 以已装 UID 名字段 + '_' 开头）",
        ModService.BundledParentOf("shurmash.LewdDew_Valley_helper", inst) == "shurmash.LewdDew_Valley",
        "结果=" + (ModService.BundledParentOf("shurmash.LewdDew_Valley_helper", inst) ?? "(null)"));

    Check("DP2 没有分隔符就不算子模块：A.ModHelper 是独立 mod，不能并到 A.Mod 头上",
        ModService.BundledParentOf("A.ModHelper", inst) is null,
        "结果=" + (ModService.BundledParentOf("A.ModHelper", inst) ?? "(null)"));

    Check("DP3 作者段不同一律不算",
        ModService.BundledParentOf("other.LewdDew_Valley_helper", inst) is null, "命中即误判");

    Check("DP4 多个候选取最长前缀（A.Mod_x 应归 A.Mod_x 而不是 A）",
        ModService.BundledParentOf("shurmash.LewdDew_Valley_helper_assets",
            new[] { "shurmash.LewdDew", "shurmash.LewdDew_Valley" }) == "shurmash.LewdDew_Valley",
        "结果=" + (ModService.BundledParentOf("shurmash.LewdDew_Valley_helper_assets",
            new[] { "shurmash.LewdDew", "shurmash.LewdDew_Valley" }) ?? "(null)"));

    // 成人过滤：这是今天实测出来的真 bug —— 不开「显示成人内容」时，成人 mod 的依赖永远搜不到。
    // 走真实网络，失败按跳过处理（离线不该把测试舱判红）。
    var savedAdult = NexusService.IncludeAdultContent;
    NexusService.IncludeAdultContent = false;
    try
    {
        var nx = new NexusService();
        var blocked = await nx.BrowseModsAsync("downloads", 0, 8, "stardewvalley", searchText: "LewdDew");
        var opened = await nx.BrowseModsAsync("downloads", 0, 8, "stardewvalley",
            searchText: "LewdDew", forceIncludeAdult: true);
        if (blocked is null || opened is null)
            Console.WriteLine("NOTE  DP5 跳过：Nexus 不可达（离线）");
        else
            Check("DP5 依赖搜索带成人过滤时 0 条、放开后有结果（展示偏好不再掐断依赖解析）",
                blocked.Count == 0 && opened.Count > 0,
                "带过滤=" + blocked.Count + " ‖ 放开=" + opened.Count);
        var control = await nx.BrowseModsAsync("downloads", 0, 4, "stardewvalley", searchText: "Content Patcher");
        if (control is not null)
            Check("DP6 对照：普通 mod 的搜索不受影响（证明 DP5 不是查询写坏）",
                control.Count > 0 && control.Any(e => (e.Name ?? "").Contains("Content Patcher", StringComparison.Ordinal)),
                "count=" + control.Count + " ‖ 首条=" + (control.FirstOrDefault()?.Name ?? "(空)"));
    }
    catch (Exception ex) { Console.WriteLine("NOTE  DP5/DP6 异常跳过：" + ex.Message); }
    finally { NexusService.IncludeAdultContent = savedAdult; }
}

// ── BD 捆绑包重复安装：装两次不许把自己的子包扫进回收站（用户 2026-09-30 实测被拆散）──
// 真机症状：下载 LewdDew Valley（一个 zip 里含 LewdDew_Valley + LewdDew_Valley_helper 两个子包），
// 装完后日志出现「[判重清理] 同 UniqueID 旧副本子包 zLewdDewValley/LewdDew_Valley 已移入回收站」，
// 于是主 mod 跑到顶层、helper 留在 zLewdDewValley 里，捆绑包被拆开。
// 疑点：InstallNew 的同 UID 清扫只排除安装目标 folderName（顶层名），而子包 Folder 是 "Top/Sub"。
{
    var bdRoot = Path.Combine(Path.GetTempPath(), "jg-bundle-test");
    try { if (Directory.Exists(bdRoot)) Directory.Delete(bdRoot, true); } catch { }
    Directory.CreateDirectory(bdRoot);
    var bdGame = Path.Combine(bdRoot, "game");
    Directory.CreateDirectory(Path.Combine(bdGame, "Mods"));
    var bdCfg = new ConfigService();
    var bdMod = new ModService();

    string BdZip(string name, params (string rel, string body)[] entries)
    {
        var src = Path.Combine(bdRoot, name + "-src");
        foreach (var (rel, body) in entries)
        {
            var f = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, body);
        }
        var z = Path.Combine(bdRoot, name + ".zip");
        if (File.Exists(z)) File.Delete(z);
        System.IO.Compression.ZipFile.CreateFromDirectory(src, z);
        return z;
    }
    string BdManifest(string modName, string uid) =>
        "{\"Name\":\"" + modName + "\",\"Author\":\"shurmash\",\"Version\":\"1.0.0\","
        + "\"UniqueID\":\"" + uid + "\",\"Dependencies\":[],\"UpdateKeys\":[]}";

    var bundleZip = BdZip("zBundle",
        ("zBundle/LewdDew_Valley/manifest.json", BdManifest("LewdDew Valley", "shurmash.LewdDew_Valley")),
        ("zBundle/LewdDew_Valley_helper/manifest.json", BdManifest("LewdDew_Valley_helper", "shurmash.LewdDew_Valley_helper")));

    var e1 = bdMod.InstallNew(bdGame, bundleZip, out _, cfg: bdCfg);
    var main1 = Path.Combine(bdGame, "Mods", "zBundle", "LewdDew_Valley", "manifest.json");
    var help1 = Path.Combine(bdGame, "Mods", "zBundle", "LewdDew_Valley_helper", "manifest.json");
    Check("BD1 首次安装双子包捆绑包：两个子包都在 zBundle 里，没被同 UID 清扫误伤",
        e1 is null && File.Exists(main1) && File.Exists(help1),
        "err=" + (e1 ?? "null") + " ‖ 主包=" + (File.Exists(main1) ? "在" : "不在")
        + " ‖ helper=" + (File.Exists(help1) ? "在" : "不在"));

    // 再装一次（用户"删了又装"/重复点安装就是这个路径）
    var e2 = bdMod.InstallNew(bdGame, bundleZip, out _, cfg: bdCfg);
    var main2 = Path.Combine(bdGame, "Mods", "zBundle", "LewdDew_Valley", "manifest.json");
    var help2 = Path.Combine(bdGame, "Mods", "zBundle", "LewdDew_Valley_helper", "manifest.json");
    var hoisted = File.Exists(Path.Combine(bdGame, "Mods", "LewdDew_Valley", "manifest.json"));
    var uidsAfter = string.Join("+", bdMod.Scan(bdGame).Where(m => !string.IsNullOrWhiteSpace(m.UniqueID))
        .Select(m => m.Folder).OrderBy(x => x, StringComparer.Ordinal));
    Check("BD2 同一个捆绑包装第二次：仍是一个完整的 zBundle，没被拆成顶层+残留",
        e2 is null && File.Exists(main2) && File.Exists(help2) && !hoisted,
        "err=" + (e2 ?? "null") + " ‖ 主包=" + (File.Exists(main2) ? "在" : "不在")
        + " ‖ helper=" + (File.Exists(help2) ? "在" : "不在")
        + " ‖ 主包被提到顶层=" + hoisted + " ‖ 现在的条目=[" + uidsAfter + "]");

    try { Directory.Delete(bdRoot, true); } catch { }
}

// ── OV 覆盖型包（汉化补丁）：宿主判定四出口 + 备份/还原 + 失效校验 ──
// 这类包没有 manifest.json，只有一个要盖到别人目录里的文件。装错的表现为
// "列表说装好了、游戏里毫无变化"，用户自己永远查不出来，所以判定和还原都要能证明。
{
    var ovRoot = Path.Combine(Path.GetTempPath(), "jg-overlay-test");
    try { if (Directory.Exists(ovRoot)) Directory.Delete(ovRoot, true); } catch { }
    var ovGame = Path.Combine(ovRoot, "game");
    var ovCfg = new ConfigService();
    var ovMod = new ModService();

    string OvHost(string name)
    {
        var i18n = Path.Combine(ovGame, "Mods", name, "i18n");
        Directory.CreateDirectory(i18n);
        File.WriteAllText(Path.Combine(i18n, "zh.json"), "ORIGINAL-" + name);
        File.WriteAllText(Path.Combine(i18n, "default.json"), "keep-" + name);
        return i18n;
    }
    string OvZip(string name, params (string path, string body)[] entries)
    {
        var src = Path.Combine(ovRoot, name + "-src");
        foreach (var (p, b) in entries)
        {
            var f = Path.Combine(src, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, b);
        }
        var z = Path.Combine(ovRoot, name + ".zip");
        if (File.Exists(z)) File.Delete(z);
        System.IO.Compression.ZipFile.CreateFromDirectory(src, z);
        return z;
    }

    var hA = OvHost("ModA");
    var packA = OvZip("packA", ("ModA/i18n/zh.json", "OVERLAY-A"));

    // ① 包内路径点名宿主 → 装上、留备份、记账；旁文件不许碰
    var e1 = ovMod.InstallNew(ovGame, packA, out var n1, cfg: ovCfg);
    var bakA = Path.Combine(hA, "zh.json.junigrid_backup");
    Check("OV1 按包内路径认宿主并覆盖：原文件进 .junigrid_backup、旁文件不动、记一条账",
        e1 is null
        && File.ReadAllText(Path.Combine(hA, "zh.json")) == "OVERLAY-A"
        && File.Exists(bakA) && File.ReadAllText(bakA) == "ORIGINAL-ModA"
        && File.ReadAllText(Path.Combine(hA, "default.json")) == "keep-ModA"
        && ovCfg.Current.Overlays.Count == 1,
        "err=" + (e1 ?? "null") + " ‖ 记录=" + ovCfg.Current.Overlays.Count
        + " ‖ 展示名=" + (n1 ?? "(null)"));

    var rep1 = ModService.OverlayHealth(ovGame, ovCfg.Current);
    Check("OV2 刚装完的校验：1 条、0 失效",
        rep1.ByHost.TryGetValue("ModA", out var r1) && r1.Total == 1 && r1.Stale == 0
        && rep1.StaleKeys.Count == 0,
        "Total=" + (rep1.ByHost.TryGetValue("ModA", out var q1) ? q1.Total : -1));

    // ── 开关：关掉要能再打开，所以包内那份必须自己留副本 ──
    var recA = ovCfg.Current.Overlays.Values.First();
    var storeFile = Path.Combine(ModService.OverlayStoreDir(recA.StoreId), "i18n", "zh.json");
    Check("OV3 安装时在 AppData 下留了包内文件副本（开关来回靠它，不依赖缓存目录）",
        recA.StoreId.Length > 0 && recA.Enabled && File.Exists(storeFile)
        && File.ReadAllText(storeFile) == "OVERLAY-A",
        "StoreId=" + recA.StoreId + " ‖ 副本=" + (File.Exists(storeFile) ? "在" : "不在"));

    var offKey = ovCfg.Current.Overlays.Keys.First();
    var eOff = ovMod.SetOverlayEnabled(ovGame, offKey, false, ovCfg);
    Check("OV4 关掉：原版放回、账与备份都留着、校验按「关着才对」判定",
        eOff is null && File.ReadAllText(Path.Combine(hA, "zh.json")) == "ORIGINAL-ModA"
        && File.Exists(bakA) && ovCfg.Current.Overlays.Count == 1
        && !ovCfg.Current.Overlays[offKey].Enabled
        && ModService.OverlayHealth(ovGame, ovCfg.Current).StaleKeys.Count == 0,
        "err=" + (eOff ?? "null") + " ‖ 现值=" + File.ReadAllText(Path.Combine(hA, "zh.json")));

    var eOn = ovMod.SetOverlayEnabled(ovGame, offKey, true, ovCfg);
    Check("OV5 再打开：不重新下载也盖回去，且原版备份没被覆盖版污染",
        eOn is null && File.ReadAllText(Path.Combine(hA, "zh.json")) == "OVERLAY-A"
        && ovCfg.Current.Overlays[offKey].Enabled
        && File.ReadAllText(bakA) == "ORIGINAL-ModA"
        && ModService.OverlayHealth(ovGame, ovCfg.Current).StaleKeys.Count == 0,
        "err=" + (eOn ?? "null") + " ‖ 备份=" + File.ReadAllText(bakA));

    // 副本丢了就不能假装能打开
    Directory.Delete(ModService.OverlayStoreDir(ovCfg.Current.Overlays[offKey].StoreId), true);
    var eLost = ovMod.SetOverlayEnabled(ovGame, offKey, false, ovCfg);
    var eNoCopy = ovMod.SetOverlayEnabled(ovGame, offKey, true, ovCfg);
    Check("OV6 副本已丢失时「打开」明确报错，而不是悄悄什么都不做",
        eLost is null && eNoCopy is not null && eNoCopy.Contains("重新安装", StringComparison.Ordinal),
        "err=" + (eNoCopy ?? "(null)"));
    ovCfg.Current.Overlays.Clear();

    // 还原：回到原字节、备份消失、账与副本一起清掉
    OvHost("ModR");
    var hR = Path.Combine(ovGame, "Mods", "ModR", "i18n");
    ovMod.InstallNew(ovGame, OvZip("packR", ("ModR/i18n/zh.json", "OVERLAY-R")), out _, cfg: ovCfg);
    var keyR = ovCfg.Current.Overlays.Keys.First();
    var sidR = ovCfg.Current.Overlays[keyR].StoreId;
    var e2 = ovMod.RevertOverlay(ovGame, keyR, ovCfg);
    Check("OV7 删除（彻底还原）：原字节回来、备份与副本与账全部清干净",
        e2 is null && File.ReadAllText(Path.Combine(hR, "zh.json")) == "ORIGINAL-ModR"
        && !File.Exists(Path.Combine(hR, "zh.json.junigrid_backup"))
        && !Directory.Exists(ModService.OverlayStoreDir(sidR))
        && ovCfg.Current.Overlays.Count == 0,
        "err=" + (e2 ?? "null"));

    // 卸载宿主：覆盖记录与 AppData 里的副本必须一起走，否则留下永远"找不到宿主"的死账和没人引用的目录
    OvHost("ModU");
    var hU = Path.Combine(ovGame, "Mods", "ModU", "i18n");
    ovMod.InstallNew(ovGame, OvZip("packU", ("ModU/i18n/zh.json", "OVERLAY-U")), out _, cfg: ovCfg);
    var keyU = ovCfg.Current.Overlays.Keys.First();
    var sidU = ovCfg.Current.Overlays[keyU].StoreId;
    var storeU = ModService.OverlayStoreDir(sidU);
    Check("OV7b 前置：副本确实落在 AppData 下（否则下面那条断言是空的）",
        Directory.Exists(storeU) && ovCfg.Current.Overlays.Count == 1,
        "副本目录=" + (Directory.Exists(storeU) ? "在" : "不在") + " ‖ 记录=" + ovCfg.Current.Overlays.Count);
    var eU = ovMod.Uninstall(ovGame, "ModU", true, ovCfg);
    Check("OV7c 卸载宿主 mod：连带清掉覆盖记录与副本目录，不留死账",
        eU is null && !ovCfg.Current.Overlays.ContainsKey(keyU)
        && !Directory.Exists(storeU) && !Directory.Exists(Path.Combine(ovGame, "Mods", "ModU")),
        "err=" + (eU ?? "null") + " ‖ 记录还在=" + ovCfg.Current.Overlays.ContainsKey(keyU)
        + " ‖ 副本还在=" + Directory.Exists(storeU));
    ovCfg.Current.Overlays.Clear();

    // 第二个包盖同一文件时，不许用"第一个覆盖版"冒充原版
    OvHost("ModB");
    var packB1 = OvZip("packB1", ("ModB/i18n/zh.json", "FIRST-B"));
    var packB2 = OvZip("packB2", ("ModB/i18n/zh.json", "SECOND-B"));
    ovMod.InstallNew(ovGame, packB1, out _, cfg: ovCfg);
    ovMod.InstallNew(ovGame, packB2, out _, cfg: ovCfg);
    var hB = Path.Combine(ovGame, "Mods", "ModB", "i18n");
    var recB = ovCfg.Current.Overlays.Values.FirstOrDefault(v => v.Host == "ModB");
    var bakB = Path.Combine(hB, "zh.json.junigrid_backup");
    Check("OV8 两个包先后盖同一文件：备份里必须还是真原版，不是上一个覆盖版",
        recB is not null && recB.OriginalSha256.Length > 0
        && File.Exists(bakB) && File.ReadAllText(bakB) == "ORIGINAL-ModB"
        && File.ReadAllText(Path.Combine(hB, "zh.json")) == "SECOND-B",
        "备份内容=" + (File.Exists(bakB) ? File.ReadAllText(bakB) : "(无)")
        + " ‖ 现值=" + File.ReadAllText(Path.Combine(hB, "zh.json")));
    ovCfg.Current.Overlays.Clear();

    // 宿主被整体重装（InstallUpdate 就是把旧目录移进回收站再装新的）→ 覆盖静默消失
    var hC = OvHost("ModC");
    ovMod.InstallNew(ovGame, OvZip("packC", ("ModC/i18n/zh.json", "OVERLAY-C")), out _, cfg: ovCfg);
    Directory.Delete(Path.Combine(ovGame, "Mods", "ModC"), true);
    Directory.CreateDirectory(hC);
    File.WriteAllText(Path.Combine(hC, "zh.json"), "UPSTREAM-NEW-C");
    var rep5 = ModService.OverlayHealth(ovGame, ovCfg.Current);
    Check("OV9 宿主重装后覆盖没了 → 校验必须报失效（只报不自动重放）",
        rep5.ByHost.TryGetValue("ModC", out var r5) && r5.Stale == 1 && rep5.StaleKeys.Count == 1,
        "Stale=" + (rep5.ByHost.TryGetValue("ModC", out var q5) ? q5.Stale : -1));

    // 失效那条没有东西可还原：销账可以，但绝不能顺手删掉宿主里的上游新文件
    var e5 = ovMod.RevertOverlay(ovGame, rep5.StaleKeys.First(), ovCfg);
    Check("OV10 失效记录「清除」只销账，不碰宿主目录里的上游新文件",
        e5 is null && ovCfg.Current.Overlays.Count == 0
        && File.Exists(Path.Combine(hC, "zh.json"))
        && File.ReadAllText(Path.Combine(hC, "zh.json")) == "UPSTREAM-NEW-C",
        "err=" + (e5 ?? "null") + " ‖ 记录=" + ovCfg.Current.Overlays.Count);

    // ── 判定不出的三种出口：一律拒装，且不新建目录硬塞 ──
    var e7 = ovMod.InstallNew(ovGame, OvZip("bare", ("zh.json", "X")), out _, cfg: ovCfg);
    Check("OV11 包里只有一个裸 zh.json（没说宿主）→ 拒绝，不建孤儿目录",
        e7 is not null && !Directory.Exists(Path.Combine(ovGame, "Mods", "bare"))
        && ovCfg.Current.Overlays.Count == 0,
        "err=" + (e7 ?? "(null)"));

    var e8 = ovMod.InstallNew(ovGame, OvZip("ghost", ("NotInstalled/i18n/zh.json", "X")), out _, cfg: ovCfg);
    Check("OV12 认出宿主名但对方没装 → 拒绝，且文案点名缺哪个",
        e8 is not null && e8.Contains("NotInstalled", StringComparison.OrdinalIgnoreCase)
        && !Directory.Exists(Path.Combine(ovGame, "Mods", "NotInstalled")),
        "err=" + (e8 ?? "(null)"));

    var e9 = ovMod.InstallNew(ovGame,
        OvZip("two", ("ModA/i18n/zh.json", "X"), ("ModB/i18n/zh.json", "Y")), out _, cfg: ovCfg);
    Check("OV13 一个包跨两个 mod 目录 → 判定不出，拒绝安装",
        e9 is not null && ovCfg.Current.Overlays.Count == 0, "err=" + (e9 ?? "(null)"));

    // 宿主处于禁用态（磁盘上是 .ModD）时也要认得 —— 记录存不带点的名字，找的时候两种都试
    var hD = Path.Combine(ovGame, "Mods", ".ModD", "i18n");
    Directory.CreateDirectory(hD);
    File.WriteAllText(Path.Combine(hD, "zh.json"), "ORIGINAL-ModD");
    var e10 = ovMod.InstallNew(ovGame, OvZip("packD", ("ModD/i18n/zh.json", "OVERLAY-D")), out _, cfg: ovCfg);
    Check("OV14 宿主被禁用（Mods/.ModD）时按包内名 ModD 仍能落对地方",
        e10 is null && File.ReadAllText(Path.Combine(hD, "zh.json")) == "OVERLAY-D"
        && !Directory.Exists(Path.Combine(ovGame, "Mods", "ModD"))
        && ovCfg.Current.Overlays.Values.All(v => v.Host == "ModD"),
        "err=" + (e10 ?? "null") + " ‖ 现值=" + File.ReadAllText(Path.Combine(hD, "zh.json")));

    // 依赖直装带 requireUniqueId：候选包没 manifest 就是"不是这个 mod"，必须回哨兵值换下一个，
    // 绝不能退化成覆盖安装或一条把整条候选循环打断的错误文案。
    var e11 = ovMod.InstallNew(ovGame, packA, out _, requireUniqueId: "Some.Host", cfg: ovCfg);
    Check("OV15 依赖直装遇到无 manifest 的候选 → 返回 UID 不匹配哨兵，不装不覆盖",
        e11 == ModService.UidMismatchError, "err=" + (e11 ?? "(null)"));

    ovCfg.Current.Overlays.Clear();
    try { Directory.Delete(ovRoot, true); } catch { }
}

// 覆盖包按「用户真实选择」重同步一遍（测试期钉入的 Abigail/Emily 会被冲掉），
// 然后把配置文件按备份字节整体还原 —— 旧版只手工 Remove 几个键，
// 实测会把用户真实的 Abigail/Emily 肖像选择抹掉。
var scanFinal = ps.Scan(bDir);
ps.SyncToDisk(bDir, scanFinal);
RestoreConfig();
Check("Z0 测试期对用户配置的写入已全部回滚（按原字节比对）", ConfigIsPristine(),
    "当前 " + (File.Exists(cfgFile) ? new FileInfo(cfgFile).Length : -1) + " B vs 开跑前 " + cfgBefore.Length + " B");

if (!realMode && fail == 0) { try { Directory.Delete(bDir, true); } catch { } }
if (!realMode && fail > 0) Console.WriteLine("沙箱保留供诊断: " + bDir);

Console.WriteLine($"\n════════ 总计：{pass} PASS / {fail} FAIL ═════════");
Environment.Exit(fail == 0 ? 0 : 1);
