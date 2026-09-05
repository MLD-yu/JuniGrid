# JuniGrid 测试报告

> 2026-09-05 ｜ 测试方式：编译验证 + 静态风险扫描 + 源码白盒审查 + 真机 GUI 黑盒遍历（computer-use 自动化）+ 灰盒契约核对
> 本轮测试未修改任何代码；未执行任何破坏性操作（未清理缓存、未关闭游戏、未卸载 MOD，所有输入操作已取消还原）

## 一、已执行测试及结果

### 1. 构建与依赖（通过）
| 项 | 结果 |
|---|---|
| `dotnet build` | ✅ 成功，0 警告 0 错误（1.8s） |
| `dotnet list package --vulnerable --include-transitive` | ✅ 无易受攻击的包 |
| 静态风险扫描 | async void：无；Thread.Sleep：仅退出 Flush/序列化重试内（合理）；空 catch：14 处（多为刻意的清理容错，可接受）；sync-over-async：Nexus.razor:987-990 的 `.Result` 位于 `Task.WhenAll` 之后（安全模式） |

### 2. GUI 黑盒真机遍历（7/7 页面通过，3 个观察项）
| 页面 | 结果 | 要点 |
|---|---|---|
| 首页 | ✅ | 统计卡片与真实环境强一致（63 MOD = 61 启用 + 2 禁用，与 SMAPI 实际加载 61 mods 吻合）；热力图/年份下拉/账号面板正常；启动按钮运行中禁用 ✅ |
| Mod 管理 | ✅ | 列表/筛选计数（63/61/2/0）/排序/搜索（英文关键词命中中文译名 mod）/多选批量栏/备注对话框全部正常 |
| 备注边界值 | ✅ | 输入 24 字符被精确截断为 20，计数器 20/20；取消不保存 |
| Nexus 浏览 | ✅ | Trending+Mods 双板块实时数据、排序 Tab、回到顶部正常 |
| Nexus 搜索 | ✅ | "NPC Map Locations" 返回 21 结果、分页正确、**已安装徽标正确联动本地库**、搜索历史置顶 |
| Nexus 详情 | ✅ | Requirements(3)/Translations/Changelogs 折叠面板、BBCode 渲染、空数据回退文案正常 |
| SMAPI 日志 | ✅⚠ | 132 行实时日志、错误行红色高亮、命令输入框在游戏非本程序启动时正确禁用；**但翻译污染见缺陷 9** |
| 设置 | ✅ | 缓存统计（下载临时 2.3GB 等）、存储/内存/账号/关于区块完整；内存阈值 96% 正确回显 |
| 任务悬浮窗 | ✅ | 「运行中…」状态正确；关闭游戏有二次确认（防误触设计验证通过） |

**测试环境噪声（非缺陷）**：启动命令 `cmd /c start ''` 的引号被 Git Bash 转换，产生系统弹窗「Windows 找不到文件 ''」——已确认弹窗属 cmd.exe（测试命令自身），与程序无关。

### 3. 灰盒契约核对（通过）
- C# → JS interop：**55 个调用 vs JS 定义 55/55 匹配**，无缺失函数、无未加载脚本（9 个 JS 全部被 index.html 引用）
- CSS：9 个引用全部有效（reboot.css 来自 FluentUI 库内容路径，非缺失）
- DI：20 个服务全部 AddSingleton，Blazor Hybrid 单 UI 线程下无生命周期错配；ConfigService 最早实例化后直接注册，无双实例风险
- 每处 interop 调用均有 try/catch 兜底

## 二、缺陷清单

### P1（严重）—— ✅ 已于 2026-09-05 修复并验证（见文末「修复记录」）

