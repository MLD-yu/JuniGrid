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
            // v1.1.2b：最小尺寸完全由 WM_GETMINMAXINFO hook 按【物理像素】1536×864 强制
            //（用户设计规定值；hook 内坐标即物理像素，直接生效）。
            // 必须放在 InitializeComponent 之后 —— XAML 里的 MinWidth/MinHeight(1536/864 DIP)
            // 会在高 DPI 下换算成更大的物理值把窗口二次拉大，覆盖这里清零前的设置。
            // WPF 属性清零让位给 hook，拖拽下限 = 1536×864PX 精确不放大。
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

        // 无边框窗口最大化时会超出工作区（约 8px，被系统裁掉），
        // 导致 WebView 底部内容（滚动到底的那几行）被切、滚不完全。
        // 拦截 WM_GETMINMAXINFO，把最大尺寸/位置限制在系统工作区（避开任务栏）。
        var src = HwndSource.FromHwnd(hwnd);
        src?.AddHook(WndProcClampMaximized);
        // v1.1.2b：窗口尺寸检查点 —— 到达 1974×1383PX / 1536×864PX 时记录窗口状态
        SizeChanged += OnWindowSizeChanged;
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
        mm.ptMaxPosition = new POINT32(wa.Left, wa.Top);
        mm.ptMaxSize = new POINT32(wa.Right - wa.Left, wa.Bottom - wa.Top);
        mm.ptMaxTrackSize = new POINT32(wa.Right - wa.Left, wa.Bottom - wa.Top);
        // v1.1.2：拖拽最小尺寸强制 —— 设计值 1536×864 指的是【物理像素】，直接按设备像素写入
        // （hook 里的一切坐标都是物理像素，不要再乘 DPI）。此前误乘 DPI 导致最小值被放大、
        // 用户永远拖不到规定的 1536×864；更早版本则根本没设此项，窗口能拖到几百像素宽。
        // 小屏兜底：最小值不超过本屏工作区，否则小屏上窗口永远缩不小。
        try
        {
            var minW = (int)Math.Min(1536, wa.Right - wa.Left);
            var minH = (int)Math.Min(864, wa.Bottom - wa.Top);
            mm.ptMinTrackSize = new POINT32(minW, minH);
        }
        catch { }
        Marshal.StructureToPtr(mm, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

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

    // ─── v1.1.7：窗口尺寸策略：启动即最大化（XAML WindowState）+ 最小 1536×864 ───
    // 从最大化点「还原」时固定恢复到 1974×1383，不用系统 RestoreBounds 里记的旧尺寸
    // （那可能是很久之前随手拖出来的小窗，还原出来突兀）。
    private bool _wasMaximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == System.Windows.WindowState.Maximized) { _wasMaximized = true; return; }
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
                var hwnd = new WindowInteropHelper(this).Handle;
                var dpi = hwnd != IntPtr.Zero ? (int)GetDpiForWindow(hwnd) : 96;
                if (dpi <= 0) dpi = 96;
                var scale = dpi / 96.0;
                var wa = SystemParameters.WorkArea;
                var waPhysW = wa.Width * scale;     // 工作区物理像素
                var waPhysH = wa.Height * scale;

                var physW = Math.Min(waPhysW - 16, Math.Max(400.0, waPhysW * (1974.0 / 2560.0)));
                var physH = Math.Min(waPhysH - 16, Math.Max(300.0, waPhysH * (1110.0 / 1528.0)));

                Width = Math.Max(MinWidth, Math.Round(physW / scale));
                Height = Math.Max(MinHeight, Math.Round(physH / scale));
                _restoreTargetPhysW = physW;   // 供尺寸检查点比对（本屏的还原期望尺寸）
                _restoreTargetPhysH = physH;

                // 还原位置越界兜底 —— Normal 位置若还停在屏外挂载的 -32000 附近
                //（旧版本启动时留下的），还原后窗口整个在屏幕外，表现为"窗口消失"。
                if (Left < wa.Left - 100 || Left + Width > wa.Right + 100
                    || Top < wa.Top - 100 || Top + Height > wa.Bottom + 100)
                {
                    Left = Math.Max(wa.Left, (wa.Width - Width) / 2 + wa.Left);
                    Top = Math.Max(wa.Top, (wa.Height - Height) / 2 + wa.Top);
                }
                Log($"[还原] 目标工作区 77.1%×72.6%（=本屏 {physW:F0}×{physH:F0}PX）→ 实际 " +
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

    // v1.1.2b：用户要求 —— 窗口到达规定尺寸时记录状态。
    // 还原尺寸检查点 = 本屏还原期望值（2560×1528 参考屏上即 1974×1110PX）；
    // 最小尺寸检查点 = 固定 1536×864PX。
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
            else if (Math.Abs(pw - 1536) < 4 && Math.Abs(ph - 864) < 4)
                Log($"[尺寸检查点] ★ 到达最小标准尺寸 1536×864PX（实际 {pw:F0}×{ph:F0}PX，" +
                    $"Width={ActualWidth:F0} Height={ActualHeight:F0} DIP，dpi={dpi}，WindowState={WindowState}，Left={Left:F0} Top={Top:F0}）");
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
