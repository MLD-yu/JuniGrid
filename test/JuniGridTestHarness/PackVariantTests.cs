using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 同一个包里多张图撞到同一张目标（配色/样式）时的行为，以及"同包源图不该冒充不可用"。
/// <para>起因是实测包 Yomi's Golden Princess Hairstyle：4 个配色文件夹里各有一张
/// hairstyles.png/.xnb，8 个文件全指 Characters/Farmer/hairstyles。老判定整组拒绝，
/// 玩家一个都用不上；现在按文件夹分版本，默认装第一版，其余在对象卡的版本行里换。</para>
/// </summary>
public class PackVariantTests
{
    const string Hair = "Characters/Farmer/hairstyles";

    static byte[] Xnb(int w, int h, byte fill) => GameSandbox.VanillaXnb(w, h, fill);

    static byte[] Png(int w, int h, byte fill)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = fill; px[i + 1] = fill; px[i + 2] = fill; px[i + 3] = 255; }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>造一个"多配色"包：每个配色一个文件夹，里面一张同资产的 xnb（可选再放一张同名 png）。</summary>
    static string WriteColorPack(params (string Folder, byte Fill)[] colors)
    {
        var src = TempDirs.Create("color");
        foreach (var (folder, fill) in colors)
            GameSandbox.Write(Path.Combine(src, folder, "hairstyles.xnb"), Xnb(16, 16, fill));
        return src;
    }

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    static ContentOverrideRecord OnlyRow(GameSandbox g)
        => g.Cfg.Current.ContentOverrides.Values.Single();

    [Fact]
    public void ColorsInOnePack_InstallTheFirstAndListTheRest()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var src = WriteColorPack(("白色-White", 0xC3), ("金色-Golden", 0xA1), ("黑色-Black", 0xB2));

        var plan = ContentOverrideService.Prepare(g.Dir, src);
        Assert.Single(plan.Items);                                  // 一张表只能有一条
        Assert.Equal(3, plan.VariantLabels[Hair].Count);
        // 默认那版按文件夹名序号排：白(767D) < 金(91D1) < 黑(9ED1)
        Assert.Equal("白色-White", plan.Items[0].Label);

        var err = ContentOverrideService.Commit(g.Dir, plan, "配色包", null, g.Cfg, out _);
        Assert.Null(err);
        var r = OnlyRow(g);
        Assert.Equal("白色-White|金色-Golden|黑色-Black", string.Join("|", r.Variants));
        Assert.Equal("白色-White", r.VariantLabel);
        Assert.Equal(Hex(Xnb(16, 16, 0xC3)), Hex(File.ReadAllBytes(g.Content(Hair))));
    }

    [Fact]
    public void SwitchVariant_WritesThatColor_AndKeepsOneRecord()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var src = WriteColorPack(("白色-White", 0xC3), ("金色-Golden", 0xA1), ("黑色-Black", 0xB2));
        ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src), "配色包", null, g.Cfg, out _, src);

        var slot = OnlyRow(g).Slot;
        Assert.Null(ContentOverrideService.SwitchVariant(g.Cfg, g.Dir, Hair, slot, "黑色-Black"));

        Assert.Single(g.Cfg.Current.ContentOverrides);                        // 换配色不新增账
        var r = OnlyRow(g);
        Assert.Equal("黑色-Black", r.VariantLabel);
        Assert.Equal(3, r.Variants.Count);
        Assert.True(r.Enabled);
        var onDisk = File.ReadAllBytes(g.Content(Hair));
        Assert.Equal(Hex(Xnb(16, 16, 0xB2)), Hex(onDisk));
        Assert.Equal(Hex(onDisk), r.AppliedSha256);                           // 账跟盘对得上
        Assert.Equal(Hex(onDisk), Hex(File.ReadAllBytes(
            ContentOverrideService.StoreFile(g.Dir, r.StoreId, Hair))));     // 副本也是这一版
    }

    [Fact]
    public void SwitchVariant_SameColorAgain_IsANoOp()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var src = WriteColorPack(("白色-White", 0xC3), ("黑色-Black", 0xB2));
        ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src), "配色包", null, g.Cfg, out _, src);
        var before = File.ReadAllBytes(g.Content(Hair));

        Assert.Null(ContentOverrideService.SwitchVariant(g.Cfg, g.Dir, Hair, OnlyRow(g).Slot, "白色-White"));
        Assert.Equal(before, File.ReadAllBytes(g.Content(Hair)));
    }

    [Fact]
    public void SwitchVariant_WithoutPackBackup_RefusesAndLeavesTheBodyAlone()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var src = WriteColorPack(("白色-White", 0xC3), ("黑色-Black", 0xB2));
        ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src), "配色包", null, g.Cfg, out _, src);
        var r = OnlyRow(g);
        Directory.Delete(ContentOverrideService.PackOriginalDir(g.Dir, r.PackDir), true);
        var before = File.ReadAllBytes(g.Content(Hair));

        Assert.NotNull(ContentOverrideService.SwitchVariant(g.Cfg, g.Dir, Hair, r.Slot, "黑色-Black"));
        Assert.Equal(before, File.ReadAllBytes(g.Content(Hair)));
        Assert.Equal("白色-White", OnlyRow(g).VariantLabel);                  // 账也没改
    }

    [Fact]
    public void Rejudge_KeepsTheColorTheUserPicked_WhenReencoding()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var src = WriteColorPack(("白色-White", 0xC3), ("金色-Golden", 0xA1), ("黑色-Black", 0xB2));
        ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src), "配色包", null, g.Cfg, out _, src);
        var r = OnlyRow(g);
        ContentOverrideService.SwitchVariant(g.Cfg, g.Dir, Hair, r.Slot, "黑色-Black");
        Assert.Null(ContentOverrideService.SetEnabled(g.Cfg, g.Dir, ContentOverrideService.KeyOf(Hair, r.Slot), false));

        // 按当前本体重编：默认那版是"白色"，若不认人挑的那版就会把黑色悄悄换回白色
        var res = ContentOverrideService.Rejudge(g.Cfg, g.Dir, r.Slot, rewriteForCurrentVersion: true);
        Assert.Null(res.Error);
        Assert.Equal(Hex(Xnb(16, 16, 0xB2)), Hex(File.ReadAllBytes(g.Content(Hair))));
        Assert.Equal("黑色-Black", OnlyRow(g).VariantLabel);
    }

    [Fact]
    public void SingleFileVersion_PicksNoColorList()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var src = WriteColorPack(("金色-Golden", 0xA1));
        Assert.Null(ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src), "单色包", null, g.Cfg, out _));
        Assert.Empty(OnlyRow(g).Variants);
        Assert.Equal("", OnlyRow(g).VariantLabel);
    }

    [Fact]
    public void XnbMarkerPrefix_IsStrippedFromTheColorName()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var src = WriteColorPack(("[xnb]金色-Golden", 0xA1), ("[xnb]黑色-Black", 0xB2));
        ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src), "标记包", null, g.Cfg, out _, src);
        Assert.Equal("金色-Golden|黑色-Black", string.Join("|", OnlyRow(g).Variants));   // 作者标编码方式的前缀不进界面
    }

    /// <summary>作者常把 PNG 源图与 XNB 并排放（实测 Jecky 光标包 = Cursors.xnb + cursors.png）。
    /// 装上的是那份 XNB，那张尺寸对不上的同名 PNG 再列一行"不可用"就是纯误导。</summary>
    [Fact]
    public void SourcePngSiblingOfInstalledXnb_IsNotReportedAsUnusable()
    {
        using var g = new GameSandbox();
        g.WriteVanilla("LooseSprites/Cursors", 16, 16);
        var src = TempDirs.Create("sib");
        GameSandbox.Write(Path.Combine(src, "Cursors.xnb"), Xnb(16, 16, 0x55));
        File.WriteAllBytes(Path.Combine(src, "cursors.png"), Png(4, 4, 0x66));   // 裁片，尺寸不可能装

        var plan = ContentOverrideService.Prepare(g.Dir, src);
        Assert.Single(plan.Items);
        Assert.Empty(plan.Rejects);                                   // 关键：源图不占"不适配"名额
        Assert.Null(ContentOverrideService.Commit(g.Dir, plan, "光标包", null, g.Cfg, out _));
        Assert.Equal(0, OnlyRow(g).SkippedCount);
        Assert.Equal("", OnlyRow(g).SkippedNote);
        Assert.Equal(Hex(Xnb(16, 16, 0x55)), Hex(File.ReadAllBytes(g.Content("LooseSprites/Cursors"))));
    }

    /// <summary>每个配色要带上【自己那句】尺寸说明。界面按配色分行时，
    /// 4 个配色的文件全叫 hairstyles.png —— 只有备注各归各的，行才分得开（用户抓到的那条）。</summary>
    [Fact]
    public void EachColorCarriesItsOwnSizeNote()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);                       // 原版 16×16
        var src = TempDirs.Create("notes");
        GameSandbox.Write(Path.Combine(src, "矮-Short", "hairstyles.xnb"), Xnb(16, 8, 0xA1));
        GameSandbox.Write(Path.Combine(src, "高-Tall", "hairstyles.xnb"), Xnb(16, 64, 0xB2));

        Assert.Null(ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src),
            "说明包", null, g.Cfg, out _, src));
        var r = OnlyRow(g);
        Assert.Equal(2, r.VariantFacts.Count);
        var shortNote = r.VariantFacts.First(v => v.Label == "矮-Short").Note;
        var tallNote = r.VariantFacts.First(v => v.Label == "高-Tall").Note;
        Assert.Contains("16×8", shortNote);
        Assert.DoesNotContain("16×64", shortNote);          // 不串到别人身上
        Assert.Contains("16×64", tallNote);
    }

    /// <summary>作者常把同一张画同时给 PNG 与 XNB 两份（同一文件夹里 hairstyles.png + hairstyles.xnb）。
    /// 这是<b>同一版的两种编码</b>，不是两个配色：分版本必须按【文件夹】分，按文件分就变成 3 个配色，
    /// 玩家会看见"金色"和"金色(xnb)"两个选项，而长按其中一个只是把另一份盖掉。</summary>
    [Fact]
    public void SameFolderPngAndXnb_AreOneColor_NotTwo()
    {
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var src = TempDirs.Create("twin");
        GameSandbox.Write(Path.Combine(src, "金色-Golden", "hairstyles.xnb"), Xnb(16, 16, 0xA1));
        File.WriteAllBytes(Path.Combine(src, "金色-Golden", "hairstyles.png"), Png(16, 16, 0xA1));
        GameSandbox.Write(Path.Combine(src, "黑色-Black", "hairstyles.xnb"), Xnb(16, 16, 0xB2));

        var plan = ContentOverrideService.Prepare(g.Dir, src);
        Assert.Equal("金色-Golden|黑色-Black", string.Join("|", plan.VariantLabels[Hair]));   // 两版，不是三版
        Assert.Equal("金色-Golden", plan.Items[0].Label);
        Assert.False(plan.Items[0].Encoded);        // 同版里取作者的 XNB：整份替换，不裁到原版高度
        Assert.NotEqual((long)16 * 16 * 4 + 177, File.ReadAllBytes(Path.Combine(g.Dir, "Content",
            "Characters", "Farmer", "hairstyles.xnb")).Length);      // 写进去的不是 PNG 重编出来的那份
        Assert.Null(ContentOverrideService.Commit(g.Dir, plan, "双编码包", null, g.Cfg, out _, src));
        Assert.Equal(2, OnlyRow(g).Variants.Count);
    }

    /// <summary>2026-10-07 定：一张 XNB 都没有的纯 PNG 包【不走本体覆盖通道】——
    /// 作者只交 PNG 就是交给 CP 加载的形状，我们烤进本体等于替他改发布方式。
    /// 拦的是"要不要接管"这道闸，不是拆掉 PNG 编码能力（混包里某个资产只有 PNG 时还得用它）。</summary>
    [Fact]
    public void PngOnlyPack_IsNotTakenByTheOverrideChannel()
    {
        using var g = new GameSandbox();
        g.WriteVanilla("LooseSprites/Cursors", 16, 16);
        var src = TempDirs.Create("png");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "Cursors.png"), Png(16, 16, 0x77));

        var plan = ContentOverrideService.Prepare(g.Dir, src);
        Assert.Single(plan.Items);                       // 机制上编得出来 —— 拒它是产品决定，不是做不到
        Assert.False(ModService.OverrideChannelTakes(plan, false, true, hasSmapi: true));
        Assert.False(ModService.OverrideChannelTakes(plan, false, true, hasSmapi: false));   // 没 SMAPI 也不绕
        Assert.True(ModService.OverrideChannelTakes(plan, true, false, hasSmapi: true));     // 有 XNB 照旧接管
        Assert.True(ModService.OverrideChannelTakes(plan, true, true, hasSmapi: false));
    }

    // ── 「尺寸不同」那栏的备注认领给哪条记录 ──────────────────────────────

    static ContentOverrideRecord Rec(string asset) => new() { Asset = asset, Slot = "s", PackName = "p" };

    static List<ContentOverrideRecord> MaruRows(params string[] assets) => assets.Select(Rec).ToList();

    /// <summary>备注就是 ContentOverrideService 里那句 "文件：此文件 a×b，游戏 资产 为 c×d —— …"。</summary>
    static string Note(string file, string asset, string dim)
        => $"{file}：此文件 {dim}，游戏 {asset} 为 128×128 —— 按原样整体替换，超出部分本游戏用不上";

    [Fact]
    public void NoteAboutBeach_IsNotClaimedByTheDefaultPortrait()
    {
        // 账上默认形象排在沙滩前面，而 "Portraits/Maru" 正好是 "Portraits/Maru_Beach" 的前缀。
        var items = MaruRows("Portraits/Maru", "Portraits/Maru_Beach");
        var hit = ContentOverrideTargets.RecordForNote(items, "Maru_Beach.xnb",
            Note("Maru_Beach.xnb", "Portraits/Maru_Beach", "128×320"));

        Assert.Equal("Portraits/Maru_Beach", hit?.Asset);
    }

    [Fact]
    public void NoteAboutBeach_IsFound_RegardlessOfRowOrder()
        => Assert.Equal("Portraits/Maru_Beach",
            ContentOverrideTargets.RecordForNote(
                MaruRows("Portraits/Maru_Beach", "Portraits/Maru"), "Maru_Beach.xnb",
                Note("Maru_Beach.xnb", "Portraits/Maru_Beach", "128×320"))?.Asset);

    [Fact]
    public void NoteAboutDefault_IsNotStolenByTheLongerAssetName()
    {
        // 反方向同样致命：沙滩那条在前，整串 Contains 会让"默认"这行指向沙滩的图。
        var items = MaruRows("Portraits/Maru_Beach", "Portraits/Maru");
        var hit = ContentOverrideTargets.RecordForNote(items, "Maru.xnb",
            Note("Maru.xnb", "Portraits/Maru", "128×320"));
        Assert.Equal("Portraits/Maru", hit?.Asset);
    }

    [Fact]
    public void NoteWithoutAnAssetPath_FallsBackToTheFileName()
    {
        var items = MaruRows("Portraits/Maru", "Portraits/Maru_Beach");
        var hit = ContentOverrideTargets.RecordForNote(items, "Maru_Beach.xnb", "宽度不一致，整表替换会导致横向错位");
        Assert.Equal("Portraits/Maru_Beach", hit?.Asset);
    }

    [Fact]
    public void NoteNamingNothing_LeavesTheRowUnclaimed()
    {
        var items = MaruRows("Portraits/Maru");
        Assert.Null(ContentOverrideTargets.RecordForNote(items, "Abigail_Beach.xnb",
            Note("Abigail_Beach.xnb", "Portraits/Abigail_Beach", "128×320")));
    }

    /// <summary>尺寸备注是【整包一份】抄到包里每条记录上的（安装当时的快照），界面靠"这条备注
    /// 说的是哪张图"把它并回那一行的小字。认错 ⇒ 同一张图在卡片的"尺寸不同"栏又冒出来一次，
    /// 一张正常一行"不适配"（用户 2026-10-07：为什么有两个玛鲁沙滩图）。</summary>
    [Fact]
    public void PackLevelSizeNote_AttachesToItsOwnAssetRow()
    {
        using var g = new GameSandbox();
        g.WriteVanilla("Portraits/Maru", 16, 16);
        g.WriteVanilla("Portraits/Maru_Beach", 16, 16);
        var src = g.WritePack("沙滩包", ("Portraits/Maru", 16, 32), ("Portraits/Maru_Beach", 16, 32));
        ContentOverrideService.Commit(g.Dir, ContentOverrideService.Prepare(g.Dir, src),
            "沙滩包", null, g.Cfg, out _, src);

        var rows = g.Cfg.Current.ContentOverrides.Values.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].ResizedNote, rows[1].ResizedNote);          // 同一份包级快照，抄了两遍
        Assert.Contains("Portraits/Maru_Beach", rows[0].ResizedNote);

        var forwards = rows;
        var backwards = rows.GetRange(0, rows.Count);
        backwards.Reverse();
        foreach (var order in new[] { forwards, backwards })             // 账上先后不该改变认领结果
            foreach (var line in rows[0].ResizedNote.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cut = line.IndexOf('：');
                var name = line[..cut];
                var reason = line[(cut + 1)..];
                var want = "Portraits/" + Path.GetFileNameWithoutExtension(name);
                Assert.Equal(want, ContentOverrideTargets.RecordForNote(order, name, reason)?.Asset);
            }
    }

    /// <summary>接线闸：这几个造「源包目录」的 helper 必须走 <see cref="TempDirs"/>。
    /// 漏在实机上的形态是 %TEMP% 里 3244 个 jg-pack-*（跑一轮漏一批，谁都不负责删），
    /// 所以钉的不只是「TempDirs 会删」，而是「helper 交出来的每个目录确实在登记表上」。</summary>
    [Fact]
    public void EveryPackSourceHelper_RegistersItsTempDir()
    {
        using var g = new GameSandbox();

        var color = WriteColorPack(("白色-White", 0xC3));
        var viaSandbox = g.WritePack("裸包", ("Portraits/Emily", 16, 16));

        Assert.True(TempDirs.IsRegistered(color), "配色包目录没进登记表");
        Assert.True(TempDirs.IsRegistered(viaSandbox), "GameSandbox.WritePack 目录没进登记表");
        Assert.True(TempDirs.IsRegistered(g.Dir), "沙盒本身没进登记表（Dispose 删不动时无人接手）");
    }
}