| # | 位置 | 问题 | 证据 |
|---|---|---|---|
| 1 | `Services/ModService.cs:281` InstallUpdate | **更新安装先删旧版、无回滚**：`Directory.Delete(dest, recursive: true)` 先删除现有 mod，再 `MoveDirectorySafe` 移入新版。若移动/复制中途失败（磁盘满、文件占用、断电），旧版已毁、新版残缺，且不像禁用/卸载路径那样先进回收站 → **用户 MOD 可能永久丢失**。建议：先把旧版移入 .junigrid_trash（复用 Uninstall 的 staging 逻辑），装完再清理 | 删除后紧跟的 MoveDirectorySafe 失败路径无还原逻辑（:282, :287-291 只删 temp） |
| 2 | `Services/ModService.cs:358` InstallNew | **UniqueID 判重直接永久删除**：发现同 UniqueID 的其他文件夹时执行 `Directory.Delete(dir2, recursive: true)`，不进 .junigrid_trash。与项目整体「删除皆入回收站可还原」的设计冲突；且 dir2 是 Mods 顶层文件夹，若一个多分包容器里某个子包恰好同 UniqueID，会整包误删 | 对比 SetDisabled:158-163 / Uninstall:189-205 均入回收站 |

### P2（一般）

| # | 位置 | 问题 |
|---|---|---|
| 3 | `Services/ResumableDownload.cs:38,84` | `EnsureSuccessStatusCode` 抛出的 404/403 等**永久性 HTTP 错误被当瞬时错误重试满 5 次**（递增延迟累计 15s+），大文件场景用户白等；应按状态码区分（4xx 立即失败，5xx/网络异常才重试） |
| 4 | `Services/ResumableDownload.cs:92,97` | 重试间隔 `Task.Delay` **未传 CancellationToken**：用户移除任务（Cts.Cancel）后需再等最多 attempt 秒才真正停止 |
| 5 | `Services/ConfigService.cs:41-44` Load | 配置 JSON 损坏时**静默重置为默认值且不备份损坏文件**：用户丢失游戏路径、Nexus 登录态、备注/历史等全部数据，且无损坏现场可供诊断。建议先把 ConfigPath 改名留档 |
| 6 | `Services/ConfigService.cs:214` | NexusApiKey **明文存储**于 %APPDATA%/JuniGrid/junigrid.config.json。本地应用风险有限，但建议用 DPAPI（ProtectedData）加密 |
| 7 | `Services/ConfigService.cs:153,188` | 后台 SaveLoopAsync 与退出 Flush **共用固定 tmp 路径** `junigrid.config.json.tmp`：ProcessExit Flush 与后台写盘并发时可能互相覆盖 tmp（_saveRunning 闸门不覆盖 Flush），极端时序下产生损坏替换 |
| 8 | `Services/TranslationService.cs:77-80` | 翻译失败时**把英文原文作为译文写入永久缓存**：网络恢复后这些文本永远不再重试；且 _cache 无上限，长会话内存持续增长 |
| 9 | `Components/Pages/Logs.razor` + `Services/LogLineClassifier.cs:42-52` | **页面自动翻译污染 SMAPI 日志**：INFO/WARN/DEBUG 被随机译成「信息/警告/调试」（同页混排两种），mod 名亦被译（Pen Pals→笔友）。LogLineClassifier 依赖 `Contains(" INFO ")` 等英文标签 → **翻译开启时级别筛选（err/upd/loaded）与着色全部失效**。建议：日志页禁用翻译或翻译时豁免日志容器/级别标签 |

### P3（轻微）

