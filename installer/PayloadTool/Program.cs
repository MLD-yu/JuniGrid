// JuniGrid 安装包打包工具：
//   dotnet run -c Release -- c <publish目录> <输出文件>   打包（JGP1 容器 + 单流 LZMA 固实压缩）
//   dotnet run -c Release -- x <payload文件> <输出目录>   解包（与 InstallerEngine 相同的读取方式）
// 压缩率比原 payload.zip 的逐文件 Deflate 高约 25~30%，打包后自动全量校验。
//
// JGP1 容器布局：
//   4 字节魔数 "JGP1" | int32 条目数 | 条目表{ byte kind(0=文件), uint16 路径长, UTF-8 路径, int64 大小 } |
//   5 字节 LZMA 属性 + LZMA 流（所有文件内容按条目顺序拼接，固实压缩，流尾带结束标记）
// 解压端（JuniGridInstaller.InstallerEngine）按同一布局读取。

using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpCompress.Compressors.LZMA;

if (args.Length != 3 || (args[0] != "c" && args[0] != "x"))
{
    Console.Error.WriteLine($"用法: PayloadTool c <publish目录> <输出文件> | x <payload文件> <输出目录>（实际收到 {args.Length} 个参数: [{string.Join(" | ", args)}]）");
    return 1;
}
if (args[0] == "x")
{
    Extract(args[1], args[2]);
    Console.WriteLine($"解包完成: {args[2]}");
    return 0;
}

var inputDir = Path.GetFullPath(args[1]);
var outputFile = Path.GetFullPath(args[2]);
if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"目录不存在: {inputDir}");
    return 1;
}

// 终端用户用不到的构建副产物：
//  - *.pdb / *.map —— 调试符号与前端 sourcemap
//  - Microsoft.DiaSymReader.Native.* / mscordaccore* / mscordbi —— 仅调试器/转储分析需要，
//    已随 DebugType=none 不生成 pdb，运行时功能不受影响
string[] ExcludedFileNames =
[
    "Microsoft.DiaSymReader.Native.amd64.dll",
    "mscordbi.dll",
];
Func<string, bool> IsExcluded = path =>
    ExcludedFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
    || Path.GetFileName(path).StartsWith("mscordaccore", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".map", StringComparison.OrdinalIgnoreCase);

var files = Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories)
    .Where(f => !IsExcluded(f))
    // 按扩展名+路径排序：同类内容相邻，固实 LZMA 的去重窗口利用率最高
    .OrderBy(Path.GetExtension, StringComparer.OrdinalIgnoreCase)
    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
    .Select(f => (full: f, rel: Path.GetRelativePath(inputDir, f).Replace('\\', '/'), size: new FileInfo(f).Length))
    .ToList();

long totalRaw = files.Sum(f => f.size);
Console.WriteLine($"条目: {files.Count} 个文件, 原始 {totalRaw / 1048576.0:N1} MB");

// ---- 压缩 ----
var sw = System.Diagnostics.Stopwatch.StartNew();
Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
using (var outStream = File.Create(outputFile))
{
    Span<byte> header = stackalloc byte[8];
    "JGP1"u8.CopyTo(header);
    BinaryPrimitives.WriteInt32LittleEndian(header[4..], files.Count);
    outStream.Write(header);

    Span<byte> entry = stackalloc byte[3 + sizeof(long)];
    foreach (var f in files)
    {
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(f.rel);
        if (pathBytes.Length > ushort.MaxValue) throw new IOException($"路径过长: {f.rel}");
        entry[0] = 0; // kind = 文件
        BinaryPrimitives.WriteUInt16LittleEndian(entry[1..3], (ushort)pathBytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(entry[3..], f.size);
        outStream.Write(entry);
        outStream.Write(pathBytes);
    }

    // eos=true：LZMA SDK 在编码端大小未知的模式下本就会写结束标记，
    // 显式声明并让解码端「读到结束标记为止」，两端语义才一致
    var props = new LzmaEncoderProperties(eos: true, dictionary: 1 << 26, numFastBytes: 273);
    var lzma = LzmaStream.Create(props, isLzma2: false, outStream);
    // SharpCompress 不会替你写 5 字节属性头，必须由调用者先写入（解码端按它建流）
    outStream.Write(lzma.Properties);
    long done = 0, lastReport = 0;
    var buf = new byte[1 << 20];
    foreach (var f in files)
    {
        using var src = File.OpenRead(f.full);
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            lzma.Write(buf, 0, n);
            done += n;
            if (done - lastReport >= 16L * 1024 * 1024)
            {
                lastReport = done;
                Console.WriteLine($"  压缩中 {done / 1048576.0:N0}/{totalRaw / 1048576.0:N0} MB");
            }
        }
    }
    // 必须在 outStream 结束前完成 LZMA 流（写出尾部分节）
    lzma.Dispose();
}
sw.Stop();
var packed = new FileInfo(outputFile).Length;
Console.WriteLine($"压缩完成: {packed / 1048576.0:N1} MB ({100.0 * packed / totalRaw:N1}%), 耗时 {sw.Elapsed.TotalMinutes:N1} 分钟");

