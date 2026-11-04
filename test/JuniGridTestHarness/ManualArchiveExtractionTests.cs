using System.IO.Compression;
using JuniGrid.Services;

namespace JuniGridTestHarness;

public sealed class ManualArchiveExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-manual-extract-" + Guid.NewGuid().ToString("N"));

    public ManualArchiveExtractionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void ZipContentsAreExtractedToTheChosenFolder()
    {
        var archive = Path.Combine(_root, "pack.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Example/manifest.json").Open()))
            writer.Write("{\"Name\":\"Example\"}");

        var destination = Path.Combine(_root, "chosen");
        ModService.ExtractArchiveToDirectory(archive, destination);

        Assert.Equal("{\"Name\":\"Example\"}", File.ReadAllText(Path.Combine(destination, "Example", "manifest.json")));
    }

    [Fact]
    public void UnsupportedFileIsRejected()
    {
        var archive = Path.Combine(_root, "not-an-archive.zip");
        File.WriteAllText(archive, "not a zip");

        Assert.Throws<InvalidOperationException>(() => ModService.ExtractArchiveToDirectory(archive, Path.Combine(_root, "out")));
    }

    [Fact]
    public void ZipSlipEntryCannotWriteOutsideTheChosenFolder()
    {
        var archive = Path.Combine(_root, "unsafe.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open()))
            writer.Write("outside");
        var destination = Path.Combine(_root, "chosen");

        Assert.NotNull(Record.Exception(() => ModService.ExtractArchiveToDirectory(archive, destination)));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }
}
