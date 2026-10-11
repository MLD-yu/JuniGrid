using System.Text;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>素材包页"一行 = 一张图"的显示层。
/// <para>起因（实测 Nexus 1083 那个 furry 包）：账上 1554 行里只有 318 行是纹理，
/// 1236 行是对话表/事件脚本/日程这类 <c>DictionaryReader&lt;String,String&gt;</c>，
/// 而其中 1217 行是同一批资产的 11 种语言副本。界面按文件逐行摊，用户看到的
/// "1554 个图像文件"里绝大多数既不是图、也不是另一张图。</para>
/// <para>这一批钉死三件事：① 不是图的账不列；② 同一张图的语言副本并成一行，
/// 基名那条不在包里时由副本代表（不许凭空少一张）；③ <b>藏起来不等于放手</b> ——
/// 整包关掉时那些看不见的文件必须照样退回原版。</para></summary>
public class PackImageRowTests
{
    // ─────────────── 造账：直接写 Content + 记一条覆盖，绕开安装链路 ───────────────

    /// <summary>真格式【文本表】xnb：类型表里只有 DictionaryReader，没有 Texture2DReader。
    /// <para>填到内容区长 177 + 4k —— 也就是 <c>XnbCodec.TryTextureArea</c> 那道"只读 14 字节头
    /// 看面积"的形状测试会算出正的面积。它实测会把 317 个文本行当成图（Maru 排程表 area=636、
    /// Penny 对话表 3177），所以判"是不是图"必须问类型表，不能问面积。
    /// <paramref name="k"/> 只是让"原版"和"包盖上去那份"字节不同，好让还原断言有意义。</para></summary>
    private static byte[] DictXnb(int k = 100)
    {
        const string Reader =
            "Microsoft.Xna.Framework.Content.DictionaryReader`2[[System.String, mscorlib]," +
            "[System.String, mscorlib]], mscorlib";
        var name = Encoding.ASCII.GetBytes(Reader);
        var body = new List<byte> { 1 };
        body.AddRange(Enc7(name.Length));
        body.AddRange(name);
        body.Add(0);                      // 0 个外部引用
        body.AddRange(Enc7(0));           // 内联值的 reader 名：空
        body.AddRange(Enumerable.Repeat((byte)0x20, 177 + 4 * k - body.Count));
        return XnbCodec.WrapRaw(body.ToArray());
    }

    private static IEnumerable<byte> Enc7(int v)
    {
        while (true)
        {
            var b = (byte)(v & 0x7f);
            v >>= 7;
            if (v == 0) { yield return b; yield break; }
            yield return (byte)(b | 0x80);
        }
    }

    /// <summary>往沙盒本体里放一个资产、把原版那份存进原版仓库、再记一条"这个包盖着它"的账。
    /// 走的是安装链路留下的同一套盘上结构，所以还原那条路才有的可还原。</summary>
    private static ContentOverrideRecord Cover(GameSandbox s, string slot, string asset,
        byte[] mod, byte[]? vanilla = null)
    {
        if (vanilla is not null)
            GameSandbox.Write(ContentOverrideService.OriginalPath(s.Dir, asset, "1.6.15"), vanilla);
        GameSandbox.Write(s.Content(asset), mod);
        var rec = new ContentOverrideRecord
        {
            Asset = asset,
            Slot = slot,
            PackName = slot,
            PackDir = slot,
            StoreId = "",
            Version = "1.6.15",
            Enabled = true,
            OriginalSha256 = vanilla is null ? "" : GameSandbox.Hex(vanilla),
            AppliedSha256 = GameSandbox.Hex(mod),
        };
        s.Cfg.Current.ContentOverrides[ContentOverrideService.KeyOf(asset, slot)] = rec;
        s.Cfg.Save(s.Cfg.Current);
        return rec;
    }

    private static byte[] Tex(int w = 64, int h = 64, byte fill = 0x40) => GameSandbox.VanillaXnb(w, h, fill);


