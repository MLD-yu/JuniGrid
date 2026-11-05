using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using JuniGrid.Manifest;

namespace JuniGrid.ManifestTool;

/// <summary>
/// 发版时的清单签名工具（离线跑，私钥不在这台机器以外的任何地方）。
///
/// <para>四个子命令：
/// <list type="bullet">
///   <item><c>keygen</c> —— 生成密钥对。私钥落到你指定的目录（务必放仓库外），公钥打印出来，
///   贴进 <c>JuniGrid.Manifest/ManifestTrust.cs</c> 的 <c>PublicKeySpkiBase64</c>。</item>
///   <item><c>make</c> —— 给一个 setup.exe 生成清单并签名：算 sha256/大小、写 latest.json、
///   出 latest.json.sig。这两个文件就是要发布到静态托管上的东西。</item>
///   <item><c>sign</c> —— 只签一份已有的 latest.json。</item>
///   <item><c>verify</c> —— 用内置公钥验一份清单+签名（发布前自检）。</item>
/// </list>
///
/// <para>用法示例：
/// <code>
/// dotnet run --project tools/JuniGrid.ManifestSigner -- keygen --out E:\junigrid\keys
/// dotnet run --project tools/JuniGrid.ManifestSigner -- make --exe dist-tmp\JuniGrid-cn-v1.3.1-setup.exe --version 1.3.1 --outdir dist-tmp --key %USERPROFILE%\.junigrid\keys\junigrid-manifest-2026-01.pem
/// dotnet run --project tools/JuniGrid.ManifestSigner -- verify --manifest dist-tmp\latest.json --sig dist-tmp\latest.json.sig
/// </code>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Usage(); return 2; }
            var cmd = args[0].ToLowerInvariant();
            var opt = Parse(args.Skip(1));
            switch (cmd)
            {
                case "keygen": return Keygen(opt);
                case "pubkey": return PubKey(opt);
                case "make": return Make(opt);
                case "sign": return Sign(opt);
                case "verify": return Verify(opt);
                default: Usage(); return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败：" + ex.Message);
            return 1;
        }
    }

    private static void Usage()
    {
        Console.Error.WriteLine("""
            JuniGrid 清单签名工具

              keygen --out <目录>
                生成密钥对。打印公钥（贴进 ManifestTrust.PublicKeySpkiBase64）。

              make --exe <setup.exe> --version <版本> --outdir <目录> [--url <下载地址>] --key <私钥pem>
                生成 latest.json（sha256/大小/版本/地址）并签名出 latest.json.sig。

              sign --manifest <latest.json> --key <私钥pem> --out <latest.json.sig>
                只签名。

              verify --manifest <latest.json> --sig <latest.json.sig>
                用内置公钥验签（发布前自检）。
            """);
    }

    // ── keygen ────────────────────────────────────────────────────────────────
    private static int Keygen(Dictionary<string, string> opt)
    {
        var dir = opt.GetValueOrDefault("out") ?? ".";
        Directory.CreateDirectory(dir);
        var (pem, pub) = ManifestSigner.GenerateKeyPair();

        var path = Path.Combine(dir, $"junigrid-manifest-{ManifestTrust.KeyId}.pem");
        File.WriteAllText(path, pem);
        try
        {
            // Windows 上没有 POSIX 权限位，但至少别让别的用户随便读（同一台机器上的其他账号）
            if (OperatingSystem.IsWindows())
            {
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().Name,
                    FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(acl);
            }
        }
        catch { /* 权限收紧失败不算错，但要说一句 */ Console.Error.WriteLine("（没能收紧私钥文件的 ACL，自己看着办）"); }

        Console.WriteLine($"私钥已写入：{path}");
        Console.Error.WriteLine("⚠ 私钥绝不要进仓库、绝不要贴进聊天记录、绝不要放在会被同步的网盘里。离线备份两份。");
        Console.WriteLine();
        Console.WriteLine("把下面这行贴进 JuniGrid.Manifest/ManifestTrust.cs 的 PublicKeySpkiBase64：");
        Console.WriteLine();
        Console.WriteLine($"    public const string PublicKeySpkiBase64 = \"{pub}\";");
        return 0;
    }

    // ── pubkey ────────────────────────────────────────────────────────────────
    private static int PubKey(Dictionary<string, string> opt)
    {
        var keyPath = opt.GetValueOrDefault("key") ?? throw new ArgumentException("缺少 --key");
        var pub = ManifestSigner.PublicKeyFromPem(File.ReadAllText(keyPath));
        Console.WriteLine(pub);   // 单行，不折行 —— 贴进代码时不会被抄错
        return 0;
    }

    // ── make ──────────────────────────────────────────────────────────────────
    private static int Make(Dictionary<string, string> opt)
    {
        var exe = opt.GetValueOrDefault("exe") ?? throw new ArgumentException("缺少 --exe");
        var version = opt.GetValueOrDefault("version") ?? throw new ArgumentException("缺少 --version");
        var outDir = opt.GetValueOrDefault("outdir") ?? Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".";
        var keyPath = opt.GetValueOrDefault("key") ?? throw new ArgumentException("缺少 --key");
        var url = opt.GetValueOrDefault("url")
            ?? $"https://github.com/MLD-yu/JuniGrid/releases/download/v{version.TrimStart('v', 'V')}/JuniGrid-cn-v{version.TrimStart('v', 'V')}-setup.exe";

        Directory.CreateDirectory(outDir);
        var info = new FileInfo(exe);
        if (!info.Exists) throw new FileNotFoundException("找不到安装包", exe);

        using var fs = info.OpenRead();
        var sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();

        var doc = new ManifestDocument
        {
            Schema = 1,
            Version = version.Trim().TrimStart('v', 'V'),
            Sha256 = sha,
            Size = info.Length,
            Url = url,
            PublishedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            KeyId = ManifestTrust.KeyId,
        };
        var bytes = ManifestDocument.Serialize(doc);
        var sig = ManifestSigner.Sign(bytes, File.ReadAllText(keyPath));

        var jsonPath = Path.Combine(outDir, "latest.json");
        var sigPath = Path.Combine(outDir, "latest.json.sig");
        File.WriteAllBytes(jsonPath, bytes);
        // 签名文件写成 base64 文本：文本在任何托管（Pages/raw/镜像/git）里都不会被
        // 换行转换或编码猜测毁掉。解码时空白会被忽略，带不带尾换行都行。
        File.WriteAllText(sigPath, Convert.ToBase64String(sig) + "\n", new UTF8Encoding(false));

        Console.WriteLine($"版本    : {doc.Version}");
        Console.WriteLine($"大小    : {doc.Size} bytes");
        Console.WriteLine($"SHA-256 : {doc.Sha256}");
        Console.WriteLine($"地址    : {doc.Url}");
        Console.WriteLine($"密钥    : {doc.KeyId}");
        Console.WriteLine();
        Console.WriteLine($"清单    : {jsonPath}");
        Console.WriteLine($"签名    : {sigPath}");
        Console.WriteLine();
        Console.WriteLine("把这两个文件传到静态托管（GitHub Pages / EdgeOne Pages …）的同一个目录下。");
        Console.WriteLine("签名文件是 base64 文本（88 字符 + 换行）—— 用文本模式上传，不会被换行转换毁掉。");
        return 0;
    }

    // ── sign ──────────────────────────────────────────────────────────────────
    private static int Sign(Dictionary<string, string> opt)
    {
        var manifest = opt.GetValueOrDefault("manifest") ?? throw new ArgumentException("缺少 --manifest");
        var keyPath = opt.GetValueOrDefault("key") ?? throw new ArgumentException("缺少 --key");
        var outPath = opt.GetValueOrDefault("out") ?? manifest + ".sig";
        var bytes = File.ReadAllBytes(manifest);
        var sig = ManifestSigner.Sign(bytes, File.ReadAllText(keyPath));
        File.WriteAllText(outPath, Convert.ToBase64String(sig) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"已签名：{outPath}（base64 文本）");
        return 0;
    }

    // ── verify ────────────────────────────────────────────────────────────────
    private static int Verify(Dictionary<string, string> opt)
    {
        var manifest = opt.GetValueOrDefault("manifest") ?? throw new ArgumentException("缺少 --manifest");
        var sigPath = opt.GetValueOrDefault("sig") ?? throw new ArgumentException("缺少 --sig");
        var bytes = File.ReadAllBytes(manifest);
        var sigText = File.ReadAllText(sigPath);
        if (!ManifestTrust.VerifyBase64(bytes, sigText, out var keyId))
        {
            Console.Error.WriteLine("✗ 验签不过 —— 别发布");
            return 1;
        }
        var doc = ManifestDocument.Parse(bytes);
        Console.WriteLine($"✓ 验签通过（密钥 {keyId}）");
        if (doc is not null)
            Console.WriteLine($"  版本 {doc.Version} / {doc.Size} 字节 / sha256 {doc.Sha256}");
        return 0;
    }

    // ── 参数解析（--key value 与 --key=value 都收）─────────────────────────────
    private static Dictionary<string, string> Parse(IEnumerable<string> args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) d[pending] = "";
                var body = a[2..];
                var eq = body.IndexOf('=');
                if (eq > 0) { d[body[..eq]] = body[(eq + 1)..]; pending = null; }
                else { pending = body; d[body] = ""; }
            }
            else if (pending is not null) { d[pending] = a; pending = null; }
        }
        return d;
    }
}
