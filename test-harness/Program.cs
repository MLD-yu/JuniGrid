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
        foreach (var o in ch.AllOptions)
            Console.WriteLine($"    [{(o.IsVanilla ? "官方" : o.IsNative ? "默认" : "皮肤")}] " +
                $"{o.PackName}  folder={o.PackFolder}  HasSprite={o.HasSprite}\n" +
                $"        立绘={o.SourceFile}\n        立绘哈希={HashOf(o.SourceFile)}\n" +
                $"        精灵={o.SpriteFile ?? "(无)"}\n        精灵哈希={(o.SpriteFile is null ? "(无)" : HashOf(o.SpriteFile))}\n" +
                $"        configKeys=[{string.Join(",", o.ConfigKeys)}]  并入={string.Join("、", o.Dupes)}");
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
        AppInfo.Version == "1.2.2",
        "AppInfo.Version=" + AppInfo.Version + "（期望 1.2.2，由 JuniGrid.csproj <Version> 注入）");

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
        && r8.err.Contains("连不上 Steam 服务器") && r8.err.Contains("DIRECT")
        && sw8.ElapsedMilliseconds < 30000,
        $"起进程 {r8.attempts} 次 / 耗时 {sw8.ElapsedMilliseconds} ms / err=" + (r8.err ?? "(没报错)"));

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
    // 转换包与沙箱自带的 JGTest Emily Pack 用的是同一张合成 PNG ⇒ 逐像素相同，
    // 会被「同画面合并」并成一条（出现在 Dupes 里而不是单列一条皮肤）—— 两条都算认到了。
    var looseSeen = emilyB16 is not null
        && (emilyB16.Skins.Any(s => s.PackFolder == "JGTest Loose Emily")
            || emilyB16.Skins.Any(s => s.Dupes.Any(d => d.Contains("JGTest Loose Emily", StringComparison.OrdinalIgnoreCase))));
    Check("B16c 转出来的包被立绘扫描认到（单列一条或与同画面的包合并皆可），UID 走 JuniGrid.PortraitPack.*",
        looseSeen && File.ReadAllText(Path.Combine(loose, "manifest.json")).Contains("JuniGrid.PortraitPack."),
        "Emily 皮肤=" + (emilyB16 is null ? "(无角色行)" : string.Join(",", emilyB16.Skins.Select(s => s.PackFolder)))
        + " ‖ 并入=" + (emilyB16 is null ? "" : string.Join("/", emilyB16.Skins.SelectMany(s => s.Dupes))));

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

    // ── B17 近似合并：整张画面差 ≤5% 才并（作者把同一张画重导了一遍、字节不同的那种）──
    // 判据是整张逐像素，不是 v1.3.9b 那种只看春季格子的感知哈希 —— 后者会把四季不同的季节包误杀。
    // 用独立沙箱目录：往 bDir 里塞三个 Abigail 包会污染后面的 B 用例。
    var nDir = Path.Combine(Path.GetTempPath(), "jg-nearart-test");
    try { if (Directory.Exists(nDir)) Directory.Delete(nDir, true); } catch { }
    Directory.CreateDirectory(Path.Combine(nDir, "Mods"));
    foreach (var id in new[] { "Abigail" })
        foreach (var sub in new[] { "Portraits", "Characters" })
        {
            var xp = Path.Combine(nDir, "Content", sub, id + ".xnb");
            Directory.CreateDirectory(Path.GetDirectoryName(xp)!);
            File.WriteAllBytes(xp, new byte[] { 0x58, 0x4E, 0x42, 0x58 });
        }
    void MkArtPack(string name, byte[] png)
    {
        var dir = Path.Combine(nDir, "Mods", name);
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllBytes(Path.Combine(dir, "assets", "Abigail.png"), png);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            $"{{\"Name\":\"{name}\",\"UniqueID\":\"JuniGrid.Test.{name}\",\"Version\":\"1.0.0\",\"ContentPackFor\":{{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}}}");
        File.WriteAllText(Path.Combine(dir, "content.json"),
            """{"Format":"2.5","Changes":[{"Action":"Load","Target":"Portraits/Abigail","FromFile":"assets/Abigail.png"}]}""");
    }
    MkArtPack("JGTest Base Abigail", BuildPng(64, 64));
    MkArtPack("JGTest Near Abigail", BuildPng(64, 64, 8));      // 差 8/4096 = 0.20% ⇒ 该并
    MkArtPack("JGTest Far Abigail", BuildPng(64, 64, 410));     // 差 410/4096 = 10.0% ⇒ 不该并
    var scanB17 = new PortraitSkinService(new ModService(), cfgSvc).Scan(nDir);
    var abB17 = scanB17.Characters.FirstOrDefault(c => c.Id.Equals("Abigail", StringComparison.OrdinalIgnoreCase));
    var keepB17 = abB17?.Skins.FirstOrDefault(s => s.NearDupes.Count > 0);
    Check("B17 整张差 0.2% 的两张并成一条（记进 NearDupes），差 10% 的那张仍单列",
        abB17 is not null && abB17.Skins.Count == 2
        && keepB17 is not null && keepB17.NearDupes.Count == 1
        && keepB17.NearDupes[0].Contains("Near Abigail")
        && abB17.Skins.Any(s => s.PackName.Contains("Far Abigail")),
        "Abigail 皮肤数=" + (abB17?.Skins.Count ?? -1)
        + " ‖ 近似并入=" + (keepB17 is null ? "(没有)" : string.Join("/", keepB17.NearDupes))
        + " ‖ 各条=" + (abB17 is null ? "" : string.Join(" , ", abB17.Skins.Select(s => s.PackName))));
    Check("B17b 精确相同记进 Dupes、近似记进 NearDupes，两本账不混",
        abB17 is not null && abB17.Skins.All(s => s.Dupes.Count == 0) && keepB17 is not null,
        "Dupes 计数=" + (abB17 is null ? "" : string.Join(",", abB17.Skins.Select(s => s.Dupes.Count))));

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
    PortraitSkinService.FoldSkinsAgainstDefault(listB22, _ => null, diagB22);
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
        Check("B23d 锁定中 SelectSkin 拒绝改写，并把选择钉回锁定快照",
            cfgSvc.Current.PortraitSkins.TryGetValue("JGTestKid", out var lockedSel)
            && lockedSel != "SHOULD_NOT_STICK"
            && lockedSel != "Other Pack Never"
            && lockedSel == "JGTest Kid Family",
            "当前=" + (cfgSvc.Current.PortraitSkins.TryGetValue("JGTestKid", out var t) ? t : "(无)"));

        psB21.SetSeasonSkin(kDir, scanB21, "JGTestKid", "winter", "Other Pack");
        Check("B23e 锁定中 SetSeasonSkin 拒绝写入季节分配",
            !cfgSvc.Current.PortraitSeasonSkins.ContainsKey("JGTestKid")
            && !cfgSvc.Current.PortraitSeasonSkins.ContainsKey("JGTestKidClone"),
            "季节=" + string.Join(",", cfgSvc.Current.PortraitSeasonSkins.Keys));

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

    ps.SelectSkin(bDir, scan3, "Emily", "Portraiture/Portraits/FakePack");
    fwConfig = File.ReadAllText(Path.Combine(fwDir, "config.json"));
    Check("B8 选中 Portraiture 素材包 → HDP", fwConfig.Contains("HDP"));
}

// ═══════════════ 清理 ═══════════════
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
