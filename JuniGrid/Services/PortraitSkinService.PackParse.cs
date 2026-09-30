using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace JuniGrid.Services;

public sealed partial class PortraitSkinService
{
    // ══════════════════════ 扫描 ══════════════════════

    private sealed class PackScan
    {
        public string Folder = "";        public string Name = "";
        /// <summary>v1.3.4：内容包 UniqueID —— 代换 CP 原生 {{ModId}} token 用
        ///（Downtown Zuzu 的角色/立绘全部以 {{ModId}}_Name 形式注册）。</summary>
        public string Uid = "";
        /// <summary>磁盘上的真实目录（禁用包带 . 前缀）。文件解析必须用它 ——
        /// Folder 是规范化无点名，对禁用包拼出来的路径不存在（Axylvoo 空卡实测）。</summary>
        public string RawDir = "";
        /// <summary>角色 id → 整表立绘来源文件（缩略图候选，按出现序）。</summary>
        public Dictionary<string, List<string>> PortraitFiles = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>整表替换精灵图的角色 id（判断"含精灵图"）。</summary>
        public HashSet<string> SpriteChars = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>角色 id → 整表精灵图来源文件（弹窗右侧预览用）。</summary>
        public Dictionary<string, List<string>> SpriteFiles = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>角色 id → 该包给此角色打补丁时挂的 When 布尔键（config 开关）。</summary>
        public Dictionary<string, HashSet<string>> WhenKeys = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SchemaKeys = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>v1.7.9：ConfigSchema 的取值表 —— 开关键 → (AllowValues, Default)。
        /// 只留键名是老写法，画风这种"一个键多个取值"的开关没有取值表就枚举不出来。</summary>
        public Dictionary<string, (List<string> Values, string Default)> SchemaOptions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>v1.7.9：DynamicTokens —— 派生 token 名 → (它绑的开关键, 开关取值 → token 值)。
        /// [CP] Miku Mod Plus 的 DT_PortraitFile 就是这么把 VanillaPortraitChange=SD 变成
        /// 文件名 MikuModPlus_CharacterPortraits_SD 的。</summary>
        public Dictionary<string, (string Key, Dictionary<string, string> ByValue)> DynamicTokens
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>v1.7.9：哪些资产名的画风候选是「作者声明出来的」（ConfigSchema + DynamicTokens），
        /// 不是靠扫目录/文件名猜的。只有声明来源才允许救娘家行 —— 见组装循环那条门。</summary>
        public readonly HashSet<string> DeclaredStyleAssets = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>变体资产：资产名是已知角色 id 的下划线变体（如 Wizard_Spring → Wizard）。
        /// 这类 Load 的 Target 是变体本体（Portraits/Wizard_Spring），常配 1.6 Appearance 按季节
        /// 挂载 —— Baechu 法师的变体 Load 被"装 SVE 不生效"门槛挡掉后，Appearance 全部指向
        /// 缺失资产，立绘空白（实机）。覆盖包需要整族钉住。</summary>
        public List<(string Kind, string BaseId, string VariantId, string File)> AssetVariants = new();
        /// <summary>v1.7.28：这个包在 HD 肖像通道上登记的角色 —— (数据资产名, 角色资产名)。
        /// 目标形如 <c>Mods/HDPortraits/Wizard</c>；渲染端按它画对话框大头照，绕开
        /// <c>Portraits/Wizard</c>。旧逻辑按"命名空间不认识"整条丢弃（Dacar 实测）。</summary>
        public List<(string DataAsset, string Npc)> HdEntries = new();
        /// <summary>v1.7.29：本包在 <c>Mods/&lt;私有命名空间&gt;/&lt;资产&gt;</c> 上 <c>Load</c> 出来的图
        /// （完整资产名 → FromFile 原文，<b>按补丁出现顺序</b>保留全部）。
        /// 必须留列表不能留字典：同一资产常有多条 Load 分别来自互斥的 Include 子文件
        ///（Dacar 的 portraits.json / RRRRportraits.json 各钉一条 <c>Mods/DacarRasmodia/Wizard</c>），
        /// 字典后者盖前者 ⇒ 拿到的是【没生效那一支】的图。HD 卡按顺序取第一条真解得出文件的。</summary>
        public List<(string Target, string FromFile)> ModsPrivateLoads = new();
        /// <summary>v1.7.29：<c>EditData</c> 打在 <c>Mods/HDPortraits/&lt;角色&gt;</c> 上的
        /// <c>Entries.Portrait</c> 原文（可能带 <c>{{TargetWithoutPath}}</c>）。</summary>
        public Dictionary<string, string> HdPortraitPointers = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>v1.7.20：作者用「同一个资产名 + When:{Season}」表达四季的包 —— 记成
        /// <c>"Portraits/Caroline" → { spring → 文件, summer → 文件, … }</c>。
        /// Caroline (Overhaul) 四条补丁全是 <c>Target: Portraits/Caroline</c> + <c>When:{Season:spring}</c>，
        /// 文件却只叫 <c>Spring.png</c>（不含角色名）⇒ 覆盖包按文件名找季节图找不到，退化成
        /// "静态钉一张 + Priority Late+10"，把包自己的四季全压成同一张（用户从最初就报的
        /// "春夏秋冬都是裸体"）。这层映射只有包自己知道，必须从 content.json 取，不能猜文件名。</summary>
        public Dictionary<string, Dictionary<string, string>> BaseSeasonPatches
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>v1.7.37：本包在 <c>Portraits/&lt;x&gt;</c> 与 <c>Characters/&lt;x&gt;</c> 上的
        /// 每一条图像声明，逐条原样留着（含被换肤判据挡掉的装饰叠加、局部补丁、文件缺失）。
        /// 由 <c>Scan</c> 归并进 <see cref="PortraitScanResult.RivalSheets"/>。
        /// ⚠ 与 PortraitFiles/SpriteFiles 无关：那两个是"这个包能当皮肤的图"，判据严格；
        /// 这边是"谁碰过这份资产"，越全越好 —— 混用会让换肤判据被对手高度污染。</summary>
        public List<RivalSheet> RivalDecls = new();
        /// <summary>包 config.json 的当前值（懒加载）。FromFile 里的 {{配置键}} 靠它代换 ——
        /// Elle's Cuter Horses 的马皮肤是 assets/Horse/{{Horse Skin}}.png，文件名取决于
        /// 玩家在 GMCM 里选的马皮肤（config.json 的当前值）。</summary>
        public Dictionary<string, string>? ConfigValues;
        /// <summary>v1.7.7：角色 id →「画风候选」。补丁的 FromFile 把一个 {{配置键}} 写在
        /// **路径段**上（Donut's 的 assets/{{AlesiaPortrait}}/{{TargetWithoutPath}}.png）时，
        /// 这个键的值就是 assets/ 下的一个子目录名 —— 一套画风机理上是独立的一张卡，
        /// 而 CP 只会按 config 解析出一套，界面上永远只能看到字母序第一个（实测
        /// 覆盖包钉的是 Dawn，Donut / Donut(OLD) 两套根本选不到）。
        /// 这里按包内真实存在该角色图片的子目录逐条登记（Code/ 这种放 json 的目录自然落空）。
        /// ⚠ 刻意**不**并进 PortraitFiles：娘家判定 / PickSource / MergeByResolved 都吃那个
        /// 字段的既有语义，塞多套进去会连带回归（用户要求宁可保守）。</summary>
        public Dictionary<string, List<(string Token, string Style, string? Portrait, string? Sprite)>> StyleCandidates
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>v1.7.10：资产名 →「互斥 When 分支」。作者用**一个布尔开关的两条分支**
        /// 各放一套画（[CP] Donut's 的 Wizard：Rasmodia Patch=false → assets/fifadog 的脸；
        /// =true → assets/KlevLovins 的脸 + KlevLovins/Sprites 的身体）时，脸与身体必须
        /// 成对登记 —— 老写法把两者分别塞进 PortraitFiles / SpriteFiles 各取第一条，
        /// 于是缝出"fifadog 的脸 + KlevLovins 的身体"这种游戏里根本不存在的组合（用户实测
        /// "法师乱套：大头照匹配了另一个 mod 的精灵图"）。Value 是开关要写的值，
        /// Portrait/Sprite 是同分支的那两张。</summary>
        public Dictionary<string, List<(string Key, string Value, string? Portrait, string? Sprite)>> BranchAssets
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>v1.7.10：DynamicTokens 在**当前** config + 当前装了哪些 mod 下算出来的值
        /// （token 名 → 值，CP 同语义：后写的覆盖前面的）。作者把画风挂在
        /// <c>HasMod</c> / 跨包 config 这类条件上时（Ohodavi's Portraits for Rasmodia 的
        /// <c>assets{{HatPortrait}}/Witch_{{Version}}.png</c>），只有算出这两个 token 才解析得出文件名。</summary>
        public Dictionary<string, string> TokenNow = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>v1.7.29：这个包的 FromFile/Target 里出现过的【跨包动态 token】原文
        /// （<c>{{Spiderbuttons.CMCT/Dynamic: &lt;UID&gt;,&lt;token&gt;}}</c>）。全部包解析完之后，
        /// Scan 会把被引用那个包算好的 TokenNow 值按原样键塞回这张表 —— 于是代换那一步
        /// 不需要认识这种写法（Dacar 的法师高清脸 <c>Witch_{{...PortraitSVE}}.png</c> 就卡在这）。</summary>
        public HashSet<string> NeededCrossTokens = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>v1.7.10：这个包里我们**判不了**的 CP 条件 / 代换不出的 token（原样留一份）。
        /// 静默跳过 = 用户只看到"这个包的肖像没显示"、我们也查不到；改成自己报数，
        /// 盲区才有验收线（日志一行点名，--audit-packs 汇总条数）。</summary>
        public readonly HashSet<string> UnknownConds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>记一条判不了的条件/解不出的 token。每包封顶 40 类，
        /// 免得一个大包把日志刷成噪音（真正要看的头几条就在前面）。</summary>
        public void NoteUnknown(string what)
        {
            if (what.Length == 0 || UnknownConds.Count >= 40) return;
            UnknownConds.Add(what.Length > 90 ? what[..90] : what);
        }
    }

    /// <summary>Mods 目录里"装了哪些 mod"的索引 —— CP 的 HasMod / 跨包 config 条件要用。</summary>
    private sealed record ModIndex(HashSet<string> Uids, Dictionary<string, string> DirByUid);

