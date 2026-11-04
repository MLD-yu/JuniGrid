using System.IO.Compression;
using System.Text;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 解压的两条路：条目少走库（<see cref="ZipFile.ExtractToDirectory(string, string, Encoding)"/>），
/// 条目多走 <see cref="ModService.ExtractZip"/> 的「预建目录树 + 多线程分片」。
/// <para>起因（实机 2026-11-04）：50344「Pelipper Town」13,545 个条目 / 100 MB，
/// 单线程整包解压实测 16.8 秒，而界面上只有一个不动的「正在安装到 Mods…」。
/// 提速的同时有三条不能破的契约，在这里钉住：条目一条不落（分片取模最容易漏尾段）、
/// zip-slip 条目绝不落到 destDir 之外、包内 mtime 保留。
/// （GBK 条目名的解码由 <see cref="GbkEncodingTests"/> 钉着：两条路走的是同一个
/// <see cref="ZipFile"/> 入口、同一份 encoding，不存在只在大包上乱码的可能。）</para>
/// </summary>
public class ZipFastExtractTests
{
    const int Threshold = ModService.ZipParallelMinEntries;

    [Fact]
    public void Small_Zip_Still_Extracts()
    {
        using var fx = new Fixture();
        fx.Add("pack/manifest.json", "{}");
        fx.Add("pack/readme.md", "hello");
        fx.Build();

        ModService.ExtractZip(fx.ZipPath, fx.OutDir);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(fx.OutDir, "pack", "manifest.json")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fx.OutDir, "pack", "readme.md")));
    }

    /// <summary>过了并行阈值之后每一条都必须落盘 —— 分片按 <c>i += lanes</c> 取，
    /// 条数与线程数不整除时尾段最容易漏。</summary>
    [Fact]
    public void Large_Zip_Extracts_Every_Entry()
    {
        using var fx = new Fixture();
        const int n = Threshold + 37;                 // 故意不整除
        for (var i = 0; i < n; i++)
            fx.Add($"PelipperTown.Mod/assets/{i % 7}/file_{i:0000}.json", i.ToString());
        fx.Build();

        ModService.ExtractZip(fx.ZipPath, fx.OutDir);

        Assert.Equal(n, Directory.GetFiles(fx.OutDir, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("0", File.ReadAllText(Path.Combine(fx.OutDir, "PelipperTown.Mod/assets/0/file_0000.json")));
        Assert.Equal((n - 1).ToString(), File.ReadAllText(
            Path.Combine(fx.OutDir, $"PelipperTown.Mod/assets/{(n - 1) % 7}/file_{(n - 1):0000}.json")));
    }

    /// <summary>非 ASCII 条目名（作者用中文写目录名）在并行分支里必须原样保留 ——
    /// 那个名字会被当成覆盖宿主写进账，乱码了就永远对不上已装的 mod。</summary>
    [Fact]
    public void Large_Zip_Keeps_Unicode_Entry_Names()
    {
        using var fx = new Fixture();
        for (var i = 0; i < Threshold; i++)
            fx.Add($"[CP] 说明/{i:0000}.png", "x");
        fx.Build();

        ModService.ExtractZip(fx.ZipPath, fx.OutDir);

        Assert.True(Directory.Exists(Path.Combine(fx.OutDir, "[CP] 说明")), "中文目录名应原样保留");
        Assert.True(File.Exists(Path.Combine(fx.OutDir, "[CP] 说明", "0000.png")));
    }

    /// <summary>zip-slip：带 <c>..</c> 的条目一条都不能写到 destDir 外面去。</summary>
    [Fact]
    public void Large_Zip_Skips_ZipSlip_Entries()
    {
        using var fx = new Fixture();
        for (var i = 0; i < Threshold; i++)
            fx.Add($"ok/{i:0000}.txt", "x");
        fx.Add("../escaped.txt", "pwned");
        fx.Add("..\\escaped2.txt", "pwned");
        fx.Build();

        ModService.ExtractZip(fx.ZipPath, fx.OutDir);

        Assert.False(File.Exists(Path.Combine(fx.Root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(fx.Root, "escaped2.txt")));
        Assert.Equal(Threshold, Directory.GetFiles(fx.OutDir, "*", SearchOption.AllDirectories).Length);
    }

    /// <summary>包内时间戳要留下来（有工具按 mtime 判新旧，库的实现也保留）。</summary>
    [Fact]
    public void Large_Zip_Preserves_Entry_Timestamp()
    {
        using var fx = new Fixture();
        for (var i = 0; i < Threshold; i++)
            fx.Add($"m/{i:0000}.txt", "x", new DateTime(2019, 3, 4, 5, 6, 8));
        fx.Build();

        ModService.ExtractZip(fx.ZipPath, fx.OutDir);

        var one = Directory.GetFiles(fx.OutDir, "*.txt", SearchOption.AllDirectories)[0];
        Assert.Equal(2019, File.GetLastWriteTime(one).Year);
    }

    /// <summary>临时目录 + 一次性写好的 zip（先收集条目再 Build —— 反复以 Update 模式
    /// 打开会每写一条就重写一次中央目录，几百条就慢到不能当测试）。</summary>
    sealed class Fixture : IDisposable
    {
        readonly List<(string Name, string Text, DateTime? Stamp)> _entries = new();
        public string Root { get; }
        public string ZipPath { get; }
        public string OutDir { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "jg-zipfx-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
            ZipPath = Path.Combine(Root, "pack.zip");
            OutDir = Path.Combine(Root, "out");
            Directory.CreateDirectory(OutDir);
        }

        public void Add(string name, string text, DateTime? stamp = null)
            => _entries.Add((name, text, stamp));

        public void Build()
        {
            using var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Create);
            foreach (var (name, text, stamp) in _entries)
            {
                var e = zip.CreateEntry(name);
                if (stamp is { } s) e.LastWriteTime = s;
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
