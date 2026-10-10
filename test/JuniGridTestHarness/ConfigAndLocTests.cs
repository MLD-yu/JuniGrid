using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// ConfigService 落盘行为（JUNIGRID_CONFIG_DIR 沙箱）。这些测试共享沙箱文件，
/// 程序集已禁用并行；每个测试开头 Reset、结尾 Flush，避免防抖写盘跨测试互踩。
/// 注意：损坏配置/丢主文件等失败路径会经 AppLog 往真 %APPDATA%\JuniGrid\juni-grid.log
/// 追加几行 WRN/ERR —— 日志文件本来就是给人看的，无害。
/// </summary>
public class ConfigServiceTests
{
    private static string Dir => TestSandbox.Dir;
    private static string CfgPath => TestSandbox.ConfigPath;

    private static void Flush(ConfigService cfg) => cfg.Flush();

    [Fact]
    public void FreshSandbox_WritesDefaultConfig_WithFirstRunDate()
    {
        TestSandbox.ResetConfigDir();
        var cfg = new ConfigService();
        Flush(cfg);
        Assert.True(File.Exists(CfgPath));
        Assert.False(string.IsNullOrWhiteSpace(cfg.Current.FirstRunDate));
        Assert.Equal("", cfg.Current.GamePath);          // 默认配置是空壳
        Assert.Equal("zh", cfg.Current.Language);
        Assert.Equal(CfgPath, ConfigService.ConfigFilePath);
    }

    [Fact]
    public void Save_Load_Roundtrip()
    {
        TestSandbox.ResetConfigDir();
        var c1 = new ConfigService();
        c1.Save(new JuniGridConfig
        {
            GamePath = @"E:\Games\Stardew Valley",
            Language = "en",
            TotalLaunchCount = 7,
        });
        Flush(c1);

        var c2 = new ConfigService();
        Assert.Equal(@"E:\Games\Stardew Valley", c2.Current.GamePath);
        Assert.Equal("en", c2.Current.Language);
        Assert.Equal(7, c2.Current.TotalLaunchCount);
        Flush(c2);
    }

    [Fact]
    public void Save_KeepsTopLevelKeysThisBuildDoesNotKnow()
    {
        // 实机 2026-10-08：机器上另一个 10-03 时代的安装副本（它的模型里根本没有
        // ContentOverrides 这个属性）存了一次盘，223 条内容包账被"整份序列化覆盖"抹掉，
        // 而游戏本体里 147 个文件还被那些包盖着。写盘口必须留下自己不认识的键。
        TestSandbox.ResetConfigDir();
        var c1 = new ConfigService();
        c1.Save(new JuniGridConfig { GamePath = @"X:\SDV" });
        Flush(c1);
        var text = File.ReadAllText(CfgPath).TrimEnd();
        File.WriteAllText(CfgPath, text[..^1] + ",\"someFutureKey\":{\"a\":1}}");

        var c2 = new ConfigService();                     // Load 认不得那个键，也不该丢它
        c2.Save(c2.Current);
        Flush(c2);
        Assert.Contains("someFutureKey", File.ReadAllText(CfgPath));
    }

