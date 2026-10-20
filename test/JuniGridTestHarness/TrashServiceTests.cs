using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>回收站的列、还原、永久删除。
/// 目录名是唯一的状态载体，而历史上改过三次命名规则（下划线带时间、连字符带时间、
/// 下划线带 8 位 Guid），这批用例把三条规则和还原冲突都钉住。</summary>
public class TrashServiceTests : IDisposable
{
    private readonly string _game =
        Path.Combine(Path.GetTempPath(), "jg-trash-" + Guid.NewGuid().ToString("N")[..8]);

    private string ModsDir => Path.Combine(_game, "Mods");
    private string TrashDir => Path.Combine(ModsDir, ".junigrid_trash");

    public TrashServiceTests()
    {
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(TrashDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void MakeTrashEntry(string folder, string content = "x")
    {
        var d = Path.Combine(TrashDir, folder);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "manifest.json"), content);
    }

    // ── 名字解析：三条历史规则 ────────────────────────────────

    [Fact]
    public void Parse_UnderscoreWithTimestamp()
    {
        var (name, at) = TrashService.ParseFolderName("SDV-Radiance_20261016_012340");
        Assert.Equal("SDV-Radiance", name);
        Assert.Equal(new DateTime(2026, 10, 16, 1, 23, 40), at);
    }

    [Fact]
    public void Parse_HyphenWithTimestamp()
    {
        var (name, at) = TrashService.ParseFolderName("AlternativeTextures-20261016_071718");
        Assert.Equal("AlternativeTextures", name);
        Assert.NotNull(at);
    }

    [Fact]
    public void Parse_GuidSuffix_HasNoTime()
    {
        var (name, at) = TrashService.ParseFolderName("GrandpasToolbelt_b857ddcc");
        Assert.Equal("GrandpasToolbelt", name);
        Assert.Null(at);
    }

    [Fact]
    public void Parse_Unrecognized_KeepsWholeName()
    {
        var (name, at) = TrashService.ParseFolderName("SomeRandomFolder");
        Assert.Equal("SomeRandomFolder", name);
        Assert.Null(at);
    }

    [Fact]
    public void Parse_NameWithUnderscoreInside_OnlyStripsTrailingStamp()
    {
        // 原名本身就带下划线 —— 只剥末尾的 _yyyyMMdd_HHmmss
        var (name, _) = TrashService.ParseFolderName("My_Mod_20261016_120000");
        Assert.Equal("My_Mod", name);
    }

    // ── 列目录 ────────────────────────────────────────────────

    [Fact]
    public void List_EmptyTrash_ReturnsEmpty()
    {
        Assert.Empty(TrashService.List(_game));
    }

    [Fact]
    public void List_NoTrashDir_ReturnsEmpty_DoesNotThrow()
    {
        Directory.Delete(TrashDir, true);
        Assert.Empty(TrashService.List(_game));
    }

    [Fact]
    public void List_ReportsSizeAndFileCount()
    {
        MakeTrashEntry("A_20261016_120000", new string('x', 100));
        var item = Assert.Single(TrashService.List(_game));
        Assert.Equal("A", item.DisplayName);
        Assert.Equal(1, item.FileCount);
        Assert.True(item.Bytes >= 100);
    }

    [Fact]
    public void List_FlagsEntryThatStillExistsInMods()
    {
        MakeTrashEntry("Dup_20261016_120000");
        Directory.CreateDirectory(Path.Combine(ModsDir, "Dup"));

        var item = Assert.Single(TrashService.List(_game));
        Assert.True(item.AlsoInMods);
        Assert.True(item.Conflicts);
    }

    [Fact]
    public void List_ReadsNexusIdFromSidecar()
    {
        MakeTrashEntry("WithId_20261016_120000");
        File.WriteAllText(Path.Combine(TrashDir, "WithId_20261016_120000", ".junigrid.json"),
            "{\"nexusModId\":49397}");

        var item = Assert.Single(TrashService.List(_game));
        Assert.Equal(49397, item.NexusModId);
    }

