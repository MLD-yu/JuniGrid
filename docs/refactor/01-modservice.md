## Split plan

Target: `JuniGrid/Services/ModService.cs` (3909 lines, `public sealed partial class ModService`).
Existing sibling `ModService.AssetPack.cs` (329 lines) already owns "裸素材包 → CP 包转换"
(CandidateAssetTargets / StripAssetExt / ResolveAssetTarget / HasForeignAssetTarget /
IsPortraitFamilyTarget / AssetExists / VanillaAssetSize / DeclaredAssetNames + cache /
ModsContentSignature / InstallLooseAssetPackAsCp / ReadSourceSize). **No bucket below duplicates it.**
AssetPack.cs depends on exactly two things from ModService.cs — `RenderPackContentJson`+`PackJsonOpts`
(:298-299) and `StageExistingToTrash` (:203); both land in **ModService.Archive.cs**, so AssetPack.cs
needs zero edits.

Every file below is one **contiguous** range of the original — no cherry-picking, so the split is
verifiable by concatenation. Each new file repeats the header
`namespace JuniGrid.Services;` + `public sealed partial class ModService` + its own usings + its own
closing brace.

| new file | line ranges from ORIG | members moved | rationale |
|---|---|---|---|
| `ModService.cs` (611) | 1-611 | usings(1-7), namespace(9), class decl(16), `static ModService()`(23), `ScanRaw`(31), `HiddenChildUids`(156), `CollectHiddenChildUids`(161), `Scan`(183), `sTmpSwept`(268), `CleanupDuplicateCopies`(270), `MainVersionOf`(334), `SetDisabled`(368), `Uninstall`(495), `TryRemoveEmptyTopShell`(585), `IsGameProcessAlive`(604) | 扫描 + 判重 + 启禁用 + 卸载 = the spine. Keeps the static ctor and the two static fields (`sTmpSwept`) that everything else treats as process-global. |
| `ModService.Manifest.cs` (305) | 612-916 | `ApplyRemarkToManifest`(622), `SyncAllRemarks`(668), `ReadManifestText`(718), `WriteManifestAtomic`(728), `ManifestJson`(770), `InstallUpdate`(783), `UidMismatchError`(886), `BundledParentOf`(895) | "manifest.json 的读写" one bucket: 备注同步写 Name、宽松共享锁读、原子写、严格解析选项、以及单包 zip 替换更新（唯一另一处写 manifest 的入口）。 |
| `ModService.Overlay.Probe.cs` (671) | 917-1587 | `OverlayBackupSuffix`(921), **`OverlayPlan`**(925), **`OverlayFail`**(927), **`OverlayReport`**(952), `NormalizeOverlayHost`(960), `OverlaySlotOf`(967), `ResolveHostDir`(975), `FindInstalledSubpackageByLeaf`(992), `Sha256Of`(1015), `ProbeOverlay`(1036), `VanillaAssetLayoutDirs`(1217), `IsPackDocFile`(1226), `IsVanillaAssetLayoutDir`(1236), `HostCandidateRows`(1243), `FindInstalledRequirementHost`(1252), `MatchDeclaredHosts`(1265), `DeclaredHostMatches`(1292), `HasRowTie`(1313), `InstalledHostsByPageId`(1319), `InstalledHostsByName`(1343), `SoleHostCandidate`(1369), `SoleTranslationJson`(1388), `HostI18nKeys`(1406), `HostI18nCovers`(1420), `FindHostsByI18nKeys`(1434), `RankHostsByI18nKeys`(1443), `ProbeTranslationOnlyPack`(1472), `ProbeOverlayForDeclaredHost`(1556) | 覆盖包**判定**：类型定义 + 「这个包该盖到谁身上」的全部证据链（包内路径 / 页面前置 / i18n 键 / 单 json）。 |
| `ModService.Overlay.Resolve.cs` (543) | 1588-2130 | `MapPackFilesOntoHost`(1593), `HostPathShapes`(1623), `PackUniqueIds`(1633), `DifferentModInThatFolder`(1655), `One`(1693), `SplitShellPlanByBundleChild`(1706), `ProbeOnePlanPerHostDir`(1736), `FindInstalledHostByExactDirName`(1754), `PackDeclaredConfigFields`(1776), `FindHostsByDeclaredConfigFields`(1797), `CoversConfigFields`(1816), `AddDistinct`(1834), `JoinHostNames`(1840), `HostSpellingKey`(1847), `FindInstalledHostBySpelling`(1863), `WithI18nHome`(1895), `JsonTopKeys`(1928), `OverlayRefusal`(1941), `ProbeTranslatedRepackage`(2015), `IdenticalOverlapRatio`(2095), `InstalledPageOf`(2121) | 覆盖包**落点解析**：宿主名归一（拼写/配置项/i18n 搬家）、逐文件映射、捆绑包壳拆分、以及"整包重打包的汉化"识别。 |
| `ModService.Overlay.Manage.cs` (464) | 2131-2594 | `ApplyOverlay`(2134), `MovedNote`(2238), `OverlayLive`(2243), `TryAdoptOverlay`(2252), `OverlayStoreId`(2281), `DropReplacedStore`(2299), `OverlayHealth`(2312), `OverlayKey`(2345), `OverlayAppliedForMod`(2353), `OverlaySiblings`(2367), `OverlayStoreDir`(2379), `RelocateBundleTopOverlays`(2388), `TryMoveOverlayCopy`(2421), `PurgeOverlaysOf`(2442), `SetOverlayEnabled`(2466), `RevertOverlay`(2514), `SetOverlayGroupEnabled`(2558), `RevertOverlayGroup`(2573), `GroupKeys`(2588) | 覆盖包**落盘与账本**：写文件/备份/副本目录、OverlayRecord 记账、健康度、开关、还原、卸载连带清理。UI 直接调用的全部 public 入口都在这里。 |
| `ModService.Install.cs` (324) | 2596-2919 | `OverrideChannelTakes`(2600), `InstallNew`(2605) | 新装主流程（含"本体覆盖通道"判据）。它自己不含任何 helper —— 全部依赖其他 partial，是天然的编排层。 |
| `ModService.Archive.cs` (590) | 2920-3509 | `PortraitureFrameworkUid`(2922), `PackJsonOpts`(2923), `RenderPackContentJson`(2929), `ImageShapeOf`(2941), `SidecarFileName`(2971), `WriteNexusIdSidecar`(2976), `ReadNexusIdSidecar`(2991), **`SidecarPayload`**(3025), `ArchiveHasManifest`(3040), `ArchiveReadable`(3068), `ManifestLeaf`(3091), `ExtractToTemp`(3095), `DetectArchiveFormat`(3139), `ExtractManagedArchive`(3170), `ResolveModRoot`(3199), `SafeManifestName`(3212), `ManifestsContainUid`(3223), `StripPackTag`(3242), `CommonPrefix`(3256), `MoveDirectorySafe`(3282), `CopyDirectoryContents`(3301), `StageExistingToTrash`(3324), `StageSubpackageToTrash`(3339), `TrashGuardInstance`(3362), `EnsureTrashReady`(3366), `ProtectTrash`(3375), **`TrashGuard`**(3388), **`NativeMethods`**(3421), `RestoreStaged`(3437), `ChooseFolderName`(3452), `SanitizeFolderName`(3461), `TryDelete`(3469), `TryReadManifestCleaned`(3478), `OrphanEntry`(3495) | 文件系统底层：边车 / 包内 json / 压缩包格式识别与解压 / 临时目录 / 跨盘搬运 / 回收站暂存与保护锁 / 目录命名。全是 `static`，无状态，自成一层。 |
| `ModService.Entry.cs` (149) | 3510-3658 | `BuildModEntry`(3515), `DepString`(3641), `DepIsRequired`(3652) | manifest.json → `ModEntry` 模型构建（含 Dependencies / ContentPackFor / MinimumVersion 解析）。150 行是它的真实体量；强行并入别的桶会破坏"单一职责"（可选：并入 Manifest.cs 变成 454 行双区段）。 |
| `ModEntry.cs` (36) | 3661-3696 | top-level `public sealed class ModEntry` | **独立顶层类型，零风险**。移出后 partial 类里不再夹带第二个类型。 |
| `NexusUpdateTruth.cs` (212) | 3698-3909 | top-level `public static class NexusUpdateTruth` + `NumericSegments`(3900) | **独立顶层类型，零风险**。N 网"装上了但更新判定不认"的判定/落盘逻辑，与 ModService 只通过 `ModEntry`/`JuniGridConfig` 交互。 |

