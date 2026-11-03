# 交接需求：JuniGrid 窗口拖入本地 mod 压缩包（拖放部分）

日期：2026-10-21　状态：按钮安装已实机验收通过；**窗口拖放两轮尝试均失败，光标显示禁止**

## 0. 一句话任务

让"从资源管理器把 zip/7z/rar 拖进 JuniGrid 主窗口 → 松手安装到 Mods"真正工作。
安装管线本身已经全部就绪且验收过，**只差把拖放事件接进来这一步**。

## 1. 现状：已完成、已验收、不要重做的部分

| 组件 | 文件 | 状态 |
| --- | --- | --- |
| 安装入口 `InstallLocalPacksAsync(paths)` | `JuniGrid/Services/InstallService.LocalPacks.cs` | ✅ 端到端实调通过（真实 `InstallNew`，含重复安装进回收站的幂等处理） |
| 粗筛 `HasPackExtension` / `IsLocalPackPath` / `FilterLocalPacks` | 同上 | ✅ 23 个单测全绿（`test/JuniGridTestHarness/LocalPackFilterTests.cs`） |
| 交接队列 `LocalPackDelivery`（Deliver/Take，事件+队列） | `JuniGrid/Services/LocalPackDelivery.cs` | ✅ 已接好 |
| 按钮安装（OpenFileDialog 多选） | `JuniGrid/Components/Pages/Mods.razor` `PickLocalPacks` | ✅ **用户实机确认"从本地上传有用"** |
| 页面接收与安装、跨页跳转 | `Mods.razor` `DrainLocalPacksAsync` / `MainLayout.razor` | ✅ 已接好（按钮走的这条） |
| WPF 拖放蒙层 | `JuniGrid/MainWindow.xaml` `DropHint` | ✅ 存在，等拖放事件驱动 |
| i18n | `JuniGrid/i18n/en.json` | ✅ 文案齐全 |

**唯一断链**：`MainWindow` 收不到"文件被拖进来/松手"事件。
现在代码里 `HookLocalPackDrop` 收到路径后做的事已经写好
（`OnFilesDropped` → `FilterLocalPacks` → `LocalPackDelivery.Deliver`），
只要事件能进来，整条链就通。

## 2. 两轮失败尝试（别再走回头路）

### 尝试 1：WPF `AllowDrop` + `DragEnter/Drop` 事件 —— 失败
- 做法：`webView.AllowExternalDrop = false;` + `Window.AllowDrop = true` + WPF 拖放事件。
- 实机表现：**拖文件进窗口，光标变禁止，WPF 事件一次都没触发**。

### 尝试 2：在主窗口 HWND 上注册自研 OLE drop target —— 失败
- 做法：`RevokeDragDrop(mainHwnd)` + `RegisterDragDrop(mainHwnd, WindowFileDropTarget)`，
  WebView2 初始化后立刻一次、1 秒后再一次。实现见
  `JuniGrid/Services/WindowFileDropTarget.cs`（自声明 `IDropTarget`/`POINTL`/`DropEffects`，
  从 CF_HDROP 读路径，代码本身可复用，**别删**）。
- 实机表现：**光标仍然禁止**。
- 注册结果有日志：`%APPDATA%\JuniGrid\startup.log` 里的
  「OLE drop target 注册成功 / 注册失败 hr=0x…」（先拿这条日志定位，见 §4）。

## 3. 根因分析（高置信度）

本项目 WebView2 是 **Composition 宿主**（`WebView2CompositionControl`），
但它在主窗口内**仍然创建了子 HWND**（窗口类通常是 `Chrome_WidgetWin_0` /
`Chrome_RenderWidgetHostHWND` / `VisualHostWindow` 之一，项目 MainWindow.xaml
自己的注释也写着 "WebView2 is a child HWND"）。

