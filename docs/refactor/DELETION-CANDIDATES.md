# 删除候选清单（供其他 Agent 参考）

> 生成日期：本次重构审计。基线：`dotnet build` 0 错误，475 个 xUnit 测试全绿，git HEAD `10f426f`。
>
> **修订（第二版）**：经外部评审复核，本文件三处结论已更新 —— 见文末「十一、修订记录」。
> 最要紧的一条：`StashOverview` 经 `git log -S` 证明**从未被接线**，从"建议保留"改为"可删"。
> 仓库：C:\Users\21092\OneDrive\Desktop\JuniGrid（.NET 10 WPF，Nullable enabled）

## 先说结论

**这个仓库基本没有死代码。** 对 808 个 `private`/`internal` 声明做了逐成员引用计数扫描，
全仓库只有 **1 个**零引用成员（`AppLog.FlushTimer`），而且它是活着的 `System.Threading.Timer`，靠副作用工作，**不能删**。

44 个 `Services/*.cs` 文件**没有一个是整文件废弃**的。

所以真正可删的东西极少，而且大部分「看起来能删」的其实删了会出事 —— 见第三节的陷阱清单。

---

## 一、建议删除（低风险）

### 1.1 `NexusService.CacheHits` / `CacheMisses`

位置：`JuniGrid/Services/NexusService.cs:181-182`

```csharp
private static long _apiRequests, _cacheHits, _cacheMisses;   // :179
public static long ApiRequests => Volatile.Read(ref _apiRequests);
public static long CacheHits => Volatile.Read(ref _cacheHits);      // :181  ← 删
public static long CacheMisses => Volatile.Read(ref _cacheMisses);  // :182  ← 删
```

**零引用证据：**
```
grep -rn "CacheHits\|CacheMisses" --include=*.cs --include=*.razor JuniGrid test | grep -v /obj/ | grep -v /bin/
→ 只有 NexusService.cs:181 和 :182 两行声明
```

**只删这两个属性，绝不删底层字段。** `_cacheHits`/`_cacheMisses` 是活的：
- `:345`、`:354` 各有一处 `Interlocked.Increment(ref _cacheHits)`
- `:356` 有 `Interlocked.Increment(ref _cacheMisses)`
- 它们喂给 `BumpTraffic()` → `TakeTrafficDelta()` → 被 `Components/Pages/Nexus.razor:1240` 消费

⚠️ **注意：删之前先看这段注释（:176-178）**
```
/// <summary>本进程真实发出去的 Nexus API/GraphQL 请求数，和命中缓存的次数。
/// AUP 审的正是「一个客户端消耗多少请求」，界面上给出来比口头保证可信：Nexus 页每次进页
/// 打一行「本次进页 网络 N 次 / 缓存 M 次」，他截图就能回给审核方。</summary>
```
界面用的是 `TakeTrafficDelta()`（一帧快照），这两个累计属性确实没人调。但如果将来要做
「累计用量」面板，它们是现成的。**删是安全的，但保留成本为零。**

### 1.2 `ContentOverrideService.OrphanOverrideGroup.Revertible`

位置：`JuniGrid/Services/ContentOverrideService.cs:1879`

```csharp
public sealed record OrphanOverrideGroup(string Slot, string PackName, string PackDir,
    int EnabledCount, int SafeToRevert, int AlreadyReverted, int DriftedCount, int AddedOnly)
{
    /// <summary>能安全还原的条数（现值 == 我们放上去那份）。界面只在它 > 0 时才该给还原入口。</summary>
    public bool Revertible => SafeToRevert > 0;   // ← 删这一行（整个属性）
}
```

**零引用证据：**
```
grep -rn "Revertible" --include=*.cs --include=*.razor JuniGrid test | grep -v /obj/ | grep -v /bin/
→ 只有 ContentOverrideService.cs:1879 一行
```

