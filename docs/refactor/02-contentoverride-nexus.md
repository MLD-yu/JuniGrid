## Split plan

Targets: `JuniGrid/Services/ContentOverrideService.cs` (2395 lines, `public static class ContentOverrideService`) and
`JuniGrid/Services/NexusService.cs` (1818 lines, `public sealed class NexusService`).
Both become `partial` (`public static partial class` / `public sealed partial class`). Precedent in repo:
`DepotDownloaderService.cs` + `.Apply.cs` + `.FileOps.cs`.

Rules honoured: every new file is ONE **contiguous** range of the original (only `ContentOverrideService.Install.cs`
takes two blocks, declared below), so the split is verifiable by concatenation. Each new file repeats
`namespace JuniGrid.Services;` + the `partial` class header + its own usings + its own closing brace.
**All constants stay in the original file** (no path-helper literal is duplicated anywhere).
No type is created, no member is renamed, no signature changes.

### ContentOverrideService.cs — 2395 → 8 files

| new file | line ranges from ORIG | members moved | rationale |
|---|---|---|---|
| `ContentOverrideService.cs` (172, **retained**) | 1-172 | usings(1-4), namespace(6), class doc(8-50), class decl(51), `ContentRoot`(53), `AssetFullPath`(55-56), `LegacyOverridesDir`(65), `LegacyOriginalsDir`(66), `DerivedDir`(68), `LegacyStoreDir`(70), `VersionedMark`(72), `UnknownVersion`(74), `_rootWritable`(76), `SharedStoreFolder`(80), `SharedStoreDir`(84), `StoreRoot`(88-112), `OriginalsRoot`(115-116), `OriginalVersionSegment`(123-127), `OriginalPath`(131-132), `OriginalPath`(138-141), `PacksRoot`(144-145), `PackOriginalDir`(148-149), `DerivedRoot`(155), `StoreDir`(158-159), `StoreFile`(161-162), `KeyOf`(164), `SlotOf`(165-166), `Sanitize`(168-172) | 位置与键：全部路径常量/派生路径/键拼装。**这是唯一持有 `private const` 字面量的文件**，别的 partial 一律只调用不重复声明（`SharedStoreFolder` 仍 `public const`，DepotDownloaderService 在读）。`_rootWritable` 与 `StoreRoot` 同处一地，探测缓存不外迁。 |
| `ContentOverrideService.Store.cs` (266) | 174-439 | `_migrated`(176), `MigrateLegacyStore`(181-196), `VersionizeOriginals`(203-222), `BackfillVersions`(229-260), `PruneUnreferencedStores`(270-304), `MoveTree`(306-321), `PreserveOriginalPack`(329-359), `CopyTree`(361-369), `ForeignRoot`(376), `ForeignPath`(380-382), **`StrayOverride`**(388-390), `Strays`(403-428), `SameBytes`(431-439) | 磁盘库存：老位置搬家、原版仓库分层、无引用副本清理、原包备份、账外图暂存。`Strays` 归这里是因为它读的正是 `content-originals/<版本>` 与 `content-foreign` 两处，与 `ForeignPath` 同源。 |
| `ContentOverrideService.Install.cs` (289) | 441-695 **+** 2362-2394 | `Records`(441-444), **`PackGroup`**(451-471), `Groups`(479-497), `SetPackEnabled`(504-524), `StaleRows`(527-532), `RebuildForCurrentVersion`(540-551), `ForVersion`(554-555), `Prepare`(562-563), `Commit`(568-695) **+** `StoreCopy`(2362-2377), `DropStore`(2379-2387), `Sha256`(2389-2393), `Sha256Of`(2394) | 账本视图 + 落账 + 落盘。`SetPackEnabled → RebuildForCurrentVersion → Prepare/Commit` 是同一根调用链；尾部 4 个磁盘/哈希私有 helper 是 `Commit`/`Rejudge`/`Plan` 共用的叶子，跟着 `Commit` 走（第二段连续块，见下）。 |
| `ContentOverrideService.Rebuild.cs` (386) | 697-1082 | `_rebuildTried`(700), `NeedsRebuild`(705-728), `CacheZipPages`(732-754), `ZipEntrySets`(759-780), `EntryNames`(791-818), `PackDirSignature`(821-825), `PageIdForPack`(833-842), `BindPageIds`(853-880), `RekeySlot`(884-898), **`RebuildReport`**(907-908), `RebuildFromDisk`(934-1038), `OriginalLayers`(1041-1051), `ReadOriginal`(1054-1062), `StoreIndexByBytes`(1066-1082) | 盘→账重建 + 页号回填。压缩包条目清单那一串（`CacheZipPages/ZipEntrySets/EntryNames/PackDirSignature/PageIdForPack`）只被这两个入口用，且 `_rebuildTried` 是它们自己的进程级记忆。 |
| `ContentOverrideService.Rejudge.cs` (235) | 1084-1318 | **`RejudgeResult`**(1088), `Rejudge`(1109-1318) | 单个 209 行的方法 + 它的结果 record，天然一桶；强行并入 `Install.cs` 会把 289 推到 524 且职责混（重判 ≠ 新装）。 |
| `ContentOverrideService.Plan.cs` (325) | 1320-1644 | `SwitchVariant`(1330-1385), `PackImageSource`(1393-1416), `BytesMatch`(1419-1421), `ResolvePackBackup`(1425-1437), `TrimDupSuffix`(1441-1448), `SlotsNeedingRejudge`(1451-1457), **`PlanItem`**(1463-1464), `VariantFolderOf`(1468-1473), **`PlanResult`**(1475-1506), `Plan`(1509-1644) | 解析与试算：只解析不写盘的那一半。`SwitchVariant`/`PackImageSource` 都靠 `ResolvePackBackup`+`TrimDupSuffix` 回原包里取字节，与 `Plan` 同一套"包内路径→资产"的读法。 |
| `ContentOverrideService.State.cs` (444) | 1646-2089 | `SetEnabled`(1653-1702), `RevertAsset`(1707-1728), `RevertAsset`(1729-1730), `DropOriginal`(1735-1746), `Delete`(1753-1773), `DeletePack`(1778-1790), `DropPackBackup`(1803-1817), `RevertAll`(1823-1835), **`OverrideState`**(1842), `DiskState`(1847-1857), `StateOf`(1859-1860), `Drifted`(1864-1865), `ForeignOnDisk`(1870-1871), **`OrphanOverrideGroup`**(1875-1880), `AuditOrphans`(1904-1943), `LivePackNames`(1948-1985), `LocaleSiblings`(1990-2007), `LocaleTail`(2016), `LocaleBase`(2019), `ImageRows`(2025-2041), `SlotSiblings`(2046-2055), `ShownRowPredicate`(2059-2067), `ImageGroupState`(2076-2089) | 盘账一致性 + 开关/删除/还原 + 界面行归组。这三段原本就被原文的 `// ────` 分隔条切开，但共用 `DiskState/StateOf` 一族判定，拆开反而要跨文件读同一个概念。444 行在目标区间内。 |
| `ContentOverrideService.Resolve.cs` (270) | 2091-2360 | `_sizeCache`(2103-2104), `TextureSizeOf`(2106-2120), `BuildStemIndex`(2128-2155), `_swapCache`(2158-2159), `IsSwappable`(2177-2194), `ResolveTarget`(2208-2339), `IsSingleFramePortraitFill`(2357-2360) | 目标解析：14 字节头扫描 → 基名索引 → 能否整张换 → 逐文件定目标。`_sizeCache`/`_swapCache` 只被这一段用，跟着走。 |