OLE 拖放的目标解析规则（`OleGetDropTarget`）：从光标下**最深**的 HWND 开始，
该 HWND 注册过 drop target 就用它，否则沿父链向上找。
WebView2 把它的 target 注册在**子 HWND** 上，位置正好盖住整个客户区 →
解析在子 HWND 就终止了，**永远轮不到主窗口上我们注册的 target**。
`AllowExternalDrop=false` 只是让它的 target 对文件拖放返回 `DROPEFFECT_NONE`
（这就是禁止符），并不会把 target 注销。

## 4. 第一步：先取证，再动手（10 分钟）

1. 让用户双击启动（见 §7 环境坑），拖一次文件，然后把
   `%APPDATA%\JuniGrid\startup.log` 最后 10 行发回来——确认
   「OLE drop target 注册成功 hr=0」是否出现（出现=主窗注册没问题，
   问题就锁定在子 HWND 截胡）。
2. 加一段临时诊断（或写个 20 行的独立小程序）：`EnumChildWindows(mainHwnd)`
   列出所有子 HWND 的类名/矩形，并对每个子 HWND 调 `RevokeDragDrop` 打印 hr
   （`S_OK`=它确实注册过 target，`OLE_E_WRONGCOMPOBJ`/`DRAGDROP_E_NOTREGISTERED`
   附近错误=没注册）。重点看有没有 `Chrome_WidgetWin*` 类、
   尺寸≈客户区的子窗。

## 5. 修复方案（按推荐顺序）

### 方案 A（推荐，最小改动）：把 WebView2 子 HWND 的 target 摘掉，让解析落到主窗

在现有 `HookLocalPackDrop` / `RegisterOurDropTarget` 的同一时机
（BlazorWebViewInitialized + 延迟重试）加一段：

```
EnumChildWindows(mainHwnd, (h, _) => {
    RevokeDragDrop(h);        // 摘掉 WebView2 在子 HWND 上的 target（有则成功）
    return true;
});
```

主窗上我们的 target 已经注册好了（尝试 2 的代码），子窗一旦没有 target，
`OleGetDropTarget` 沿父链上溯就会命中它。
**不需要**在子 HWND 上再注册我们的 target，也不需要动 WPF 的 `AllowDrop`
（保持 false/不设，WPF 那套已被证伪）。

注意：WebView2 可能在导航、resize、进/出后台时**重新注册**它的 target。
把"摘子窗 target"抽成方法，在下面这些时机各调一次：
- `BlazorWebViewInitialized`（现有）
- 延迟 1s / 3s 各一次（现有重试链上）
- `CoreWebView2.NavigationCompleted`
- `CoreWebView2.WindowCloseRequested` 不需要；`Activated` 事件里可再来一次

### 方案 B（A 无效时）：把我们的 target 直接注册到子 HWND

同 A 的时机枚举子 HWND，对**每个**子 HWND：
`RevokeDragDrop(child); RegisterDragDrop(child, _dropTarget);`
（`WindowFileDropTarget` 现成可复用；子 HWND 由 UI 线程创建，回调仍在 UI 线程，
现有的 `Dispatcher.BeginInvoke` 包装不用改。）
蒙层/路径处理逻辑不变。

### 方案 C（B 也无效时的兜底，需先问用户）：放弃窗口拖放

按钮安装已验收可用。撤掉 `HookLocalPackDrop` 调用与 `DropHint` 蒙层
（保留 `WindowFileDropTarget.cs` 文件无妨，或一并删），
只保留「从文件安装」按钮，并把 Mods 页按钮的 `data-tip` 文案改成
「从本地文件安装 mod 压缩包（也可直接拖入窗口）」→ 去掉"拖入"半句。
**做这个决定前必须告知用户并确认。**

## 6. 验收标准

1. 拖 `zz-junigrid-droptest.zip` 进窗口：光标是**绿色复制箭头**（不是禁止符），
   窗口出现「松手即安装到 Mods」蒙层；