| # | 位置 | 问题 |
|---|---|---|
| 10 | `Services/CoverCacheService.cs:22`、`NexusService.cs:23,729`、`UpdateService.cs:25,387`、`SelfUpdateService.cs:35` | 每次操作 `new HttpClient()`，高频调用下有 socket 耗尽风险（当前调用频率低，影响小）；建议注入共享静态/HttpClientFactory |
| 11 | `Components/Pages/Nexus.razor:993` | LoadHomeAsync `catch { }` 静默吞掉网络错误，首页空态无任何提示，用户不知是加载失败还是无内容 |
| 12 | `Services/TaskCenterService.cs:49-55` | RequestSave 懒初始化 Timer 无锁，并发首调可能创建两个 Timer（泄漏一个，仅触发一次） |
| 13 | `Services/TaskCenterService.cs:57-67` | SaveNow 快照为浅拷贝，序列化期间下载线程继续改 `TaskItem.Log` → 偶发「集合已修改」异常被吞、该次落盘跳过（有下次重试兜底） |
| 14 | `Services/ModService.cs:108-109` | 版本比较用 `Version.TryParse`，不识别 `1.2.3-beta` 等预发布号（解析失败按 null 处理），判重时可能保留旧版本副本 |
| 15 | `Services/PlayTimeService.cs:47-61` | 每 tick 固定 +30s，游戏在 tick 边界启停有 ±30s 误差；休眠恢复后首个 tick 可能多计（设计取舍，可接受） |
| 16 | Nexus 页翻译质量 | 作者署名 "by X" 被机翻为「经过 X」（如「水贾卡鲁 经过 16」），详情页 Requirements 里 mod 专名被译（NPC Map Locations→NPC 地图位置），易误导用户。建议豁免署名行/专名列 |
| 17 | `Services/TranslationService.cs:19` | 翻译 HttpClient 超时 12s 无重试排队上限，弱网下大批文本分块串行等待，页面可能长时间残留英文（体验项） |

### 正向发现（设计良好，值得保持）
- 扫描容错链（物化目录列表→单项 try/catch→孤儿兜底→manifest 损坏兜底）多处真实事故修复沉淀，覆盖非常细
- 配置防抖+单写者+tmp 原子替换+退出 Flush 的持久化协调器结构清晰
- 断点续传的镜像切换、心跳合并、进度节流直接命中历史根因（注释里保留事故编号，可追溯性好）
- 关闭游戏二次确认、游戏运行中启动按钮禁用、外部启动时 SMAPI 命令输入禁用等防误触设计全部到位
- 搜索联动本地库显示「已安装」徽标，跨模块数据流正确

## 三、风险与建议的后续动作

## 修复记录（2026-09-05）

**缺陷 1、2 已修复**（仅改 `Services/ModService.cs`，共 3 处 + 2 个新辅助方法）：

- `InstallUpdate`：旧版不再直接删除，改为 `StageExistingToTrash`（同盘原子改名移入 `.junigrid_trash`，时间戳+序号命名）→ 装新版 → 失败时 `RestoreStaged` 原路移回；旧版文件被占用时给出友好提示并放弃更新（旧版未动）。更新成功后旧版保留在回收站，按现有清理策略过期。
- `InstallNew`：同名启用/禁用副本同样先入回收站再安装，失败回滚两份副本。
- UniqueID 判重（原 ：358）：`Directory.Delete` 永久删除 → 改为移入回收站（可还原）；被占用则保留原样并记日志。

**验证**（仓库外临时工程真实执行，15/15 通过）：
1. 更新成功：新版装入、旧版进回收站且内容完整、扫描显示新版本号 ✅
2. 更新时文件被占用：友好提示「正被占用…旧版未动」，旧版与回收站均无变化 ✅
3. 重装同名替换：旧版进回收站 ✅
4. UniqueID 判重：旧副本移入回收站而非永久删除 ✅
5. 回收站副本手动还原：内容完整无损 ✅

