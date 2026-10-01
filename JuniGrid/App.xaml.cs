using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using JuniGrid.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace JuniGrid;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(StoragePaths.AppDataDir, "crash.log");

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
        // 活下来的那个实例负责处理 nxm:// 注册项：「一键安装」开着就从 Vortex/NMM 手里抢过来，
        // 关了才只在「没人能接」时修回自己（见方法注释）
        EnsureNxmHandlerPointsAtSelf(ReadOneClickInstallFlag());
        PendingNxmLink = nxmArg;

        base.OnStartup(e);
    }

    /// <summary>OnStartup 早于 DI，读不了 ConfigService.Current，这里只瞄一眼配置文件里的「一键安装」。
    /// 读不到按该属性的默认值 true 处理 —— 配置文件缺失＝首启，损坏时 ConfigService 也会回落到同一默认。</summary>
    private static bool ReadOneClickInstallFlag()
    {
        try
        {
            var p = ConfigService.ConfigFilePath;
            if (!File.Exists(p)) return true;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            return !doc.RootElement.TryGetProperty("enableOneClickInstall", out var v)
                   || v.ValueKind != System.Text.Json.JsonValueKind.False;
        }
        catch { return true; }
    }

    /// <summary>设置页开关调用：立刻把 nxm:// 注册项指向当前实例。</summary>
    public static void ClaimNxmHandlerNow() => EnsureNxmHandlerPointsAtSelf(true);

    // 抢注前那条命令的原值（Vortex / NMM 写下的）存在我们自己的键下：
    // nxm 那棵树是我们建的、释放时要动它，不能把要还原的东西寄存在会被动掉的地方。
    private const string NxmBackupKey = @"Software\JuniGrid";
    private const string NxmBackupValue = "NxmDisplacedCommand";
    private const string NxmRootKey = @"Software\Classes\nxm";
    private const string NxmCmdKey = NxmRootKey + @"\shell\open\command";

    /// <summary>从注册表那条命令里取出 exe 路径（值形如 "C:\...\Vortex.exe" "%1"）。</summary>
    private static string? ExeOf(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var m = Regex.Match(cmd, "\"?(.+?\\.exe)\"?", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : cmd;
    }

    /// <summary>设置页开关关闭时调用：把我们压掉的那条原命令交还给上一个管理器。
    /// 只删键是不够的 —— 删掉的正是别家写进去的那个值，删完谁都没有；对方会不会自己再写回来
    /// 取决于它自己的时机，我们控制不了。抢注时已把原值备份，所以这里能确定性地还原。
    /// 没有备份（或备份里的 exe 已不存在）才退回删除。</summary>
    public static void ReleaseNxmHandler()
    {
        try
        {
            string? saved = null;
            using (var k = Registry.CurrentUser.OpenSubKey(NxmBackupKey))
                saved = k?.GetValue(NxmBackupValue) as string;
            var back = ExeOf(saved);

            if (back is { Length: > 0 } && File.Exists(back))
            {
                // 原样写回，一个字都不能改：这条值本身就是完整命令行（自带引号和参数）。
                // 早先这里误传给 WriteSelf，于是整条又被包一层引号、再追加一个 "%1"，
                // 变成 ""E:\...\Vortex.exe" "%1"" "%1" —— Vortex 解析不出 -d，只置顶不下载。
                WriteNxmCommand(saved!);
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(NxmBackupKey, true);
                    if (k?.GetValue(NxmBackupValue) is not null) k.DeleteValue(NxmBackupValue);
                }
                catch { }
                AppLog.Info("Startup", "nxm:// 处理器已交还原管理器: " + back);
                return;
            }

            // 没有可交还的备份时，也只有「这条登记确实是我们自己的（或我们留下的残骸）」才删。
            // 别的管理器后来自己登记回来了，删树等于把人家整棵 nxm 键端掉（连 URL Protocol 一起没），
            // 症状就是"关了开关反而谁都收不到下载"。
            string? cur = null;
            using (var k = Registry.CurrentUser.OpenSubKey(NxmCmdKey))
                cur = k?.GetValue(null) as string;
            var holder = ExeOf(cur);
            var isOurs = holder is { Length: > 0 }
                && string.Equals(holder, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            var isOurGhost = holder is { Length: > 0 } && !File.Exists(holder)
                && string.Equals(Path.GetFileName(holder), "JuniGrid.exe", StringComparison.OrdinalIgnoreCase);
            if (!isOurs && !isOurGhost)
            {
                AppLog.Info("Startup", "nxm:// 登记已不属于我们（" + (holder ?? "空") + "），交还时不动它");
                return;
            }

            if (Registry.CurrentUser.OpenSubKey(NxmRootKey) is not null)
                Registry.CurrentUser.DeleteSubKeyTree(NxmRootKey);
            AppLog.Info("Startup", back is { Length: > 0 }
                ? $"已删除 nxm:// 处理器（备份里的 {back} 已不存在，交还无意义）"
                : "已删除 nxm:// 处理器（我们没抢过别人，没有可交还的对象）");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Startup", "释放 nxm:// 处理器失败: " + ex.Message);
        }
    }

    private static void WriteSelf(string exe) => WriteNxmCommand($"\"{exe}\" \"%1\"");

    /// <summary>补上 URL Protocol 这个空值 —— Windows 认不认 nxm:// 是协议，看的是它，
    /// 不是 command 键。缺了的症状：点网页上的「下载」按钮，浏览器什么都不做。
    /// 只在缺失时写，返回是否补过。</summary>
    private static bool EnsureUrlProtocolMarker()
    {
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(NxmRootKey);
            if (root is null || root.GetValue("URL Protocol") is not null) return false;
            root.SetValue(null, "URL:nxm");
            root.SetValue("URL Protocol", "");
            AppLog.Info("Startup", "nxm:// 缺 URL Protocol 标记（点下载会毫无反应），已补上");
            return true;
        }
        catch (Exception ex) { AppLog.Warn("Startup", "补 URL Protocol 失败: " + ex.Message); return false; }
    }

    /// <summary>写 nxm 处理器。参数是**整条命令行**，原样落盘不再加工 ——
    /// 交还别的管理器时必须走这里，不能走 WriteSelf（那条会给参数再套一层引号并追加 "%1"）。</summary>
    private static void WriteNxmCommand(string commandLine)
    {
        using (var root = Registry.CurrentUser.CreateSubKey(NxmRootKey))
        {
            root?.SetValue(null, "URL:nxm");
            root?.SetValue("URL Protocol", "");
        }
        using var cmd = Registry.CurrentUser.CreateSubKey(NxmCmdKey);
        cmd?.SetValue(null, commandLine);
    }

    /// <summary>nxm:// 处理器自检（HKCU，不需要管理员）。
    /// 开（=「一键安装」开着）：无条件抢过来，但先把压掉的那条原命令备份下来，好在关闭时交还。
    /// 关：退出竞争，一律不写自己 —— 唯一例外是这条注册是我们自己留下的死路径（旧版本把命令写进了
    /// 仓库 bin\Debug，那份 exe 一删点「Mod Manager Download」就静默失败，看起来像"我们不接下载"），
    /// 这种残局要收拾。注意"键不存在"不算残局：那正是我们把位置让出去了。</summary>
    private static void EnsureNxmHandlerPointsAtSelf(bool claim)
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || !File.Exists(exe)) return;
            string? cur;
            using (var rk = Registry.CurrentUser.OpenSubKey(NxmCmdKey))
                cur = rk?.GetValue(null) as string;
            var path = ExeOf(cur);

            // 已经指着自己 ⇒ 两种模式都不用动，也不能动（动了会把备份覆盖成我们自己）。
            // 但"命令在、URL Protocol 缺"这种半残状态必须补 —— 那个空值才是 Windows 把 nxm://
            // 当成协议交给浏览器的凭据，缺了的症状是点「下载」按钮毫无反应（2026-09-30 实测）。
            if (path is not null && path.Equals(exe, StringComparison.OrdinalIgnoreCase))
            {
                EnsureUrlProtocolMarker();
                return;
            }

            if (!claim)
            {
                var oursDead = path is { Length: > 0 }
                               && !File.Exists(path)
                               && Path.GetFileName(path).Equals("JuniGrid.exe", StringComparison.OrdinalIgnoreCase);
                if (!oursDead) return;
            }

            // 只有确实压掉了一个还能用的管理器才记备份；指向死路径的旧值不值得留档
            if (cur is { Length: > 0 } && path is { Length: > 0 } && File.Exists(path))
                try
                {
                    using var bk = Registry.CurrentUser.CreateSubKey(NxmBackupKey);
                    bk?.SetValue(NxmBackupValue, cur);
                }
                catch { }

            WriteSelf(exe);
            // 记原值必须记整条命令，不能记剥掉参数后的 exe 路径：
            // Vortex 登记的是 "<exe>" -d "%1"，少了 -d 它 commander 解析不出 --download，
            // 就只会把窗口置顶然后直接 return（表现为"弹到前台但不下载"）。
            AppLog.Info("Startup",
                $"nxm:// 处理器已{(claim ? "接管" : "修回")}当前实例: {exe}" +
                (string.IsNullOrEmpty(cur) ? "" : "（原: " + cur + "）"));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Startup", "nxm:// 处理器自检失败: " + ex.Message);
        }
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

    /// <summary>
    /// v1.1.8：用安装包自带的 WebView2 Evergreen 引导补装运行时。
    /// 返回 true = 引导已跑完且退出码成功（0 / 已装更高版本）。
    /// </summary>
    private static bool TryInstallBundledWebView2()
    {
        try
        {
            var setup = Path.Combine(AppContext.BaseDirectory, "tools", "WebView2", "MicrosoftEdgeWebView2Setup.exe");
            if (!File.Exists(setup)) return false;
            LogInfo("正在用内置引导安装 WebView2 运行时…");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = setup,
                // /silent /install：静默装 Evergreen（约 1–2 分钟，按网络情况）
                Arguments = "/silent /install",
                UseShellExecute = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(5 * 60 * 1000)) return false;
            return p.ExitCode is 0 or 3010 or 1638;
        }
        catch (Exception ex)
        {
            LogInfo("WebView2 内置引导失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>Startup 事件占位 —— 真正的 splash → main 编排放在这里。</summary>
    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        // v1.1.2：WebView2 运行时前置检测 —— 正常 Win10/11 预装，但 Windows 沙盒、
        // LTSC/精简系统可能没有。缺失时裸异常是一屏英文堆栈（界面永远出不来）。
        // v1.1.8：安装包自带 tools\WebView2\MicrosoftEdgeWebView2Setup.exe，缺运行时
        // 自动补装（新机免手动装依赖）；引导也不在时才退回提示用户去官网装。
        try
        {
            _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            LogInfo("WebView2 运行时缺失: " + ex.Message);
            if (TryInstallBundledWebView2())
            {
                // 引导装完后本进程的浏览器环境句柄已失效，提示用户重启（或直接拉起自己）
                LogInfo("WebView2 运行库已通过内置引导安装完成");
                var relaunch = System.Windows.MessageBox.Show(
                    "缺少的 Microsoft WebView2 运行时已安装完成。\n\n是否立即重新启动 JuniGrid？",
                    "JuniGrid",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information);
                if (relaunch == System.Windows.MessageBoxResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = Environment.ProcessPath ?? "JuniGrid.exe",
                            UseShellExecute = true,
                        });
                    }
                    catch { }
                }
                Shutdown();
                return;
            }
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

    /// <summary>把主窗口拉到所有应用最前并抢前台焦点（登录、下载完成时调用）。
    /// Windows 有「前台锁」：非前台进程直接调 SetForegroundWindow 会被系统忽略。
    /// 这里用经典绕过链 —— AttachThreadInput 把本线程输入队列临时挂到当前前台窗口线程，
    /// 再 BringWindowToTop + SetForegroundWindow，并短暂置 Topmost 抬升 Z 序。</summary>
    public static void BringMainWindowToFront()
    {
        try
        {
            var app = Current;
            if (app is null) return;
            if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(BringMainWindowToFront); return; }
            var w = app.MainWindow;
            if (w is null) return;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) { w.Activate(); return; }

            w.Topmost = true;
            w.Topmost = false;
            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            var fore = GetForegroundWindow();
            var foreTid = GetWindowThreadProcessId(fore, IntPtr.Zero);
            var curTid = GetCurrentThreadId();
            bool attached = foreTid != 0 && foreTid != curTid && AttachThreadInput(curTid, foreTid, true);
            try { SetForegroundWindow(hwnd); }
            finally { if (attached) AttachThreadInput(curTid, foreTid, false); }
            w.Activate();
        }
        catch { }
    }

    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>读 Windows 系统强调色（HKCU\...\DWM\AccentColor，0x00BBGGRR）→ "#RRGGBB"。
    /// 读不到返回 null（前端回落到默认蓝）。用于「成功」类 toast 跟随系统配色。</summary>
    public static string? GetSystemAccentHex()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            var v = k?.GetValue("AccentColor") as int?;
            if (v is not int a) return null;
            int r = a & 0xFF, g = (a >> 8) & 0xFF, b = (a >> 16) & 0xFF;
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch { return null; }
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