2. 松手：右下角任务中心出现「安装本地包：zz-junigrid-droptest.zip」，
   完成后 Mods 列表出现 **ZZ JuniGrid Drop Test**，toast「已安装 1 个本地 mod」；
   磁盘上 `E:\Steam\steamapps\common\Stardew Valley\Mods\zz-junigrid-droptest\`
   有 manifest.json；
3. 再拖同一个包第二次：安装成功且旧目录进 `Mods\.junigrid_trash\`（不静默覆盖）；
4. 拖一个文件夹或 `.txt`：红色 toast「只支持压缩包…」，不开任务、不跳转；
5. 在**别的页面**（首页/设置）拖：自动跳到 Mod 管理页并完成安装；
6. 拖拽中途把窗口切走（Alt+Tab）：蒙层不残留；
7. 回归：按钮安装仍然可用；主题切换/窗口最小化恢复正常（蒙层与
   `ThemeRevealOverlay` 同层，别改 z 序）；游戏内 SMAPI 日志页正常。
8. 测完删掉 ZZ JuniGrid Drop Test（含回收站）与桌面测试 zip。

## 7. 环境与命令（前人踩过的坑）

- **构建**：`dotnet build JuniGrid\JuniGrid.csproj -c Debug`。
  运行中的 JuniGrid 会锁 exe/dll 导致 MSB3027——先 `Stop-Process -Name JuniGrid`。
- **测试**（xunit.v3 / MTP，不是 `dotnet test`）：
  `dotnet run --project test\JuniGridTestHarness\JuniGridTestHarness.csproj -c Release`；
  单类：直接跑 `test\JuniGridTestHarness\bin\Release\net10.0-windows10.0.17763.0\JuniGridTestHarness.exe -class "JuniGridTestHarness.LocalPackFilterTests"`。
  基线：全量 521 个，21 个失败为环境原因（写 `%LOCALAPPDATA%` 被拒），与本功能无关。
- **启动**：必须由**用户本人**双击
  `C:\Users\21092\OneDrive\Desktop\JuniGrid\JuniGrid\bin\Debug\net10.0-windows10.0.17763.0\JuniGrid.exe`。
  AI 的 shell（含提权后的会话）拉起的实例带受限令牌，写不了 `%APPDATA%` →
  WebView2 初始化失败 → **白屏**，已反复验证，别浪费时间从 shell 启动。
- 单实例互斥体 `JuniGrid.SingleInstance`：启动"没反应/秒退"先查它
  （`Mutex.OpenExisting`），僵尸实例会吞掉新启动。
- 测试包：`C:\Users\21092\Desktop\zz-junigrid-droptest.zip`
  （UniqueID `zz.junigrid.dragdroptest`）。没有就按 §1 表里 manifest 形状重造一个。
- 日志：`%APPDATA%\JuniGrid\startup.log`（启动/拖放注册）、
  `juni-grid.log`（运行时）、`crash.log`（揭示流程）。

## 8. 相关代码坐标

- `JuniGrid/MainWindow.xaml.cs`：`HookLocalPackDrop` / `RegisterOurDropTarget` /
  `OnFilesDropped` / `ShowDropHint` / `ResetDropHint` / `NotifyDroppedNothing`
  （在 `BlazorWebViewInitialized` 内被调用，`_wv2` 字段即 CompositionControl）
- `JuniGrid/Services/WindowFileDropTarget.cs`：自声明 `IDropTarget` 的 COM 实现，
  CF_HDROP 读路径，Enter/Leave 计数控蒙层——**直接复用**
- `JuniGrid/MainWindow.xaml`：`DropHint` 蒙层（Border，IsHitTestVisible=False）
- `JuniGrid/Services/InstallService.LocalPacks.cs`：`FilterLocalPacks` + `InstallLocalPacksAsync`
- `JuniGrid/Services/LocalPackDelivery.cs`：`Deliver`/`Take`/`Delivered`
- `JuniGrid/Components/Pages/Mods.razor`：`DrainLocalPacksAsync` / `PickLocalPacks`
- `JuniGrid/Components/Layout/MainLayout.razor`：收到 Deliver 时导航到 /mods
