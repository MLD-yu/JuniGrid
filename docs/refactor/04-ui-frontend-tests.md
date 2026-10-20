# 04 — UI / frontend / tests audit

Scope: `JuniGrid/MainWindow.xaml.cs` (1123), `JuniGrid/App.xaml.cs` (729), `JuniGrid/Components/**/*.razor` (19 files), `JuniGrid/wwwroot` (13 js + 13 css), `test/JuniGridTestHarness/*.cs` (37 files / 6481 lines), `JuniGrid/Services` (44 files). Baseline 0 errors / 475 tests is the acceptance net; nothing here requires a test change.

Fact corrections up front: `Components/` holds **19** `.razor` files, not 20. `wwwroot/js` holds **13** `.js` (12 `junigrid.*` + 1 vendor `gsap.min.js`), not 78. `wwwroot/css` holds **13** `.css` (3608 / 4723 lines total).

## Split plan

Both targets are already `partial` classes whose XAML half lives in `obj/.../MainWindow.g.cs` / `App.g.cs`, so adding sibling `partial` files needs **no csproj change** (`JuniGrid.csproj` has no explicit `Compile` items; SDK default globbing picks them up). The repo already uses exactly this shape — `Services/ModService.cs` + `ModService.AssetPack.cs`, `DepotDownloaderService.*.cs` x7. Follow that precedent: new files declare `public partial class X` **without** repeating the base class.

### `JuniGrid/MainWindow.xaml.cs` -> 3 files

| new file | line ranges from ORIG | members moved | rationale |
|---|---|---|---|
| `MainWindow.xaml.cs` (keep) | 1-487, 1123 | `LogPath` (13, static readonly), `Log` (15-23, internal static, 41 call sites across all three files), `EnsureStaticWebAssetContentRoots` (32-81, static) + its local fn `Touch` (37-49), `MapAssetThumbHost` (85-98, static), `OnThumbWebResourceRequested` (100-130, static) + local static fn `NotFound` (107-108), `MainWindow()` ctor (132-487) | Boot responsibility: static-asset roots, thumb virtual host, DI registration, WebView2 init / `ProcessFailed` / `ui-ready` wiring, closing fade. 488 lines. |
| `MainWindow.Window.cs` (new) | 489-750 **and** 918-1095 | consts `DWMWA_WINDOW_CORNER_PREFERENCE`/`DWMWCP_ROUND` (489-490), `DwmSetWindowAttribute` (492-494, static extern), `SW_MINIMIZE` (501), `ShowWindow` (503-504, static extern), `OnSourceInitialized` (506-560, protected override, instance), `_startupLeft`/`_startupTop`/`_startupCentered` (566-567, instance), `RevealAtStartupPosition` (570-582, public instance), `WM_GETMINMAXINFO`/`MONITOR_DEFAULTTONEAREST` (585-586), `WndProcClampMaximized` (587-630, instance), `MonitorMinTrackSize` (636-638, static), `GetDpiForWindow` (642-643, static extern), nested structs `POINT32`/`RECT32`/`MONITORINFO` (645-656), `ABM_GETAUTOHIDEBAR`/`ABE_BOTTOM` (659-660), `APPBARDATA` (662-671), `SHAppBarMessage`/`GetWindowRect` (673-677, static extern), `AutoHideBottomBarHeight` (681-698, static), `MINMAXINFO` (699-703), `MonitorFromWindow`/`GetMonitorInfo` (705-708, static extern), `_closing` (713, instance), `_wv2` (721, instance), `EnterLowMemoryMode` (723-738), `ExitLowMemoryMode` (740-750) **//** `_wasMaximized` (923, instance), `OnStateChanged` (925-997, protected override), `_restoreTargetPhysW`/`_restoreTargetPhysH` (1000, instance), `VerifyMaximizedPlacement` (1011-1018), `CheckMaximizedGeometry` (1020-1043), `SWP_NOZORDER`/`SWP_NOACTIVATE` (1045-1046), `SetWindowPos` (1048-1050, static extern), `OnWindowSizeChanged` (1055-1085), `MinimizeWindow` (1088-1095, public static) | 440 lines. **These two ranges must land in the same file** - see the cannot-move note below. Keeps every nested P/Invoke struct inside one compilation unit and pairs it with its only consumers. |
| `MainWindow.Theme.cs` (new) | 752-916 **and** 1097-1122 | `_themeRevealing` (762, private static), `ThemeRevealMs` (765, public static field), `RevealThemeSwitchAsync` (768-903, public static async), `OpenNexusOverlay` (906-916, public static) **//** `ThemeColorLight` (1100-1101, private static readonly), `ThemeColorDark` (1102-1103), `ApplyShellTheme` (1106-1122, public static) | 191 lines. One responsibility: paint the WPF shell to match the Blazor theme (circular reveal + corner/frame colours). Under the 300-line target; merging it into `Window.cs` would recreate the grab-bag it is being pulled out of. |