验证工程：`%TEMP%\jg-fixtest\`（编译 ModService.cs + AppLog.cs + StoragePaths.cs 独立运行，未动仓库）。

**未修复项**：P2 缺陷 3~7、10~17 保持原状，按上文建议排序处理。

## 修复记录 2（2026-09-05 第二批）

**缺陷 9（日志页翻译污染）已修复 —— 采用「前缀保护、只翻正文」方案**（改 `wwwroot/js/junigrid.translate.js`）：

- 新增 `LOG_PREFIX_RE`：行首方括号前缀（`[HH:MM:SS 级别 来源]`）视为技术标记，永不送翻译；
- 不做 DOM 拆分：送翻前剥前缀、写回时拼回，对 Blazor 重渲染幂等，还原逻辑不变；
- 剥完前缀只剩空/纯符号/无英文单词的行（如整行只有前缀）自动跳过；
- 按正文去重（不同来源同名正文只翻一次）。
- 真机验证：所有日志行的 `[02:32:17 INFO  SMAPI]`、`[02:32:23 ERROR game]`、`[02:32:22 DEBUG Pen Pals]` 前缀保持英文原样（此前被译成「信息 SMAPI」「笔友」）✅；级别着色（DEBUG 灰/ERROR 红）与筛选不受影响 ✅。
- 附带更正：原报告称「筛选失效」不准确——筛选与着色在 C# 侧基于原始行计算（Logs.razor:51,168），不受 DOM 翻译影响；实际问题是前缀被译导致的混排观感与 smapi.io 对照困难。
- 端到端正文翻译验证受网络限制：测试时系统代理关闭（Clash 127.0.0.1:7897 未启用），Google 翻译接口直连超时，正文暂无法译出（与本次改动无关，旧代码同样不可用）。JS 语法（node --check）与前缀拆分逻辑（node 单测：带级别行/纯列表行/[JuniGrid] 中文行）已验证通过。开启代理后进日志页即可看到「前缀英文 + 正文中文」效果。

**缺陷 8（翻译失败污染缓存）已修复**（改 `Services/TranslationService.cs`）：

- `TranslateBatch` 中整块失败（两个 host 都不通，返回空数组）时只回填原文、**不再写入 `_cache`**——网络恢复后这些文本可正常重试；
- 部分成功块仍按原逻辑缓存（缺失项回退原文照旧）。
- 真机复现过原缺陷：接口不可达时 ~130 条日志正文被旧代码写入缓存，网络恢复后不再重试；修复后不再发生。

## 修复记录 3（2026-09-05 第三批：P2/P3 第一批小改动）

| 缺陷 | 修复内容 | 验证 |
|---|---|---|
| 缺陷 5（配置损坏静默重置） | `ConfigService.Load` catch 中先把损坏文件复制为 `junigrid.config.json.corrupt-<时间戳>` 留档，记 Error 日志后再重置默认 | 代码审查（仅 5 行 catch 内容，仅在损坏路径触发） |
| 缺陷 3（4xx 盲目重试） | `ResumableDownload` 新增 `PermanentDownloadException`：4xx（除 408/429）立即失败不重试，错误信息带状态码 | mock 服务器行为测试 9/9：404 → 49ms/1 次请求失败（原为 15s+/5 次）；500 → 仍正常重试 3 次（回归 ✅） |
| 缺陷 4（取消后延迟才停） | 重试间隔两处 `Task.Delay` 传入 `CancellationToken` | 同上测试：重试延迟中取消 → 310ms 内抛 OCE 停止，无新请求（原为最长再等 attempt 秒） |
| 缺陷 12（Timer 懒初始化竞态） | `TaskCenterService._saveTimer` 改为构造函数创建（单例生命周期），`RequestSave` 只做 `Change(800)` | 代码审查 + 编译通过 |
| 缺陷 13（落盘序列化竞态） | `SaveNow` 改为锁内序列化+写盘（tasks.json 小、800ms 防抖一次，代价可忽略） | 代码审查 + 编译通过 |

验证工程：`%TEMP%\jg-dltest\`（本地 HttpListener mock，覆盖 404 快速失败/500 重试/延迟中取消/重试后成功 4 场景 9 断言）。

**剩余未修复**：缺陷 6（DPAPI 加密 NexusApiKey，第二批）、缺陷 7（Flush/SaveLoop 共享 tmp 路径）、缺陷 10（HttpClient 实例化收敛）、缺陷 11（首页加载失败无提示）及 P3 其余项。

## 用户反馈问题处理记录（2026-09-05）

**1.「翻译功能失效」—— 非程序缺陷，为网络环境问题**：实测系统代理关闭（ProxyEnable=0）且 Clash 代理端口 127.0.0.1:7897 无监听，Google 翻译双接口直连全部超时（curl 000/12s）。翻译接口在此网络环境不可达，任何版本都无法翻译。**处理**：开启 Clash 并打开系统代理后重启 JuniGrid（.NET 进程会缓存代理状态，运行中开启代理不一定生效）。注意当前版本已修复「失败污染缓存」，恢复网络后重进页面即可重新翻译。

**2.「Steam 官方启动永远卡在启动中…」—— 已修复（体验缺陷）**：根因是用户 Steam 账号未拥有游戏（家庭共享），`steam://rungameid` 只是"通知" Steam 必然成功，Steam 弹出「借用游戏」确认窗后游戏进程永远不出现；`AccountPanel.WaitForRunningAsync` 静默干等 90 秒后无提示回退，期间再点击又是新的 90 秒等待，观感即"永远卡在启动中，退出 Steam 也不恢复"。修复（改 `AccountPanel.razor`）：
- Steam 模式等待 30 秒仍无游戏进程 → toast 提示「Steam 可能在等你处理弹窗（家庭共享借用/登录），若账号未拥有游戏请改用 SMAPI 启动」；
- 90 秒超时回退按钮时也 toast 说明原因；
- `WaitForRunningAsync` 补 `OperationCanceledException` 捕获；
- `Launch()` 全程 try/catch 兜底——此前 `_launching=true` 之后任何一步抛异常都会让按钮永久卡「启动中…」且无提示（防御性修复，当前代码路径未实际触发过）。

