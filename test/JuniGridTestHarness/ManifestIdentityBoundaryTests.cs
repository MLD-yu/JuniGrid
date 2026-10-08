using System.IO.Compression;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 用户 2026-10-08 定的边界，钉死在这里：
/// <b>没有 manifest.json 的包</b>才允许按名字 / 内容猜宿主；
/// <b>带 manifest.json 的包一律以它自己写的 UniqueID 为准，不许按名字并到别的 mod 上</b>。
/// <para>猜错的代价不是"装不上"，是把两个 mod 整混 —— 实测 Goshcakes 把每个季节界面包都发成
/// 同一个 UniqueID=<c>Goshcakes.customMenuEtc</c>，名字判据把 Fall UI 的 27 个文件盖进了
/// Spring UI 的目录；而"同 UID 判重清扫"还会把用户已装那份整个移进回收站。</para>
/// 三条用例用<b>同一个包形状</b>跑两遍，只改"有没有 manifest"，结果必须一边倒。
/// </summary>
public class ManifestIdentityBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-mb-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly ConfigService _cfg = new();

    public ManifestIdentityBoundaryTests()
    {
        _game = Path.Combine(_root, "game");
        var host = Path.Combine(_game, "Mods", "HostOne");
        Directory.CreateDirectory(Path.Combine(host, "i18n"));
        File.WriteAllText(Path.Combine(host, "manifest.json"),
            @"{ ""Name"": ""HostOne"", ""Author"": ""a"", ""Version"": ""1.0"", ""UniqueID"": ""Host.One"",
               ""Description"": ""x"", ""GameVersion"": ""1.6.0"", ""EntryDll"": """" }");
        File.WriteAllText(Path.Combine(host, "i18n", "default.json"),
            @"{ ""greeting"": ""hi"", ""farewell"": ""bye"", ""menu.open"": ""open"" }");
        _cfg.Load();
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>摆一个包（可带 manifest.json），压成 zip，跑**生产的** InstallNew。</summary>
    private (string? Err, string? Name) Install(bool withManifest, string uid, string name)
    {
        var src = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "i18n"));
        File.WriteAllText(Path.Combine(src, "i18n", "zh.json"),
            @"{ ""greeting"": ""你好"", ""farewell"": ""再见"", ""menu.open"": ""打开"" }");
        if (withManifest)
            File.WriteAllText(Path.Combine(src, "manifest.json"),
                "{ \"Name\": \"" + name + "\", \"Author\": \"b\", \"Version\": \"2.0\", \""
                + "UniqueID\": \"" + uid + "\", \"Description\": \"y\", \"GameVersion\": \"1.6.0\", "
                + "\"EntryDll\": \"\" }");
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(src, zip);
        var err = new ModService().InstallNew(_game, zip, out var modName, null, cfg: _cfg);
        return (err, modName);
    }

    private string[] HostFiles() => Directory.GetFiles(
        Path.Combine(_game, "Mods", "HostOne"), "*", SearchOption.AllDirectories)
        .Select(f => Path.GetFileName(f)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void SameShapeWithoutManifest_IsTreatedAsAnOverlay()
    {
        var before = HostFiles();
        var (err, _) = Install(withManifest: false, uid: "", name: "");
        Assert.True(err is null, "无 manifest 的包应当作为覆盖包装进去，却报：" + err);
        Assert.Contains("zh.json", HostFiles());                 // 盖进了 HostOne/i18n/
        Assert.NotEqual(before, HostFiles());
    }

    [Fact]
    public void SameShapeWithManifest_NeverEvictsTheSameNamedMod()
    {
        // 显示名与已装的 HostOne 一模一样、包内路径也一模一样 —— 但它自己写了另一个 UniqueID。
        // 旧写法只看文件夹名：已装那份会被整个移进回收站，新那份顶上它的名字，
        // 用户看到的症状是「我那个 mod 自己没了」。现在必须原地停下，一个字节都不动。
        var before = HostFiles();
        var (err, _) = Install(withManifest: true, uid: "Some.Other", name: "HostOne");
        Assert.NotNull(err);
        Assert.Contains("UniqueID", err);   // 只挑两种语言里都出现的词：别的用例会把这个进程切成英文
        Assert.True(HostFiles().SequenceEqual(before),
            "已装那份被改动了：before=[" + string.Join(",", before) + "] after=["
            + string.Join(",", HostFiles()) + "]");
        var trash = Path.Combine(_game, "Mods", ".junigrid_trash");
        Assert.False(Directory.Exists(trash) && Directory.GetDirectories(trash).Length > 0,
            "已装那份被扫进了回收站");
    }

    [Fact]
    public void SameUidDifferentName_IsRefusedAndKeepsTheInstalledOne()
    {
        // 名字判据不许在这一侧出手：撞 UID 而素材不同 ⇒ 既不覆盖也不装，更不许把已装那份扫进回收站。
        var before = HostFiles();
        var (err, _) = Install(withManifest: true, uid: "Host.One", name: "HostOne Spring UI");
        Assert.NotNull(err);
        Assert.Contains("UniqueID", err);   // 同上：断言不许依赖界面语言
        Assert.Equal(before, HostFiles());                        // 已装那份完好，没被"判重清扫"吃掉
    }

    /// <summary>实机 34635「DaisyNiko's Earthy Recolour - Chinese」的形状：包内顶层目录名
    /// <b>就是已装宿主的文件夹名</b>，而包里的 <c>assets/Maps/fall_beach.png</c> 这个目标
    /// 在宿主自己 content.json 里也声明过 ⇒ <c>HasForeignAssetTarget</c> 认得出它。
    /// 旧顺序下转换那条路先接管，把它转成 <c>JuniGrid.AssetPack.*</c> 去打原版资产；
    /// 现在覆盖判定排在转换之前。这条用例钉的就是这个先后顺序 —— 谁把它换回去，这条就红。</summary>
    [Fact]
    public void HostNamedTopFolder_WinOverTheCpConversionChannel()
    {
        var host = Path.Combine(_game, "Mods", "[CP] Host One");
        Directory.CreateDirectory(Path.Combine(host, "assets", "Maps"));
        File.WriteAllText(Path.Combine(host, "assets", "Maps", "fall_beach.png"), "原版");
        File.WriteAllText(Path.Combine(host, "content.json"),
            @"{ ""Changes"": [ { ""Action"": ""EditImage"", ""Target"": ""Maps/fall_beach"",
               ""FromFile"": ""assets/Maps/fall_beach.png"" } ] }");

        var src = Path.Combine(_root, "rp");
        var packFile = Path.Combine(src, "[CP] Host One", "assets", "Maps");
        Directory.CreateDirectory(packFile);
        File.WriteAllText(Path.Combine(packFile, "fall_beach.png"), "重染版");
        var zip = Path.Combine(_root, "rp.zip");
        ZipFile.CreateFromDirectory(src, zip);

        var err = new ModService().InstallNew(_game, zip, out _, null, cfg: _cfg);
        Assert.True(err is null, "应当作为覆盖包装进去，却报：" + err);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_game, "Mods"))
            .Where(d => Path.GetFileName(d).StartsWith("JuniGrid.AssetPack", StringComparison.OrdinalIgnoreCase)));
        var landed = Path.Combine(host, "assets", "Maps", "fall_beach.png");
        Assert.Equal("重染版", File.ReadAllText(landed));
        Assert.True(File.Exists(landed + ".junigrid_backup"), "原版没留备份");
        Assert.Equal("原版", File.ReadAllText(landed + ".junigrid_backup"));
    }

    /// <summary>实机 44611「Walk Of Life - 职业大修 - 中文更新」：宿主的 i18n/zh.json 早就在位、
    /// 与包里逐字节相同 ⇒ 一个字节都不该写。旧写法在循环开头就记一句"已放到宿主的
    /// 「i18n/zh.json」"，于是卡片写着"已放到宿主"、Mods 行一个覆盖徽标都没有。
    /// 这条不依赖第二次安装，也不碰共享沙箱里的账，所以不会被并行用例的顺序影响。</summary>
    [Fact]
    public void AlreadyInPlaceOverlay_NeverClaimsItPlacedTheFile()
    {
        var zh = @"{ ""greeting"": ""你好"", ""farewell"": ""再见"", ""menu.open"": ""打开"" }";
        File.WriteAllText(Path.Combine(_game, "Mods", "HostOne", "i18n", "zh.json"), zh);

        var src = Path.Combine(_root, "same");
        Directory.CreateDirectory(Path.Combine(src, "i18n"));
        File.WriteAllText(Path.Combine(src, "i18n", "zh.json"), zh);
        var zip = Path.Combine(_root, "same.zip");
        ZipFile.CreateFromDirectory(src, zip);

        var notes = new List<string>();
        var err = new ModService().InstallNew(_game, zip, out _, null, cfg: _cfg, installNotes: notes);
        Assert.True(err is null, "内容已在位应当算成功，却报：" + err);
        // 断言不许依赖界面语言（别的用例会把进程切成英文）：搬家提示一定会点名落点路径，
        // "没改动"那句不会。
        Assert.DoesNotContain(notes, n => n.Contains("i18n/zh.json"));
        Assert.Single(notes);
        Assert.Equal(zh, File.ReadAllText(Path.Combine(_game, "Mods", "HostOne", "i18n", "zh.json")));
        Assert.False(File.Exists(Path.Combine(_game, "Mods", "HostOne", "i18n", "zh.json"
            + ".junigrid_backup")), "没改动就不该留原版备份");
    }
}