Line accounting: 488 + 440 + 191 + 4 blank separators (488, 751, 917, 1096) = 1123 OK

### `JuniGrid/App.xaml.cs` -> 3 files

| new file | line ranges from ORIG | members moved | rationale |
|---|---|---|---|
| `App.xaml.cs` (keep) | 1-123, 311-371, 373-550, 691-729 | `LogPath` (15, static readonly), `MutexName`/`PipeName`/`ActivateCommand` (21-24, const), `_mutex` (25, static), `Services` (28), `PendingNxmLink` (31), `_uninstallMode` (33), `OnStartup` (35-107, protected override), `ReadOneClickInstallFlag` (111-122, static) **//** `TryTakeOverSingleInstance` (315-342, static), `TryInstallBundledWebView2` (348-371, static) **//** `OnAppStartup` (374-529, instance, the `Startup=` handler from `App.xaml:4`) incl. local fns `RevealMain` (439-455) / `TryReveal` (457-473), `UiReadyCallback` (532), `NotifyUiReady` (534-543), `LogInfo` (547-550) **//** `OnDispatcherUnhandledException` (691-706), `OnUnhandledException` (708-711), `OnUnobservedTaskException` (713-717), `Log` (719-728) | 401 lines. Lifecycle: single-instance gate, WebView2 runtime bootstrap, splash->main choreography, crash handlers. |
| `App.NxmHandler.cs` (new) | 124-309 | `ClaimNxmHandlerNow` (125, public static), `NxmBackupKey`/`NxmBackupValue`/`NxmRootKey`/`NxmCmdKey` (129-132, const), `ExeOf` (135-140, static), `IsOurHandlerExe` (147-149, static), `ShouldRecordNxmBackup` (151-157, internal static), `ReleaseNxmHandler` (163-217, public static), `WriteSelf` (219, static), `EnsureUrlProtocolMarker` (224-236, static), `WriteNxmCommand` (240-249, static), `EnsureNxmHandlerPointsAtSelf` (256-309, static) | 186 lines. Sole responsibility: HKCU `Software\Classes\nxm` takeover / hand-back. Zero coupling to the rest except `ReadOneClickInstallFlag()` at 94 and `AppLog`. |
| `App.Activation.cs` (new) | 552-689 | `StartPipeServer` (552-578), `DispatchNxm` (580-587, internal static), `TryActivateExistingInstance` (591-602, static), `ActivateExistingWindow` (606-623, static), `SetForegroundWindow` (625-626, static extern), `BringMainWindowToFront` (632-659, public static), `SW_RESTORE` (661), `ShowWindow`/`BringWindowToTop`/`GetForegroundWindow`/`GetWindowThreadProcessId`/`GetCurrentThreadId`/`AttachThreadInput` (663-674, static extern), `GetSystemAccentHex` (678-689, public static) | 138 lines. Second-instance IPC + bring-my-window-forward + system accent read. All Win32/shell-facing, none of it touched by the startup sequence except `StartPipeServer()` at line 91. |

