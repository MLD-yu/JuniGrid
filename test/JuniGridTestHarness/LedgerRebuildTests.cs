using System.Security.Cryptography;
using JuniGrid.Services;
using SharpCompress.Writers;      // IWriter.WriteAll 是扩展方法，不给 using 就看不见

namespace JuniGridTestHarness;

/// <summary>账没了、盘还在 ⇒ 从盘上把账认回来（<see cref="ContentOverrideService.RebuildFromDisk"/>）。
/// <para>起因（2026-10-08 实机）：一个模型里没有 <c>ContentOverrides</c> 这个键的旧安装副本
/// 整份 <c>Save</c> 覆盖了配置，账被抹平、盘上一个字节没动 —— 13 个原包目录还躺在
/// <c>asset-packs</c> 下，游戏本体里 147 个文件仍被它们盖着，而内容包页写着"暂无内容包"。
/// 全仓当时<b>没有一条从盘到账的路</b>：盘只被当作"这条账的包还活着吗"的证据。</para>
/// <para>这一批用例钉的是它的三条纪律：三方对得上才认；认的时候【一个字节都不碰 Content】；
/// 对不上的宁可点名也不硬认（硬认的代价是把 Steam 修好的新版本体当成"我们的原版"存进留底）。</para>
/// </summary>
public class LedgerRebuildTests
{
    private const string Face = "Portraits/Emily";

    [Fact]
    public void WipedLedger_IsRebuiltFromDisk_ContentUntouchedAndOriginalsNotPoisoned()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        var vanilla = File.ReadAllBytes(g.Content(Face));
        var src = g.WritePack("Rebuilt Pack", (Face, 16, 16));
        var installed = g.Install("Rebuilt Pack", src);
        Assert.Single(installed);
        var applied = File.ReadAllBytes(g.Content(Face));
        Assert.False(applied.AsSpan().SequenceEqual(vanilla));      // 装上了，盘上是我们那张

        g.Cfg.Current.ContentOverrides.Clear();                     // 模拟被旧副本整份覆盖
        Assert.True(ContentOverrideService.NeedsRebuild(g.Cfg, g.Dir));

        var r = ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        Assert.Equal(1, r.Packs);
        Assert.Equal(1, r.Records);
        Assert.Equal(1, r.Applied);
        var rec = Assert.Single(g.Cfg.Current.ContentOverrides.Values);
        Assert.Equal(Face, rec.Asset);
        Assert.True(rec.Enabled);                                   // 现值 == 我们那份
        Assert.Equal("Rebuilt Pack", rec.PackName);
        Assert.Equal("Rebuilt Pack", rec.PackDir);                  // 认得自己的原包在哪
        Assert.Equal(Gex(applied), rec.AppliedSha256);
        Assert.Equal(Gex(vanilla), rec.OriginalSha256);

