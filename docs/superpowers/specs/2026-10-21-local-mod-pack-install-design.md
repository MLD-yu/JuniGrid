# 本地 mod 压缩包安装：拖放 + 按钮入口

日期：2026-10-21
状态：待实现

## 背景

目前把 mod 装进 `Mods/` 只有三条入口，全部来自 Nexus：

| 入口 | 位置 |
| --- | --- |
| N 网直装 | `InstallService.InstallModDirectAsync` |
| `.nxm` 网页一键安装 | `InstallService.HandleNxmLinkAsync` |
| 依赖直装 | `InstallService.InstallMissingDependenciesAsync` |

三条最后都汇到同一个函数：`ModService.InstallNew(gamePath, zipPath, ...)`。

用户想从本地装包（U 盘、论坛下载、朋友分享），现在没有任何入口。而且
`InstallNew` 早就把本地包的形状都吃透了 —— 它按**魔数**而不是扩展名识别
zip / rar / 7z（`ModService.Archive.cs:230` `DetectArchiveFormat`），能处理
manifest、覆盖包、CP 转换、立绘包转换、同名旧目录进回收站。缺的只是一条
"把本地路径递进去"的通路。

## 目标

- 把 `.zip` / `.7z` / `.rar` 拖进 Mod 管理页，松手即安装进 `Mods/`。
- 工具栏提供一个"从文件安装"按钮，鼠标悬停提示"从本地文件安装 mod 压缩包"。
- 两者走**完全相同**的安装管线，与 Nexus 安装共享同一把写入锁与任务中心。

## 非目标

- **不支持拖未压缩的文件夹**。`InstallNew` 只吃压缩包，首版不新增"文件夹直装"
  通道。拖入文件夹时给出明确提示，而不是静默失败。
- 不解析 Nexus mod ID。本地包装完后没有 N 网身份，更新检测按现有"无
  NexusModId"路径自然跳过。
- 不做拖放的跨页排队。不在 Mods 页时拖入，跳转过去再装。

## 关键约束：WebView2 拿不到真实文件路径

网页侧的 `DataTransfer` 只暴露 `File` 对象，**不暴露磁盘路径**。几百 MB 的
压缩包不可能经由 JS interop 转字节数组。因此拖放**必须在 WPF 层接收**：

```csharp
args.WebView.AllowExternalDrop = false;   // 让 WebView2 不要吞掉外部拖放
AllowDrop = true;                          // 由窗口这一层接
```

`AllowExternalDrop` 默认是 `true`（WebView2 自己处理拖放，网页能收到
`drop` 事件但拿不到路径）。设成 `false` 后，拖放事件冒泡到 WPF 宿主，
`e.Data.GetData(DataFormats.FileDrop)` 就能拿到真实路径。

### 已知风险

本项目用的是 `WebView2CompositionControl`（`MainWindow.xaml.cs:721`），
不是普通的 `WebView2`。后者是独立子窗口（HWND），拖放投递给子窗口，
`AllowExternalDrop = false` + WPF `Drop` 是社区标准做法且经过大量验证；
前者**没有独立子窗口**，网页直接合成进 WPF 视觉树。

反射核实过 SDK 1.0.3179.45：`CoreWebView2CompositionController` 只暴露
`AllowExternalDrop` 属性，**没有 `DragEnter` / `DragOver` / `Drop` 事件**
（普通 `CoreWebView2Controller` 有）。也就是说合成模式下拖放完全由宿主
OLE 目标接管，WebView2 不参与。

**所以必须先做 spike**：设完 `AllowExternalDrop=false` + `Window.AllowDrop=true`，
在 `DragOver`/`Drop` 打日志，实际拖一个文件进去看有没有反应。

**降级路径**：spike 不通过就撤掉拖放部分，只保留按钮入口。按钮链路是纯
Blazor + `Microsoft.Win32.OpenFileDialog`，不依赖任何 WebView2 拖放能力。

### 拖放事件的三条硬性细节

1. **`DragOver` 必须 `e.Effects = DragDropEffects.Copy; e.Handled = true;`**
   —— 不设 `Effects`，光标显示禁止符；不设 `Handled`，`Drop` 根本不触发。
