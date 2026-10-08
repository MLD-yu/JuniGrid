using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 「裸素材包 → Content Patcher 包」的第二条路：按【包内目录名】认目标。
/// 这些包没有 manifest，但作者已经把要改哪个资产写在了目录名上 ——
/// <c>LooseSprites.png/Cursors.png</c> 就是「把 LooseSprites/Cursors 换成这张图」。
/// 同一种约定在作者自己发布的 CP 包里长成 <c>FromFile: "assets/{{Target}}.png"</c>，
/// 所以转换只是把 <c>X.png</c> 这个目录名里的点去掉、文件名剩下的部分当资产名，不需要猜。
/// （旧版还有一条「包根直接躺 Emily.png」的肖像转换器，随立绘页一起移除：
/// 那种包现在落到后面的本体覆盖链，或直接作为普通文件夹由素材包页处理。）
/// </summary>
public sealed partial class ModService
{
    /// <summary>转出来的包用这个 UID 前缀。必须与 <c>JuniGrid.PortraitPack.</c> 错开 ——
    /// 肖像包的自愈按那个前缀过滤并会用【立绘命名规则】重算 content.json，
    /// 把 UI 包喂进去会把 LooseSprites/* 的目标改写成 Portraits/*。</summary>
    public const string AssetPackUidPrefix = "JuniGrid.AssetPack.";

    private static readonly Regex TargetInContentJson = new(
        "\"Target\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>从包内相对路径列出所有<b>候选</b>目标资产名，由调用方拿"游戏里到底有没有这个资产"去挑。
    /// 认两种流传写法：① 目录名就是资产路径（<c>LooseSprites/Cursors.xnb</c>，Content Akatcher 时代
    /// 的标准布局，实测最常见）；② 目标写在目录名的扩展名上（<c>LooseSprites.png/Cursors.png</c>）。
    /// 从最短的后缀开始排（外层套的往往是包名目录，不是资产路径）。
    /// ⚠ 别拿转换产物的目录名反推作者约定 —— 立绘那条转换器会把 XNB 源的每段"去扩展名再加 .png"
    /// （ModService.cs 的 XNB 解码分支），所以 <c>LooseSprites/xxx.xnb</c> 出来会长成
    /// <c>assets/LooseSprites.png/xxx.png</c>，那是我们自己的命名。</summary>
    internal static List<string> CandidateAssetTargets(string relPath)
    {
        var segs = relPath.Replace('\\', '/').Split('/');
        var list = new List<string>();
        for (var i = segs.Length - 2; i >= 0; i--)
        {
            var parts = new List<string>();
            for (var j = i; j < segs.Length; j++) parts.Add(StripAssetExt(segs[j]));
            var t = string.Join('/', parts);
            if (t.Length > 0 && !list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
        }
        return list;
    }

    /// <summary>只剥掉表示"这是个资产目录/资产文件"的那一种扩展名（.png/.xnb），别的名字一律原样。</summary>
    private static string StripAssetExt(string seg)
    {
        var ext = Path.GetExtension(seg);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase))
            return seg[..^ext.Length];
        return seg;
    }

    /// <summary>挑出真正存在的那个目标；一个都不存在返回 null（调用方据此决定不接管）。</summary>
    /// <summary><paramref name="declared"/> = 调用方在本轮开始前取一次的「已装 mod 声明的资产名」表。
    /// 不传就自己取一次 —— 取那张表要先扫一遍 Mods 算签名（实测 0.18 秒），逐文件自己取就是
    /// 每个候选 0.18 秒（2026-10-07 实测：一个 256 张图的包在「正在安装到 Mods…」里跑了 4 分半
    /// 才落到一句"拒绝安装"，而那 4 分半全花在重扫 Mods 上）。</summary>
    internal static string? ResolveAssetTarget(string gamePath, string relPath, HashSet<string>? declared = null)
    {
        foreach (var t in CandidateAssetTargets(relPath))
            if (AssetExists(gamePath, t, declared)) return t;
        return null;
    }