Concatenation check: 1-172 ∪ 174-439 ∪ 441-695 ∪ 2362-2394 ∪ 697-1082 ∪ 1084-1318 ∪ 1320-1644 ∪ 1646-2089 ∪ 2091-2360
covers every line except the blank separators 173, 440, 696, 1083, 1319, 1645, 2090, 2361, 2395 — each of which is
regenerated per file, not "moved". No member appears in two files.

**static vs instance**: the whole class is `static`; there is not a single instance member (verified: `grep -n "async" ` and
every declaration carries `static`). Nothing to re-thread.

**Nested types and where they must travel**: `StrayOverride` → `.Store.cs` (only `Strays` builds it);
`PackGroup` → `.Install.cs` (only `Groups` builds it); `RebuildReport` → `.Rebuild.cs`; `RejudgeResult` → `.Rejudge.cs`;
`PlanItem`/`PlanResult` → `.Plan.cs` (`Prepare` returns `PlanResult`, but partial classes share types, so `.Install.cs`
compiles unchanged); `OverrideState` → `.State.cs` (all 12 uses of the enum live in `.State.cs` plus `Packs.razor`
via `ContentOverrideService.OverrideState`, which is unaffected); `OrphanOverrideGroup` → `.State.cs`.
Private helpers: `Sha256`/`Sha256Of`/`StoreCopy`/`DropStore` travel together into `.Install.cs` (4 call sites in
`.Rebuild.cs`, `.Rejudge.cs`, `.Plan.cs`, `.State.cs` — all satisfied by partial sharing); `SameBytes` travels with
`Strays`; `TrimDupSuffix`/`ResolvePackBackup` travel with `PackImageSource`; `LocaleTail` (static `Regex`) travels with
`LocaleBase` (its only consumer) — note `BuildStemIndex` in `.Resolve.cs` also uses `LocaleTail`, fine across partials.