`Packs.razor` 直接读兄弟字段 `SafeToRevert` 判断，不用这个派生属性。
删除零风险，但同样**保留成本为零**（一行表达式属性）。

---

## 二、需要你判断的（不建议我替你决定）

### 2.1 `SaveVersionService.StashOverview()`

位置：`JuniGrid/Services/SaveVersionService.cs:273-289`（约 20 行）

```csharp
/// <summary>留底区里现在有哪些档、各留了几份底（设置 → 缓存与存储 的「存档升级前的留底」那一行展开用）。
/// 按最新一份底的时间倒序。</summary>
public static List<(string SaveName, int Stamps, DateTime Newest, string Note)> StashOverview()
```

**零引用证据：**
```
grep -rn "StashOverview" --include=*.cs --include=*.razor --include=*.js --include=*.xaml . | grep -v /obj/ | grep -v /bin/
→ 只有 SaveVersionService.cs:273 一行
```

**~~不建议删的理由~~（已被证据推翻，见下）：** 原判断是"页面现在不调它，可能是 UI 改版时把入口去掉了，
它是唯一的枚举 API，删了想恢复得重写"。

**推翻它的证据（三条，均已复跑）：**

1. `git log --oneline -S "StashOverview" -- JuniGrid/` → **只命中一个提交** `bcf4f18`
   （"checkpoint: 肖像页与核心服务现状快照"）。也就是说它是**随那次快照一起生下来的**，
   从来没有第二个提交给它接过线。
2. 「存档升级前的留底」这七个字，全仓库**只出现在它自己的注释里**：
   ```
   grep -rn "存档升级前的留底" --include=*.cs --include=*.razor --include=*.json --include=*.js JuniGrid
   → 只有 SaveVersionService.cs:271（注释本身）
   ```
   任何 razor、任何 i18n json 都没有这个文案。
3. 界面里唯一的「留底」字样是 Packs 页的「与留底不一致」（`Packs.razor:323`），
   但它走的是 `ContentOverrideService.Strays`，与 `StashOverview` 无关。

**修正后的结论：这不是"被 UI 改版删掉的入口"，而是从没接通过的一段 API。可以删。**
（仍属 public API，删除需你点头。）

---

## 三、陷阱清单：看起来能删，实际不能（最重要的一节）

### 3.1 `ConfigService` 的 5 个 `Portrait*` 属性 —— 最大的坑

位置：`JuniGrid/Services/ConfigService.cs:559-563`

`PortraitSkins` / `PortraitSkinVariants` / `PortraitVanillaDefaults` / `PortraitLocks` / `PortraitTrueVanilla`

**每个属性的 grep 结果都只有自己那一行声明 —— 看起来完全没用。**

但它们上方的注释（`:551-558`）写明了：这是给**共享同一个 `junigrid.config.json` 的旧版 junigrid-blazor** 做
读写往返用的占位属性。删掉属性 → 下次保存时该 key 从 json 里消失 → 旧版丢用户的皮肤/锁定选择。

**结论：绝对不能删。零引用是设计，不是缺陷。**

### 3.2 `ConfigService.TotalPlayMinutes` —— 只写不读，但不能删

位置：`JuniGrid/Services/ConfigService.cs:520`

```
ConfigService.cs:520:    public long TotalPlayMinutes { get; set; }
LauncherService.cs:33:       c.TotalPlayMinutes += mins;      ← 唯一的写入点
```

读的地方一个都没有。但它是**持久化配置**，删了下次保存会丢 key。
和 3.1 同一个原因：`ConfigService.KeepUnknownTopLevel` 的存在就是为了兼容共享配置文件的另一个 app。

**结论：不删。也请不要「为了对称」给它加一个读取方。**

### 3.3 `AppLog.FlushTimer` —— 全仓库唯一的零引用成员，但不能删

位置：`JuniGrid/Services/AppLog.cs:38`

```csharp
private static readonly System.Threading.Timer FlushTimer = ...
```

`grep -rn "FlushTimer" --include=*.cs .` → 只有声明这一行。

