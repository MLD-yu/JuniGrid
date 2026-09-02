using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace JuniGrid;

/// <summary>
/// GUI 卸载向导（JuniGrid.exe --uninstall）：深色确认页 → 立绘卸载中 → 白底完成页。
/// 目录删除通过延时 cmd 自删（等本进程退出后 rd /s /q），与旧 uninstall.ps1 同机制。
/// </summary>
public partial class UninstallWindow : Window
{
    // 与 installer/JuniGridInstaller/InstallerEngine.cs 保持一致（同一卸载 AppId）
    private const string UninstallKeyName = "{7E1B2C64-9A4D-4C0E-9F61-3A5D8B2C4E10}_is1";
    private static readonly string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;

    private readonly string _installDir;
    private bool _working;
    private bool _confirmed;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public UninstallWindow()
    {
        InitializeComponent();
        _installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            }
            catch { }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
        };
        // 卸载模式里这是唯一窗口：关掉就结束应用；已确认卸载的话退出前调度目录自删
        Closed += (_, _) =>
        {
            if (_confirmed) ScheduleSelfDelete();
            Application.Current?.Shutdown();
        };

        // 应用正在运行 → 拦截页（对齐 Riot：无法卸载，请先关闭应用）
        if (IsAppRunning())
        {
            ConfirmScreen.Visibility = Visibility.Collapsed;
            BlockedScreen.Visibility = Visibility.Visible;
        }
    }

    private static bool IsAppRunning()
    {
        try
        {
            var self = Environment.ProcessId;
            return Process.GetProcessesByName("JuniGrid").Any(p => p.Id != self);
        }
        catch { return false; }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        _working = true;
        _confirmed = true;
        BtnUninstall.IsEnabled = false;
        BtnCancel.IsEnabled = false;

        ConfirmScreen.Visibility = Visibility.Collapsed;
        WorkingScreen.Visibility = Visibility.Visible;
        StartIndeterminate();

        Task.Run(() => { try { DoUninstall(); } catch { } })
            .ContinueWith(_ =>
            {
                StopIndeterminate();
                WorkingScreen.Visibility = Visibility.Collapsed;
                DoneScreen.Visibility = Visibility.Visible;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void DoUninstall()
    {
        // 1) 结束其它正在运行的 JuniGrid（不杀自己）
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
        Thread.Sleep(400);

        // 2) 快捷方式
        try
        {
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "JuniGrid.lnk"));
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "JuniGrid.lnk"));
            var group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "JuniGrid");
            if (Directory.Exists(group)) Directory.Delete(group, true);
        }
        catch { }

        // 3) 注册表卸载项
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false); } catch { }
    }

    /// <summary>调度目录自删：必须在窗口关闭（进程即将退出）时才执行。
    /// WebView2 子进程等释放文件锁有延迟，rd 失败会静默跳过，所以跨 ~15s 重试三次。</summary>
    private void ScheduleSelfDelete()
    {
        try
        {
            var dir = _installDir;
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping -n 3 127.0.0.1 > nul & rd /s /q \"{dir}\"" +
                            $" & ping -n 4 127.0.0.1 > nul & rd /s /q \"{dir}\"" +
                            $" & ping -n 8 127.0.0.1 > nul & rd /s /q \"{dir}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch { }
    }

    private void StartIndeterminate()
    {
        IndBar.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, new DoubleAnimation
        {
            From = -150,
            To = 520,
            Duration = TimeSpan.FromMilliseconds(900),
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopIndeterminate()
        => IndBar.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, null);
}