Line accounting: 401 + 186 + 138 + 4 blanks (310, 372, 551, 690) = 729 OK

### Members that CANNOT move without changing semantics

- **`POINT32` / `RECT32` / `MONITORINFO` / `MINMAXINFO` / `APPBARDATA` are load-bearing across BOTH moved MainWindow ranges.** `MONITORINFO` is used by `OnSourceInitialized` (527), `WndProcClampMaximized` (592), `OnStateChanged` (952), `CheckMaximizedGeometry` (1027) and `OnWindowSizeChanged` (1074); `RECT32` is used by `MonitorMinTrackSize` (636) and by `AutoHideBottomBarHeight` (681). **That is why 489-750 and 918-1095 must go to the same file.** Splitting them into `Window.cs` / `Sizing.cs` would be legal C# (partial shares private types) but would leave every P/Invoke declaration in one file and every consumer in another - do not do it.
- `_wv2` (MainWindow:721) and `_closing` (713) are read/written from all three files (721 <- 373/729/744/781/1117; 713 <- 451/452). They may live in `Window.cs`; `partial` makes them visible everywhere. No move required, no behaviour change.
- **Static field init order: NOT a hazard here, verified.** MainWindow's statics are `LogPath` (13, `Path.Combine(StoragePaths.AppDataDir, ...)`), `_themeRevealing` (762), `ThemeRevealMs` (765), `ThemeColorLight`/`Dark` (1100/1102, `Color.FromArgb`). No initializer references another MainWindow static, so cross-file textual ordering cannot matter. Same for App: `NxmCmdKey = NxmRootKey + ...` (132) is a `const` expression folded at compile time regardless of file order - and both move together anyway.
- `Log` (MainWindow:15) and `LogInfo` (App:547) have 41 and 11 call sites spread over all three future files. They stay in the kept file; do not duplicate them into the new parts.
- Nothing else is immovable. No nested private class is shared across a proposed boundary.

### Split risks (read before executing)

- `MainWindow.xaml` declares `x:Name="blazorWebView"` (line 42) and `x:Name="ThemeRevealOverlay"` (line 55). The generated fields land in `obj/.../MainWindow.g.cs` as `internal` members of the same partial type, so `ThemeRevealOverlay` is reachable from `MainWindow.Theme.cs` - but only if that file sits in `namespace JuniGrid;`. Get the namespace wrong and you get CS0103, not a subtle bug.
- Do **not** re-declare `: Window` / `: Application` on the new parts. `ModService.AssetPack.cs:16` shows the repo convention (`public sealed partial class ModService`, no base). Repeating a different base is CS0263; repeating the same one compiles but invites drift.
- Keep `RevealAtStartupPosition` `public` and `OnSourceInitialized`/`OnStateChanged` `protected override` exactly as-is - `App.xaml.cs:453` calls the former, WPF calls the latter two by vtable.
- Acceptance check after the split: `dotnet build` 0 errors, then confirm `git diff --stat` shows only adds plus one shortened file, and that the 475 tests still pass (none of them reference `MainWindow` or `App` - `grep -rn "MainWindow\|JuniGrid\.App" test/JuniGridTestHarness/*.cs` returns nothing, so the split is test-invisible and a green run proves nothing about it; do the build check).

## Simplifications (safe, zero behaviour change)