**但它是一个活着的 `System.Threading.Timer`，靠构造函数副作用工作**（定时刷盘）。
删掉 → 日志不再定时落盘。这是「字段不通过名字引用」的经典假阳性。

### 3.4 四个路径净化函数 —— 不能合并，也不能删

| 函数 | 位置 | 空名回退 |
|---|---|---|
| `ContentOverrideService.Sanitize` | ContentOverrideService.cs:168-172 | 可返回 `""` |
| `ModService.SanitizeFolderName` | ModService.cs:3461-3467 | 回退 `"NewMod"` |
| `DepotDownloaderService.Staging.SanitizeFolderName` | DepotDownloaderService.Staging.cs:323-332 | 回退 `"unknown"` |
| `DepotDownloaderService.Staging.SafeDirName` | DepotDownloaderService.Staging.cs:470-471 | **删除**非法字符而非替换为 `_` |

统一它们会**改变已存在用户磁盘上的目录名**。

### 3.5 结构体字段 —— 看着没用，其实是内存布局

`MONITORINFO.dwFlags`（MainWindow.xaml.cs:655）、`APPBARDATA.uCallbackMessage`（:667）、
`MEMORYSTATUSEX` 全部字段（MemoryService.cs:270-277）、`WINTRUST_*` 全部字段（XnaRedistService.cs:149-168）

都是 `[StructLayout(LayoutKind.Sequential)]` 字段。删任何一个或调换顺序 →
后面所有字段偏移错位 → `GetMonitorInfo` / `SHAppBarMessage` / `GlobalMemoryStatusEx` 全部读到垃圾数据。

### 3.6 MainWindow 的 [尺寸检查点] 日志（约 60 行）

位置：`MainWindow.xaml.cs:1052-1085`（`OnWindowSizeChanged`）、`:1020-1043`（`CheckMaximizedGeometry`）

纯日志，看起来最容易删。但注释（:558、:991、:1052）说明这是 DPI/最大化几何回归的**专用探针**，
那个 bug 花了好几个版本才定位。删了下次回归就是瞎的。

### 3.7 三个手写日志器 —— 不合并

| 日志器 | 位置 | 输出文件 |
|---|---|---|
| `MainWindow.Log` | MainWindow.xaml.cs:13-23 | `startup.log` |
| `App.LogInfo` / `App.Log` | App.xaml.cs:15, 547-550, 719-728 | `crash.log` |
| `AppLog` | Services/AppLog.cs:10-40 | `juni-grid.log`（1MB 轮转） |

合并会：改诊断输出文件位置、破坏所有打印完整日志路径的消息字符串（:483、:702）、
丢掉 `startup.log` 存在的理由（`AppLog` 初始化之前的启动期日志）。

### 3.8 SHA-256 的 4 处副本 —— 不合并

| 实现 | 位置 | 失败模式 |
|---|---|---|
| `CommandBridgeService.Sha256Of` | :125-129 | **抛异常** |
| `ModService.Sha256Of` | :1015-1019 | 吞掉返回 `""` |
| `ContentOverrideService.Sha256` | :2389-2392 | 吞掉 + 记日志返回 `""` |
| `SelfUpdateService.VerifyInstaller` | :172-179 | 内联，带 `ToLowerInvariant` |

合并成一个 helper 必须额外提供「吞异常」重载，否则改变 `CommandBridgeService` 的行为。

### 3.9 `ModService.WriteManifestAtomic` —— 不并入 `AtomicFile`

位置：`ModService.cs:728-756` vs `AtomicFile.cs:22-49`

| | 重试次数 | 退避 | 总窗口 |
|---|---|---|---|
| `WriteManifestAtomic` | 4 | 50/100/150ms | **~300ms** |
| `AtomicFile.WriteAllText` | 5 | 10/20/30/40ms | **~100ms** |

