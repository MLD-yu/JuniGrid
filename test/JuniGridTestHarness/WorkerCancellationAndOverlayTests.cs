using System.Reflection;
using System.Text.Json;
using JuniGrid.Services;

namespace JuniGridTestHarness;

// ══════════════════════════════════════════════════════════════════════════
// 2026-10-07 从 junigrid-blazor 那套大套件里的 U43a–f / U44 / U44b / U45a–h 照搬过来的 16 条。
// 钉的都是当天在他机器上实机跑出来的三件事：
//   ① 暂停停不住 worker ⇒ 一行任务把自己堵死 39 秒，那个僵尸还把新一跑正要装的包删了；
//   ② Premium 出口只有详情页那一个调用方会打开网页 ⇒ 任务面板点「↻ 重试」时界面写着
//      「已改为网页方式」而浏览器一次都没弹（mod 20581）；
//   ③ 覆盖通道把「包自己的标题」当成没装的前置报出去（mod 1993 的文件 35407）。
// 被测成员在两棵树里都从 private 提到 internal（沿用本仓库存例：ModService.ChooseFolderName
// 就是这么暴露给测试的），只有 TaskCenterService 的两个 Timer 字段仍走反射 —— 见下面注释。
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 等安装锁必须带 CancellationToken。旧写法用的是不带 token 的 WaitAsync()：暂停那一跑永远
/// 排在门外占着队，于是「前面还有 N 个安装」里有一多半是自己上一跑的僵尸（实机 09:22:07 那 39 秒）。
/// </summary>
public class InstallGateCancellationTests
{
    [Fact]
    public async Task PairedRun_QuitsWhileWaitingForGate_AndFreesTheQueue()
    {
        var msgs = new List<string>();
        Action<string, double?, double?> Step = (m, pct, spd) => msgs.Add(m);
        int acquired = 0, released = 0;
        try
        {
            // ① 前置：开跑时这把全局锁必须是空的，否则下面全是空断言
            var holderCts = new CancellationTokenSource();
            var holder = InstallService.EnterInstallGateAsync(Step, 95, holderCts.Token);
            Assert.True(await Task.WhenAny(holder, Task.Delay(3000)) == holder,
                "开跑时拿不到安装锁 ⇒ 这把 static 锁被别的东西占着，下面的断言全是空的");
            acquired++;
            Assert.Empty(msgs);                                   // 空锁不该报「排队等待写入」

            // ② 僵尸：排在门外，然后用户点暂停
            var zombieCts = new CancellationTokenSource();
            var zombie = InstallService.EnterInstallGateAsync(Step, 95, zombieCts.Token);
            zombieCts.Cancel();
            Assert.True(await Task.WhenAny(zombie, Task.Delay(3000)) == zombie,
                "3 秒过去还没退出 ⇒ 等锁又用回不带 token 的 WaitAsync() 了（变异点）");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => zombie);

            // ③ 僵尸走了就不该再占队：下一跑压根不该看到「前面还有 N 个安装」那行
            var liveCts = new CancellationTokenSource();
            var live = InstallService.EnterInstallGateAsync(Step, 95, liveCts.Token);
            Assert.False(string.Join(" ｜ ", msgs).Contains("前面还有", StringComparison.Ordinal),
                "僵尸退出后下一跑仍被报成在排队 ⇒ 它没真走（变异点：等锁不带 token）");

            // ④ 持锁者交还后，门外那一跑立刻进来（Release 没漏、也没被异常弹回来）
            InstallService.ExitInstallGate(); released++;
            Assert.True(await Task.WhenAny(live, Task.Delay(3000)) == live,
                "交还锁之后门外那一跑 3 秒内没拿到 ⇒ 唤醒链断了");
            Assert.True(live.IsCompletedSuccessfully,
                "门外那一跑被异常弹回来了：" + live.Exception?.GetBaseException().Message);
            acquired++;
        }
        finally
        {
            while (released < acquired) { InstallService.ExitInstallGate(); released++; }
        }
    }
}

/// <summary>
/// 「继续」的实现是换一个新的 CTS + 把 worker 闭包从头重挂，所以旧那一跑是被取代的僵尸。
/// 僵尸一律不许再碰共享的东西：半截 zip 是新一跑正要拿去装的文件（实机 09:22:46 它按行级
/// task.Status 判"用户取消了"，把包删了，真错误「这个包要盖在…上」被假错误「Could not find file」盖掉），
/// 行状态是新一跑正在写的字段。
/// </summary>
public class SupersededRunTests
{
    /// <summary>
    /// 用真实的 Pause/ResumeMark（判据要钉的就是这两个方法真的换了 CTS）。
    /// ⚠ 先把这个实例的防抖落盘定时器换成空转的：TaskCenterService 写的是
    ///   StoragePaths.AppDataDir\tasks.json —— 那是 %APPDATA%，TestSandbox 设的
    ///   JUNIGRID_CONFIG_DIR 管不到它，不掐就是往玩家真任务表里写玩具行。
    /// </summary>
    private static TaskCenterService NewCenterThatNeverWritesDisk()
    {
        var c = new TaskCenterService();
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(TaskCenterService).GetField("_saveTimer", F)?.SetValue(c,
            new Timer(_ => { }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite));
        (typeof(TaskCenterService).GetField("_reapTimer", F)?.GetValue(c) as Timer)?.Dispose();
        return c;
    }

