# JuniGrid 重构方案（待你确认）

基线（实测）：`dotnet build` 0 错误；475 个 xUnit 测试全绿；git HEAD = 10f426f，工作区干净。
测试命令：`./test/JuniGridTestHarness/bin/Debug/net10.0-windows10.0.17763.0/JuniGridTestHarness.exe`
（注意：本仓库在 .NET 10 SDK 下 `dotnet test` 已不可用，VSTest 目标被移除。）

## 一、拆分方案（纯文件级 partial，类型名/成员/可见性一字不改）

只加 `partial` 关键字，不新增类、不改签名、不改调用方、不重排成员。

| 原文件 | 行数 | 拆成 | 目标文件 |
|---|---|---|---|
| ModService.cs | 3909 | 10 | .Manifest / .Overlay.Probe / .Overlay.Resolve / .Overlay.Manage / .Install / .Archive / .Entry + 拆出 ModEntry.cs、NexusUpdateTruth.cs |
| ContentOverrideService.cs | 2395 | 待报告 02 | — |
| NexusService.cs | 1818 | 待报告 02 | — |
| InstallService.cs | 1706 | 3 | .DependencyRanking / .DependencyResolve |
| UpdateService.cs | 1178 | 3 | .SmapiInstall / .Files |
| MainWindow.xaml.cs | 1123 | 3 | .Window / .Theme |
| StorageService.cs | 838 | 2 | .Migrate |
| App.xaml.cs | 729 | 3 | .NxmHandler / .Activation |

## 二、三个真实隐患（拆错会静默出错，不出编译错误）

1. `InstallService._installGate` / `_installQueue` / `_busyCount`（:49-51）
   static SemaphoreSlim 被复制到第二个文件 → **能编译**，但两把锁 → 互斥失效，无测试报错。
2. `UpdateService.Http`（:21）/ `DownloadHttp`（:887）
   static readonly HttpClient，跨文件移动会让首次调用可能拿到 null；复制则变成两个连接池。
3. `StorageService._gate`（:40）
   实例锁被复制 → 两个锁域 → 该锁本来要防的字典损坏会重现。

对策：所有 static 字段锁死在其初始化器所在文件，每个文件成员名单去重比对。

## 三、简化方案（零行为变化）

| 类别 | 内容 | 出处 |
|---|---|---|
| 冗余限定名 | `System.Windows.WindowState` → `WindowState`（7 处） | MainWindow |
| 死代码 | 不可达 `return false;`（CS0162，2 处） | ModService:3053,3057 |
| 孤立注释 | 5 处指向已删除方法的 XML 文档块 | ModService |
| 转发壳 | `StorageService.CopyTree` 纯转发 → 删壳直调 | StorageService:752 |
| 空规则 | 空 CSS 规则 `.jg-main{}`、空 media query | mods.css:328, home.css:105 |
| 返回值 | `EnsureUrlProtocolMarker` bool 恒被丢弃 → void | App:224 |
| 成员收窄 | `ThemeRevealMs` public → private const | MainWindow:765 |

## 四、明确「不做」清单（看似能简化、实际会出事）

| 项 | 为什么不能动 |
|---|---|
| 合并 `WriteManifestAtomic` → `AtomicFile` | 重试从 4×50ms(300ms) 变 5×10ms(100ms)，杀毒锁文件时早放弃 3 倍 |
| 5 个 `Portrait*` 配置属性 | 与旧版 junigrid-blazor 共享 config.json，删了旧版丢配置 |
| 4 个路径净化函数合并 | 空名回退各不相同（""/"NewMod"/"unknown"/删字符），统一会改磁盘目录名 |
| `TotalPlayMinutes` | 只写不读，但属持久化配置，删了下次保存丢 key |
| `AppLog.FlushTimer` | 全仓库唯一零引用成员，但是活着的 Timer，靠副作用工作 |
| MainWindow 的 [尺寸检查点] 日志 | ~60 行，是 DPI/最大化几何回归的专用探针 |
| 三个手写日志器统一 | 会改诊断输出文件、破坏启动前日志顺序保证 |
| 合并 SHA-256 4 处副本 | 失败模式不同（抛异常 vs 返回 "" vs ""+日志），合并会改 CommandBridgeService 行为 |

## 五、风险分级与建议执行顺序

| 批次 | 内容 | 自动化验收 | 风险 |
|---|---|---|---|
| 1 | 删 0 项 + 简化（零行为变化） | 475 测试覆盖 | 极低 |
| 2 | 拆 InstallService / UpdateService / StorageService | 475 测试覆盖 | 低 |
| 3 | 拆 ModService（3909 行） | 475 测试覆盖 | 低 |
| 4 | 拆 ContentOverrideService / NexusService | 475 测试覆盖 | 低 |
| 5 | 拆 MainWindow / App | **无测试覆盖**，仅编译 + 手动开程序 | 中 |

**关键事实：MainWindow 和 App 零测试覆盖。** `grep MainWindow test/` 返回空。
那 475 个测试对这俩文件是瞎的 —— 拆它们只能靠编译通过 + 你手动验证（开程序、切主题、最小化还原、点 nxm:// 链接）。
建议放到最后单独做。

## 六、验收标准

1. `dotnet build JuniGrid.sln` → 0 错误
2. 测试 exe → **475 个通过，0 失败**（数量不允许减少，测试文件一行不改）
3. 每个被改文件一张改前/改后职责对照表
4. `git diff --stat` 只有新增文件 + 原文件变短