**Cannot move without changing semantics**: none for this class. All six static field initializers
(`_rootWritable` :76, `_migrated` :176, `_rebuildTried` :700, `LocaleTail` :2016, `_sizeCache` :2103, `_swapCache` :2158)
are pure constructions that reference no other static field of this type and call no method with side effects, so their
initialization order is unobservable after the split. Do **not** "tidy" `_rootWritable`/`_migrated` into different files
than `StoreRoot`/`MigrateLegacyStore` — that is the only ordering coupling worth keeping co-located.

### NexusService.cs — 1818 → 7 files

| new file | line ranges from ORIG | members moved | rationale |
|---|---|---|---|
| `NexusService.cs` (423, **retained**) | 1-423 | usings(1-8), namespace(10), class doc(12-21), class decl(22), `Base`(24), `Http`(25), `CreateClient`(27-38), `_bearerToken`(42), `BearerToken`(43-47), `IsAuthenticated`(49), `EnsureFreshToken`(53), `RefreshGate`(54), `_lastRefreshTryUtc`(55), `EnsureFreshBearerAsync`(59-71), `MaxConcurrentApiRequests`(80), `ApiGate`(81), `_apiPauseUntilUtcTicks`(82), `MinRetryAfter`(85), `FallbackWhenNoHeaders`(86), `MaxInlineRetryWait`(87), `ApiGateQueueTimeout`(90), `PauseUntil`(92-101), `WaitIfPausedAsync`(103-114), `TryGetIntHeader`(116-123), `ObserveRateLimit`(126-146), `ApplyPauseFrom429`(149-169), `_apiRequests/_cacheHits/_cacheMisses`(179), `ApiRequests`(180), `CacheHits`(181), `CacheMisses`(182), `TakeTrafficDelta`(185-191), `_trafficReq/_trafficHit/_trafficMiss`(192), `BumpTraffic`(193-198), `SendApiAsync`(201-212), `SendApiInnerAsync`(214-273), `CacheTtlMs`(282), `CacheFailTtlMs`(283), `ResponseCache`(284), `InFlight`(285), `_cacheGen`(286), `ClearResponseCache`(289-297), `BrowseDiskDir`(300), `Key`(304-305), `BrowseDiskPath`(307-314), **`DiskEntry`**(317-321), `Cacheable`(327-328), `CachedAsync`×3(330-375), `ReadDisk`(377-394), `WriteDisk`(396-414), `SlowHttp`(417), `CreateSlow`(418-423) | 传输层 + 响应缓存：限流闸门、429/Retry-After、token 刷新、内存+磁盘缓存。所有别的 partial 都只调 `SendApiAsync`/`GraphQlAsync`/`CachedAsync`，所以这一桶必须留下。`DiskEntry` 与 `ReadDisk/WriteDisk` 同处一地（它是那两人的序列化壳）。 |
| `NexusService.Mod.cs` (268) | 425-692 | `GetCategoriesAsync`(427-443), `GetModAsync`(445-459), **`ModPageEvidence`**(467), `GetModPageEvidenceAsync`(469-479), `DescriptionModLinks`(486-505), `GetModDetailAsync`(508-567), `GetStr`(570-572), `GetInt64`(574-576), `SanitizeHtml`(583-692) | mod 元数据 + JSON/BBCode 解析。`GetStr/GetInt64` 被 `.Files/.Requirements/.Browse/.Account` 共用，partial 共享所以无需外迁；`SanitizeHtml` 只被 `GetModDetailAsync` 用。 |
| `NexusService.Files.cs` (243) | 694-936 | `GetLatestMainFileAsync`(698-704), `GetFileByIdAsync`(708-713), `ListFilesAsync`(715-737), `PickMainFile`(740-754), `PickMainForInstall`(757-770), `NormPkgName`(774-775), `LooksLikeAssetPack`(777-783), `GetModFingerprintsBatchAsync`(795-839), `GetChangelogsAsync`(842-858), `GraphQlEndpoint`(866), `_includeAdultContent`(872), `IncludeAdultContent`(873-877), `_adultFilterVersion`(880), `AdultFilterVersion`(881), `BumpAdultFilterVersion`(883), `_lastBrowseTotalCount`(886), `LastBrowseTotalCount`(887-891), `SurpriseSeed`(893), `ReshuffleSurprise`(894), `GameIdCache`(895), `GraphQlAsync`(897-919), `EnsureGameIdAsync`(923-936) | 文件清单/更新指纹/更新日志 **+ GraphQL 发射口**。`GraphQlEndpoint` 与 `GraphQlAsync` 必须同文件：唯一常量 + 唯一方法。成人开关与浏览态字段紧挨 `GraphQlAsync`（键拼装、Surprise 种子都在这里被读），所以一起走。 |
| `NexusService.Requirements.cs` (346) | 938-1283 | `GetModRequirementsAsync`(939-954), `ParseRequirementNodes`(959-968), `GetModFileVersionsAsync`(978-992), `ParseFileRows`(995-1005), `GetModRequirementsWithCoversAsync`(1008-1036), `FindModByNameAsync`(1045-1081), `NormalizeForNameMatch`(1085-1086), `GetModTranslationsAsync`(1089-1162), `GetDownloadHistoryAsync`(1173-1207), `GetDownloadUrlAsync`(1210-1227), `DownloadHttp`(1231), `CreateDownloadClient`(1233-1243), `DownloadFileAsync`(1250-1256), `GetNxmDownloadUrlAsync`(1264-1283) | 依赖/版本/按名搜索/译本 + 下载。`DownloadHttp`+`CreateDownloadClient`+`DownloadFileAsync` 三件一体（长超时客户端只服务下载），落在这一段是连续切分的自然结果。 |
| `NexusService.Browse.cs` (325) | 1285-1609 | `BrowseModsAsync`(1311-1330), **`BrowseCache`**(1334), `BrowseModsCoreAsync`(1336-1357), `FetchChunkedAsync`(1360-1399), `BrowseModsChunkAsync`(1402-1488), `CardSel`(1495-1496), `ParseModNodes`(1499-1524), `GetModsByUploaderAsync`(1536-1578), `BrowseByModIdAsync`(1590-1609) | 榜单/搜索：入口 + 缓存壳 + 分块补页 + 卡片字段表 + 节点解析 + 作者页。`CardSel` 只被这三条查询拼串用。 |
| `NexusService.Account.cs` (142) | 1612-1753 | `ValidateAsync`(1617-1622), `AccountFromJwt`(1626-1651), `GetUserExtrasAsync`(1655-1659), `GetUserExtrasCoreAsync`(1661-1679), `GetUploaderInfoAsync`(1686-1704), `GetUploaderAvatarAsync`(1711-1722), `FetchAvatarAsync`(1729-1734), `FetchAvatarCoreAsync`(1736-1753) | 账号与头像：JWT 解包、扩展信息、上传者信息、头像 data-URI。142 行是它的真实体量；并入 `.Browse.cs` 会变成 467 行的两个无关职责。 |
| `NexusModels.cs` (63) | 1756-1818 | `NexusUser`(1756), `NexusUserExtras`(1760-1761), `NexusModInfo`(1763), `NexusModFingerprint`(1769), `NexusFileInfo`(1771), `NexusChangelog`(1774), `NexusModListEntry`(1776-1784), `NexusAuthorMods`(1788), `NexusModDetail`(1790-1794), `NexusModDependency`(1797), `NexusRequirement`(1800), `NexusRequirementEx`(1803), `NexusTranslationItem`(1806), `NexusDownloadResult`(1808-1815), `NexusDownloadProgress`(1818) | **Free win.** 15 top-level records that have nothing to do with the class. Needs only `namespace JuniGrid.Services;` — no usings (no `JsonElement`, no HTTP types). Zero risk: they are already top-level, so no reference anywhere changes. |