// ---- 校验：全量解压并与源文件逐个比对 SHA-256 ----
Console.WriteLine("校验中（全量解压比对哈希）…");
using (var container = File.OpenRead(outputFile))
{
    Span<byte> head = stackalloc byte[8];
    container.ReadExactly(head);
    if (!head[..4].SequenceEqual("JGP1"u8)) throw new IOException("输出文件魔数不符");
    int count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
    if (count != files.Count) throw new IOException("条目数不符");

    var entries = new List<(string rel, long size)>(count);
    Span<byte> entry = stackalloc byte[3 + sizeof(long)];
    for (int i = 0; i < count; i++)
    {
        container.ReadExactly(entry);
        if (entry[0] != 0) throw new IOException("未知条目类型");
        var pathBytes = new byte[BinaryPrimitives.ReadUInt16LittleEndian(entry[1..3])];
        container.ReadExactly(pathBytes);
        entries.Add((System.Text.Encoding.UTF8.GetString(pathBytes), BinaryPrimitives.ReadInt64LittleEndian(entry[3..])));
    }

    var props = new byte[5];
    container.ReadExactly(props);
    // 不传 outputSize：解码在 LZMA 结束标记处自然终止（编码端未知大小模式必带标记）
    using var lzma = LzmaStream.Create(props, container, leaveOpen: true);

    var hashBuf = new byte[1 << 20];
    foreach (var (rel, size) in entries)
    {
        var srcPath = Path.Combine(inputDir, rel);
        if (new FileInfo(srcPath).Length != size) throw new IOException($"大小不符: {rel}");
        using var src = File.OpenRead(srcPath);
        using var shaA = SHA256.Create();
        using var shaB = SHA256.Create();
        var buf = hashBuf;
        long remaining = size;
        while (remaining > 0)
        {
            int n = lzma.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) throw new IOException($"LZMA 流提前结束: {rel}");
            shaA.TransformBlock(buf, 0, n, null, 0);
            int m = src.Read(buf, 0, n);
            if (m != n) throw new IOException($"源文件读取不足: {rel}");
            shaB.TransformBlock(buf, 0, m, null, 0);
            remaining -= n;
        }
        shaA.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        shaB.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (!shaA.Hash!.AsSpan().SequenceEqual(shaB.Hash!))
            throw new IOException($"哈希不符: {rel}");
    }
    Console.WriteLine("校验通过 ✓");
}

Console.WriteLine($"完成: {outputFile}");
return 0;

// 解包：读取方式与 JuniGridInstaller.InstallerEngine 逐行对应，用于离线验证安装端解压路径
static void Extract(string payloadPath, string outDir)
{
    using var stream = File.OpenRead(payloadPath);
    var entries = ReadHeader(stream);

    var props = new byte[5];
    stream.ReadExactly(props);
    using var lzma = LzmaStream.Create(props, stream, leaveOpen: true);

    Directory.CreateDirectory(outDir);
    var buf = new byte[1 << 16];
    foreach (var (rel, size) in entries)
    {
        var dest = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var dst = File.Create(dest);
        long remaining = size;
        while (remaining > 0)
        {
            int n = lzma.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) throw new IOException("LZMA 流提前结束：" + rel);
            dst.Write(buf, 0, n);
            remaining -= n;
        }
    }
}

static List<(string rel, long size)> ReadHeader(Stream stream)
{
    Span<byte> head = stackalloc byte[8];
    stream.ReadExactly(head);
    if (!head[..4].SequenceEqual("JGP1"u8))
        throw new IOException("不是 JGP1 容器");
    int count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
    var entries = new List<(string, long)>(count);
    Span<byte> entry = stackalloc byte[3 + sizeof(long)];
    for (int i = 0; i < count; i++)
    {
        stream.ReadExactly(entry);
        if (entry[0] != 0) throw new IOException("未知条目类型");
        var pathBytes = new byte[BinaryPrimitives.ReadUInt16LittleEndian(entry[1..3])];
        stream.ReadExactly(pathBytes);
        entries.Add((System.Text.Encoding.UTF8.GetString(pathBytes), BinaryPrimitives.ReadInt64LittleEndian(entry[3..])));
    }
    return entries;
}
