using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace JuniGrid.Services;

public sealed partial class PortraitSkinService
{
    // ══════════════════ v1.7.2 进程内扫描快照 ══════════════════

    private PortraitScanResult? _memScan;
    private string? _memScanGame;
    private string? _memScanSig;
    private long _memScanAt;

    /// <summary>内存里有、且 Mods 签名未变的扫描结果；否则 null。UI 进页时先吃这个，零等待。</summary>
    public PortraitScanResult? TryGetMemoryScan(string gamePath)
    {
        if (_memScan is null || _memScanGame is null) return null;
        if (!string.Equals(_memScanGame, gamePath, StringComparison.OrdinalIgnoreCase)) return null;
        // 15 秒内直接信任（切页来回点）；超过则核对签名（一次目录 stat，仍远快于重扫）
        if (Environment.TickCount64 - _memScanAt < 15_000) return _memScan;
        // v1.7.37：过期但内存里还有 ⇒ 先上屏、后台再核对签名。以前这里同步算
        // ModsSignature（大 Mods 树要遍历几千个文件），OnInitialized 卡在 UI 线程上，
        // 表现就是「每次进肖像页都要转半天」。签名若变就作废，下次进页/刷新重扫。
        var stale = _memScan;
        _ = Task.Run(() =>
        {
            try
            {
                var sig = ModsSignature(Path.Combine(gamePath, "Mods"));
                if (!string.Equals(sig, _memScanSig, StringComparison.Ordinal))
                    InvalidateMemoryScan();
                else
                    _memScanAt = Environment.TickCount64;
            }
            catch { /* 核对失败就继续用旧快照 */ }
        });
        return stale;
    }

    private void StoreMemoryScan(string gamePath, PortraitScanResult scan, string? sig)
    {
        _memScan = scan;
        _memScanGame = gamePath;
        _memScanSig = sig;
        _memScanAt = Environment.TickCount64;
    }

    /// <summary>显式作废内存快照（Mod 启停/安装/卸载后调用，强制下次重扫）。</summary>
    public void InvalidateMemoryScan()
    {
        _memScan = null;
        _prewarmedSig = null;
        // 签名缓存一并丢掉 —— 启停/装卸已经改了树，下一次必须重新 stat
        _sigCache = null;
        _sigCacheDir = null;
    }

    /// <summary>
    /// 版本切换期间挂起立绘读盘。我们自己就是 Mods 目录最大的读者（扫描要解码图片判透明/
    /// 比画面，缩略图预热要逐张读），Windows 上目录里有文件被打开时 Directory.Move 会失败 ⇒
    /// 切换退化成整棵拷贝（实测 4.3 秒那次就是被自家扫描挡住，113 项白拷一遍）。
    /// 切换开始置 true、结束置 false；扫描与缩略图在挂起期间排队等待，不开新的读盘任务。
    /// </summary>
    public static volatile bool Suspended;

