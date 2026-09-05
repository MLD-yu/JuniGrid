# 灰盒测试用例（GB）

基于内部结构知识（DI 注册、JS interop 契约、数据流）设计，验证模块间集成与接口契约。标注 ✅ 为本次已静态验证/真机验证。

## 启动与 DI 集成

| ID | 集成链路 | 前置条件 | 步骤 | 预期结果 | 验证状态 |
|---|---|---|---|---|---|
| GB-01 | MainWindow → BuildServiceProvider → 全部服务 | 启动 | 检查服务注册 | 全部 AddSingleton（20 个服务），Blazor Hybrid 单 UI 线程下生命周期一致，无 scoped-in-singleton 陷阱 | ✅ 静态验证（MainWindow.xaml.cs:115-143） |
| GB-02 | configService 先于容器实例化 | 启动 | — | 最早加载的 ConfigService 实例直接注册，避免二次实例化导致双份防抖队列 | ✅（:120 注释与代码一致） |
| GB-03 | PlayTimeService → LauncherService 依赖 | 启动 | — | 构造注入，IsGameRunning 同时覆盖本程序/外部启动的游戏 | ✅ 设计确认，BB-11 数据佐证 |
| GB-04 | ConfigService → NexusService 静态开关同步 | 修改成人过滤设置 | 保存 | SyncAdultFilter 更新静态字段并递增 AdultFilterVersion，Nexus 页弃用旧快照重拉 | 建议 GUI 验证 |
| GB-05 | ConfigService → StoragePaths.CacheRoot | 更改缓存目录 | 保存后立即新建下载 | 静态入口即时生效（无需重启，除 WebView2） | 建议验证 |
| GB-06 | PendingWebView2MoveFrom 迁移链 | 更改缓存目录含 WebView2 | 重启 | 启动时（WebView2 初始化前）自动搬迁后清空标记 | 建议验证 |

## JS Interop 契约（C# ↔ JS）

| ID | 集成链路 | 步骤 | 预期结果 | 验证状态 |
|---|---|---|---|---|
| GB-07 | C# InvokeVoidAsync → junigridJs.* 定义 | 核对全部 55 个调用与 9 个 JS 模块定义 | 一一对应，无缺失/签名不匹配 | ✅ 55/55 匹配（core/ui/scroll/splash/taskdock/detail/nexus/translate/widgets） |
| GB-08 | index.html 脚本加载顺序 | 检查 9 个 script 标签 | 按依赖序加载（core 先于使用方） | ✅ |
| GB-09 | index.html CSS 引用 | 检查 9 个 link | 本地 css 8 个存在；reboot.css 来自 FluentUI 库内容路径（非缺失） | ✅ |
| GB-10 | JSInvokable 回调（TranslationService.TranslateBatch） | JS 翻译模块收集文本 → 调用 .NET | [JSInvokable] 特性存在，返回数组与顺序一一对应 | ✅ 静态验证 |
| GB-11 | JS → C# 异常兜底 | 翻译接口全部失败 | JS 端保留原文，页面不出现空白文本 | 建议 GUI 验证（断网） |
| GB-12 | 每处 interop 调用 try/catch | 全局 grep | 所有 InvokeVoidAsync 调用点均包裹 try/catch，JS 失败不炸页面 | ✅ 抽查符合（如 AccountPanel.razor:157、Nexus.razor:961-963） |

## 数据流集成（页面 → 服务 → 磁盘/网络）