`ModService.cs:741-743` 的注释描述的正是「杀毒软件锁住文件」场景。
合并后**早放弃 3 倍**，那个场景会开始失败。
要合并必须先给 `AtomicFile.WriteAllText` 加 `retryDelayMs` 参数。

---

## 四、可以收窄可见性的 public 成员（不是删除，是 API 变更）

`NexusUpdateTruth` 这 4 个成员是 `public`，但外部零调用（只在本文件内互相用）：

| 成员 | 位置 | 全仓库命中数 |
|---|---|---|
| `StampKey` | ModService.cs:3704 | 3（全在本文件） |
| `GetInstallRecord` | ModService.cs:3707 | 5（全在本文件） |
| `LocalVersionToCompare` | ModService.cs:3799 | 6（含 1 处 razor 注释提及） |
| `AlreadyHasRemoteFile` | ModService.cs:3811 | 4（含 1 处测试注释提及） |

改成 `private` 是安全的，但这是 **API 变更**，需要单独批准。

---

## 五、其他形态的冗余（不是「删代码」，是删重复结构）

| 项 | 位置 | 处理 |
|---|---|---|
| 不可达 `return false;`（CS0162） | ModService.cs:3053, :3057 | 删两行 |
| 孤立 XML 文档块（指向已删除的方法） | ModService.cs:758-767, 956-957, 1685-1692, 2933-2939, 3296-3298 | 删注释 |
| `StorageService.CopyTree` 纯转发壳 | StorageService.cs:752-753 | 删壳，调用点直调 `ModService.CopyDirectoryContents` |
| `ModService.IsGameProcessAlive` 纯转发属性 | ModService.cs:601-610 | 内联到 2 个调用点（:631, :672）；注意其注释声称会释放 Process 句柄，实际没有，注释是过期的 |
| `InstallService.NexusFilesUrl` 单行 URL 构造 | InstallService.cs:133-134 | 内联进唯一调用点 `PremiumNeedsWebFallback`（:146） |
| 空 CSS 规则 `.jg-main{}` | wwwroot/css/mods.css:328-331 | 删空块，留注释 |
| 空 media query | wwwroot/css/home.css:105-106 | 删 |
| `.jg-shimmer-line` 死声明 `height:14px` | wwwroot/css/effects.css:311 | 被 detail.css:52 覆盖，删后者生效的那条 |
| 重复 CSS 块（**级联顺序依赖**） | `.jg-modal-overlay` effects.css:213 vs misc.css:154；`.jg-modal` effects.css:217 vs misc.css:161 | **只删输掉的那条声明，绝不移动赢的那条** —— 移动会改渲染 |
| 冗余 `System.Windows.*` 限定名（7 处） | MainWindow.xaml.cs:434, 473, 482, 555, 928, 934, 1024, 908 | 去掉前缀（已有 `using System.Windows;`） |
| `EnsureUrlProtocolMarker` 返回值恒被丢弃 | App.xaml.cs:224-236 | 改 `void` |
| 孤立文档注释 | App.xaml.cs:545 | 删 |

---

## 六、明确不删的测试与前端

- **测试工程**（`test/JuniGridTestHarness`，37 文件 / 6481 行 / 475 用例）：无冗余文件、无重复用例。
  对 287 个可静态归属的测试方法做归一化方法体哈希，**0 对完全相同**。
  3 个零 `[Fact]` 的文件（GlobalUsings / TempDirs / TestSandbox）是共享 fixture，被引用 45 次。
- **前端**：`wwwroot/js` 实为 13 个文件（12 个 `junigrid.*` + 1 个 vendor gsap），`wwwroot/css` 13 个，不是 78 个。
  对 12 个 `junigrid.*.js` 做 5 行窗口块哈希，**0 个重复块**。JS 里重复的函数名都在各自 IIFE 作用域内。
- **`wwwroot/js/vendor/gsap.min.js`**：第三方 vendored 代码，不是应用代码。

---

## 七、给接手 Agent 的操作约束

