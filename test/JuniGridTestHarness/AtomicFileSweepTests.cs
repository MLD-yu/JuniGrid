using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>原子写盘的<b>垃圾回收</b>那一半（<see cref="AtomicFileTests"/> 管的是写本身）。
/// <para>起因（2026-10-09 实机）：他的 <c>%APPDATA%\JuniGrid</c> 里躺着 21 个
/// <c>tasks.json.&lt;8位十六进制&gt;.tmp</c>（924 KB），全是进程被杀留下的 —— 旧实现只在
/// "还会重试"那条分支里删 tmp，放弃时不删，也没有任何地方清扫。</para>
/// <para>这批用例钉的是清扫的<b>克制</b>：只删"能证明是我们自己造的、而且已经死了十分钟"的那一份；
/// 别人的 <c>.tmp</c>、目标文件已经不在的、刚写下去的，一律不碰。</para></summary>
public class AtomicFileSweepTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "jg-atomic-sweep-" + Guid.NewGuid().ToString("N")[..8]);

    public AtomicFileSweepTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    private string P(string name) => Path.Combine(_dir, name);
    private void Aged(string name, double hours) =>
        File.SetLastWriteTime(P(name), DateTime.Now.AddHours(-hours));

    [Fact]
    public void Sweep_KeepsFreshForeignAndOrphaned_TakesOnlyOurs()
    {
        File.WriteAllText(P("tasks.json"), "{}");                        // 真实目标
        File.WriteAllText(P("tasks.json.1a2b3c4d.tmp"), "half-written");  // 我们的，两小时前
        Aged("tasks.json.1a2b3c4d.tmp", 2);
        File.WriteAllText(P("tasks.json.9f8e7d6c.tmp"), "in flight");     // 我们的，但刚写下
        File.WriteAllText(P("notes.txt.tmp"), "someone else's");          // 命名不合我们的格式
        File.WriteAllText(P("gone.json.99887766.tmp"), "target missing"); // 目标已经不在了
        Aged("gone.json.99887766.tmp", 5);

        Assert.Equal(1, AtomicFile.SweepStale(new[] { _dir }));

        Assert.False(File.Exists(P("tasks.json.1a2b3c4d.tmp")));
        Assert.True(File.Exists(P("tasks.json.9f8e7d6c.tmp")));
        Assert.True(File.Exists(P("notes.txt.tmp")));
        Assert.True(File.Exists(P("gone.json.99887766.tmp")));
        Assert.True(File.Exists(P("tasks.json")));
    }

    [Fact]
    public void Sweep_ToleratesMissingDir_AndScansEveryGivenDir()
    {
        var b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, "playtime.json"), "{}");
        File.WriteAllText(Path.Combine(b, "playtime.json.0123abcd.tmp"), "x");
        File.SetLastWriteTime(Path.Combine(b, "playtime.json.0123abcd.tmp"), DateTime.Now.AddHours(-3));

        Assert.Equal(1, AtomicFile.SweepStale(new[] { Path.Combine(_dir, "nope"), b, b }));   // 不存在 + 重复给
    }

    [Fact]
    public void FailedWrite_LeavesNoTmpBehind()
    {
        // 目标被别的句柄独占 ⇒ File.Move 必失败。旧实现重试完就把 tmp 丢在盘上；
        // 现在放弃前必须自己收走，异常照旧抛给调用方（不静默吞）。
        var target = P("config.json");
        File.WriteAllText(target, "old");
        using (var hold = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(target, "new"));

        Assert.Equal("old", File.ReadAllText(target));                  // 旧内容没被截断
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));                 // 一个垃圾都没留
    }
}