2. **`DragEnter` 也必须 `e.Handled = true`**，否则冒泡链上别的元素先吃掉。
3. **`Window` 的 `AllowDrop = true` 要在 XAML 或构造函数里设**，不能等到
   `BlazorWebViewInitialized` —— 那个事件在 WebView 初始化完之后才来，而
   WPF 的 OLE drop target 注册在窗口创建时就完成了。

## 架构

```
资源管理器拖入
    │
    ▼
MainWindow.Drop (WPF)  ──►  DataFormats.FileDrop → string[]
    │                          │
    │                          ├─ 过滤非压缩包 → toast 提示
    │                          ▼
    │                     InstallService.InstallLocalPacksAsync(paths)
    │                          │
按钮点击 (Mods.razor)          │  每个包开一条 TaskItem（kind = "install"）
    │                          │
    ▼                          ▼
OpenFileDialog  ──► 同一入口    _installGate 串行 → InstallNew
                               │
                               ▼
                          Scan + OnChanged + PageRefresh("mods")
```

### 新增 / 改动的单元

| 文件 | 变更 |
| --- | --- |
| `Services/InstallService.LocalPacks.cs` | **新增**。`IsLocalPackPath` / `FilterLocalPacks`（纯函数，可单测）+ `InstallLocalPacksAsync` |
| `Services/LocalPackDelivery.cs` | **新增**。WPF 层与 Blazor 层之间的送达通道（事件 + 最近一次请求） |
| `MainWindow.xaml.cs` | `AllowExternalDrop = false`、`AllowDrop = true`、四个拖放事件 |
| `Components/Pages/Mods.razor` | 工具栏按钮 + 订阅送达事件 + 自动刷新 |
| `Components/Layout/MainLayout.razor` | 非 Mods 页收到送达时 `NavigateTo("/mods")` |
| `wwwroot/js/junigrid.dragdrop.js` | ~~新增~~ **不需要**（蒙层改 WPF） |
| `wwwroot/index.html` | ~~引入新 js~~ **不需要** |
| `i18n/en.json` | 新增文案英文 |
| `test/JuniGridTestHarness/LocalPackFilterTests.cs` | **新增**单测 |

### `HasPackExtension` / `IsLocalPackPath` / `FilterLocalPacks`

三个函数，**前两个分开是为了好测** —— `IsLocalPackPath` 要查磁盘，
单测就得造临时文件；`HasPackExtension` 是纯字符串判断：

```csharp
internal static bool HasPackExtension(string? path)        // 纯函数
internal static bool IsLocalPackPath(string? path)         // 纯函数 + File.Exists + 非目录
internal static IReadOnlyList<string> FilterLocalPacks(IEnumerable<string>? paths)
```

`HasPackExtension`：非空、扩展名（忽略大小写）属于 `.zip` / `.7z` / `.rar`。
**用 `Path.GetExtension`，不用 `EndsWith`** —— 后者会把 `foo.zip.txt` 判成压缩包。

`IsLocalPackPath`：`HasPackExtension` && `File.Exists` && 不是目录。

`FilterLocalPacks`：`paths.Where(IsLocalPackPath)` 去重（同一路径拖两次
只装一次，按 `StringComparer.OrdinalIgnoreCase` 去重）。

注意**不能**用"扩展名对就放行"来替代 `ArchiveReadable`：真实下载缓存里
有"扩展名 `.zip`、头四字节却是 `Rar!`"的包（`ModService.Archive.cs:131`
的注释记录了这个坑）。但那是**识别格式**的事，`InstallNew` 内部已经处理，
入口这一层只做粗筛，不做二次判定 —— 这里刻意不打开文件，因为粗筛要在
`Drop` 事件里同步跑完，不能为了看一眼魔数去 IO 几十个文件。

### `InstallLocalPacksAsync`

签名：

```csharp
public Task<int> InstallLocalPacksAsync(IReadOnlyList<string> paths)
```

返回成功安装的个数。

每个包：