1. **删之前必须自己重跑一次 grep 验证**，不要相信本文档里的行号（本次拆分后行号会全部偏移）。
2. **`git` 是你的回滚点**：HEAD = `10f426f`，任何删除都能 `git checkout -- <file>` 还原。
3. 删完必须：`dotnet build JuniGrid.sln` 0 错误 + 测试 exe 跑出 **475 个通过 0 失败**。
4. **测试文件一行都不许改**。它是验收安全网，改测试等于拆掉标准。
5. 不要顺手「修」`ModService.cs:838 / :2755 / :3216` 的 `JsonDocument.Parse` 不一致 ——
   那里确实是个 bug（没传 `ManifestJson`，带注释的 manifest 会静默丢 Version/Name），
   但**修它改变行为**，必须单独作为一个 bug 修复任务，不能混进重构。
6. `installer/JuniGridInstaller` 是**独立程序集**，不要与主程序合并，也不要复用主程序的 `AtomicFile`/`ModService`。
   （它的 `InstallRecycle.WriteManifest` / `InstallConfig.Patch` 用了**固定 tmp 文件名**，是真实的并发写隐患，
   但那是独立任务，不在删除范围内。）
7. `smapi-bridge` 编译目标是 net6.0 且 `DisableImplicitFrameworkReferences=true`，引用的是游戏自带 DLL。
   **视为冻结**，不要拆分或现代化，除非先证明 net6.0 引用集能解析新 API。

---

## 八、第二批候选（来自 ContentOverrideService / NexusService 分析，已由协调者独立裁决）

> 重要：以下候选是子代理提出的，**其中 2 项被协调者驳回**。驳回理由写在一起，供接手 Agent 判断。

### 8.1 ✅ 成立：`ContentOverrideService.Records(ConfigService)`

位置：`JuniGrid/Services/ContentOverrideService.cs:441-444`

```csharp
public static List<ContentOverrideRecord> Records(ConfigService cfg)
    => cfg.Current.ContentOverrides.Values
        .OrderBy(r => r.PackName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();
```

证据（精确 + 宽泛两轮 grep 都空）：
```
grep -rn "ContentOverrideService\.Records\|Records(Cfg)\|Records(cfg)" --include=*.cs --include=*.razor JuniGrid test
→ 无匹配
grep -rn "\.Records(" --include=*.cs --include=*.razor JuniGrid test
→ 无匹配
```
`Groups`（:479）自己做了排序，没有走它。**public API，删除需批准。**

### 8.2 ❌ 已驳回：`NexusModListEntry.CreatedAt` / `FileSize`

子代理称「grep 无匹配，可删」，**但它在同一份报告里自己写了矛盾证据**：

```
Positional new NexusModListEntry(...) sites (Packs.razor:1454, InstallService.cs:886, 1547) rely on argument order
```

实际核实：全仓库有 **5 处** `NexusModListEntry(` 构造点：
```
Components/Pages/Packs.razor:1454
Services/InstallService.cs:883
Services/InstallService.cs:1544
Services/NexusService.cs:1518
Services/NexusService.cs:1776  ← record 声明本身
```

这是 **record 的位置参数**（带默认值）。删字段 = 改构造函数参数个数 = 5 个构造点全部要改。
**收益为零，风险非零。驳回。**

### 8.3 ⚠️ 有争议：`OrphanOverrideGroup` 的 4 个计数字段

`SafeToRevert` / `AlreadyReverted` / `AddedOnly` / `DriftedCount`（:1876）

子代理证据：`Packs.razor` 只读 `PackName` 和 `EnabledCount`（:54-55）。

**但**：这些字段在 `AuditOrphans`（:1930-1940）里被**实际计算**，删除意味着还要改那段循环。
而且它们是「孤儿对账」的完整证据链 —— 界面现在不显示，不等于以后不显示。
**建议保留。** 若确实要删，必须同时简化 `AuditOrphans`，不能只删字段。