- <tag: shrink> Redundant `System.Windows.*` qualification while `using System.Windows;` is already at line 3 -> drop the prefix [JuniGrid/MainWindow.xaml.cs:434 `System.Windows.WindowState.Minimized`; :473 and :482 `System.Windows.MessageBox.Show`; :555 and :928 and :934 and :1024 `System.Windows.WindowState`; :908 `System.Windows.Application.Current`]
- <tag: shrink> Same for `System.Windows.Threading` (using at App:6) and `System.Diagnostics` (using at App:1) -> `System.Windows.Threading.DispatcherPriority.Background` becomes `DispatcherPriority.Background` [App.xaml.cs:528]; `System.Windows.Visibility.Visible` becomes `Visibility.Visible` [App.xaml.cs:464]; `System.Diagnostics.Process.Start` becomes `Process.Start` [App.xaml.cs:400, MainWindow.xaml.cs:912]
- <tag: shrink> Same for `System.Windows.Threading.DispatcherTimer` -> `DispatcherTimer` (add `using System.Windows.Threading;`; `System.Windows` is already imported) [MainWindow.xaml.cs:1014]
- <tag: delete> `EnsureUrlProtocolMarker()` returns `bool` but the only call site discards it -> change the return type to `void` and drop `return true/false` [JuniGrid/App.xaml.cs:224-236, discarded at :272]
- <tag: delete> Orphaned doc comment with no member under it (the member it described was removed; nothing in `MainWindow` reads an alpha=0 flag - `grep -rn "alpha\|隐形挂载" JuniGrid/MainWindow.xaml.cs` returns only this comment) [JuniGrid/App.xaml.cs:545]
- <tag: delete> Empty CSS rule `.jg-main { }` containing only a comment, no declarations - contributes nothing to the cascade; keep the comment, drop the empty block [JuniGrid/wwwroot/css/mods.css:328-331]
- <tag: delete> Empty media query `@media (min-width: 1800px) { }` [JuniGrid/wwwroot/css/home.css:105-106]
- <tag: yagni> Duplicated magic colour: the WebView2 fallback frame colour is hard-coded `0xFF, 0xF3, 0xF6, 0xFB` at the init site while the identical value is already named `ThemeColorLight` -> derive it from `ThemeColorLight` (`System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B)`, the pattern already used at :1118) [JuniGrid/MainWindow.xaml.cs:363-364 vs :1100-1101]
- <tag: shrink> `ThemeRevealMs` is `public static int` but has zero references outside `MainWindow` (only :842 and :864) -> `private const int ThemeRevealMs = 400`. Behaviour identical; public surface shrinks. [JuniGrid/MainWindow.xaml.cs:765]
- <tag: shrink> Three consecutive blank lines around the interop block [JuniGrid/MainWindow.xaml.cs:640-641]

## Simplifications (would change behaviour - listed, not proposed)

- Three hand-rolled file loggers coexist with the `AppLog` service: `MainWindow.Log` -> `startup.log` [MainWindow.xaml.cs:13-23], `App.LogInfo` + `App.Log` -> `crash.log` [App.xaml.cs:15, 547-550, 719-728], `AppLog` -> `juni-grid.log` with 1 MB rotation [Services/AppLog.cs:10-40]. Unifying them is the obvious dedupe but moves diagnostics to a different file, breaks every full-log-path message string (:483, :702) and loses the pre-`AppLog`-initialisation ordering guarantee that `startup.log` exists for.
- `OpenNexusOverlay(string url)` is named and commented as opening the in-window Nexus overlay but actually calls `Process.Start(..., UseShellExecute = true)` - the in-app overlay was removed (see the `MainWindow.xaml` comment 内置 Nexus 浏览器覆盖层已移除). Renaming to `OpenExternalUrl` would be honest, but it changes the call site at `Components/Pages/ModDetail.razor:643`. [MainWindow.xaml.cs:905-916]
- `MainWindow.Log` is `internal static` yet `grep -rn "MainWindow.Log" --include=*.cs --include=*.razor .` (excluding obj/bin) returns nothing -> could be `private`. Safe in isolation, but it is the natural seam for the logging unification above, so do not tighten it first.

## Deletion candidates (need user approval)