    // ─────────────── ① 不是图的账不列，语言副本并成一行 ───────────────

    [Fact]
    public void ImageItems_SkipTextRows_AndMergeLocaleCopies()
    {
        using var s = new GameSandbox("1.6.15");
        const string slot = "N1083";
        Cover(s, slot, "Portraits/Emily", Tex());
        Cover(s, slot, "Portraits/Emily.zh-CN", Tex());
        Cover(s, slot, "Portraits/Emily.ja-JP", Tex());
        Cover(s, slot, "Data/Boots", DictXnb());                 // 文本表，面积测试会误判成图
        Cover(s, slot, "Data/Boots.zh-CN", DictXnb());
        Cover(s, slot, "Characters/Dialogue/Emily", DictXnb());

        var g = Assert.Single(ContentOverrideService.Groups(s.Dir, s.Cfg));
        Assert.Equal(6, g.Items.Count);                           // 账还是 6 条
        Assert.Equal(new[] { "Portraits/Emily" },
            g.ImageItems.Select(r => r.Asset).ToArray());         // 界面只有 1 行
    }

    [Fact]
    public void ImageItems_KeepsALocaleOnlyImage_WhenThePackHasNoBaseCopy()
    {
        using var s = new GameSandbox("1.6.15");
        const string slot = "N1";
        // 作者只交出中文那一份（基名那张本体里也不是这个包盖的）—— 并组后这条不能消失，
        // 否则就是"装上了、界面上找不到、也没法关掉"。
        Cover(s, slot, "LooseSprites/Cursors.zh-CN", Tex());

        var g = Assert.Single(ContentOverrideService.Groups(s.Dir, s.Cfg));
        Assert.Equal(new[] { "LooseSprites/Cursors.zh-CN" },
            g.ImageItems.Select(r => r.Asset).ToArray());
        Assert.Equal(new[] { "LooseSprites/Cursors.zh-CN" },
            ContentOverrideService.SlotSiblings(s.Cfg, slot, "LooseSprites/Cursors").OrderBy(x => x).ToArray());
    }

    // ─────────────── ② 藏起来的文件，整包关掉时照样要还原 ───────────────

    [Fact]
    public void TurningPackOff_RevertsTheRowsTheUiNeverShows()
    {
        using var s = new GameSandbox("1.6.15");
        const string slot = "N1083";
        // 每一行都先立一份不同的原版，再"盖上"，这样还原有没有发生是看得出来的。
        Cover(s, slot, "Portraits/Emily", Tex(fill: 0x77), Tex(fill: 0x11));
        Cover(s, slot, "Portraits/Emily.zh-CN", Tex(fill: 0x77), Tex(fill: 0x22));
        Cover(s, slot, "Data/Boots", DictXnb(200), DictXnb(100));

        var g = Assert.Single(ContentOverrideService.Groups(s.Dir, s.Cfg));
        Assert.Single(g.ImageItems);                              // 界面上这一包只露出 1 行
        Assert.Equal(3, g.Items.Count);                           // 账上是 3 个文件

        Assert.Null(ContentOverrideService.SetPackEnabled(s.Cfg, s.Dir, slot, false));
        // 关掉之后：三个文件全部退回各自那份原版，包括界面上根本没列出来的那两份
        Assert.Equal(s.Sha(ContentOverrideService.OriginalPath(s.Dir, "Portraits/Emily", "1.6.15")),
            s.Sha(s.Content("Portraits/Emily")));
        Assert.Equal(s.Sha(ContentOverrideService.OriginalPath(s.Dir, "Portraits/Emily.zh-CN", "1.6.15")),
            s.Sha(s.Content("Portraits/Emily.zh-CN")));
        Assert.Equal(s.Sha(ContentOverrideService.OriginalPath(s.Dir, "Data/Boots", "1.6.15")),
            s.Sha(s.Content("Data/Boots")));
        Assert.All(g.Items, r => Assert.False(r.Enabled));
    }