Concatenation check: 1-423 ∪ 425-692 ∪ 694-936 ∪ 938-1283 ∪ 1285-1609 ∪ 1612-1753 ∪ 1756-1818 covers all but the blank
separators 424, 693, 937, 1284, 1610, 1611, 1754 (`}` of the class), 1755.

**static vs instance** — this class is mixed, and the split must not blur it:
instance members (need a `NexusService` instance, so their file must stay a normal class part):
`GetCategoriesAsync`, `GetModAsync`, `GetModPageEvidenceAsync`, `GetModDetailAsync`, `GetLatestMainFileAsync`,
`GetFileByIdAsync`, `ListFilesAsync`, `GetModFingerprintsBatchAsync`, `GetChangelogsAsync`, `GraphQlAsync`,
`EnsureGameIdAsync`, `GetModRequirementsAsync`, `GetModFileVersionsAsync`, `GetModRequirementsWithCoversAsync`,
`FindModByNameAsync`, `GetModTranslationsAsync`, `GetDownloadHistoryAsync`, `GetDownloadUrlAsync`,
`DownloadFileAsync`, `GetNxmDownloadUrlAsync`, `BrowseModsAsync`, `BrowseModsCoreAsync`, `FetchChunkedAsync`,
`BrowseModsChunkAsync`, `GetModsByUploaderAsync`, `BrowseByModIdAsync`, `ValidateAsync`, `GetUserExtrasAsync`,
`GetUserExtrasCoreAsync`, `GetUploaderInfoAsync`, `GetUploaderAvatarAsync`, `FetchAvatarAsync`, plus the instance
state `_lastBrowseTotalCount`/`LastBrowseTotalCount`/`SurpriseSeed`/`ReshuffleSurprise` and the **instance initializer**
`SurpriseSeed = Random.Shared.Next()`.
Everything else is `static` (`public static class`-style members living inside a sealed instance class).
`ValidateAsync` returns `Task.FromResult(...)` and stays instance-only for API symmetry — do not make it static during
the split (that would be a signature change).