| ID | 集成链路 | 步骤 | 预期结果 | 验证状态 |
|---|---|---|---|---|
| GB-13 | 设置页 → ConfigService.Save → junigrid.config.json | 修改游戏目录 | 250ms 防抖落盘，camelCase JSON 原子替换 | ✅ 代码审查+真机（设置已持久化） |
| GB-14 | Mods 页 → ModService.Scan → 列表渲染 | 进入 Mod 页 | 63 条与磁盘一致；判重/孤儿/禁用标记全链路正确 | ✅ 真机（BB-18/19） |
| GB-15 | Nexus 搜索 → 历史写入 Config → 搜索面板回显 | 搜索 "NPC Map Locations" | NexusSearchHistory 新词置顶、上限 10 条、落盘 | ✅ 真机（BB-46） |
| GB-16 | 本地安装检测 → Nexus 结果「已安装」徽标 | 搜索已装 mod | manifest UniqueID/NexusModId 与搜索结果 id 匹配 → 显示徽标 | ✅ 真机（BB-45） |
| GB-17 | 详情页安装 → ResumableDownload → InstallService → TaskCenterService → Mods 页 | 一键安装 | 任务实时进度（0.4s 节流）、心跳行合并、完成后 Mod 页可见 | 建议 GUI 验证 |
| GB-18 | 任务进度 → TaskDock 悬浮窗 → /tasks 页 | 下载中观察 | 三处 UI 状态一致（OnChanged 事件驱动） | 建议 GUI 验证 |
| GB-19 | 启动器 → SMAPI 进程 stdout → Logs 页 → LogLineClassifier | SMAPI 启动游戏 | 日志实时显示、级别着色正确 | ✅ 真机（132 行，错误行红色） |
| GB-20 | 游戏进程退出 → LauncherService → PlayTime/Config 统计 | 玩 5 分钟退出 | TotalPlayMinutes 与 playtime.json 增长一致 | 建议长时间验证 |
| GB-21 | 页面翻译 ↔ LogLineClassifier [缺陷9] | 开启翻译后打开日志页并按级别筛选 | **当前失败**：INFO/WARN/DEBUG 被译为「信息/警告/调试」，Classify 的 Contains(" INFO ") 不匹配 → 筛选/着色失效 | ⚠ 真机复现 |
| GB-22 | 页面翻译 ↔ 搜索历史/备注输入框 | 开启翻译后使用搜索 | 输入框 placeholder/value 不应被翻译替换导致提交错值 | 建议 GUI 验证 |
| GB-23 | CoverCacheService → Mod 封面缓存 → ModCovers 配置 | 浏览后断网进 Mod 页 | w240-<sha1>.img 缓存命中，封面离线可显示 | 建议验证 |
| GB-24 | SelfUpdateService → Changed 事件 → UI 提示 | 有新版本 | 下载完成后 Changed 触发 UI 提示；启动时 CheckAsync 异常静默（:47 try-catch{}）不影响主流程 | 代码审查 ✅ |
| GB-25 | SteamService → 账号面板显示 | Steam 登录状态变化 | 显示「维也纳丶 Steam 客户端级登录」；未装 Steam 时优雅降级 | ✅ 真机显示正常 |
| GB-26 | NexusSsoService → 浏览器拉起 → 回调接管 | 重新登录 Nexus | Process.Start 打开授权页（UseShellExecute），回调写入 ApiKey 并刷新用户信息 | 建议验证（涉及真实登录） |

## 竞态与生命周期

| ID | 集成链路 | 步骤 | 预期结果 | 验证状态 |
|---|---|---|---|---|
| GB-27 | 批量启禁 ↔ 后台扫描 | 批量操作时重新进入 Mod 页 | Scan 物化目录列表+单项容错，不炸整页 | ✅ 代码审查（v0.72.6 修复在位）+真机无异常 |
| GB-28 | 下载线程 Report ↔ UI 重渲染 ↔ SaveNow | 大文件下载中切页 | 心跳合并、节流防 UI 刷爆、落盘偶发失败有重试 | ✅ 代码审查 |
| GB-29 | 任务取消链（Remove → Cts → ResumableDownload/InstallService） | 移除运行中任务 | 取消传播到下载循环与解压/安装前置检查 | 代码审查 ✅，建议 GUI 验证 [缺陷4 关联] |
| GB-30 | ConfigService.Saved 事件 → TaskDock 显隐 | 修改 ShowTaskDock | 事件可能在后台线程触发，订阅方需调度回 UI | 代码审查 ✅（注释明确约定） |
| GB-31 | ProcessExit 三方兜底（Config.Flush / PlayTime.Save / TaskCenter 防抖） | 任务管理器杀进程 | 尽力落盘；强杀窗口内最多丢最后一个 tick/一次防抖 | ✅ 代码审查，边界已知 |
| GB-32 | WebView2 数据目录 ↔ 缓存迁移 | 迁移期间使用内置浏览器 | 迁移标记下次启动处理，运行期不搬占用目录 | 代码审查 ✅ |
