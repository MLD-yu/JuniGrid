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
    [InlineData("Wildflour.Cozy", "Wildflourmods", 2)] // 一方包含另一方
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
    public void NameMatchScore_Levels(string uid, string name, int expected)
        => Assert.Equal(expected, InstallService.NameMatchScore(uid, name));
}