**Nested types and where they must travel**: `DiskEntry`(317-321) → `NexusService.cs` with `ReadDisk/WriteDisk`;
`ModPageEvidence`(467) → `NexusService.Mod.cs` with `GetModPageEvidenceAsync`; `BrowseCache`(1334) → `NexusService.Browse.cs`
with `BrowseModsAsync`. **None of these three may be promoted to a top-level record**: `InstallService.cs:1472` writes
`new Dictionary<int, NexusService.ModPageEvidence>()`, and the tests reach `NexusService.BrowseCache` — promoting them
would break those qualified names.

**Cannot move without changing semantics**:
1. `Http`(25) = `CreateClient()`, `SlowHttp`(417) = `CreateSlow()` and `DownloadHttp`(1231) = `CreateDownloadClient()` are
   three `static readonly` fields whose initializers **call static methods**. They survive this split because each
   initializer only touches its own factory and each factory builds its own `HttpClient` without reading another static
   field. **Keep each field in the same file as its factory** (done: `Http`+`CreateClient`+`SlowHttp`+`CreateSlow` all in
   the retained file; `DownloadHttp`+`CreateDownloadClient` both in `.Requirements.cs`). Do not later move
   `CreateClient` to a "helpers" file while `Http` stays elsewhere.
2. `ApiGate = new SemaphoreSlim(MaxConcurrentApiRequests, MaxConcurrentApiRequests)`(81) depends on
   `private const int MaxConcurrentApiRequests`(80) — safe today **only because it is a `const`**. If anyone changes it to
   `static readonly int`, splitting the two lines apart would make `ApiGate` observe `0` and the app would deadlock on the
   first API call. Keep :80 and :81 adjacent (both in the retained file).
