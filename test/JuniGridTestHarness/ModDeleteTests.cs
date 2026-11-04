using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// v1.3.14：删除器的行为契约。
/// <para>
/// 这批改动的风险不在"能不能删掉"（原实现也能），而在<b>语义没变</b>：
/// 回收站落点、子包层级、清空后的壳、以及"取消到底保证了什么"。
/// 取消的保证被刻意定成：<b>还没开始物理删除 → 一个字节都不动</b>；
/// 已经开始删 → 停手并把剩余移回。这两条都必须在测试里钉住。
/// </para>
/// </summary>
public class ModDeleteTests
{
    private static string NewGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "jg-del-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Mods"));
        return root;
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    /// <summary>同步调用的 IProgress —— <see cref="Progress{T}"/> 在线程池上异步派发，
    /// 测试里想在报告的那一刻立刻取消，必须用这个。</summary>
    private sealed class InlineProgress : IProgress<DeleteTick>
    {
        private readonly Action<DeleteTick> _on;
        public InlineProgress(Action<DeleteTick> on) => _on = on;
        public void Report(DeleteTick value) => _on(value);
    }

    [Fact]
    public async Task Deletes_Nested_Tree_And_Leaves_No_Trash_Residue()
    {
        var game = NewGame();
        try
        {
            var mod = Path.Combine(game, "Mods", "DeepMod");
            WriteFile(Path.Combine(mod, "manifest.json"), 64);
            WriteFile(Path.Combine(mod, "assets", "a", "x.png"), 128);
            WriteFile(Path.Combine(mod, "assets", "b", "y.png"), 256);

            var r = await ModDeleteService.DeleteAsync(game, ["DeepMod"], toTrash: false,
                cfg: null, progress: null, CancellationToken.None);

            Assert.Equal(1, r.Deleted);
            Assert.False(r.AnyFailed);
            Assert.False(r.Canceled);
            // v1.3.19：返回时只保证"已经不在 Mods 里" —— 同盘改名是瞬时的
            Assert.False(Directory.Exists(mod));
            // 逐文件清理交给了后台队列（被杀软拖慢时可能几十秒），这里允许它慢慢来：
            // 断言的是"最终不残留"，不是"调用返回时就删干净"。
            var trash = StoragePaths.GameTrashDir(game);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Directory.Exists(trash)
                   && Directory.GetFileSystemEntries(trash).Length > 0
                   && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            if (Directory.Exists(trash))
                Assert.Empty(Directory.GetFileSystemEntries(trash));
        }
        finally { TryKill(game); }
    }

    [Fact]
    public async Task To_Trash_Keeps_Contents_And_Keeps_Subpackage_Level()
    {
        var game = NewGame();
        try
        {
            // 捆绑包的子包：回收站里也要保持 "Bundle/[CC] Sub" 这一层，
            // 否则手动还原时玩家无法整层拖回 Mods
            WriteFile(Path.Combine(game, "Mods", "Bundle", "[CC] Sub", "manifest.json"), 32);
            WriteFile(Path.Combine(game, "Mods", "Bundle", "[CC] Sub", "content.json"), 48);

            var r = await ModDeleteService.DeleteAsync(game, ["Bundle/[CC] Sub"], toTrash: true,
                cfg: null, progress: null, CancellationToken.None);

            Assert.Equal(1, r.Deleted);
            Assert.False(Directory.Exists(Path.Combine(game, "Mods", "Bundle", "[CC] Sub")));

            var trash = StoragePaths.GameTrashDir(game);
            var kept = Directory.GetFiles(trash, "manifest.json", SearchOption.AllDirectories);
            Assert.Single(kept);
            Assert.Contains(Path.Combine("Bundle", "[CC] Sub"), kept[0]);
            // 子包删空后顶层空壳要跟着清掉，否则 Mods 里留一个 SMAPI 加载不了的空目录
            Assert.False(Directory.Exists(Path.Combine(game, "Mods", "Bundle")));
        }
        finally { TryKill(game); }
    }

    [Fact]
    public async Task Missing_Folder_Counts_As_Failure_Not_Crash()
    {
        var game = NewGame();
        try
        {
            var r = await ModDeleteService.DeleteAsync(game, ["没这个mod"], toTrash: false,
                cfg: null, progress: null, CancellationToken.None);

            Assert.Equal(0, r.Deleted);
            Assert.Equal(1, r.Failed);
            Assert.NotNull(r.FirstError);
        }
        finally { TryKill(game); }
    }

    [Fact]
    public async Task Cancel_Before_Start_Touches_Nothing()
    {
        var game = NewGame();
        try
        {
            var mod = Path.Combine(game, "Mods", "BigMod");
            for (var i = 0; i < 200; i++)
                WriteFile(Path.Combine(mod, "f" + i + ".bin"), 512);

            // v1.3.19：删除只剩"同盘改名"一个真实动作，所以可取消的窗口就是
            // "还没开始搬"。开跑前就取消 → 必须原样退回，一个字节都不动。
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var r = await ModDeleteService.DeleteAsync(game, ["BigMod"], toTrash: false,
                cfg: null, progress: null, ct: cts.Token);

            Assert.True(r.Canceled);
            Assert.Equal(0, r.Deleted);
            Assert.Equal(200, Directory.GetFiles(mod, "*.bin").Length);   // 一个都没少
        }
        finally { TryKill(game); }
    }

    [Fact]
    public async Task Progress_Reports_Each_Target()
    {
        var game = NewGame();
        try
        {
            var mod = Path.Combine(game, "Mods", "Sized");
            WriteFile(Path.Combine(mod, "a.bin"), 1000);
            WriteFile(Path.Combine(mod, "b.bin"), 2000);

            var seen = new List<DeleteTick>();
            var progress = new InlineProgress(seen.Add);

            await ModDeleteService.DeleteAsync(game, ["Sized"], toTrash: false,
                cfg: null, progress: progress, ct: CancellationToken.None);

            // v1.3.19：不再做字节统计（那是一次全盘枚举，正是删除"开跑前"最贵的部分）。
            // 现在每个目标搬完报一条，进度直接按条数走。
            Assert.NotEmpty(seen);
            Assert.Contains(seen, t => t.Percent == 100);
            Assert.Contains(seen, t => t.Message.Contains("Sized"));
        }
        finally { TryKill(game); }
    }

    [Fact]
    public async Task Orphan_Purge_Only_Reclaims_OnePass_Staging_Names()
    {
        var game = NewGame();
        try
        {
            var trash = StoragePaths.GameTrashDir(game);
            Directory.CreateDirectory(trash);

            // 上次被强杀留下的"一次性删除"落点：<名字>_<8位hex>
            var orphan = Path.Combine(trash, "PelipperTown.Mod_a467ef09");
            WriteFile(Path.Combine(orphan, "manifest.json"), 64);
            WriteFile(Path.Combine(orphan, "assets", "a.png"), 128);

            // 用户主动移进回收站的那条：<名字>_<yyyyMMdd_HHmmss> —— 绝不能碰
            var kept = Path.Combine(trash, "BetterStardewValley_20261104_042110");
            WriteFile(Path.Combine(kept, "manifest.json"), 64);

            // 判据里还有"10 分钟内刚建的不动"，这里把创建时间推到一小时前
            var old = DateTime.UtcNow.AddHours(-1);
            Directory.SetCreationTimeUtc(orphan, old);
            Directory.SetCreationTimeUtc(kept, old);

            ModDeleteService.PurgeOrphanedStaging(game);

            // 后台清理是队列，等它跑完
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Directory.Exists(orphan) && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.False(Directory.Exists(orphan));
            Assert.True(Directory.Exists(kept), "用户移入回收站的条目被误删了");
            Assert.True(File.Exists(Path.Combine(kept, "manifest.json")));
        }
        finally { TryKill(game); }
    }

    [Fact]
    public async Task Batch_Deletes_Every_Target()
    {
        var game = NewGame();
        try
        {
            foreach (var name in new[] { "A", "B", "C" })
                WriteFile(Path.Combine(game, "Mods", name, "manifest.json"), 16);

            var r = await ModDeleteService.DeleteAsync(game, ["A", "B", "C"], toTrash: false,
                cfg: null, progress: null, CancellationToken.None);

            Assert.Equal(3, r.Deleted);
            foreach (var name in new[] { "A", "B", "C" })
                Assert.False(Directory.Exists(Path.Combine(game, "Mods", name)));
        }
        finally { TryKill(game); }
    }

    /// <summary>禁用包在磁盘上的目录名末段带 "."，删除时必须认得出来（否则"删了还在"）。</summary>
    [Fact]
    public async Task Finds_Dot_Prefixed_Folder()
    {
        var game = NewGame();
        try
        {
            WriteFile(Path.Combine(game, "Mods", ".SleepyMod", "manifest.json"), 16);

            var r = await ModDeleteService.DeleteAsync(game, ["SleepyMod"], toTrash: false,
                cfg: null, progress: null, CancellationToken.None);

            Assert.Equal(1, r.Deleted);
            Assert.False(Directory.Exists(Path.Combine(game, "Mods", ".SleepyMod")));
        }
        finally { TryKill(game); }
    }

    /// <summary>
    /// v1.3.15：<b>调用点必须立刻返回</b>。
    /// <para>
    /// 现在的交互是"点删除 → 立刻 toast 一句 → 界面继续用"，没有进度弹窗了。
    /// 这条契约完全建立在 <see cref="ModDeleteService.DeleteAsync"/> 不在调用线程上
    /// 干活这一点上：它一旦退化成同步，Blazor 的 <c>@onclick</c> 就会被钉在
    /// <c>Directory.Move</c> / <c>File.Delete</c> 上 —— 界面照样冻住，只是这次冻在
    /// 删除而不是下载。
    /// </para>
    /// <para>
    /// 2000 个文件是有意选的量级：整棵树的实际删除在这台机器上要几百毫秒到一秒，
    /// 与 150ms 的判定线拉得开，不会因为 CI 抖动误报。真正同步的实现等不到这一行。
    /// </para>
    /// </summary>
    [Fact]
    public async Task DeleteAsync_Does_Not_Block_The_Caller_Thread()
    {
        var game = NewGame();
        try
        {
            var mod = Path.Combine(game, "Mods", "HugeMod");
            for (var i = 0; i < 2000; i++)
                WriteFile(Path.Combine(mod, "d" + (i % 40), "f" + i + ".bin"), 64);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var task = ModDeleteService.DeleteAsync(game, ["HugeMod"], toTrash: false,
                cfg: null, progress: null, CancellationToken.None);
            var callCost = sw.ElapsedMilliseconds;

            Assert.True(callCost < 150, $"DeleteAsync 的调用点阻塞了 {callCost}ms —— 它必须在后台跑");

            var r = await task;
            Assert.Equal(1, r.Deleted);
            Assert.False(r.AnyFailed);
            Assert.False(Directory.Exists(mod));
        }
        finally { TryKill(game); }
    }

    private static void TryKill(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
