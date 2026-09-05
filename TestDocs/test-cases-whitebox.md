# 白盒测试用例（WB）

依据源码语句/分支/边界/异常路径设计。标注 [缺陷] 的用例对应 TEST-REPORT.md 中的缺陷条目。

## ModService.cs

| ID | 测试方法 | 输入/前置条件 | 步骤 | 预期结果 | 覆盖类型 |
|---|---|---|---|---|---|
| WB-01 | `Scan(gamePath)` | gamePath = "" / null | 调用 Scan | 返回空数组，不抛异常 | 分支 |
| WB-02 | `Scan` | Mods 目录不存在 | 调用 Scan | 返回空数组 | 分支 |
| WB-03 | `Scan` | Mods 目录在枚举途中被删除 | 调用 Scan | 捕获 DirectoryNotFoundException，返回空数组（:30-31） | 异常路径 |
| WB-04 | `Scan` | `.junigrid_trash` 目录存在 | 调用 Scan | 回收站目录被跳过不扫描（:41） | 分支 |
| WB-05 | `Scan` | 顶层目录无 manifest.json 且无子 manifest | 调用 Scan | 生成 OrphanEntry，Name=文件夹名，HasManifest=false（:58） | 语句 |
| WB-06 | `Scan` | manifest.json 为 0 字节 | 调用 Scan | 兜底收进来并带「⚠ manifest.json 为空」提示，不隐身（:69-70） | 异常路径 |
| WB-07 | `Scan` | 一个文件夹含多个子 manifest（内容包分包） | 调用 Scan | 每个嵌套 manifest 各生成一条 ModEntry（:61-71） | 语句 |
| WB-08 | `Scan` | 扫描瞬间某目录被改名（禁用操作并发） | 批量启禁同时扫描 | 单项异常被跳过，扫描不中断（:87-89） | 竞态/异常 |
| WB-09 | `Scan` 判重 | 同 UniqueID 的 X（启用）与 .X（禁用）并存 | 调用 Scan | 只显示启用副本，禁用副本隐藏不删（:94-121） | 分支 |
| WB-10 | `Scan` 判重 | 同 UniqueID 两份均为启用，版本 1.0.0 vs 1.1.0 | 调用 Scan | 保留版本高的（:108-110） | 边界 |
| WB-11 | `Scan` 判重 | 版本号含预发布后缀 "1.2.3-beta" | 调用 Scan | Version.TryParse 失败 → 视为 null，版本比较退化为保留先出现的 [缺陷-缺陷16] | 边界 |
| WB-12 | `SetDisabled` | folderName="A/B"（多级子包），disabled=true | 调用 SetDisabled | 只改顶层 "A"→".A"，整体禁用（:137） | 分支 |
| WB-13 | `SetDisabled` | 启用时传入旧名但磁盘为 ".X" | 调用 | 容错找到 .X 并启用（:139-144） | 分支 |
| WB-14 | `SetDisabled` | 目标名已存在（重复副本） | 调用 | 旧副本移入 .junigrid_trash 带时间戳，再完成改名（:154-163） | 语句 |
| WB-15 | `SetDisabled` | 文件被游戏占用 | 调用 | 返回中文提示「正被占用…」，不抛异常（:172-178） | 异常路径 |
| WB-16 | `Uninstall(toTrash:true)` | 同名 mod 一秒内删除两次 | 连续两次卸载 | 第二个 staging 追加序号 `_2`，互不覆盖（:199-200） | 边界 |
| WB-17 | `Uninstall(toTrash:false)` | 移入回收站成功但 Delete 失败（文件占用） | 调用 | 记录警告日志，回收站空壳目录被清理（:219-227） | 异常路径 |
| WB-18 | `Uninstall` | Directory.Move 中途失败 | 调用 | staging 若已部分移走则回移还原（:213-214） | 异常路径 |
| WB-19 | `InstallUpdate` [缺陷1] | 正常 zip、dest 已存在；移动阶段磁盘满 | 调用 | **当前实现**：先 Directory.Delete(dest)（:281）再移动 → 失败时旧版已删、无回滚。用例验证失败场景下旧 mod 是否可恢复 | 异常路径（应失败） |
| WB-20 | `InstallUpdate` | expectedUniqueId 与包内 manifest 不符 | 调用 | 返回「下载的包不是这个 Mod…」，放弃安装（:259-269） | 分支 |
| WB-21 | `InstallNew` | zip 无 manifest.json | 调用 | 返回提示改用手动下载，temp 已清理（:306-314） | 分支 |
| WB-22 | `InstallNew` [缺陷2] | 新包 UniqueID 与现有另一文件夹相同 | 调用 | **当前实现**：另一文件夹被 Directory.Delete 永久删除（:358），不入回收站。用例验证数据可否找回 | 语句（应失败） |
| WB-23 | `InstallNew` | zip 内文件在根目录（modRoot==temp） | 调用 | 按 manifest Name 生成文件夹名并 Copy（:326-327, 368） | 分支 |
| WB-24 | `SanitizeFolderName` | Name 含非法字符 `\/:"*?<>|` 或全空白 | 调用 | 替换为 `_`；全空白回退 "NewMod"（:426-432） | 边界 |
| WB-25 | `MoveDirectorySafe` | src/dest 跨盘符（C:→E:） | 调用 | IOException → 复制+删源（:403-416） | 异常路径 |
| WB-26 | `BuildModEntry` UpdateKeys | "Nexus:23135@main"、"GitHub:owner/repo" | 调用 | nexusId=23135（剥 @ 后缀），githubRepo 正确（:498-518） | 边界 |
| WB-27 | `BuildModEntry` 依赖 | Dependency 带 IsRequired=false | 调用 | 可选依赖不进 Dependencies（:533-538） | 分支 |
| WB-28 | `BuildModEntry` | manifest 用 "UniqueId"（小写 d，SMAPI 内置写法） | 调用 | 忽略大小写读到 UniqueID（:579-580） | 边界 |
| WB-29 | `BuildModEntry` | manifest JSON 带尾随逗号/行内注释 | 调用 | JObject 宽松解析成功（:493） | 语句 |