3. `EnsureFreshToken`(53) is assigned from `MainWindow.xaml.cs:304`; `BearerToken`(43-47) is written by
   `NexusOAuthService`. Both stay in the retained file so the public static surface that `App`/`MainWindow` touch is in one
   place.

## Simplifications (safe)

- <tag: delete> orphan XML doc line `/// <summary>Mod metadata (name + current version + cover). null on error.</summary>` — it describes `GetModAsync` but sits stacked directly above a *different* summary on `GetCategoriesAsync`; the real `GetModAsync` doc is elsewhere. Deleting it changes zero behavior. [JuniGrid/Services/NexusService.cs:425]
- <tag: delete> orphan XML doc block `/// <summary>Nexus API v1 与 GraphQL 的唯一发送出口…</summary>` (171-175) — it now documents the `_apiRequests` field, and `SendApiAsync` carries its own `/// <summary>唯一出口…` at :200. Doc-only. [JuniGrid/Services/NexusService.cs:171-175]
- <tag: delete> `private static async Task<T?> CachedAsync<T>(string key, Func<Task<T?>> factory, bool persist)` (the 3-argument overload) — zero callers: the only internal use is line 331 (4 args) and every external call site passes either 2 args (446, 470, 1658, 1733) or 4 with `persist:` (1321, 1545). [JuniGrid/Services/NexusService.cs:333-334]
- <tag: delete> `try { … } catch { }` wrapper inside `GetUploaderAvatarAsync` (1713-1721) — the only awaited call, `GetUploaderInfoAsync`, already ends with `catch { return null; }` (1703), so the outer catch can never run. Keep the `/missing` filter and the `return null`. [JuniGrid/Services/NexusService.cs:1713-1721]
- <tag: shrink> `MoveTree`'s 7-line copy loop (310-316) is byte-for-byte `CopyTree`'s body (363-368) → replace with `CopyTree(src, dst);` and keep the file-count guard (317-320) verbatim. Identical because `dst` is never inside `src` on any of the three call sites (188, 189, 192). [JuniGrid/Services/ContentOverrideService.cs:306-321]
- <tag: shrink> `PackGroup.StampedVersion` walks `Items.Select(r => r.Version)` twice and `Distinct` twice → `Items.Select(r => r.Version).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is [var only] ? only : ""`. Same result for the 0-, 1- and N-version cases. [JuniGrid/Services/ContentOverrideService.cs:468-471]
- <tag: stdlib> `System.Collections.Concurrent.ConcurrentDictionary<…>` is fully qualified at two field declarations while `using System.Collections.Concurrent;` is already in scope (and :76 uses the bare name) → drop the namespace prefix. [JuniGrid/Services/ContentOverrideService.cs:2103-2104, 2158-2159]
- <tag: stdlib> `System.Text.RegularExpressions.Regex.Match(…)` while `using System.Text.RegularExpressions;` is in scope (and :1472, :2016 use the bare name) → `Regex.Match(…)`. [JuniGrid/Services/ContentOverrideService.cs:741]
- <tag: stdlib> `System.Text.Encoding.UTF8` → `Encoding.UTF8` (`using System.Text;` at :6) [JuniGrid/Services/NexusService.cs:1634]; `System.Net.WebUtility.HtmlDecode/HtmlEncode` → `WebUtility.HtmlDecode/HtmlEncode` (`using System.Net;` at :3) [JuniGrid/Services/NexusService.cs:591, 622]

### Would change behavior (do not do)

- Merging `NormPkgName`(774-775) and `NormalizeForNameMatch`(1085-1086) into one normalizer: the first lowercases *after* filtering, the second lowercases *per character before* filtering. For characters whose invariant lowercase is not length-preserving (e.g. `İ` → `i̇`) the two produce different strings, which feeds `PickMainForInstall`'s folder-name hint and `FindModByNameAsync`'s exact-match. [NexusService.cs:774, 1085]
- Inlining `SendApiAsync`(201-212) into `SendApiInnerAsync`(214-273): saves a method but destroys the documented single counting point (`计数只在这里加，别的地方数不到`) that the Nexus AUP traffic log depends on.
- Caching `PlanResult.Rejects`(1484): every access currently allocates a fresh list; callers only enumerate, so it is safe today, but a cached list would change object identity for any future caller that mutates it.

## Deletion candidates (need user approval)