1. `_center.Start(LocService.Tf("安装本地包：{0}", 文件名), "install")`
2. `Interlocked.Increment(ref _busyCount)`（复用现有 Busy 语义）
3. `using var longRun = _center.BeginLongRunning(task)` —— `InstallNew` 是同步
   重活，实测可达 50s+，必须豁免僵尸看门狗
4. `await EnterInstallGateAsync(...)` / `finally ExitInstallGate()`
5. `await Task.Run(() => _mods.InstallNew(...))` —— 丢线程池，别冻 UI
6. `_center.Finish(task, ok, msg)`

**串行**：一个接一个，不并发。`_installGate` 本来就是全局串行，但显式串行
还能保证任务中心里多条本地包的顺序与用户拖入顺序一致，且失败不会互相
干扰。

**失败不中断**：某个包失败（没有 manifest、覆盖包认不出宿主、UID 撞车），
记下原因继续装下一个。全部结束给一条汇总 toast：
"已安装 N 个；M 个失败（原因见任务中心）"。

**装完统一收尾一次**：所有包处理完后 `Notify()` 一次即可。

**不要**再发 `PageRefresh.Request("mods")`：`Notify()` → `OnChanged` →
`Mods.razor:3259 OnInstallerChanged` 已经 `RescanAndRestate`（清缓存 + 全盘
扫描 + 重新判定更新）。再补一条刷新信号就是第二遍全盘扫，白烧 IO。

`NotifyPortraits()` 仍然要发 —— 本地包可能落进立绘页的素材目录，
而 Mods 页的重扫不会碰立绘缓存。

### `InstallNew` 的真实调用形状

```csharp
string? InstallNew(string gamePath, string zipPath, out string? modName,
    int? nexusModId = null, string? requireUniqueId = null,
    string? portraiturePackName = null, ConfigService? cfg = null,
    List<string>? installNotes = null,
    IReadOnlyList<NexusRequirement>? packRequirements = null)
```

返回 **null = 成功**，错误串 = 失败。本地包没有 N 网身份，`nexusModId`
传 null；但 **`cfg: _cfg` 必须传** —— 不传就走"记不了账宁可不装"那条
分支，覆盖型包会被直接拒绝。

`portraiturePackName` 传压缩包文件名（去掉扩展名），这是立绘包转 CP 包时
用来命名目录的，与 Nexus 链路传任务标题同理。

**不要传 `packRequirements`**：那是从 N 网页面拿的登记前置，本地包没有
这个来源，传 null 让 `ProbeOverlay` 只按包内路径判断。

### `LocalPackDelivery`

WPF 层没有 Blazor 的渲染上下文，不能直接调组件。用一个极小的单例，
**同时是事件总线和一个"待领取的请求"槽**：

```csharp
public sealed class LocalPackDelivery
{
    private readonly Queue<IReadOnlyList<string>> _queue = new();

    public event Action? Delivered;

    public void Deliver(IReadOnlyList<string> paths)   // WPF 线程调用
    {
        _queue.Enqueue(paths);
        Delivered?.Invoke();
    }

    /// <summary>取走最早的一批待领取请求（取一次就没了）。没有则返回 null。</summary>
    public IReadOnlyList<string>? Take()
        => _queue.Count > 0 ? _queue.Dequeue() : null;
}
```

**为什么是队列不是单槽**：用户停在首页时连拖两批，单槽会让第二批覆盖
第一批，只装一半。

**为什么需要这个槽**：拖入时 Mods 页可能还没挂载（用户停在首页/设置页）。
光靠事件，`MainLayout` 导航过去时事件已经发完，新挂载的 `Mods.razor`
什么也收不到 —— 用户看到跳转，然后什么都没发生。

**领取顺序**：

1. WPF `Drop` → `Deliver(paths)`，事件同步触发
2. `MainLayout` 收到：`Nav.Uri` 不在 `/mods` → `NavigateTo("/mods")`；
   已经在就不动
3. `Mods.razor` 收到：`Take()` 并 `InstallLocalPacksAsync` —— **它是唯一的
   安装方**
4. 若 `Mods.razor` 是导航之后才挂载的，在 `OnInitialized` 里 `Take()` 补领