    [Fact]
    public void ResumeMark_SwapsTheCts_PausedAloneDoesNot()
    {
        var center = NewCenterThatNeverWritesDisk();
        var row = center.Start("U43d 僵尸判据", "install");
        var run1 = row.Cts.Token;
        Assert.False(InstallService.IsStaleRun(row, run1));       // 刚起：当前跑

        center.Pause(row);                                        // 点暂停：Cancel + 行状态，CTS 不换
        Assert.False(InstallService.IsStaleRun(row, run1));       // 暂停 ≠ 被取代（包要留给续传）
        center.ResumeMark(row);                                   // 点继续：ResetCts 换新的
        Assert.True(InstallService.IsStaleRun(row, run1),
            "「继续」没有换 CTS ⇒ 僵尸判据恒为 False，下面两条白测");
        Assert.NotEqual(run1, row.Cts.Token);
        Assert.False(InstallService.IsStaleRun(row, row.Cts.Token));   // 新一跑自己不算僵尸
        center.Remove(row);
    }

    [Fact]
    public void HalfPackGate_AllowsOnlyTheCurrentRunningRun()
    {
        var center = NewCenterThatNeverWritesDisk();
        var row = center.Start("U43e 删包闸三态", "install");
        var run1 = row.Cts.Token;
        Assert.True(InstallService.MayDiscardHalfPack(row, run1), "跑着的那一跑该能清半截包");
        center.Pause(row);
        Assert.False(InstallService.MayDiscardHalfPack(row, run1), "暂停中该留着包给续传");
        center.ResumeMark(row);                                   // 行状态被改回 running，run1 成僵尸
        Assert.False(InstallService.MayDiscardHalfPack(row, run1),
            "被取代的旧跑照旧判成「可删」⇒ 就是实机那条假错误「Could not find file」的来路（变异点）");
        Assert.True(InstallService.MayDiscardHalfPack(row, row.Cts.Token), "当前跑该照常可以");
        center.Remove(row);
    }

    [Fact]
    public void FinishIfTimeout_SupersededRunWritesNothing()
    {
        var center = NewCenterThatNeverWritesDisk();
        var row = center.Start("U43f 僵尸落终态", "install");
        var run1 = row.Cts.Token;
        center.Pause(row);
        center.ResumeMark(row);                       // 行是 running，run1 成僵尸
        var svc = new InstallService(null!, null!, null!, center, null!, null!, null!);

        Assert.Null(svc.FinishIfTimeout(row, new TimeoutException("模拟超时"), run1));
        Assert.Equal("running", row.Status);          // 僵尸没写成 failed

        // 正对照：当前跑真的超时【必须】落 failed，否则这道闸等于无条件 return null
        Assert.NotNull(svc.FinishIfTimeout(row, new TimeoutException("模拟超时"), row.Cts.Token));
        Assert.Equal("failed", row.Status);
        center.Remove(row);
    }
}

/// <summary>免费账号撞 Premium 的统一出口。旧写法把「打开网页」挂在调用方，而任务面板的
/// 「↻ 重试」走 Center.Retry ⇒ worker 返回值被丢掉，没人开页面。</summary>
public class PremiumExitTests
{
    [Fact]
    public void PremiumExit_HandsOverTheFilesPageUrl()
    {
        string? opened = null;
        InstallService.PremiumNeedsWebFallback(20581, u => opened = u);
        Assert.Equal("https://www.nexusmods.com/stardewvalley/mods/20581?tab=files", opened);
    }

    [Fact]
    public void PremiumExit_StillCarriesThePremiumMarker()
    {
        // ModDetail 的分流判据就是 err.Contains("premium") —— 动这句字面量的前缀，
        // 详情页那条路会静默降级成普通失败（不弹提示、也不开页面）。
        var msg = InstallService.PremiumNeedsWebFallback(20581, _ => { });
        Assert.True(msg.Contains("premium", StringComparison.OrdinalIgnoreCase), "返回=" + msg);
    }
}