说明：Steam 账号是否拥有游戏无法通过 steam:// 协议预查（该信息只在 Steam 侧），因此无法在启动前拦截，只能靠等待期间的提示引导。

## 修复记录 4（2026-09-05 第四批：窗口状态机，用户反馈两个新 bug）

**Bug A：点「还原」后窗口消失（飞到屏幕外 X=-32506）**
根因：`App.xaml.cs` 启动采用"屏外挂载"（主窗口先放 (-32000,-32000) 防 WebView2 黑框），但 `RevealMain()` 揭示时只切了 Maximized，**Normal 位置从未移回屏内**（预算的 targetLeft/targetTop 存了没用）。用户点「还原」时 Windows 按屏外的 Normal 位置还原 → 窗口整个在屏幕外，任务栏点回来可能又最大化，观感即"窗口消失"。
修复：① `RevealMain` 揭示前先把 Normal 位置设回屏内居中；② `OnStateChanged` 还原分支加位置越界兜底（越界即拉回工作区，同时救回存量屏外窗口）。真机验证：还原后窗口位置 [128,18] 屏内 ✅。

**Bug B：还原后内容被裁剪（用户报：应还原为 1974×1383 但裁剪）**
根因：1974×1383 与最小 1536×864 都是**物理像素**设计值，但 WPF 的 Width/Height/MinWidth 是**逻辑像素（DIP）**。用户屏幕 2560×1600 @150% 缩放，逻辑工作区仅 ~1707×1019 —— 还原成 1974 DIP = 物理 2961px，比屏幕还大 → 内容被裁剪；MinWidth=1536 DIP 也几乎占满逻辑屏。
修复：① 还原目标尺寸按工作区钳制（min(设计值, 工作区-边距)）并保持 1974:1383 比例、不小于 MinWidth/MinHeight；② 构造函数把 MinWidth/MinHeight 也按工作区适配（小屏/高 DPI 下取设计值与工作区的较小者，下限 760×420）。真机验证：还原后 2304×1492 物理（=1536×995 DIP，宽度贴 MinWidth、高度贴工作区），完整可见无裁剪 ✅。

改动文件：`App.xaml.cs`（RevealMain）、`MainWindow.xaml.cs`（OnStateChanged 钳制 + 构造 MinWidth 适配）。编译 0 错误。

