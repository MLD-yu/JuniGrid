using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using JuniGrid.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Web.WebView2.Core;

namespace JuniGrid;

public partial class MainWindow : Window
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid", "startup.log");

    internal static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n");
        }
        catch { }
    }

    public MainWindow()
    {
        try
        {
            Log("=== JuniGrid boot ===");
            Log($"BaseDir = {AppContext.BaseDirectory}");
            var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            Log($"wwwroot/index.html exists? {File.Exists(wwwrootPath)} @ {wwwrootPath}");

            // v0.2.2：配置最早加载 —— 缓存位置（含 WebView2 目录）由它决定
            var configService = new ConfigService();

            // v0.2.2：上次更改缓存目录时 WebView2 正被占用无法搬 → 趁 WebView2 还没初始化，先执行遗留迁移
            var wv2Use = StoragePaths.WebView2Dir;
            var wv2From = configService.Current.PendingWebView2MoveFrom;
            if (!string.IsNullOrWhiteSpace(wv2From) && Directory.Exists(wv2From))
            {
                if (string.Equals(Path.GetFullPath(wv2From), Path.GetFullPath(wv2Use), StringComparison.OrdinalIgnoreCase))
                {
                    configService.Current.PendingWebView2MoveFrom = null;
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(wv2Use)!);
                        if (Services.StorageService.TryMoveTree(wv2From, wv2Use))
                        {
                            Log($"WebView2 数据已迁移到 {wv2Use}");
                            configService.Current.PendingWebView2MoveFrom = null;
                        }
                        else
                        {
                            wv2Use = wv2From;   // 部分文件占用 → 本会话继续用旧目录，下次启动再试
                            Log("WebView2 数据迁移不完整（部分文件被占用），本会话继续使用 " + wv2From);
                        }
                    }
                    catch (Exception ex)
                    {
                        wv2Use = wv2From;
                        Log("WebView2 数据迁移失败: " + ex.Message + "（本会话继续使用 " + wv2From + "）");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(wv2From))
            {
                configService.Current.PendingWebView2MoveFrom = null;   // 原目录已不存在
            }
            if (configService.Current.PendingWebView2MoveFrom is null && wv2From is not null)
            {
                configService.Save(configService.Current);
            }

            // v0.2.2：可迁移项默认位置统一挪到 %TEMP%\JuniGrid —— 未设置缓存目录时，
            // 把旧默认位置（LocalAppData）的既有数据一次性搬过去（WebView2 必须在初始化前搬完）
            if (StoragePaths.CacheRoot is null)
            {
                var legacyPairs = new (string From, string To)[]
                {
                    (Path.Combine(StoragePaths.LocalAppDataDir, "smapi-installer"), StoragePaths.SmapiInstallerDir),
                    (Path.Combine(StoragePaths.LocalAppDataDir, "mods-backup"), StoragePaths.ModsBackupDir),
                    (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid_WV2"), StoragePaths.WebView2Dir),
                };
                foreach (var (from, to) in legacyPairs)
                {
                    try
                    {
                        if (!Directory.Exists(from) || Directory.Exists(to)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        if (Services.StorageService.TryMoveTree(from, to))
                            Log($"旧默认缓存已迁移到 {to}");
                        else
                            Log($"旧默认缓存迁移不完整（部分文件被占用），留在原处可稍后清理: {from}");
                    }
                    catch (Exception ex) { Log("旧默认缓存迁移失败: " + ex.Message); }
                }
            }

            // Isolate the Blazor WebView2 user-data folder.
            // 注意：不要再 pin WEBVIEW2_BROWSER_EXECUTABLE_FOLDER ——
            // WebView2 运行时自动更新后旧版本目录会被删除，固定路径会变成
            // 无效目录，导致初始化直接报 0x8007139F（状态错误）。交给系统
            // 自动定位运行时即可。
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", wv2Use);

            var services = new ServiceCollection();
            services.AddWpfBlazorWebView();
#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif
            services.AddFluentUIComponents();
            services.AddSingleton(configService);   // v0.2.2：最早加载的那个实例直接注册，避免二次实例化
            services.AddSingleton<GameService>();
            services.AddSingleton<ModService>();
            services.AddSingleton<LauncherService>();
            services.AddSingleton<SteamService>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<NexusService>();
            services.AddSingleton<UpdateQueueService>();
            services.AddSingleton<PageRefreshService>();
            services.AddSingleton<TaskCenterService>();
            services.AddSingleton<InstallService>();
            services.AddSingleton<NexusSsoService>();
            // v0.2.1：缓存与存储管理 + 内存管理
            services.AddSingleton<StorageService>();
            services.AddSingleton<MemoryService>();
            // v1.0.2：应用自更新检查
            services.AddSingleton<SelfUpdateService>();
            // v1.08：Nexus 封面/图片本地缓存（国内 CDN 直连极慢）
            services.AddSingleton<CoverCacheService>();
            // v1.1.5：每日游玩时长统计（首页 GitHub 式热力图数据源）
            services.AddSingleton<PlayTimeService>();
            // v1.1.2：内置翻译器（谷歌引擎 + 磁盘缓存 + 微批），详情页/日志页共用
            services.AddSingleton<TranslationService>();
            var provider = services.BuildServiceProvider();
            Resources.Add("services", provider);
            App.Services = provider;
            Log("DI configured");

            // 游戏在运行但不是本程序启动的（如 JuniGrid 重启）→ 接上现有 SMAPI 日志
            provider.GetRequiredService<LauncherService>().AttachIfGameRunning();

            // v0.2.1：内存管理后台循环随启动常驻 —— 定时/阈值自动压缩不依赖设置页是否打开过
            _ = provider.GetRequiredService<MemoryService>();

            // v1.1.5：游玩时长统计循环随启动常驻 —— 不管首页开不开都在累计
            _ = provider.GetRequiredService<PlayTimeService>();

            // v1.0.2：启动后台检查一次应用新版本（不阻塞 UI，失败静默）
            provider.GetRequiredService<SelfUpdateService>().StartBackgroundCheck();

            InitializeComponent();
            // v1.1.2b：拖拽最小尺寸完全由 WM_GETMINMAXINFO hook 强制（v1.1.8 起按所在
            // 显示器工作区比例计算，见 WndProcClampMaximized）。
            // 必须放在 InitializeComponent 之后 —— XAML 里的 MinWidth/MinHeight(1100/650 DIP)
            // 会在高 DPI 下换算成更大的物理值把窗口二次拉大，覆盖这里清零前的设置。
            // WPF 属性清零让位给 hook。
            MinWidth = 0;
            MinHeight = 0;
        // v0.35.0：吞掉 "no browser renderer with ID" 未观察异常（页面切换时残留的 JS 调用打到已销毁 renderer）
        // v0.43.0：全项目未处理异常 / 未观察任务异常统一写入 juni-grid.log
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Services.AppLog.Error("AppDomain", e.ExceptionObject?.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.AppLog.Error("Task", e.Exception?.ToString() ?? "unknown");
            if (e.Exception?.ToString().Contains("no browser renderer") == true)
            {
                e.SetObserved();
                Log("swallowed renderer-ID exception");
            }
        };

            Log("InitializeComponent done");

            // v0.19.0：监听前端 postMessage('ui-ready')，通知 App 层去淡出 Splash + 滑入主窗
            blazorWebView.BlazorWebViewInitialized += (_, args) =>
            {
                try
                {
                    _wv2 = args.WebView;   // v0.2.1：留存引用，最小化时挂起 WebView2 省内存
                    // 未渲染帧的兜底色默认是白色：最小化恢复/可见性切换的瞬间会先闪白再出
                    // 内容（浅色主题下是"白→内容"跳变）。设为 shell 主题色后，
                    // 任何"还没内容"的帧都是界面本来的浅色，恢复全程无色跳。
                    args.WebView.DefaultBackgroundColor =
                        System.Drawing.Color.FromArgb(0xFF, 0xF3, 0xF6, 0xFB);
                    args.WebView.CoreWebView2.WebMessageReceived += (_, e) =>
                    {
                        try
                        {
                            var msg = e.TryGetWebMessageAsString();
                            if (msg == "ui-ready") App.NotifyUiReady();
                        }
                        catch { }
                    };
                    // v1.0.9：WebView2 子进程崩溃自愈 —— 渲染进程挂掉时 Reload 重启它，
                    // 浏览器进程挂掉时记录日志（此时只能整窗重建，先保证不无声死掉）
                    args.WebView.CoreWebView2.ProcessFailed += (_, pf) =>
                    {
                        try
                        {
                            Log($"WebView2 进程失败: kind={pf.ProcessFailedKind}, exitCode={pf.ExitCode}, reason={pf.FailureSourceModulePath}");
                            if (pf.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                                || pf.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                            {
                                Dispatcher.BeginInvoke(() =>
                                {
                                    try { args.WebView.CoreWebView2.Reload(); Log("WebView2 渲染进程已 Reload 恢复"); }
                                    catch (Exception rex) { Log("Reload 恢复失败: " + rex.Message); }
                                });
                            }
                        }
                        catch (Exception pex) { Log("ProcessFailed 处理异常: " + pex.Message); }
                    };
                }
                catch (Exception ex) { Log("WebMessageReceived hook failed: " + ex.Message); }
            };

            // 主窗的出场由 SplashWindow 统一接管，这里不再做 Opacity 淡入淡出。
            // 之前 Loaded 里 Opacity=0+淡入，会被某种第二次 Loaded/切换再次置 0，
            // 导致主窗虽 Visible 却全透明——表现为“主界面不出现、进程却活着”。
            // 去掉那段淡入：主窗默认全不透明显示。

            // 无边框窗口最小化的"影残"修复
            // 最小化到任务栏后，WPF 主窗口虽已收起，但无边框窗口 + WebView2 的
            // 渲染宿主窗口（独立的 Chrome_Widget HWND）不一定会跟着一并从屏幕撤下，
            // 会在桌面层残留一个"不可见的可命中窗口"，把鼠标点击吃掉
            // （现象：最小化后只有桌面/桌面图标点不动，应用/开始/任务栏正常）。
            // 这里在进入 Minimized 时强制把 WebView2 宿主隐藏（不再驻留屏幕），
            // 还原时再恢复可见，杜绝该残留命中区。
            //
            // 恢复闪烁修复（v1.0.9）：此前恢复时 WebView 要延迟 60ms 才显示，
            // 期间露出窗口底色；WebView2 又被 TrySuspendAsync 挂起，Resume 后
            // 渲染器要几百毫秒才产出新帧，未渲染帧按默认白色呈现 ——
            // 黑一闪 → 白一闪 → 内容，就是"一闪一闪"。现在：
            // ① 窗口底色与 WebView2 DefaultBackgroundColor 都 = 浅色主题色；
            // ② 恢复时立即显示 WebView（不再等待）；
            // ③ 只用 MemoryUsageTargetLevel Low/Normal 省内存（官方文档明确
            //    不许与 TrySuspendAsync/Resume 混用），不中断帧呈现 ——
            //    恢复瞬间直接重现最小化前的最后一帧，全程无色跳。
            StateChanged += (_, e2) =>
            {
                var isMin = WindowState == System.Windows.WindowState.Minimized;
                Dispatcher.BeginInvoke(() =>
                {
                    var target = isMin ? Visibility.Collapsed : Visibility.Visible;
                    if (blazorWebView.Visibility == target) return;
                    try { blazorWebView.Visibility = target; }
                    catch (Exception ex) { Log("WebView 可见性同步异常: " + ex.Message); }
                    if (isMin)
                        _ = EnterLowMemoryModeAsync();
                    else
                        ExitLowMemoryMode();
                });
            };

            // ---- 关闭淡出（打开淡入移除，避免 Opacity=0 让主窗透明不可见） ----
            Closing += (_, e) =>
            {
                if (_closing) return;
                _closing = true;
                e.Cancel = true;
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(
                    Opacity, 0, new Duration(TimeSpan.FromMilliseconds(180)))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                fadeOut.Completed += (_, _) => Close();
                BeginAnimation(OpacityProperty, fadeOut);
            };

            // 如果这次启动本身就是被 nxm:// 链接拉起的，现在 DI 好了，交给安装服务
            if (App.PendingNxmLink is { } pending)
            {
                App.PendingNxmLink = null;
                _ = provider.GetRequiredService<InstallService>().HandleNxmLinkAsync(pending);
            }

            if (!File.Exists(wwwrootPath))
            {
                System.Windows.MessageBox.Show(
                    $"关键文件缺失！\n\nwwwroot/index.html 没有被打包到:\n{wwwrootPath}\n\n" +
                    "这就是白屏的原因。请检查 csproj 是否正确包含 wwwroot 文件夹。",
                    "JuniGrid 诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log($"CRASH: {ex}");
            System.Windows.MessageBox.Show(
                $"JuniGrid 初始化失败\n\n{ex.Message}\n\n完整日志: {LogPath}",
                "启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // Win32 窗口状态命令（对无边框窗口最小化/还原最可靠）。
    // 无边框（WindowStyle=None + CaptionHeight=0）时 WindowState.Minimized
    // 在部分系统上不会真正把窗口从屏幕撤掉，会残留一个透明交互窗口，
    // 把下面的桌面鼠标点拦截（最小化后原区域点不动）。用 ShowWindow
    // 强制系统级最小化/还原，会连同 WebView 子窗口一起正确处理。
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", PreserveSig = true, SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Log("OnSourceInitialized (WPF window HWND created)");

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        Log("DWM rounded corners applied");

        // v1.1.5：启动默认尺寸 = 所在显示器的 77.1% × 72.7%（响应式）。
        // 在 2560×1600 的屏上即 1974×1163 物理像素；DIP 值按 DPI 缩放换算，
        // 其他用户千奇百怪的显示器/缩放比例自动适配。XAML 里的 1600×1000 只是兜底。
        // v1.1.8：尺寸算好后同屏同源算出居中坐标【存起来】——此时窗口还挂在屏外
        // (-32000)，不能直接挪回，否则 Splash 播完前主窗就露出来了；现身时由
        // RevealAtStartupPosition 应用。旧流程在 App.RevealMain 里按创建时的
        // XAML 尺寸(1600×1000)预算位置，尺寸后来变了位置没跟着算 → 打开偏离中心。
        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                var dpiT = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice;
                var monW = (double)(mi.rcMonitor.Right - mi.rcMonitor.Left);
                var monH = (double)(mi.rcMonitor.Bottom - mi.rcMonitor.Top);
                Width = Math.Max(MinWidth, monW * 0.7711 / dpiT.M11);
                Height = Math.Max(MinHeight, monH * 0.7269 / dpiT.M22);
                // 工作区物理像素 → DIP：WPF 应用 Left/Top 时按窗口当前 DPI 换算回物理，
                // 这里取其逆变换，单屏与同缩放多屏下精确居中
                _startupLeft = mi.rcWork.Left / dpiT.M11
                               + ((mi.rcWork.Right - mi.rcWork.Left) / dpiT.M11 - Width) / 2;
                _startupTop = mi.rcWork.Top / dpiT.M22
                              + ((mi.rcWork.Bottom - mi.rcWork.Top) / dpiT.M22 - Height) / 2;
                _startupCentered = true;
                Log($"startup size = {Width:0}x{Height:0} DIP (monitor {monW:0}x{monH:0} px @ {dpiT.M11:0.00}), centered target ({_startupLeft:0},{_startupTop:0})");
            }
        }
        catch { }

        // 无边框窗口最大化时会超出工作区（约 8px，被系统裁掉），
        // 导致 WebView 底部内容（滚动到底的那几行）被切、滚不完全。
        // 拦截 WM_GETMINMAXINFO，把最大尺寸/位置限制在系统工作区（避开任务栏）。
        var src = HwndSource.FromHwnd(hwnd);
        src?.AddHook(WndProcClampMaximized);
        // v1.1.8：已最大化时跨屏拖动 → DPI 变更，最大化几何需要按新屏重算
        DpiChanged += (_, _) =>
        {
            if (WindowState == System.Windows.WindowState.Maximized)
                VerifyMaximizedPlacement();
        };
        // v1.1.2b：窗口尺寸检查点 —— 到达 1974×1383PX / 1536×864PX 时记录窗口状态
        SizeChanged += OnWindowSizeChanged;
    }

    // ─── v1.1.8：启动现身位置 ───
    // OnSourceInitialized 在窗口还挂在屏外时按【所在显示器】算好的居中坐标（DIP）。
    // 旧流程在 App.RevealMain 里用创建时的 XAML 尺寸（1600×1000）预算位置，而窗口
    // 随后被改成显示器 77.1%×72.7%，尺寸变了位置没跟着算 → 打开整体偏离中心。
    private double _startupLeft, _startupTop;
    private bool _startupCentered;

    /// <summary>App 启动流程现身主窗时调用：挪到所在显示器工作区的正中。</summary>
    public void RevealAtStartupPosition()
    {
        if (_startupCentered)
        {
            Left = _startupLeft;
            Top = _startupTop;
            return;
        }
        // 兜底：按屏居中没算成（GetMonitorInfo 失败等异常路径）—— 用主屏工作区 + 当前实际尺寸
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + (wa.Height - ActualHeight) / 2;
    }

    // 把最大化的范围锁定到工作区，消除无边框最大化的底部越界裁切。
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private IntPtr WndProcClampMaximized(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        // 多显示器：用窗口当前所在屏的工作区（避开任务栏）。
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;
        var wa = mi.rcWork;
        var mm = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        // v1.1.8b：ptMaxPosition 的语义是【相对显示器原点的偏移】，不是绝对坐标！
        // （DefWindowProc 会把它再加到显示器原点上。）此前写绝对值 wa.Left/Top：
        // 主屏原点 (0,0) 时绝对==相对碰巧正确；任何副屏上最大化都落在
        // 「原点×2」（平板@(2560,0) 实测 (5120,0)，正好屏外一个屏宽 →「最大化后消失」）。
        // 兜底随后 SetWindowPos 摆正，又会让 Windows 把最大化当手动调整、WPF 同步回
        // Normal → 触发还原逻辑，「点了最大化又缩回去」。改为相对偏移后一次落位正确。
        mm.ptMaxPosition = new POINT32(wa.Left - mi.rcMonitor.Left, wa.Top - mi.rcMonitor.Top);
        mm.ptMaxSize = new POINT32(wa.Right - wa.Left, wa.Bottom - wa.Top);
        mm.ptMaxTrackSize = new POINT32(wa.Right - wa.Left, wa.Bottom - wa.Top);
        // v1.1.5：任务栏【自动隐藏】时 rcWork == 整个显示器，最大化窗口恰好盖满全屏，
        // Windows 会抑制自动隐藏任务栏的边缘唤出（鼠标压底边无效）。此处把最大化
        // 高度减 1 物理像素 —— 窗口不再"恰好盖满全屏"，边缘唤出立即恢复，而这 1px
        // 在视觉上不可见。可见任务栏时 rcWork 本就挖掉了任务栏，不受影响。
        if (AutoHideBottomBarHeight(mi.rcMonitor) is int barH && barH > 0)
        {
            var maxH = Math.Max(0, wa.Bottom - wa.Top - 1);
            mm.ptMaxSize = new POINT32(wa.Right - wa.Left, maxH);
            mm.ptMaxTrackSize = new POINT32(wa.Right - wa.Left, maxH);
        }
        // v1.1.2：拖拽最小尺寸强制 —— hook 里的一切坐标都是物理像素，不要再乘 DPI。
        // 更早版本则根本没设此项，窗口能拖到几百像素宽。
        // v1.1.8b：最小尺寸随显示器 —— 所在显示器分辨率的 60% × 54%。
        // 校准基准（用户确认的设计规定值）：2560×1600 的屏上 = 1536×864PX。
        // 大屏最小值等比放大、小屏等比缩小；每条 WM_GETMINMAXINFO 都按窗口当前
        // 所在屏重算，跨屏拖动自动跟随，无需 DPI 换算。
        try
        {
            var (minW, minH) = MonitorMinTrackSize(mi.rcMonitor);
            mm.ptMinTrackSize = new POINT32(minW, minH);
        }
        catch { }
        Marshal.StructureToPtr(mm, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    // v1.1.8b：本屏拖拽最小尺寸（物理像素）—— 所在显示器分辨率的 60% × 54%。
    // 校准基准（用户确认）：2560×1600 屏 = 1536×864PX。注意基数是【整块显示器】
    // 而非工作区，否则有任务栏的屏上高会比无任务栏的屏小一截。
    // WndProcClampMaximized 与尺寸检查点日志共用，保证两处口径永远一致。
    private static (int W, int H) MonitorMinTrackSize(RECT32 monitorRect) =>
        ((int)((monitorRect.Right - monitorRect.Left) * 60 / 100),
         (int)((monitorRect.Bottom - monitorRect.Top) * 54 / 100));



    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT32 { public int X, Y; public POINT32(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT32 rcMonitor;
        public RECT32 rcWork;
        public uint dwFlags;
    }

    // ─── v1.1.5：自动隐藏任务栏检测（最大化 WebView2 底部避让用）───
    private const int ABM_GETAUTOHIDEBAR = 0x0007;
    private const int ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT32 rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT32 lpRect);

    /// <summary>查询指定显示器底部是否有【自动隐藏】任务栏，返回它的厚度（像素）；
    /// 没有返回 0；查询异常返回 null（调用方按无自动隐藏处理）。</summary>
    private static int? AutoHideBottomBarHeight(RECT32 monitorRect)
    {
        try
        {
            var abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                uEdge = (uint)ABE_BOTTOM,
                rc = monitorRect,
            };
            var hBar = SHAppBarMessage(ABM_GETAUTOHIDEBAR, ref abd);
            if (hBar == IntPtr.Zero) return 0;   // 本屏底部没有自动隐藏任务栏
            if (GetWindowRect(hBar, out var r))
                return Math.Max(0, r.Bottom - r.Top);
            return null;
        }
        catch { return null; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT32 ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ==================================================================
    // 内置 Nexus 浏览器（主窗口覆盖层）
    // ==================================================================
    private bool _closing;

    // v0.2.1：最小化省内存 —— WebView2 是常驻内存大头（渲染整个 UI 的 Chromium 多进程）。
    // v1.0.9：只切 MemoryUsageTargetLevel Low/Normal（官方给后台窗口的省内存姿态，
    // 文档明确要求与 TrySuspendAsync/Resume 二选一、不得混用）。不再挂起 WebView2：
    // 挂起会停掉帧呈现，恢复时渲染器唤醒要几百毫秒，是"最小化恢复一闪一闪"的主因之一；
    // Low 档同样会把浏览器进程内存大量换出磁盘，且不中断呈现，恢复即显最后一帧。
    // 宿主自身的工作集仍一并换出。
    private Microsoft.Web.WebView2.Wpf.WebView2CompositionControl? _wv2;

    private async Task EnterLowMemoryModeAsync()
    {
        // 注意：CoreWebView2 的 getter 在 WebView2 尚未初始化或浏览器进程已崩溃时
        // 会直接抛异常，必须整体包进 try/catch，否则最小化/还原一瞬间就炸掉 UI 线程
        try
        {
            var core = _wv2?.CoreWebView2;
            if (core is not null)
            {
                try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }
                catch { /* 旧 WebView2 运行时不支持该属性，跳过 */ }
            }
        }
        catch (Exception ex) { Log("EnterLowMemoryMode 跳过: " + ex.Message); }
        try { Services.MemoryService.TrimWorkingSet(); } catch { }
    }

    private void ExitLowMemoryMode()
    {
        try
        {
            var core = _wv2?.CoreWebView2;
            if (core is null) return;
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; }
            catch { /* 同上 */ }
        }
        catch (Exception ex) { Log("ExitLowMemoryMode 跳过: " + ex.Message); }
    }

    // ══════════════════════════════════════════════════════════════
    // v1.1.2：主题切换圆形揭示（CapturePreview 快照 + WPF 挖洞动画）
    // View Transitions 的快照层在 WebView2 合成渲染路径下偶发「整层空白/
    // 渲染器停摆」，无法根治 —— 改用完全普通的绘制路径：
    //   1) CoreWebView2.CapturePreviewAsync 截当前（旧主题）页面；
    //   2) 截图铺在 WebView 上方的覆盖层 Image 里（盖住页面）；
    //   3) 通知 JS 立即（无动画）切到新主题；
    //   4) 覆盖层 OpacityMask 从开关位置挖一个不断变大的圆洞露出新主题；
    //   5) 动画结束摘除覆盖层。两个方向同一段代码，对称且不会白屏。
    // ══════════════════════════════════════════════════════════════
    private static bool _themeRevealing;

    /// <summary>圆形揭示动画时长。</summary>
    public static int ThemeRevealMs = 400;

    /// <summary>由 TitleBar 调用：从 (cssX, cssY)（CSS 像素 = WPF DIP）开始圆形揭示到新主题。</summary>
    public static async Task RevealThemeSwitchAsync(
        double cssX, double cssY, string nextTheme, Func<string, Task> applyThemeJs)
    {
        var app = System.Windows.Application.Current;
        var w = app?.MainWindow as MainWindow;
        if (w is null || MainWindow._themeRevealing)
        {
            await applyThemeJs(nextTheme);   // 正在揭示/无窗口：直接瞬时切换
            return;
        }
        MainWindow._themeRevealing = true;
        try
        {
            var core = w._wv2?.CoreWebView2;
            var overlay = w.ThemeRevealOverlay;
            if (core is null || overlay is null)
            {
                await applyThemeJs(nextTheme);   // 兜底：无法截图时瞬时切换
                return;
            }

            // 1. 截当前（旧主题）页面
            using var ms = new System.IO.MemoryStream();
            await core.CapturePreviewAsync(
                Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();   // 跨线程可用

            // 2. 覆盖层铺旧画面（盖住页面）
            overlay.Source = bmp;
            overlay.Visibility = Visibility.Visible;

            // 3. 页面立即（无动画）切到新主题，并让宿主底色跟随
            await applyThemeJs(nextTheme);
            ApplyShellTheme(nextTheme == "dark");
            await Task.Delay(60);   // 给新主题至少一帧绘制时间

            // 4. OpacityMask 从开关位置挖圆洞（洞内透明露出新主题，洞外不透明旧画面）
            double wd = overlay.ActualWidth, ht = overlay.ActualHeight;
            if (wd < 1 || ht < 1)
            {
                await applyThemeJs(nextTheme);
                return;
            }
            double endR = Math.Sqrt(
                Math.Pow(Math.Max(cssX, wd - cssX), 2) +
                Math.Pow(Math.Max(cssY, ht - cssY), 2));
            var brush = new System.Windows.Media.RadialGradientBrush
            {
                MappingMode = System.Windows.Media.BrushMappingMode.Absolute,
                Center = new System.Windows.Point(cssX, cssY),
                GradientOrigin = new System.Windows.Point(cssX, cssY)
            };
            System.Windows.Media.Animation.DoubleAnimation anim;
            if (nextTheme == "dark")
            {
                // 明变暗：旧浅色截图【只显示在以开关为圆心的收缩圆内】（圆外透明，
                // 露出已翻转的真实暗色页面）—— 圆半径从全屏收缩到 0，
                // 白色随圆缩回开关，四周先入夜，与官网「浅色向按钮收回」一致
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Black, 0.0));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Black, 0.985));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Transparent, 1.0));
                brush.RadiusX = endR;
                brush.RadiusY = endR;
                overlay.OpacityMask = brush;
                anim = new System.Windows.Media.Animation.DoubleAnimation(
                    endR, 0.001, TimeSpan.FromMilliseconds(ThemeRevealMs))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                    }
                };
            }
            else
            {
                // 变亮：旧暗色截图铺满，圆洞从开关扩大露出新浅色页面
                //（浅色从按钮处向四周发散）
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Transparent, 0.0));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Transparent, 0.985));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Black, 1.0));
                brush.RadiusX = 0.001;
                brush.RadiusY = 0.001;
                overlay.OpacityMask = brush;
                anim = new System.Windows.Media.Animation.DoubleAnimation(
                    0.001, endR, TimeSpan.FromMilliseconds(ThemeRevealMs))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                    }
                };
            }
            var tcs = new TaskCompletionSource();
            anim.Completed += (_, _) => tcs.TrySetResult();
            brush.BeginAnimation(System.Windows.Media.RadialGradientBrush.RadiusXProperty, anim);
            brush.BeginAnimation(System.Windows.Media.RadialGradientBrush.RadiusYProperty, anim);
            await tcs.Task;

            // 5. 清理
            overlay.OpacityMask = null;
            overlay.Source = null;
            overlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log("主题圆形揭示失败，回退为瞬时切换: " + ex.Message);
            try { await applyThemeJs(nextTheme); } catch { }
        }
        finally
        {
            var w2 = app?.MainWindow as MainWindow;
            if (w2 is not null)
            {
                MainWindow._themeRevealing = false;
                var o = w2.ThemeRevealOverlay;
                if (o is not null)
                {
                    o.OpacityMask = null;
                    o.Source = null;
                    o.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    /// <summary>Blazor 页面调这里：在主窗口内打开 Nexus 浏览覆盖层。</summary>
    public static void OpenNexusOverlay(string url, bool queueMode = false)
    {
        var w = System.Windows.Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        _ = queueMode; // 内置浏览器已移除：统一跳系统浏览器
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("打开系统浏览器失败: " + ex.Message); }
    }

    /// <summary>路由离开「Mod 管理」时收起覆盖层（保留 WebView2 实例避免重新初始化）。</summary>
    public static void HideNexusOverlay()
    {
        var w = System.Windows.Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        w.Dispatcher.Invoke(() =>
        {
            {
                    App.Services?.GetService<UpdateQueueService>()?.Stop();
            }
        });
    }

    private void Toolbar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    // ─── v1.1.5/v1.1.8：窗口尺寸策略 ───
    // 启动即窗口化、按所在屏 77.1%×72.7% 居中现身（见 OnSourceInitialized/RevealAtStartupPosition）；
    // 拖拽最小尺寸按所在显示器 60%×54%（2560×1600 屏 = 1536×864PX，见 WndProcClampMaximized）。
    // 从最大化点「还原」时恢复到所在屏工作区的 77.1%×72.6%，不用系统 RestoreBounds
    // 里记的旧尺寸（那可能是很久之前随手拖出来的小窗，还原出来突兀）。
    private bool _wasMaximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == System.Windows.WindowState.Maximized)
        {
            _wasMaximized = true;
            VerifyMaximizedPlacement();
            return;
        }
        if (WindowState == System.Windows.WindowState.Normal && _wasMaximized)
        {
            _wasMaximized = false;
            // 还原动画期间直接改尺寸会被状态机打回，调度到本布局拍之后执行
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // v1.1.2c：还原尺寸（开源通用）—— 按【工作区比例】锁定，而非固定物理像素。
                // 校准基准：2560×1528 工作区上为 1974×1110PX（16:9），即宽 77.1%、高 72.6%。
                // 任何分辨率/缩放下都占工作区相同比例：作者屏上精确 1974×1110PX；
                // 1080p 小屏自动等比缩小不裁剪；4K 大屏等比放大保持观感一致（主流软件行为）。
                // v1.1.8：工作区取自【窗口实际所在屏】（MonitorFromWindow + GetMonitorInfo，
                // 物理像素，无需 DPI 换算）—— 原用 SystemParameters.WorkArea 只描述主屏，
                // 副屏上最大化后还原会按主屏算尺寸（偏小）、越界判断还会把窗口拽回主屏。
                var hwnd = new WindowInteropHelper(this).Handle;
                var dpi = hwnd != IntPtr.Zero ? (int)GetDpiForWindow(hwnd) : 96;
                if (dpi <= 0) dpi = 96;
                var scale = dpi / 96.0;
                double waPhysW, waPhysH, waLeft, waTop;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (hwnd != IntPtr.Zero && GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
                {
                    waPhysW = mi.rcWork.Right - mi.rcWork.Left;
                    waPhysH = mi.rcWork.Bottom - mi.rcWork.Top;
                    waLeft = mi.rcWork.Left;
                    waTop = mi.rcWork.Top;
                }
                else
                {
                    // 兜底：取不到所在屏（理论不发生）—— 退回主屏工作区
                    var wa = SystemParameters.WorkArea;
                    waPhysW = wa.Width * scale;
                    waPhysH = wa.Height * scale;
                    waLeft = wa.Left * scale;
                    waTop = wa.Top * scale;
                }

                var physW = Math.Min(waPhysW - 16, Math.Max(400.0, waPhysW * (1974.0 / 2560.0)));
                var physH = Math.Min(waPhysH - 16, Math.Max(300.0, waPhysH * (1110.0 / 1528.0)));

                Width = Math.Max(MinWidth, Math.Round(physW / scale));
                Height = Math.Max(MinHeight, Math.Round(physH / scale));
                _restoreTargetPhysW = physW;   // 供尺寸检查点比对（本屏的还原期望尺寸）
                _restoreTargetPhysH = physH;

                // 还原位置越界兜底（同一块屏的工作区，物理像素）—— Normal 位置若还停在
                // 屏外挂载的 -32000 附近（旧版本启动时留下的），还原后窗口整个在屏幕外，
                // 表现为"窗口消失"。
                var leftPhys = Left * scale;
                var topPhys = Top * scale;
                if (leftPhys < waLeft - 100 || leftPhys + Width * scale > waLeft + waPhysW + 100
                    || topPhys < waTop - 100 || topPhys + Height * scale > waTop + waPhysH + 100)
                {
                    Left = Math.Round((waLeft + (waPhysW - Width * scale) / 2) / scale);
                    Top = Math.Round((waTop + (waPhysH - Height * scale) / 2) / scale);
                }
                Log($"[还原] 所在屏工作区 {waPhysW:F0}×{waPhysH:F0}PX 的 77.1%×72.6%（={physW:F0}×{physH:F0}PX）→ 实际 " +
                    $"Width={Width:F0} Height={Height:F0} DIP = {Width * scale:F0}×{Height * scale:F0}PX (dpi={dpi}, scale={scale:0.##})");
                // v1.1.2c：检查点必然记录（SizeChanged 版本可能因布局时序漏触发）
                Log($"[尺寸检查点] ★ 到达还原标准尺寸（本屏期望 {physW:F0}×{physH:F0}PX，" +
                    $"实际 {Width * scale:F0}×{Height * scale:F0}PX，Width={Width:F0} Height={Height:F0} DIP，" +
                    $"dpi={dpi}，WindowState={WindowState}，Left={Left:F0} Top={Top:F0}）");
            }));
        }
    }

    // 本屏还原期望尺寸（物理像素），由还原逻辑写入，供尺寸检查点比对
    private double _restoreTargetPhysW, _restoreTargetPhysH;

    // ─── v1.1.8：最大化落位保险丝 ───
    // 两层问题、一道兜底：
    // ① 无 manifest 时进程是 System DPI 感知，副屏坐标被系统虚拟化，最大化直接
    //    落到屏外整整一个屏宽（实测 (5120,0)）→ app.manifest 切 PerMonitorV2 根治；
    // ② PMv2 下跨屏（拖到 DPI 不同的副屏立刻最大化）存在 WM_DPICHANGED 与
    //    SC_MAXIMIZE 的竞态：最大化消息按旧 DPI 环境处理，几何被按新旧 DPI 比例
    //    缩放（实测落 (1707,0) 尺寸÷1.5，卡在主屏右缘）。
    // 兜底：最大化落定后若几何 ≠ 本屏工作区，强制摆正。双段检查覆盖竞态：
    // 布局拍后一次 + 120ms 后（DPI 变更落定）复查一次。
    private void VerifyMaximizedPlacement()
    {
        Dispatcher.BeginInvoke(() => CheckMaximizedGeometry());
        var t = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => { t.Stop(); CheckMaximizedGeometry(); };
        t.Start();
    }

    private void CheckMaximizedGeometry()
    {
        try
        {
            if (WindowState != System.Windows.WindowState.Maximized) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi)) return;
            // 目标几何与 WM_GETMINMAXINFO hook 完全同口径：工作区 + 自动隐藏任务栏 1px 让位
            var barH = AutoHideBottomBarHeight(mi.rcMonitor);
            var targetW = mi.rcWork.Right - mi.rcWork.Left;
            var targetH = mi.rcWork.Bottom - mi.rcWork.Top - (barH > 0 ? 1 : 0);
            var off = Math.Abs(r.Left - mi.rcWork.Left) > 2 || Math.Abs(r.Top - mi.rcWork.Top) > 2
                   || Math.Abs(r.Right - r.Left - targetW) > 2 || Math.Abs(r.Bottom - r.Top - targetH) > 2;
            if (!off) return;
            Log($"[最大化兜底] 窗口 ({r.Left},{r.Top})-({r.Right},{r.Bottom}) ≠ 所在屏工作区 " +
                $"({mi.rcWork.Left},{mi.rcWork.Top}) {targetW}×{targetH} → 强制摆正");
            SetWindowPos(hwnd, IntPtr.Zero,
                mi.rcWork.Left, mi.rcWork.Top, targetW, targetH,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log("最大化落位检查失败: " + ex.Message); }
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    // v1.1.2b：用户要求 —— 窗口到达规定尺寸时记录状态。
    // 还原尺寸检查点 = 本屏还原期望值（2560×1528 参考屏上即 1974×1110PX）；
    // 最小尺寸检查点 = 本屏最小尺寸（工作区 43%×42.5%，与拖拽下限同口径）。
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var dpi = (int)GetDpiForWindow(hwnd);
            if (dpi <= 0) return;
            var scale = dpi / 96.0;
            var pw = ActualWidth * scale;
            var ph = ActualHeight * scale;
            if (_restoreTargetPhysW > 0 && Math.Abs(pw - _restoreTargetPhysW) < 4 && Math.Abs(ph - _restoreTargetPhysH) < 4)
                Log($"[尺寸检查点] ★ 到达还原标准尺寸（本屏期望 {_restoreTargetPhysW:F0}×{_restoreTargetPhysH:F0}PX，" +
                    $"实际 {pw:F0}×{ph:F0}PX，Width={ActualWidth:F0} Height={ActualHeight:F0} DIP，dpi={dpi}，WindowState={WindowState}，Left={Left:F0} Top={Top:F0}）" +
                    (_restoreTargetPhysW < 1970 ? " ≈ 1974×1110PX 基准" : ""));
            else
            {
                // v1.1.8b：最小尺寸检查点 —— 与 WndProcClampMaximized 同口径，按窗口所在
                // 显示器 60%×54% 计算。
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
                {
                    var (minW, minH) = MonitorMinTrackSize(mi.rcMonitor);
                    if (Math.Abs(pw - minW) < 4 && Math.Abs(ph - minH) < 4)
                        Log($"[尺寸检查点] ★ 到达本屏最小尺寸 {minW}×{minH}PX（实际 {pw:F0}×{ph:F0}PX，" +
                            $"Width={ActualWidth:F0} Height={ActualHeight:F0} DIP，dpi={dpi}，WindowState={WindowState}，Left={Left:F0} Top={Top:F0}）");
                }
            }
        }
        catch { }
    }

    /// <summary>用 Win32 ShowWindow 强制作最小化，确保无边框窗口真正从屏幕撤出，避免残留透明交互窗拦截鼠标。</summary>
    public static void MinimizeWindow()
    {
        var w = System.Windows.Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_MINIMIZE);
        else w.WindowState = System.Windows.WindowState.Minimized;
    }

    // ─── v1.1.1：深浅主题 —— 窗口底色与 WebView2 兜底帧色跟随前端 data-theme ───
    // 圆角外壳外的四角露出的是 WPF 窗口底色；WebView2 未出帧的瞬间显示 DefaultBackgroundColor。
    // 两者必须与前端 shell 底色一致，否则深色主题下四角/恢复瞬间会闪浅色。
    private static readonly System.Windows.Media.Color ThemeColorLight =
        System.Windows.Media.Color.FromArgb(0xFF, 0xF3, 0xF6, 0xFB);
    private static readonly System.Windows.Media.Color ThemeColorDark =
        System.Windows.Media.Color.FromArgb(0xFF, 0x1C, 0x1E, 0x23);

    /// <summary>前端切换主题后调用（TitleBar）：同步 WPF 窗口底色 + WebView2 兜底帧色。</summary>
    public static void ApplyShellTheme(bool dark)
    {
        var app = System.Windows.Application.Current;
        var w = app?.MainWindow as MainWindow;
        if (w is null) return;
        w.Dispatcher.Invoke(() =>
        {
            var color = dark ? ThemeColorDark : ThemeColorLight;
            w.Background = new System.Windows.Media.SolidColorBrush(color);
            try
            {
                if (w._wv2 is not null)
                    w._wv2.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
            }
            catch { }
        });
    }

    /// <summary>
    /// 用 Win32 强制显示主窗口。WPF 的 Visibility=Visible 对已 Show()/Hidden 过的
    /// 窗口不一定触发 HWND SW_SHOW（导致窗口 visible=False、主界面不出现）。
    /// 这里直接对 HWND 发 ShowWindow(SW_SHOW)，绕开该情况，保证系统真正显示。
    /// </summary>
    public static void ShowMainWindow()
    {
        var w = Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        // 先让 WPF 的状态机认为窗口可见——否则 ShowWindow 一下会被 WPF 的
        // layout pass 当成「仍 Hidden」而撤销（之前一直 visible=False 的根源）。
        w.Visibility = Visibility.Visible;
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Log($"ShowMainWindow: Visibility={w.Visibility} hwnd=0x{hwnd.ToInt64():X}");
        bool r = ShowWindow(hwnd, SW_SHOW);
        Log($"ShowMainWindow: SW_SHOW={r} IsWindowVisible={IsWindowVisible(hwnd)} style=0x{GetWindowLong(hwnd, GWL_STYLE) & (WS_VISIBLE | WS_MINIMIZE):X}");
        w.Activate();
    }
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    private const int GWL_STYLE = -16, WS_VISIBLE = 0x10000000, WS_MINIMIZE = 0x20000000;

    private void QueueSkip_Click(object sender, RoutedEventArgs e) =>
        App.Services?.GetService<UpdateQueueService>()?.Skip();

}