- `NexusService.CacheHits` [JuniGrid/Services/NexusService.cs:181] - zero-reference evidence: `grep -rn "CacheHits\|CacheMisses" --include=*.cs --include=*.razor JuniGrid/ test/ | grep -v /obj/ | grep -v /bin/` -> only `NexusService.cs:181` and `:182` (the two declarations). Note the underlying counters `_cacheHits`/`_cacheMisses` ARE live (:345, :354, :356) and feed `BumpTraffic` / `TakeTrafficDelta()` (:185-192) which `Components/Pages/Nexus.razor:1240` consumes - delete only the two properties, never the fields.
- `NexusService.CacheMisses` [JuniGrid/Services/NexusService.cs:182] - same grep, same result.
- `SaveVersionService.StashOverview()` [JuniGrid/Services/SaveVersionService.cs:273] - zero-reference evidence: `grep -rn "StashOverview" --include=*.cs --include=*.razor . | grep -v /obj/ | grep -v /bin/` -> `./JuniGrid/Services/SaveVersionService.cs:273` only. Its own doc comment claims it serves the Settings storage row that expands the pre-upgrade save backups - that row no longer calls it. 20 lines plus a `BackupRoot` scan; safe to remove, but it is the only enumeration API for the backup area, so confirm the Settings row was dropped on purpose.
- `ContentOverrideService.OrphanOverrideGroup.Revertible` [JuniGrid/Services/ContentOverrideService.cs:1879] - zero-reference evidence: `grep -rn "Revertible" --include=*.cs --include=*.razor . | grep -v /obj/ | grep -v /bin/` -> `./JuniGrid/Services/ContentOverrideService.cs:1879` only. `Packs.razor` consumes the sibling `SafeToRevert` count directly instead. One-line expression-bodied property.
- Not a deletion, but adjacent: `ConfigService.PortraitSkins` / `PortraitSkinVariants` / `PortraitVanillaDefaults` / `PortraitLocks` / `PortraitTrueVanilla` [JuniGrid/Services/ConfigService.cs:559-563] each have exactly one hit in the repo (their own declaration), i.e. they look dead. **They are not** - see Do-Not-Touch.

## Do NOT touch

- `ConfigService.PortraitSkins` ... `PortraitTrueVanilla` [JuniGrid/Services/ConfigService.cs:559-563] - the comment directly above them (lines 555-558) states they are deliberate read-only round-trip placeholders: the old `junigrid-blazor` app shares the same `%APPDATA%\JuniGrid\junigrid.config.json`, so deleting the properties makes the next save drop those keys and the old app loses the user's skin/lock choices. Zero references is the design, not a defect.
- Struct fields that look unused but are marshalling-critical: `MONITORINFO.dwFlags` [MainWindow.xaml.cs:655] and `APPBARDATA.uCallbackMessage` [MainWindow.xaml.cs:667] have one hit each, but they are `[StructLayout(LayoutKind.Sequential)]` fields - removing or reordering them shifts every subsequent field and breaks `GetMonitorInfo`/`SHAppBarMessage`. Same for `MemoryService` MEMORYSTATUSEX fields [Services/MemoryService.cs:270-277] and `XnaRedistService` WINTRUST_* fields [Services/XnaRedistService.cs:149-168].
- The [尺寸检查点] logging in `OnWindowSizeChanged` [MainWindow.xaml.cs:1052-1085] and `CheckMaximizedGeometry` [MainWindow.xaml.cs:1020-1043] - about 60 lines of pure logging that look deletable. The comments (:558, :991, :1052) show they are the deliberate instrument for a DPI/maximize-geometry regression that took several versions to pin down. Remove them and the next regression is blind.
- `AutoHideBottomBarHeight`'s `Math.Max(0, wa.Bottom - wa.Top - 1)` 1-pixel deduction [MainWindow.xaml.cs:611-613, mirrored at :1032] - looks like an off-by-one, is the documented fix for auto-hide taskbar edge-reveal suppression.
- `AttachThreadInput` foreground-lock bypass [App.xaml.cs:646-655] - this is the OS-blessed workaround, not a hack to tidy up. Deleting it silently degrades bring-window-to-front to nothing on Windows 10/11.
- The bare `catch { }` swallows in `EnsureStaticWebAssetContentRoots` [MainWindow.xaml.cs:48, :64] and `OnThumbWebResourceRequested` [MainWindow.xaml.cs:126-129] - both are on the boot path; a throw there means a white screen with no diagnostic. Likewise `EnsureNxmHandlerPointsAtSelf`'s `catch (Exception ex)` [App.xaml.cs:305-308]: registry access must never abort startup.
- The path-traversal guard in `OnThumbWebResourceRequested` [MainWindow.xaml.cs:109-114] - `name.Contains("..")` plus `IndexOfAny(Path.GetInvalidFileNameChars())` is a security check on a virtual-host handler that turns a URL into a file read. Never simplify it away.
- `ShouldRecordNxmBackup`'s three-way exclusion [App.xaml.cs:151-157] and `ReleaseNxmHandler`'s `isOurs`/`isOurGhost` discrimination [App.xaml.cs:197-205] - the comments at :142-146 and :190-192 record two real field failures (closing the switch left nobody receiving downloads). Both branches look redundant and are not.
- `test/JuniGridTestHarness/*` - read-only by instruction. Nothing below proposes an edit.
- `wwwroot/js/vendor/gsap.min.js` - vendored third-party, not app code.