    /// <summary>为动态季节资源找一张稳定的代表图。
    /// 常见结构是 assets/Portraits/&lt;角色&gt;/&lt;角色&gt;_Spring.png；只在 content.json
    /// 无法静态解析出来源时调用，避免把普通包的非目标素材误当作肖像。
    /// v1.7.2：再兜一层画风子目录（Donut's 的 assets/Donut/Alesia_Spring.png）——
    /// {{Season}}/{{画风}} token 代换失败时整包角色会变空白卡。</summary>
    private static string? FindCharacterAsset(string packRoot, string assetKind, string charId)
    {
        foreach (var tryId in AssetLookupIds(charId))
        {
            var hit = FindCharacterAssetOne(packRoot, assetKind, tryId);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static string? FindCharacterAssetOne(string packRoot, string assetKind, string charId)
    {
        Func<string?, bool> ok = assetKind.Equals("Characters", StringComparison.OrdinalIgnoreCase)
            ? (f => LooksLikeSprite(f) && !IsNonWalkSprite(f))
            : LooksLikePortrait;
        try
        {
            var characterDir = Path.Combine(packRoot, "assets", assetKind, charId);
            if (Directory.Exists(characterDir))
            {
                foreach (var p in Directory.EnumerateFiles(characterDir, "*.png", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    if (ok(p)) return p;
            }

            var assetDir = Path.Combine(packRoot, "assets", assetKind);
            if (Directory.Exists(assetDir))
            {
                // 直铺命名：assets/Characters/Wizard.png（无 _ 后缀）—— 性转包常见，
                // 旧写法只搜 charId_*.png 会整包认成「没有精灵图」
                var exact = Path.Combine(assetDir, charId + ".png");
                if (File.Exists(exact) && ok(exact)) return exact;
                foreach (var p in Directory.EnumerateFiles(assetDir, charId + "_*.png", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    if (ok(p)) return p;
                // 画风子目录：assets/<风格>/<id>_*.png 或 assets/<风格>/<id>.png
                foreach (var sub in Directory.EnumerateDirectories(assetDir))
                {
                    foreach (var a in Directory.EnumerateFiles(sub, charId + "_*.png", SearchOption.TopDirectoryOnly)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                        if (ok(a)) return a;
                    var b = Path.Combine(sub, charId + ".png");
                    if (File.Exists(b) && ok(b)) return b;
                    var cdir = Path.Combine(sub, charId);
                    if (Directory.Exists(cdir))
                    {
                        foreach (var c in Directory.EnumerateFiles(cdir, "*.png", SearchOption.TopDirectoryOnly)
                                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                            if (ok(c)) return c;
                    }
                }
            }
            // assets 直接摊开的风格目录（Donut's：assets/Donut/Alesia_Spring.png）
            var assetsRoot = Path.Combine(packRoot, "assets");
            if (Directory.Exists(assetsRoot))
            {
                // assets/Wizard.png 直铺在 assets 根下
                var rootExact = Path.Combine(assetsRoot, charId + ".png");
                if (File.Exists(rootExact) && ok(rootExact)) return rootExact;
                foreach (var hit in Directory.EnumerateFiles(assetsRoot, charId + "_*.png", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    if (ok(hit)) return hit;
                foreach (var sub in Directory.EnumerateDirectories(assetsRoot))
                {
                    foreach (var a in Directory.EnumerateFiles(sub, charId + "_*.png", SearchOption.TopDirectoryOnly)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                        if (ok(a)) return a;
                    var b = Path.Combine(sub, charId + ".png");
                    if (File.Exists(b) && ok(b)) return b;
                }
            }
            return null;
        }
        catch { return null; }
    }

    // ══════════════════════ content.json 解析 ══════════════════════

    /// <summary>解析一个 content 文件（主 content.json 或 Include 链上的任意子 json）。
    /// inheritedWhenKeys：Include 处挂的 When 布尔键要遗传给子 patch（SCC 的
    /// SlightlyCuterAbigail 这类开关写在 Include 上，不遗传则 config 开关永远写不中）。
    /// gated：本 patch 是否处于 When 门控之下 —— 影响"娘家包"判定权重（compat 包对
    /// 别家 mod 角色的 Data/Characters 兼容改动是门控的，真娘家是无条件的）。</summary>
    private void ParseContentPack(PackScan pack, string packRoot, string contentFile,
        HashSet<string> nativeCandidates, Dictionary<(string Pack, string Char), int> nativeOwners,
        Dictionary<string, string> displayNames,
        List<string> inheritedWhenKeys, bool gated, HashSet<string> visited, int depth, ModIndex idx)
    {
        if (depth > 6) return;
        if (!File.Exists(contentFile)) return;
        if (!visited.Add(Path.GetFullPath(contentFile))) return;
        // ⚠ CP 语义：FromFile（含 Include 的）永远相对内容包根目录（content.json 所在），
        // 不是当前子 json 所在目录
        var packDir = packRoot;
        pack.ConfigValues ??= LoadConfigValues(packRoot);
        JObject root;
        try
        {
            using var sr = new StreamReader(contentFile);
            // CP 生态的 content.json 常带 // 注释与尾逗号（East Scarp 实测）。
            // 2026-09-20 对本机 186 个 manifest/content 逐个跑严格 JObject.Parse：0 失败，
            // 且 "值,\n// 注释\n}" 这一形状 Newtonsoft 13.0.3 直接吃下 —— 早先"不容忍尾逗号"
            // 的结论不成立，故去掉了当时的清洗步骤。解析失败仍会让整包在 catch 里静默消失。
            root = JObject.Parse(sr.ReadToEnd());
        }
        catch (Exception ex)
        {
            // v1.3.3：解析失败必须留痕 —— 此前这里是纯 catch { return; }，
            // 整个包静默消失没有任何日志，用户只能看到"立绘页少了角色"无从排查。
            // v1.7.10：必须带包名 —— 只写 "content 解析失败 content.json" 时日志里全是同一句，
            // 定位不到是哪个包（实测：查"法师包为什么不出卡"时被这句误导了整整一轮）。
            AppLog.Warn("Portraits",
                $"[清单解析失败] {pack.Folder} 的 {Path.GetFileName(contentFile)}: {ex.Message}");
            return;
        }

        // ConfigSchema：社区惯例布尔键含角色名（ReplaceAbigail）
        if (root["ConfigSchema"] is JObject schema)
            foreach (var k in schema.Properties())
            {
                pack.SchemaKeys.Add(k.Name);
                // v1.7.9：连 AllowValues/Default 一起留 —— 「一个开关挑画风」的取值空间
                // 只有作者自己知道，猜文件名会猜出地图/信纸（实测 Miku 包 assets/png 里
                // 混着 1280×564 的信纸背景和 320×752 的地图图集）
                if (k.Value is JObject so
                    && so["AllowValues"]?.ToString() is { Length: > 0 } av)
                {
                    var vals = av.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    if (vals.Count > 0)
                        pack.SchemaOptions[k.Name] = (vals, so["Default"]?.ToString()?.Trim() ?? "");
                }
            }

        // v1.7.9：DynamicTokens —— 作者写的「开关取值 → 文件名/目录名」映射表。
        // [CP] Miku Mod Plus：VanillaPortraitChange=SD → DT_PortraitFile=MikuModPlus_CharacterPortraits_SD，
        // 而立绘补丁只写 assets/png/{{DT_PortraitFile}}.png —— 不读这张表就永远算不出源文件，
        // Miku 整个角色在立绘页消失（实测 2026-09-26）。
        if (root["DynamicTokens"] is JArray dynTokens)
            foreach (var t in dynTokens.OfType<JObject>())
            {
                var tkName = t["Name"]?.ToString()?.Trim() ?? "";
                // ⚠ 值**可以是空字符串**：Ohodavi's Portraits for Rasmodia 的
                // HatPortrait 默认档就是 ""（assets{{HatPortrait}}/… → assets/…），
                // 只有"没写 Value 属性"才跳过。当成无效丢掉 ⇒ 整包路径拼不出来。
                var valTok = t["Value"];
                if (tkName.Length == 0 || valTok is null || valTok.Type == JTokenType.Null) continue;
                var tkVal = valTok.ToString().Trim();
                var tw = t["When"] as JObject;
                // v1.7.10：按 CP 语义挑"当前成立"的那一档 —— 全部条件都成立才算，
                // 文件里靠后的同名条目覆盖靠前的。判不了的条件（Season/跨 mod 派生 token）
                // 按"不成立"处理：宁可少认一条画风，也不能凭猜往用户机器上写 config。
                var holds = true;
                if (tw is not null)
                    foreach (var wp in tw.Properties())
                        if (EvalWhenCond(wp.Name, wp.Value, pack, idx) != true) { holds = false; break; }
                if (holds) pack.TokenNow[tkName] = tkVal;
                if (tw is null) continue;
                foreach (var wp in tw.Properties())
                {
                    var bound = wp.Name;
                    var wv = wp.Value?.ToString()?.Trim() ?? "";
                    if (wv.Length == 0 || !pack.SchemaOptions.ContainsKey(bound)) continue;
                    if (!pack.DynamicTokens.TryGetValue(tkName, out var dt))
                    {
                        dt = (bound, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                        pack.DynamicTokens[tkName] = dt;
                    }
                    dt.ByValue[wv] = tkVal;
                    break;      // 一条派生 token 只绑一个开关（多键组合写法实测没有）
                }
            }

        if (root["Changes"] is not JArray changes) return;
        foreach (var c in changes.OfType<JObject>())
        {
            var action = c["Action"]?.ToString() ?? "Load";

            // When 里的布尔字面量 = config 开关（Include 的会遗传给子 patch）
            var whenKeys = new List<string>(inheritedWhenKeys);
            var whenBools = new List<(string Key, string Value)>();
            if (c["When"] is JObject when)
                foreach (var kv in when.Properties())
                {
                    // 带操作符的是条件表达式（"HasMod |contains=xxx": true），不是 config 开关
                    if (kv.Name.Contains('|')) continue;
                    // ⚠ 值允许 JSON 布尔**或字符串 "true"/"false"**，键名允许空格：
                    // Donut's 写的就是 "Rasmodia Patch": "false"（字符串 + 带空格的键名），
                    // 老判据（只认真布尔、键名不许有空格）两道都把它踢掉 ⇒ 选了卡也不翻开关。
                    var bv = WhenBoolValue(kv.Value);
                    if (bv is null) continue;
                    whenKeys.Add(kv.Name);
                    whenBools.Add((kv.Name, bv));
                }
            var selfGated = gated || whenKeys.Count > 0;

            // Include：大包（SVE）主 content.json 只写 Include，真 patch 在子文件里
            if (action.Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                var inc = c["FromFile"]?.ToString();
                if (string.IsNullOrWhiteSpace(inc) || inc.Contains("{{")) continue;
                foreach (var part in inc.Split(','))
                {
                    var pat = part.Trim();
                    if (pat.Length == 0) continue;
                    IEnumerable<string> matches;
                    try
                    {
                        var full = Path.Combine(packDir, pat.Replace('/', Path.DirectorySeparatorChar));
                        if (pat.Contains('*') || pat.Contains('?'))
                            matches = Directory.GetFiles(Path.GetDirectoryName(full) ?? packDir,
                                Path.GetFileName(full), SearchOption.TopDirectoryOnly);
                        else
                            matches = File.Exists(full) ? new[] { full } : Array.Empty<string>();
                    }
                    catch { continue; }
                    foreach (var m in matches)
                    {
                        var subPack = new PackScan { Folder = pack.Folder, Name = pack.Name, Uid = pack.Uid };
                        ParseContentPack(subPack, packRoot, m, nativeCandidates, nativeOwners,
                            displayNames, whenKeys, selfGated, visited, depth + 1, idx);
                        Absorb(pack, subPack);
                    }
                }
                continue;
            }

            var fromFile = c["FromFile"]?.ToString();
            if (string.IsNullOrWhiteSpace(fromFile)) fromFile = null;
            // v1.7.29：记下这条补丁引用的跨包动态 token。⚠ 键必须存【整段 token 原文】
            //（含 "Spiderbuttons.CMCT/Dynamic: " 前缀）—— 代换那一步是按
            // {{...}} 里的整段文字查 TokenNow 的，只存 "UID,token" 那截永远查不中，
            // 于是解不开的那条被跳过、后面【互斥 Include 里】的另一条顶上来了
            //（Dacar 实测：卡片显示成 RRRR 分支的 Witch_32.png，而游戏里生效的是 Witch_SVE.png）。
            if (fromFile is { Length: > 0 } && fromFile.Contains("CMCT/Dynamic", StringComparison.OrdinalIgnoreCase))
                foreach (System.Text.RegularExpressions.Match cm in System.Text.RegularExpressions.Regex.Matches(
                             fromFile, @"\{\{\s*(Spiderbuttons\.CMCT/Dynamic:\s*[^{}]+?)\s*\}\}",
                             System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    pack.NeededCrossTokens.Add(cm.Groups[1].Value.Trim());

            var targets = c["Target"] switch
            {
                JArray arr => arr.Select(t => t?.ToString() ?? ""),
                null => Array.Empty<string>(),
                JToken t => (t.ToString() ?? "").Split(','),
            };

            // v1.7.2：展开 {{Season}} —— Donut's Seasonal Anime 这类包用
            // Target="Portraits/Alesia_{{Season}}" + FromFile=".../Alesia_{{Season}}.png"
            // 表达四季变体。旧逻辑见 {{ 就丢目标、FromFile 也代换不掉 → 该包十几个
            // NPC 里只有不带 token 的几个能进立绘页（实测只认出 7 个）。
            // 展开成 Spring/Summer/Fall/Winter 四条具体目标（磁盘文件名是首字母大写）。
            var seasonExpand = new[] { "Spring", "Summer", "Fall", "Winter" };
            bool NeedSeasonExpand(string s) =>
                s.Contains("{{Season}}", StringComparison.OrdinalIgnoreCase);

            var workList = new List<(string Target, string? FromFile)>();
            foreach (var targetRaw in targets)
            {
                var t0 = targetRaw.Trim();
                if (t0.Length == 0) continue;
                if (NeedSeasonExpand(t0) || (fromFile is not null && NeedSeasonExpand(fromFile)))
                {
                    foreach (var sn in seasonExpand)
                    {
                        var t1 = t0.Replace("{{Season}}", sn, StringComparison.OrdinalIgnoreCase);
                        var f1 = fromFile?.Replace("{{Season}}", sn, StringComparison.OrdinalIgnoreCase);
                        workList.Add((t1, f1));
                    }
                }
                else workList.Add((t0, fromFile));
            }

            foreach (var (targetRaw, fromFileRaw) in workList)
            {
                var target = targetRaw.Trim();
                var curFromFile = fromFileRaw;
                // v1.3.4：{{ModId}}_ 前缀剥离 —— 角色数据键与立绘资产名归到同一裸名 id
                //（Downtown Zuzu：数据键 {{ModId}}_Callum / 立绘 Portraits/Callum）。
                if (pack.Uid.Length > 0)
                    target = target.Replace("{{ModId}}_", "", StringComparison.OrdinalIgnoreCase)
                                   .Replace("{{ModId}}", "", StringComparison.OrdinalIgnoreCase);
                if (target.Length == 0 || target.Contains("{{") ||
                    target.Contains('*') || target.Contains('?'))
                    continue;

                var slash = target.IndexOf('/');
                if (slash <= 0) continue;
                var prefix = target[..slash];
                var tail = target[(slash + 1)..];
                if (tail.Length == 0 || tail.Contains('*') || tail.Contains('?')) continue;
                // v1.7.28：Mods/** 命名空间 —— 其中 Mods/HDPortraits/<角色> 是 HD 肖像通道，
                // 必须登记；其余（SVE_Portraits、别家 mod 的私有资产）以前也是走到下面的
                // aspect 判定被 else continue 丢掉，行为不变。
                if (prefix.Equals("Mods", StringComparison.OrdinalIgnoreCase))
                {
                    // HD 肖像通道：数据资产、它指向的那张高清表、以及指向关系，三条分别记账，
                    // 到 Scan 末尾（所有包都解析完、跨包动态 token 能代换了）再拼成皮肤卡。
                    RegisterHdPortraitEntry(pack, tail);
                    if (action.Equals("Load", StringComparison.OrdinalIgnoreCase)
                        && curFromFile is { Length: > 0 }
                        && !tail.StartsWith("HDPortraits/", StringComparison.OrdinalIgnoreCase))
                        pack.ModsPrivateLoads.Add((target, curFromFile));
                    else if (action.Equals("EditData", StringComparison.OrdinalIgnoreCase)
                             && tail.StartsWith("HDPortraits/", StringComparison.OrdinalIgnoreCase)
                             && c["Entries"]?["Portrait"]?.ToString() is { Length: > 0 } ptxt)
                        pack.HdPortraitPointers[target] = ptxt.Trim();
                    continue;
                }
                // v1.3.6：1.6 子路径资产目标（Sunberry Village："Portraits/AichaSBV/AichaSBV"）——
                // 旧代码见第二个 '/' 直接丢弃，基础立绘/精灵全没登记，只有泳装这类无子路径
                // 变体被吸收成默认（用户实测：桑贝里村满屏泳装 + 20 个 NPC 只剩 5 个）。
                // 归属：首段 = 角色名；token 代换仍传完整 tail（"Assets/{{Target}}.png" 靠它拼路径）。
                string name;
                if (tail.Contains('/'))
                {
                    var segs = tail.Split('/');
                    if (segs.Any(s => s.Length == 0)) continue;
                    name = segs[0];
                }
                else name = tail;

                if (prefix.Equals("Data", StringComparison.OrdinalIgnoreCase) &&
                    (name.Equals("Characters", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("NPCDispositions", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("NPCDispositions/", StringComparison.OrdinalIgnoreCase)))
                {
                    // 娘家包判定：包里出现 Data/Characters/<id> 或 Data/NPCDispositions/<id>
                    // 字面量，或 Entries 键 / Records 键 / Fields 第 2 元素（SVE 的写法）→
                    // 该包是该角色的娘家包候选。⚠ NPCDispositions 是 1.6 旧式注册 ——
                    // Adventurer's Guild Expanded 只写它、全程不碰 Data/Characters，不认的话
                    // 它的 NPC（Daisy/Daniel/Gabriel/Silly/Zinnia）全被"无娘家"门槛滤掉（实测）。
                    // 无条件 patch 记 rank 0（真娘家）；When 门控的 compat 改动记 rank 1，
                    // 有真娘家时让位（SCC 对 SVE 角色的兼容改动不该抢 SVE 的娘家身份）。
                    // ⚠ 这里是 EditData，不能被下面的 Load-only 门拦掉（SVE 全靠它注册角色）
                    var rank = selfGated ? 1 : 0;
                    if (name.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("NPCDispositions/", StringComparison.OrdinalIgnoreCase))
                    {
                        var cid = name.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)
                            ? name["Characters/".Length..]
                            : name["NPCDispositions/".Length..];
                        if (cid.Length > 0 && !cid.Contains('/'))
                            RememberNative(nativeOwners, pack.Folder, cid, rank);
                    }
                    else
                    {
                        var before = nativeCandidates.Count;
                        CollectDataCharacterIds(c, nativeCandidates, pack, displayNames, packRoot);
                        foreach (var cid in nativeCandidates.Skip(before))
                            RememberNative(nativeOwners, pack.Folder, cid, rank);
                    }
                    continue;
                }

                string aspect;
                if (prefix.Equals("Portraits", StringComparison.OrdinalIgnoreCase)) aspect = "portrait";
                else if (prefix.Equals("Characters", StringComparison.OrdinalIgnoreCase)) aspect = "sprite";
                else if (prefix.Equals("Animals", StringComparison.OrdinalIgnoreCase)) aspect = "animal";
                else continue;

                // v1.7.37：先记账"这份资产上还有谁画了多高的表"，再走换肤判据。
                // 顺序是重点：装饰叠加、FromArea 局部差分、FromFile 指向不存在文件的声明
                // 在下面几处都会被判据丢掉，但它们同样会把我们的矮表压出残行。
                // 纯信息层 —— 下面所有判据、以及落盘一侧一行都没改。
                if (aspect != "animal")
                    RecordRivalDecl(pack, packDir, prefix, tail, name, action, c, curFromFile);

                if (aspect == "sprite")
                {
                    // 精灵表来源：整表 Load，或「整张不透明替换」的 EditImage
                    //（SDS 的精灵就是 EditImage 分年覆盖：Rane.png 当 Year=1、Rane1.png
                    // 之后 —— 只认 Load 会把 Empty.png 占位当精灵，弹窗右侧空白，实测）。
                    // FromArea/ToArea 局部改动与 Overlay 声明不算。
                    var sprIsLoad = action.Equals("Load", StringComparison.OrdinalIgnoreCase);
                    var sprIsEdit = action.Equals("EditImage", StringComparison.OrdinalIgnoreCase);
                    // 叠加层两种写法都要认：Portraiture 的 `Overlay: true`，以及 CP 原生的
                    // `PatchMode: "Overlay"` —— Seasonal Cute Characters 的鼻子就是后者
                    //（Add_Emily_Seasonal_Nose 一条 Overlay 覆盖 13 个季节资产，FromFile 固定
                    // 指向 Emily_Nose.png）。旧版只挡前者 ⇒ 鼻子图被登记成 Emily_Winter_Indoor
                    // 的"本尊"，落盘按 Replace 钉上去，448 行只剩 8 行有像素 = 人直接隐身
                    //（2026-09-28 实机：Emily/Victor 冬天室内室外）。
                    var sprIsOverlay = sprIsEdit
                        && (string.Equals(c["Overlay"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(c["PatchMode"]?.ToString(), "Overlay", StringComparison.OrdinalIgnoreCase));
                    if ((sprIsLoad || sprIsEdit)
                        && c["FromArea"] is null && c["ToArea"] is null && curFromFile is not null
                        && !sprIsOverlay)
                    {
                        var concrete = ResolveFromFileTokens(curFromFile, prefix, tail, pack);
                        if (concrete is not null)
                        {
                            string abs = "";
                            try
                            {
                                abs = Path.GetFullPath(Path.Combine(packDir,
                                    concrete.Replace('/', Path.DirectorySeparatorChar)));
                            }
                            catch { }
                            // ⚠ 精灵表天然大量透明（走路表 64×480）—— 不能套立绘那条
                            // 「实心像素≥35%」的整图替换判定，否则 Rasmodia 这类性转皮
                            // 的 Characters/Magnus 被当装饰叠加丢掉，预览回落男巫师（实测）。
                            if (abs.Length > 0 && File.Exists(abs) && !IsNoSpritesSource(abs))
                            {
                                if (!pack.SpriteFiles.TryGetValue(name, out var slist))
                                    pack.SpriteFiles[name] = slist = new();
                                if (!slist.Contains(abs, StringComparer.OrdinalIgnoreCase)) slist.Add(abs);
                                RecordVariant(pack, "Characters", name, abs);
                                // v1.7.10：同分支配对登记（脸+身体必须来自同一条互斥分支）
                                RecordBranch(pack, name, whenBools, false, abs);
                                RecordBaseSeasonPatch(pack, "Characters", name, c["When"], abs);
                            }
                        }
                    }
                    // FromFile 缺失/代换失败：从包里搜真图（与立绘同策略）——
                    // 否则 Characters 目录里的 Wizard.png 直铺命名会整包丢精灵。
                    // 只在「整表替换」意图下兜底，避免把 FromArea 装饰补丁的文件当成精灵。
                    if ((sprIsLoad || sprIsEdit)
                        && c["FromArea"] is null && c["ToArea"] is null
                        && !sprIsOverlay
                        && (!pack.SpriteFiles.ContainsKey(name) || pack.SpriteFiles[name].Count == 0))
                    {
                        var sprFallback = FindCharacterAsset(packDir, "Characters", name);
                        if (sprFallback is not null && !IsNoSpritesSource(sprFallback))
                        {
                            if (!pack.SpriteFiles.TryGetValue(name, out var slist))
                                pack.SpriteFiles[name] = slist = new();
                            if (!slist.Contains(sprFallback, StringComparer.OrdinalIgnoreCase)) slist.Add(sprFallback);
                            RecordVariant(pack, "Characters", name, sprFallback);
                        }
                    }
                    pack.SpriteChars.Add(name);
                    if (whenKeys.Count > 0) RememberWhenKeys(pack, name, whenKeys);
                    continue;
                }

                // portrait / animal：整表 Load 或「整张不透明替换」的 EditImage 才算换肤。
                // ⚠ EditImage 缺省是 Overlay 叠加 —— 对话粉底、电视节目、地图包之类对全角色
                // 立绘的装饰性叠加全是这个形态，直接算皮肤会满屏不相关皮肤（实机用户反馈）；
                // 但 Nyapu 这类肖像美化包也用无参 EditImage 整张替换（实测 95 个目标一张补丁，
                // 用户反馈"下载的肖像不显示"）。鉴别信号：替换图不透明像素占比 ≥90%
                //（装饰叠加图大量透明）。
                var isLoad = action.Equals("Load", StringComparison.OrdinalIgnoreCase);
                var isEdit = action.Equals("EditImage", StringComparison.OrdinalIgnoreCase);
                if (!isLoad && !isEdit) continue;
                // FromArea/ToArea = 局部改图（节日表情差分等），同样不是换肤
                if (c["FromArea"] is not null || c["ToArea"] is not null) continue;
                // 明确声明 Overlay:true → 作者自己承认是装饰叠加
                if (isEdit && string.Equals(c["Overlay"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 动态 FromFile 先试代换常见 token（SCC 这类大包整表用
                // {{TargetPathOnly}}/{{TargetWithoutPath}} 拼路径），代换不出就只让角色出现
                //（缩略图走占位/回退）
                // v1.7.5：FromFile 失败也要登记角色名 —— Donut's 的 {{AlesiaPortrait}}
                // 在 config 里是 false/true 时拼出 assets/false/... 根本不存在，旧逻辑
                // 静默跳过 → 该角色在包里"不存在"，页签只数出 14 个（实测）。
                // 回退 FindCharacterAsset 从画风子目录取真图。
                var registered = false;
                if (curFromFile is not null)
                {
                    // v1.7.7：先把「配置文件段上的 {{配置键}}」登记成画风候选 —— 放在代换
                    // 结果分支之前，因为 config.json 一旦被写成目录名（本功能自己就会写，
                    // 见 WritePackConfig），token 是解析得出来的；只在解析失败时才登记的话，
                    // 下次扫描这个包就退化成一张卡，用户选的画风会凭空消失（实测推演）。
                    RegisterStyleCandidates(pack, packDir, name, curFromFile);
                    var concrete = ResolveFromFileTokens(curFromFile, prefix, tail, pack);
                    if (concrete is not null)
                    {
                        string abs = "";
                        try
                        {
                            abs = Path.GetFullPath(Path.Combine(packDir,
                                concrete.Replace('/', Path.DirectorySeparatorChar)));
                        }
                        catch { }
                        // v1.6.6：FromFile 指向的文件不存在 → 静默跳过会让整包"消失"无从排查
                        //（转换包 FromFile 写错时实测）。留一条痕，与 [EditImage跳过] 同级。
                        if (abs.Length == 0 || !File.Exists(abs))
                            AppLog.Warn("Portraits",
                                $"[FromFile缺失] {pack.Folder}: {name} ← {concrete}（文件不存在，尝试兜底）");
                        if (abs.Length > 0 && File.Exists(abs))
                        {
                            // EditImage 还须「整张不透明替换」才算换肤；装饰叠加直接跳过，
                            // 也不登记 PortraitFiles 键（避免装饰包把角色凭空带上页）。
                            // 跳过时记日志 —— 肖像美化包被误判时用户报"包不显示"有迹可查。
                            if (isEdit && !IsOpaqueImage(abs))
                            {
                                AppLog.Warn("Portraits",
                                    $"[EditImage跳过] {name} ← {Path.GetFileName(abs)}（弱透明叠加，非整张替换）");
                                continue;
                            }
                            if (!pack.PortraitFiles.TryGetValue(name, out var list))
                                pack.PortraitFiles[name] = list = new();
                            if (!list.Contains(abs, StringComparer.OrdinalIgnoreCase)) list.Add(abs);
                            RecordVariant(pack, "Portraits", name, abs);
                            // v1.7.10：同分支配对登记
                            RecordBranch(pack, name, whenBools, true, abs);
                            RecordBaseSeasonPatch(pack, "Portraits", name, c["When"], abs);
                            registered = true;
                        }
                    }
                }
                if (!registered)
                {
                    // token 代换失败 / 文件不在 config 指定的画风目录 → 从包里搜真图
                    var fallback = FindCharacterAsset(packDir, "Portraits", name);
                    if (fallback is not null && (!isEdit || IsOpaqueImage(fallback)))
                    {
                        if (!pack.PortraitFiles.TryGetValue(name, out var list))
                            pack.PortraitFiles[name] = list = new();
                        if (!list.Contains(fallback, StringComparer.OrdinalIgnoreCase)) list.Add(fallback);
                        RecordVariant(pack, "Portraits", name, fallback);
                        registered = true;
                    }
                }
                // 仍然没有图也登记空表 —— 让角色出现在扫描结果里（走占位/原版回退），
                // 否则 config 关掉画风的角色整包消失
                if (!registered && !pack.PortraitFiles.ContainsKey(name))
                    pack.PortraitFiles[name] = new();
                if (whenKeys.Count > 0) RememberWhenKeys(pack, name, whenKeys);
            }
        }

        // v1.7.25：1.6 Appearance 条目里 Portrait/Sprite 可以指向【名字不带角色 id】的资产 ——
        // RRR 给法师挂的舞会脸叫 `Portraits/Rasmodia_FlowerDance`（TargetField=["Wizard","Appearance"]）。
        // RecordVariant 的筛子是"第一个下划线前的裸名必须是已知 NPC" ⇒ 这条整块丢掉 ⇒ 覆盖包永远
        // 不钉它 ⇒ 游戏在那个场合显示别人家的图（2026-09-28 用户："法师怎么还是这个大头照"、
        // "这张肖像系统里没有"）。地面真值不在文件名里，在 Appearance 条目里，照它登记。
        RegisterAppearanceVariants(pack, packDir, changes);
    }

    /// <summary>按包内 Appearance 引用登记场合资产：只认领【本包自己 Load/EditImage 出来的】
    /// 资产名，绝不替别家的图认领（否则会把别人的场合图钉成我们的）。</summary>
    private static void RegisterAppearanceVariants(PackScan pack, string packDir, JArray changes)
    {
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes.OfType<JObject>())
        {
            if ((c["Action"]?.ToString() ?? "Load") is not ("Load" or "EditImage")) continue;
            var tgt = c["Target"]?.ToString() ?? "";
            var us = tgt.IndexOf('/');
            if (us <= 0) continue;
            var kind = tgt[..us];
            if (kind != "Portraits" && kind != "Characters") continue;
            var ff = c["FromFile"]?.ToString() ?? "";
            if (ff.Length == 0 || owned.ContainsKey(tgt)) continue;
            var concrete = ResolveFromFileTokens(ff, kind, tgt[(us + 1)..], pack) ?? ff;
            try
            {
                var abs = Path.GetFullPath(Path.Combine(packDir,
                    concrete.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(abs)) owned[tgt] = abs;
            }
            catch { }
        }
        if (owned.Count == 0) return;
        foreach (var c in changes.OfType<JObject>())
        {
            if ((c["Target"]?.ToString() ?? "").Split('/').Last() != "Characters") continue;
            if (c["TargetField"] is not JArray tf || tf.Count < 2) continue;
            if (!string.Equals(tf[1]?.ToString(), "Appearance", StringComparison.OrdinalIgnoreCase)) continue;
            var npc = CanonCharId(tf[0]?.ToString() ?? "");
            if (npc.Length == 0 || c["Entries"] is not JObject entries) continue;
            foreach (var ep in entries.Properties())
            {
                var objs = ep.Value is JObject one ? new[] { one }
                    : ep.Value is JArray arr ? arr.OfType<JObject>() : Array.Empty<JObject>();
                foreach (var ev in objs)
                    foreach (var field in new[] { "Portrait", "Sprite" })
                    {
                        var asset = ev[field]?.ToString() ?? "";
                        // Appearance 里写的是完整资产名（"Portraits/Rasmodia_FlowerDance"），
                        // 逗号分隔的多张（随机/条件）逐张认。
                        foreach (var a in asset.Split(',', StringSplitOptions.TrimEntries))
                        {
                            var us2 = a.IndexOf('/');
                            if (us2 <= 0) continue;
                            var kind = a[..us2];
                            var name2 = a[(us2 + 1)..];
                            if (kind != "Portraits" && kind != "Characters") continue;
                            if (!owned.TryGetValue(kind + "/" + name2, out var file)) continue;
                            if (!VanillaNames.ContainsKey(npc) && !ModNames.ContainsKey(npc)) continue;
                            if (!pack.AssetVariants.Any(v => v.Kind == kind
                                    && string.Equals(v.VariantId, name2, StringComparison.OrdinalIgnoreCase)))
                                pack.AssetVariants.Add((kind, npc, name2, file));
                        }
                    }
            }
        }
    }

    private static void RememberWhenKeys(PackScan pack, string charId, List<string> keys)
    {
        if (!pack.WhenKeys.TryGetValue(charId, out var set))
            pack.WhenKeys[charId] = set = new(StringComparer.OrdinalIgnoreCase);
        set.UnionWith(keys);
    }

    /// <summary>When 的值是不是布尔开关取值？JSON 布尔与字符串 "true"/"false" 都算 ——
    /// CP 把 config 值一律按字符串处理，作者两种写法都有（Donut's 用字符串，实测）。</summary>
    private static string? WhenBoolValue(Newtonsoft.Json.Linq.JToken? v) => v?.Type switch
    {
        JTokenType.Boolean => (bool)v ? "true" : "false",
        JTokenType.String => v.ToString().Trim().ToLowerInvariant() is { } s
                             && (s == "true" || s == "false") ? s : null,
        _ => null,
    };

    /// <summary>静态判定一条 When 条件：true 成立 / false 不成立 / null = 判不了。
    /// 只认三类算得动的：① 本包 config 开关（值写布尔或字符串都算、键名可带空格）；
    /// ② <c>HasMod</c> 与 <c>"HasMod |contains=X"</c> —— 用 Mods 目录里真实存在的 UniqueID
    /// （禁用包不算：枚举时已跳过，SMAPI 本来也没加载它）；
    /// ③ <c>Query: '{{别的mod/Config: 键}}' = '值'</c> —— 只支持这一种形态，去读那个包的 config.json。
    /// 其余（Season / DayOfWeek / 跨 mod 派生 token）一律 null ⇒ 调用方按"不成立"处理：
    /// 宁可少认一条画风，也不能凭猜往用户机器上写 config。</summary>
    private static bool? EvalWhenCond(string keyRaw, Newtonsoft.Json.Linq.JToken? valueTok,
        PackScan pack, ModIndex idx)
    {
        var r = EvalWhenCondInner(keyRaw, valueTok, pack, idx);
        if (r is null) pack.NoteUnknown(keyRaw.Trim());   // 判不了也要留名，盲区才有验收线
        return r;
    }

    private static bool? EvalWhenCondInner(string keyRaw, Newtonsoft.Json.Linq.JToken? valueTok,
        PackScan pack, ModIndex idx)
    {
        var key = (keyRaw ?? "").Trim();
        if (key.Length == 0) return null;
        var expected = valueTok?.ToString().Trim() ?? "";
        var negated = expected.Equals("false", StringComparison.OrdinalIgnoreCase);

        // ② HasMod（"HasMod": "A, B" 与 "HasMod |contains=A": true 两种写法）
        if (key.StartsWith("HasMod", StringComparison.OrdinalIgnoreCase))
        {
            var pipe = key.IndexOf('|');
            string list;
            if (pipe >= 0)
            {
                var eq = key.IndexOf('=', pipe);
                if (eq < 0) return null;
                list = key[(eq + 1)..];
            }
            else list = expected;
            var uids = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (uids.Length == 0) return null;
            var all = uids.All(u => idx.Uids.Contains(u));
            return pipe >= 0 ? (negated ? !all : all) : all;
        }

        // ③ Query 条件。CP 的写法是 {"Query": "{{UID/Config: 键}} = '值'"} —— 表达式在【值】里；
        //    也见过把整串写进键名的包，两种都吃。跨 mod 版 token 名带斜杠：
        //    {{Spiderbuttons.CMCT/Config: <UID>, <键>}}（Dacar / Ohodavi / RRRR 的法师脸就挂
        //    在这种条件上，旧正则不许带斜杠 ⇒ 整条判不了、那包一张卡都不出，2026-09-28 实测）。
        var qe = key.StartsWith("Query:", StringComparison.OrdinalIgnoreCase)
            ? key[(key.IndexOf(':') + 1)..]
            : key.Equals("Query", StringComparison.OrdinalIgnoreCase) ? expected : "";
        if (qe.Trim().Length > 0)
        {
            // ⚠ 必须先试 CMCT 那条：通用正则的 UID 段允许有点号，会把 "Spiderbuttons.CMCT"
            // 当成 UID 抓走，查不到目录就 return null ⇒ CMCT 条件永远判不了（实测绕了一圈）。
            var m = System.Text.RegularExpressions.Regex.Match(qe,
                @"\{\{\s*Spiderbuttons\.CMCT/Config:\s*([^,{}]+?)\s*,\s*([^{}]+?)\s*\}\}[^=]*=\s*'?([^']*)'?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var uidG = 1; var keyG = 2;
            if (!m.Success)
            {
                m = System.Text.RegularExpressions.Regex.Match(qe,
                    @"\{\{\s*([^^{}/\s]+)\s*/\s*Config:\s*([^{}]+?)\s*\}\}[^=]*=\s*'?([^']*)'?\s*$");
                if (!m.Success) return null;
            }
            var uid = m.Groups[uidG].Value.Trim();
            var cfgKey = m.Groups[keyG].Value.Trim().Trim('\'', '"');
            if (!idx.DirByUid.TryGetValue(uid, out var otherDir)) return null;
            if (LoadConfigValues(otherDir) is not { } oc
                || !oc.TryGetValue(cfgKey, out var real)) return null;
            return string.Equals((real ?? "").Trim(), m.Groups[3].Value.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        // ① 本包 config 开关（当前值 → 没写过就用 ConfigSchema 的 Default）
        var known = (pack.ConfigValues is { } cfg && cfg.ContainsKey(key))
                    || pack.SchemaOptions.ContainsKey(key);
        if (!known || expected.Length == 0) return null;
        var have = "";
        if (pack.ConfigValues is { } c2 && c2.TryGetValue(key, out var raw)) have = (raw ?? "").Trim();
        if (have.Length == 0 && pack.SchemaOptions.TryGetValue(key, out var so2)) have = so2.Default;
        return have.Length == 0 ? null
            : string.Equals(have, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把这条 patch 解析出的图按「分支签名」配对登记：同一条互斥分支的脸与身体
    /// 才会落到同一条记录上。签名 = 该 patch 上全部布尔开关的 键=值（多键组合也认）。
    /// 只在"这条 patch 确实被布尔开关门控"时登记 —— 无开关的普通补丁一律不进这本账。</summary>
    private static void RecordBranch(PackScan pack, string assetName,
        List<(string Key, string Value)> whenBools, bool isPortrait, string file)
    {
        if (whenBools.Count == 0 || assetName.Length == 0 || assetName.Contains('/')) return;
        // 作者常写成 Portraits/Wizard_{{Season}}（Donut's 实测）→ 四季各登记一次，
        // 必须归到基名下才配得对。口径与 RecordVariant 一致：第一个 '_' 前的裸名 + 别名归一。
        var us = assetName.IndexOf('_');
        var name = us <= 0 ? assetName : CanonCharId(assetName[..us]);
        var key = whenBools[0].Key;
        var value = whenBools[0].Value;
        if (!pack.BranchAssets.TryGetValue(name, out var list))
            pack.BranchAssets[name] = list = new();
        var at = list.FindIndex(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Value, value, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            list.Add((key, value, isPortrait ? file : null, isPortrait ? null : file));
            return;
        }
        var b = list[at];
        if (isPortrait && b.Portrait is null) b.Portrait = file;
        if (!isPortrait && b.Sprite is null) b.Sprite = file;
        list[at] = b;
    }

    /// <summary>v1.7.10：这个 (包, 角色) 的「互斥分支画风」。够格的条件很窄 ——
    /// 同一个布尔开关、≥2 个不同取值、且**每个取值都解析出了脸**；任一分支没脸就不拆
    ///（那只是"开关关掉"，不是两套画）。画风名用那张脸所在的母目录名（作者按画风分目录时
    /// 目录名就是画师名，比 true/false 好认），开关值留给落盘用（见 Variant / VariantLabel）。</summary>
    private static List<(string Token, string Style, string? Portrait, string? Sprite)>? BranchVariants(
        PackScan p, string id)
    {
        // 别名侧同样要能查到（和 StyleVariants 同一个坑）
        foreach (var key in AssetLookupIds(id))
        {
            if (p.BranchAssets.TryGetValue(key, out var all) && all.Count >= 2)
                return ShapeBranches(p, key, all);
        }
        return null;
    }

    /// <summary>把某个资产名下的分支账变成画风候选：同一个布尔键、≥2 个取值、每个取值都解析出了脸。</summary>
    private static List<(string Token, string Style, string? Portrait, string? Sprite)>? ShapeBranches(
        PackScan p, string key, List<(string Key, string Value, string? Portrait, string? Sprite)> all)
    {
        var hit = new List<(string, string, string?, string?)>();
        foreach (var g in all.GroupBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Count() < 2) continue;
            // v1.7.14：只有【作者 ConfigSchema 声明过的开关】才算画风轴。CP 内置状态 token
            //（IsOutdoors / Season / DayOfWeek / Weather / IsFestivalDay …）是游戏运行时
            // 在"同一张肖像"的多张图之间自己切换，不是玩家能选的画风 —— 拿它拆卡会让
            // Claire 冬天冒出"室内/室外"两张几乎一样的卡，而一角色只能选一张 ⇒ 另一档
            // 在冬天那个地点根本没被钉。这类分支改由落盘端按文件名把 indoor+outdoor 一起钉
            //（见 Override 的 BuildSeasonFilesForChar / 冬天双钉）。
            // ⚠ 判据只用 SchemaOptions（ConfigSchema），不能用 pack.ConfigValues：CP 会把
            // 每一条 When 键（含 IsOutdoors）都自动写进包自己的 config.json ⇒ 用 config.json
            // 判会把运行时状态误当画风（Donut's 的 Claire 实测：config.json 里有 IsOutdoors）。
            if (!p.SchemaOptions.ContainsKey(g.Key)) continue;
            var withPortrait = g.Where(b => b.Portrait is { Length: > 0 }
                && File.Exists(b.Portrait)).ToList();
            if (withPortrait.Count < 2) continue;
            foreach (var b in withPortrait)
                hit.Add((g.Key, b.Value, b.Portrait, b.Sprite));
        }
        return hit.Count >= 2 ? hit : null;
    }

    /// <summary>资产名是已知角色 id 的下划线变体（Wizard_Spring → Wizard）时记为变体资产。
    /// VariantId 保持游戏原资产名（ParrotBoy_Winter），BaseId 归到角色 id（Leo）——
    /// 覆盖包按 VariantId 写 Target 才能打中 1.6 Appearance 引用的真实资产。</summary>
    /// <summary>CP 的 When.Season 四季词（大小写不敏感）。</summary>
    private static readonly HashSet<string> SeasonNames =
        new(StringComparer.OrdinalIgnoreCase) { "spring", "summer", "fall", "winter" };

    /// <summary>登记「基资产 + When:{Season}」的分季映射（见 <see cref="PackScan.BaseSeasonPatches"/>）。
    /// 只认：① 资产名是基名（不含 '_'，变体资产本身就是单季）；② When 里的 Season 值全部是
    /// 四季词（可以逗号并列，如 "spring, summer, fall"）。任一条不满足就整条不记 —— 猜错会
    /// 把四季钉成错的图，宁可不钉。</summary>
    private static void RecordBaseSeasonPatch(PackScan pack, string kind, string assetName,
        Newtonsoft.Json.Linq.JToken? whenTok, string file)
    {
        if (assetName.Length == 0 || assetName.Contains('_') || assetName.Contains('/')) return;
        if (whenTok is not Newtonsoft.Json.Linq.JObject w) return;
        var raw = w.Properties()
            .FirstOrDefault(p => p.Name.Equals("Season", StringComparison.OrdinalIgnoreCase))
            ?.Value.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return;
        var segs = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return;
        foreach (var s in segs)
            if (!SeasonNames.Contains(s)) return;
        var key = kind + "/" + assetName;
        if (!pack.BaseSeasonPatches.TryGetValue(key, out var map))
            pack.BaseSeasonPatches[key] = map = new(StringComparer.OrdinalIgnoreCase);
        foreach (var s in segs) map[s.ToLowerInvariant()] = file;
    }

    private static void RecordVariant(PackScan pack, string kind, string assetName, string file)    {
        var us = assetName.IndexOf('_');
        if (us <= 0) return;
        var baseId = CanonCharId(assetName[..us]);
        if (!VanillaNames.ContainsKey(baseId) && !ModNames.ContainsKey(baseId)) return;
        // ⚠ 后缀必须在【季节/场合白名单】里。"本体名 + 下划线 + 任意词"这个形状不只换肤通道有：
        // 本机实测 88 条不属于白名单，其中 78 条是 zLewdDewValley 的剧情状态图
        //（Abigail_LewDew / Marnie_LewDewExtra / Jas_Collar / Jas_Nude…），另有
        // Clint_Magician / Krobus_Trenchcoat / Governor_walking —— 这些都没有 1.6 Appearance 引用，
        // 只有画它的那个 mod 自己的代码/事件会去取。照旧登记就等于把别人的过场立绘换成我们选的
        // 脸（用户 2026-09-29 实测后拍板：这类一律不动）。真由 Appearance 引用的差分走
        // RecordAppearanceVariants 那条登记，不受这里限制（实测 Suki/Beatrice_IceFestival 就在其中）。
        foreach (var seg in assetName[(us + 1)..].Split('_', StringSplitOptions.RemoveEmptyEntries))
            if (!AppearanceSuffixes.Contains(seg)) return;
        if (!pack.AssetVariants.Any(v => v.Kind == kind && v.VariantId == assetName
                && string.Equals(v.File, file, StringComparison.OrdinalIgnoreCase)))
            pack.AssetVariants.Add((kind, baseId, assetName, file));
    }

    /// <summary>区域内可见像素占比 ≥5% 视为有内容（全透明/近空白返回 false）。</summary>
    private static bool RegionHasPixels(DecodedTexture tex, int x, int y, int w, int h)
    {
        var px = tex.PixelsRgba;
        var vis = 0;
        var threshold = Math.Max(1, w * h / 20);
        for (var yy = y; yy < y + h && yy < tex.Height; yy++)
            for (var xx = x; xx < x + w && xx < tex.Width; xx++)
                if (px[(yy * tex.Width + xx) * 4 + 3] > 16 && ++vis >= threshold) return true;
        return false;
    }

    /// <summary>v1.7.37：把一条图像声明记进「这份资产上还有谁」（对手高度取证）。
    /// 位置是重点 —— 放在换肤判据之前，所以装饰叠加、FromArea 局部差分、FromFile 指向
    /// 不存在文件的声明全都进账：这些恰恰会把我们的矮表压出残行。
    /// ⚠ 只记账，不参与任何落盘判据（v4 的 A+C 取舍由用例 B55 守着）。</summary>
    private static void RecordRivalDecl(PackScan pack, string packDir, string prefix,
        string tail, string name, string action, JToken c, string? fromFile)
    {
        var act = (action ?? "").Trim();
        // 「整表」= 会把这份资产整体换掉。Load 天然整表；EditImage 是【区域覆盖】语义，
        // 带 FromArea/ToArea 或作者自认叠加（Overlay / PatchMode:Overlay）的只涂一小块，
        // 不构成"它比我高"。⚠ 这个字段是 Max(H) 的唯一入口：放宽一格，一张 64×999 的
        // 表情差分就能把所有 NPC 误报成"矮了 26 行"（变异 M4b 专门打这里）。
        bool isLoad = act.Equals("Load", StringComparison.OrdinalIgnoreCase);
        bool isEdit = act.Equals("EditImage", StringComparison.OrdinalIgnoreCase);
        bool fullSheet = isLoad
            || (isEdit && c["FromArea"] is null && c["ToArea"] is null
                && !string.Equals(c["Overlay"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c["PatchMode"]?.ToString(), "Overlay", StringComparison.OrdinalIgnoreCase));

        string abs = "";
        if (fromFile is { Length: > 0 })
        {
            var concrete = ResolveFromFileTokens(fromFile, prefix, tail, pack, noteUnknowns: false);
            if (concrete is not null)
                try
                {
                    abs = Path.GetFullPath(Path.Combine(packDir,
                        concrete.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch { abs = ""; }
        }
        bool exists = abs.Length > 0 && File.Exists(abs);
        var (w, h) = ImgSize(exists ? abs : null);
        pack.RivalDecls.Add(new RivalSheet
        {
            Pack = pack.Folder,
            Asset = prefix + "/" + name,
            Action = act,
            Priority = c["Priority"]?.ToString(),
            From = fromFile,
            // 文件不存在/token 代换不出 ⇒ File 与尺寸都留 0：这条仍是"有人碰过这份资产"的
            // 证据（M2 打这里），只是撑不起 Max(H)。
            File = exists ? abs : "",
            W = w,
            H = h,
            When = c["When"]?.ToString(),
            FullSheet = fullSheet,
        });
    }

    /// <summary>代换 FromFile 里可静态确定的 CP token（目标已知时是确定值）。
    /// v1.3.4：补上 {{Target}}（完整目标资产名）—— Ridgeside Village 的全部默认立绘
    /// 都写的是 Assets/{{Target}}.png（"Portraits/Aguar" → "Assets/Portraits/Aguar.png"），
    /// 旧版不支持导致默认立绘代换失败记成空，角色唯一来源落到 Beach 差分上
    ///（整村 NPC 显示成泳装）。只处理 Target / TargetPathOnly / TargetWithoutPath /
    /// TargetName 四个；其余 token（季节、config 值…）代换不了返回 null。
    /// 代换结果不再含 {{ }} 才算成功。</summary>
    private static string? ResolveFromFileTokens(string fromFile, string targetPrefix, string targetName,
        PackScan? pack = null, bool noteUnknowns = true)
    {
        var fullTarget = targetPrefix + "/" + targetName;
        // 子路径目标（"AichaSBV/AichaSBV"）的 {{TargetName}} 取末段 —— 与 CP 语义一致
        var lastName = targetName.Contains('/')
            ? targetName[(targetName.LastIndexOf('/') + 1)..] : targetName;
        var s = fromFile
            .Replace("{{TargetPathOnly}}", targetPrefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{{TargetWithoutPath}}", targetName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{TargetName}}", lastName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{Target}}", fullTarget, StringComparison.OrdinalIgnoreCase);
        // v1.3.4：{{ModId}}_ 前缀剥离（与数据键同规则）
        s = s.Replace("{{ModId}}_", "", StringComparison.OrdinalIgnoreCase);
        // {{配置键}}：按包 config.json 的当前值代换（Elle's Cuter Horses 的
        // assets/Horse/{{Horse Skin}}.png → assets/Horse/PintoSilver.png）。
        // 代换不掉的（缺 config 或键不存在）保持原样 → 上层判 null 走不可用卡。
        // v1.7.5：config 值是 true/false/空 = 开关而不是画风目录名 —— 不能拼进路径
        //（Donut's 的 AlesiaPortrait:false 会拼出 assets/false/...），保持 token 让上层
        // 走 FindCharacterAsset 兜底。
        // ⚠ 包没写过 config.json 时（CP 首次启动才生成，[CP] Romanceable Rasmodia 实测就没有）
        // 用 ConfigSchema 的 Default 代换 —— 否则 {{Portrait Style}} 永远解不出，整包脸都看不见。
        if (s.Contains("{{") && pack is not null)
        {
            var cfg = pack.ConfigValues;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\{\{\s*([^{}|]+?)\s*\}\}",
                m =>
                {
                    var tk = m.Groups[1].Value;
                    var t = "";
                    if (cfg is not null && cfg.TryGetValue(tk, out var v)) t = (v ?? "").Trim();
                    if (t.Length == 0 && pack.SchemaOptions.TryGetValue(tk, out var so)) t = so.Default;
                    // 值是开关（true/false）或空 = 不是画风目录名，不能拼进路径
                    //（Donut's 的 AlesiaPortrait:false 会拼出 assets/false/...）→ 保持 token 让上层兜底
                    if (t.Length == 0 || t.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("false", StringComparison.OrdinalIgnoreCase))
                        return m.Value;
                    return t;
                });
        }
        // v1.7.10：还剩 {{X}} 就试 DynamicTokens 算出来的当前值 ——
        // Ohodavi's Portraits for Rasmodia 写的是 assets{{HatPortrait}}/Witch_{{Version}}.png，
        // 两个 token 都由 HasMod 门控的 DynamicTokens 决定，不代换就整包解析不出文件。
        if (s.Contains("{{") && pack?.TokenNow is { Count: > 0 } tn)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\{\{\s*([^{}|]+?)\s*\}\}",
                m => tn.TryGetValue(m.Groups[1].Value, out var tv) ? tv : m.Value);
        // 还是解不出来 ⇒ 把剩下的 token 记进"判不了"清单（哪个包卡在哪个 token 上）。
        // ⚠ 但"多值枚举开关"不算盲区：AllowValues ≥2 的键（Donut's 的 AlesiaPortrait）我们
        // 是走画风枚举出卡的，故意不代换 —— 记进来就是噪音，会把真盲区（{{Festival}} 这类）淹掉。
        // noteUnknowns=false：对手高度记账那条也要代换一次，同一个盲区记两遍会把
        // --audit-packs 的 40 条上限填满（上限本身就是防噪的）。
        if (noteUnknowns && s.Contains("{{") && pack is not null)
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(s, @"\{\{\s*([^{}]+?)\s*\}\}"))
            {
                var tk = m.Groups[1].Value;
                if (pack.SchemaOptions.TryGetValue(tk, out var sch)
                    && (sch.Values.Count >= 2 || sch.Values.All(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || v.Equals("false", StringComparison.OrdinalIgnoreCase)))) continue;
                if (pack.StyleCandidates.Values.Any(list => list.Any(c =>
                        string.Equals(c.Token, tk, StringComparison.OrdinalIgnoreCase)))) continue;
                pack.NoteUnknown("token {{" + tk + "}}");
            }
        return s.Contains("{{") ? null : s;
    }

    /// <summary>v1.7.7：把 FromFile 里「整段就是一个 {{配置键}}」的路径 token 登记成画风候选。
    /// 三条闸门缺一不可，少一条就会把别的包误拆成多张卡：
    /// ① token 前后都是 '/'（文件名段的 {{Horse Skin}}、资产名段的 {{Target}} 都不算）；
    /// ② 键是本包认识的配置键（ConfigSchema 或 config.json 里出现过）—— 挡掉 {{Season}}
    ///    这类 CP 内置 token；
    /// ③ assets/&lt;子目录&gt; 里真有该角色的立绘（Donut's 的 Code/ 放的是子补丁 json，自然落空）。
    /// config.json 已写成目录名的那条排最前 —— 「第一条卡」就是游戏当前显示的那套，
    /// 老配置（没记画风）回落第一条时画面不会跳。实测：本机 12 个角色命中
    ///（Alesia/Gunther/Jadu/Jolyne/Victor/Marlon/Sandy/Wizard…各 2~3 套画）。</summary>
    private static void RegisterStyleCandidates(PackScan pack, string packDir, string assetName, string fromFile)
    {
        // 每个 (包, 资产名) 只登记一次：里面要枚举目录，四季展开后同一条会被打多次
        if (assetName.Length == 0 || assetName.Contains('/') || assetName.Contains('.')) return;
        if (pack.StyleCandidates.ContainsKey(assetName)) return;
        var norm = fromFile.Replace('\\', '/');
        var tokens = new List<string>();
        // 收尾用 (?=/) 而不是吃掉那个 '/'：一行里连写两个段 token（assets/{{A}}/{{B}}/x.png）时
        // 正则不重叠会漏掉后一个
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(norm, @"/\{\{\s*([^{}/]+?)\s*\}\}(?=[/{])"))
        {
            var k = m.Groups[1].Value;
            var known = pack.SchemaKeys.Contains(k)
                        || (pack.ConfigValues is { } cfg && cfg.ContainsKey(k));
            if (known && !tokens.Contains(k, StringComparer.OrdinalIgnoreCase)) tokens.Add(k);
        }
        // 登记一次即定型：后面同名的其它补丁（变体/精灵）不再重复枚举
        pack.StyleCandidates[assetName] = new List<(string, string, string?, string?)>();

        // v1.7.9：① 声明驱动 —— token 由 DynamicTokens 派生时，照作者写的映射表生成画风。
        // 取值来自 ConfigSchema.AllowValues、文件名来自 DynamicTokens，两个都是作者自己声明的，
        // 比"扫目录猜哪个文件像立绘"准（实测 Miku 包的 assets/png 里还混着 1280×564 信纸
        // 与 320×752 地图，靠尺寸筛是猜出来的，不是认出来的）。
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(norm, @"\{\{\s*([^{}/]+?)\s*\}\}"))
        {
            var tk = m.Groups[1].Value.Trim();
            if (!pack.DynamicTokens.TryGetValue(tk, out var dt)) continue;
            if (!pack.SchemaOptions.TryGetValue(dt.Key, out var schemaOpt) || schemaOpt.Values.Count < 2) continue;
            var byDecl = new List<(string, string, string?, string?)>();
            foreach (var v in schemaOpt.Values)
            {
                if (!dt.ByValue.TryGetValue(v, out var sub)) continue;
                var abs = SafeFull(packDir, norm.Replace(m.Value, sub));
                if (abs is null || !File.Exists(abs) || !LooksLikePortrait(abs)) continue;
                byDecl.Add((dt.Key, v, abs, null));     // 画风名用开关取值（GMC 里看到的也是它）
            }
            if (byDecl.Count < 2) continue;
            AppLog.Info("Portraits", $"[画风登记·声明] {pack.Folder}:{assetName} ← {dt.Key} " +
                $"共 {byDecl.Count} 档（默认 {schemaOpt.Default}）");
            // Default 那一档排最前 = 游戏当前显示的那套；老配置没记取值时回落它不会跳画面
            var defAt = byDecl.FindIndex(x => string.Equals(x.Item2, schemaOpt.Default,
                StringComparison.OrdinalIgnoreCase));
            if (defAt > 0) { var hit = byDecl[defAt]; byDecl.RemoveAt(defAt); byDecl.Insert(0, hit); }
            pack.StyleCandidates[assetName] = byDecl;
            pack.DeclaredStyleAssets.Add(assetName);
            return;
        }
        var currentOf = new System.Func<string, string>(token =>
        {
            // 当前 config 值指向的那个画风放最前
            var cur = pack.ConfigValues is { } c && c.TryGetValue(token, out var cv) ? (cv ?? "").Trim() : "";
            if (cur.Length == 0 && pack.SchemaOptions.TryGetValue(token, out var so0)) cur = so0.Default;
            return cur.Equals("true", StringComparison.OrdinalIgnoreCase)
                || cur.Equals("false", StringComparison.OrdinalIgnoreCase) ? "" : cur;
        });
        if (tokens.Count == 0) return;   // 声明分支没接上时才要求目录段 token

        // v1.7.10：② 配置键的 AllowValues 本身就是画风目录名 —— [CP] Romanceable Rasmodia 的
        // "Portrait style" = Original / CreepyKat's / Nyapu / Dacar's，assets/Portraits 下就是这
        // 四个目录，但里面的文件叫 Witch_*.png（≠ 资产名 Wizard）。这种包**不能**走下面的目录枚举
        //（那条要求目录里有 <资产名>.png，一张都对不上 ⇒ 整包看不见），只能逐值代换整条 FromFile。
        foreach (var token in tokens.ToList())
        {
            if (!pack.SchemaOptions.TryGetValue(token, out var sch) || sch.Values.Count < 2) continue;
            if (sch.Values.All(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("false", StringComparison.OrdinalIgnoreCase))) continue;  // 布尔开关交给分支机制
            var byCfg = new List<(string, string, string?, string?)>();
            foreach (var v in sch.Values)
            {
                var probe = System.Text.RegularExpressions.Regex.Replace(norm,
                    @"\{\{\s*" + System.Text.RegularExpressions.Regex.Escape(token) + @"\s*\}\}", v,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var one = ResolveFromFileTokens(probe, "Portraits", assetName, pack);
                var abs = one is null ? null : SafeFull(packDir, one);
                if (abs is null || !File.Exists(abs) || !LooksLikePortrait(abs)) continue;
                byCfg.Add((token, v, abs, null));
            }
            if (byCfg.Count < 2) continue;
            AppLog.Info("Portraits", $"[画风登记·取值表] {pack.Folder}:{assetName} ← {token} " +
                $"共 {byCfg.Count} 档（默认 {sch.Default}）");
            var defIdx = byCfg.FindIndex(x => string.Equals(x.Item2, sch.Default,
                StringComparison.OrdinalIgnoreCase));
            if (defIdx > 0) { var hit = byCfg[defIdx]; byCfg.RemoveAt(defIdx); byCfg.Insert(0, hit); }
            pack.StyleCandidates[assetName] = byCfg;
            pack.DeclaredStyleAssets.Add(assetName);
            return;
        }
        foreach (var token in tokens)
        {
            foreach (var (style, portrait, sprite) in EnumerateStyleDirs(packDir, assetName, currentOf(token)))
                pack.StyleCandidates[assetName].Add((token, style, portrait, sprite));
            if (pack.StyleCandidates[assetName].Count > 0) break;   // 一个资产只跟一个配置键（多键取首个）
        }
        if (pack.StyleCandidates[assetName].Count < 2) pack.StyleCandidates.Remove(assetName);
    }

    /// <summary>把包内相对路径转成绝对路径；写法离谱（跳出包根）一律返回 null。</summary>
    private static string? SafeFull(string packDir, string rel)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(packDir,
                rel.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(packDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full : null;
        }
        catch { return null; }
    }

    /// <summary>v1.7.7：这个 (包, 角色) 够不够格拆成「每个画风一张卡」。返回 null = 不够
    ///（没有画风候选 / 只有一个画风）—— 调用方走老路径，产出的选项与改动前逐字段一致。
    /// 一条资产只跟一个配置键；万一有绑键的（实测没有），取画风最多的那个。</summary>
    private static List<(string Token, string Style, string? Portrait, string? Sprite)>? StyleVariants(
        PackScan p, string id)
    {
        // 别名侧也要能查到：包只声明 Portraits/Wizard，合并卡的 Magnus 那一行靠别名拿文件；
        // 画风账是按资产名登记的，不跟着找别名 ⇒ Magnus 那条退化成"整包第一条"，
        // 两个人钉成两张不同的脸（实测：选 CreepyKat's，Wizard 换了 Magnus 没换）。
        foreach (var key in AssetLookupIds(id))
        {
            if (p.StyleCandidates.TryGetValue(key, out var all) && all.Count >= 2)
            {
                var g = all.GroupBy(a => a.Token, StringComparer.OrdinalIgnoreCase)
                           .OrderByDescending(x => x.Count()).First();
                if (g.Count() >= 2) return g.ToList();
            }
        }
        return null;
    }

    /// <summary>v1.7.7：画风卡自己的 config 键 —— 把裸 token 键换成 "token=画风目录"。
    /// 键表里没有这个 token 时要补一条：合并卡成员 GuntherSilvian 用的键是 GuntherPortrait，
    /// 不含资产名、被组装循环那句按 id 的过滤挡掉（实测），不补就没法把画风写进源包 config.json。</summary>
    private static string[] StyleKeys(string[] keys, string token, string style)
    {
        var hit = false;
        var list = keys.Select(k =>
        {
            if (!k.Equals(token, StringComparison.OrdinalIgnoreCase)) return k;
            hit = true;
            return token + "=" + style;
        }).ToList();
        if (!hit) list.Add(token + "=" + style);
        return list.ToArray();
    }

    /// <summary>枚举 packRoot/assets/ 下真实含有该角色立绘的子目录（=画风），字母序稳定排列，
    /// current 命中的那个提到最前。立绘是硬门槛（没立绘的目录成不了卡），精灵只是附带登记。</summary>
    private static List<(string Style, string Portrait, string? Sprite)> EnumerateStyleDirs(
        string packDir, string assetName, string current)
    {
        var found = new List<(string Style, string Portrait, string? Sprite)>();
        string assetsRoot;
        try { assetsRoot = Path.Combine(packDir, "assets"); } catch { return found; }
        if (!Directory.Exists(assetsRoot)) return found;
        string[] subs;
        try { subs = Directory.GetDirectories(assetsRoot); } catch { return found; }
        foreach (var sub in subs.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            var style = Path.GetFileName(sub);
            if (style.Length == 0 || style.StartsWith(".")) continue;
            var (portrait, sprite) = PickStyleArt(sub, assetName);
            if (portrait is null) continue;
            found.Add((style, portrait, sprite));
        }
        if (current.Length > 0)
        {
            var at = found.FindIndex(f => string.Equals(f.Style, current, StringComparison.OrdinalIgnoreCase));
            if (at > 0)
            {
                // 先摘再插：Insert+RemoveAt 混着写下标会错一位（.NET 10 的 List 也没有 Move）
                var hit = found[at];
                found.RemoveAt(at);
                found.Insert(0, hit);
            }
        }
        return found;
    }

    /// <summary>在一个画风目录里挑这个角色的立绘与精灵：先 &lt;资产名&gt;.png，再
    /// &lt;资产名&gt;_Spring.png，再字母序；目录根上没有时再看一层子目录（KlevLovins 的
    /// Sprites/Wizard_Spring.png 就是这么放的）。同一张文件不当自己的精灵
    ///（画风目录里 64×64 的单帧图两边都像，不排掉会出现"立绘=精灵"的怪卡）。
    /// 判定复用产品里已有的尺寸探针，跟 FindCharacterAsset 同一套标准。</summary>
    private static (string? Portrait, string? Sprite) PickStyleArt(string dir, string assetName)
    {
        string? portrait = null, sprite = null;
        for (var level = 0; level < 2 && (portrait is null || sprite is null); level++)
        {
            var roots = new List<string> { dir };
            if (level == 1)
            {
                try { roots = Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { return (portrait, sprite); }
            }
            foreach (var root in roots)
            {
                var files = StyleFiles(root, assetName);
                if (files.Count == 0) continue;
                if (portrait is null)
                    portrait = files.FirstOrDefault(f => !IsNoPortraitsSource(f) && LooksLikePortrait(f));
                if (sprite is not null) continue;
                sprite = files.FirstOrDefault(f =>
                    !string.Equals(f, portrait, StringComparison.OrdinalIgnoreCase)
                    && !IsNoSpritesSource(f) && !IsNonWalkSprite(f) && LooksLikeSprite(f));
            }
        }
        return (portrait, sprite);
    }

    /// <summary>画风目录内该角色的候选文件，按「同名 → 春季 → 字母序」排。</summary>
    private static List<string> StyleFiles(string dir, string assetName)
    {
        var hits = new List<string>();
        try
        {
            hits.AddRange(Directory.EnumerateFiles(dir, assetName + ".png", SearchOption.TopDirectoryOnly));
            hits.AddRange(Directory.EnumerateFiles(dir, assetName + "_*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p.Contains("_Spring", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase));
        }
        catch { }
        return hits;
    }

    /// <summary>读包 config.json 的当前值（键 → 字符串）。解析失败返回 null，不影响扫描。</summary>
    private static Dictionary<string, string>? LoadConfigValues(string packRoot)
    {
        try
        {
            var p = Path.Combine(packRoot, "config.json");
            if (!File.Exists(p)) return null;
            using var sr = new StreamReader(p);
            if (JObject.Parse(sr.ReadToEnd()) is not JObject o) return null;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in o.Properties())
                if (kv.Value is not null
                    && kv.Value.Type is not (JTokenType.Object or JTokenType.Array))
                    d[kv.Name] = kv.Value.ToString();
            return d;
        }
        catch { return null; }
    }

    /// <summary>娘家关系记最小 rank：0 = 无条件 Data/Characters patch（真娘家），1 = When 门控的
    /// compat 改动。组装阶段每个角色只认 rank 最小的那个候选包。</summary>
    private static void RememberNative(Dictionary<(string Pack, string Char), int> owners,
        string pack, string charId, int rank)
    {
        if (owners.TryGetValue((pack, charId), out var cur) && cur <= rank) return;
        owners[(pack, charId)] = rank;
    }

    /// <summary>v1.7.28：认 <c>Mods/HDPortraits/&lt;角色资产名&gt;</c> 这一族目标 —— HD 肖像通道。
    /// Portraiture（config.json 的 <c>active: "HDP"</c>）与 HD Portraits 本体都按这条数据资产里的
    /// <c>Portrait</c> 字段画对话框大头照，<b>完全不读</b> <c>Portraits/&lt;npc&gt;</c>。
    /// 覆盖包不接管它，用户换任何皮肤那张脸都纹丝不动（2026-09-29 实机法师）。
    /// 索引式写法（Target 就是 <c>Mods/HDPortraits</c>、角色名写在 Entries 键上）只记一条盲区，
    /// 不猜着接管 —— 本机没有活实例，改错会把整张索引资产写坏。</summary>
    private static void RegisterHdPortraitEntry(PackScan pack, string tail)
    {
        var segs = tail.Split('/');
        if (!segs[0].Equals("HDPortraits", StringComparison.OrdinalIgnoreCase)) return;
        if (segs.Length < 2 || segs[1].Length == 0)
        {
            pack.NoteUnknown("Mods/HDPortraits 索引式写法（角色名在 Entries 键上），暂不接管");
            return;
        }
        var dataAsset = "Mods/HDPortraits/" + segs[1];
        if (!pack.HdEntries.Any(e => string.Equals(e.DataAsset, dataAsset, StringComparison.OrdinalIgnoreCase)))
            pack.HdEntries.Add((dataAsset, segs[1]));
    }

    private void Absorb(PackScan into, PackScan from)
    {
        foreach (var (id, files) in from.PortraitFiles)
        {
            if (!into.PortraitFiles.TryGetValue(id, out var list)) into.PortraitFiles[id] = list = new();
            foreach (var f in files)
                if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        }
        into.SpriteChars.UnionWith(from.SpriteChars);
        foreach (var (id, files) in from.SpriteFiles)
        {
            if (!into.SpriteFiles.TryGetValue(id, out var slist)) into.SpriteFiles[id] = slist = new();
            foreach (var f in files)
                if (!slist.Contains(f, StringComparer.OrdinalIgnoreCase)) slist.Add(f);
        }
        foreach (var (id, keys) in from.WhenKeys)
        {
            if (!into.WhenKeys.TryGetValue(id, out var set)) into.WhenKeys[id] = set = new(StringComparer.OrdinalIgnoreCase);
            set.UnionWith(keys);
        }
        into.SchemaKeys.UnionWith(from.SchemaKeys);
        foreach (var (k, v) in from.SchemaOptions) into.SchemaOptions.TryAdd(k, v);
        foreach (var (k, v) in from.DynamicTokens) into.DynamicTokens.TryAdd(k, v);
        into.DeclaredStyleAssets.UnionWith(from.DeclaredStyleAssets);
        foreach (var c in from.UnknownConds) into.NoteUnknown(c);
        // v1.7.7：画风候选也是 Include 子文件里登记的（Donut's 的真 patch 全在 assets/Code/*.json）
        foreach (var (id, cands) in from.StyleCandidates)
        {
            if (!into.StyleCandidates.TryGetValue(id, out var list))
                into.StyleCandidates[id] = list = new();
            foreach (var c in cands)
                if (!list.Any(x => x.Token == c.Token && x.Style == c.Style)) list.Add(c);
        }
        foreach (var (id, brs) in from.BranchAssets)
        {
            if (!into.BranchAssets.TryGetValue(id, out var blist))
                into.BranchAssets[id] = blist = new();
            foreach (var b in brs)
                if (!blist.Any(x => x.Key == b.Key && x.Value == b.Value)) blist.Add(b);
        }
        // 变体资产也是 Include 子文件里登记的（Baechu 的 Code/Wizard.json），不搬就全丢
        foreach (var v in from.AssetVariants)
            if (!into.AssetVariants.Contains(v))
                into.AssetVariants.Add(v);
        // HD 肖像通道同理：Dacar 的真 patch 全在 Include 进来的 assets/data/portraits.json 里
        foreach (var h in from.HdEntries)
            if (!into.HdEntries.Any(e => string.Equals(e.DataAsset, h.DataAsset, StringComparison.OrdinalIgnoreCase)))
                into.HdEntries.Add(h);
        // Include 子文件里登记的 HD 通道账目也要搬（Dacar 的真 patch 全在 assets/data/*.json）
        foreach (var e in from.ModsPrivateLoads) into.ModsPrivateLoads.Add(e);
        foreach (var (k, v) in from.HdPortraitPointers) into.HdPortraitPointers[k] = v;
        foreach (var t in from.NeededCrossTokens) into.NeededCrossTokens.Add(t);
        // 对手高度记账同样在 Include 子文件里（实测：Donut's / SCC-SVE / SVE / Ridgeside / WAG /
        // LewdDew 六个包的 patch 全在 assets/Code/*.json，不搬 ⇒ RivalSheets 里整个包消失，
        // 法师那条就是因此看不见 SCC-SVE 的 Magnus 四季表）。
        foreach (var r in from.RivalDecls) into.RivalDecls.Add(r);
    }

    /// <summary>Target=="Data/Characters" 的 EditData：Entries 键名 / Records 键 / Fields 第 2 元素
    /// 都是 mod 自带角色的 id 候选（SVE 的写法）。字段名噪音靠「最终必须有立绘」过滤。
    /// 同时抓取 Entries/Records 值与 Fields 覆盖里的 DisplayName —— 汉化包把中文名写在这个
    /// 字段（直接中文或 {{i18n:key}} token，用包内 i18n/zh.json 代换），供立绘页显示。</summary>
    private static void CollectDataCharacterIds(JObject change, HashSet<string> into,
        PackScan? pack, Dictionary<string, string> names, string? packRoot)
    {
        void AddCandidate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            // v1.3.4：{{ModId}}_ 前缀剥离 —— Downtown Zuzu（SpaceCore 自注册 NPC）的
            // 角色键是 {{ModId}}_Name，而它的立绘 Target 是裸名 Portraits/Name；
            // 两边必须归到同一个 id 才能配对（代换成完整 uid 反而对不上）。
            s = s.Replace("{{ModId}}_", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("{{ModId}}", "", StringComparison.OrdinalIgnoreCase);
            if (s.Contains(' ') || s.Contains('/') || s.Length > 40) return;
            into.Add(s);
        }

        void AddName(string? idRaw, JToken? v)
        {
            if (idRaw is null || v is null) return;
            var raw = v.Type == JTokenType.String
                ? v.ToString()
                : (v is JObject jo ? jo["DisplayName"]?.ToString() : null);
            if (string.IsNullOrWhiteSpace(raw)) return;
            var id = idRaw.Replace("{{ModId}}_", "", StringComparison.OrdinalIgnoreCase)
                          .Replace("{{ModId}}", "", StringComparison.OrdinalIgnoreCase);
            if (id.Length == 0 || id.Contains('/') || id.Length > 40) return;
            string? resolved = raw;
            var m = System.Text.RegularExpressions.Regex.Match(
                raw, @"\{\{\s*i18n:([^{}|]+?)\s*\}\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var i18n = packRoot is null ? null : LoadPackI18n(packRoot);
                resolved = i18n is not null && i18n.TryGetValue(m.Groups[1].Value.Trim(), out var zh) ? zh : null;
            }
            else if (raw.Contains("{{")) return;   // 其它代换不了的 token（config 等）
            if (string.IsNullOrWhiteSpace(resolved)) return;
            resolved = resolved.Trim();
            if (resolved.Length == 0 || resolved.Length > 40) return;
            names[id] = resolved;
        }

        if (change["Entries"] is JObject entries)
            foreach (var k in entries.Properties())
            {
                AddCandidate(k.Name);
                AddName(k.Name, k.Value);
            }
        if (change["Records"] is JArray records)
            foreach (var r in records)
            {
                if (r is JObject ro)
                {
                    var rid = ro["Key"]?.ToString() ?? ro["Id"]?.ToString();
                    AddCandidate(rid);
                    AddName(rid, ro);
                }
                else if (r is JArray ra && ra.Count > 0) AddCandidate(ra[0]?.ToString());
            }
        if (change["Fields"] is JArray fields)
            foreach (var f in fields)
            {
                if (f is not JArray fa || fa.Count < 2) continue;
                // 1.6 模型格式的 Fields: [角色id, "DisplayName", 中文名] —— 第 2 元素是
                // 字段名而非 id（旧斜串格式的 Fields [行号, id, …] 走原 AddCandidate 逻辑）
                if (string.Equals(fa[1]?.ToString(), "DisplayName", StringComparison.OrdinalIgnoreCase)
                    && fa.Count > 2)
                {
                    AddName(fa[0]?.ToString(), fa[2]);
                    continue;
                }
                AddCandidate(fa[1]?.ToString());
            }
    }

    /// <summary>读包内 i18n 中文词典（zh.json → zh-Hans.json → zh-CN.json），供
    /// DisplayName 的 {{i18n:key}} 代换。结果按包根缓存 —— 同一包扫描期只读一次。</summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>?> I18nCache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? LoadPackI18n(string packRoot)
    {
        return I18nCache.GetOrAdd(packRoot, root =>
        {
            try
            {
                var dir = Path.Combine(root, "i18n");
                if (!Directory.Exists(dir)) return null;
                foreach (var name in new[] { "zh.json", "zh-Hans.json", "zh-CN.json" })
                {
                    var f = Path.Combine(dir, name);
                    if (!File.Exists(f)) continue;
                    using var sr = new StreamReader(f);
                    if (JObject.Parse(sr.ReadToEnd()) is not JObject o) continue;
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in o.Properties())
                        if (kv.Value is not null && kv.Value.Type == JTokenType.String)
                            d[kv.Name] = kv.Value.ToString();
                    if (d.Count > 0) return d;
                }
            }
            catch { }
            return null;
        });
    }

    /// <summary>字符串是否含 CJK 汉字（判断 DisplayName 是不是真中文名）。</summary>
    private static bool HasCjk(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var ch in s)
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
        return false;
    }

}