`Take()` 的"取一次就没了"保证：Mods 页已经挂着时，`MainLayout` 不会
重复装；Mods 页后挂载时，补领那一次也只会装一遍。

**线程**：`Delivered` 在 WPF UI 线程触发，而 Blazor Hybrid 的渲染上下文
也在 WPF Dispatcher 上 —— 但组件侧仍一律走 `InvokeAsync`，与
`TaskDock.razor` 订阅 `Center.OnChanged` 的写法保持一致，不赌线程模型。

**为什么不让 WPF 直接调 `InstallLocalPacksAsync`**：装完要刷新 Mods 页，
而刷新是 Blazor 的活。让页面自己订阅，责任边界清楚；WPF 层只负责"把路径
交出去"。

### 蒙层

**不能用 DOM**。`AllowExternalDrop = false` 之后 WebView2 不再处理外部拖放，
网页里根本收不到 `dragenter` / `dragleave` —— JS 计数方案跑不起来。

改用 **WPF 蒙层**：`MainWindow.xaml` 的 `Grid` 里加一层 `DropHint`（与
`ThemeRevealOverlay` 同级，后者已经证明 WPF 元素能盖在合成 WebView 之上），
`IsHitTestVisible="False"`，`Visibility="Collapsed"`。

- `DragEnter` → `Visibility = Visible`
- `DragLeave` / `Drop` → `Collapsed`
- **用 `DragEnter` / `DragLeave` 计数**：进一次 +1，出一次 -1，归零才隐藏。
  鼠标从窗口边缘移进子元素会触发一次 `DragLeave`，不计数会疯狂闪烁。
- 计数在 `Drop` 与窗口 `Deactivated` 时强制归零，防止漏事件后蒙层卡死。
- 蒙层样式用 WPF 画（半透明遮罩 + 虚线框 + 文案），不依赖网页 CSS。
  文案走 `LocService.Tr`，与全站一致。

**好处**：省掉一整条 JS 桥（不需要 `dropHint` 函数、不需要
`ExecuteScriptAsync`），也绕开了 WebView2 合成层与网页 DOM 的时序问题。

### 按钮

Mods 页工具栏，文件夹图标旁：

```razor
<button class="jg-mini-btn" data-tip='@Loc.T("从本地文件安装 mod 压缩包")'
        @onclick="PickLocalPacks">
```

`data-tip` 是全站已有的胶囊提示机制（`TitleBar` / 文件夹按钮都在用）。

`PickLocalPacks` 里直接 `new OpenFileDialog { Multiselect = true, Filter = ... }`
—— Blazor Hybrid 跑在 WPF UI 线程上，可以直接弹（`GameVersionDialog.razor:615`
的 `OpenFolderDialog` 是同一套写法）。

## 错误处理

| 情况 | 行为 |
| --- | --- |
| 游戏目录未设置 | toast 提示去设置页；不创建任何任务 |
| 拖入文件夹 | toast"只支持压缩包（zip / 7z / rar）" |
| 拖入非压缩包 | 同上 |
| 包损坏 / 打不开 | 该包任务落 failed，其余包继续 |
| 全部失败 | 汇总 toast 报失败数，指向任务中心 |

**过滤发生在哪一层**：`LocalPackDelivery` 之前。WPF `Drop` 里先按
`FilterLocalPacks` 粗筛一遍 —— 拖进来的如果是文件夹或 `.txt`，**根本不该
跳到 Mod 管理页**，更不该开任务。只有至少一个合法包才 `Deliver`。

**toast 由谁发**：`InstallService` 没有 JS interop，发不了 toast。所有用户
可见的提示（"只支持压缩包"、"已安装 N 个 / M 个失败"、"还没设置游戏目录"）
**统一由 `Mods.razor` 发**。WPF 侧被筛掉的条目直接调全站已有的
`junigridJs.toast`（经 `ExecuteScriptAsync`），不额外造一条通道。

**游戏目录未设置**：`Mods.razor` 在调 `InstallLocalPacksAsync` 之前先查
`Cfg.Current.GamePath`，为空就 toast `"还没设置游戏目录 —— 先到「设置」页选择"`
（照抄 `InstallModDirectAsync` 的文案），不创建任务。