## Frontend report (no split proposals)

Oversized files, `wc -l` over `JuniGrid/wwwroot` and `JuniGrid/Components`:

| rank | file | lines | note |
|---|---|---|---|
| 1 | `Components/Pages/Mods.razor` | **5282** | markup ends at 866, the `@code` block is **4415 lines** - by far the worst offender in the repo; also holds nested `private sealed class ModUpdateState` (:2589) and `ScrollRestoreReporter` (:3232) |
| 2 | `Components/Pages/Packs.razor` | **1830** | `@code` from :799 (1031 lines) |
| 3 | `Components/Pages/Nexus.razor` | **1491** | `@code` from :636 (855); nested `private enum LoginStep` (:640) |
| 4 | `Components/Pages/Settings.razor` | **1462** | `@code` from :562 (900) |
| 5 | `Components/Pages/ModDetail.razor` | **992** | `@code` from :335 (657) |
| 6 | `wwwroot/js/junigrid.ui.js` | **841** | 93 `function` declarations - the JS dump for every page |
| 7 | `wwwroot/css/packs.css` | **688** | |
| 8 | `wwwroot/css/misc.css` | **678** | self-described misc grab-bag; 30 top-level rules |
| 9 | `Components/GameVersionDialog.razor` | **662** | `@code` from :146 (516) |
| 10 | `wwwroot/css/mods.css` | **624** | |
| 11 | `Components/Layout/AccountPanel.razor` | **519** | `@code` from :105 (414) |
| 12 | `wwwroot/js/junigrid.widgets.js` | **510** | 68 `function` declarations |
| 13 | `wwwroot/css/effects.css` | **504** | |

Under 500 and fine: `Tasks.razor` 484, `Home.razor` 414, `base.css` 414, `junigrid.core.js` 412, `shell.css` 412, `nexus.css` 354, `Logs.razor` 341, `junigrid.nexus.js` 330, `junigrid.detail.js` 326, `detail.css` 278, `junigrid.scroll.js` 276, `junigrid.taskdock.js` 233, `TitleBar.razor` 213, `junigrid.packs.js` 213, `SelfUpdateButton.razor` 207.

Obvious duplicated logic, ranked:

1. **Verbatim method duplication between `Mods.razor` and `Packs.razor`.** `ToggleSearch()` [Mods.razor:1542-1546 vs Packs.razor:1217-1221] is character-identical, including the `junigridJs.searchToggle` selector literal `".jg-search-x"`. `ClearSearchAndClose()` [Mods.razor:1548-1555 vs Packs.razor:1223-1229] differs only by one comment line. Both are page-scoped `private` helpers with no cross-page reference - real copy-paste, not a shared base.
2. **Conflicting duplicate CSS blocks (cascade-order dependent, not merely redundant).** `.jg-modal-overlay` is declared at `effects.css:213` (`z-index: 99998; backdrop-filter: blur(4px); background: rgba(0,0,0,0.35)`) and again at `misc.css:154` (`background: rgba(0,0,0,0.4)`, no `z-index`, no `backdrop-filter`). Per the `index.html` load order (base, shell, home, mods, portraits, effects, detail, nexus, misc, swipe-row, packs, theme, violet) misc wins on `background` while `z-index`/`backdrop-filter` survive from effects - a split rule nobody can read in one place. Same pattern for `.jg-modal` [effects.css:217 vs misc.css:161]: `width: min(420px, 86vw)` is dead, overridden to `min(420px, 92vw)`; `padding: 22px 24px` is dead, overridden to `18px 20px`.
3. **Dead duplicate declaration.** `.jg-shimmer-line` is set to `height: 14px` at `effects.css:311` but `detail.css:52` (later in load order) sets `height: 12px` plus `border` and `box-shadow` - the 14px never applies.
4. **Split single rule.** `.jg-content`'s layout lives at `base.css:285` while its `position: relative` lives at `misc.css:563` purely because the back-to-top button needed it. `.jg-main` likewise: full rule at `base.css:286`, an empty leftover rule at `mods.css:328`.
5. **Duplicated trivial handlers.** `AskClear() => _confirmClear = true;` appears identically in `Logs.razor:205` and `Tasks.razor:411`; the `DoClear` bodies diverge (Logs clears lines, Tasks calls `Center.ClearMatching`) so only `AskClear` is a true duplicate - and it is one line, below the worth-fixing threshold.
6. **Not duplication (avoid the false positive).** `.jg-account` / `.jg-account-name` / `.jg-launch-big` reappear in `home.css` but every hit is inside `@media (max-width: 980px)` (:92-97) or `@media (min-width: 1400px)` (:101-104) - legitimate responsive overrides, leave them. The 12 `function` names that repeat across JS files (`close` x3, `endDrag` x3, `frame` x3) are all inside separate IIFE scopes - a 5-line-window block hash across all 12 `junigrid.*.js` files found **0** duplicated blocks.

## Test harness (read-only scan - no edits proposed)

- **No entirely redundant test file.** Every file with 0 `[Fact]`/`[Theory]` is a shared fixture, not a dead file: `GlobalUsings.cs` (4 lines, `global using Xunit; global using System.IO;`), `TempDirs.cs` (50, temp-dir registry), `TestSandbox.cs` (35, redirects `JUNIGRID_CONFIG_DIR`). Together they are referenced 45 times from the other test files.
- **No duplicate test.** 323 `[Fact]`/`[Theory]` attributes (291 `[Fact]`, 32 `[Theory]`) expand to the 475 baseline cases. Hashing the normalised body of all 287 statically-attributable test methods across all 37 files yields **0** identical pairs. Duplicate method names are limited to `Dispose` x15 (each an `IDisposable` cleanup on a different class) and `Fixture_IsPresent` x3 (three distinct fixture classes) - not redundancy.
- Smallest real test files, all justified: `NexusKindLabelTests.cs` (32 lines, 1 `[Theory]` / 4 cases) pins `Nexus.KindLabelFor`'s null-fallback against a documented `ArgumentNullException` that fired on every render; `TempDirsTests.cs` (47, 2 facts) pins the never-sweep-by-prefix guarantee that once deleted 3244 foreign directories; `FolderRenameMigrationTests.cs` (39, 1 fact + 1 theory) and `UninstallOwnershipTests.cs` (37, 1 fact + 3 theory cases) are single-concern but each guards a data-loss path.
- The largest test file, `WorkerCancellationAndOverlayTests.cs` (948 lines), is 15% of the harness - a legitimate concentration, not redundancy.

## Services: is any file entirely unused?

**No.** Method: for every one of the 44 `JuniGrid/Services/*.cs` files, extract each top-level type name and count occurrences across all `.cs`/`.razor`/`.xaml` under `JuniGrid/` and `test/`, excluding `obj/`, `bin/` and the declaring file itself:

