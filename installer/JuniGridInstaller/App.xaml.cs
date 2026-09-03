using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace JuniGridInstaller;

public partial class App : Application
{
    private static Mutex? _single;

    public static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "jgsetup-log.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("App.OnStartup");

        // 单实例：安装包双击第二次不该再弹一个安装器。已有实例时激活它的窗口置前，
        // 本实例退出。
        _single = new Mutex(true, "JuniGrid.Setup.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log("已有安装器实例在运行 → 激活后退出");
            ActivateExistingWindow();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Log("AppDomain.UnhandledException: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) =>
            Log("UnobservedTaskException: " + a.Exception);
        // 安装器不能闪退：兜底弹窗给用户看原因
        DispatcherUnhandledException += (_, args) =>
        {
            Log("DispatcherUnhandledException: " + args.Exception);
            try
            {
                MessageBox.Show("安装器遇到错误：" + args.Exception.Message,
                    "JuniGrid 安装", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true;
        };
        Log("App.OnStartup done");
    }

    /// <summary>把已在运行的安装器窗口还原/置前（后台进程无权直接抢焦点，走 Win32）。</summary>
    private static void ActivateExistingWindow()
    {
        try
        {
            var w = Current.Windows.OfType<InstallerWindow>().FirstOrDefault();
            if (w is null) return;
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Show();
            w.Activate();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
        }
        catch (Exception ex) { Log("ActivateExistingWindow: " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
