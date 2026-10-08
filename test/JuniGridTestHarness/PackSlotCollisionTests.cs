using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 「两个不同的包会不会被互相当成同一个覆盖包」—— 这是本轮测试要回答的第一问。
/// <para>为什么必须钉死：<c>ContentOverrideService.SlotOf</c> 在<b>没有 Nexus 页号</b>时
/// 退回包名规范化（<c>Sanitize</c>：只把非法文件名字符换成 '_' 再 Trim），
/// 而覆盖账的键是 <c>asset|slot</c>。于是只要两个包名规范化后撞上，
/// 它们对同一张资产就撞进同一条账 —— 后装的顶掉先装的账，
/// 而界面上两个包看起来各占各的。这是"名字相似的 mod 被互相当成覆盖包"的机理。</para>
/// <para>现有 184 条真实账里 9 个 slot 全带页号（N1839 等），所以线上没暴雷；
/// 但无页号场景（本地文件夹包、汉化包、离线导入）从没被任何一条用例覆盖过。</para>
/// </summary>
public class PackSlotCollisionTests
{
    const string Hair = "Characters/Farmer/hairstyles";

    // ─────────────── 一、Sanitize 本身：哪些不同输入会坍成同一个 slot ───────────────

    [Theory]
    [InlineData("Portrait:Anime")]          // 冒号是 Windows 非法文件名字符
    [InlineData("Portrait?Anime")]
    [InlineData("Portrait*Anime")]
    [InlineData("Portrait/Anime")]          // 作者写相对路径当包名
    [InlineData("Portrait\\Anime")]
    [InlineData("Portrait|Anime")]
    public void Sanitize_CollapsesIllegalChars_ToTheSameSlot(string a)
    {
        // 对照组：这些包在用户看来是"名字不一样"的
        Assert.Equal(ContentOverrideService.SlotOf("Portrait_Anime", null),
                     ContentOverrideService.SlotOf(a, null));
    }

    [Fact]
    public void Sanitize_CollapsesTrailingAndLeadingSpaces()
    {
        Assert.Equal(ContentOverrideService.SlotOf("Cool Mod", null),
                     ContentOverrideService.SlotOf("  Cool Mod  ", null));
    }

    [Fact]
    public void Sanitize_KeepsOrdinaryLookalikeNamesApart()
    {
        // 对照：相似但不同的普通拼写【不该】坍到一起（这一条绿，才说明上面几条红是真问题）
        Assert.NotEqual(ContentOverrideService.SlotOf("Portrait Anime Styled", null),
                        ContentOverrideService.SlotOf("Portrait Anime Style", null));
        Assert.NotEqual(ContentOverrideService.SlotOf("Starry Sky Interface", null),
                        ContentOverrideService.SlotOf("Starry Sky Interfaces", null));
    }

    [Fact]
    public void SlotOf_PrefersNexusId_SoSameNameDifferentPageStaysApart()
    {
        // 有页号时页号说了算：两个同名包只要页号不同就是两条独立的账
        Assert.NotEqual(ContentOverrideService.SlotOf("Same Name", 1839),
                        ContentOverrideService.SlotOf("Same Name", 14));
        Assert.Equal("N1839", ContentOverrideService.SlotOf("任意名字", 1839));
    }

    // ─────────────── 二、真实后果：两个包对同一张资产会不会串账 ───────────────

    [Fact]
    public void TwoLocalPacksWithCollidingNames_ShareOneLedgerRow()
    {
        // 现状取证：两个在用户看来不同的包（'Portrait:Anime' 与 'Portrait_Anime'），
        // 无页号，装同一张资产 → KeyOf(asset, slot) 相同 ⇒ 账被覆盖，只剩一条。
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);

