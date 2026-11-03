using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// manifest 的 <c>MinimumApiVersion</c> 与「可选依赖的最低版本」必须原样进 ModEntry ——
/// 界面靠这两个字段判「SMAPI 太旧」和「可选依赖版本不够」。
/// <para>实机 2026-10-29：StardewUI 要 SMAPI 4.5.2（实际跑 4.5.1）、Iconic Framework 与
/// Self Checkout 的 GMCM 标着 <c>IsRequired: false</c> 却因 1.12.0 &lt; 1.14.1 被 SMAPI
/// 整条跳过 —— 界面两处都报 0。漏解析这两个字段，判据就是静音的。</para>
/// </summary>
public sealed class ManifestApiVersionAndOptionalDepTests
{
    private static ModEntry ScanSingle(string manifestJson)
    {
        var root = TempDirs.Create("manifest-api");
        try
        {
            var mod = Path.Combine(root, "Mods", "Widget");
            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "manifest.json"), manifestJson);
            return Assert.Single(new ModService().Scan(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MinApiVersion_And_OptionalDependencyMinimumVersion_AreParsed()
    {
        var e = ScanSingle("""
        {
          "Name": "Widget",
          "Author": "t",
          "Version": "1.0.0",
          "UniqueID": "t.Widget",
          "MinimumApiVersion": "4.5.2",
          "Dependencies": [
            { "UniqueID": "t.Gate", "MinimumVersion": "1.14.1", "IsRequired": false }
          ]
        }
        """);

        Assert.Equal("4.5.2", e.MinApiVersion);
        // 可选依赖：缺了不报「缺少依赖」（不在必需表里），但版本判定要知道它
        Assert.DoesNotContain("t.Gate", e.Dependencies);
        Assert.Contains("t.Gate", e.OptionalDependencies);
        Assert.Equal("1.14.1", e.DependencyMinVersions["t.Gate"]);
    }

    [Fact]
    public void LegacyMinimumApiVersionForSmapi_IsAccepted()
    {
        var e = ScanSingle("""
        {
          "Name": "Old",
          "Author": "t",
          "Version": "1.0.0",
          "UniqueID": "t.Old",
          "MinimumApiVersionForSMAPI": "3.18"
        }
        """);

        Assert.Equal("3.18", e.MinApiVersion);
    }

    /// <summary>两条判据都落在同一个比较器上：本机 &lt; 要求 才算不够。
    /// 相等不能报（每台机器都装的就是要求那一版；报错等于让所有人看到一条假红字）。</summary>
    [Theory]
    [InlineData("1.12.0", "1.14.1", true)]
    [InlineData("4.5.1", "4.5.2", true)]
    [InlineData("4.5.2", "4.5.2", false)]
    [InlineData("4.5.10", "4.5.9", false)]
    public void VersionCompare_OnlyFlagsStrictlyOlder(string have, string need, bool older)
        => Assert.Equal(older, VersionUtil.IsOlderThan(have, need));

    /// <summary>实机那一行：GMCM 是可选依赖，装着 1.12.0、作者要 1.14.1 —— SMAPI 照样跳过，
    /// 所以它必须出现在「依赖版本过旧」里（旧写法只遍历必需依赖，这条永远为空）。</summary>
    [Fact]
    public void StaleDepUids_FlagsOptionalDependency_WhenInstalledButOlder()
    {
        var m = new ModEntry
        {
            Dependencies = new() { "t.Required" },
            OptionalDependencies = new() { "spacechase0.GenericModConfigMenu" },
            DependencyMinVersions = new(StringComparer.OrdinalIgnoreCase)
            {
                ["t.Required"] = "1.0.0",
                ["spacechase0.GenericModConfigMenu"] = "1.14.1",
            },
        };
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["t.Required"] = "1.0.0",
            ["spacechase0.GenericModConfigMenu"] = "1.12.0",
        };

        Assert.Equal(new[] { "spacechase0.GenericModConfigMenu" },
            DependencyRules.StaleDepUids(m, installed));
    }

    /// <summary>可选依赖没装是允许的（作者自己说的），不能因为没装就报红字。</summary>
    [Fact]
    public void StaleDepUids_IgnoresMissingOptionalDependency()
    {
        var m = new ModEntry
        {
            OptionalDependencies = new() { "spacechase0.GenericModConfigMenu" },
            DependencyMinVersions = new(StringComparer.OrdinalIgnoreCase)
            {
                ["spacechase0.GenericModConfigMenu"] = "1.14.1",
            },
        };
        Assert.Empty(DependencyRules.StaleDepUids(m, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("4.5.2", "4.5.1", "4.5.2")]
    [InlineData("4.5.2", "4.5.2", null)]
    [InlineData("4.5.2", "installed", null)]   // 只确认装了、读不出版本：不指控
    [InlineData("4.5.2", null, null)]
    [InlineData(null, "4.5.1", null)]
    public void SmapiTooOldNeed_OnlyFlagsStrictlyOlder(string? need, string? have, string? expected)
        => Assert.Equal(expected, DependencyRules.SmapiTooOldNeed(need, have));

    /// <summary>「一键安装」的输入必须与界面那份清单同源 —— 实机 2026-10-29：弹窗列出了
    /// GMCM（可选依赖、装着太旧），点「开始安装」却什么也没发生：管线自己只遍历必需依赖，
    /// 批次为空直接收工。这里把四种组合钉死。</summary>
    [Theory]
    [InlineData(false, null, true)]      // 必需 + 没装            → 要装
    [InlineData(false, "1.0.0", true)]   // 必需 + 装着太旧        → 要换
    [InlineData(false, "2.0.0", false)]  // 必需 + 够新            → 不动
    [InlineData(true, null, false)]      // 可选 + 没装            → 不动（作者允许）
    [InlineData(true, "1.0.0", true)]    // 可选 + 装着太旧        → 要换（SMAPI 会跳过依赖方）
    [InlineData(true, "2.0.0", false)]   // 可选 + 够新            → 不动
    public void DepsToProcess_CoversRequiredMissing_AndAnyStale(bool optional, string? installed, bool expected)
    {
        const string dep = "some.Dep";
        var m = new ModEntry
        {
            Dependencies = optional ? new() : new() { dep },
            OptionalDependencies = optional ? new() { dep } : new(),
            DependencyMinVersions = new(StringComparer.OrdinalIgnoreCase) { [dep] = "1.5.0" },
        };
        var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (installed is not null) installedVersions[dep] = installed;

        var got = DependencyRules.DepsToProcess(m, installedVersions);
        Assert.Equal(expected, got.Contains(dep));
    }
}
