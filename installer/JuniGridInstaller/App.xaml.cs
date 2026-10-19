using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace JuniGridInstaller;

public partial class App : Application
{
    /// <summary>安装窗口的标题，跨进程找它时要逐字相等。InstallerWindow.xaml 的 Title
    /// 绑的就是这一个值，所以两边不可能各写一份漂移。
    /// 判据不能只"标题非空"：IME/TSF 那类帧窗口（CiceroUIWndFrame、Default IME、MSCTFIME UI）
    /// 标题也是非空的，选中它们就等于又回到"什么都没显示"。</summary>
    public const string WindowTitle = "JuniGrid 安装";

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

    private static string PreviewRecycle(string dir)
    {
        // 默认只算不删。要真删必须显式设 JGINSTALLER_APPLY=1（和 JGINSTALLER_NOLEGACY 同一类
        // 测试逃生口），这样这个入口既能用来验证边界，也不会被人当 "rm" 误用。
        var apply = Environment.GetEnvironmentVariable("JGINSTALLER_APPLY") == "1";
        var sb = new System.Text.StringBuilder();
        sb.Append("recycle-check 目录: ").Append(dir)
          .AppendLine(apply ? "  【本次会真删】" : "  【只算不删】");
        if (!Directory.Exists(dir)) { sb.AppendLine("  （目录不存在，什么都不做）"); return sb.ToString(); }
        var r = InstallRecycle.RecycleOldInstall(dir, InstallerEngine.PayloadTopLevelNames(), dryRun: !apply);
        sb.AppendLine("  清单: " + (r.HadManifest ? "有（按清单删）" : "无（退回顶层白名单）"));
        sb.AppendLine("  " + (apply ? "删掉" : "会删掉") + "的条目数: " + r.Deleted);
        sb.AppendLine("  目录" + (apply ? (r.DirRemoved ? "已空、已删除" : "非空、保留") : "删完后会剩下")
            + ": " + (r.Remaining.Count == 0 ? "（空）" : string.Join(", ", r.Remaining)));
        if (r.Failed.Count > 0) sb.AppendLine("  失败: " + string.Join(" | ", r.Failed));
        return sb.ToString();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("App.OnStartup");

        // 验证入口：JuniGridSetup.exe --recycle-preview <目录> [输出文件]
        // 只报告「会删哪些、会留哪些」，一个文件都不删 —— 这样就能在没有真实安装的情况下
        // 证明回收逻辑的边界（尤其"用户自己的文件必须留下"那条），不必拿装机目录做实验。
        if (e.Args.Length >= 2 && e.Args[0].Equals("--recycle-preview", StringComparison.OrdinalIgnoreCase))
        {
            var report = PreviewRecycle(e.Args[1]);
            try { Console.Out.WriteLine(report); Console.Out.Flush(); } catch { }
            try
            {
                var outPath = e.Args.Length >= 3
                    ? e.Args[2]
                    : Path.Combine(Path.GetTempPath(), "jg-recycle-preview.txt");
                File.WriteAllText(outPath, report);
            }
            catch { }
            Shutdown(0);
            return;
        }

        // 搬家验证入口：JuniGridSetup.exe --migrate-preview <旧目录> <新目录> [输出文件]
        // 默认只报「会搬哪几个数据目录、各多大、配置会不会改」，一个文件都不动；
        // 真搬要显式设 JGINSTALLER_APPLY=1 —— 这个入口是用来验边界的，不能当 mv 用。
        if (e.Args.Length >= 3 && e.Args[0].Equals("--migrate-preview", StringComparison.OrdinalIgnoreCase))
        {
            var apply = Environment.GetEnvironmentVariable("JGINSTALLER_APPLY") == "1";
            var cfgOut = e.Args.Length >= 4
                ? e.Args[3]
                : Path.Combine(Path.GetTempPath(), "jg-migrate-preview.txt");
            string report;
            try
            {
                var r = InstallMigrate.Run(e.Args[1], e.Args[2], e.Args.Length >= 5 ? e.Args[4]
                    : InstallMigrate.DefaultConfigPath(), dryRun: !apply);
                var sb = new System.Text.StringBuilder();
                sb.Append("migrate-check ").Append(r.OldDir).Append(" → ").Append(r.NewDir)
                  .AppendLine(apply ? "  【本次会真搬】" : "  【只算不搬】");
                sb.AppendLine("  搬走的: " + (r.Moved.Count == 0 ? "（没有可搬的数据目录）" : string.Join(" | ", r.Moved)));
                sb.AppendLine("  合计: " + r.Bytes / (1024 * 1024) + " MB ‖ 配置改指新目录=" + r.ConfigPatched);
                if (r.Failed.Count > 0) sb.AppendLine("  没搬成的: " + string.Join(" | ", r.Failed));
                report = sb.ToString();
            }
            catch (Exception ex) { report = "migrate-check 异常: " + ex; }
            try { Console.Out.WriteLine(report); Console.Out.Flush(); } catch { }
            try { File.WriteAllText(cfgOut, report); } catch { }
            Shutdown(0);
            return;
        }

        // 只读验证入口：JuniGridSetup.exe --nxm-default [输出文件]
        // 报告「装机那个勾会默认成什么、注册表里现在是谁在接下载链接」，不写任何键。
        if (e.Args.Length >= 1 && e.Args[0].Equals("--nxm-default", StringComparison.OrdinalIgnoreCase))
        {
            var nxmReport = "";
            try { nxmReport = InstallerEngine.DescribeNxmDefault(); }
            catch (Exception ex) { nxmReport = "nxm-default 异常: " + ex; }
            try { Console.Out.WriteLine(nxmReport); Console.Out.Flush(); } catch { }
            try
            {
                var outPath = e.Args.Length >= 2
                    ? e.Args[1]
                    : Path.Combine(Path.GetTempPath(), "jg-nxm-default.txt");
                File.WriteAllText(outPath, nxmReport);
            }
            catch { }
            Shutdown(0);
            return;
        }

        // 验证入口：JuniGridSetup.exe --one-click-write <配置文件> <true|false> [输出文件]
        // 真写那一个键（这是装机流程会给用户做的事），但只写你指定的那份文件 ——
        // 用它跑夹具，就能证明"其它键一个不少 / 坏配置不动 / 没有文件时能建"，不碰 %APPDATA%。
        if (e.Args.Length >= 3 && e.Args[0].Equals("--one-click-write", StringComparison.OrdinalIgnoreCase))
        {
            var want = e.Args[2].Equals("true", StringComparison.OrdinalIgnoreCase);
            var ok = InstallConfig.SetOneClickInstallIfAbsent(e.Args[1], want);
            var rep = "one-click-write-if-absent → " + e.Args[1] + " = " + want + " ‖ 写成=" + ok + "\n"
                + (File.Exists(e.Args[1]) ? File.ReadAllText(e.Args[1]) : "(配置文件不存在)");
            try
            {
                var outPath = e.Args.Length >= 4 ? e.Args[3]
                    : Path.Combine(Path.GetTempPath(), "jg-one-click.txt");
                File.WriteAllText(outPath, rep);
            }
            catch { }
            Shutdown(0);
            return;
        }

        // 守卫验证入口：JuniGridSetup.exe --recycle-target <旧位置> <新位置> [输出文件]
        // 只回答"这次会回收哪个目录、还是决定不回收"，一个文件都不碰。
        // 它钉的是"谁把哪个目录交给回收这一步"——2026-10-04 那个 bug 正是这里传错（重读了
        // 已被本次安装改掉的注册表），而组件夹具全绿，所以这条必须单独有入口能验。
        if (e.Args.Length >= 3 && e.Args[0].Equals("--recycle-target", StringComparison.OrdinalIgnoreCase))
        {
            string rt;
            try
            {
                var got = InstallerEngine.RecycleTargetOf(e.Args[1], e.Args[2]);
                rt = "legacy=" + (e.Args[1].Length == 0 ? "(空)" : e.Args[1])
                   + " ‖ target=" + e.Args[2] + " ⇒ " + (got is null ? "不回收" : "回收 " + got);
            }
            catch (Exception ex) { rt = "recycle-target 异常: " + ex; }
            try { Console.Out.WriteLine(rt); Console.Out.Flush(); } catch { }
            try
            {
                var outPath = e.Args.Length >= 4 ? e.Args[3]
                    : Path.Combine(Path.GetTempPath(), "jg-recycle-target.txt");
                File.WriteAllText(outPath, rt);
            }
            catch { }
            Shutdown(0);
            return;
        }

        // 单实例：两个安装器同时往同一个目录写，会留下半新半旧的文件，这道闸要留。
        // 但过闸的人必须有可见结果 —— 要么把已在运行那个实例的窗口拉到前面，要么弹窗讲清楚。
        // 1.2.8 及之前读的是本进程的 App.Current.Windows：那一刻窗口还没造出来，列表恒空，
        // 于是第二次及以后的双击全都「日志记一句就静默退出」，屏上无痕 —— 撞上这条的用户
        // 会把重下载、换右键、用管理员全都试成白按。
        _single = new Mutex(true, "JuniGrid.Setup.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            var (hwnd, pid) = FindOtherInstanceWindow();
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.RestoreAndRaise(hwnd);
                Log($"已有安装器实例在运行 → 拉起旧窗口 pid={pid} hwnd={hwnd}");
            }
            else
            {
                Log("已有安装器实例在运行 → 没找到它的窗口 弹窗告知");
                MessageBox.Show(
                    "这台电脑上已经有一个 JuniGrid 安装程序在运行，但屏幕上找不到它的窗口。\n\n" +
                    "先翻一下任务栏和别的显示器，那个安装窗口可能还开着。\n" +
                    "确实找不到就重启电脑，再双击这个安装包。",
                    "JuniGrid 安装", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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

        // 窗口在这儿显式创建，不走 App.xaml 的 StartupUri：StartupUri 会在上面那条
        // Shutdown() 之后照样把窗口造出来再关掉，用户看到的是「闪一下就没了」。
        MainWindow = new InstallerWindow();
        MainWindow.Show();
        Log("App.OnStartup done");
    }

    /// <summary>找另一个安装器进程的顶层窗口。它在别的进程里，只能按进程名 + 窗口句柄找；
    /// 安装包会被改名发布（JuniGrid-cn-vX.Y.Z-setup.exe），按自己的文件名匹配才不受改名影响。
    /// 对方比我们先起、窗口还在造的途中时等一会儿再看，最多 ~1.2 秒。</summary>
    private static (IntPtr Hwnd, int Pid) FindOtherInstanceWindow()
    {
        var self = Environment.ProcessId;
        var image = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var others = 0;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(image))
            {
                try
                {
                    if (p.Id == self) continue;
                    others++;
                    var h = NativeMethods.FindInstallerWindowOfProcess(p.Id, WindowTitle);
                    if (h != IntPtr.Zero) return (h, p.Id);
                }
                catch (Exception ex) { Log("FindOtherInstanceWindow: " + ex.Message); }
                finally { p.Dispose(); }
            }
            if (others == 0) return (IntPtr.Zero, 0);   // 那个实例已经退场，没东西要拉
            System.Threading.Thread.Sleep(300);
        }
        return (IntPtr.Zero, 0);
    }
}
