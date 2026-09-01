using System.Windows;
using JuniGrid.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JuniGrid;

/// <summary>
/// 内置 Nexus 浏览器。免费账户的完整下载流程在这里走完：
/// 登录 Nexus → 打开 mod 页面 → 点「Mod Manager Download」→
/// 页面跳 nxm:// 的瞬间被拦截 → 交给 InstallService 自动下载安装，
/// 全程不出启动器。登录状态保存在 WebView2 用户数据目录，下次免登。
/// </summary>
public partial class NexusBrowserWindow : Window
{
    private readonly string _startUrl;
    private readonly bool _queueMode;
    private bool _ready;

    public NexusBrowserWindow(string startUrl, bool queueMode = false)
    {
        InitializeComponent();
        _startUrl = startUrl;
        _queueMode = queueMode;

        if (_queueMode)
        {
            var q = App.Services?.GetService<UpdateQueueService>();
            if (q is not null)
            {
                q.OnAdvanced += OnQueueAdvanced;
                Closed += (_, _) => q.OnAdvanced -= OnQueueAdvanced;
            }
            UpdateQueueTitle();
        }

        Loaded += async (_, _) => await InitAsync();
    }

    // ---- 更新队列：装完一个 → 自动打开下一个 mod 的文件页 ----
    private void OnQueueAdvanced()
    {
        Dispatcher.Invoke(() =>
        {
            var q = App.Services?.GetService<UpdateQueueService>();
            if (q is null) return;
            if (q.CurrentModId is int next && _ready)
            {
                web.CoreWebView2.Navigate(
                    $"https://www.nexusmods.com/stardewvalley/mods/{next}?tab=files");
            }
            UpdateQueueTitle();
        });
    }

    private void UpdateQueueTitle()
    {
        var q = App.Services?.GetService<UpdateQueueService>();
        if (q is null) return;
        Title = q.CurrentModId is not null
            ? $"更新队列 {q.Done + 1}/{q.Total} —— 请在页面点 Mod Manager Download"
            : $"✅ 队列全部装完（{q.Total} 个）—— 可以关窗口了";
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        try
        {
            await web.EnsureCoreWebView2Async();
            var cwv = web.CoreWebView2;

            // 拦截 nxm:// —— 网页点 Mod Manager Download 最终会跳这个协议
            cwv.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    var installer = App.Services?.GetService<InstallService>();
                    if (installer is not null)
                        _ = installer.HandleNxmLinkAsync(e.Uri);
                    Dispatcher.Invoke(() =>
                        Title = "✅ 已接管下载 —— 去启动器「Mod 管理」页顶部看安装动态");
                }
            };

            // 网页想开新窗口时，就在本窗口打开（比如登录跳转）
            cwv.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                cwv.Navigate(e.Uri);
            };

            cwv.SourceChanged += (_, _) =>
                Dispatcher.Invoke(() => txtUrl.Text = web.Source?.ToString() ?? "");

            web.Source = new Uri(_startUrl);
            _ready = true;
        }
        catch (Exception ex)
        {
            txtUrl.Text = "浏览器初始化失败：" + ex.Message;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_ready && web.CanGoBack) web.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_ready && web.CanGoForward) web.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_ready) web.Reload();
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        UpdateService.OpenUrl(web.Source?.ToString() ?? _startUrl);
    }
}