### 8.4 ✅ 成立（但属 API 收窄，非删除）：`ContentOverrideService.ForeignRoot`

位置：`:376`。不是死代码（:381 在用），但 `public` 且外部零调用 → 可改 `private`。

### 8.5 ❌ 已驳回：`StrayOverride.OriginalSize` / `OriginalCapturedAt`

位置：`:389-390`。同样是 record 位置参数，构造点在 :423 / :425。
删了要改构造点，收益与风险不成比例。**驳回。**

---

## 九、本批确认的「不做」新增项

| 项 | 位置 | 为什么不能做 |
|---|---|---|
| 合并 `NormPkgName` + `NormalizeForNameMatch` | NexusService.cs:774 / :1085 | 第一个是**过滤后**转小写，第二个是**逐字符过滤前**转小写。对 `İ → i̇` 这类长度会变的字符，两者输出不同，分别喂给 `PickMainForInstall` 的目录名和 `FindModByNameAsync` 的精确匹配 |
| 内联 `SendApiAsync` | NexusService.cs:201-212 | 它是 Nexus AUP 流量日志的**唯一计数点**（注释明写「计数只在这里加」）。内联后计数散落 |
| 把 `MaxConcurrentApiRequests` 从 `const` 改 `static readonly` | NexusService.cs:80 | `ApiGate = new SemaphoreSlim(MaxConcurrentApiRequests, ...)` 依赖它是编译期常量。改成 readonly 且拆到不同文件 → `ApiGate` 拿到 0 → **所有 API 调用死锁** |
| 提升 `ModPageEvidence` / `BrowseCache` / `DiskEntry` 为顶层类型 | NexusService | `InstallService.cs:1472` 写 `Dictionary<int, NexusService.ModPageEvidence>`，测试用 `NexusService.BrowseCache`。提级会破坏这些限定名 |
| 把 `IsSwappable` 的面积判据改回 `area > 0` | ContentOverrideService.cs:2161-2194 | 注释明写【绝对不能】。面积版会把 schedule/dialogue 表误判，实测坏了 26 个字符 |
| 删除 `MoveTree` 的复制计数守卫 | ContentOverrideService.cs:317-320 | 2026-10-02 就是没计数直接删，丢了 268 个活目录 |

## 十一、修订记录（第二版，经外部评审复核）

### 11.1 ❌ 已推翻：「三个静默隐患」的前提是错的

第一版 §二 声称「static SemaphoreSlim 被复制到第二个文件 → **能编译**，但两把锁 → 互斥失效，无测试报错」。

**这个说法错了。** C# 里 partial 同一个类重复声明同名成员 = **CS0102 / CS0111，编译期直接报错**。
测试根本不参与，不存在「编译过但运行时静默失效」这回事。

真正剩下的风险只有一条：**跨 partial 文件的静态字段初始化器，文本顺序不被语言规范保证**。
这只在「初始化器读另一个 static 字段」时才咬人。已核实：

- `UpdateService.CreateClient()`（:25-33）不读任何静态字段，且 `Http` 本来就留在主文件
- `ModService` 的 5 个静态字段初始化器互不引用
- `NexusService.ApiGate` 依赖的是 `const`（编译期折叠），安全

**保留的对策**（无害，照做）：static 字段与其工厂方法/依赖常量同文件。
**但心态要纠正**：不要因为「怕静默失败」而过度谨慎 —— 重复声明会编译报错，这是好事。

### 11.2 ✅ 已澄清：「拆出 ModEntry.cs / NexusUpdateTruth.cs」不是提级

第一版被指「自相矛盾：承诺不新增类，又拆出两个顶层类型」。**经核实，指控不成立。**

HEAD 版本 `ModService.cs`：

```
3659: }                                   ← ModService 类在这里就闭合了
3660: (空行)
3661: public sealed class ModEntry         ← 列 0，早就是顶层类型
3696: }
3698: /// <summary> ...
3702: public static class NexusUpdateTruth  ← 列 0，早就是顶层类型
```

