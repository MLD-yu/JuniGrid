using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace JuniGridInstaller;

public partial class App : Application
{
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
}
