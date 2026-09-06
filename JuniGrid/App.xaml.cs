using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JuniGrid.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JuniGrid;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid", "crash.log");

    // ---- 单实例 + nxm:// 转发 ----
    // 用户在 Nexus 网页点「Mod Manager Download」时，Windows 会用
    // nxm:// 链接拉起 JuniGrid.exe。如果已有实例在跑，第二实例通过
    // 命名管道把链接递给主实例，然后自己退出。
    private const string MutexName = "JuniGrid.SingleInstance";
    private const string PipeName = "JuniGrid.NxmPipe";
    /// <summary>二次启动经管道发给主实例的激活指令。</summary>
    private const string ActivateCommand = "jg:activate";
    private static Mutex? _mutex;

    /// <summary>DI container, set by MainWindow right after BuildServiceProvider.</summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>An nxm:// link that arrived before the DI container was ready.</summary>
    public static string? PendingNxmLink { get; set; }

    private static bool _uninstallMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 卸载模式（JuniGrid.exe --uninstall 或安装目录里的独立 Uninstall.exe）：
        // 跳过单实例/管道/splash，只显示卸载向导。
        // 必须在 mutex 之前分流——主实例在跑时控制面板也要能拉起卸载器。
        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "";
        _uninstallMode = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
                         || exeName.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase);
        if (_uninstallMode)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            return;
        }

        var nxmArg = e.Args.FirstOrDefault(
            a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            if (nxmArg is not null)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(2000);
                    using var w = new StreamWriter(client) { AutoFlush = true };
                    w.WriteLine(nxmArg);
                }
                catch { /* main instance unreachable — just exit */ }
                Shutdown();
                return;
            }

            // 不带 nxm 链接的二次启动 = 用户又点了一次快捷方式。标准做法是把
            // 已有实例的窗口激活置前（任务栏高亮、可立即操作），本实例直接退出。
            // 只有联系不上主实例（如升级装完后旧实例占锁、管道无响应）才走旧的
            // 「结束其它实例后接管」兜底，避免「点了没反应、开出来的还是旧版」。
            if (TryActivateExistingInstance())
            {
                Shutdown();
                return;
            }
            if (!TryTakeOverSingleInstance())
            {
                Shutdown();
                return;
            }
        }

        // Catch EVERYTHING — UI thread, background threads, unobserved tasks.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        StartPipeServer();
        PendingNxmLink = nxmArg;

        base.OnStartup(e);
    }

    /// <summary>单实例锁被占时（无 nxm 转发场景）：结束其它 JuniGrid 实例并等锁释放。
    /// 注意只按进程名 "JuniGrid" 匹配 —— 安装器是 JuniGridSetup、卸载向导是
    /// Uninstall.exe，进程名都不同，不会误伤。返回 false = 5 秒内仍拿不到锁
    /// （如旧实例提权运行无法终止），调用方放弃启动。</summary>
    private static bool TryTakeOverSingleInstance()
    {
        try
        {
            var self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("JuniGrid"))
            {
                if (p.Id == self) { p.Dispose(); continue; }
                try { p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }
        catch { }

        // 持有者被杀后锁被废弃，WaitOne 抛 AbandonedMutexException 时其实已拿到所有权
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (_mutex!.WaitOne(TimeSpan.FromMilliseconds(250))) return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Startup 事件占位 —— 真正的 splash → main 编排放在这里。</summary>
    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        // v1.1.2：WebView2 运行时前置检测 —— 正常 Win10/11 预装，但 Windows 沙盒、
        // LTSC/精简系统可能没有。缺失时裸异常是一屏英文堆栈（界面永远出不来），
        // 这里弹中文提示告诉用户装一下再启动。
        try
        {
            _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            LogInfo("WebView2 运行时缺失: " + ex.Message);
            System.Windows.MessageBox.Show(
                "检测到系统缺少 Microsoft WebView2 运行时，JuniGrid 的界面依赖它。\n\n" +
                "请下载并安装一次（装完重新启动本程序）：\n" +
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" +
                "提示：Windows 沙盒是一次性系统，每次新开沙盒都需要重新安装。\n" +
                $"技术信息：{ex.Message}",
                "JuniGrid 无法启动：缺少 WebView2 运行时",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        if (_uninstallMode)
        {
            new UninstallWindow().Show();
            return;
        }

        // 1) 先弹透明 splash 窗口（logo 停 0.5s → 淡入 1.2s）
        var splash = new SplashWindow();
        splash.Show();

        // 2) 后台构造 MainWindow（屏外挂载；现身位置由 MainWindow 按所在显示器居中）
        MainWindow? main = null;
        bool uiReady = false;
        bool introDone = false;
        bool revealed = false;

        void RevealMain()
        {
            if (revealed) return;
            revealed = true;
            LogInfo("RevealMain: 显示主窗口");
            // v1.1.5：启动默认【窗口化】—— 现身即窗口化居中，不再强制最大化。
            // 旧逻辑（v1.1.2/v1.1.7）启动即最大化 + 三次复查补投，会把 OnSourceInitialized
            // 里按显示器比例算好的响应式窗口尺寸（77.1%×72.7%）整个覆盖掉；
            // 最大化交给用户自己点标题栏按钮。
            // v1.1.8：居中交给 MainWindow.RevealAtStartupPosition —— 它用与
            // OnSourceInitialized 尺寸计算同一块显示器、同一套 DPI 换算得出的坐标现身，
            // 尺寸与位置永远同屏匹配（SystemParameters.WorkArea 只描述主屏，而窗口
            // 屏外挂载就近落在哪块屏并不确定，两屏不一致时会出现"按 A 屏算尺寸、
            // 摆到 B 屏居中"的错位）。
            main!.RevealAtStartupPosition();
            main.Activate();
        }

        void TryReveal()
        {
            LogInfo($"TryReveal: introDone={introDone} uiReady={uiReady} mainNull={main is null}");
            if (!(introDone && uiReady) || main is null) return;

            // 分段转场：先淡出整个 Splash（含文字）。MainWindow 要等 Splash
            // 完全关闭后再 SW_SHOW 现身 —— 避免“动画没演完、界面就从背后顶出来”。
            if (!revealed && splash.Visibility == System.Windows.Visibility.Visible)
            {
                splash.Closed += (_, _) => RevealMain();
                splash.FadeOutAndClose();
            }
            else
            {
                RevealMain();
            }
        }

        splash.IntroCompleted += () =>
        {
            LogInfo("Splash.IntroCompleted fired");
            introDone = true;
            Dispatcher.Invoke(TryReveal);
        };

        // 用 Loaded → BlazorWebView 首次 UI Ready 作为 uiReady 信号：
        // MainLayout.OnAfterRenderAsync 会通过 JS interop 调 App.NotifyUiReady()。
        UiReadyCallback = () =>
        {
            LogInfo("UiReadyCallback fired");
            uiReady = true;
            Dispatcher.Invoke(TryReveal);
        };

        // Dispatcher 空闲时创建主窗口 —— 让 splash 先渲染出来
        Dispatcher.BeginInvoke(new Action(() =>
        {
            main = new MainWindow();
            MainWindow = main;
            // v1.1.8：不再提前预算居中位置 —— 此刻窗口还是 XAML 兜底尺寸(1600×1000)，
            // 响应式尺寸要等 Show 触发 OnSourceInitialized 才确定，提前算的位置必然过期。
            // 现身坐标由 OnSourceInitialized 按所在显示器算好、RevealAtStartupPosition 应用。
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            // v0.23.0：屏外挂载 —— WebView2 是独立子 HWND，DirectComposition 直写屏幕，
            // 父窗口任何透明手段（Opacity/layered）都拦不住它的黑底。
            // DWM 不合成屏外窗口，放 (-32000,-32000) 启动，动画播完再挪回滑入。
            main.ShowActivated = false;
            main.Left = -32000;
            main.Top = -32000;
            main.Show();

            // 竞态兜底：无论前端「ui-ready」握手或 Splash.IntroCompleted 有没有按时
            // 到位，主窗口都必须在有限时间内滑入，绝不出现“淡出后空窗、进程还活着”。
            // 每 300ms 复查一次；主窗一旦可见就停。动画全程约 5s，兜底放宽到 6s，
            // 保证动画真正播完（IntroCompleted）后才切页，避免抢切。
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            long elapsedMs = 0;
            timer.Tick += (_, _) =>
            {
                elapsedMs += 300;
                if (revealed) { timer.Stop(); return; }
                if (elapsedMs >= 6000) { introDone = true; uiReady = true; }
                TryReveal();
            };
            timer.Start();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>由 MainLayout.OnAfterRenderAsync → JS → C# 触发。</summary>
    public static Action? UiReadyCallback { get; set; }

    public static void NotifyUiReady()
    {
        UiReadyCallback?.Invoke();
        // v0.2.1：UI 就绪数秒后把启动峰值的工作集换出 —— 只换页不 GC（无暂停感），
        // GC 才几 MB，工作集大头是运行时/框架映像；系统要内存时会自动换回。
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            try { JuniGrid.Services.MemoryService.TrimWorkingSet(); } catch { }
        });
    }

    /// <summary>启动阶段隐形挂载标志：MainWindow.OnSourceInitialized 检查它决定是否 alpha=0。</summary>

    private static void LogInfo(string line)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n"); } catch { }
    }

    private void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    var link = await r.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(link))
                    {
                        if (link == ActivateCommand)
                            await Dispatcher.InvokeAsync(ActivateExistingWindow);
                        else
                            await Dispatcher.InvokeAsync(() => DispatchNxm(link));
                    }
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    internal static void DispatchNxm(string link)
    {
        var installer = Services?.GetService<InstallService>();
        if (installer is not null)
            _ = installer.HandleNxmLinkAsync(link);
        else
            PendingNxmLink = link;
    }

    /// <summary>二次启动时通知主实例激活窗口。管道连不上（主实例不存在/假死）返回 false，
    /// 调用方再走接管兜底。</summary>
    private static bool TryActivateExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(ActivateCommand);
            return true;
        }
        catch { return false; }
    }

    /// <summary>把主窗口还原/置前并交给用户操作（= 用户说的「选中状态」：任务栏高亮、
    /// 窗口获得前台焦点）。后台进程无权直接抢焦点，需借 Win32 allow foreground 链路。</summary>
    private static void ActivateExistingWindow()
    {
        try
        {
            var w = Current.MainWindow;
            if (w is null) return;
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Show();
            w.Activate();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch (Exception ex) { LogInfo("ActivateExistingWindow: " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // v0.60.0：吞掉 "no browser renderer with ID" —— WebView2 在页面切换/最小化恢复时，
        // 残留的 JS 调用打到已销毁的 renderer 会从这里抛到 UI 线程，之前只吞了 TaskScheduler
        // 那条路，WpfDispatcher 这条路漏了导致反复炸日志。
        if (e.Exception?.ToString().Contains("no browser renderer") == true)
        {
            e.Handled = true;
            return;
        }
        Log("UI", e.Exception);
        MessageBox.Show($"JuniGrid 启动失败\n\n{e.Exception?.Message}\n\n完整日志已写入：\n{LogPath}",
                        "启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Log("FATAL", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("TASK", e.Exception);
        e.SetObserved();
    }

    private static void Log(string tag, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {ex}\n\n");
        }
        catch { /* ignore logging failure */ }
    }
}