```bash
for f in JuniGrid/Services/*.cs; do
  types=$(grep -oP '^(?:public|internal)\s+(?:static\s+|sealed\s+|partial\s+|abstract\s+|readonly\s+)*(?:class|record|struct|interface|enum)\s+\K[A-Za-z0-9_]+' "$f" | sort -u)
  hit=0
  for t in $types; do
    c=$(grep -rn "\b$t\b" --include=*.cs --include=*.razor --include=*.xaml JuniGrid/ test/ | grep -v "/obj/" | grep -v "/bin/" | grep -v "^$f:" | wc -l)
    if [ "$c" -gt 0 ]; then hit=1; fi
  done
  if [ "$hit" -eq 0 ]; then echo "UNREFERENCED-FILE $f types=[$types]"; fi
done
```

Output: `--- done ---` with zero `UNREFERENCED-FILE` lines. Thinnest but still live: `LzxDecoder.cs` (1 external hit - `XnbDecoder.cs:151` `new LzxDecoder(16)`), `TranslationService.cs` (3), `DxtDecoder.cs` (3), `PlayTimeService.cs` (5). Note `TranslationService`'s real callers are JS-interop (`wwwroot/js/junigrid.trans.js:101,162` invoke `TransBatch`/`ClearTransCache` by name), so a C#-only grep undercounts it - never judge a JS-invoked service by C# references alone.

## Risk notes

- **`MainWindow` / `App` have zero test coverage** - `grep -rn "MainWindow\|JuniGrid\.App" test/JuniGridTestHarness/*.cs` returns nothing. The 475-test net stays green even if the split breaks the app at runtime. The only real gate is `dotnet build` 0 errors plus a manual launch (splash -> main, theme toggle, minimize/restore, nxm:// second-instance activation).
- XAML code-behind partials are namespace- and name-sensitive: a new part in the wrong namespace produces CS0103 on `blazorWebView`/`ThemeRevealOverlay` rather than an obvious error. `InitializeComponent()` must stay called exactly once, from the ctor at MainWindow.xaml.cs:330.
- The `App.xaml` `Startup="OnAppStartup"` wiring is by name only; moving `OnAppStartup` to another partial is safe, but renaming it during the move would silently start the app with no splash and no main window.
- `RevealThemeSwitchAsync` mutates `_themeRevealing` as a cross-instance re-entrancy latch (:773/:778/:893) and resolves the live window via `Application.Current.MainWindow` on every call. Keep it `static` and keep the `finally` block intact - the `finally` re-fetches `MainWindow` (line 890) because the first reference may be stale; simplifying that to reuse `w` changes cleanup behaviour on a window swap.
- `OnSourceInitialized` computes and stashes `_startupLeft`/`_startupTop`/`_startupCentered` for a later call from `App.xaml.cs:453`. If `RevealAtStartupPosition` lands in a different file from those three fields that still works (partial), but any attempt to make them locals or recompute in place breaks the splash choreography.
- The `JUNIGRID_CONFIG_DIR` sandbox (`test/JuniGridTestHarness/TestSandbox.cs`) is a process-wide env var; any change reachable from the MainWindow split that touches `StoragePaths`/`ConfigService` initialisation (e.g. moving `LogPath` evaluation) could make a test write into the real `%APPDATA%\JuniGrid`. `LogPath` is `static readonly` and evaluated on first MainWindow touch - keep it in the kept file and keep it `static readonly`.
- The two `MessageBox.Show` calls inside the ctor's `catch` [MainWindow.xaml.cs:473-477 and :482-484]: if a partial-file mistake makes the ctor throw earlier, the user sees a modal dialog before the splash closes - hard to distinguish from a real boot failure during acceptance.
- Deleting `EnsureUrlProtocolMarker`'s return value is safe today, but `EnsureNxmHandlerPointsAtSelf` ignores it at :272 - if someone later wants to log a repaired marker, the bool has to come back. Cheap to keep; the change is cosmetic.
- Any CSS consolidation that reorders rules across `effects.css`/`detail.css`/`misc.css` will change rendering: the duplicates in items 2-4 currently resolve by load order, and three of them have different values on each side. Consolidate by deleting the losing declaration, never by moving the winning one.
