using System.Security.Cryptography;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 沙盒假游戏目录。
/// <para>假 xnb 用的是<b>真格式</b>：内容区开头 161 字节（1 个 reader、148 字节的
/// Texture2DReader 程序集限定名、int32 版本=0、shared=0、根索引=1、format=0）是从
/// "官方未压缩件 / 官方 LZX 件 / 社区工具产物"三方逐字节相同的那段抄来的。
/// 早期那版 FakeXnb 与真实格式差四处，全被 XnbDecoder 的 1 字节扫描兜底吸收了 ——
/// 拿它验出来的绿等于没验（见 [[junigrid-xnb-and-loose-pack-facts]]）。</para>
/// </summary>
internal sealed class GameSandbox : IDisposable
{
    private const string XnbTexturePrefix =
        "AZQBTWljcm9zb2Z0LlhuYS5GcmFtZXdvcmsuQ29udGVudC5UZXh0dXJlMkRSZWFkZXIsIE1pY3Jvc29mdC5YbmEuRnJhbWV3b3JrLkdyYXBoaWNzLCBWZXJzaW9uPTQuMC4wLjAsIEN1bHR1cmU9bmV1dHJhbCwgUHVibGljS2V5VG9rZW49ODQyY2Y4YmUxZGU1MDU1MwAAAAAAAQAAAAA=";

    public string Dir { get; }
    public ConfigService Cfg { get; }

