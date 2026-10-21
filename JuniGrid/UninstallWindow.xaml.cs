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
/// 目录收尾在退出前拉起自身子实例（--purge-install-dir）删两个 exe + 空目录，
/// 不再起 cmd 延时自删（原因见 <see cref="ScheduleSelfDelete"/> 的注释）。
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

        // 换目录装过之后，老目录里那份 Uninstall.exe（= 主程序的副本）还在，还能双击。
        // 快捷方式与卸载项都是<b>全局唯一</b>的：那时它们指向的是新装那份，
        // 老副本一按就把它们抹掉 ⇒ 用户在控制面板里再也找不到"卸载 JuniGrid"，
        // 桌面图标也没了，而新装那份还好端端地跑着。所以先问一句"这条登记说的是我不是"。
        var registeredHere = RegisteredHere();

        // 2) 快捷方式
        if (registeredHere)
        {
            try
            {
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "JuniGrid.lnk"));
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "JuniGrid.lnk"));
                var group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "JuniGrid");
                if (Directory.Exists(group)) Directory.Delete(group, true);
            }
            catch { }
        }

        // 3) 注册表卸载项
        if (registeredHere)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false); } catch { }
        }
    }

    /// <summary>注册表里那条唯一的卸载项是不是登记着<b>本目录</b>这一份。</summary>
    private bool RegisteredHere()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return KeyDescribesThisDir(k?.GetValue("InstallLocation") as string, _installDir);
        }
        catch { return false; }
    }

    /// <summary>判"这条卸载登记指向哪份安装"。抽出来单独钉：装到 E:\JuniGrid2 之后
    /// E:\JuniGrid1 里那份副本仍然可执行，靠的就是这一句把它认出去。</summary>
    internal static bool KeyDescribesThisDir(string? installLocation, string ownDir)
    {
        var a = NormDir(installLocation);
        var b = NormDir(ownDir);
        return a.Length > 0 && b.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormDir(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try
        {
            return Path.GetFullPath(p)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return p.TrimEnd('\\', '/'); }
    }

    /// <summary>装机目录里的安装清单文件名。格式与写它的一方一致：
    /// 见 <c>installer/JuniGridInstaller/InstallRecycle.cs</c>（首行 <c>JGMF1</c>，之后每行一个相对路径）。</summary>
    internal const string ManifestFile = "junigrid-files.txt";

    /// <summary>读安装清单；没有清单、格式不认识、或条目要穿越出安装目录 ⇒ 一律不计入。</summary>
    internal static List<string> ReadInstallManifest(string dir)
    {
        var list = new List<string>();
        try
        {
            var p = Path.Combine(dir, ManifestFile);
            if (!File.Exists(p)) return list;
            var lines = File.ReadAllLines(p);
            if (lines.Length == 0 || lines[0].Trim() != "JGMF1") return list;
            foreach (var raw in lines.Skip(1))
            {
                var rel = raw.Trim().Replace('\\', '/');
                if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Split('/').Contains("..")) continue;
                list.Add(rel);
            }
        }
        catch { }
        return list;
    }

    /// <summary>按清单删掉我们自己写出去的文件，并把因此变空的目录收掉。
    /// 正在运行的两个 exe 删不掉，交给收尾子实例（<see cref="ScheduleSelfDelete"/>）。
    /// <b>清单以外的东西一个都不碰。</b></summary>
    internal static int CleanByManifest(string dir, List<string> rels)
    {
        var deleted = 0;
        foreach (var rel in rels)
        {
            var full = SafeCombine(dir, rel);
            if (full is null) continue;
            try
            {
                if (File.Exists(full)) { File.Delete(full); deleted++; }
                else if (Directory.Exists(full)) { Directory.Delete(full, true); deleted++; }
            }
            catch { }
        }
        try
        {
            if (File.Exists(Path.Combine(dir, ManifestFile))) { File.Delete(Path.Combine(dir, ManifestFile)); deleted++; }
        }
        catch { }
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
        catch { }
        return deleted;
    }

    private static string? SafeCombine(string root, string rel)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 卸载后处理本目录残留的<b>两个 exe</b>（JuniGrid.exe / Uninstall.exe）。
    ///
    /// <para>历史实现是起一个 <c>cmd.exe /c ping ... &amp; del /q /f ... &amp; rd ...</c> 延时自删。
    /// 那串命令是恶意软件家族的经典自删手法（用 ping 当 sleep + del /f + rd），
    /// 杀软启发式规则里针对它的签名最硬，是误报的主要来源之一。v1.3.0 起彻底不用。</para>
    ///
    /// <para><b>为什么不是 MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)：</b>
    /// 那个标志内部是往 <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\
    /// PendingFileRenameOperations</c> 写值，该键对 BUILTIN\Users 只有 ReadKey
    /// （实测非管理员调用返回 ERROR_ACCESS_DENIED = 5），MSDN 也明确要求调用者属于
    /// administrators 组。本程序清单是 asInvoker（<c>app.manifest</c> 里有说明：
    /// 装机全程只写 HKCU / %LocalAppData%，不该提权），所以这条路走不通。</para>
    ///
    /// <para><b>现在这样做：</b>进程退出后，启动一个<b>极短</b>的子实例
    /// （<c>JuniGrid.exe --purge-install-dir &lt;dir&gt;</c>），它只做三件事 ——
    /// 删两个 exe、删空目录、退出。不碰 shell、不拼命令行删除语句、不按名字杀进程，
    /// 从行为上看就是一个程序在收拾自己的安装目录。</para>
    ///
    /// <para>与旧实现的能力差异：旧实现跨 ~15 秒重试三次（等 WebView2 子进程释放文件锁）。
    /// 新实现用"重试到成功或超时"的循环，覆盖同样的场景。</para>
    /// </summary>
    internal static string[] PurgeArguments(string dir) =>
        ["--purge-install-dir", dir];

    /// <summary>调度本目录的清理：必须在窗口关闭（进程即将退出）时才执行。</summary>
    private void ScheduleSelfDelete()
    {
        try
        {
            var dir = _installDir;
            // 有清单就按清单把"我们写出去的文件"收干净（此刻进程还活着，两个 exe 留给子实例）。
            // 没清单就一个文件都不多删 —— 子实例只摘两个 exe，空目录才会被收掉，
            // 目录里只要还剩一个认不清的文件就原样留着（宁可留一堆 dll，也不能端掉用户的版本缓存）。
            var manifest = ReadInstallManifest(dir);
            if (manifest.Count > 0) CleanByManifest(dir, manifest);

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            // ArgumentList 是只读集合，只能逐条 Add —— 转义交给框架，不手拼命令行字符串。
            foreach (var a in PurgeArguments(dir)) psi.ArgumentList.Add(a);
            Process.Start(psi);
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