**翻译系统国内可用性（同日）——多翻译源架构**：实测确认国内免 Key 免费接口全部不可用（Google 被墙、Bing 网页反爬返验证码、有道老接口已停、Edge auth 已变更），故改为**多源架构**：
- 默认仍走 Google（海外/挂代理用户不变）；
- 新增**百度翻译源**：用户在 `设置 → 页面翻译（国内源）` 填入百度翻译开放平台（免费注册）的 AppID/Secret 后自动切换，国内直连可用；多条文本 `\n` 合并单请求、按行原序返回；凭据留空/失败自动回落 Google；
- 失败等待从 12s×2 串行降为 6s；凭据修改即时生效（无需重启）；
- UI：设置页新增「页面翻译（国内源）」卡片（AppID/Secret 输入 + 保存 + 开放平台链接），已真机验证渲染与保存正常。
  （**注**：此多源架构实现后即被下一条"整体移除"取代——见下。）

**翻译功能整体移除（用户最终决定，同日）**：国内可用性依赖 VPN 或用户自备 Key，维护成本高于价值，应用户要求**完整移除**页面翻译功能。删除清单（全局 grep 复核 = **0 残留引用**）：
- 删除文件：`Services/TranslationService.cs`、`wwwroot/js/junigrid.translate.js`、`wwwroot/assets/nav/translate.svg`（含 bin 输出副本与运行时缓存 translate-cache.json）
- `TopNav.razor`：翻译开关按钮、`_translateOn`、`ToggleTranslate`、`OnAfterRender` 的 translateInit/SetEnabled、`TranslationService` 注入
- `Settings.razor`：「页面翻译（国内源）」卡片、`SaveBaiduTranslate`、凭据字段与 OnInitializedAsync 加载
- `ConfigService.cs`：`TranslatePage`/`BaiduTranslateAppId`/`BaiduTranslateSecret` 属性
- `MainWindow.xaml.cs`：`TranslationService` 的 DI 注册；`wwwroot/index.html`：脚本引用；`shell.css`：`.jg-translate-btn` 样式块
- 有意保留（同名但无关）：`NexusService` 的"译本搜索"（搜 mod 汉化版的独立功能）、CSS 的 `translateY/translateZ`（图形变换属性）、`SplashWindow` 的 `TranslateTransform`（WPF 动画）
- 兼容性：老用户配置文件里残留的 `translatePage` 等字段被 JSON 反序列化自然忽略，无需迁移
真机验证：编译 0 错误 0 警告；顶栏翻译按钮消失、5 个导航图标布局正常；首页渲染无异常 ✅

**启动等待机制最终版（用户二次反馈后重写）**：用户实测"无 Steam 的账号点官方启动，按钮永久卡「启动中…」，关 Steam 也不恢复"。重写 `WaitForRunningAsync`（`AccountPanel.razor`）：
- **任何退出路径必然复位** `try/finally` 保证 `_launching=false`（此前 OCE 路径不复位，是"永久卡住"的隐患）；
- **总超时 90s → 45s**，25s 时给 Steam 弹窗提示 toast；
- **新增 Steam 存活检测**（`LauncherService.IsSteamRunning` 静态属性）：Steam 出现过又消失（连续 4s）→ 立即 toast「Steam 已退出，已取消本次启动等待」并复位；12s 内从未出现 → toast「Steam 一直没有运行…」并复位。用户"关闭 Steam 也不恢复"的场景现在 4 秒内自动恢复。

**中途取消按钮（用户新增需求，同日）**：「启动中…」状态下左下角显示与「运行中」同款的红色 ✕（CSS `.jg-launch-row.launching` 与 `.running` 并列显示），点击 = **立即终断等待 + `Launcher.KillGame()` 关闭游戏进程**（无论游戏是否已被拉起，SMAPI/官方两种模式都覆盖）+ toast「已取消本次启动」。实现：`_launchCancelled` volatile 标志（`Launch()` 开头重置、`WaitForRunningAsync` 循环首行检查、`CancelLaunch()` 置位）、`OnButtonClick` 分流（`_launching` 时走取消而非忽略）。**自动路径保留**：不点 ✕ 时仍是 25s 提示 / 45s 自动恢复。真机端到端验证：点启动 → 「启动中…」+ 红 ✕ 出现 → 点 ✕ → 按钮立即恢复「▶ 启动游戏」、KillGame 执行 ✅。