(3659 = the class's closing brace, 3660/3697/2595 = blank separators — regenerate per file, not "moved".)

### static vs instance
Every file except three is **predominantly static**. Instance members (i.e. anything that would break if
someone made the class static) live only in: `ModService.cs` (`ScanRaw`, `Scan`, `HiddenChildUids`,
`CleanupDuplicateCopies`, `SetDisabled`, `Uninstall`), `Manifest.cs` (`ApplyRemarkToManifest`,
`SyncAllRemarks`, `InstallUpdate`), `Overlay.Probe.cs` (`ProbeOverlay`, `FindInstalledRequirementHost`,
`MatchDeclaredHosts`, `DeclaredHostMatches`, `InstalledHostsByPageId`, `InstalledHostsByName`,
`FindHostsByI18nKeys`, `RankHostsByI18nKeys`, `ProbeTranslationOnlyPack`, `ProbeOverlayForDeclaredHost`),
`Overlay.Resolve.cs` (`ProbeOnePlanPerHostDir`, `FindInstalledHostByExactDirName`,
`FindInstalledHostBySpelling`, `ProbeTranslatedRepackage`), `Overlay.Manage.cs` (`ApplyOverlay`,
`SetOverlayEnabled`, `RevertOverlay`, `SetOverlayGroupEnabled`, `RevertOverlayGroup`), `Install.cs`
(`InstallNew`). Everything else is `static`.

### nested types that must travel with their owners
- `OverlayPlan`/`OverlayFail`/`OverlayReport` (925/927/952) — declared once in **Overlay.Probe.cs**, but
  consumed by Overlay.Resolve.cs, Overlay.Manage.cs, Install.cs **and the test harness**
  (`ModService.OverlayPlan`, `ModService.OverlayFail`, `ModService.OverlayReport`). Same partial ⇒ fine;
  accessibility must not change (`internal`/`internal`/`public`).
- `TrashGuard`(3388) + `TrashGuardInstance`(3362) + `NativeMethods`(3421) — keep all three in
  **Archive.cs**; `NativeMethods` is referenced only by `TrashGuard.Acquire`.
- `SidecarPayload`(3025) — referenced only by `WriteNexusIdSidecar`(2976); same file.
- `PackJsonOpts`(2923) + `RenderPackContentJson`(2929) — referenced only by `ModService.AssetPack.cs`;
  same file.
- `VanillaAssetLayoutDirs`(1217) + `IsVanillaAssetLayoutDir`(1236) — same file (Probe).
- `OverlayBackupSuffix`(921) declared in Probe, consumed at 2169/2474/2522 in **Manage** — legal
  (same type), but do **not** "helpfully" duplicate the const.

### cross-half coupling (the question you asked)
The overlay half needs **exactly three** members from the core half:

| needed by overlay | declared at | call sites in overlay region |
|---|---|---|
| `ScanRaw(string)` | ModService.cs:31 | 1325, 1350, 1448, 1760, 1871, 2032 |
| `ReadManifestText(string)` | ModService.Manifest.cs:718 | 1639, 1670, 1822, 1932, 2022 |
| `ManifestJson` | ModService.Manifest.cs:770 | 1639, 1670, 1822, 1932, 2022 |

That is the entire overlay→core surface. Nothing else crosses (verified: no overlay-region reference to
`TryDelete`, `StageExistingToTrash`, `StageSubpackageToTrash`, `MoveDirectorySafe`, `ExtractToTemp`,
`ChooseFolderName`, `TryRemoveEmptyTopShell`, `IsGameProcessAlive`, `SetDisabled`, `Scan`).
Going the other way the core half needs **one** member back: `PurgeOverlaysOf` (Uninstall:571).
And Install.cs is the one real consumer of the overlay half:
`ProbeOverlay`(2643), `ProbeOverlayForDeclaredHost`(2639), `ProbeTranslatedRepackage`(2767),
`ApplyOverlay`(2651, 2769), `OverlayRefusal`(2721), `SoleHostCandidate`(2721), `One`(2642),
`OverlayPlan`/`OverlayFail`(2640-2643).

### warts the split will make visible (do not "fix" while moving)
- `PackUniqueIds`(1633) and `DifferentModInThatFolder`(1655) are **install-side** helpers physically parked
  inside the overlay region; their only callers are `InstallNew`(2810, 2818). They will land in
  Overlay.Resolve.cs. Moving them into Install.cs is semantically better but creates a second range —
  flagged as optional phase 2, not part of this plan.
- `One()`(1693) sits between the orphaned doc block for `SplitShellPlanByBundleChild`(1685-1692) and that
  method's real doc block (1695-1705).

## Simplifications (safe)

- `delete` unreachable `return false;` after `return a.Entries.Any(...)` inside the `case "rar"` / `case "7z"` arms -> remove both statements (CS0162) [ModService.cs:3053] and [ModService.cs:3057]
- `delete` orphaned/duplicated XML doc blocks (comment-only, zero IL): `758-767` (describes a removed "两遍法" string cleaner, now attached to `ManifestJson`) [ModService.cs:758-767]; `956-957` (describes `ResolveHostDir`, attached to `NormalizeOverlayHost`) [ModService.cs:956-957]; `1685-1692` (duplicate of `SplitShellPlanByBundleChild`'s real doc, attached to `One()`) [ModService.cs:1685-1692]; `2933-2939` (describes a removed "自愈重算 content.json" method, attached to `ImageShapeOf`) [ModService.cs:2933-2939]; `3296-3298` (second of two consecutive summaries on `CopyDirectoryContents`) [ModService.cs:3296-3298]
- `shrink` `MainVersionOf` tie-break: `top.Select(x => x.Folder).OrderBy(...).First()` + `top.First(x => x.Folder == firstFolder)` -> `var entry = top.OrderBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).First();` (stable sort ⇒ identical element) [ModService.cs:349-352]
- `shrink` `NumericSegments`: drop the redundant `seg.Length > 0 &&` guard — `SplitOptions.RemoveEmptyEntries` already guarantees it [ModService.cs:3906-3907]
- `shrink` `Scan` UID dedupe: `e.UniqueID?.Trim()` -> `e.UniqueID.Trim()` — `ModEntry.UniqueID` is non-nullable and defaults to `""` [ModService.cs:201]
- `yagni` `IsGameProcessAlive` private static property -> inline `LauncherService.IsGameProcessRunning()` at its 2 call sites (631, 672) and delete the wrapper. Its doc comment (601-603) claims it disposes `Process` handles, which the body does not do — the comment is stale, the wrapper is vestigial [ModService.cs:601-610]
- `native` `Sha256Of`: `SHA256.HashData(File.ReadAllBytes(file))` -> `using var fs = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(fs));` — same digest, no whole-file buffer; both paths still throw-then-catch to `""` [ModService.cs:1015-1019]
- `shrink` `TryReadManifestCleaned`: collapse to `try { var t = ReadManifestText(p); return string.IsNullOrWhiteSpace(t) ? null : t; } catch { return null; }` (drops a stale comment claiming it strips comments, which it no longer does) [ModService.cs:3478-3492]
- `shrink` `IsVanillaAssetLayoutDir`: `Array.IndexOf(VanillaAssetLayoutDirs, name.Trim().ToLowerInvariant()) >= 0` -> `VanillaAssetLayoutDirs.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase)` — identical set membership, one less allocation [ModService.cs:1236-1237]
- `shrink` `DetectArchiveFormat`: `int read; using (var s = File.OpenRead(path)) read = s.Read(...);` -> `using var s = File.OpenRead(path); var read = s.Read(head, 0, head.Length);` (pure syntactic) [ModService.cs:3141-3144]
- `shrink` `HostPathShapes`: the 2-item `yield` iterator is only ever consumed by `.FirstOrDefault(predicate)` (1604, 1615) — a `static string[]`-returning expression is equivalent and cheaper [ModService.cs:1623-1630]
- `shrink` reattach (not delete) `248-263`: this is `CleanupDuplicateCopies`'s summary but it sits above `sTmpSwept`(268); move it directly above `CleanupDuplicateCopies`(270) so `sTmpSwept`'s own summary (264-267) is the only one attached to the field [ModService.cs:248-270]

### would change behavior (listed, NOT recommended)
- `InstallUpdate` reads `Version` with `JsonDocument.Parse(ReadManifestText(manifest))` **without** `ManifestJson` -> any manifest carrying SMAPI-style comments/trailing commas silently yields `newVersion == null`. Switching to `ManifestJson` fixes it but changes observable output [ModService.cs:838]
- Same pattern for `modName` in `InstallNew` [ModService.cs:2755] and for `SafeManifestName` (bundle display name) [ModService.cs:3216]. Note :808/:2742/:3229 deliberately *do* pass `ManifestJson` with an explicit comment — the inconsistency is the bug, not the intent.
- Merging `ArchiveHasManifest`'s `case "rar"`/`case "7z"` arms (3050-3057) into one needs `ArchiveFactory.Open` instead of the two concrete archive types — different dispatch, not a pure shrink.

## Deletion candidates (need user approval)

**No member of ModService.cs is fully unreferenced.** Proof — a per-member reference count over the whole
file finds exactly 12 members with a single occurrence (the declaration itself), and every one of them is
referenced from outside the file:

```
grep -rn "\b<name>\b" . | grep -v /obj/ | grep -v /bin/ | grep -v publish/
  SyncAllRemarks          -> JuniGrid/Components/Pages/Mods.razor:3058
  BundledParentOf         -> JuniGrid/Services/InstallService.cs:812
  OverlayHealth           -> test/.../WorkerCancellationAndOverlayTests.cs:663
  OverlayAppliedForMod    -> JuniGrid/Components/Pages/ModDetail.razor:488,660
  SetOverlayGroupEnabled  -> JuniGrid/Components/Pages/Mods.razor:1819
  RevertOverlayGroup      -> JuniGrid/Components/Pages/Mods.razor:1828
  RenderPackContentJson   -> JuniGrid/Services/ModService.AssetPack.cs:299
  ArchiveHasManifest      -> JuniGrid/Services/InstallService.cs:688
  ArchiveReadable         -> JuniGrid/Services/InstallService.cs:161
  FindInstalledEntry      -> JuniGrid/Services/InstallService.cs:293,614
  RecordInstallByModId    -> JuniGrid/Services/InstallService.cs:301,621
  TryHealFromPriorDownload-> JuniGrid/Components/Pages/Mods.razor:4646,4858
```

What *is* redundant is **public surface with zero external callers** — narrowing it is the delete-shaped
change, but it alters the API, so it needs your sign-off:

- `NexusUpdateTruth.StampKey` [ModService.cs:3704] — `public`, but grep `\bStampKey\b` over `*.cs`/`*.razor` returns only ModService.cs:3704, 3709, 3737 ⇒ `private` is provably safe
- `NexusUpdateTruth.GetInstallRecord` [ModService.cs:3707] — `public`; only ModService.cs:3707, 3792, 3801, 3813, 3866
- `NexusUpdateTruth.LocalVersionToCompare` [ModService.cs:3799] — `public`; only ModService.cs:3799, 3884 (+ prose mentions in a Mods.razor comment at :4909)
- `NexusUpdateTruth.AlreadyHasRemoteFile` [ModService.cs:3811] — `public`; only ModService.cs:3811, 3862, 3883 (+ a prose mention in `RemoteVersionRestoreTests.cs:9`)
- `ModService.PortraitureFrameworkUid` [ModService.cs:2922] — used only by InstallService.cs:874/938/1019; keep public (InstallService is a different type), but note the section comment "肖像转换器已移除" no longer matches any code in this file

## Do NOT touch

- `static ModService()` [ModService.cs:23] — code-page 936 registration; documented root-cause fix for "mod 能不能装取决于哪个服务先被实例化". Exactly one file may declare it.
- `ReadManifestText`'s `FileShare.ReadWrite | FileShare.Delete` [ModService.cs:718-724] and `WriteManifestAtomic`'s temp-file + 4× retry [ModService.cs:728-756] — documented E10/E11 fixes.
- Every bare `catch { }` / `catch { return X; }` in the file (e.g. 176, 193, 284, 355, 551, 706, 754, 814, 861, 1011, 1018, 2284, 3062, 3088, 3219, 3488) — deliberate resilience against a filesystem that is being mutated by the game/Explorer/antivirus. They are load-bearing, not laziness.
- Path-traversal guards: `dstPath.StartsWith(hostRoot, ...)` [ModService.cs:2162] and `ExtractManagedArchive`'s zip-slip check [ModService.cs:3170-3189].
- `SanitizeFolderName`/`ChooseFolderName` U+FFFD (lossy GBK) logic [ModService.cs:3444-3467] — irreversible if removed.
- `Scan`'s UID dedupe in-place-replacement block [ModService.cs:215-227] and the `Folder` fallback dedupe [ModService.cs:235-244] — both carry comments naming the exact crash they prevent.
- `CleanupDuplicateCopies`' `.tmp` sweep + `Interlocked.Exchange(ref sTmpSwept, 1)` gate [ModService.cs:268-284] — a deliberate 358ms-per-rescan optimisation.
- `StageExistingToTrash`/`RestoreStaged` rollback pairs [ModService.cs:3324, 3437] and every `stagedOld`/`stagedDest`/`stagedDisabled` restore path — this is the "任何中断旧版都不丢" guarantee.
- `ArchiveHasManifest`'s `catch { return true; }` [ModService.cs:3062] — "读不动就当它有身份" is intentional (fails toward normal install).
- `ManifestJson`'s `CommentHandling.Skip` + `AllowTrailingCommas` [ModService.cs:770-774] — SMAPI dialect; strict parsing would report good mods as corrupt.
- `DepIsRequired`'s `try { return (bool)tk; } catch { return true; }` [ModService.cs:3652-3658] — tolerates `false`/`"false"`/`0`; "读不懂按必需" is deliberate.
- `TrashGuard`'s omission of `FILE_SHARE_DELETE` [ModService.cs:3404-3408] — that omission *is* the protection mechanism.
- `ScanRaw`'s materialised `dirs` list [ModService.cs:41-45] and the empty-shell cleanup block [ModService.cs:63-97] — both have incident dates attached.
- All `LocService.Tr`/`LocService.Tf` strings — tests assert on substrings (e.g. `Assert.DoesNotContain("翻译", ...)`).

## Risk notes

- **Static field init order.** Five static-field initializers get scattered: `sTmpSwept`(268)→ModService.cs, `ManifestJson`(770)→Manifest.cs, `VanillaAssetLayoutDirs`(1217)→Overlay.Probe.cs, `PackJsonOpts`(2923)→Archive.cs, `TrashGuardInstance`(3362)→Archive.cs. C# runs partial-class static initializers in an order the spec does not pin across files. Today none of them reads another, so the move is safe — but this becomes a real hazard the moment someone adds a cross-field dependency. The static ctor (23) runs *after* all field initializers, which is what the code-page registration needs; do not convert it to a field initializer.
- **`TrashGuardInstance` must stay in the same file as `TrashGuard` and `NativeMethods`.** It is a private nested type initialised by a private static readonly field; splitting them still compiles but makes the ownership invisible.
- **`OverlayPlan`/`OverlayFail`/`OverlayReport` are referenced by the test harness as `ModService.OverlayPlan` etc.** Any change of nesting (e.g. "let's promote them to top-level records") breaks 20+ test lines and `Mods.razor:1713/3097`. File move only.
- **`docs/refactor/api-before.txt` is the API snapshot** — after the split, every line of it must still match. Nested types keep their `ModService.X` names and member signatures are unchanged, so the snapshot stays valid *if and only if* no modifier is accidentally altered during the move (`internal`→`private` on `ManifestJson`, `OverrideChannelTakes`, `ChooseFolderName`, `StageExistingToTrash`, `ArchiveHasManifest`, `ArchiveReadable`, `DetectArchiveFormat`, `CandidateAssetTargets` would all silently break the harness).
- **Usings must be re-derived per file.** Archive.cs needs `System.IO`, `System.IO.Compression` (ZipFile), `System.Runtime.InteropServices` (DllImport), `System.Text.Json`, `Microsoft.Win32.SafeHandles` (SafeFileHandle), `SharpCompress.Archives`, `SharpCompress.Common`. Manifest/Overlay.*/Entry need `System.IO` + `System.Text.Json`. Install/ModService need only `System.IO` (+ `System.Text.Json` for ModService.cs's `MainVersionOf`). Dropping `System.Runtime.InteropServices` breaks `DllImport`; dropping `Microsoft.Win32.SafeHandles` breaks `SafeFileHandle`.
- **Apply cuts bottom-up** (NexusUpdateTruth first, ModService.cs last) or do the whole split in one scripted pass; every cut above shifts every line number below.
- **`sTmpSwept` is a process-global one-shot gate** — if it accidentally becomes instance (or is duplicated into two files as two fields), the `.tmp` sweep runs on every rescan again, which the comment at 264-267 says cost 358ms per rescan.
- **Don't reorder members while moving.** `One()`(1693) and the orphaned doc block at 1685-1692 are adjacent; a "tidy-up" during the move silently re-attaches a doc comment to the wrong method. Fix docs as a separate, reviewable commit (see Simplifications).
- **Behavioural landmines the split must not touch:** the strict-vs-`ManifestJson` parse asymmetry at 838/2755/3216 (a latent bug — fixing it changes output), and the `PurgeOverlaysOf` call at 571 which makes core→overlay the only backwards dependency.
- **Test surface:** 475 xUnit tests compile against the whole project, so any missed `using`, missing `partial`, or duplicated `static ModService()` fails at compile time, not at runtime. That is the cheapest possible verification: build + run the harness and the split is proven.
