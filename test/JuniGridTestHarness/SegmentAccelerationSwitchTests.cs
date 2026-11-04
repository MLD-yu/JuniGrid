using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>分片加速的排障逃生口（环境变量 <see cref="ConfigService.SegmentDownloadEnvVar"/>）。
/// <para>这条口子不是给用户的选项 —— 设置页没有开关，它只服务于一件事：**出问题时能先关掉对比**。
/// 典型现场是被 CDN 按连接限流（3 任务 × 4 车道 = 12 条连接）或代理节点对多连接不友好，
/// 表现是「每个包都在慢慢失败」，自动回落救不了。这时候能一条命令关掉，比改代码重编译快得多。</para>
/// <para>所以这里钉两件事：<b>默认必须是开</b>（任何一个拼错的值、空值、没设置，都不能把功能关掉），
/// 以及「关」的写法要认全（0/false/off/no，不分大小写、忽略空格）—— 排障时人不会去查文档。</para></summary>
public class SegmentAccelerationSwitchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("garbage")]      // 写错了就当作没设：宁可开着，也不能因为一个笔误让功能悄悄消失
    public void AnythingButAnExplicitOffLeavesSegmentsOn(string? raw)
        => Assert.True(ConfigService.SegmentAccelerationEnabled(raw));

    [Theory]
    [InlineData("0")]
    [InlineData(" 0 ")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("False")]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("no")]
    [InlineData("NO")]
    public void ExplicitOffValuesTurnSegmentsOff(string raw)
        => Assert.False(ConfigService.SegmentAccelerationEnabled(raw));

    [Fact]
    public void EnvVarNameIsTheDocumentedOne()
        => Assert.Equal("JUNIGRID_SEGMENT_DOWNLOAD", ConfigService.SegmentDownloadEnvVar);
}