    public GameSandbox(string version = "1.6.15")
    {
        Dir = Path.Combine(Path.GetTempPath(), "jg-ver-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(Dir, "Content"));
        Directory.CreateDirectory(Path.Combine(Dir, "Mods"));
        File.WriteAllText(Path.Combine(Dir, "Stardew Valley.exe"), "");
        Cfg = new ConfigService();
        Cfg.Current.GamePath = Dir;
        Cfg.Current.ContentOverrides.Clear();
        Cfg.Save(Cfg.Current);
        SetVersion(version);
    }

    /// <summary>伪造版本部署记录：ResolveCurrentVersion 第一眼看它，指纹对得上就认它，
    /// 所以"换游戏版本"在沙盒里就是改这一行。</summary>
    public void SetVersion(string v)
    {
        var dll = Path.Combine(Dir, "Stardew Valley.dll");
        if (!File.Exists(dll)) File.WriteAllText(dll, "fake body for the version fingerprint");
        var fi = new FileInfo(dll);
        File.WriteAllText(Path.Combine(Dir, ".junigrid-version"),
            $"Stardew Valley.dll|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{v}|1");
    }

    public static byte[] VanillaXnb(int w, int h, byte fill = 0x40)
    {
        var body = new List<byte>(Convert.FromBase64String(XnbTexturePrefix));
        body.AddRange(BitConverter.GetBytes(w));
        body.AddRange(BitConverter.GetBytes(h));
        body.AddRange(BitConverter.GetBytes(1));              // level count
        body.AddRange(BitConverter.GetBytes(w * h * 4));      // dataSize
        body.AddRange(Enumerable.Repeat(fill, w * h * 4));
        return XnbCodec.WrapRaw(body.ToArray());
    }

    public void WriteVanilla(string asset, int w, int h) => Write(Content(asset), VanillaXnb(w, h));

    /// <summary>造一个裸素材包目录：每个资产一张同尺寸 xnb，填充字节故意不同，便于确认"写进去的确实是包里那张"。</summary>
    public string WritePack(string packName, params (string Asset, int W, int H)[] items)
    {
        var src = Path.Combine(Path.GetTempPath(), "jg-pack-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (asset, w, h) in items)
            Write(Path.Combine(src, asset.Replace('/', Path.DirectorySeparatorChar) + ".xnb"),
                  VanillaXnb(w, h, 0x77));
        return src;
    }

    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public string Content(string asset) => ContentOverrideService.AssetFullPath(Dir, asset);
    public string Sha(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "(无文件)";

    /// <summary>期望哈希就地算，别把 SHA 常量抄进断言里。</summary>
    public static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Prepare + Commit（安装链路里真正写盘的那一段），并钉住"这一步必须成功"。</summary>
    public List<ContentOverrideRecord> Install(string packName, string srcDir)
    {
        var plan = ContentOverrideService.Prepare(Dir, srcDir);
        Assert.NotEmpty(plan.Items);
        Assert.Null(ContentOverrideService.Commit(Dir, plan, packName, null, Cfg, out _, srcDir));
        return Cfg.Current.ContentOverrides.Values
            .Where(r => r.PackName == packName)
            .OrderBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}

/// <summary>版本分层（记录认得自己是哪一版编的）+ 外来文件漂移闸 + 无主副本清理。
/// 这批是 2026-10-07 那轮改动的常驻回归，原先只活在一次性沙盒驱动器里。</summary>
public class ContentVersionLayerTests
{
    private const string Face = "Portraits/Emily";
    private const string Body = "Characters/Emily";

    // ─────────────── ① 版本戳与回填 ───────────────

    [Fact]
    public void Commit_StampsEveryRowWithTheCurrentVersion()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        s.WriteVanilla(Body, 64, 128);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320), (Body, 64, 128)));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("1.6.15", r.Version));
    }

    [Fact]
    public void BackfillVersions_TakesTheLayerThatActuallyHoldsTheOriginal()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        foreach (var r in rows) r.Version = "";
        s.Cfg.Save(s.Cfg.Current);

        Assert.Equal(1, ContentOverrideService.BackfillVersions(s.Cfg, s.Dir));
        Assert.Equal("1.6.15", rows[0].Version);
    }

    [Fact]
    public void BackfillVersions_PrefersTheCurrentLayerWhenSeveralMatch()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        // 把同一份原版再抄进另一个层 ⇒ 两层都命中，当前层必须赢
        var other = Path.Combine(ContentOverrideService.OriginalsRoot(s.Dir), "1.5.0",
            Face.Replace('/', Path.DirectorySeparatorChar) + ".orig.xnb");
        GameSandbox.Write(other, File.ReadAllBytes(ContentOverrideService.OriginalPath(s.Dir, Face)));
        rows[0].Version = "";
        s.Cfg.Save(s.Cfg.Current);

        ContentOverrideService.BackfillVersions(s.Cfg, s.Dir);
        Assert.Equal("1.6.15", rows[0].Version);
    }

    [Fact]
    public void BackfillVersions_LeavesItBlank_WhenOnlyNonCurrentLayersMatch()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var root = ContentOverrideService.OriginalsRoot(s.Dir);
        foreach (var v in new[] { "1.5.0", "1.5.5" })
            GameSandbox.Write(Path.Combine(root, v, Face.Replace('/', Path.DirectorySeparatorChar) + ".orig.xnb"),
                File.ReadAllBytes(ContentOverrideService.OriginalPath(s.Dir, Face)));
        s.SetVersion("1.6.99");                       // 当前版本那一层是空的
        rows[0].Version = "";
        s.Cfg.Save(s.Cfg.Current);

        Assert.Equal(0, ContentOverrideService.BackfillVersions(s.Cfg, s.Dir));
        Assert.Equal("", rows[0].Version);            // 说不清就不猜
    }

    // ─────────────── ② 还原读的是记录自己那一层 ───────────────

    [Fact]
    public void Revert_AfterVersionSwitch_RestoresThatVersionsOriginal_NotADelete()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var orig = rows[0].OriginalSha256;
        Assert.NotEmpty(orig);

        s.SetVersion("1.6.16");                       // 原版躺在 1.6.15 那一层
        Assert.Null(ContentOverrideService.SetPackEnabled(s.Cfg, s.Dir, rows[0].Slot, false));

        // 修之前：只查当前层 ⇒ 查不到 ⇒ 走进"这文件是我们新增的"分支 ⇒ 直接删掉本体那张
        Assert.True(File.Exists(s.Content(Face)), "还原不许把原版删没");
        Assert.Equal(orig, s.Sha(s.Content(Face)));
    }

    // ─────────────── ③ 换版本后打开 = 重编，不是回放 ───────────────

    [Fact]
    public void StaleEnable_ReencodesRestampsAndReplacesTheStoredCopy()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var slot = rows[0].Slot;
        var oldStore = rows[0].StoreId;
        Assert.Null(ContentOverrideService.SetPackEnabled(s.Cfg, s.Dir, slot, false));

        s.SetVersion("1.6.16");
        Assert.Null(ContentOverrideService.SetPackEnabled(s.Cfg, s.Dir, slot, true));

        Assert.Equal("1.6.16", rows[0].Version);
        Assert.True(rows[0].Enabled);
        Assert.NotEqual(oldStore, rows[0].StoreId);                                   // 重编 ⇒ 换了副本
        Assert.False(Directory.Exists(ContentOverrideService.StoreDir(s.Dir, oldStore))); // 旧的丢掉
        Assert.Equal(rows[0].AppliedSha256, s.Sha(s.Content(Face)));                  // 盘上就是我们那份
        Assert.Equal(2, Directory.GetFiles(ContentOverrideService.OriginalsRoot(s.Dir), "*.orig.xnb",
            SearchOption.AllDirectories).Length);                                     // 两个版本各一层
    }

    [Fact]
    public void StaleSingleRowEnable_ReencodesThroughSetEnabled()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        s.WriteVanilla(Body, 64, 128);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320), (Body, 64, 128)));
        var slot = rows[0].Slot;
        Assert.Null(ContentOverrideService.SetPackEnabled(s.Cfg, s.Dir, slot, false));

        // 整包开关的版本判断只是"一次解析覆盖全包"的省法（101 张不必解 101 遍），
        // 真正兜住"别回放旧版本字节"的是 SetEnabled 里那一份 —— 变异测试证明：
        // 只拆整包那一份，行为完全不变（0 红），拆单行这一份立刻红。
        s.SetVersion("1.6.16");
        var face = rows.Single(r => r.Asset == Face);
        var body = rows.Single(r => r.Asset == Body);
        var oldStore = face.StoreId;
        Assert.Null(ContentOverrideService.SetEnabled(s.Cfg, s.Dir,
            ContentOverrideService.KeyOf(face.Asset, face.Slot), true));

        Assert.Equal("1.6.16", face.Version);
        Assert.True(face.Enabled);
        Assert.NotEqual(oldStore, face.StoreId);
        Assert.Equal(face.AppliedSha256, s.Sha(s.Content(Face)));
        Assert.False(body.Enabled);                                     // 只开这一行，兄弟不该被顺手打开
    }

    [Fact]
    public void StaleEnable_WithoutPackBackup_FailsWithoutTouchingTheBody()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var slot = rows[0].Slot;
        ContentOverrideService.SetPackEnabled(s.Cfg, s.Dir, slot, false);
        var vanilla = s.Sha(s.Content(Face));
        foreach (var d in Directory.GetDirectories(ContentOverrideService.PacksRoot(s.Dir)))
            Directory.Delete(d, true);                                                // 原包备份没了

        s.SetVersion("1.6.17");
        Assert.NotNull(ContentOverrideService.SetPackEnabled(s.Cfg, s.Dir, slot, true));
        Assert.False(rows[0].Enabled);
        Assert.Equal(vanilla, s.Sha(s.Content(Face)));                                // 本体一字节没动
    }

    [Fact]
    public void StaleEnable_WhileStillApplied_AskedToRevertFirst()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var applied = rows[0].AppliedSha256;

        s.SetVersion("1.6.15");   // 故意把本体说成另一版：账上 1.6.15 的还盖着
        s.SetVersion("1.5.0");
        Assert.NotNull(ContentOverrideService.SetPackEnabled(s.Cfg, s.Dir, rows[0].Slot, true));
        // 重编第一步要抄"当前版本的原版"，包还盖着就会把我们自己那张抄成原版 ⇒ 必须停手
        Assert.Equal(applied, s.Sha(s.Content(Face)));
        Assert.False(File.Exists(ContentOverrideService.OriginalPath(s.Dir, Face, "1.5.0")));
    }

    // ─────────────── ④ 外来文件不许被静默盖掉 ───────────────

    [Fact]
    public void ForeignBytesOnDisk_BlockRevert_AndStayPut()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var key = ContentOverrideService.KeyOf(rows[0].Asset, rows[0].Slot);

        var foreign = new byte[] { 1, 2, 3, 4, 5 };
        GameSandbox.Write(s.Content(Face), foreign);                                   // 第三方那张
        Assert.Equal(ContentOverrideService.OverrideState.Foreign,
            ContentOverrideService.StateOf(s.Dir, rows[0]));
        Assert.True(ContentOverrideService.Drifted(s.Dir, rows[0]));

        Assert.NotNull(ContentOverrideService.SetEnabled(s.Cfg, s.Dir, key, false));
        Assert.Equal(GameSandbox.Hex(foreign), s.Sha(s.Content(Face)));                // 原样还在
        Assert.True(rows[0].Enabled);                                                 // 账也没撤
    }

    [Fact]
    public void RevertAll_CountsHeldBackSeparately()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        s.WriteVanilla(Body, 64, 128);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320), (Body, 64, 128)));
        var foreign = new byte[] { 9, 9, 9 };
        GameSandbox.Write(s.Content(Body), foreign);

        var (reverted, held) = ContentOverrideService.RevertAll(s.Cfg, s.Dir);
        Assert.Equal(1, reverted);
        Assert.Equal(1, held);
        Assert.Equal(GameSandbox.Hex(foreign), s.Sha(s.Content(Body)));
    }

    [Fact]
    public void DeletePack_RefusesWholly_WhenAnyFileIsForeign()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        s.WriteVanilla(Body, 64, 128);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320), (Body, 64, 128)));
        GameSandbox.Write(s.Content(Body), new byte[] { 9, 9, 9 });

        Assert.NotNull(ContentOverrideService.DeletePack(s.Cfg, s.Dir, rows[0].Slot));
        // 整包一起不删：逐张删会留下"包被删了一半"，比删不掉更难解释
        Assert.Equal(2, s.Cfg.Current.ContentOverrides.Values.Count(r => r.Slot == rows[0].Slot));
    }

    [Fact]
    public void InPlaceBytes_RevertStillSucceeds()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var orig = rows[0].OriginalSha256;

        Assert.Null(ContentOverrideService.SetEnabled(s.Cfg, s.Dir,
            ContentOverrideService.KeyOf(rows[0].Asset, rows[0].Slot), false));
        Assert.Equal(orig, s.Sha(s.Content(Face)));
        Assert.False(rows[0].Enabled);
    }

    [Fact]
    public void AlreadyVanillaOnDisk_IsNotDrift_AndIsCountedSeparately()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        // Steam 验证完整性之后的样子：账上开着，盘上已经是原版
        GameSandbox.Write(s.Content(Face), File.ReadAllBytes(ContentOverrideService.OriginalPath(s.Dir, Face)));

        Assert.Equal(ContentOverrideService.OverrideState.AlreadyVanilla,
            ContentOverrideService.StateOf(s.Dir, rows[0]));
        Assert.False(ContentOverrideService.Drifted(s.Dir, rows[0]));
        var g = ContentOverrideService.Groups(s.Dir, s.Cfg).Single(x => x.Slot == rows[0].Slot);
        Assert.Equal(0, g.DriftedCount);
        Assert.Equal(1, g.VanillaCount);
    }

    // ─────────────── ⑤ 无主副本目录 ───────────────

    [Fact]
    public void Prune_DeletesOrphansAndKeepsEveryReferencedCopy()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var root = ContentOverrideService.DerivedRoot(s.Dir);
        var old = DateTime.UtcNow.AddDays(-2);
        for (var i = 0; i < 3; i++)
        {
            var d = Path.Combine(root, "dead" + i);
            Directory.CreateDirectory(d);
            Directory.SetLastWriteTimeUtc(d, old);                                    // 躲开 10 分钟新鲜期
        }

        Assert.Equal(3, ContentOverrideService.PruneUnreferencedStores(s.Cfg, s.Dir));
        Assert.True(Directory.Exists(ContentOverrideService.StoreDir(s.Dir, rows[0].StoreId)));
    }

    [Fact]
    public void Prune_RefusesEverything_WhenTheLedgerPointsAtAMissingCopy()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        s.Install("packA", s.WritePack("packA", (Face, 128, 320)));
        var root = ContentOverrideService.DerivedRoot(s.Dir);
        var spare = Path.Combine(root, "spare");
        Directory.CreateDirectory(spare);
        Directory.SetLastWriteTimeUtc(spare, DateTime.UtcNow.AddDays(-2));
        // 账上多一条指向不存在副本的记录 ⇒ 引用集与盘面对不上，此时删任何东西都可能删活在的
        s.Cfg.Current.ContentOverrides["Portraits/Ghost|ghost"] = new ContentOverrideRecord
        { Asset = "Portraits/Ghost", Slot = "ghost", PackName = "ghost", StoreId = "notOnDisk" };
        s.Cfg.Save(s.Cfg.Current);

        Assert.Equal(0, ContentOverrideService.PruneUnreferencedStores(s.Cfg, s.Dir));
        Assert.True(Directory.Exists(spare));
    }

    // ─────────────── ⑥ 界面分桶用的那个版本号 ───────────────

    [Fact]
    public void PackGroupVersion_IsBlank_WhenTheRowsDisagree()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla(Face, 128, 320);
        s.WriteVanilla(Body, 64, 128);
        var rows = s.Install("packA", s.WritePack("packA", (Face, 128, 320), (Body, 64, 128)));
        var slot = rows[0].Slot;
        Assert.Equal("1.6.15", ContentOverrideService.Groups(s.Dir, s.Cfg)
            .Single(g => g.Slot == slot).StampedVersion);

        rows[0].Version = "1.5.0";
        s.Cfg.Save(s.Cfg.Current);
        Assert.Equal("", ContentOverrideService.Groups(s.Dir, s.Cfg)
            .Single(g => g.Slot == slot).StampedVersion);                             // 混版不硬挑一个
    }
}

/// <summary>字体图集解码（BC4 单通道遮罩）。这条是给素材页字体卡的真封面兜底的。</summary>
public class FontAtlasTests
{
    [Fact]
    public void DecodeFontMask_ReadsTheBc4AlphaSubBlock()
    {
        // 一个 4×4 块：alpha0=255、alpha1=0、选择子全 0 ⇒ 整块应是实心白
        var block = new List<byte> { 255, 0 };
        block.AddRange(new byte[6]);
        block.AddRange(new byte[8]);
        var src = block.ToArray();
        var tex = DxtDecoder.DecodeFontMask(src, 0, 4, 4);

        Assert.Equal(16, tex.Width * tex.Height);
        Assert.All(Enumerable.Range(0, 16), i =>
        {
            Assert.Equal(255, tex.PixelsRgba[i * 4 + 3]);     // 墨满
            Assert.Equal(26, tex.PixelsRgba[i * 4]);          // 纸底上铺墨
        });
    }
}