    [Fact]
    public void SlotSiblings_CoversBaseAndEveryLocaleCopyOfThatOneImage()
    {
        using var s = new GameSandbox("1.6.15");
        const string slot = "N1083";
        Cover(s, slot, "LooseSprites/Cursors", Tex());
        Cover(s, slot, "LooseSprites/Cursors.zh-CN", Tex());
        Cover(s, slot, "LooseSprites/Cursors.fr-FR", Tex());
        Cover(s, slot, "LooseSprites/Cursors2", Tex());           // 另一个名字，不能被并进来
        Cover(s, slot, "Portraits/Emily", Tex());

        var sib = ContentOverrideService.SlotSiblings(s.Cfg, slot, "LooseSprites/Cursors")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "LooseSprites/Cursors", "LooseSprites/Cursors.fr-FR", "LooseSprites/Cursors.zh-CN",
        }, sib);
    }

    [Fact]
    public void MixedGroup_RepresentedByAnEnabledCopy_SoTheRowDoesNotLie()
    {
        using var s = new GameSandbox("1.6.15");
        const string slot = "N1083";
        // 真机状态：1083 那包的 Cursors 基名被单独关掉过，11 份语言副本还盖着。
        // 拿基名当代表会让这一行/这张卡报成"没应用"，而游戏里早就是 fur 鼠标了。
        Cover(s, slot, "LooseSprites/Cursors", Tex()).Enabled = false;
        Cover(s, slot, "LooseSprites/Cursors.zh-CN", Tex());
        s.Cfg.Save(s.Cfg.Current);

        var g = Assert.Single(ContentOverrideService.Groups(s.Dir, s.Cfg));
        var row = Assert.Single(g.ImageItems);
        Assert.Equal("LooseSprites/Cursors.zh-CN", row.Asset);
        Assert.True(row.Enabled);
        Assert.Equal(ContentOverrideService.OverrideState.InPlace,
            ContentOverrideService.ImageGroupState(s.Dir, s.Cfg, slot, "LooseSprites/Cursors"));
        // 「按对象」那张卡也得跟着说"有版本在位"
        Assert.True(Assert.Single(ContentOverrideTargets.Build(s.Cfg,
            ContentOverrideService.ShownRowPredicate(s.Dir, s.Cfg))).AnyOn);
    }

    // ─────────────── ④ 删除时原包备份要跟着走 ───────────────

    [Fact]
    public void DeletePack_RemovesTheOriginalPackBackup()
    {
        using var s = new GameSandbox("1.6.15");
        const string slot = "N1083";
        var vanilla = Tex(fill: 0x11);
        GameSandbox.Write(ContentOverrideService.OriginalPath(s.Dir, "Portraits/Emily", "1.6.15"), vanilla);
        var rec = Cover(s, slot, "Portraits/Emily", Tex(fill: 0x77), vanilla);
        rec.PackDir = "thePack";
        s.Cfg.Save(s.Cfg.Current);
        // 原包备份：结构与原包一致的目录，里面随便放一个文件代表那 200MB
        var backup = Path.Combine(ContentOverrideService.PacksRoot(s.Dir), "thePack", "Content", "Portraits");
        GameSandbox.Write(Path.Combine(backup, "Emily.xnb"), Tex());
        Assert.True(Directory.Exists(backup));

        Assert.Null(ContentOverrideService.DeletePack(s.Cfg, s.Dir, slot));

        Assert.False(Directory.Exists(Path.Combine(ContentOverrideService.PacksRoot(s.Dir), "thePack")));
        Assert.Empty(s.Cfg.Current.ContentOverrides);
        // 本体那张退回当初那份原版（原版快照在删除时会被一并丢掉，所以哈希提前记）
        Assert.Equal(GameSandbox.Hex(vanilla), s.Sha(s.Content("Portraits/Emily")));
    }

    [Fact]
    public void DeletePack_KeepsTheBackup_WhenAnotherSlotStillClaimsThatFolder()
    {
        using var s = new GameSandbox("1.6.15");
        var a = Cover(s, "N1", "Portraits/Emily", Tex());
        var b = Cover(s, "N2", "Portraits/Alex", Tex());
        a.PackDir = "shared";
        b.PackDir = "shared";          // 老账才可能这样；新装撞名会落 -2
        s.Cfg.Save(s.Cfg.Current);
        var backup = Path.Combine(ContentOverrideService.PacksRoot(s.Dir), "shared");
        Directory.CreateDirectory(backup);

        Assert.Null(ContentOverrideService.DeletePack(s.Cfg, s.Dir, "N1"));
        Assert.True(Directory.Exists(backup));        // 还有 N2 指着它，不能删

        Assert.Null(ContentOverrideService.DeletePack(s.Cfg, s.Dir, "N2"));
        Assert.False(Directory.Exists(backup));       // 最后一个认领者走了才清
    }

    [Fact]
    public void DeletePack_KeepsTheBackup_WhenTheLedgerNeverRecordedAFolder()
    {
        using var s = new GameSandbox("1.6.15");
        Cover(s, "N1", "Portraits/Emily", Tex());      // PackDir 留空 = 老账
        var stray = Path.Combine(ContentOverrideService.PacksRoot(s.Dir), "somethingElse");
        Directory.CreateDirectory(stray);

        Assert.Null(ContentOverrideService.DeletePack(s.Cfg, s.Dir, "N1"));
        Assert.True(Directory.Exists(stray));          // 不知道是谁的，不动手
    }

    // ─────────────── ⑤ 安装只收图像，文字/数据表一律拒 ───────────────

    [Fact]
    public void Install_TakesImages_AndRefusesTextTables()
    {
        using var s = new GameSandbox("1.6.15");
        s.WriteVanilla("Portraits/Emily", 128, 320);
        GameSandbox.Write(s.Content("Data/Boots"), DictXnb());     // 本体里就是 DictionaryReader

        var src = TempDirs.Create("pack");
        GameSandbox.Write(Path.Combine(src, "Portraits", "Emily.xnb"), GameSandbox.VanillaXnb(128, 320, 0x77));
        GameSandbox.Write(Path.Combine(src, "Data", "Boots.xnb"), DictXnb(200));

        var plan = ContentOverrideService.Prepare(s.Dir, src);
        Assert.Equal(new[] { "Portraits/Emily" }, plan.Assets.ToArray());
        var rej = Assert.Single(plan.RejectRows);
        Assert.Equal("Data/Boots", rej.Asset);
        Assert.Contains("文字/数据表", rej.Msg);
        // 拒了就不能顺手把本体那份改掉：本体仍是原版那一份
        Assert.Equal(GameSandbox.Hex(DictXnb()), s.Sha(s.Content("Data/Boots")));
    }

    // ─────────────── ③ 按对象视图：一张图一张卡，文本表不占卡 ───────────────

    [Fact]
    public void TwoPacks_OnTheSameImage_DifferentlySpelledLocales_StillOneCard()
    {
        using var s = new GameSandbox("1.6.15");
        // 实测：1083 那包 Cursors 的代表是 Cursors.de-DE（基名被单独关掉），
        // 而 13113 那包交出的是基名 Cursors —— 同一个键空间里两种写法，
        // 分组键不剥语言后缀就会拆成两张卡，"这张图上有几个版本"当场说错。
        Cover(s, "N1083", "LooseSprites/Cursors.de-DE", Tex());
        Cover(s, "N13113", "LooseSprites/Cursors", Tex(64, 64, 0x55));

        var targets = ContentOverrideTargets.Build(s.Cfg,
            ContentOverrideService.ShownRowPredicate(s.Dir, s.Cfg));
        var cursors = Assert.Single(targets);
        Assert.Equal("A|LooseSprites/Cursors", cursors.Key);
        Assert.Equal("光标与界面", cursors.Name);
        Assert.Equal("LooseSprites/Cursors", cursors.Sub);
        Assert.Equal(2, cursors.VersionCount);
    }

    [Fact]
    public void TargetGrid_OneCardPerImage_NotPerFile()
    {
        using var s = new GameSandbox("1.6.15");
        const string slot = "N1083";
        Cover(s, slot, "Portraits/Emily", Tex());
        Cover(s, slot, "Portraits/Emily_Winter", Tex());          // 同一个人的另一个形象
        Cover(s, slot, "Portraits/Emily_Winter.zh-CN", Tex());
        Cover(s, slot, "Characters/Emily", Tex());                // 身子，并进同一个对象
        Cover(s, slot, "Data/Boots", DictXnb());
        Cover(s, slot, "Characters/Dialogue/Emily", DictXnb());

        var shown = ContentOverrideService.ShownRowPredicate(s.Dir, s.Cfg);
        var targets = ContentOverrideTargets.Build(s.Cfg, shown);
        // 文本表不占卡；同一个人的【立绘】和【行走图】各占一张 —— 两半分开交付、分开挑，
        // 并成一张卡时"当前：X 包"只说中一半（2026-10-10）。
        Assert.Equal(2, targets.Count);
        Assert.Equal("P|立绘|Emily", targets[0].Key);              // 立绘排在行走图前面
        var face = targets.Single(x => x.Key == "P|立绘|Emily");
        var body = targets.Single(x => x.Key == "P|行走图|Emily");
        Assert.Equal(new[] { "Portraits/Emily", "Portraits/Emily_Winter" }, face.Assets.ToArray());
        Assert.Equal(new[] { "Characters/Emily" }, body.Assets.ToArray());
        Assert.Equal(2, face.VariantCount);                       // 默认 + 冬季
        Assert.Equal(1, body.VariantCount);
        Assert.Equal("立绘", face.Category);
        Assert.Equal("行走图", body.Category);
    }

    /// <summary><c>Content\Characters</c> 底下不只有 NPC 走路表：实测顶层 125 张是走路表，
    /// <c>Farmer/</c> 17 张是玩家自己（发型/皮肤/配饰），<c>Monsters/</c> 73 张是怪物。
    /// 分类只看第一段目录 ⇒ 发型卡被塞进"行走图"，而且 <c>IsPerson</c> 也认它当"某个人的行走图"，
    /// 副标题跟着写"行走图"（2026-10-10 用户在界面上抓到）。
    /// 变异点：CategoryOf 退回只看第一段 / IsPerson 退回只看前缀 ⇒ 这条红在分类与键上。</summary>
    [Fact]
    public void FarmerAndMonsterSheets_AreNotWalkSprites()
    {
        using var s = new GameSandbox("1.6.15");
        const string slot = "N20581";
        Cover(s, slot, "Characters/Farmer/hairstyles", Tex());
        Cover(s, slot, "Characters/Monsters/Bat", Tex());
        Cover(s, slot, "Characters/Abigail", Tex());

        var targets = ContentOverrideTargets.Build(s.Cfg,
            ContentOverrideService.ShownRowPredicate(s.Dir, s.Cfg));
        var hair = targets.Single(t => t.Assets.Contains("Characters/Farmer/hairstyles"));
        Assert.Equal("玩家形象", hair.Category);
        Assert.Equal("A|Characters/Farmer/hairstyles", hair.Key);   // 不是"某个人的一半"
        Assert.Equal("Characters/Farmer/hairstyles", hair.Sub);     // 副标题给真路径，不写"行走图"
        Assert.Equal("怪物", targets.Single(t => t.Assets.Contains("Characters/Monsters/Bat")).Category);
        Assert.Equal("行走图", targets.Single(t => t.Assets.Contains("Characters/Abigail")).Category);
    }
}