- `ContentOverrideService.Records(ConfigService)` [JuniGrid/Services/ContentOverrideService.cs:441-444] — zero-reference evidence: `grep -rn --include=*.cs --include=*.razor -E "ContentOverrideService\.Records|\bRecords\(Cfg\)|\bRecords\(cfg\)" JuniGrid test | grep -v '/obj/'` → **no matches**. `Groups`(479) does its own ordering, so nothing routes through it. (public API; needs sign-off)
- `NexusService.CacheHits` / `CacheMisses` [JuniGrid/Services/NexusService.cs:181-182] — zero-reference evidence: `grep -rn --include=*.cs --include=*.razor -w "CacheHits\|CacheMisses" JuniGrid test | grep -v '/obj/'` → only the two definition lines. The UI reads the same numbers via `TakeTrafficDelta()` (Nexus.razor:1240). Removing the properties leaves `_cacheHits`/`_cacheMisses` written-but-unread; they can be dropped in the same edit (increments at 345, 354, 356).
- `StrayOverride.OriginalSize`, `StrayOverride.OriginalCapturedAt` [JuniGrid/Services/ContentOverrideService.cs:389-390] — zero-read evidence: `grep -rn --include=*.cs --include=*.razor -w "OriginalSize" JuniGrid test | grep -v '/obj/'` → only the record declaration; same for `OriginalCapturedAt` → only :390. `Packs.razor` reads `Asset/Version/CurrentFile/OriginalFile/ForeignFile/Moved` only; tests read `Moved`. Changes the record's constructor (call sites 423, 425).
- `OrphanOverrideGroup.Revertible` [JuniGrid/Services/ContentOverrideService.cs:1879] — zero-reference evidence: `grep -rn --include=*.cs --include=*.razor -w "Revertible" JuniGrid test | grep -v '/obj/'` → only the definition.
- `OrphanOverrideGroup.SafeToRevert` / `AlreadyReverted` / `AddedOnly` [JuniGrid/Services/ContentOverrideService.cs:1876] — evidence: `grep -n "o\.\(SafeToRevert\|AlreadyReverted\|AddedOnly\|DriftedCount\)" JuniGrid/Components/Pages/Packs.razor` → **no output**; the only fields the UI reads are `PackName` and `EnabledCount` (Packs.razor:54-55). They are still computed in `AuditOrphans`(1930-1940) — removing them means also simplifying that loop, which is why this needs sign-off rather than a silent edit.
- `NexusModListEntry.CreatedAt`, `NexusModListEntry.FileSize` [JuniGrid/Services/NexusService.cs:1779-1780] — zero-read evidence: `grep -rn --include=*.cs --include=*.razor "\.CreatedAt\|\.FileSize" JuniGrid test | grep -v '/obj/'` → **no matches**. Both are populated from GraphQL (`GetStr(n,"createdAt")`, `fileSize` at 1515-1520), so removal also trims `CardSel`(1495-1496). Positional `new NexusModListEntry(...)` sites (Packs.razor:1454, InstallService.cs:886, 1547) rely on argument order, so this is a shape change, not a drop-in.
- `ContentOverrideService.ForeignRoot` [JuniGrid/Services/ContentOverrideService.cs:376] — not dead (used at :381) but `public` with zero external callers: `grep -rn --include=*.cs --include=*.razor -w "ForeignRoot" JuniGrid test | grep -v '/obj/'` → :376 and :381 only. Candidate for `private`, not deletion.

## Do NOT touch