## ResumableDownload.cs

| ID | 测试方法 | 输入/前置条件 | 步骤 | 预期结果 | 覆盖类型 |
|---|---|---|---|---|---|
| WB-30 | `RunAsync` 正常流 | 稳定 URL | 下载 12MB 文件 | 文件完整；进度回调节流 ≥1s 间隔（:70-79） | 语句 |
| WB-31 | `RunAsync` 续传 | 首次下载 30% 后断连 | 自动重试 | 带 `Range: bytes=written-` 请求，206 时 Append 续写（:35-36, 41, 53） | 分支 |
| WB-32 | `RunAsync` 不支持 Range | 服务器对 Range 返回 200 | 重试 | written 归零、FileMode.Create 重写，不产生损坏文件（:42-46） | 分支 |
| WB-33 | `RunAsync` 镜像切换 [正常] | 直连失败且 written==0 | 首次失败立即切镜像，不等重试耗尽（:87-92） | 语句 |
| WB-34 | `RunAsync` HTTP 4xx [缺陷3] | URL 返回 404 | 观察重试行为 | **当前实现**：HttpRequestException 进入重试，共 5 次×递增延迟 ≈ 15s+；预期应立即失败 | 异常路径 |
| WB-35 | `RunAsync` 取消 [缺陷4] | 下载中触发 ct.Cancel（用户移除任务） | 观察取消延迟 | **当前实现**：catch 中 Task.Delay 未传 ct，最多再等 attempt 秒；预期应立即退出 | 异常路径 |
| WB-36 | `RunAsync` 全候选失败 | 所有镜像均不可达 | 调用 | 候选耗尽后走普通重试直至 maxAttempts，最终抛异常给调用方 | 边界 |
| WB-37 | `FormatBytes` | 0 / 1023 / 1024 / 1048576 / 超大值 | 调用 | "0 B" / "1023.0 KB" 前 F1，B 为 F0；单位不越界 | 边界 |