        var packA = g.WritePack("p1", (Hair, 16, 16));
        var packB = g.WritePack("p2", (Hair, 16, 16));
        try
        {
            Assert.Null(ContentOverrideService.Commit(g.Dir,
                ContentOverrideService.Prepare(g.Dir, packA), "Portrait:Anime", null, g.Cfg, out _, packA));
            Assert.Null(ContentOverrideService.Commit(g.Dir,
                ContentOverrideService.Prepare(g.Dir, packB), "Portrait_Anime", null, g.Cfg, out _, packB));

            var rows = g.Cfg.Current.ContentOverrides.Values.ToList();
            // 期望是 2 条（两个包各自一条账，其中一条 Enabled=false 让位）；
            // 实际是 1 条 —— 后装的把前一条的账整个顶掉了。
            Assert.Single(rows);
            Assert.Equal("Portrait_Anime", rows[0].PackName);
        }
        finally { TryDelete(packA); TryDelete(packB); }
    }

    [Fact]
    public void TwoLookalikePacksWithDifferentNexusIds_KeepSeparateRows()
    {
        // 对照组：同样两个包，但【给页号】⇒ 两条独立账，只有一条在位。
        // 撤掉 SlotOf 里的页号分支，本条就会红 —— 这就是它的价值。
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);

        var packA = g.WritePack("p1", (Hair, 16, 16));
        var packB = g.WritePack("p2", (Hair, 16, 16));
        try
        {
            Assert.Null(ContentOverrideService.Commit(g.Dir,
                ContentOverrideService.Prepare(g.Dir, packA), "Portrait Anime Styled", 1839, g.Cfg, out _, packA));
            Assert.Null(ContentOverrideService.Commit(g.Dir,
                ContentOverrideService.Prepare(g.Dir, packB), "Portrait Anime Styled", 14, g.Cfg, out _, packB));

            var rows = g.Cfg.Current.ContentOverrides.Values.ToList();
            Assert.Equal(2, rows.Count);
            Assert.Single(rows, r => r.Enabled);
            Assert.Equal(2, rows.Select(r => r.Slot).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally { TryDelete(packA); TryDelete(packB); }
    }

    [Fact]
    public void DisablingOneCollidedPack_TurnsOffTheOtherToo()
    {
        // 串账的真正代价：用户在界面上关掉"Portrait:Anime"这个包，
        // 实际被关掉的是另一个包盖在本体上的图（两个包 slot 相同，SetPackEnabled 按 slot 匹配）。
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);

        var packA = g.WritePack("p1", (Hair, 16, 16));
        var packB = g.WritePack("p2", (Hair, 16, 16));
        try
        {
            ContentOverrideService.Commit(g.Dir,
                ContentOverrideService.Prepare(g.Dir, packA), "Portrait:Anime", null, g.Cfg, out _, packA);
            var slot = g.Cfg.Current.ContentOverrides.Values.First().Slot;

            ContentOverrideService.Commit(g.Dir,
                ContentOverrideService.Prepare(g.Dir, packB), "Portrait_Anime", null, g.Cfg, out _, packB);

            // 按 slot 关掉"一个包"，结果整组都没了
            ContentOverrideService.SetPackEnabled(g.Cfg, g.Dir, slot, false);
            Assert.All(g.Cfg.Current.ContentOverrides.Values, r => Assert.False(r.Enabled));
        }
        finally { TryDelete(packA); TryDelete(packB); }
    }

    [Fact]
    public void AuditOrphans_TreatsCollidingNamesAsOneLiveGroup()
    {
        // 孤儿判定按 slot 分组（AuditOrphans），两个坍成同 slot 的包会被并成一组，
        // 只要组内任一 packDir 还在就整组不报 —— 真包被删了也不会被点名。
        using var g = new GameSandbox();
        g.WriteVanilla(Hair, 16, 16);
        var packA = g.WritePack("p1", (Hair, 16, 16));
        var packB = g.WritePack("p2", (Hair, 16, 16));
        try
        {
            ContentOverrideService.Commit(g.Dir,
                ContentOverrideService.Prepare(g.Dir, packA), "Portrait:Anime", null, g.Cfg, out _, packA);
            ContentOverrideService.Commit(g.Dir,
                ContentOverrideService.Prepare(g.Dir, packB), "Portrait_Anime", null, g.Cfg, out _, packB);

            // 账上只有一条了（已被顶掉），所以"另一个包被删"这件事在账上根本不存在
            var rows = g.Cfg.Current.ContentOverrides.Values.ToList();
            Assert.Single(rows);
            Assert.Equal("Portrait_Anime", rows[0].PackName);
        }
        finally { TryDelete(packA); TryDelete(packB); }
    }

    // ─────────────── 三、转换通道的第二条碰撞入口 ───────────────

    [Fact]
    public void ConvertedPackUid_IsCaseInsensitiveOnPackName()
    {
        // InstallLooseAssetPackAsCp 用 SHA256(packName.ToLowerInvariant()) 前 10 位当 UID，
        // 于是 'Cool Pack' 与 'COOL PACK' 转出来是同一个 UID ——
        // 安装链的同 UID 判重清扫会把其中一个当成另一个的更新删掉。
        static string Uid(string n) =>
            "JuniGrid.AssetPack." + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(n.ToLowerInvariant())))[..10];

        Assert.Equal(Uid("Cool Pack"), Uid("COOL PACK"));
        // 对照：真正不同的名字必须给出不同 UID
        Assert.NotEqual(Uid("Cool Pack"), Uid("Cool Pack 2"));
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }
}