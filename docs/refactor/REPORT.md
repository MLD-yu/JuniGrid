# 重构交付报告

提交：`10fbcab`　基线：`10f426f`

---

## 一、结论

| 项 | 改前 | 改后 |
|---|---|---|
| 编译 | 0 错误 | **0 错误**（`--no-incremental` 全量重建） |
| 测试 | 475 通过 / 0 失败 | **475 通过 / 0 失败** |
| 测试文件改动 | — | **0 行**（未动一行，仍是验收安全网） |
| 公开 API | — | **零增减**（快照 diff 仅 `partial` 关键字） |
| 最大服务文件 | 3909 行 | **1102 行** |

**这是一次纯搬家：没有改任何逻辑，没有删任何有调用方的代码，没有改任何签名或可见性。**

---

## 二、改前改后功能对照表（逐个文件）

### 2.1 ModService：3909 → 600（共 10 个文件）

| 文件 | 行数 | 职责（改前 → 改后） | 等价性依据 |
|---|---|---|---|
| `ModService.cs` | 600 | 扫描/判重/启禁用/卸载 + static 构造 + sTmpSwept | 成员清单 122→122 |
| `ModService.Manifest.cs` | 312 | manifest.json 读写、备注同步、单包 zip 替换更新 | 同上 |
| `ModService.Overlay.Probe.cs` | 677 | 覆盖包**判定**：类型定义 + 宿主证据链 | OverlayPlan/Fail/Report 仍嵌套、可见性未变 |
| `ModService.Overlay.Resolve.cs` | 550 | 覆盖包**落点解析**、捆绑包壳拆分、汉化重打包识别 | 同上 |
| `ModService.Overlay.Manage.cs` | 471 | 覆盖包**落盘与账本**、健康度、开关、还原 | 同上 |
| `ModService.Install.cs` | 331 | 新装主流程 + 本体覆盖通道判据 | 同上 |
| `ModService.Archive.cs` | 600 | 边车/压缩包解压/回收站暂存/TrashGuard/NativeMethods | 三件同文件，未拆散 |
| `ModService.Entry.cs` | 156 | manifest.json → ModEntry 模型构建 | 同上 |
| `ModEntry.cs` | 38 | **顶层类型**（HEAD 里本来就在类外，列 0） | 引用名不变 |
| `NexusUpdateTruth.cs` | 216 | **顶层类型**（同上） | 引用名不变 |

**关键验证**：从 `git show HEAD` 提取拆分前版本，与 10 个新文件合并后比对 —— 内容行 1960 → 1954，
差值 6 全部是本次的 IsGameProcessAlive 内联（4 行）+ 2 处不可达 return false。**拆分本身零内容改动。**

### 2.2 ContentOverrideService：2395 → 173（共 8 个文件）

| 文件 | 行数 | 职责 | 等价性依据 |
|---|---|---|---|
| `ContentOverrideService.cs` | 173 | **唯一**持有全部路径常量 + 路径派生 + 键拼装 | 成员清单 106→106 |
| `.Store.cs` | 274 | 老位置搬家、原版仓库分层、无引用副本清理 | 同上 |
| `.Install.cs` | 297 | 账本视图 + 落账 + 落盘（含尾部哈希 helper） | PlanResult 仍嵌套 |
| `.Rebuild.cs` | 396 | 盘→账重建 + 页号回填 | 同上 |
| `.Rejudge.cs` | 242 | 重判 | 同上 |
| `.Plan.cs` | 333 | 解析与试算（只读不写盘） | PlanResult/PlanItem 嵌套未变 |
| `.State.cs` | 452 | 盘账一致性 + 开关/删除/还原 + 界面行归组 | LocaleTail 单处声明 |
| `.Resolve.cs` | 278 | 目标解析（纹理尺寸/基名索引/整张换判定） | 同上 |

### 2.3 NexusService：1818 → 424（共 7 个文件）

| 文件 | 行数 | 职责 | 等价性依据 |
|---|---|---|---|
| `NexusService.cs` | 424 | 传输层 + 限流闸门 + 响应缓存 | 成员清单 114→114 |
| `.Mod.cs` | 277 | mod 元数据 + JSON/BBCode 解析 | ModPageEvidence 仍嵌套 |
| `.Files.cs` | 252 | 文件清单/更新指纹/更新日志 + GraphQL 发射口 | GraphQlEndpoint 与 GraphQlAsync 同文件 |
| `.Requirements.cs` | 354 | 依赖/版本/按名搜索/译本 + 下载 | DownloadHttp + 工厂同文件 |
| `.Browse.cs` | 332 | 榜单/搜索/分块补页 | BrowseCache 仍嵌套 |
| `.Account.cs` | 150 | 账号与头像 | 同上 |
| `NexusModels.cs` | 65 | **15 个顶层 record**（本来就顶层，零风险搬家） | 引用名不变 |