## ConfigService.cs

| ID | 测试方法 | 输入/前置条件 | 步骤 | 预期结果 | 覆盖类型 |
|---|---|---|---|---|---|
| WB-38 | `Load` 正常 | 合法配置文件 | 启动 | Current 反序列化成功，AdultFilter/StoragePaths 同步 | 语句 |
| WB-39 | `Load` 损坏 [缺陷5] | 配置 JSON 被截断 | 启动 | **当前实现**：静默重置为默认值，损坏文件不备份 → 用户丢失游戏路径/Nexus 登录态；预期应先备份损坏文件 | 异常路径 |
| WB-40 | `Save` 防抖合并 | 250ms 内连续 Save 100 次（批量操作） | 观察 IO | 合并为一次真实写盘；_dirtyVersion 单调递增（:89-109） | 边界 |
| WB-41 | `SaveLoopAsync` 单写者 | 上一轮写盘未完成时再次触发 | 观察 | Interlocked.Exchange 闸门阻止并发写循环（:116） | 并发 |
| WB-42 | `WriteAtomicAsync` 原子性 | 写盘进程被杀（写一半） | 重启读取 | tmp+Move 替换，旧配置完好（:152-156） | 异常路径 |
| WB-43 | `WriteAtomicAsync` 重试 | 目标盘 I/O 抖动 IOException | 调用 | 40ms*attempt 重试 4 次；最终失败保持 dirty（:158-161） | 分支 |
| WB-44 | `Flush` 退出兜底 | 修改后 250ms 内立即退出 | 观察 ProcessExit | 防抖取消，未落盘修改同步写出（:167-198） | 语句 |
| WB-45 | `Flush` 与 SaveLoop 并发 [缺陷7] | 后台写盘进行中触发 ProcessExit | 观察 tmp 文件 | **当前实现**：两者共用 ConfigPath+".tmp"，存在互相覆盖竞态；预期 Flush 也走单写者闸门 | 竞态 |
| WB-46 | `SyncAdultFilter` | OnlyAdult 与 FilterAdult 同时为 true（非法组合） | 调用 | include=false，两者互斥语义保持（:58-66） | 分支 |
| WB-47 | 安全审查 | 检查配置文件内容 | 打开 junigrid.config.json | **发现**：NexusApiKey 明文存储 [缺陷6] | 安全 |

## TaskCenterService.cs

| ID | 测试方法 | 输入/前置条件 | 步骤 | 预期结果 | 覆盖类型 |
|---|---|---|---|---|---|
| WB-48 | `Load` 恢复 | tasks.json 中含 running 任务 | 重启 | running 全部转 failed（:39），日志保留（TaskItem.Log 有 setter，:194） | 分支 |
| WB-49 | `Report` 心跳合并 | 连续 100 条「正在下载…」进度 | 观察 t.Log | 仅覆盖上一条心跳行，不追加；事件行正常追加；超 200 条移除最旧（:85-93） | 边界 |
| WB-50 | `Report` 并发 [缺陷13] | 下载线程 Report 与 SaveNow 序列化同时进行 | 观察 | 浅拷贝快照下序列化时集合被改 → 偶发异常被吞、该次落盘跳过（下次重试兜底） | 竞态 |
| WB-51 | `Remove` | 移除运行中任务 | 调用 | t.Cts.Cancel() 联动取消后台下载（:120-128） | 语句 |
| WB-52 | `RequestSave` 首调并发 [缺陷12] | 两线程同时首次 RequestSave | 观察 | 懒初始化无锁 → 可能创建两个 Timer，泄漏一个；预期双重检查锁 | 竞态 |
| WB-53 | `TotalPercent` | 无 running 任务但列表顶部有 done(100%) | 调用 | 返回顶部任务进度而非 0（:171-175） | 分支 |
| WB-54 | `ClearMatching` | 筛选含 running 任务 | 调用 | 移除并取消 running 的 Cts（:142-159） | 语句 |