**取消后按钮又变「运行中」（用户反馈竞态，已修）**：用户实测点 ✕ 取消后过一会儿按钮又变回「运行中…」且游戏没被关掉。根因是**竞态**：Steam 的拉起管线（家庭共享校验/预载）可能在「取消」**之后**才把游戏进程拉出来——一次性 `KillGame()` 杀了个空，游戏随后照常启动，轮询检测到进程就把按钮翻回「运行中」。修复：
- `LauncherService` 新增 `KillGameWatchdogAsync(windowMs=20s, stopWhen)` 清场看门狗：持续监视期间凡是冒出的 SMAPI/游戏进程一律关闭，连续 2.5s 无进程或用户重新点启动（stopWhen）则提前结束；
- `CancelLaunch` 改用看门狗，并置 `_cancelWatchdog` 标志；轮询 `PollRunningAsync` 在看门狗运行期间**不把迟到的进程算作「运行中」**（马上会被清掉）——按钮保持「▶ 启动游戏」不再回弹。
- 「启动中」按钮视觉同步：收窄为与「运行中」同款尺寸（宽 100%→100%-50px 的 320ms 过渡动画，由长变短；取消/恢复时由短变长），红 ✕ 同步浮现——纯 CSS（`.jg-launch-row.launching`），复用既有 width transition。





**追加修复（同日）——两个窗口状态机回归**：

1. **启动不再最大化（小窗启动）**：RevealMain 里撤销 Left/Top 后复现——窗口挂在 (-32000,-32000) 屏外时 Windows 算不出最大化几何，`WindowState=Maximized` 被吞、窗口停在屏外小窗。终版修复：恢复「先移回屏内 targetLeft/Top 再 Maximized」，并加 300/800/1500ms 三次复查补投（非前台揭示时 Maximized 偶发被前台锁吞）。真机验证：启动即全屏最大化 ✅。
2. **拖拽最小尺寸不生效（可拖到 377 DIP 宽、内容裁剪）**：`WM_GETMINMAXINFO` 自定义 hook 设了 `handled=true` 抢在 WPF 之前，但只设了 max 未设 `ptMinTrackSize`，XAML 的 MinWidth/MinHeight 形同虚设。修复：hook 内按 `GetDpiForWindow` 把 1536×864 DIP 换算成设备像素强制写入 `ptMinTrackSize`（小屏兜底：不超过本屏工作区）。程序化验证：`SetWindowPos` 请求 400×300 → 系统精确钳回 **1536×864** ✅。

结论（用户三问）：① 启动默认全屏最大化 ✅；② 还原尺寸 = min(1974×1383 DIP, 工作区) 并保持设计比例——在 100% 缩放的屏幕上即精确的 1974×1383，用户当前屏幕（高缩放）下按比例钳到可容纳的最大值且完整可见；③ 拖拽最小被强制在 1536×864 DIP（按 DPI 换算），内容不再被裁剪 ✅。

**最终修正（用户指出尺寸单位错误后的重写）**：1974×1383 / 1536×864 指的是**物理像素（PX）**，不是 WPF 逻辑单位。此前"按工作区比例钳制"的实现方向完全错误——这两个尺寸在用户屏（2560×1600@150%，dpi=144）上都能精确放下。重写：
- `OnStateChanged` 还原分支：目标 = **精确 1974×1383 物理像素**（DIP = PX÷1.5 = 1316×922），仅物理屏放不下的小屏才钳到工作区；
- hook `ptMinTrackSize`：直接写 **1536×864 物理像素**（不再乘 DPI）；
- `MinWidth/MinHeight` WPF 属性清零（必须在 `InitializeComponent()` 之后，否则被 XAML 覆盖回 DIP 值造成二次放大——实测踩坑）；
- 按用户要求新增**尺寸检查点日志**（`startup.log`）：窗口到达两个规定尺寸时记录完整状态。