    [Fact]
    public void Save_UnknownKeyPreservation_StillLetsOurOwnValuesWin()
    {
        // 只保"不认识"的键。模型认识的必须以内存为准 —— 不然用户在设置里改的东西会被
        // 盘上那份顶回来，那比丢别人的键更糟。
        TestSandbox.ResetConfigDir();
        var c1 = new ConfigService();
        c1.Save(new JuniGridConfig { GamePath = @"X:\SDV", TotalLaunchCount = 9 });
        Flush(c1);
        // 改文件里那一个字段：用 JsonNode 定点改，别拿字符串 Replace 碰运气
        //（配置是 WriteIndented 的，"key": 9 冒号后面有空格）
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CfgPath))!.AsObject();
        root["totalLaunchCount"] = 123;
        File.WriteAllText(CfgPath, root.ToJsonString());

        var c2 = new ConfigService();                     // 盘上那份 123 被读进来
        Assert.Equal(123, c2.Current.TotalLaunchCount);
        c2.Current.TotalLaunchCount = 7;                  // 内存改完保存 ⇒ 内存说了算
        c2.Save(c2.Current);
        Flush(c2);
        var c3 = new ConfigService();
        Assert.Equal(7, c3.Current.TotalLaunchCount);
    }

    [Fact]
    public void MissingMainFile_IsRestoredFromBak()
    {
        TestSandbox.ResetConfigDir();
        var c1 = new ConfigService();
        c1.Save(new JuniGridConfig { GamePath = @"X:\SDV" });
        Flush(c1);

        var c2 = new ConfigService();                    // Load 时把非空壳配置复制成 .bak
        Assert.True(File.Exists(CfgPath + ".bak"));
        Flush(c2);

        File.Delete(CfgPath);                            // 主文件丢失（用户手删/杀进程半路）
        var c3 = new ConfigService();
        Assert.Equal(@"X:\SDV", c3.Current.GamePath);    // 从 .bak 救回
        Assert.True(File.Exists(CfgPath));               // 并写回主文件
        Flush(c3);
    }

    [Fact]
    public void CorruptConfig_FallsBackToDefaults_KeepsCorruptScene()
    {
        TestSandbox.ResetConfigDir();
        File.WriteAllText(CfgPath, "{ this is not json !!!");

        var cfg = new ConfigService();
        Assert.Equal("", cfg.Current.GamePath);          // 退回默认，不抛异常
        Assert.Contains(Directory.GetFiles(Dir, "junigrid.config.json.corrupt-*"),
            _ => true);                                  // 损坏现场留档
        Flush(cfg);
    }

    [Fact]
    public void LegacyPascalCaseConfig_StillLoads()
    {
        // v1.1.x 前 PascalCase 旧配置必须能读 —— PropertyNameCaseInsensitive 就是防这个
        TestSandbox.ResetConfigDir();
        File.WriteAllText(CfgPath, @"{ ""GamePath"": ""D:\\old-sdv"", ""Language"": ""en"" }");

        var cfg = new ConfigService();
        Assert.Equal(@"D:\old-sdv", cfg.Current.GamePath);
        Assert.Equal("en", cfg.Current.Language);
        Flush(cfg);
    }
}

/// <summary>轻量 i18n：源文本即 key，英文走内嵌 en.json。</summary>
/// <summary>LocService 构造时把自己挂到静态 <c>_inst</c> 上（服务层没有注入点，只能靠它取词），
/// 而 <c>Lang</c> 读的是它手里那份配置的 <c>Current.Language</c>。这个类把语言设成 en 之后留着不管，
/// 于是【进程内后面每一个类】的 <c>LocService.Tr/Tf</c> 都拿到英文 —— 中文格式串的断言当场翻红。
/// 实机症状：新加一个测试类改动了执行顺序之后，PackVariantTests 那句 <c>line.IndexOf('：')</c>
/// 返回 -1（备注已是英文冒号）。清配置文件救不了它，泄漏在内存里的静态实例上。</summary>
public class LocServiceTests
{
    [Fact]
    public void Zh_ReturnsSourceText()
    {
        TestSandbox.ResetConfigDir();
        var cfg = new ConfigService();
        var loc = new LocService(cfg);
        Assert.Equal("zh", loc.Lang);
        Assert.Equal("已锁定", loc.T("已锁定"));
        Assert.Equal("不存在的键__xyz", loc.T("不存在的键__xyz"));   // 未命中回落中文
        Assert.Equal("已锁定", LocService.Tr("已锁定"));            // 静态入口同样可用
    }

    [Fact]
    public void En_CatalogHits_And_MissingKeysFallBack()
    {
        TestSandbox.ResetConfigDir();
        var cfg = new ConfigService();
        var loc = new LocService(cfg);
        loc.SetLang("en");
        try
        {
            Assert.Equal("en", loc.Lang);

            Assert.Equal("Locked", loc.T("已锁定"));
            Assert.Equal("Unlocked", loc.T("未锁定"));
            Assert.Equal("Game Version Manager", loc.T("游戏版本管理"));
            Assert.Equal("不存在的键__xyz", loc.T("不存在的键__xyz"));

            // F：占位符替换（en.json 里该句保留 {0}）
            Assert.Equal("Started downloading abc (progress is in the task dock)",
                loc.F("已开始下载 {0}（进度见右下角任务中心）", "abc"));
            // F：格式串坏掉时回落原文，不抛
            Assert.Equal("Locked", loc.F("已锁定", 1, 2, 3));
        }
        finally { loc.SetLang("zh"); }   // 静态 _inst 会把英文带给后面每一个类
    }
}