## PlayTimeService.cs

| ID | 测试方法 | 输入/前置条件 | 步骤 | 预期结果 | 覆盖类型 |
|---|---|---|---|---|---|
| WB-55 | `Tick` 游戏运行 | IsGameRunning=true | 等 30s tick | 当天秒数 +30 并落盘（:47-61） | 语句 |
| WB-56 | `Tick` 游戏未运行 | IsGameRunning=false | 等 tick | 不累计、不落盘 | 分支 |
| WB-57 | 跨天累计 | 23:59:50 游戏运行，tick 落在 00:00:20 | 观察 | 30s 计入新日期 key（DateTime.Now 在 lock 内取） | 边界 |
| WB-58 | `Load` 损坏 | playtime.json 非法 JSON | 启动 | 警告日志 + 空字典，不崩溃（:71） | 异常路径 |
| WB-59 | 并发 | Tick 线程与 UI 读 Snapshot 同时进行 | 调用 | lock(_gate) 保护，Snapshot 返回副本（:43） | 并发 |
| WB-60 | 精度 | 游戏运行 1 分钟内启停多次 | 对比 | 每 tick 固定 +30s，最大误差 ±30s（设计取舍，非缺陷） | 边界 |

## TranslationService.cs

| ID | 测试方法 | 输入/前置条件 | 步骤 | 预期结果 | 覆盖类型 |
|---|---|---|---|---|---|
| WB-61 | `TranslateBatch` 缓存命中 | 相同文本二次请求 | 调用 | 缓存命中瞬时返回，不发网络请求（:52） | 分支 |
| WB-62 | `TranslateBatch` 分块 | 100 条待译文本 | 调用 | 按 ≤48 条且 ≤6000 字符分块，结果索引对齐不错位（:59-84） | 边界 |
| WB-63 | `TranslateChunk` 主接口失败 | translate.googleapis.com 不可达 | 调用 | 切换 clients5.google.com 镜像（dict-chrome-ex 客户端标识），throttle 正常释放（finally，:132） | 异常路径 |
| WB-64 | `TranslateChunk` 返回缺条 | 接口少返回若干条 | 调用 | 缺失项回退原文，不错位（:77-79） | 异常路径 |
| WB-65 | 失败缓存 [缺陷8] | 网络完全断开时翻译一批文本 | 恢复网络后重译 | **当前实现**：原文被写入 _cache 永久缓存，恢复后也不再重试；预期失败项不入缓存 | 异常路径 |
| WB-66 | 并发上限 | 10 个 chunk 同时翻译 | 观察 | SemaphoreSlim(2) 限制最多 2 个在途请求（:100） | 并发 |

## LogLineClassifier.cs

| ID | 测试方法 | 输入/前置条件 | 步骤 | 预期结果 | 覆盖类型 |
|---|---|---|---|---|---|
| WB-67 | `Classify` | "[ERR] ..." / 含 " ERROR " / "[ERROR]" | 调用 | 返回 "err"（错误优先于更新提示，:41-44） | 分支 |
| WB-68 | `Classify` | 行含 "(you have 1.0.39)" 无级别标签 | 调用 | 返回 "upd"（:56-58） | 语句 |
| WB-69 | `Classify` | INFO 行简介含 "update by ..." | 调用 | 有级别标签时标签优先，不误判为 upd（:49-52 注释场景） | 分支 |
| WB-70 | `Classify` [缺陷9 关联] | 行被翻译为 "[00:37:32 信息 SMAPI]" | 调用 | 所有 Contains(" INFO ") 均不匹配 → 返回 ""，着色与筛选失效 | 边界（跨模块） |
| WB-71 | `MatchesFilter("loaded")` | "Loaded 50 mods:" 及条目行 | 调用 | 头/条目正则均匹配（:32-34） | 语句 |
| WB-72 | `MatchesFilter` | filter 未知值 | 调用 | 默认 true（全部通过） | 分支 |