    private static int _readers;
    private static readonly Timer ResumeTimer = new(_ => Suspended = false, null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>切换开始：挂起读盘，并兜底 30 秒后自动恢复 —— 切换线程被强杀或崩在半路时，
    /// 不能把立绘页永久锁死（那看起来就像"扫描坏了"）。</summary>
    public static void SuspendReads()
    {
        Suspended = true;
        KeepSuspended();
    }

    /// <summary>给挂起续期。⚠ 没有这一步，30 秒兜底会在一次长切换（拷 700 MB 完全可能超过
    /// 30 秒）中途把 Suspended 放开 ⇒ 立绘扫描醒过来重新读 Mods，正搬着的目录又被占住 ——
    /// 我们这次要修的正是"自己抢自己"。切换每走完一个阶段调一次；线程真死了才会自动放行。</summary>
    public static void KeepSuspended() => ResumeTimer.Change(30000, Timeout.Infinite);

    public static void ResumeReads()
    {
        ResumeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Suspended = false;
    }

    /// <summary>等立绘这边正在跑的扫描/缩略图收手（切换前调用）。超时也放行 —— 改名还有重试兜底。</summary>
    public static void WaitUntilIdle(int timeoutMs = 8000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _readers) > 0 && Environment.TickCount64 < until) Thread.Sleep(100);
    }

    /// <summary>一次读盘（扫描或缩略图生成）的租约：挂起时排队等，计数供切换侧等空闲。</summary>
    private static IDisposable ReadLease()
    {
        var until = Environment.TickCount64 + 20000;
        while (Suspended && Environment.TickCount64 < until) Thread.Sleep(150);
        Interlocked.Increment(ref _readers);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _alive = 1;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _alive, 0) == 0) return;
            Interlocked.Decrement(ref _readers);
        }
    }

    // ══════════════════ v1.3.9 扫描快照缓存 ══════════════════

    private static string ScanCachePath =>
        Path.Combine(StoragePaths.AppDataDir, "portrait-scan-cache.json");

    /// <summary>快照结构版本：每当给 PortraitScanResult 增加【需要参与序列化】的派生字段就 +1；
    /// 出卡/合并等【产出语义】变了也要 +1（v2=Miku 声明画风拆卡、v3=Henchman/SVE_Henchman 同义词合并、
    /// v4=分支拆卡只认 ConfigSchema（IsOutdoors 等运行时状态不再拆成多卡）、
    /// v5=删除跨包哈希去重（同脸不同 mod 不再折叠，每张单独出卡）、
    /// v6=非角色肖像黑名单（AnsweringMachine 电话机等无生命物体不再出卡）、
    /// v7=代表图偏好默认脸（只有变体资产的包不再拿事件换装当封面）、
    /// v8=外观后缀表收 Wedding 并提为共享表（_Wedding 差分归并进角色卡，不再单开一张）、
    /// v9=扫描结果新增 BaseSeasonPatches（包用「基资产+When:Season」表达四季时的分季表，
    /// 覆盖包靠它才不会把一张图无条件钉满四季）、
    /// v10=封面改判据：只认包声明的分季表（BaseSeasonPatches），不再按文件名里的季节提权
    /// —— 旧判据把 East Scarp 的 Juliet 封面弄成 Juliet_Winter.png，而季节差分资产是
    ///「某个场合的脸」，不是默认像（2026-09-28 实测），
    /// 否则旧快照命中后看到的还是改前的卡，白测。
    /// v13=扫描结果新增 HdPortraitEntries / HdPortraitRendererInstalled（Mods/HDPortraits/* 这条
    /// HD 肖像通道，Dacar 那类包以前整包静默丢弃）。
    /// 旧快照缺这些字段（如 v1.7.13 的 PackDeps/PackFolderByUid），命中后反序列化出来是空的 ⇒
    /// 版本不符直接作废、强制重扫回填一次，避免"改了扫描却因旧快照看不到效果"。
    /// v14=扫描结果新增 HdSkins（HD 通道里可当皮肤选的那些高清表）—— 旧快照命中后这张表恒空 ⇒
    /// [CP] Dacar Rasmodia Portraits 那类包在肖像页里永远不出卡。
    /// v15=「默认」行锁死原版（用户 2026-09-29 拍板）：缓存里存的是【算好的角色卡】，
    /// 不抬版本的话旧快照命中，Gus/Marnie/Linus 这些还是指着 zLewdDewValley 的重绘图，
    /// 改了出卡逻辑却看不见效果（实测踩过一次，见 v12 的说明）。
    /// v16=同脸别名改按「任一来源同文件」配对（AliasMap）—— v15 锁死默认行后，本体的默认行不再
    /// 指向扩展包那张，只比默认行就并不上 MorrisTod/MarlonFay 这类变体 ⇒ 一个人裂成两张裸 id 卡、
    /// 本体还丢掉那款皮肤。旧快照存的是【并好的卡】，不抬版本看不到修复（对账实测 0→20 格）。
    /// v17=再加「本体 id + 大写剧情后缀」这条分组信号（…Lewd 那 17 条靠它并回本体）。
    /// 缓存里存的就是 AliasOf/Members，v16 快照是【没并】的状态 ⇒ 不抬版本对账仍是 76 格。
    /// v18=分组与佐证 E1 的长度门槛 4→3：Sam 只有三个字母被挡在外面，SamLewd 单独挂在页面上
    ///（用户 2026-09-30 实测）。同样要抬版本才看得到并进去。
    /// v19=硬同义词的另一侧（法师在 SVE 里叫 Magnus）补上「默认行」= 主 id 的原版 xnb。
    /// 缓存里存的就是 Vanilla 那一行，不抬版本它仍是空的 ⇒ 落盘照旧走娘家行（SVE 的女巫脸）。
    /// v20=扫描结果新增 RivalSheets（同一份资产上所有包的声明 + 尺寸）。这个字段只在扫描期
    /// 从 content.json 生成，旧快照命中 ⇒ 字典恒空，页面上的"你的表只有 1–N 行"永远不出。
    /// ⚠ 与 PackDeps/PackFolderByUid 同一个坑：快照里缺字段就是静默空，不报错也不重扫。</summary>
    private const int ScanCacheVer = 20;

    /// <summary>Content\Portraits\*.xnb 的角色名前缀（Abigail_Winter → Abigail），按游戏目录缓存。
    /// 用来判「这张裸图是不是给某个已知角色的」。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>>
        VanillaIdsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Content/Portraits 里"不是角色"的资产 —— 画的是无生命物体，不该在立绘页出卡。
    /// AnsweringMachine 是电话机（留言事件用的贴图），anime 画风包给它也画了一套 ⇒ 会被当成
    /// "原版有肖像"塞进候选（实测用户贴脸问"这也能算 NPC"）。其余原版肖像（Grandpa/Governor/
    /// Bear/Birdie/Henchman/…）都是拟人形象或生物，保留。</summary>
    public static readonly HashSet<string> NonPersonPortraits =
        new(StringComparer.OrdinalIgnoreCase) { "AnsweringMachine" };

    private static HashSet<string> VanillaPortraitIds(string gamePath)
        => VanillaIdsCache.GetOrAdd(gamePath, gp =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Path.Combine(gp, "Content", "Portraits");
                if (!Directory.Exists(dir)) return set;
                foreach (var f in Directory.EnumerateFiles(dir, "*.xnb"))
                {
                    var nm = Path.GetFileNameWithoutExtension(f);
                    var cut = nm.IndexOf('_');
                    var baseName = cut > 0 ? nm[..cut] : nm;
                    if (NonPersonPortraits.Contains(baseName)) continue;
                    set.Add(baseName);
                }
            }
            catch { }
            return set;
        });

    /// <summary>上一次扫描认出的角色 id（含 mod 新增角色，如 SVE 的 Andy / ScarlettFake）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>>
        ScannedCharIdsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>扫描跑完（或快照命中）就把角色表留在内存里，供裸素材包判定用。</summary>
    private static void RememberScannedCharIds(string gamePath, IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        if (set.Count > 0) ScannedCharIdsCache[gamePath] = set;
    }

    /// <summary>原版角色 ∪ 上次扫描认出的角色。mod 新增角色只有扫描过才认得：
    /// 内存里没有就顺手读一次落盘快照（只认得"这个目录该不该出现转换按钮"，
    /// 认不出就当不该动它 —— 多认几个名字只会让裸素材包更容易被认出来，
    /// 不会把别人的包误判成肖像包，那一道靠"整棵子树没有 manifest.json"挡）。</summary>
    private static HashSet<string> KnownPortraitIds(string gamePath)
    {
        var ids = new HashSet<string>(VanillaPortraitIds(gamePath), StringComparer.OrdinalIgnoreCase);
        if (!ScannedCharIdsCache.TryGetValue(gamePath, out var scanned))
            ScannedCharIdsCache[gamePath] = scanned = LoadScannedCharIds(gamePath);
        foreach (var id in scanned) ids.Add(id);
        return ids;
    }

    private static HashSet<string> LoadScannedCharIds(string gamePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ScanCachePath)) return set;
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ScanCachePath));
            if (!string.Equals(j["game"]?.ToString(), gamePath, StringComparison.OrdinalIgnoreCase)) return set;
            if (j["scan"]?["Characters"] is Newtonsoft.Json.Linq.JArray arr)
                foreach (var t in arr)
                    if (t["Id"]?.ToString() is { Length: > 0 } id)
                        set.Add(id);
        }
        catch { }
        return set;
    }

    /// <summary>
    /// Mods 目录签名 = ①顶层条目名 ②全部 manifest/content.json/config.json 的
    /// (相对路径, mtime, size) ③全部 .png/.xnb 素材的 (相对路径, size)。
    /// ②③ 都只 stat 不读内容，一次目录遍历拿全。
    /// 为什么必须有 ①③：立绘页认的皮肤有两个来源 —— CP 包（有 json）和 Portraiture 素材包
    /// （<c>Portraiture/Portraits/名字/Emily.png</c>，一个 json 都没有）。旧签名只看 json，
    /// 于是手放/手删/手改这类纯 PNG 素材包完全不动签名，立绘页一直吃旧快照
    ///（2026-09-19 实测：把 TP's Emily Portrait 搬进搬出，界面长时间不变）。
    /// 素材只取 size 不取 mtime：换 mtime 不代表换画，而换画必然改字节数（除非逐字节相同，
    /// 那本来就该命中缓存）。scan-algo 版本号跟扫描语义走，算法变更必须作废旧快照。</summary>
    private static string ModsSignature(string modsDir)
    {
        // v1.7.37：结果缓存 45 秒 —— 进页/扫描/快照命中都会算签名，大 Mods 树（尤其
        // OneDrive）一次全量 stat 要秒级。应用内启停/装卸会走 InvalidateMemoryScan，
        // 顺带清掉这里；纯手改文件最迟 45 秒后也能被看见。
        if (_sigCache is not null
            && string.Equals(_sigCacheDir, modsDir, StringComparison.OrdinalIgnoreCase)
            && Environment.TickCount64 - _sigCacheAt < 45_000)
            return _sigCache;
        try
        {
            // v1.7.7：画风卡改了扫描产出（同包多画风从 1 条变 N 条）→ 必须让历史快照全部作废，
            // 否则用户升完级看到的还是旧缓存那一套（Mods 没动就不会重扫）
            // v1.7.10：互斥 When 分支也出画风卡（法师那张缝出来的卡必须被冲掉），
            // 原版角色名单改用 Content/Portraits（多出的 9 个事件演员必须重扫才有），
            // 画风取值表/ConfigSchema Default 代换又改了一轮解析产出 → 再升号
            // v1.7.11：「含精灵图」角标不再借包级 SpriteChars（ToArea 局部补丁不算整表），
            // 默认行新增"图实际来自哪个扩展包" → 旧快照两样都是错的，必须重扫
            var parts = new List<string> { "scan-algo:assets-v30c-cmct-config" };
            static bool SkipPath(string path)
            {
                return path.Contains(".junigrid_trash", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                    // 自己写的覆盖包/遗留包绝不能进签名 —— 换肤落盘会改它的 PNG/config，
                    // 签名跟着变 → 每次进页都判缓存失效、全量重扫（实测：进立绘必转圈）
                    || path.Contains(OverrideFolder, StringComparison.OrdinalIgnoreCase)
                    || path.Contains(LegacyOverrideFolder, StringComparison.OrdinalIgnoreCase);
            }
            foreach (var e in Directory.EnumerateFileSystemEntries(modsDir))
            {
                if (SkipPath(e)) continue;
                parts.Add("T:" + Path.GetFileName(e));
            }
            foreach (var f in new DirectoryInfo(modsDir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (SkipPath(f.FullName)) continue;
                var rel = Path.GetRelativePath(modsDir, f.FullName);
                var name = f.Name;
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("content.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                {
                    // 结构文件带 mtime：config.json 被改写（哪怕字节数没变）就是角色开关变了
                    parts.Add($"J:{rel}|{f.LastWriteTimeUtc.Ticks}|{f.Length}");
                }
                else if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                      || name.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add($"A:{rel}|{f.Length}");
                }
            }
            parts.Sort(StringComparer.Ordinal);
            using var sha = System.Security.Cryptography.SHA1.Create();
            var sig = Convert.ToHexString(sha.ComputeHash(
                System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts))));
            _sigCache = sig;
            _sigCacheDir = modsDir;
            _sigCacheAt = Environment.TickCount64;
            return sig;
        }
        catch { return ""; }
    }

    private static string? _sigCache;
    private static string? _sigCacheDir;
    private static long _sigCacheAt;

    /// <summary>尝试读快照：签名一致才命中。损坏/版本不符静默返回 null 走重扫。</summary>
    private PortraitScanResult? TryLoadScanCache(string gamePath, out string sig)
    {
        sig = string.IsNullOrWhiteSpace(gamePath) ? "" : ModsSignature(Path.Combine(gamePath, "Mods"));
        if (sig.Length == 0) return null;
        try
        {
            if (!File.Exists(ScanCachePath)) return null;
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ScanCachePath));
            // 结构版本不符 → 作废重扫（旧快照缺新增的可序列化派生字段，命中会让它们恒空）
            if ((j["ver"]?.Value<int?>() ?? 0) != ScanCacheVer) return null;
            // 必须连目录一起认：签名只覆盖 Mods 那棵树，两个不同目录算出同一个签名是可能的
            // （测试台跑沙箱扫描就会把真实目录的快照顶掉 —— 实测立绘页因此整页空）。
            var cachedGame = j["game"]?.ToString();
            if (!string.IsNullOrWhiteSpace(cachedGame)
                && !string.Equals(Path.GetFullPath(cachedGame).TrimEnd('\\', '/'),
                    Path.GetFullPath(gamePath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.Equals(j["sig"]?.ToString(), sig, StringComparison.Ordinal)
                && j["scan"] is not null)
            {
                var scan = j["scan"]!.ToObject<PortraitScanResult>(
                    new Newtonsoft.Json.JsonSerializer
                    {
                        ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver(),
                        TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto,
                        ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
                    });
                if (scan is { Characters.Count: > 0 })
                {
                    AppLog.Warn("Portraits", "扫描快照命中（签名一致），跳过重扫");
                    return scan;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Portraits", "扫描快照读取失败（走重扫）: " + ex.Message);
        }
        return null;
    }

    /// <summary>写快照（扫描完成后调用）。失败静默 —— 缓存永远不能拖垮主流程。</summary>
    private static void SaveScanCache(string gamePath, PortraitScanResult scan, string sig)
    {
        try
        {
            Directory.CreateDirectory(StoragePaths.AppDataDir);
            var obj = new Newtonsoft.Json.Linq.JObject
            {
                ["sig"] = sig,
                ["ver"] = ScanCacheVer,
                ["game"] = gamePath,
                ["time"] = DateTime.Now.ToString("O"),
                ["scan"] = Newtonsoft.Json.Linq.JToken.FromObject(scan)
            };
            File.WriteAllText(ScanCachePath, obj.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Portraits", "扫描快照写入失败: " + ex.Message);
        }
    }

    /// <summary>
    /// v1.3.3b：立绘有效性检测 —— 全透明占位（游戏里根本没有对话立绘，显示出来
    /// 一块空框）判为无效。
    /// v1.3.4：回退"非方形→无效"判定 —— 用户的自定义肖像存在非方形尺寸（半身像/
    /// 宽幅构图），一刀切会误杀用户添加的肖像（实测反馈：新肖像全消失）。只保留
    /// 全透明这一无争议的无效形态。
    /// 解码失败按有效处理（宁多显示不误杀）。
    /// v1.6.8：判定结果进持久探针缓存（portrait-probe-cache.json），冷扫描不再
    /// 全量重复解码。
    /// </summary>
    private static bool IsBlankPortrait(string file)
    {
        var key = ProbeKey("blank", file);
        if (ProbePersist.TryGetValue(key, out var cached)) return cached;
        var verdict = ProbeBlank(file);
        ProbePersist[key] = verdict;
        return verdict;
    }

    private static bool ProbeBlank(string file)
    {
        try
        {
            var tex = PixelKit.DecodePng(file);
            if (tex is null) return false;
            var px = tex.PixelsRgba;
            for (var i = 3; i < px.Length; i += 4)   // RGBA：每 4 字节第 4 位是 A
                if (px[i] > 16) return false;        // 存在可见像素 → 有效立绘
            return true;                              // 全透明 → 无效
        }
        catch { return false; }
    }

    // ── v1.6.8：探针判定持久缓存（跨进程）──
    // ⚠ 键必须带探针种类前缀：IsOpaqueImage（true=实心肖像）与 IsBlankPortrait
    // （true=空白废图）语义相反，共用一个键会互相污染 —— 谁先探测谁定调，
    // 另一个读到反向结论：实心肖像被当"空白"删掉 / 真叠加被当"整表"放进页面（实测）。
    private static readonly ConcurrentDictionary<string, bool> ProbePersist = LoadProbePersist();

    private static string ProbePersistPath =>
        Path.Combine(StoragePaths.AppDataDir, "portrait-probe-cache.json");

    private static string ProbeKey(string kind, string file)
    {
        var fi = new FileInfo(file);
        return $"{kind}|{file}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
    }

    private static ConcurrentDictionary<string, bool> LoadProbePersist()
    {
        try
        {
            var p = ProbePersistPath;
            if (!File.Exists(p)) return new();
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(p));
            var d = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in j)
            {
                // 旧格式（无种类前缀）的键语义不明，直接丢弃
                if (kv.Value?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean) continue;
                if (!kv.Key.StartsWith("opaque|", StringComparison.Ordinal)
                    && !kv.Key.StartsWith("blank|", StringComparison.Ordinal)) continue;
                d[kv.Key] = (bool)kv.Value!;
            }
            return d;
        }
        catch { return new(); }
    }

    /// <summary>写探针持久缓存。v1.6.8：只在本扫描收尾调用一次 —— 每判定一张就全量
    /// 重写 JSON 会产生几百次同步写盘，比解码本身还慢（实测回归）。</summary>
    public static void SaveProbePersist()
    {
        try
        {
            var obj = new Newtonsoft.Json.Linq.JObject();
            foreach (var kv in ProbePersist)
                obj[kv.Key] = kv.Value;
            File.WriteAllText(ProbePersistPath, obj.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch { }
    }

    /// <summary>「整张替换图」判定：抽样统计 alpha≥200 的实心像素占比 ≥35%。立绘天然带
    /// 透明背景（脸周围），不透明占比只有 6-7 成（Nyapu 实测 64-72%），不能用不透明率；
    /// 但真替换图的美术像素全是实心的（alpha=255），而装饰性叠加（腮红/色调层）几乎全是
    /// 弱透明像素、实心占比≈0。v1.6.8：结果进持久探针缓存 —— 扫描期间同一文件只解码
    /// 一次，跨进程文件未变也直接取历史判定。</summary>
    private static bool IsOpaqueImage(string path)
    {
        // v1.6.8：探针判定持久缓存 —— 冷扫描（安装/切版本后首次进肖像页）原本要全量
        // 解码几百张 PNG 做 透明度/空白 判定，是进页卡顿的大头。文件没变（路径+修改时间
        // +大小）直接取历史判定，只有新/变文件才解码。键带 "opaque" 种类前缀（见 ProbeKey 注释）。
        var key = ProbeKey("opaque", path);
        if (ProbePersist.TryGetValue(key, out var cached)) return cached;
        var verdict = ProbeOpaque(path);
        ProbePersist[key] = verdict;
        return verdict;
    }

    private static bool ProbeOpaque(string path)
    {
        try
        {
            var tex = PixelKit.DecodePng(path);
            if (tex is null) return false;
            long total = 0, solid = 0;
            for (var y = 0; y < tex.Height; y += 2)
                for (var x = 0; x < tex.Width; x += 2)
                {
                    total++;
                    if (tex.PixelsRgba[(y * tex.Width + x) * 4 + 3] >= 200) solid++;
                }
            return total > 0 && solid * 100 >= total * 35;
        }
        catch { return false; }
    }

    private static string Sha1(string s)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)));
    }
}
