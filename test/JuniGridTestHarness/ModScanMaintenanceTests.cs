using JuniGrid.Services;

namespace JuniGridTestHarness;

public sealed class ModScanMaintenanceTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "jg-scan-maint-" + Guid.NewGuid().ToString("N"));
    private readonly ModService _mods = new();

    public ModScanMaintenanceTests() => Directory.CreateDirectory(Path.Combine(_game, "Mods"));

    public void Dispose() { try { Directory.Delete(_game, true); } catch { } }

    [Fact]
    public void CleanupIsSkippedUntilTheTopLevelTreeChangesThenRunsAgain()
    {
        CreateMod("A", "Example.Shared");
        Assert.Equal(0, _mods.CleanupDuplicateCopiesIfChanged(_game));
        Assert.Equal(0, _mods.CleanupDuplicateCopiesIfChanged(_game));

        CreateMod("B", "Example.Shared");

        Assert.Equal(1, _mods.CleanupDuplicateCopiesIfChanged(_game));
        Assert.True(Directory.Exists(Path.Combine(_game, "Mods", "A")));
        Assert.False(Directory.Exists(Path.Combine(_game, "Mods", "B")));
        Assert.True(Directory.Exists(TrashService.TrashRoot(_game)));
    }

    private void CreateMod(string folder, string uid)
    {
        var dir = Path.Combine(_game, "Mods", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            $$"""{"Name":"{{folder}}","Version":"1.0.0","UniqueID":"{{uid}}"}""");
    }
}