## 测试

**单测**（`LocalPackFilterTests.cs`）：

- `HasPackExtension`：`.ZIP` / `.Zip` / `.zip` 都 `true`；`.txt` / 无扩展名 /
  `foo.zip.txt` 都 `false`（这条专门钉住 `Path.GetExtension` vs `EndsWith`）
- `IsLocalPackPath`：不存在的路径 `false`；目录路径 `false`；真实临时文件 `true`
- `FilterLocalPacks`：合法与非法混合只留合法的；同一路径重复只留一份；
  大小写不同的同一路径只留一份

**单测跑法**：不是 `dotnet test`。这是 .NET 10 + xunit.v3 的 MTP 工程：

```
dotnet run --project test/JuniGridTestHarness/JuniGridTestHarness.csproj -c Release
```

**实机验证**（用户已授权在真实游戏目录测试）：

1. 造一个测试包：`manifest.json`（UniqueID = `zz.junigrid.dragdroptest`）+
   一个占位资源文件，压成 zip
2. 拖进 Mod 管理页 → 蒙层亮 → 松手 → 任务中心出现"安装本地包"
3. 验证 `E:\Steam\steamapps\common\Stardew Valley\Mods\` 下出现对应目录
4. 点"从文件安装"按钮 → 对话框弹出 → 选中同一个包 → 再装一次
   （这次应触发同名目录处理，验证它没有静默覆盖）
5. 列表自动刷新、任务中心显示成功
6. **清理**：把测试 mod 从 Mods 卸载掉

## 实现后的验证结果（2026-10-21）

### 已完成并通过

| 项 | 结果 |
| --- | --- |
| `dotnet build JuniGrid\JuniGrid.csproj -c Debug` | **0 错误**；新增文件零警告 |
| 单测 `LocalPackFilterTests` | **23 个全部通过**（0 失败） |
| 全量单测 | 521 总数 / 21 失败 —— 与改动前**基线完全一致**（`git stash` 前后对跑确认）；失败全是 `%LOCALAPPDATA%` 写入被环境拒绝，与本改动无关 |
| `InstallNew` 端到端真实调用 | **成功**：返回 null，`modName` = "ZZ JuniGrid Drop Test"，`Mods\zz-junigrid-droptest\` 下 `manifest.json`（277 B）+ `EntryDll`（13 B）均落盘 |
| 重复安装同一包 | **正确**：旧目录移入 `.junigrid_trash\zz-junigrid-droptest_20261021_195030`，新版本装上，**没有静默覆盖** |
| `FilterLocalPacks` 真实路径测试 | **正确**：`.txt` / 不存在的文件 / 目录 / 大小写重复路径全被排除，只留合法 zip |

### 未能完成：GUI 拖放实机验证

**本次会话的文件沙箱（workspace-write）禁止写入 `%APPDATA%` 与游戏目录**，
而 JuniGrid 启动本身就要写 `%APPDATA%\JuniGrid`（配置 / 日志 / WebView2 数据）。
因此 GUI 起不来，`WebView2CompositionControl` 能否收到外部拖放**仍未验证**。

上述端到端验证是通过反射直接调用 `ModService.InstallNew` 完成的 ——
**服务层链路已确认可用**，未验证的只剩 WPF 拖放事件本身。

**待办**：在正常环境（不经沙箱启动的 JuniGrid）里做一次拖放测试；
若拖放不响应，按设计撤掉拖放部分，只保留按钮入口。

## 风险

| 风险 | 缓解 |
| --- | --- |
| `WebView2CompositionControl` 收不到外部拖放 | 按钮入口保底；**尚未实机验证**，见上节 |
| `DragLeave` 在子元素间频繁触发导致蒙层闪烁 | 只在窗口级切换，且用 `DragEnter` 计数 |
| 大包安装期间 UI 假死 | `Task.Run` + `BeginLongRunning`（与 Nexus 直装同款） |
| 本地包无 N 网身份，更新检测报错 | 走现有 `NexusModId == null` 分支，已存在 |
