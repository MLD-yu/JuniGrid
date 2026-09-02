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

            // 不带 nxm 链接的二次启动 = 用户明确要再开一次。最典型是升级装完后
            // 启动：旧实例若因权限不足/竞态没被安装器关掉，会一直占着单实例锁，
            // 原来的处理是无声退出，表现为「点了没反应、开出来的还是旧版」。改为
            // 关掉旧实例后由本实例接管。
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
        if (_uninstallMode)
        {
            new UninstallWindow().Show();
            return;
        }

        // 1) 先弹透明 splash 窗口（logo 停 0.5s → 淡入 1.2s）
        var splash = new SplashWindow();
        splash.Show();

        // 2) 后台构造 MainWindow（Visibility=Hidden + 位置预设到屏幕下方）
        MainWindow? main = null;
        bool uiReady = false;
        bool introDone = false;
        bool revealed = false;
        double targetTop = 0;
        double targetLeft = 0;

        void RevealMain()
        {
            if (revealed) return;
            revealed = true;
            LogInfo("RevealMain: 显示主窗口");
            main!.Left = targetLeft;
            main.Top = targetTop + 34;
            main.Activate();

            // 位置滑入
            var slide = new DoubleAnimation
            {
                From = targetTop + 34, To = targetTop,
                Duration = TimeSpan.FromMilliseconds(460),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            main.BeginAnimation(System.Windows.Window.TopProperty, slide);

            // v0.21.0：用 Win32 层 alpha 从 0 → 255 淡入（DWM 合成器直接控制像素）
            // 淡入完成后自动移除 WS_EX_LAYERED，恢复零开销正常合成路径
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
            // 预算目标位置（居中）
            var screenW = SystemParameters.WorkArea.Width;
            var screenH2 = SystemParameters.WorkArea.Height;
            main.Left = (screenW - main.Width) / 2 + SystemParameters.WorkArea.Left;
            targetLeft = main.Left;
            targetTop = (screenH2 - main.Height) / 2 + SystemParameters.WorkArea.Top;
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
                        await Dispatcher.InvokeAsync(() => DispatchNxm(link));
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