    // ── 还原 ──────────────────────────────────────────────────

    [Fact]
    public void Restore_MovesBackUnderOriginalName()
    {
        MakeTrashEntry("ModA_20261016_120000");
        var item = Assert.Single(TrashService.List(_game));

        Assert.Null(TrashService.Restore(_game, item, null, overwrite: false));
        Assert.True(Directory.Exists(Path.Combine(ModsDir, "ModA")));
        Assert.False(Directory.Exists(Path.Combine(TrashDir, "ModA_20261016_120000")));
    }

    [Fact]
    public void Restore_ConflictWithoutOverwrite_RefusesAndLeavesBoth()
    {
        MakeTrashEntry("Dup_20261016_120000");
        Directory.CreateDirectory(Path.Combine(ModsDir, "Dup"));
        var item = Assert.Single(TrashService.List(_game));

        Assert.NotNull(TrashService.Restore(_game, item, null, overwrite: false));
        Assert.True(Directory.Exists(Path.Combine(ModsDir, "Dup")));
        Assert.True(Directory.Exists(Path.Combine(TrashDir, "Dup_20261016_120000")));
    }

    [Fact]
    public void Restore_ConflictWithOverwrite_KeepsBothSides()
    {
        // Mods 里现有的那份要被挪进回收站留底，而不是被删掉
        MakeTrashEntry("Dup_20261016_120000", "OLD");
        var liveDir = Path.Combine(ModsDir, "Dup");
        Directory.CreateDirectory(liveDir);
        File.WriteAllText(Path.Combine(liveDir, "manifest.json"), "NEW");
        var item = Assert.Single(TrashService.List(_game));

        Assert.Null(TrashService.Restore(_game, item, null, overwrite: true));

        Assert.Equal("OLD", File.ReadAllText(Path.Combine(ModsDir, "Dup", "manifest.json")));
        // 原来 Mods 里那份现在躺在回收站，内容还是 NEW
        var after = TrashService.List(_game);
        var parked = Assert.Single(after);
        Assert.Equal("Dup", parked.DisplayName);
        Assert.Equal("NEW", File.ReadAllText(
            Path.Combine(TrashDir, parked.FolderName, "manifest.json")));
    }

    [Fact]
    public void Restore_UsesCallerSuppliedName()
    {
        MakeTrashEntry("Weird_20261016_120000");
        var item = Assert.Single(TrashService.List(_game));

        Assert.Null(TrashService.Restore(_game, item, "Correct Name", overwrite: false));
        Assert.True(Directory.Exists(Path.Combine(ModsDir, "Correct Name")));
    }

    [Fact]
    public void Restore_MissingEntry_ReturnsError()
    {
        var item = new TrashItem("Gone_20261016_120000", "Gone", null, 0, 0, null, false, "Gone_20261016_120000");
        Assert.NotNull(TrashService.Restore(_game, item, null, overwrite: false));
    }

    // ── 永久删除 ──────────────────────────────────────────────

    [Fact]
    public void DeletePermanently_RemovesDirectory()
    {
        MakeTrashEntry("Dead_20261016_120000");
        var item = Assert.Single(TrashService.List(_game));

        Assert.Null(TrashService.DeletePermanently(_game, item));
        Assert.Empty(TrashService.List(_game));
    }

    [Fact]
    public void DeletePermanently_AlreadyGone_IsNoOp()
    {
        var item = new TrashItem("Nope_20261016_120000", "Nope", null, 0, 0, null, false, "Nope_20261016_120000");
        Assert.Null(TrashService.DeletePermanently(_game, item));
    }

    [Fact]
    public void BatchDelete_CountsFailures()
    {
        MakeTrashEntry("A_20261016_120000");
        MakeTrashEntry("B_20261016_120000");
        var items = TrashService.List(_game);
        Assert.Equal(2, items.Count);

        Assert.Equal(0, TrashService.DeletePermanently(_game, items));
        Assert.Empty(TrashService.List(_game));
    }
}
