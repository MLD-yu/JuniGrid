using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 覆盖宿主"本机装没装"的判定：<b>登记行带页面号时只比页面号，名字只在没填号时才用</b>。
/// <para>实机 2026-10-13 的 50261「Parchment - Fishing Log Chinese Translation」被拒成
/// 「这个包要盖在「Parchment - Fishing Log」（N 网 #50188）上，但你还没装它」—— 而宿主就装着：
/// 三个字符串互不相等（前置行「Parchment - Fishing Log」／宿主 manifest 的 Name
/// 「[Parchment] Fishing Logbook」／目录名 FishingLogbook），旧判据只比名字的拼写键，
/// 一个都不命中。而 50188 这个数字两边都带着：前置行的 modId ＋ 宿主自己 UpdateKeys 的 "Nexus:50188"。</para>
/// <para>名字是作者手填的自由文本，页面号是硬标识 —— 与
/// <see cref="ManifestIdentityBoundaryTests"/> 那条"有 manifest 只认 UniqueID"是同一条原则。</para>
/// </summary>
public class DeclaredHostIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-dhi-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;

    public DeclaredHostIdentityTests()
    {
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(_game, "Mods"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>摆一个已装宿主：目录名 / manifest 的 Name / UpdateKeys 三样分开给，
    /// 因为真机上它们本来就不是一回事。</summary>
    private void Install(string folder, string manifestName, string uniqueId, string? nexusUpdateKey,
        string[]? i18nKeys = null)
    {
        var dir = Path.Combine(_game, "Mods", folder);
        Directory.CreateDirectory(Path.Combine(dir, "i18n"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{ \"Name\": \"" + manifestName + "\", \"Author\": \"a\", \"Version\": \"1.0\", \""
            + "UniqueID\": \"" + uniqueId + "\", \"Description\": \"x\", \"EntryDll\": \"\""
            + (nexusUpdateKey is null ? "" : ", \"UpdateKeys\": [ \"" + nexusUpdateKey + "\" ]") + " }");
        File.WriteAllText(Path.Combine(dir, "i18n", "default.json"), Json(i18nKeys ?? new[] { "k" }));
    }

    private static string Json(IEnumerable<string> keys)
        => "{" + string.Join(",", keys.Select(k => "\"" + k + "\":\"x\"")) + "}";

    private static NexusRequirement Row(string name, int modId)
        => new(name, "", "", modId, false);

    private (int Count, string? One) Match(params NexusRequirement[] rows)
        => new ModService().MatchDeclaredHosts(_game, rows);

    /// <summary>真机 50261 那一例的端到端形状：整包一个裸 zh.json，宿主自述页面号与前置行一致，
    /// 而三个名字互不相等。跑**生产的** <see cref="ModService.ProbeOverlay"/> ——
    /// 修复前它报 HostNotInstalled（"你还没装它"），修复后必须给出盖进宿主 i18n/ 的计划。</summary>
    [Fact]
    public void EndToEnd_Real50261Shape_LandsInsideTheInstalledHost()
    {
        var realKeys = new[] { "item.fishingLog.name", "item.fishingLog.description",
            "config.logbookShortcut.name", "ui.cover.title", "ui.search.placeholder", "ui.tooltip.goToStats" };
        Install("FishingLogbook", "[Parchment] Fishing Logbook",
            "PeacefulEnd.Parchment.FishingLogbook", "Nexus:50188", realKeys);
        var pack = Path.Combine(_root, "pack" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "zh.json"), Json(realKeys));

        var plan = new ModService().ProbeOverlay(_game, pack, out var fail, out var guess,
            new[] { Row("Parchment - Fishing Log", 50188) });

        Assert.Equal(ModService.OverlayFail.None, fail);
        var only = Assert.Single(plan);
        Assert.Equal("FishingLogbook", only.Host);
        Assert.Contains(only.DstPaths, d => d.Replace('\\', '/') == "i18n/zh.json");
    }

    [Fact]
    public void RealCase_PageIdMatches_EvenThoughAllThreeNamesDiffer()
    {
        Install("FishingLogbook", "[Parchment] Fishing Logbook",
            "PeacefulEnd.Parchment.FishingLogbook", "Nexus:50188");
        var got = Match(Row("Parchment - Fishing Log", 50188));      // 50261 页面登记的那一行（真值）
        Assert.Equal(1, got.Count);
        Assert.Equal("FishingLogbook", got.One);
    }

    [Fact]
    public void NameMatchesExactly_ButHostDeclaresADifferentPageId_InstallsNothing()
    {
        // 号对不上、名字逐字相等 ⇒ 同名不同家，不许盖（实测踩过：同名不同 UID 曾把已装 mod
        // 整个扫进回收站）。这条就是那扇门，别让人把它开回去。
        Install("FishingLogbook", "[Parchment] Fishing Logbook",
            "PeacefulEnd.Parchment.FishingLogbook", "Nexus:50188");
        var got = Match(Row("[Parchment] Fishing Logbook", 999999));   // 名字逐字相等，号不是它
        Assert.Equal(0, got.Count);
        Assert.Null(got.One);
    }

    [Fact]
    public void HostHasNoPageIdOfItsOwn_NameStillCounts()
    {
        // 手工装的宿主常常没写 UpdateKeys（实机 32901 / 41314 那批裸翻译包的宿主就是这形状）。
        // 它没有自述可违，名字是仅剩的证据 —— 一律拒会把这批汉化永远关在门外。
        Install("Better Game Menu", "Better Game Menu", "some.bgm", null);
        var got = Match(Row("Better Game Menu", 32032));
        Assert.Equal(1, got.Count);
        Assert.Equal("Better Game Menu", got.One);
    }

    [Fact]
    public void RowWithoutPageId_StillFallsBackToName()
    {
        // 实测 18080：作者登记了名字、modId 填的是 "0"。这种行只能按名字认。
        Install("Crop Harvest Bubbles", "Crop Harvest Bubbles", "someone.cgb", null);
        var got = Match(Row("Crop Harvest Bubbles", 0));
        Assert.Equal(1, got.Count);
        Assert.Equal("Crop Harvest Bubbles", got.One);
    }

    [Fact]
    public void TwoInstalledHostsShareThePageId_IsTieAndRefuses()
    {
        // 并列一律不给"其中一个"（下游会直接往那个目录写字）。
        Install("BundleA/Main", "Main", "some.main", "Nexus:50188");
        Install("BundleB/Main", "Main", "other.main", "Nexus:50188");
        var got = Match(Row("Parchment - Fishing Log", 50188));
        Assert.Equal(2, got.Count);
        Assert.Null(got.One);
    }

    [Fact]
    public void LoaderRow_IsNeverAHost_EvenWithAPageId()
    {
        // SMAPI 不住在 Mods\ 里；剔掉它是"宿主只有一个"能成立的前提（实测 24232 两行：#20781 与 SMAPI）。
        Install("FishingLogbook", "[Parchment] Fishing Logbook",
            "PeacefulEnd.Parchment.FishingLogbook", "Nexus:50188");
        var got = Match(Row("SMAPI - Stardew Modding API", 240), Row("Parchment - Fishing Log", 50188));
        Assert.Equal(1, got.Count);
        Assert.Equal("FishingLogbook", got.One);
    }
}