`cat -A` 确认这两行**以列 0 开头、无缩进** —— 它们从来就不在 ModService 类内部。

**所以这是「把两个本来就顶层、只是物理上挤在同一文件里的类型搬到各自文件」，不是提级。**
引用名不变（`ModEntry`、`NexusUpdateTruth`，从不是 `ModService.ModEntry`）。

**但这条警告的下半段仍然成立且重要**：`NexusService.ModPageEvidence` 和 `NexusService.BrowseCache`
**确实是嵌套类型**（`InstallService.DependencyResolve.cs:206` 写着 `Dictionary<int, NexusService.ModPageEvidence>`），
**绝对不能提级**。区别在于：ModEntry/NexusUpdateTruth 本来就是顶层，那两个不是。

### 11.3 ✅ 已修正：`StashOverview` 从「建议保留」改为「可删」

见 §2.1 的更新。核心证据：`git log -S` 只命中一个快照提交，「存档升级前的留底」七个字全仓库只存在于它自己的注释里。
**它从未被接线，不是被 UI 删掉的。**

### 11.4 ✅ 已采纳：提交前必须注意的三个风险

1. **13 个新文件不在 git 里。** `git clean -fd` 一次就永久没了，`git checkout` 救不回未跟踪文件。
2. **分批验收无法归因。** 批次 1/2/3 全在同一个未提交工作树里，任何一次绿都是三批混测的结果。
   要单独证明某一批，必须分次提交后单独测。
3. **构建可能空转。** 增量构建在文件时间戳早于产物时会秒回「0 错误」而不真正编译。
   要拿到可信数字，用 `dotnet build --no-incremental`。

### 11.5 ✅ 已采纳：拆分脚本必须移出仓库

代理用过 `split.cjs` / `map.cjs` / `fix.cjs` 这类临时脚本，靠「第 14 行是否含 `partial`」做重入守卫。
拆完后第 14 行**正好就是** `public sealed partial class InstallService` ⇒ 守卫放行 ⇒
用为 1706 行原文件写的区间去切现在 1102 行的文件 ⇒ 越界、覆盖掉从未入库的新文件。

**当前状态：已核实仓库根目录无任何 `.cjs`（代理已自行清理）。**
**规矩：这类脚本一律放仓库外，绝不在工作树里跑。**

### 11.6 ✅ 已校准：测试覆盖率并非「全部低风险」

第一版把批次 2 整体标为「475 测试覆盖、风险低」。按测试文件里出现的类名数校准：

| 类 | 可见它的测试文件数 |
|---|---|
| ModService | 12 |
| ContentOverrideService | 6 |
| InstallService | 5 |
| UpdateService | 2 |
| NexusService | 2 |
| **StorageService** | **1** |
| SaveVersionService | **0** |
| MainWindow / App | **0** |

**StorageService 搬走的 253 行只有 1 个测试文件看得见** —— 实际保护弱于「475 测试全绿」给人的印象。

---

## 十、批次 1 已实际执行并验证通过的简化（供参考，已完成）

| 改动 | 文件 | 验收 |
|---|---|---|
| 删不可达 `return false;` ×2（CS0162） | ModService.cs `ArchiveHasManifest` | 编译 0 错误 |
| 删纯转发壳 `CopyTree`，调用点直调 `ModService.CopyDirectoryContents` | StorageService.cs | 同上 |
| 内联单行 URL 构造器 `NexusFilesUrl` | InstallService.cs → `PremiumNeedsWebFallback` | 同上 |
| 内联纯转发属性 `IsGameProcessAlive` ×2 | ModService.cs → `LauncherService.IsGameProcessRunning()` | 同上 |
| `EnsureUrlProtocolMarker` bool → void | App.xaml.cs | 同上 |

**验收：`dotnet build` 0 错误 + 475/475 测试通过 0 失败，与基线完全一致。**
