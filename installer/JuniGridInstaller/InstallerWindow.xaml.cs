using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JuniGridInstaller;

public partial class InstallerWindow : Window
{
    private readonly InstallerEngine _engine = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string? _lastTargetDir;

    public InstallerWindow()
    {
        App.Log("InstallerWindow..ctor begin");
        InitializeComponent();
        PathBox.Text = InstallerEngine.GetDefaultInstallDir();
        VersionText.Text = "· v" + InstallerEngine.Version;
        SourceInitialized += (_, _) => { App.Log("SourceInitialized"); NativeMethods.TryRoundCorners(this); };
        ContentRendered += (_, _) => App.Log("ContentRendered");
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
        };
        Closed += (_, _) => { App.Log("Window.Closed"); _cts?.Cancel(); };
        Closing += OnWindowClosing;
        App.Log("InstallerWindow..ctor done");
    }

    // v1.0.17：安装进行中（_busy）关窗先弹确认 —— 半途强退会留下半新半旧的文件。
    // 「继续退出」置 _forceClose 再 Close 放行；Closed 里既有的 _cts?.Cancel()
    // 会让引擎在下一个文件边界停下（无害，重跑安装包即可修复）。
    private bool _forceClose;

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_busy || _forceClose) return;
        e.Cancel = true;
        ExitConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void OnForceExit(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    private void OnCancelExit(object sender, RoutedEventArgs e)
        => ExitConfirmOverlay.Visibility = Visibility.Collapsed;

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnAdvanced(object sender, RoutedEventArgs e) => ShowScreen(PathScreen);

    private void OnBack(object sender, RoutedEventArgs e)
    {
        PathHint.Visibility = Visibility.Collapsed;
        ShowScreen(WelcomeScreen);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 JuniGrid 的安装位置",
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            PathBox.Text = dlg.FolderName;
    }

    private void OnInstallNow(object sender, RoutedEventArgs e) => StartInstall();

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (!ValidatePath()) return;
        StartInstall();
    }

    private void OnRetry(object sender, RoutedEventArgs e) => StartInstall();
    private void OnCloseErr(object sender, RoutedEventArgs e) => Close();

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LaunchAfterDone.IsChecked == true && _lastTargetDir is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(_lastTargetDir, "JuniGrid.exe"),
                    WorkingDirectory = _lastTargetDir,
                    UseShellExecute = true,
                });
            }
        }
        catch { }
        Close();
    }

    private bool ValidatePath()
    {
        var dir = Expand(PathBox.Text);
        if (IsValidDir(dir))
        {
            PathBox.Text = dir;
            PathHint.Visibility = Visibility.Collapsed;
            return true;
        }
        PathHint.Visibility = Visibility.Visible;
        return false;
    }

    private static string Expand(string raw) =>
        Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));

    private static bool IsValidDir(string dir)
    {
        try
        {
            if (dir.Length < 4 || !Path.IsPathRooted(dir)) return false;
            if (string.Equals(Path.GetPathRoot(dir), dir, StringComparison.OrdinalIgnoreCase)) return false; // 不允许装到盘根
            Path.GetFullPath(dir);
            return true;
        }
        catch { return false; }
    }

    private async void StartInstall()
    {
        if (_busy) return;
        var dir = Expand(PathBox.Text);
        if (!IsValidDir(dir))
        {
            ShowScreen(PathScreen);
            PathHint.Visibility = Visibility.Visible;
            return;
        }
        PathHint.Visibility = Visibility.Collapsed;
        PathBox.Text = dir;

        _busy = true;
        SetControlsEnabled(false);
        ShowScreen(ProgressScreen);
        SizeText.Visibility = Visibility.Visible;
        PercentText.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        ProgressTrack.Visibility = Visibility.Visible;
        ProgressFill.Visibility = Visibility.Visible;
        DonePanel.Visibility = Visibility.Collapsed;
        StatusText.Foreground = Brushes.White;
        StatusText.Text = "准备中…";
        PercentText.Text = "";
        ErrorPanel.Visibility = Visibility.Collapsed;
        UpdateBar(0);

        _cts = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            StatusText.Text = p.Status;
            PercentText.Text = p.Fraction >= 0.999 ? "100%" : $"{p.Fraction * 100:0}%";
            SizeText.Text = p.TotalBytes > 0
                ? string.Format("{0:0.0} / {1:0.0} MB", p.DoneBytes / 1048576.0, p.TotalBytes / 1048576.0)
                : "";
            UpdateBar(p.Fraction);
        });
        try
        {
            await _engine.InstallAsync(dir, DeskShortcut.IsChecked == true, progress, _cts.Token);
            // 不自动启动：隐藏进度元素，只留「启动 JuniGrid」复选框 + 完成按钮
            SizeText.Visibility = Visibility.Collapsed;
            PercentText.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
            ProgressTrack.Visibility = Visibility.Collapsed;
            ProgressFill.Visibility = Visibility.Collapsed;
            _lastTargetDir = dir;
            DonePanel.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "安装失败：" + ex.Message;
            StatusText.Foreground = (Brush)FindResource("ErrorBrush");
            PercentText.Text = "";
            ErrorPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private void UpdateBar(double fraction)
    {
        var w = ActualWidth <= 0 ? Width : ActualWidth;
        ProgressFill.Width = Math.Max(0, Math.Min(w, w * fraction));
    }

    private void SetControlsEnabled(bool enabled)
    {
        BtnInstallNow.IsEnabled = enabled;
        BtnAdvanced.IsEnabled = enabled;
        BtnInstall.IsEnabled = enabled;
        BtnBack.IsEnabled = enabled;
        BtnBrowse.IsEnabled = enabled;
        PathBox.IsEnabled = enabled;
        DeskShortcut.IsEnabled = enabled;
    }

    private void ShowScreen(UIElement screen)
    {
        foreach (var p in new UIElement[] { WelcomeScreen, PathScreen, ProgressScreen })
        {
            var isTarget = ReferenceEquals(p, screen);
            p.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed;
            if (isTarget)
            {
                p.Opacity = 0;
                p.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            }
        }
        DimOverlay.Visibility = ReferenceEquals(screen, PathScreen) ? Visibility.Visible : Visibility.Collapsed;
    }
}
