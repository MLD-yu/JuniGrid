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
}
