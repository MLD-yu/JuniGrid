using System.Globalization;
using System.Text.RegularExpressions;
using JuniGrid.Services;

namespace JuniGridTestHarness;

// ---------------------------------------------------------------- AppInfo

public class AppInfoTests
{
    [Fact]
    public void Version_IsThreePartSemanticVersion()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
    }

    [Fact]
    public void RepoConstants_ProduceOfficialUrls()
    {
        Assert.Equal("MLD-yu", AppInfo.RepoOwner);
        Assert.Equal("JuniGrid", AppInfo.RepoName);
        Assert.Equal("https://github.com/MLD-yu/JuniGrid/releases", AppInfo.ReleasesUrl);
        Assert.Equal("https://api.github.com/repos/MLD-yu/JuniGrid/releases/latest", AppInfo.LatestApiUrl);
    }
}

// ------------------------------------------------- ResumableDownload.FormatBytes

public class FormatBytesTests
{
    public FormatBytesTests()
    {
        // F0/F1 数字格式受 CurrentCulture 小数点影响，钉成不变文化保证断言稳定
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048575, "1024.0 KB")]     // 差 1 字节进 MB —— 界限行为钉住
    [InlineData(1048576, "1.0 MB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(1536L * 1024 * 1024, "1.5 GB")]
    [InlineData(5L << 40, "5120.0 GB")]    // 没有 TB 档：5TB 折成 GB 显示（行为钉住）
    public void Tiers_Match(long bytes, string expected)
        => Assert.Equal(expected, ResumableDownload.FormatBytes(bytes));
}

// ------------------------------------------------- LogLineClassifier

public class LogLineClassifierTests
{
    [Theory]
    [InlineData("[JuniGrid] 已注册 nxm:// 协议处理器", "sys")]
    [InlineData("[ERR] Mod X 加载失败", "err")]
    [InlineData("boom ERROR boom", "err")]
    [InlineData("[ERROR] bad thing", "err")]
    [InlineData("[10:00:00 ALERT SMAPI] DynamicShader 1.0.41: https://… (you have 1.0.39)", "upd")]
    [InlineData("[10:00:00 WARN SMAPI] watch out", "warn")]
    [InlineData("[10:00:00 INFO SMAPI] detail", "info")]
    [InlineData("[10:00:00 TRACE SMAPI] spam", "trace")]
    [InlineData("[10:00:00 DEBUG SMAPI] spam", "trace")]
    public void LevelTags_Win(string line, string expected)
        => Assert.Equal(expected, LogLineClassifier.Classify(line));

    [Fact]
    public void InfoLine_MentioningUpdate_StaysInfo_TagWins()
    {
        // 日志行自带级别标签时标签优先：mod 简介碰巧含 "update" 的 INFO 行不能染成紫
        Assert.Equal("info",
            LogLineClassifier.Classify("[08:00:00 INFO SMAPI] 1.6 update by ConcernedApe | page"));
    }

    [Theory]
    [InlineData("   at StardewValley.Game1.UpdateLocations() in D:\\Game1.cs:line 6169")]
    [InlineData("System.NullReferenceException: Object reference not set")]
    [InlineData("   --- End of inner exception stack trace ---")]
    [InlineData("InnerException:")]
    public void UntaggedExceptionLines_AreErr(string line)
        => Assert.Equal("err", LogLineClassifier.Classify(line));

    [Theory]
    [InlineData("Mod X update available")]
    [InlineData("DynamicShader 1.0.41: https://nexus… (you have 1.0.39)")]
    public void UntaggedUpdateHints_AreUpd(string line)
        => Assert.Equal("upd", LogLineClassifier.Classify(line));

    [Fact]
    public void PlainUntaggedLine_IsEmpty()
        => Assert.Equal("", LogLineClassifier.Classify("hello world"));

    [Theory]
    [InlineData("[ERR] x", "err", true)]
    [InlineData("[10:00:00 INFO SMAPI] ok", "err", false)]
    [InlineData("[10:00:00 ALERT SMAPI] (you have 1.0.39)", "upd", true)]
    [InlineData("[10:00:00 INFO SMAPI] fine", "upd", false)]
    public void MatchesFilter_ErrAndUpd_RideOnClassify(string line, string filter, bool expected)
        => Assert.Equal(expected, LogLineClassifier.MatchesFilter(line, filter));

    [Theory]
    [InlineData("Loaded 3 mods:", true)]
    [InlineData("[08:00:00 INFO SMAPI]    Cloudy Skies 1.9.1 by Khloe Leclair | Page: https://nexusmods.com", true)]
    [InlineData("random line", false)]
    public void MatchesFilter_LoadedBlock(string line, bool expected)
        => Assert.Equal(expected, LogLineClassifier.MatchesFilter(line, "loaded"));

    [Fact]
    public void UnknownFilter_MatchesEverything()
        => Assert.True(LogLineClassifier.MatchesFilter("whatever", "all"));
}

// ------------------------------------------------------- 依赖作者亲和度（InstallService）

public class AuthorAffinityTests
{
    [Theory]
    // 作者改名加尾缀：DLX.PIF 的作者 DeLiXx → Nexus 账号 DeLiXxN，缩写 "dlx" → "dlxn"。
    // 旧规则全等判不上 → 亲和度 0 →「PIF - Personal Rooms」在搜索结果第一名也被弃。
    [InlineData("DLX.PIF", "DeLiXxN", 1)]      // 改名后：缩写恰好多一个尾字母
    [InlineData("DLX.PIF", "DeLiXx", 1)]       // 改名前：缩写全等
    [InlineData("Sharogg.Tilesheets", "Sharogg", 3)]   // 作者段与上传者完全相同
    [InlineData("Wildflourmods.Cozy", "Wildflourmods", 3)]
    [InlineData("Wildflour.Cozy", "Wildflourmods", 2)] // 一方是另一方的前缀
    // 随机串里恰好含有 3 个字母不作数：实测缓存里 BFV.CombinedCrops 被认成
    // 「Stardew Valley Expanded … Russian」，上传者 jhmngbfvdcsaxdcfvgbh 中段含 "bfv" 就过了旧规则
    [InlineData("BFV.CombinedCrops", "jhmngbfvdcsaxdcfvgbh", 0)]
    // Miku 陷阱：搜 "Miku" 按下载第一是 lucasedu11 的皮肤包，绝不能认成 ChaseXavier 的
    [InlineData("ChaseXavier.Miku", "lucasedu11", 0)]
    [InlineData("ChaseXavier.Miku", "ChaseXavier", 3)]
    [InlineData("SomeMod", "Anyone", 0)]       // UID 没有作者段 → 无从判
    public void Affinity_Levels(string uid, string uploader, int expected)
        => Assert.Equal(expected, InstallService.AuthorAffinity(uid, uploader));

    [Fact]
    public void RankByAuthor_PutsRealOwnerFirst_DespiteDownloadOrder()
    {
        // Sharogg.Tilesheets 实况（2026-10-07）：搜 "Tilesheets" 按下载排，本体（27117，
        // 10.9 万下载）排不进前 8 —— DaisyNiko 298 万、Lumisteria 160 万……
        // 排序器必须把作者对得上的候选顶到最前，而不是跟着下载量走。
        var hits = new List<NexusModListEntry>
        {
            new(4736, "DaisyNiko's Tilesheets", "", "", 2982606, "", Uploader: "DaisyNiko"),
            new(9599, "Lumisteria Tilesheets - Indoor", "", "", 1601042, "", Uploader: "Lumisteria"),
            new(27117, "Sharogg's Tilesheets (indoor and outdoor structures)", "", "", 109251, "", Uploader: "Sharogg"),
        };
        var ranked = InstallService.RankByAuthor("Sharogg.Tilesheets", hits);
        Assert.Equal(27117, ranked[0].Id);
    }

    [Fact]
    public void RankByAuthor_SameAuthorManyMods_NameSegmentBreaksTie()
    {
        // 同一作者挂一排 mod 时（Sharogg 实况 8 个），亲和度全是并列的 ——
        // 下载数最高的那个不是依赖本体，靠名字段指出谁对得上。
        var hits = new List<NexusModListEntry>
        {
            new(36778, "Sharogg's Panoramas (MMAP)", "", "", 79030, "", Uploader: "Sharogg"),
            new(25552, "Balcony (PIF)", "", "", 67362, "", Uploader: "Sharogg"),
            new(27117, "Sharogg's Tilesheets (indoor and outdoor structures)", "", "", 109251, "", Uploader: "Sharogg"),
        };
        // 先把"下载最高"那个换成同作者的别的 mod，确认不靠下载数
        var swapped = new List<NexusModListEntry>
        {
            new(99999, "Sharogg's Bigger Thing", "", "", 500000, "", Uploader: "Sharogg"),
            new(27117, "Sharogg's Tilesheets (indoor and outdoor structures)", "", "", 109251, "", Uploader: "Sharogg"),
        };
        Assert.Equal(27117, InstallService.RankByAuthor("Sharogg.Tilesheets", hits)[0].Id);
        Assert.Equal(27117, InstallService.RankByAuthor("Sharogg.Tilesheets", swapped)[0].Id);
    }

    [Theory]
    [InlineData("Sharogg.Tilesheets", "Sharogg's Tilesheets (indoor and outdoor structures)", 1)]
    [InlineData("Sharogg.Tilesheets", "Sharogg's Panoramas (MMAP)", 0)]
    [InlineData("DLX.PIF", "PIF - Personal Rooms", 1)]
    [InlineData("ChaseXavier.Miku", "CP_Miku NPC", 1)]
    [InlineData("a.UI", "UI Info Suite 2", 0)]      // 名字段 <3 字符不作数，防泛词点亮全排
    [InlineData("SomeMod", "Anything", 0)]          // UID 没有作者段
    // 多段 UID：整串 "parchmentcore" 在真实页面名里不存在，所以按点号拆段认（实机 2026-10-13）
    [InlineData("PeacefulEnd.Parchment.Core", "Parchment", 1)]
    [InlineData("PeacefulEnd.Parchment.Core", "Fashion Sense", 0)]      // 同作者的别的 mod
    [InlineData("PeacefulEnd.Parchment.Core", "Scoreboard", 0)]         // "core" 不许从单词中间抠出来
    public void NameMatchScore_Levels(string uid, string name, int expected)
        => Assert.Equal(expected, InstallService.NameMatchScore(uid, name));

    /// <summary>搜索通道的放行判据矩阵：两道闸各自都有"只有它能拦住"的那一行，
    /// 拆掉任意一道都会红一条用例（这就是变异检查的靶子）。</summary>
    [Theory]
    // 只有名字闸拦得住：同作者、不同 mod（实机 2026-10-13 那条红字的成因）
    [InlineData("PeacefulEnd.Parchment.Core", "Fashion Sense", "PeacefulEnd", false)]
    // 只有作者闸拦得住：名字对得上、作者是别人（Miku 陷阱，2026-09-26）
    [InlineData("ChaseXavier.Miku", "Miku skin for Abigail", "lucasedu11", false)]
    // 两道都拦不住的那种随机串
    [InlineData("BFV.CombinedCrops", "Stardew Valley Expanded 1.15.11 Russian", "jhmngbfvdcsaxdcfvgbh", false)]
    // 代价：这条页面号其实是对的，但没有任何证据能证明它对 —— 认不出就不指路
    [InlineData("Nom0ri.RomRas", "Romanceable Rasmodia - Female Wizard", "Nom0ri", false)]
    // 两边都对上 → 放行
    [InlineData("PeacefulEnd.FashionSense", "Fashion Sense", "PeacefulEnd", true)]
    // 多段 UID 的真前置：页面名对上点号首段（第四道来源就是靠这条认出的）
    [InlineData("PeacefulEnd.Parchment.Core", "Parchment", "PeacefulEnd", true)]
    [InlineData("DLX.PIF", "PIF - Personal Rooms", "DeLiXxN", true)]
    [InlineData("Sharogg.Tilesheets", "Sharogg's Tilesheets (indoor and outdoor structures)", "Sharogg", true)]
    public void SearchHitIdentifies_Levels(string uid, string name, string uploader, bool expected)
        => Assert.Equal(expected, InstallService.SearchHitIdentifies(uid, name, uploader));
}

/// <summary>配置持久缓存的可信闸：判据收紧之前存下的错答案，靠读取处重跑这道闸来自愈。
/// 下面四条 (name/modId/uploader/source) 是从真机
/// <c>%AppData%\JuniGrid\junigrid.config.json → dependencyDisplays</c> 里逐字抄的。</summary>
public class DependencyDisplayTrustTests
{
    private static DependencyDisplayEntry Entry(string name, int modId, string uploader, string source)
        => new() { Name = name, ModId = modId, Uploader = uploader, Source = source };

    [Theory]
    // 真机那条错答案：同作者的另一个 mod，重解析后仍认不出 → 必须判不可信（进而被 DropStaleDisplay 删掉）
    [InlineData("PeacefulEnd.Parchment.Core", "Fashion Sense", 9969, "PeacefulEnd", "", false)]
    // 同一条数据换成它自己的 UID：对得上，不许连坐
    [InlineData("PeacefulEnd.FashionSense", "Fashion Sense", 9969, "PeacefulEnd", "", true)]
    // 父包 Requirements 表给的：作者自己登记的映射，没有上传者可比，靠来源字段放行
    [InlineData("Wildflour.AtelierGoods", "Wildflour's Atelier Goods", 0, "", "requirements", true)]
    // 老配置没 uploader 字段＝空串＝不可信（v1.2.4 之前存的那批）
    [InlineData("DLX.PIF", "PIF - Personal Rooms", 20537, "", "", false)]
    public void SavedDisplay_Levels(string uid, string name, int modId, string uploader, string source, bool expected)
        => Assert.Equal(expected, InstallService.SavedDisplayStillTrustworthy(
               uid, Entry(name, modId, uploader, source)));
}

// ------------------------------------------------- SponsorPrompt

/// <summary>赞助提示的触发口径：里程碑（10/100/200/500，之后每 1000）必弹；
/// 没有里程碑时每 7 天兜底一次。里程碑与 7 天都从「上次弹窗时间」重新计时。</summary>
public class SponsorPromptTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Local);

    [Theory]
    [InlineData(9, false)]        // 不到 10 次不弹
    [InlineData(10, true)]        // 第一个里程碑
    [InlineData(11, false)]       // 刚弹过、又没到下一个里程碑
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(1500, false)]
    [InlineData(2000, true)]
    [InlineData(3000, true)]
    public void ShouldPrompt_OnlyOnMilestones(int count, bool expected)
        => Assert.Equal(expected,
            SponsorPrompt.ShouldPrompt(count, Now.AddDays(-1), lastMilestone: 0, Now, disabled: false));

    [Theory]
    [InlineData(0, false)]        // 同一档已经提示过，昨天刚弹 → 不弹
    [InlineData(6, false)]
    [InlineData(7, true)]         // 满 7 天兜底
    [InlineData(30, true)]
    public void ShouldPrompt_FallsBackToSevenDays(int daysSinceLast, bool expected)
    {
        // 250 次不是里程碑；上次额度已提示到 200
        var last = Now.AddDays(-daysSinceLast);
        Assert.Equal(expected,
            SponsorPrompt.ShouldPrompt(250, last, lastMilestone: 200, Now, disabled: false));
    }

    [Fact]
    public void ShouldPrompt_FirstEverLaunchAfterUpdate()
    {
        // 老配置没有时间/里程碑字段：次数过了 10 就补一次
        Assert.True(SponsorPrompt.ShouldPrompt(747, null, 0, Now, disabled: false));
        Assert.False(SponsorPrompt.ShouldPrompt(9, null, 0, Now, disabled: false));
    }

    [Fact]
    public void ShouldPrompt_NewUserUnderTenNeverPrompts()
    {
        // 新用户不足 10 次一律不弹 —— 即使 7 天兜底的时钟早就走完、即使记账里从没弹过。
        // 到第 10 次那一刻才是第一次弹（第一个里程碑）。
        Assert.False(SponsorPrompt.ShouldPrompt(9, null, 0, Now, disabled: false));
        Assert.False(SponsorPrompt.ShouldPrompt(5, Now.AddDays(-30), 0, Now, disabled: false));
        Assert.False(SponsorPrompt.ShouldPrompt(1, Now.AddDays(-365), 0, Now, disabled: false));
        Assert.False(SponsorPrompt.ShouldPrompt(9, Now.AddDays(-30), 0, Now, disabled: false));
        // 到 10 次那一刻才弹
        Assert.True(SponsorPrompt.ShouldPrompt(10, null, 0, Now, disabled: false));
    }

    [Fact]
    public void ShouldPrompt_SameMilestoneDoesNotRepeat()
    {
        // 第 100 次已经弹过（lastMilestone=100），重启后次数仍是 100，不该再弹
        Assert.False(SponsorPrompt.ShouldPrompt(100, Now.AddMinutes(-5), 100, Now, disabled: false));
        // 但满 7 天后仍然按兜底弹
        Assert.True(SponsorPrompt.ShouldPrompt(100, Now.AddDays(-8), 100, Now, disabled: false));
    }

    [Fact]
    public void ShouldPrompt_LegacyDonatedFlagStillSilencesBeforeMigration()
    {
        // 老数据里的永久静默标记在【迁移前那一瞬】仍然算数（迁移会把它转成「已支持」，
        // 见 SponsorPromptStateTests）。这里只钉住判定函数本身不会无视它。
        Assert.False(SponsorPrompt.ShouldPrompt(2000, null, 0, Now, disabled: true));
        Assert.False(SponsorPrompt.ShouldPrompt(2000, Now.AddDays(-30), 1000, Now, disabled: true));
    }

    [Fact]
    public void ShouldPrompt_SupportedHasNoSpecialCadence()
    {
        // 「已支持」不再降频：支持过的人同样 7 天一次（曾经是 30 天）。
        // 这个条件已经不参与判定，SupportedAt 只作为"谁支持过"的账本记录保留。
        Assert.False(SponsorPrompt.ShouldPrompt(1500, Now.AddDays(-6), 1000, Now, false));
        Assert.True(SponsorPrompt.ShouldPrompt(1500, Now.AddDays(-7), 1000, Now, false));
    }

    [Fact]
    public void ShouldPrompt_MilestoneWinsOverSevenDayQuiet()
    {
        // 里程碑不受 7 天限制：第 1000 次即便昨天刚弹过，只要这一档没提示过就要弹。
        Assert.True(SponsorPrompt.ShouldPrompt(1000, Now.AddDays(-1), 500, Now, false));
        // 同一档已经提示过则不重复，回到 7 天节奏
        Assert.False(SponsorPrompt.ShouldPrompt(1000, Now.AddDays(-1), 1000, Now, false));
    }

    [Fact]
    public void ShouldPrompt_ClockRollbackDoesNotPrompt()
    {
        Assert.False(SponsorPrompt.ShouldPrompt(250, Now.AddDays(1), 200, Now, disabled: false));
    }

    [Fact]
    public void Constants_PinTheAgreedCadence()
    {
        Assert.Equal(10, SponsorPrompt.FirstPromptLaunchCount);
        Assert.Equal(7, SponsorPrompt.IntervalDays);
        Assert.True(SponsorPrompt.IsMilestone(10));
        Assert.True(SponsorPrompt.IsMilestone(100));
        Assert.True(SponsorPrompt.IsMilestone(200));
        Assert.True(SponsorPrompt.IsMilestone(500));
        Assert.True(SponsorPrompt.IsMilestone(1000));
        Assert.True(SponsorPrompt.IsMilestone(9000));
        Assert.False(SponsorPrompt.IsMilestone(999));
        Assert.False(SponsorPrompt.IsMilestone(1001));
        Assert.False(SponsorPrompt.IsMilestone(1500));
    }

    /// <summary>把产品口径逐条钉住（用户原话）：
    /// ① 新用户 ≤10 次不触发（第 10 次本身就是里程碑）；
    /// ② 老用户 ≥10 次，到里程碑触发一次；同一档不重复；
    /// ③ 7 天内没碰到里程碑，就按 7 天兜底弹一次；
    /// ④ 「已支持」不改变以上任何一条。</summary>
    [Fact]
    public void ShouldPrompt_MatchesTheAgreedRuleMatrix()
    {
        // ① 不足 10 次：记账不管写成什么样都不弹
        Assert.False(SponsorPrompt.ShouldPrompt(0, null, 0, Now, false));
        Assert.False(SponsorPrompt.ShouldPrompt(9, Now.AddDays(-999), 0, Now, false));

        // ② 里程碑到点必弹 —— 哪怕一小时前刚弹过（不受 7 天限制）
        foreach (var n in new[] { 10, 100, 200, 500, 1000, 2000, 3000 })
            Assert.True(SponsorPrompt.ShouldPrompt(n, Now.AddHours(-1), n - 1, Now, false),
                $"第 {n} 次是里程碑，应弹");

        // ②-补 同一档已经提示过：不重复，回到 7 天节奏
        Assert.False(SponsorPrompt.ShouldPrompt(10, Now.AddDays(-1), 10, Now, false));
        Assert.False(SponsorPrompt.ShouldPrompt(1000, Now.AddDays(-1), 1000, Now, false));

        // ③ 非里程碑：不满 7 天安静，满 7 天弹
        Assert.False(SponsorPrompt.ShouldPrompt(11, Now.AddDays(-6), 10, Now, false));
        Assert.True(SponsorPrompt.ShouldPrompt(11, Now.AddDays(-7), 10, Now, false));
        Assert.False(SponsorPrompt.ShouldPrompt(1500, Now.AddDays(-6), 1000, Now, false));
        Assert.True(SponsorPrompt.ShouldPrompt(1500, Now.AddDays(-8), 1000, Now, false));

        // ④ 「已支持」不影响频率：ShouldPrompt 已经不接收 supportedAt，
        //    记账里有 SupportedAt 也只是账本 —— 这里靠签名保证，无分支可测。
    }
}