### 2.4 InstallService：1706 → 1102（共 3 个文件）

| 文件 | 行数 | 职责 | 等价性依据 |
|---|---|---|---|
| `InstallService.cs` | 1102 | 安装执行 + 闸门/存活判定 + 两个嵌套 record | _installGate 等在新文件里 **0 命中** |
| `.DependencyRanking.cs` | 274 | 静态名称/UID 匹配（**零对外调用**，最干净的接缝） | 测试直接调用，类型名未变 |
| `.DependencyResolve.cs` | 337 | 这个 UID 是哪个 Nexus 页 解析器 | DisplayCache/ParentReqCache 整块搬 |

### 2.5 UpdateService：1178 → 576（共 3 个文件）

| 文件 | 行数 | 职责 | 等价性依据 |
|---|---|---|---|
| `UpdateService.cs` | 576 | 版本决策 + 缓存 + 两个顶层 record | Http + CreateClient 同文件 |
| `.SmapiInstall.cs` | 331 | SMAPI 安装器 | 同上 |
| `.Files.cs` | 288 | 文件/HTTP 管道（三处外部调用） | DownloadHttp + 工厂同文件 |

### 2.6 StorageService：838 → 587（共 2 个文件）

| 文件 | 行数 | 职责 | 等价性依据 |
|---|---|---|---|
| `StorageService.cs` | 587 | 存储页盘点与清理 | _gate/_sizes 只在此文件 |
| `.Migrate.cs` | 254 | 缓存目录搬迁 | _gate 仅被**使用**，未重复声明 |

---

## 三、顺带的零行为变化简化（5 项）

| 改动 | 文件 | 为什么等价 |
|---|---|---|
| 删不可达 return false ×2（CS0162） | ModService.Archive.cs | switch 各分支已 return，语句永不可达 |
| 删纯转发壳 CopyTree | StorageService.cs | 函数体只有一行转发，调用点直调同一方法 |
| 内联 NexusFilesUrl | InstallService.cs | 单行 URL 构造，唯一调用点 |
| 内联 IsGameProcessAlive ×2 | ModService.cs | 纯转发属性，且注释声称释放句柄但实际没有（注释本就过期） |
| EnsureUrlProtocolMarker bool→void | App.xaml.cs | 唯一调用点丢弃返回值 |

---

## 四、明确没做的事（等你决定）

| 项 | 原因 |
|---|---|
| **删除零引用成员** | 需你批准。清单见 DELETION-CANDIDATES.md，共 5 项 + 若干 API 收窄 |
| **跨服务重复代码合并** | 会改变行为：原子写重试节奏 300ms→100ms、SHA-256 失败模式不同 |
| **UI 层拆分**（MainWindow/App） | **零测试覆盖**，只能靠手动开程序验证，建议单独一轮 |
| **前端 CSS/JS** | 按约定只出报告不动手 |

---

## 五、遗留风险（诚实列出）

1. **UI 层未拆。** MainWindow.xaml.cs(1123) 和 App.xaml.cs(729) 仍是单文件。
   grep MainWindow test/ 返回空 —— 475 个测试对这俩是瞎的，拆它们必须手动验证。
2. **DepotDownloaderService 系列仍偏大。** Login.cs 1155 / Apply.cs 865 / FileOps.cs 759。
   这三个已经是 partial 分片，但没有进一步拆分，因为不在本次确认范围内。
3. **测试覆盖不均。** 按测试文件里出现的类名数：ModService 12、ContentOverrideService 6、
   InstallService 5、UpdateService 2、NexusService 2、**StorageService 1**、SaveVersionService 0。
   StorageService 搬走的 253 行只有 1 个测试文件看得见。
4. **StashOverview 经查从未接线**（git log -S 只命中一个快照提交），不是被 UI 删掉的入口。
   这条推翻了我早先「保留」的建议，现已改为可删。

---

## 六、如何自己复现验证

```bash
# 全量重建（不要用增量，会空转）
dotnet build JuniGrid.sln -v q --nologo --no-incremental

# 跑测试（注意：本仓库 dotnet test 不可用，.NET 10 SDK 移除了 VSTest 目标）
./test/JuniGridTestHarness/bin/Debug/net10.0-windows10.0.17763.0/JuniGridTestHarness.exe
# 期望：Total: 475, Errors: 0, Failed: 0
```

回滚：`git revert 10fbcab`（本次全部改动在一个提交里）