        // 重建只动账：Content 与留底都必须一个字节没变（留底被污染 = 之后还原会把我们的图当原版退回）
        Assert.True(File.ReadAllBytes(g.Content(Face)).AsSpan().SequenceEqual(applied));
        Assert.True(File.ReadAllBytes(ContentOverrideService.OriginalPath(g.Dir, Face))
            .AsSpan().SequenceEqual(vanilla));
        Assert.False(ContentOverrideService.NeedsRebuild(g.Cfg, g.Dir));   // 认回来之后不再需要
    }

    [Fact]
    public void PackTurnedOffAgain_IsRebuiltAsDisabled()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        var vanilla = File.ReadAllBytes(g.Content(Face));
        g.Install("Off Pack", g.WritePack("Off Pack", (Face, 16, 16)));
        File.WriteAllBytes(g.Content(Face), vanilla);                // 还原回原版（关着的状态）
        g.Cfg.Current.ContentOverrides.Clear();

        var r = ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        Assert.False(Assert.Single(g.Cfg.Current.ContentOverrides.Values).Enabled);
        Assert.Equal(0, r.Applied);
        Assert.Equal(1, r.Records);                                 // 关着也要有账，否则还原入口没了
    }

    [Fact]
    public void DriftedAsset_IsNamedNotClaimed()
    {
        // 现值既不是我们那份、也不是留底原版、也不是我们写过的任何副本 ⇒ Steam 修过本体 /
        // 第三方工具动过。这时【绝不】把这张当"我们的在位版"记账，更不许把它抄成原版。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Drift Pack", g.WritePack("Drift Pack", (Face, 16, 16)));
        g.Cfg.Current.ContentOverrides.Clear();
        // 同尺寸、不同内容的一张：解析这一步过得去（不是"尺寸对不上"），所以只有漂移判据能拦下它
        File.WriteAllBytes(g.Content(Face), GameSandbox.VanillaXnb(16, 16, 0x11));

        var r = ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        Assert.Equal(0, r.Records);
        Assert.Equal(1, r.Drifted);
        Assert.Empty(g.Cfg.Current.ContentOverrides);
        Assert.Contains("Drift Pack", string.Join("\n", r.Unresolved));
    }

    [Fact]
    public void DryRun_WritesNeitherLedgerNorStore()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Dry Pack", g.WritePack("Dry Pack", (Face, 16, 16)));
        g.Cfg.Current.ContentOverrides.Clear();
        var stores = Directory.GetDirectories(ContentOverrideService.DerivedRoot(g.Dir)).Length;

        var r = ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: true);
        Assert.Equal(1, r.Records);                                 // 算得出来
        Assert.Empty(g.Cfg.Current.ContentOverrides);               // 但一条都不写
        Assert.Equal(stores, Directory.GetDirectories(ContentOverrideService.DerivedRoot(g.Dir)).Length);
    }

    [Fact]
    public void PacksAlreadyOnTheLedger_AreNeverReParsed()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Kept Pack", g.WritePack("Kept Pack", (Face, 16, 16)));
        Assert.False(ContentOverrideService.NeedsRebuild(g.Cfg, g.Dir));   // 账实一致 ⇒ 廉价前置直接放行
        var r = ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        Assert.Equal(0, r.Packs);
        Assert.Equal(1, r.Already);
        Assert.Single(g.Cfg.Current.ContentOverrides.Values);              // 原账一字未动
    }

    [Fact]
    public void BodyResizedSinceInstall_PackIsNotClaimedEither()
    {
        // 本体换版（Steam 更新）后尺寸变了：解析这一步本身就过不去 ⇒ 整包点名，不硬认。
        // 这条与上一条分开设：它们的失败位置不同，合在一起就看不出是哪一道闸拦的。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Resized Pack", g.WritePack("Resized Pack", (Face, 16, 16)));
        g.Cfg.Current.ContentOverrides.Clear();
        File.WriteAllBytes(g.Content(Face), GameSandbox.VanillaXnb(32, 32, 0x40));

        var r = ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        Assert.Empty(g.Cfg.Current.ContentOverrides);
        Assert.Contains("Resized Pack", string.Join("\n", r.Unresolved));
    }

    [Fact]
    public void PageId_IsRecoveredFromTheCachedZip_WhenTheEntryListMatchesExactly()
    {
        // 页号只存在过账上；唯一还可能留着它的地方是下载缓存的文件名（nxm-<页号>-<文件号>.zip）。
        // 认的判据是"压缩包条目清单与原包目录全等"，认回来之后详情页/封面/分类那条既有的回填路就能跑。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Zip Page Pack", g.WritePack("Zip Page Pack", (Face, 16, 16)));
        var packDir = ContentOverrideService.PackOriginalDir(g.Dir, "Zip Page Pack");
        g.Cfg.Current.ContentOverrides.Clear();

        var zip = MakeZip(packDir, 424242);
        try
        {
            var r = ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
            var rec = Assert.Single(g.Cfg.Current.ContentOverrides.Values);
            Assert.Equal(424242, rec.NexusModId);
            Assert.Equal("N424242", rec.Slot);          // 与正常安装的记录同一个形状，不会长出两张卡
            Assert.Equal(1, r.Packs);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void AmbiguousZipPageIds_AreNotWritten()
    {
        // 同一份清单挂在两个不同页号下 ⇒ 不认。写错页号的代价是把「访问 mod 详情」点到别人的
        // mod 页上，封面与分类也跟着错 —— 比留空严重。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Tied Pack", g.WritePack("Tied Pack", (Face, 16, 16)));
        var packDir = ContentOverrideService.PackOriginalDir(g.Dir, "Tied Pack");
        g.Cfg.Current.ContentOverrides.Clear();

        var a = MakeZip(packDir, 111);
        var b = MakeZip(packDir, 222);
        try
        {
            ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
            var rec = Assert.Single(g.Cfg.Current.ContentOverrides.Values);   // 账还是要认回来
            Assert.Null(rec.NexusModId);                                       // 页号不猜
            Assert.Equal("Tied Pack", rec.Slot);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void ReinstallWithPageId_ReplacesTheRebuiltRows_InsteadOfAddingASecondCard()
    {
        // 认回来的记录 slot 用包名；他重新下载一次（带页号）之后 slot 会变成 "N<页号>"。
        // 旧 key 不退场就会在列表里出现同一个包两张卡（一张开一张关）。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        var src = g.WritePack("Rebind Pack", (Face, 16, 16));
        g.Install("Rebind Pack", src);                                  // 无页号安装 ⇒ slot = 包名
        g.Cfg.Current.ContentOverrides.Clear();
        ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        Assert.All(g.Cfg.Current.ContentOverrides.Values, v => Assert.Equal("Rebind Pack", v.Slot));

        Assert.Null(ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src),
            "Rebind Pack", 777, g.Cfg, out _, src));                   // 带页号重装
        var rows = g.Cfg.Current.ContentOverrides.Values.ToList();
        Assert.All(rows, v => Assert.Equal("N777", v.Slot));            // 旧 slot 的记录全部让位
        Assert.Single(rows);
    }

    [Fact]
    public void BindPageIds_LaterAppearingZip_BindsRowsThatWereRebuiltWithoutAPage()
    {
        // 认账的时候缓存里还没有这个 zip（或那次是手动导入）⇒ 记录只能先挂包名。
        // 之后缓存里出现了同名清单的 zip，页面加载时就要把页号补上、并把 slot 换成 "N<页号>"，
        // 否则这些包在界面上永远是"和 N 网分离"的状态（字母块封面 + 未分类 + 详情页点不动）。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Late Zip Pack", g.WritePack("Late Zip Pack", (Face, 16, 16)));
        var packDir = ContentOverrideService.PackOriginalDir(g.Dir, "Late Zip Pack");
        g.Cfg.Current.ContentOverrides.Clear();
        ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        Assert.All(g.Cfg.Current.ContentOverrides.Values, v => Assert.Null(v.NexusModId));

        var zip = MakeZip(packDir, 515051);
        try
        {
            Assert.Equal(1, ContentOverrideService.BindPageIds(g.Cfg, g.Dir));
            var rec = Assert.Single(g.Cfg.Current.ContentOverrides.Values);
            Assert.Equal(515051, rec.NexusModId);
            Assert.Equal("N515051", rec.Slot);
            Assert.Equal("", rec.PackCover);          // 换页号后旧封面/分类必须清空重取
            Assert.Equal("", rec.CategoryName);
            Assert.Equal(0, ContentOverrideService.BindPageIds(g.Cfg, g.Dir));   // 第二次无事可做
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void CachedZipNamesAreGbk_EncodingMatchesTheInstaller_PageIdIsRecovered()
    {
        // 缓存里读不到清单的第二类原因不是"没有档案"，是【我读不懂档案】：中文作者的 zip 用 GBK
        // 存条目名且不置 UTF-8 标志位，默认编码读出来是一串乱码 ⇒ 清单与原包目录"看起来"不相等。
        // 安装解压走的是 GBK，认页号也必须走 GBK —— 两边解码不一致，证据就等于不存在
        //（实机 2026-10-08：Yomi's Golden Princess Hairstyle 的 8 个文件与 nxm-20581-82454.zip 逐一对得上，
        //  只差这一条被判成"没有证据"）。
        const string Asset = "[xnb]金色-golden/hairstyles";
        using var g = new GameSandbox();
        g.WriteVanilla(Asset, 16, 16);
        g.Install("Gbk Pack", g.WritePack("Gbk Pack", (Asset, 16, 16)));
        var packDir = ContentOverrideService.PackOriginalDir(g.Dir, "Gbk Pack");
        Assert.Contains("[xnb]金色-golden",
            Directory.GetDirectories(packDir).Select(Path.GetFileName));   // 盘上名字是正常中文

        g.Cfg.Current.ContentOverrides.Clear();
        ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);   // 先只认到账上（此刻还没档案）
        Assert.All(g.Cfg.Current.ContentOverrides.Values, v => Assert.Null(v.NexusModId));
        var zip = MakeZip(packDir, 20581, gbk: true);
        try
        {
            Assert.Equal(1, ContentOverrideService.BindPageIds(g.Cfg, g.Dir));
            var rec = Assert.Single(g.Cfg.Current.ContentOverrides.Values);
            Assert.Equal(20581, rec.NexusModId);
            Assert.Equal("N20581", rec.Slot);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void CachedArchiveIsNotReallyAZip_PageIdIsRecovered()
    {
        // 第三类：档案在，但【格式装不进 zip 读取器】。安装器的落盘名一律写成 .zip，
        // 文件头却可能是 rar/7z（实机缓存 193 个"zip"里 14 个如此），剩下 4 个"找不到"的包
        // 清单逐条躺在 nxm-774 / nxm-4618 / nxm-1310 / nxm-2393 里。
        // 安装能吃的格式，认页号时也必须吃 —— 否则从 RAR 装的内容包永远接不回详情页。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.WriteVanilla("Maps/MenuTiles", 16, 16);
        g.Install("Seven Zip Shaped Pack",
            g.WritePack("Seven Zip Shaped Pack", (Face, 16, 16), ("Maps/MenuTiles", 16, 16)));
        var packDir = ContentOverrideService.PackOriginalDir(g.Dir, "Seven Zip Shaped Pack");
        g.Cfg.Current.ContentOverrides.Clear();
        ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);   // 此刻缓存里还没有档案
        Assert.All(g.Cfg.Current.ContentOverrides.Values, v => Assert.Null(v.NexusModId));

        var fake = MakeSevenZipNamedZip(packDir, 1310);
        try
        {
            Assert.Equal("7z", ModService.DetectArchiveFormat(fake));   // 名 .zip、头 7z —— 正是实机那批的形状
            Assert.Equal(1, ContentOverrideService.BindPageIds(g.Cfg, g.Dir));
            var rows = g.Cfg.Current.ContentOverrides.Values.ToList();
            Assert.Equal(2, rows.Count);                                 // 一个包两条资产账
            Assert.All(rows, v =>
            {
                Assert.Equal(1310, v.NexusModId);
                Assert.Equal("N1310", v.Slot);
            });
        }
        finally { File.Delete(fake); }
    }

    [Fact]
    public void RebuiltDisabledRow_PreviewsFromThePackBackup()
    {
        // 认回来的账只给"真在位"的那几条编派生副本（RebuildFromDisk 里那句 if (on && …)），
        // 所以关着的行 StoreId 是空的 —— 可它的图一直躺在 asset-packs 里。预览只认 StoreId
        // 就会把"盘上明明有"说成"该副本文件已不在磁盘上"
        //（实机 2026-10-09：No More Bowlegs 的 Characters/Abigail 就这么被谎报了一次）。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        var vanilla = File.ReadAllBytes(g.Content(Face));
        g.Install("Off Rebuild Pack", g.WritePack("Off Rebuild Pack", (Face, 16, 16)));
        // 他机器上的形状是：原包备份还在、派生副本已经被清掉（副本清理 / 旧副本覆盖过账）。
        // 只剩备份这一份，预览才看得出"到底认不认得 asset-packs"。
        foreach (var d in Directory.GetDirectories(ContentOverrideService.DerivedRoot(g.Dir)))
            Directory.Delete(d, true);
        File.WriteAllBytes(g.Content(Face), vanilla);        // 本体回到原版 ⇒ 重建时这一条判"关着"
        g.Cfg.Current.ContentOverrides.Clear();

        ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        var rec = Assert.Single(g.Cfg.Current.ContentOverrides.Values);
        Assert.False(rec.Enabled);
        Assert.Equal("", rec.StoreId);                      // 前提：这条确实没有派生副本

        var src = ContentOverrideService.PackImageSource(g.Dir, rec);
        Assert.NotNull(src);
        Assert.Contains("asset-packs", src!.Replace('\\', '/'));
        Assert.NotEqual(Gex(vanilla), Gex(File.ReadAllBytes(src)));   // 是包里那张，不是原版
        Assert.Equal(src, ContentOverrideTargets.VersionSource(g.Dir, new List<ContentOverrideRecord> { rec }, Face));
    }

    [Fact]
    public void RebuiltDisabledRow_CanBeEnabledWithoutReinstalling()
    {
        // 同一批账的第二个体检项：预览认得 asset-packs 之后，「启用」也得认。
        // 只认 StoreId 的话这里会甩一句"副本文件已丢失，需重新安装后才能启用" ——
        // 可包就在 asset-packs 里躺着，重装一遍是让人把已经有的东西再下一遍。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        var vanilla = File.ReadAllBytes(g.Content(Face));
        g.Install("Enable Me Pack", g.WritePack("Enable Me Pack", (Face, 16, 16)));
        foreach (var d in Directory.GetDirectories(ContentOverrideService.DerivedRoot(g.Dir)))
            Directory.Delete(d, true);
        File.WriteAllBytes(g.Content(Face), vanilla);
        g.Cfg.Current.ContentOverrides.Clear();
        ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);

        var rec = Assert.Single(g.Cfg.Current.ContentOverrides.Values);
        Assert.False(rec.Enabled);
        Assert.Equal("", rec.StoreId);

        Assert.Null(ContentOverrideService.SetEnabled(g.Cfg, g.Dir,
            ContentOverrideService.KeyOf(rec.Asset, rec.Slot), true));

        var after = Assert.Single(g.Cfg.Current.ContentOverrides.Values);
        Assert.True(after.Enabled);
        Assert.NotEqual("", after.StoreId);                              // 按当前本体重编并留下了副本
        Assert.Equal(Gex(File.ReadAllBytes(ContentOverrideService.StoreFile(g.Dir, after.StoreId, after.Asset))),
            Gex(File.ReadAllBytes(g.Content(Face))));                    // 本体上就是我们那份
    }

    [Fact]
    public void FlatBackup_PicksTheByteMatchingFile_NotJustAnySameName()
    {
        // 备份平铺时按同名找会有好几个候选（包里另一张同名图）。必须拿账上的字节验一次才敢用 ——
        // 拿错图比不画更糟：界面等于指着另一张图说"这个包盖的是它"。
        // 诱饵故意放在根上（EnumerateFiles 先给根），只认文件名的实现会先撞上它。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        var vanilla = File.ReadAllBytes(g.Content(Face));
        g.Install("Flat Pack", g.WritePack("Flat Pack", (Face, 16, 16)));
        foreach (var d in Directory.GetDirectories(ContentOverrideService.DerivedRoot(g.Dir)))
            Directory.Delete(d, true);
        File.WriteAllBytes(g.Content(Face), vanilla);
        g.Cfg.Current.ContentOverrides.Clear();
        ContentOverrideService.RebuildFromDisk(g.Cfg, g.Dir, dryRun: false);
        var rec = Assert.Single(g.Cfg.Current.ContentOverrides.Values);

        var dir = ContentOverrideService.PackOriginalDir(g.Dir, "Flat Pack");
        var truth = GameSandbox.VanillaXnb(16, 16, 0x77);
        Assert.Equal(rec.AppliedSha256, Gex(truth));                       // 账上写的就是包里那张
        Directory.Delete(Path.Combine(dir, "Portraits"), true);            // 平铺：文件夹版没了
        GameSandbox.Write(Path.Combine(dir, "Emily.xnb"), GameSandbox.VanillaXnb(16, 16, 0x99));   // 同名，字节不对
        GameSandbox.Write(Path.Combine(dir, "Inner", "Emily.xnb"), truth);                          // 真那张

        var src = ContentOverrideService.PackImageSource(g.Dir, rec);
        Assert.NotNull(src);
        Assert.Equal(Gex(truth), Gex(File.ReadAllBytes(src!)));
    }

    /// <summary>把原包目录压成 <c>nxm-&lt;页号&gt;-1.zip</c> 放进下载缓存（条目名 = 目录内相对路径）。
    /// <paramref name="gbk"/> = 条目名按 GBK 写且不置 UTF-8 标志位，与中文作者的真实压缩包同形。</summary>
    private static string MakeZip(string packDir, int page, bool gbk = false)
    {
        Directory.CreateDirectory(StoragePaths.DownloadsDir);
        var zip = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{page}-1.zip");
        if (gbk)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            using var fs = File.Create(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(packDir, fs,
                System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false,
                entryNameEncoding: System.Text.Encoding.GetEncoding(936));
        }
        else System.IO.Compression.ZipFile.CreateFromDirectory(packDir, zip);
        return zip;
    }

    /// <summary>写成真正的 7z（文件头 37 7A BC AF 27 1C），文件名却按安装器那套叫 <c>.zip</c>。</summary>
    private static string MakeSevenZipNamedZip(string packDir, int page)
    {
        Directory.CreateDirectory(StoragePaths.DownloadsDir);
        var path = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{page}-1.zip");
        if (File.Exists(path)) File.Delete(path);
        using var w = SharpCompress.Writers.WriterFactory.OpenWriter(path,
            SharpCompress.Common.ArchiveType.SevenZip,
            new SharpCompress.Writers.WriterOptions(SharpCompress.Common.CompressionType.LZMA));
        w.WriteAll(packDir, "*", SearchOption.AllDirectories);
        return path;
    }

    private static string Gex(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
}