最终验证（真机，dpi=144/150%）：还原 → 物理 **精确 1974×1383PX** ✅；强行缩小 → 钳在 **精确 1536×864PX** ✅；两条检查点日志均落盘：
```
[尺寸检查点] ★ 到达还原标准尺寸 1974×1383PX（实际 1974×1383PX，Width=1316 Height=922 DIP，dpi=144，WindowState=Normal，Left=-133 Top=-182）
**16:9 调整（同日）**：应用户要求，还原尺寸高度由 1383 改为 **1974×1110PX（16:9 画幅，与最小尺寸 1536×864 同比例）**，宽度不变。改动：`OnStateChanged` 目标值、检查点日志、XAML 初始 Height。真机验证：还原后物理 **精确 1974×1110PX** ✅，检查点日志落盘：
```
[尺寸检查点] ★ 到达还原标准尺寸 1974×1110PX (16:9)（实际 1974×1110PX，Width=1316 Height=740 DIP，dpi=144，WindowState=Normal）
[还原] 目标 1974×1110PX (16:9) → 实际 Width=1316 Height=740 DIP = 1974×1110PX (dpi=144, scale=1.5)
[尺寸检查点] ★ 到达最小标准尺寸 1536×864PX（实际 1536×864PX，Width=1024 Height=576 DIP，dpi=144，WindowState=Normal，Left=400 Top=250）
```
最小尺寸 1536×864PX 不变。

**Windows 沙盒启动报错（用户反馈，已加固）**：沙盒里启动报 `WebView2RuntimeNotFoundException`——**环境问题非代码缺陷**：Windows 沙盒是一次性干净系统，不带 WebView2 运行时（正常 Win10/11 预装）。沙盒内解法：装一次 [Evergreen 运行时](https://go.microsoft.com/fwlink/p/?LinkId=2124703)（每次新沙盒需重装，安装器可放共享文件夹复用）。顺手加固：`App.OnAppStartup` 最前加 WebView2 运行时前置检测（`GetAvailableBrowserVersionString`），缺失时弹中文提示框（含下载链接与技术信息）后优雅退出——覆盖 LTSC/精简系统等所有无运行时用户，替代原来的裸英文堆栈。编译 0 错误 ✅。

**开源通用化（最终版）**：固定物理像素对多用户不成立（1080p 放不下、4K 显得小）。改为**按工作区比例锁定**：以参考屏 2560×1528 校准 1974×1110（宽 77.1%、高 72.6%），任何分辨率/缩放下窗口都占工作区相同比例——作者屏（2560@150%）上精确 1974×1110PX，1080p 小屏自动等比缩小不裁剪，4K 大屏等比放大保持观感（与主流软件 DIP 行为殊途同归）。最小尺寸 1536×864PX 保持物理像素不变（hook 强制，超小屏自动让位）。检查点日志：还原时必然记录（SizeChanged 版本保留给手动拖动场景）。最终真机验证：
```
[还原] 目标工作区 77.1%×72.6%（=本屏 1974×1110PX）→ 实际 Width=1316 Height=740 DIP = 1974×1110PX (dpi=144, scale=1.5)
[尺寸检查点] ★ 到达还原标准尺寸（本屏期望 1974×1110PX，实际 1974×1110PX，Width=1316 Height=740 DIP，dpi=144，WindowState=Normal，Left=195 Top=139）
[尺寸检查点] ★ 到达最小标准尺寸 1536×864PX（实际 1536×864PX，Width=1024 Height=576 DIP，dpi=144，WindowState=Normal，Left=400 Top=250）
```







1. **优先修复缺陷 1、2**（数据丢失类），并为 `InstallUpdate` 增加回归测试（模拟移动失败场景）
2. 修复缺陷 9（日志页禁用翻译或豁免级别标签），否则日志排错功能在翻译开启时形同虚设
3. 缺陷 5：Load 失败先备份再重置，一行改动即可避免最疼的数据丢失场景
4. 建立 .NET 单元测试工程（当前仓库无任何测试工程），优先覆盖 WB-01~WB-37（ModService/ResumableDownload 全部可纯逻辑测试）
5. 引入 UI 冒烟自动化（本轮 computer-use 脚本思路可固化）覆盖 BB-11~BB-26 核心路径