    /// <summary>包里是否有【目标真实存在、而且不是立绘家族】的图片 ⇒ 该走这条按目录名转换的路。
    /// 判据带上"存在性"是故意的：裸立绘包里的 <c>Abigail/Abigail.png</c> 也能推出候选名，
    /// 但游戏里没有 <c>Abigail/Abigail</c> 这个资产，所以不算外来目标，照旧走立绘那条。</summary>
    internal static bool HasForeignAssetTarget(string gamePath, string dir)
    {
        try
        {
            var declared = DeclaredAssetNames(gamePath);   // 整趟只取一次（见 ResolveAssetTarget 的注释）
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f);
                if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase)) continue;
                var target = ResolveAssetTarget(gamePath, Path.GetRelativePath(dir, f).Replace('\\', '/'), declared);
                if (target is not null && !IsPortraitFamilyTarget(target)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>立绘家族的目标（钉在 Portraits/ 或 Characters/ 下）—— 这类包交回原来那条
    /// 肖像转换，保持已有 UID 与立绘页、覆盖包的连续性；只有出现【别的资产】才走这条新路。</summary>
    internal static bool IsPortraitFamilyTarget(string target) =>
        target.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase);

    /// <summary>目标资产真存在吗？两个权威源：① 游戏 <c>Content/&lt;target&gt;.xnb</c>；
    /// ② 已装 mod 在自己 content.json 里声明过的 Target 名（mod 新增角色、别的 mod 造的资产走这条）。
    /// 注意这只用来【排除明显打空的目标】—— 作者本人的 CP 包里也有一条打着不存在资产
    /// （实测 <c>LooseSprites/DialogBoxGreen</c>）的补丁，所以判不出来时宁可放行也不能整单废。</summary>
    internal static bool AssetExists(string gamePath, string target, HashSet<string>? declared = null)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var xnb = Path.Combine(gamePath, "Content", target.Replace('/', Path.DirectorySeparatorChar) + ".xnb");
        if (File.Exists(xnb)) return true;
        return (declared ?? DeclaredAssetNames(gamePath)).Contains(target);
    }

    /// <summary>读一个游戏 Content 里的资产原始尺寸（XNB 头，LZX/未压缩都能读；
    /// LZ4 与损坏返回 null）。用来判整表替换会不会把界面撑破。</summary>
    internal static (int Width, int Height)? VanillaAssetSize(string gamePath, string target)
    {
        try
        {
            var xnb = Path.Combine(gamePath, "Content", target.Replace('/', Path.DirectorySeparatorChar) + ".xnb");
            if (!File.Exists(xnb)) return null;
            var tex = XnbDecoder.TryDecode(xnb);
            return tex is null ? null : (tex.Width, tex.Height);
        }
        catch { return null; }
    }

    /// <summary>所有已装 mod 的 content.json 里写过的 Target 名（拆逗号、去空白、忽略大小写）。
    /// 按「Mods 目录条目 + 各 content.json 的 size/mtime」签名缓存：签名不变就不重扫，
    /// 装了/删了 mod 或改了某个包的 content.json 都会自动失效 —— 否则会拿旧集合
    /// 拒掉「刚装上前置、现在才存在」的资产。</summary>
    private static string? DeclaredCacheKey;
    private static string? DeclaredCacheSig;
    private static HashSet<string>? DeclaredCacheNames;

    internal static HashSet<string> DeclaredAssetNames(string gamePath)
    {
        var modsDir = Path.Combine(gamePath, "Mods");
        var sig = ModsContentSignature(modsDir);
        if (DeclaredCacheNames is not null
            && string.Equals(DeclaredCacheKey, gamePath, StringComparison.OrdinalIgnoreCase)
            && DeclaredCacheSig == sig) return DeclaredCacheNames;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(modsDir, "content.json", SearchOption.AllDirectories))
            {
                try
                {
                    if (new FileInfo(f).Length > 4 * 1024 * 1024) continue;
                    foreach (Match m in TargetInContentJson.Matches(File.ReadAllText(f)))
                        foreach (var part in m.Groups[1].Value.Split(','))
                        {
                            var t = part.Trim();
                            if (t.Length > 0) set.Add(t);
                        }
                }
                catch { }
            }
        }
        catch { }
        DeclaredCacheKey = gamePath;
        DeclaredCacheSig = sig;
        DeclaredCacheNames = set;
        return set;
    }

    /// <summary>Mods 树的内容签名（条目名 + content.json 的 mtime/size），够便宜又能防旧集合赖着不走。</summary>
    private static string ModsContentSignature(string modsDir)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            foreach (var e in Directory.EnumerateFileSystemEntries(modsDir))
                sb.Append(Path.GetFileName(e)).Append('|');
            foreach (var f in Directory.EnumerateFiles(modsDir, "content.json", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>把裸素材包按【包内目录名】转成能用的 CP 包。
    /// 返回 null = 成功（包已写进 <c>Mods\&lt;packName&gt;</c>）；返回文案 = 一个目录都没建，
    /// 用户原来装的东西不动。三条硬规矩：
    /// ① 目标资产必须在游戏里存在（<c>Content/&lt;t&gt;.xnb</c> 或已装 mod 声明过）；
    /// ② 整表替换只在源图尺寸 == 目标资产尺寸时打，撑破界面的补丁直接不写；
    /// ③ 一条都活不下来就整单拒绝，并把被丢掉的文件点名给用户的安装结果看 ——
    /// 绝不出现「装上 13 个文件、写出去 5 条假立绘」那种半截包。</summary>
    private static string? InstallLooseAssetPackAsCp(
        string gamePath, string srcDir, string packName, int? nexusModId, out string? modName)
    {
        modName = packName;
        var changes = new List<Dictionary<string, object?>>();
        var dropped = new List<string>();
        var uncheckedSize = 0;
        var candidates = 0;
        var dest = Path.Combine(gamePath, "Mods", packName);
        var destMade = false;

        try
        {
            // 重装同名包：先把上一版整目录挪进回收站。就地覆盖会让上一版独有的素材留在包里
            //（content.json 会被新的盖掉，但那些没人引用的 PNG 一直躺在 assets 里）。
            if (Directory.Exists(dest)) StageExistingToTrash(gamePath, dest);
            Directory.CreateDirectory(dest);
            destMade = true;
            var files = Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var declared = DeclaredAssetNames(gamePath);   // 整趟只取一次（见 ResolveAssetTarget 的注释）
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                if (rel.Split('/').Any(s => s == ".." || s.Length == 0)) continue;   // 防穿越
                candidates++;
                var target = ResolveAssetTarget(gamePath, rel, declared);
                if (target is null)
                {
                    dropped.Add(LocService.Tf("{0}：按目录名推不出游戏里存在的目标资产", rel));
                    continue;
                }

                var srcExt = Path.GetExtension(file);
                DecodedTexture? src = null;
                if (srcExt.Equals(".xnb", StringComparison.OrdinalIgnoreCase))
                {
                    // XNB 源一律解码成 PNG。解不出来的就是【不是贴图】（字体、数据资产）或文件坏了 ——
                    // 这种目标写进 content.json 只会让 CP 报错，所以点名丢掉，不默默产出坏补丁。
                    src = XnbDecoder.TryDecode(file);
                    if (src is null)
                    {
                        dropped.Add(LocService.Tf("{0}：解不出图像，{1} 多半不是贴图（例如字体）", rel, target));
                        continue;
                    }
                }
                var (sw, sh) = src is not null ? (src.Width, src.Height) : ReadSourceSize(file, srcExt);

                // ② 尺寸核对：整表替换只在两边一致时才打。源图读不出（0,0）则放行并计数
                //（宁可打了不管用，也不能因为读不出尺寸就把整包判死）。
                if (sw > 0 && sh > 0)
                {
                    var vanilla = VanillaAssetSize(gamePath, target);
                    if (vanilla is not null && (vanilla.Value.Width != sw || vanilla.Value.Height != sh))
                    {
                        dropped.Add(LocService.Tf("{0}：图是 {1}×{2}，而 {3} 是 {4}×{5}，整表替换会把界面撑破",
                            rel, sw, sh, target, vanilla.Value.Width, vanilla.Value.Height));
                        continue;
                    }
                    if (vanilla is null) uncheckedSize++;
                }

                var assetRel = "assets/" + target + ".png";
                var assetDest = Path.Combine(dest, assetRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(assetDest)!);
                if (src is not null)
                    File.WriteAllBytes(assetDest,
                        PixelKit.CropScalePng(src, 0, 0, src.Width, src.Height, src.Width, src.Height));
                else
                    File.Copy(file, assetDest, overwrite: true);

                changes.Add(new Dictionary<string, object?>
                {
                    ["Action"] = "EditImage",
                    ["Target"] = target,
                    ["FromFile"] = assetRel,
                    ["PatchMode"] = "Replace",
                });
            }

            if (changes.Count == 0)
            {
                if (destMade) try { Directory.Delete(dest, true); } catch { }
                return candidates == 0
                    ? LocService.Tr("这个包里没有找到任何能对应到游戏画面的图片（PNG/XNB），不是可安装的内容包，已放弃安装。")
                    : LocService.Tf("这个包里的 {0} 个素材文件都不适配当前的游戏，已放弃安装，你原来装的什么都没动：",
                        candidates) + "\n" + string.Join("\n", dropped.Take(12));
            }

            var uid = AssetPackUidPrefix + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(packName.ToLowerInvariant())))[..10];
            File.WriteAllText(Path.Combine(dest, "manifest.json"), JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["Name"] = packName,
                    ["Author"] = "JuniGrid",
                    ["Version"] = "1.0.0",
                    ["Description"] = "Asset pack converted to Content Patcher by JuniGrid (targets derived from the pack's own folder names).",
                    ["UniqueID"] = uid,
                    ["ContentPackFor"] = new Dictionary<string, string>
                    { ["UniqueID"] = "Pathoschild.ContentPatcher" },
                }, PackJsonOpts));
            File.WriteAllText(Path.Combine(dest, "content.json"), RenderPackContentJson(changes));
            if (nexusModId is not null) WriteNexusIdSidecar(dest, nexusModId.Value);

            AppLog.Warn("Portraits",
                $"[裸素材包] {packName}：按目录名转出 {changes.Count} 条补丁"
                + (dropped.Count > 0 ? $"，丢掉 {dropped.Count} 个文件：" + string.Join(" | ", dropped.Take(8)) : "")
                + (uncheckedSize > 0 ? $"（{uncheckedSize} 条读不到原图尺寸，未核对）" : ""));
            modName = packName;
            return null;
        }
        catch (Exception ex)
        {
            try { if (destMade && Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
            if (ex is IOException or UnauthorizedAccessException)
                return LocService.Tr("内容包写入失败（可能被占用）：") + ex.Message;
            return ex.Message;
        }
    }

    /// <summary>源图尺寸；读不出来返回 (0,0)。</summary>
    private static (int Width, int Height) ReadSourceSize(string file, string ext)
    {
        try
        {
            var tex = ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase)
                ? XnbDecoder.TryDecode(file) : PixelKit.DecodePng(file);
            return tex is null ? (0, 0) : (tex.Width, tex.Height);
        }
        catch { return (0, 0); }
    }
}
