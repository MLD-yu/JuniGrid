using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace JuniGrid;

/// <summary>
/// GUI 卸载向导（JuniGrid.exe --uninstall）：深色确认页 → 立绘卸载中 → 白底完成页。
/// 目录收尾交给独立进程在本进程退出后执行，避免加载中的运行库文件因锁定而残留。
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
    private bool _purgeUserData;

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
        _purgeUserData = PurgeUserDataBox.IsChecked == true;
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
                DoneMessage.Text = _purgeUserData
                    ? "JuniGrid 已卸载。安装目录、缓存、设置、Nexus 登录状态和收起来的存档副本将在此窗口关闭后彻底删除，删完找不回来。结果见 %TEMP%\\JuniGrid-uninstall-report.txt。"
                    : "JuniGrid 已卸载。程序文件将在此窗口关闭后清理；缓存、设置和登录状态会保留。结果见 %TEMP%\\JuniGrid-uninstall-report.txt。";
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
        var planPath = Path.Combine(Path.GetTempPath(), "jg-uninstall-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid");
            var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid");
            var manifest = ReadInstallManifest(_installDir);
            if (manifest.Count == 0) manifest = ["JuniGrid.exe", "Uninstall.exe"];
            // Uninstall.exe 是解包后从 JuniGrid.exe 复制出来的，不在 payload 清单里 ——
            // 不补进来的话普通卸载会留下一个还能双击的卸载器副本。
            foreach (var extra in new[] { "Uninstall.exe", ManifestFile })
                if (!manifest.Contains(extra, StringComparer.OrdinalIgnoreCase)) manifest.Add(extra);
            File.WriteAllText(planPath, JsonSerializer.Serialize(new
            {
                ParentPid = Environment.ProcessId,
                InstallDir = _installDir,
                ManifestFiles = manifest,
                PurgeUserData = _purgeUserData,
                AppDataDir = appData,
                LocalAppDataDir = localAppData,
                LegacyTempDir = Path.Combine(Path.GetTempPath(), "JuniGrid"),
                CustomCacheDirs = ReadCustomCacheDirs(appData),
                ReportPath = Path.Combine(Path.GetTempPath(), "JuniGrid-uninstall-report.txt"),
            }), new UTF8Encoding(false));

            if (TryStartPowerShellCleanup(planPath)) return;
            StartLegacyPurge();
        }
        catch
        {
            // 计划文件都没写成 / 连进程都起不来：退回旧的自删实例，至少把两个 exe 收掉。
            try { StartLegacyPurge(); } catch { /* 清理启动失败时保留文件，绝不递归误删。 */ }
        }
    }

    /// <summary>用 PowerShell 做收尾：它在安装目录之外，能删掉<b>包括两个 exe 在内</b>的全部
    /// 程序文件（进程不能删自己）。脚本整段 base64 内联，不落盘 —— 见 <see cref="CleanupScript"/>。</summary>
    private static bool TryStartPowerShellCleanup(string planPath)
    {
        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps)) return false;

        var psi = new ProcessStartInfo
        {
            FileName = ps,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.Environment["JG_UNINSTALL_PLAN"] = planPath;
        foreach (var arg in new[]
        {
            "-NoProfile", "-NonInteractive",
            // 企业机常把默认策略设成 AllSigned / Restricted，不加这句 -EncodedCommand 也可能被拦
            "-ExecutionPolicy", "Bypass",
            "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(CleanupScript)),
        })
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi);
        return p is not null;
    }

    /// <summary>退路：让安装目录里那份 exe 起一个极短实例，删两个 exe + 收空目录。
    /// 能力比 PowerShell 收尾弱（删不了自己那份），但它是本地代码，不依赖任何外部解释器。</summary>
    private void StartLegacyPurge()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in PurgeArguments(_installDir)) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    private static List<string> ReadCustomCacheDirs(string appData)
    {
        try
        {
            var config = Path.Combine(appData, "junigrid.config.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            if (!doc.RootElement.TryGetProperty("cacheRoot", out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString())) return [];
            var root = Path.GetFullPath(value.GetString()!);
            if (root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) return [];
            var names = new[] { "depot-staging", "downloads", "mods-backup", "portrait-covers",
                "self-update", "smapi-installer", "webview2", "nexus-browse", "nexus-web" };
            return names.Select(n => Path.Combine(root, n)).Append(Path.Combine(root, "_smapi-install.dat")).ToList();
        }
        catch { return []; }
    }

    /// <summary>卸载收尾脚本。用 <c>-EncodedCommand</c> 整段传给 PowerShell（UTF-16LE + base64），
    /// <b>不落 .ps1 文件</b>：
    /// <list type="bullet">
    /// <item>落盘的话 PS 5.1 按 ANSI 读无 BOM 的 UTF-8 脚本，中文串在 CP936 机器上会变成乱码、
    /// 撑破引号直接语法错 —— 实测过，整段脚本一行都不执行，卸载后文件全留着。</item>
    /// <item>不落盘也就不需要自删脚本，少一类"临时目录里躺个 .ps1"的杀软启发式特征。</item>
    /// </list>
    /// 计划数据走环境变量 <c>JG_UNINSTALL_PLAN</c>，路径里有空格/中文都不用拼引号。</summary>
    private const string CleanupScript = """
$ErrorActionPreference = 'SilentlyContinue'
$planPath = $env:JG_UNINSTALL_PLAN
$plan = Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json

# 父进程（卸载向导）不退出，加载中的 dll 就一直锁着 —— 先等它真的没了再动手。
$parentId = [int]$plan.ParentPid
try { Wait-Process -Id $parentId -Timeout 120 } catch { }
for ($i = 0; $i -lt 480; $i++) {
  if (-not (Get-Process -Id $parentId -ErrorAction SilentlyContinue)) { break }
  Start-Sleep -Milliseconds 250
}

# 删不动就重试：进程刚退出时 WebView2 子进程可能还攥着几百毫秒的文件锁。
function Remove-Tree([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { return }
  for ($i = 0; $i -lt 12; $i++) {
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $path)) { return }
    Start-Sleep -Milliseconds 300
  }
}

function Remove-EmptyTree([string]$root) {
  if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
  Get-ChildItem -LiteralPath $root -Directory -Recurse -Force |
    Sort-Object { $_.FullName.Length } -Descending |
    ForEach-Object { if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $_.FullName -Force } }
  if (-not (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $root -Force }
}

# 安装目录读不出来、或它本身就是磁盘根（C:\）⇒ 一个文件都不动。
# 彻底删除那支是整棵递归删，传进来一个盘根就等于格盘；
# InstallRecycle.cs 的回收口有同一道闸，两边判据必须一致。
$root = $null
if (-not [string]::IsNullOrWhiteSpace([string]$plan.InstallDir)) {
  try { $root = [IO.Path]::GetFullPath([string]$plan.InstallDir).TrimEnd('\') } catch { $root = $null }
}
if ($root -match '^[A-Za-z]:$') { $root = $null }
if (-not $root) {
  ('卸载：安装目录读不出来或本身就是磁盘根（' + [string]$plan.InstallDir + '），一个文件都没动。') |
    Set-Content -LiteralPath $plan.ReportPath -Encoding UTF8
  Remove-Item -LiteralPath $planPath -Force
  exit
}

if ($plan.PurgeUserData) {
  Remove-Tree $root
  Remove-Tree $plan.AppDataDir
  Remove-Tree $plan.LocalAppDataDir
  Remove-Tree $plan.LegacyTempDir
  foreach ($path in $plan.CustomCacheDirs) { Remove-Tree $path }
} else {
  foreach ($rel in @($plan.ManifestFiles)) {
    $relative = ([string]$rel).Replace('/', '\')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('\') -contains '..') { continue }
    $full = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))
    if ($full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Tree $full }
  }
  Remove-Tree ([IO.Path]::Combine($root, 'junigrid-files.txt'))
  Remove-EmptyTree $root
}
$left = @()
if (Test-Path -LiteralPath $root) { $left += Get-ChildItem -LiteralPath $root -Force | ForEach-Object { $_.Name } }
foreach ($path in $plan.CustomCacheDirs) { if (Test-Path -LiteralPath $path) { $left += $path } }
$head = if ($plan.PurgeUserData) { '彻底删除：' } else { '卸载：' }
if ($left.Count -eq 0) {
  ($head + '安装目录已清空，程序文件全部移除。') | Set-Content -LiteralPath $plan.ReportPath -Encoding UTF8
} else {
  @($head + '以下项目仍在磁盘上（不属于程序文件，或当前被占用）：') + $left |
    Set-Content -LiteralPath $plan.ReportPath -Encoding UTF8
}
Remove-Item -LiteralPath $planPath -Force
""";

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