// ------------------------------------------------- SponsorPromptState
/// <summary>赞助记账的独立文件：读回、只迁移一次、损坏不炸。</summary>
public class SponsorPromptStateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "jg-sponsor-state-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public SponsorPromptStateTests() => SponsorPromptState.FilePathOverride = _path;

    public void Dispose()
    {
        SponsorPromptState.FilePathOverride = null;
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var t = new DateTime(2026, 11, 3, 9, 38, 40, DateTimeKind.Local);
        SponsorPromptState.Save(new SponsorPromptState.State
        {
            LastPromptedAt = t,
            LastMilestone = 100,
            Disabled = false,
            Migrated = true
        });

        var loaded = SponsorPromptState.Load();
        Assert.Equal(t, loaded.LastPromptedAt);
        Assert.Equal(100, loaded.LastMilestone);
        Assert.True(loaded.Migrated);
    }

    [Fact]
    public void LoadOrMigrate_MovesLegacyConfigOver_ExactlyOnce()
    {
        var legacy = new JuniGridConfig
        {
            LastSponsorPromptAt = new DateTime(2026, 11, 2, 19, 41, 0, DateTimeKind.Local),
            LastSponsorMilestone = 10,
            SponsorPromptDisabled = false
        };

        var first = SponsorPromptState.LoadOrMigrate(legacy);
        Assert.Equal(legacy.LastSponsorPromptAt, first.LastPromptedAt);
        Assert.Equal(10, first.LastMilestone);
        Assert.True(first.Migrated);

        // 主配置后来被别的 JuniGrid 抹成 null（今天这个 bug 的现场）——
        // 第二次读【不许】再把 null 搬回来，否则弹窗又被重新武装。
        var clobbered = new JuniGridConfig { LastSponsorPromptAt = null, LastSponsorMilestone = 0 };
        var second = SponsorPromptState.LoadOrMigrate(clobbered);
        Assert.Equal(legacy.LastSponsorPromptAt, second.LastPromptedAt);
        Assert.Equal(10, second.LastMilestone);
    }

    [Fact]
    public void Load_CorruptFileFallsBackToEmptyState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ 这不是 json");
        var s = SponsorPromptState.Load();
        Assert.Null(s.LastPromptedAt);
        Assert.Equal(0, s.LastMilestone);
        Assert.False(s.Disabled);
    }

    [Fact]
    public void LoadOrMigrate_LegacyDonatedFlagBecomesSupported_NotPermanentSilence()
    {
        // 老版本点过「已捐赠」的用户（很可能只是误点，根本没付钱）：升级后不再永久闭嘴，
        // 转成「已支持」记录下来；频率与其他人完全一致 —— 7 天一次，里程碑照常。
        var state = SponsorPromptState.LoadOrMigrate(new JuniGridConfig { SponsorPromptDisabled = true });
        Assert.False(state.Disabled);
        Assert.NotNull(state.SupportedAt);

        var now = DateTime.Now;
        // 从没提示过 → 更新后第一次打开就弹一次（老用户次数早就过了 10）
        Assert.True(SponsorPrompt.ShouldPrompt(752, null, 500, now, state.Disabled));
        // 弹过之后 7 天内安静
        Assert.False(SponsorPrompt.ShouldPrompt(752, now.AddDays(-2), 500, now, state.Disabled));
        // 满 7 天照常回来
        Assert.True(SponsorPrompt.ShouldPrompt(752, now.AddDays(-8), 500, now, state.Disabled));
    }

    [Fact]
    public void LoadOrMigrate_ClearsDonatedFlagEvenWhenAlreadyMigrated()
    {
        // 在跑 v1.2.12 的用户文件里 Migrated 早就是 true —— 转换不能藏在迁移分支里，
        // 否则这批人永远解不开永久静默。
        SponsorPromptState.Save(new SponsorPromptState.State { Migrated = true, Disabled = true });
        var state = SponsorPromptState.LoadOrMigrate(new JuniGridConfig());
        Assert.False(state.Disabled);
        Assert.NotNull(state.SupportedAt);

        var reread = SponsorPromptState.Load();
        Assert.False(reread.Disabled);
        Assert.NotNull(reread.SupportedAt);
    }

    [Fact]
    public void LoadOrMigrate_KeepsSupportedAtOnReread()
    {
        // 幂等：第二次读不许把 SupportedAt 重新推到「现在」，否则这笔账会被反复改写。
        var first = SponsorPromptState.LoadOrMigrate(new JuniGridConfig { SponsorPromptDisabled = true });
        var second = SponsorPromptState.LoadOrMigrate(new JuniGridConfig());
        Assert.Equal(first.SupportedAt, second.SupportedAt);
    }
}