/// <summary>覆盖通道的宿主判定：只采信包自己的内部路径，且不许把「包自己的标题」当成没装的前置。
/// 实机形状 = mod 1993 的文件 35407（256 个 PNG，无 manifest.json）：
/// Mod Compatibility Textures/Deep Woods Obelisk/…(196) + /Tractor Garage/…(60)，
/// 顶层是包的标题、真宿主在第二层，两个都没装 ⇒ 旧写法凭空捏一个「前置」。</summary>
public class OverlayHostJudgementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-ovh-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly ModService _ms = new();

    public OverlayHostJudgementTests()
    {
        _game = Path.Combine(_root, "game");
        // 已装的样子：Mods/HostOne（普通宿主）· Mods/Bundle/Child（捆绑包里的子包，带 manifest）
        Directory.CreateDirectory(Path.Combine(_game, "Mods", "HostOne"));
        var child = Path.Combine(_game, "Mods", "Bundle", "Child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "manifest.json"),
            @"{ ""Name"": ""Child"", ""Author"": ""a"", ""Version"": ""1.0"", ""UniqueID"": ""a.Child"", ""Description"": ""x"", ""GameVersion"": ""1.6.0"", ""EntryDll"": """" }");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>按给定的包内路径摆一个解包目录，跑一遍宿主判定。</summary>
    private (string Fail, string Guess, string Host) Probe(params string[] relPaths)
    {
        var d = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N"));
        foreach (var rp in relPaths)
        {
            var fp = Path.Combine(d, rp.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
            File.WriteAllText(fp, "x");
        }
        var plan = _ms.ProbeOverlay(_game, d, out var fail, out var guess);
        return (fail.ToString(), guess, plan?.Host ?? "");
    }

    private string Refuse(ModService.OverlayFail fail, string guess)
        => ModService.OverlayRefusal(fail, _game, guess);

    [Fact]
    public void RealShape_PackTitleOnTop_TwoModsBelow_ReportsMultiHostNamingBoth()
    {
        var r = Probe("Mod Compatibility Textures/Deep Woods Obelisk/a.png",
                      "Mod Compatibility Textures/Tractor Garage/b.png");
        Assert.Equal("MultiHost", r.Fail);
        Assert.Equal("Deep Woods Obelisk、Tractor Garage", r.Guess);
    }

    [Fact]
    public void InstalledTopLevelDir_StillResolvesAsHost()
    {
        var r = Probe("HostOne/i18n/zh.json");
        Assert.Equal("None", r.Fail);
        Assert.Equal("HostOne", r.Host);
    }

    [Fact]
    public void SingleHostNotInstalled_StillReportsHostNotInstalled()
    {
        // 变异点：条件写成 subs.Count>=1 就会把这条一起吞进 MultiHost
        var r = Probe("NotInstalledMod/i18n/zh.json");
        Assert.Equal("HostNotInstalled", r.Fail);
        Assert.Equal("NotInstalledMod", r.Guess);
    }

    [Fact]
    public void BundledSubpackage_StillResolves_OneLevelDeeper()
    {
        // 这条把原来的 sub 变量换成了 subs[0]，换错就断
        var r = Probe("BundleTranslation/Child/i18n/zh.json");
        Assert.Equal("None", r.Fail);
        Assert.Equal("Bundle/Child", r.Host);
    }

    [Fact]
    public void TwoTopLevelDirs_ReportedByName()
    {
        var r = Probe("Alpha/x.png", "Beta/y.png");
        Assert.Equal("MultiHost", r.Fail);
        Assert.Equal("Alpha、Beta", r.Guess);
    }

    [Fact]
    public void TooManyCandidates_NamesThreeAndTheTotal()
    {
        var r = Probe("Pack/A/x.png", "Pack/B/x.png", "Pack/C/x.png", "Pack/D/x.png", "Pack/E/x.png");
        Assert.Equal("A、B、C 等 5 个", r.Guess);   // 提示卡不许变成一堵字
    }

    [Fact]
    public void Wording_NoLongerAssumesATranslation_PairedWithTheCatalog()
    {
        // 这条通道不挑内容（汉化、贴图、兼容包都走它），说"翻译"会让用户以为只有汉化包才走这里。
        // 两边都要查：中文原文（=目录的键）与英文条目（=值）。运行时语言由配置决定，
        // 只查 Tr 的输出会在英文模式下假绿。
        Assert.DoesNotContain("翻译", Refuse(ModService.OverlayFail.HostNotInstalled, "X"));
        Assert.DoesNotContain("翻译", Refuse(ModService.OverlayFail.MultiHost, "X、Y"));

        var asm = typeof(LocService).Assembly;
        var rn = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("en.json", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rn);
        using var rs = asm.GetManifestResourceStream(rn!);
        Assert.NotNull(rs);
        using var cat = JsonDocument.Parse(rs!);
        var hits = cat.RootElement.EnumerateObject()
            .Where(p => p.Name.Contains("这个包要盖在") || p.Name.Contains("放着好几个")).ToList();
        Assert.Equal(2, hits.Count);                            // 两条新键都进了目录
        foreach (var p in hits)
            Assert.False(p.Value.GetString()?.Contains("translated") == true,
                "英文值里还有 translated：" + p.Name);
    }

    [Fact]
    public void MultiHostSentence_CarriesTheCandidateNames()
    {
        var s = Refuse(ModService.OverlayFail.MultiHost, "Deep Woods Obelisk、Tractor Garage");
        Assert.Contains("Deep Woods Obelisk", s);               // 点名靠 hostGuess，不是写死一句泛泛的话
    }

    // ── 配置项汉化包：靠【两边各自声明的内容】认宿主（实机 mod 43130，2026-10-07）──
    // 整包只有 `[CP] Elle's Seasonal Buildings-CN/i18n/zh.json`，而宿主目录叫
    // `[CP] Elle's Seasonal Buildings`（多个 -CN）⇒ 拼写键认不下，旧写法报「你还没装它」。
    // 判据不许截名字：同作者姊妹包（Spring 界面 / Fall 界面）会被截法打脸，
    // 所以字段名对不上就拒、对得上【两家】也拒 —— 只有一家命中才落盘。
    private void ModWith(string folder, string file, string json)
    {
        var d = Path.Combine(_game, "Mods", folder);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, file), json);
    }
    private static string ZhJson(params string[] fields) =>
        "{" + string.Join(",", fields.Select(f => "\"config." + f + ".name\": \"译\"")) + "}";
    private (string Fail, string Host) ProbeJson(string packName, params string[] fields)
    {
        var d = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N"));
        var fp = Path.Combine(d, packName, "i18n");
        Directory.CreateDirectory(fp);
        File.WriteAllText(Path.Combine(fp, "zh.json"), ZhJson(fields));
        var plan = _ms.ProbeOverlay(_game, d, out var fail, out var guess);
        return (fail.ToString(), plan is null ? guess : plan.Host);
    }

    /// <summary>多宿主那句的措辞：不许说「判断不出该盖谁」—— 名字点得出来，
    /// 真实原因是【该替你盖哪家只有你能决定】（实机 1993：一家下面摆着 10 套变体）。</summary>
    [Fact]
    public void MultiHostSentence_SaysWeWontDecide_NotThatItCannotTell()
    {
        var zh = Refuse(ModService.OverlayFail.MultiHost, "Deep Woods Obelisk、Tractor Garage");
        Assert.DoesNotContain("判断不出", zh);
        Assert.Contains("替你决定不了", zh);
        var asm = typeof(LocService).Assembly;
        var rn = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("en.json", StringComparison.OrdinalIgnoreCase));
        using var rs = asm.GetManifestResourceStream(rn!)!;
        using var cat = JsonDocument.Parse(rs!);
        var en = cat.RootElement.EnumerateObject()
            .Where(p => p.Name.StartsWith("这个包里放着好几个别的 mod 的目录"))
            .Select(p => p.Value.GetString()).FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(en), "英文目录里查不到这条新键");
        Assert.DoesNotContain("ambiguous", en!);
        Assert.Contains("can't decide", en!);
    }

    [Fact]
    public void ConfigTranslation_HostDeclaredInContentJson_IsFound_WithoutTouchingNames()
    {
        ModWith("HostCpJson", "content.json",
            @"{ ""Format"":""2.7.0"", ""ConfigSchema"": { ""Alpha"": ""1"", ""Beta"": ""2"" }, ""Changes"": [] }");
        var r = ProbeJson("PackName-CN", "Alpha", "Beta");
        Assert.Equal("None", r.Fail);
        Assert.Equal("HostCpJson", r.Host);
    }

    [Fact]
    public void ConfigTranslation_ManifestConfigSchema_CountsToo()
    {
        ModWith("HostManifest", "manifest.json", @"{ ""Name"": ""HM"", ""ConfigSchema"": { ""Gamma"": ""1"" } }");
        var r = ProbeJson("PackName-CN", "Gamma");
        Assert.Equal("None", r.Fail);
        Assert.Equal("HostManifest", r.Host);
    }

    [Fact]
    public void ConfigTranslation_TwoModsDeclareTheSameField_RefusesInsteadOfPicking()
    {
        // 用户点名的反例：Spring 的界面包不许盖到 Fall 的界面包上。
        // 变异：把 hits.Count == 1 放宽成 >= 1 ⇒ 这条红，而症状就是"随机盖错人"。
        ModWith("SpringUi", "content.json", @"{ ""ConfigSchema"": { ""Shared"": ""1"", ""Only1"": ""2"" }, ""Changes"": [] }");
        ModWith("FallUi", "content.json", @"{ ""ConfigSchema"": { ""Shared"": ""1"", ""Only2"": ""3"" }, ""Changes"": [] }");
        var r = ProbeJson("PackName-CN", "Shared");
        Assert.Equal("AmbiguousConfigHost", r.Fail);
        Assert.Equal("FallUi、SpringUi", string.Join("、", r.Host.Split('、').OrderBy(x => x, StringComparer.Ordinal)));
    }

    [Fact]
    public void ConfigTranslation_NobodyDeclaresThoseFields_SaysNobodyUsesThoseEntries()
    {
        // 新判据不许把无关包硬塞给某一家：字段谁都声明不了 ⇒ 说"本机没有一家用得到这些条目"。
        // 也不许退回旧那句「你还没装「PackName-CN」」—— 那是把翻译自己的标题当成不存在的前置。
        var r = ProbeJson("PackName-CN", "NobodyDeclinesMe");
        Assert.Equal("HostKeysUnmatched", r.Fail);
        Assert.DoesNotContain("PackName-CN",
            ModService.OverlayRefusal(ModService.OverlayFail.HostKeysUnmatched, _game, r.Host));
    }

    // ── 实机 2026-10-07：汉化包 28252 → Frontier Farm ──
    // 包内路径 Frontier Farm/[CP] Frontier Farm/i18n/zh.json，顶层名恰好就是 Mods 里那个
    // 「没有 manifest 的壳」目录名 ⇒ 旧写法在顶层精确命中那一步直接 return，宿主记成壳，
    // 而 Mods 页两行都是子包，徽标按宿主形状查表一个都查不到：文件在位、游戏里生效，
    // 界面上这条账整个隐身（开关/还原/删除都没有入口）。

    [Fact]
    public void BundleShellTop_RefinesHostToChildRow_KeepingTheSameLandingPath()
    {
        var d = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N"));
        var fp = Path.Combine(d, "Bundle", "Child", "i18n");
        Directory.CreateDirectory(fp);
        File.WriteAllText(Path.Combine(fp, "zh.json"), "{}");
        var plan = _ms.ProbeOverlay(_game, d, out var fail, out _);
        Assert.Equal(ModService.OverlayFail.None, fail);
        Assert.Equal("Bundle/Child", plan!.Host);
        Assert.Equal(new[] { "i18n/zh.json" }, plan.RelPaths);
        Assert.Equal("Bundle/Child", plan.PackRoot);   // src 与 dst 一起少一层，落点不变
    }

    [Fact]
    public void BundleTopWithOwnManifest_StaysTheHost()
    {
        // 顶层自己带 manifest 的捆绑包：Scan 只为顶层出一条目（子包 UID 进隐藏集），
        // 宿主就是顶层，徽标本来就挂得上 —— 归一判据不许顺手把它改到子包行上。
        var top = Path.Combine(_game, "Mods", "OwnedTop");
        Directory.CreateDirectory(Path.Combine(top, "Sub"));
        File.WriteAllText(Path.Combine(top, "manifest.json"), @"{ ""Name"": ""OT"", ""UniqueID"": ""a.ot"" }");
        File.WriteAllText(Path.Combine(top, "Sub", "manifest.json"), @"{ ""Name"": ""S"", ""UniqueID"": ""a.s"" }");
        var r = Probe("OwnedTop/Sub/i18n/zh.json");
        Assert.Equal("None", r.Fail);
        Assert.Equal("OwnedTop", r.Host);
    }
}

/// <summary>上面那条判据只管新装；用户机上已经躺着一条记成壳顶层的账（Frontier Farm 28252），
/// 而它唯一的入口就是那个看不见的徽标 ⇒ 只能靠启动迁移。
/// 键里带着宿主，所以键、Host、RelPath 三处一起改，AppData 副本也要搬到新形状 ——
/// 少搬一样，「关掉再打开」就找不到那份留底。</summary>
public class OverlayBundleHostMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-ovmig-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;

    public OverlayBundleHostMigrationTests()
    {
        _game = Path.Combine(_root, "game");
        // 照他机器上那棵树摆：壳顶层没有 manifest，两个子包各有一行
        var mods = Path.Combine(_game, "Mods");
        foreach (var leaf in new[] { "[CP] Frontier Farm", "[FTM] Frontier Farm" })
        {
            var d = Path.Combine(mods, "Frontier Farm", leaf);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "manifest.json"),
                "{\"Name\":\"" + leaf + "\",\"UniqueID\":\"ff." + leaf + "\"}");
        }
        var owned = Path.Combine(mods, "Owned");      // 对照：顶层自己有行
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "manifest.json"), @"{ ""Name"": ""O"", ""UniqueID"": ""o"" }");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string HostFile => Path.Combine(_game, "Mods", "Frontier Farm", "[CP] Frontier Farm", "i18n", "zh.json");

    private JuniGridConfig ShellTopLedger(string storeId)
    {
        // 汉化包盖上去的那份文件真在位（徽标查表要拿它的哈希对账）
        Directory.CreateDirectory(Path.GetDirectoryName(HostFile)!);
        File.WriteAllText(HostFile, "{\"greeting\":\"早上好\"}");
        var c = new JuniGridConfig { GamePath = _game };
        c.Overlays["Frontier Farm|[CP] Frontier Farm/i18n/zh.json|28252"] = new OverlayRecord
        {
            Host = "Frontier Farm",
            RelPath = "[CP] Frontier Farm/i18n/zh.json",
            PackName = "nxm-28252-118566",
            NexusModId = 28252,
            StoreId = storeId,
            AppliedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(HostFile))),
        };
        c.Overlays["Owned|i18n/zh.json|33314"] = new OverlayRecord
        {
            Host = "Owned",
            RelPath = "i18n/zh.json",
            NexusModId = 33314,
        };
        return c;
    }

    [Fact]
    public void ShellTopRecord_LandsOnTheChildRow_WithItsCopyMoved()
    {
        var storeId = "migtest000001";
        var oldCopy = Path.Combine(ModService.OverlayStoreDir(storeId), "[CP] Frontier Farm", "i18n", "zh.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldCopy)!);
        File.WriteAllText(oldCopy, "{}");
        var c = ShellTopLedger(storeId);

        Assert.Equal(1, ModService.RelocateBundleTopOverlays(c, _game));
        var rec = Assert.Single(c.Overlays.Values.Where(v => v.NexusModId == 28252));
        Assert.Equal("Frontier Farm/[CP] Frontier Farm", rec.Host);
        Assert.Equal("i18n/zh.json", rec.RelPath);
        Assert.Equal("Frontier Farm/[CP] Frontier Farm|i18n/zh.json|28252",
            c.Overlays.Keys.Single(k => k.Contains("28252", StringComparison.Ordinal)));
        Assert.True(File.Exists(Path.Combine(ModService.OverlayStoreDir(storeId), "i18n", "zh.json")));
        Assert.False(File.Exists(oldCopy));
        // 对照那条：宿主自己有行，一个字都不许改
        Assert.Equal("Owned", c.Overlays["Owned|i18n/zh.json|33314"].Host);
        Assert.Equal("i18n/zh.json", c.Overlays["Owned|i18n/zh.json|33314"].RelPath);
    }

    /// <summary>迁移的终点是 Mods 页那颗徽标：它拿「行的 Folder」去 <see cref="ModService.OverlayHealth"/>
    /// 的表里查宿主。两边形状对不上就等于白迁 —— 所以这条直接跑真 Scan + 真判活，
    /// 钉住"账改完，那一行确实查得到、而且判成在位"。</summary>
    [Fact]
    public void AfterMigration_TheChildRowIsTheHostTheBadgeLooksUp()
    {
        var c = ShellTopLedger("");
        ModService.RelocateBundleTopOverlays(c, _game);
        var rowFolder = new ModService().Scan(_game)
            .Single(e => e.Name == "[CP] Frontier Farm").Folder;
        var byHost = ModService.OverlayHealth(_game, c).ByHost;
        Assert.Equal("Frontier Farm/[CP] Frontier Farm", rowFolder);
        Assert.Equal((1, 0), byHost[rowFolder]);
    }

    [Fact]
    public void Migration_IsIdempotent_SecondRunMovesNothing()
    {
        var c = ShellTopLedger("");
        Assert.Equal(1, ModService.RelocateBundleTopOverlays(c, _game));
        var keys = c.Overlays.Keys.ToList();
        Assert.Equal(0, ModService.RelocateBundleTopOverlays(c, _game));
        Assert.Equal(keys, c.Overlays.Keys.ToList());
    }
}

/// <summary>
/// v1.5.2：整包只有【一个裸 zh.json】的汉化包 —— 包内没有任何路径线索，下面每一层判据都要先看
/// 那层目录名，所以这种包以前一律判「判断不出宿主」（实机 2026-10-08：41314「Better Game Menu
/// Chinese translation」，解压后盘上就 zh.json 一个文件，29 个键）。
/// 现在改问这个包自己在 N 网页面登记的那条前置。页面号、模组名、键集全是真机/真 GraphQL 读来的。
/// </summary>
public class BareTranslationPackHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-bare-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly ModService _ms = new();

    /// <summary>真包 nxm-41314-153703.zip 里 zh.json 的 29 个顶层键；本机
    /// BetterGameMenu/i18n/default.json 声明的是同一批（29/29 全等，隔壁 CloudySkies 只撞 1 个）。</summary>
    private static readonly string[] RealKeys =
    {
        "mod.name", "tab.reload-tab", "tab.reload-overlays", "tab.open-settings", "easter-egg.honk",
        "error-page.try-again", "error-page.message", "config.enabled", "config.enabled.about",
        "config.second-row", "config.second-row.about", "config.second-row.automatic",
        "config.second-row.always", "config.second-row.never", "config.advanced", "config.advanced.about",
        "config.hot-swap", "config.hot-swap.about", "config.providers", "config.providers.about",
        "config.provider.automatic", "config.provider.disable", "config.provider.stardew",
        "config.provider.mod", "config.provider.unknown", "config.developer", "config.developer.about",
        "config.fake-tabs", "config.fake-tabs.about",
    };

    private static string Json(IEnumerable<string> keys)
        => "{" + string.Join(",", keys.Select(k => "\"" + k + "\":\"x\"")) + "}";

    public BareTranslationPackHostTests()
    {
        _game = Path.Combine(_root, "game");
        var bgm = Path.Combine(_game, "Mods", "BetterGameMenu", "i18n");
        Directory.CreateDirectory(bgm);
        File.WriteAllText(Path.Combine(bgm, "default.json"), Json(RealKeys));
        var cloudy = Path.Combine(_game, "Mods", "CloudySkies", "i18n");
        Directory.CreateDirectory(cloudy);
        File.WriteAllText(Path.Combine(cloudy, "default.json"), Json(new[] { "config.enabled" }));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static NexusRequirement Req(int id, string name)
        => new(name, "", "https://www.nexusmods.com/stardewvalley/mods/" + id, id, false);

    /// <summary>摆一个"整包只有一个裸 json"的解包目录，跑一遍宿主判定。</summary>
    private (string Fail, string Guess, string Host, string Dst) ProbeBare(
        IReadOnlyList<NexusRequirement>? reqs, string[]? keys = null)
    {
        var d = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "zh.json"), Json(keys ?? RealKeys));
        var plan = _ms.ProbeOverlay(_game, d, out var fail, out var guess, reqs);
        var dst = (plan?.DstPaths ?? plan?.RelPaths);
        return (fail.ToString(), guess, plan?.Host ?? "", dst is null ? "" : string.Join(">", dst));
    }

    /// <summary>把"整包一个翻译 json"摆在指定包内路径上（裸在根 / i18n/ / 某标题层/i18n/）。</summary>
    private (string Fail, string Guess, string Host, string Dst) ProbeJsonAt(string packPath,
        IReadOnlyList<NexusRequirement>? reqs, string[]? keys = null)
    {
        var d = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N"));
        var fp = Path.Combine(d, packPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
        File.WriteAllText(fp, Json(keys ?? RealKeys));
        var plan = _ms.ProbeOverlay(_game, d, out var fail, out var guess, reqs);
        var dst = plan?.DstPaths ?? plan?.RelPaths;
        return (fail.ToString(), guess, plan?.Host ?? "", dst is null ? "" : string.Join(">", dst));
    }

    [Fact]
    public void TranslationUnderAnArbitraryFolder_LandsInI18n_32901()
    {
        // 实机 32901「Childhood Sweetheart Caroline (Chinese)」：整包就一个
        // <c>Caroline/default.json</c>（角色名当目录名），1299 个键里 1280 个就是宿主
        // i18n/default.json 的键。旧写法要求它裸在根目录或 i18n/ 下 ⇒ 这条直接被跳过，
        // 报"文件对不上路径"。现在只看内容：键是谁的键，就落到谁的 i18n/ 下、
        // 用包里那个文件名（default.json），不替作者猜语言码。
        var r = ProbeJsonAt("Caroline/default.json", new[] { Req(32032, "Better Game Menu") });
        Assert.Equal("None", r.Fail);
        Assert.Equal("BetterGameMenu", r.Host);
        Assert.Equal("i18n/default.json", r.Dst);
    }

    [Fact]
    public void NestedUnderItsOwnTitle_49587_FindsHostByKeysAlone()
    {
        // 实机 49587「Zoom Level-Chinese Translation 2026.07」：包内那层目录名就是翻译自己的
        // 标题，页面登记 0 条前置。旧写法拿那层名字当宿主 ⇒「这个包要盖在
        // 「Zoom Level Chinese Translation 2026.07」上，但你还没装它」—— 凭空捏一个不存在的 mod。
        var r = ProbeJsonAt("Zoom Level Chinese Translation 2026.07/i18n/zh.json", null);
        Assert.Equal("None", r.Fail);
        Assert.Equal("BetterGameMenu", r.Host);
        Assert.Equal("i18n/zh.json", r.Dst);
    }

    [Fact]
    public void PackFolderLiterallyNamedI18n_IsNeverClaimedAsAMod()
    {
        // 实机 24232 包内就一层 "i18n"。键也对不上任何已装 mod 时，宁可说"判断不出宿主"，
        // 不许报「你还没装「i18n」」。
        var r = ProbeJsonAt("i18n/zh.json", null, keys: new[] { "nothing.a", "nothing.b", "nothing.c" });
        Assert.Equal("HostKeysUnmatched", r.Fail);
        Assert.DoesNotContain("i18n", ModService.OverlayRefusal(
            ModService.OverlayFail.HostKeysUnmatched, _game, r.Guess));
    }

    [Fact]
    public void DeclaredRowWithoutPageNumber_StillNamesTheHost_ButSaysNoPage()
    {
        // 实机 18080：登记了名字 "Crop Harvest Bubbles"，modId 填的是 "0"。
        // 名字照样能用；报「你还没装它」时不许凭空捏一个「#0」。
        var noPage = new NexusRequirement("Crop Harvest Bubbles", "", "", 0, false);
        var r = ProbeJsonAt("zh.json", new[] { noPage }, keys: new[] { "x.a", "x.b", "x.c" });
        Assert.Equal("HostNotInstalled", r.Fail);
        var msg = ModService.OverlayRefusal(ModService.OverlayFail.HostNotInstalled, _game, r.Guess, noPage);
        Assert.Contains("Crop Harvest Bubbles", msg);
        Assert.DoesNotContain("#0", msg);
    }

    [Fact]
    public void SmapiRow_IsNotAHostCandidate()
    {
        // 实测 24232 登记两行：#20781 与 "SMAPI - Stardew Modding API"。
        // SMAPI 不住在 Mods\ 里，剔掉它才看得出宿主只有一个；不剔就永远"分不出该盖谁"。
        var r = ProbeJsonAt("i18n/zh.json", new[]
        {
            Req(20781, "Crop Harvest Bubbles (Updated)"), Req(2400, "SMAPI - Stardew Modding API"),
        });
        Assert.Equal("HostNotInstalled", r.Fail);
        Assert.Contains("Crop Harvest Bubbles (Updated)", r.Guess);
    }

    [Fact]
    public void BundleChildI18n_IsVisibleToTheKeyRoute()
    {
        // 实机 31795 的真正病灶（2026-10-08）：它的键属于「Grandpa's Farm」，而那是个捆绑包壳 ——
        // i18n 住在 Grandpa's Farm\[CP] Grandpa's Farm\i18n\ 里。键路由原来只扫 Mods\* 一层，
        // 于是整个捆绑包生态（SVE / Grandpa's Farm / Immersive Farm…）对它全部失明，
        // 明明装着宿主却说"判断不出宿主"。名字路由本来就看得到子包，两条证据必须同一套深度。
        var child = Path.Combine(_game, "Mods", "Grandpa's Farm", "[CP] Grandpa's Farm");
        Directory.CreateDirectory(Path.Combine(child, "i18n"));
        File.WriteAllText(Path.Combine(child, "manifest.json"),
            @"{ ""Name"": ""Grandpa's Farm"", ""Author"": ""a"", ""Version"": ""1.0"",
               ""UniqueID"": ""popo.gg.GrandpasFarm"", ""Description"": ""x"",
               ""GameVersion"": ""1.6.0"", ""EntryDll"": """" }");
        var gfKeys = Enumerable.Range(1, 10).Select(i => "gf.key" + i).ToArray();
        File.WriteAllText(Path.Combine(child, "i18n", "default.json"), Json(gfKeys));

        var r = ProbeJsonAt("zh.json", null, keys: gfKeys);
        Assert.Equal("None", r.Fail);
        Assert.Equal(@"Grandpa's Farm/[CP] Grandpa's Farm", r.Host.Replace('\\', '/'));
        Assert.Contains("i18n/zh.json", r.Dst.Replace('\\', '/'));
    }

    [Fact]
    public void Real41314_LandsInsideTheDeclaredHostsI18n()
    {
        var r = ProbeBare(new[] { Req(32032, "Better Game Menu") });
        Assert.Equal("None", r.Fail);
        Assert.Equal("BetterGameMenu", r.Host);
        Assert.Equal("i18n/zh.json", r.Dst);          // 搬进 i18n/，不是宿主根目录
    }

    [Fact]
    public void NoPageData_FallsBackToWhatTheTranslationItselfDeclares()
    {
        // 作者没填 / rar 读不动 / 拿不到页面 ⇒ 退到第二证据：这份翻译自己的键是谁的键。
        // 实机 49587「Zoom Level-Chinese Translation」与 44611「Walk Of Life 职业大修-中文更新」
        // 登记的前置都是 0 行，只能靠这个（实测键集重合：ZoomLevel 100%、第二名 0%）。
        var r = ProbeBare(null);
        Assert.Equal("None", r.Fail);
        Assert.Equal("BetterGameMenu", r.Host);
        Assert.Equal("i18n/zh.json", r.Dst);
    }

    [Fact]
    public void DeclaredHostNotInstalled_ReportsThatPageNumber()
    {
        // 34705 的形状（它登记 #31185，本机只装着隔壁的 #19508）：话术要报出页面号，
        // 而不是只说"判断不出宿主"让人自己去搜。
        var sole = Req(31185, "Seeds N' Saplings - Cornucopia More Crops");
        var r = ProbeBare(new[] { sole });
        Assert.Equal("HostNotInstalled", r.Fail);
        var msg = ModService.OverlayRefusal(ModService.OverlayFail.HostNotInstalled, _game, r.Guess, sole);
        Assert.Contains("#31185", msg);
        Assert.Contains("Seeds N' Saplings - Cornucopia More Crops", msg);
    }

    [Fact]
    public void DeclaredTie_FallsToTheTranslationItself_WhichMustAlsoBeUnique()
    {
        // 登记的前置在本机撞出好几家（并列）⇒ 不许说"你还没装它"，也不许多数服从；
        // 改由这份翻译自己的键去分（实机 31795：前置写着 SVE，而 SVE 在本机是两个同名子包）。
        var both = new[]
        {
            Req(1915, "Content Patcher"), Req(32032, "Better Game Menu"),
            Req(7777, "Cloudy Skies"), Req(2400, "SMAPI"),
        };
        Assert.Equal("BetterGameMenu", ProbeBare(both).Host);   // 键只跟 BGM 的 i18n 对得上

        // 键也分不出（两家都装着、都覆盖过半）⇒ 仍然拒，不替用户挑。
        Directory.CreateDirectory(Path.Combine(_game, "Mods", "CloudySkies", "i18n"));
        File.WriteAllText(Path.Combine(_game, "Mods", "CloudySkies", "i18n", "default.json"), Json(RealKeys));
        var tie2 = ProbeBare(new[] { Req(32032, "Better Game Menu"), Req(7777, "Cloudy Skies") });
        Assert.NotEqual("None", tie2.Fail);
        Assert.Equal("", tie2.Host);
    }

    [Fact]
    public void KeysMatchNobody_NamesTheClosestModButNeverThePacksOwnFolder()
    {
        // 实机 45719「UI Info Suite 2 Alternative-Chinese」：包内那层是翻译自己的标题，
        // 298 个键里只有 95 个（32%）撞在本机装着的【原版】UIInfoSuite2 上 ⇒ 宿主真没装。
        // 旧写法把包内那个目录名当成"你没装的前置"报出去 —— 凭空捏一个不存在的 mod；
        // 而"判断不出宿主"又把已经量出来的东西藏起来。两句都不许再出现。
        var partial = RealKeys.Take(10)
            .Concat(Enumerable.Range(1, 20).Select(i => "fork.only" + i)).ToArray();
        var r = ProbeJsonAt("UIInfoSuite2Alt-Chinese/i18n/zh.json", null, keys: partial);
        Assert.Equal("HostKeysUnmatched", r.Fail);
        Assert.Equal("", r.Host);                     // 一个字节都不许落盘
        Assert.Equal("BetterGameMenu", r.Guess);      // 最接近的一家（10/30），它不是宿主
        var msg = ModService.OverlayRefusal(ModService.OverlayFail.HostKeysUnmatched, _game, r.Guess);
        Assert.DoesNotContain("UIInfoSuite2Alt-Chinese", msg);
        Assert.DoesNotContain("判断不出宿主", msg);
        Assert.DoesNotContain("你还没装它", msg);
        Assert.Contains("BetterGameMenu", msg);
    }

    [Fact]
    public void NotASingleKeyMatches_SaysSoWithoutNamingAMod()
    {
        // 实机 44609「Catcrows for Content Patcher- config Chinese」：42 个 config.<字段> 键，
        // 本机 84 家有 i18n 的 mod 里【一家都没沾上一个】⇒ 没有名字可报，就不许报一个名字。
        var r = ProbeBare(null, keys: new[]
        { "config.Basic Scarecrow.name", "config.Deluxe Scarecrow.name", "config.Never.name" });
        Assert.Equal("HostKeysUnmatched", r.Fail);
        Assert.Equal("", r.Guess);
        var msg = ModService.OverlayRefusal(ModService.OverlayFail.HostKeysUnmatched, _game, r.Guess);
        Assert.DoesNotContain("「", msg);
        Assert.DoesNotContain("判断不出宿主", msg);
    }

    [Fact]
    public void HostInstalled_ButEntriesDontMatch_SaysContentMismatchNotUnknownHost()
    {
        // 名字对上、条目对不上（真机 CloudySkies 只有 1/29 重合）⇒ 不许说成"判断不出宿主"，
        // 也不许把文件落到宿主根目录变成静默无效。
        var r = ProbeBare(new[] { Req(9999, "Cloudy Skies") },
            keys: new[] { "unrelated.a", "unrelated.b", "unrelated.c" });
        Assert.Equal("DeclaredHostKeyMismatch", r.Fail);
        Assert.Equal("CloudySkies", r.Guess);        // 宿主是认出来了（认出来才说得出"内容不对"）
        Assert.Equal("", r.Host);                    // 但没有 plan = 一个字节都不许落盘
        Assert.DoesNotContain("判断不出宿主",
            ModService.OverlayRefusal(ModService.OverlayFail.DeclaredHostKeyMismatch, _game, r.Guess));
    }

    [Fact]
    public void PackWithASecondFile_IsNotTreatedAsBare()
    {
        // 整包不止一个文件 ⇒ 不走这条最窄的通道（多一个裸 json 就说不清哪份是翻译）。
        var d = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "zh.json"), Json(RealKeys));
        File.WriteAllText(Path.Combine(d, "en.json"), Json(RealKeys));
        var plan = _ms.ProbeOverlay(_game, d, out var fail, out _, new[] { Req(32032, "Better Game Menu") });
        Assert.Equal("NoHostInPack", fail.ToString());
        Assert.Null(plan);
    }
}