- `IsSwappable`'s type-marker test (Texture2DReader/SpriteFontReader) and the explicit `【绝对不能】写 area > 0` note [ContentOverrideService.cs:2161-2194] — the area-based version demonstrably mis-classified schedule/dialogue tables and broke 26 characters.
- The width-vs-height asymmetry in `Plan`/`ResolveTarget` (height mismatch = install with a note, width mismatch = refuse) [ContentOverrideService.cs:21-35, 1570-1590] — measured, not guessed.
- `MoveTree`'s copy-count-then-delete guard [ContentOverrideService.cs:317-320] and `PruneUnreferencedStores`' arithmetic guards [ContentOverrideService.cs:262-304] — deleting without counting is how the 268 live directories were lost on 2026-10-02.
- `AuditOrphans`/`Strays` "只点名、不写盘" discipline [ContentOverrideService.cs:1898-1900, 392-402] — auto-reverting a drifted file would silently erase someone else's art.
- `Commit`'s "先写盘、后动账" ordering and its `freshlyStored`/`OriginalPath` cleanup branches [ContentOverrideService.cs:568-695] — reordering leaves half-written state.
- `SendApiAsync` as the single counting point, and `ApiGate`/`PauseUntil`/`ObserveRateLimit`/`ApplyPauseFrom429` [NexusService.cs:80-169, 201-273] — this is the Nexus AUP compliance machinery; every "simplification" here risks the 100/2500 req-per-day budget.
- Every `catch { return null; }` in NexusService (`FetchAvatarCoreAsync` 1752, `GetUploaderInfoAsync` 1703, `BrowseByModIdAsync` 1608, `GetDownloadHistoryAsync` ~1204, `GetModTranslationsAsync` 1161) — deliberate "a dead endpoint must never break the page" resilience.
- `SanitizeHtml` [NexusService.cs:583-692] and `DescriptionModLinks` [486-505] — the two-branch `[url=…]`/`[url]…[/url]` regex is pinned by a real-machine test (50188) and merging the branches silently drops the anchor-text case.
- `NexusService.EnsureFreshToken` assignment site [MainWindow.xaml.cs:304] and `BearerToken` writes [NexusOAuthService] — the static-class plumbing is deliberately callback-based to avoid a DI cycle.
- `ContentOverrideTargets.cs` (325 lines) — verified it only consumes public members (`LocaleBase` :233/:244, `PackImageSource` :266/:312, `OriginalPath` :273, `AssetFullPath` :275, `SetEnabled` :14 doc-ref). Nothing to move into it; do not fold it into the split.

## Risk notes

- **Test-harness surface**: 198 references from `test/JuniGridTestHarness` to `ContentOverrideService.*` and `NexusService.*` (all fully qualified, no `using X = ContentOverrideService;` alias exists in the repo — verified). A partial split leaves every one of them valid; renaming or promoting a nested type would break them.
- **`NexusService.ApiGate` deadlock** if `MaxConcurrentApiRequests`(:80) is ever turned from `const` into `static readonly` *and* the two lines end up in different files — `ApiGate` would then be constructed from `0`. Keep :80-:81 in the same file forever.
- **`Http`/`SlowHttp`/`DownloadHttp` initializer coupling**: each is `static readonly X = Factory()`. The split is safe only because every factory is self-contained. Moving `CreateClient` away from `Http` while leaving `SlowHttp` behind would make `SlowHttp`'s construction order matter.
- **`SurpriseSeed` is an instance initializer** (`Random.Shared.Next()`) living at :893, i.e. inside `NexusService.Files.cs` — if a future edit moves the property but not the `ReshuffleSurprise`/`BrowseModsAsync` key builder, "换一批" stops changing the cache key.
- **`GraphQlEndpoint` + `GraphQlAsync`** are the only GraphQL constants/entry point; both land in `.Files.cs`. If `GraphQlAsync` were later moved to the transport file while `GraphQlEndpoint` stayed, the split would still compile (partials) but the "one endpoint" invariant becomes unenforceable by reading one file.
- **`PlanResult`/`PlanItem` are consumed from `ModService.cs:2600` (`ContentOverrideService.PlanResult?`) and `Packs.razor`** — they stay nested, so those qualified names must not be "cleaned up" during the split.
- **`SharedStoreFolder` is `public const`** and read from `DepotDownloaderService.Apply.cs:629` and `FileOps.cs:667,669`. It must remain `public const string` in the retained file; moving or privatising it breaks the version-switch exclusion logic.
- **`Records`/`Groups` ordering is load-bearing for the UI**: `Groups` ends with `OrderBy(PackName)` and `Records` with `OrderBy(PackName).ThenBy(Asset)`. If `Records` is deleted (candidate above) do not "reuse" its ordering anywhere else — `Groups` already sorts.
- **Two-file-per-class compile order is not guaranteed by the C# spec** for static field initializers across partial parts. Nothing here depends on it today (all six/twelve initializers are independent), but any *new* static field whose initializer reads another static field must be placed in the same file as that field.
