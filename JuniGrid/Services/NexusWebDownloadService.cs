using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JuniGrid.Services;

/// <summary>应用内自动下载的结果：<see cref="Url"/> 非空即拿到了一次性 CDN 直链。</summary>
public sealed record NexusWebDownloadResult(string? Url, string? Error,
    IReadOnlyList<string>? Mirrors = null)
{
    public static NexusWebDownloadResult Ok(string url, IReadOnlyList<string>? mirrors = null)
        => new(url, null, mirrors);
    public static NexusWebDownloadResult Fail(string error) => new(null, error);
}

/// <summary>
/// v1.2.10：免费账号的「无感下载」通道（对齐 XDyno Mod Manager 的隐藏窗口方案）。
/// <para>
/// 问题：<c>download_link.json</c> 对免费账号必然 403，而 nxm:// 链接只有用户
/// 在网页上点过「Mod Manager Download」之后才存在 —— 于是每次装 mod 都要把
/// 用户丢进系统浏览器点一下再切回来。
/// </para>
/// <para>
/// 做法：开一个<b>不显示</b>的 WebView2 窗口，直接打开该文件的 N 网文件页
/// （<c>?tab=files&amp;file_id=…</c>），用 CDP 注入一次带 <c>userGesture</c> 的点击
/// 代按网页上那颗 Slow download 按钮，再在 Chromium 真正落盘之前用
/// <c>DownloadStarting</c> 把下载拦下来，取出 CDN 直链交回任务中心 ——
/// 后面的下载/断点续传/安装全走现有管线，用户只看见一个进度条。
/// </para>
/// <para>
/// 为什么不用「让 WebView2 自己下完再装」：那条路拿不到 <c>ResumableDownload</c>
/// 的镜像切换、45 秒停滞看门狗和暂停续传，任务中心的暂停/重试语义会断掉。
/// </para>
/// <para>
/// 三道兜底：
/// ① <b>人机验证</b>（Cloudflare / DDoS-Guard）—— 隐藏窗口里过不去，检测到就把
/// 窗口搬到屏幕中央让用户手点，验证通过后立刻收回屏幕外；这是<b>唯一</b>会让
/// 窗口出现在用户眼前的路径（2026-11-04 用户明确要求：除了人机验证，全程隐藏）。
/// ② <b>未登录</b> —— <b>不</b>弹窗，直接判失败交回调用方。让用户对着一个他看不见
/// 的登录页干等毫无意义；退回「打开文件页」的老路反而能装。
/// ③ <b>任何一步失败</b> —— 返回失败，调用方照旧退回「打开系统浏览器」的老路，
/// 用户至少不会卡死。
/// </para>
/// <para>
/// <b>并发</b>：池子容量是 <b>1</b>，一次只开一个窗口。这个通道只在免费账号上走
/// （Premium 直接用 API 直链，压根不进这里），而 N 网免费账号同一时间只允许一个下载：
/// 多开窗口时只有最先那个能拿到文件，其余会卡在「按钮点过但没有下载事件」直到超时
/// （2026-11-05 实机：批量更新 4 个 → 1 成 3 败；改回串行后逐个成功）。批量更新照样能跑，
/// 只是在门外排队。
/// </para>
/// <para>
/// 独立 UserDataFolder 是硬要求：主界面那个 BlazorWebView 已经占住
/// <c>StoragePaths.WebView2Dir</c>，同一个目录再起一个环境会直接初始化失败。
/// </para>
/// <para>
/// <b>合规说明（写在这里是为了以后有人翻到时不用再猜）</b>：自动按下 Slow download
/// 等于替用户跳过 N 网给免费账号准备的那个下载页（页上有广告位），这与 XDyno 的
/// 隐藏窗口方案同性质，属于灰色地带，可能触及 Nexus 的服务条款。这是本版明确
/// 做出的产品选择；把 <see cref="Enabled"/> 置 false 即可整体关掉，关掉后行为
/// 与之前完全一致（打开系统浏览器 → 用户自己点 Mod Manager Download → nxm 回流）。
/// </para>
/// </summary>
public sealed class NexusWebDownloadService
{
    /// <summary>v1.3.39：账号窗口的入口。根路径经常被 Cloudflare 挡，分区页能过。</summary>
    private const string AccountStartUrl = "https://www.nexusmods.com/stardewvalley";
    private const string AccountStartUrlFallback = "https://www.nexusmods.com/";

    /// <summary>明确没登录时直接开登录页 —— 以前不管登没登录都开游戏分区页，用户得自己在
    /// 页头找那颗小小的「Log in」，找不到就以为内置浏览器坏了（2026-11-07 用户反馈）。</summary>
    private const string LoginStartUrl = "https://users.nexusmods.com/auth/login";
    private static bool _accountRetried;
    /// <summary>v1.3.43：这个账号窗口是「因为撞了登录墙」才打开的 —— 登录成功后自动收掉。
    /// 用户自己点「打开内置网页」进来的不算（他可能只是想看看/换号，不该被自动关掉）。</summary>
    private static bool _closeAfterLogin;
    private static Window? _accountWindow;
    private static WebView2? _accountView;
    private static string? _accountName;
    public string? AccountName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_accountName)) return _accountName;
            try { return File.Exists(AccountNameCachePath) ? File.ReadAllText(AccountNameCachePath).Trim() : null; }
            catch { return null; }
        }
    }
    public event Action<string?>? AccountNameChanged;

    /// <summary>v1.3.42：最近一次取链失败是不是「撞了登录墙」。
    /// 调用方据此改成「打开内置网页让用户登录」，而不是退回打开系统浏览器
    /// —— 新用户没看过更新说明，一上来就被弹到浏览器，等于功能没生效。</summary>
    public bool LastFailureWasLoginWall { get; private set; }

    /// <summary>v1.3.39：网页账号是否已登录。Nexus 新版页头把用户名藏进了弹出菜单，
    /// 拿不到名字时至少要知道「已登录」——否则界面会一直显示「登录」，让人以为没登上。</summary>
    public bool SignedIn
    {
        get
        {
            if (_signedIn) return true;
            // 重启后窗口还没打开过时，靠上次落盘的标记 —— 否则明明登录着又会显示「登录」
            try { return File.Exists(SignedInCachePath); } catch { return false; }
        }
    }
    public event Action<bool>? SignedInChanged;
    private static bool _signedIn;
    /// <summary>v1.3.42：上一次推给界面的登录状态（null = 还没推过）。</summary>
    private static bool? _publishedSignedIn;
    private static string SignedInCachePath => Path.Combine(StoragePaths.NexusWebDir, "signed-in");
    private static string AccountNameCachePath => Path.Combine(StoragePaths.NexusWebDir, "account-name.txt");

    /// <summary>显示使用下载通道同一 profile 的 Nexus 网页，让用户登录或切换账号。</summary>
    /// <param name="closeAfterLogin">true = 这是「登录墙」打开的窗口，登录成功后自动关掉。</param>
    public async Task OpenAccountWindowAsync(bool closeAfterLogin = false)
    {
        _closeAfterLogin = closeAfterLogin;
        AppLog.Info("NexusWeb", "用户请求打开内置 Nexus 网页账号窗口");
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) throw new InvalidOperationException("WPF 界面调度器尚未初始化");
        await dispatcher.InvokeAsync(async () =>
        {
            if (_accountWindow is not null)
            {
                _accountWindow.Left = SystemParameters.WorkArea.Left + 80;
                _accountWindow.Top = SystemParameters.WorkArea.Top + 60;
                _accountWindow.Activate();
                return;
            }
            _accountRetried = false;   // 每次开窗都允许换入口重试一次
            var profile = Path.Combine(StoragePaths.NexusWebDir, "profile");
            Directory.CreateDirectory(profile);
            AppLog.Info("NexusWeb", $"正在初始化网页账号窗口 profile={profile}");
            var env = await HiddenHost.GetSharedEnvironmentAsync(profile);
            var view = new WebView2();
            var root = new System.Windows.Controls.Grid();
            root.Children.Add(view);
            var loading = new System.Windows.Controls.TextBlock
            {
                Text = "正在连接 Nexus…",
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(24),
                Background = System.Windows.Media.Brushes.White,
                Foreground = System.Windows.Media.Brushes.DimGray,
                FontSize = 16,
            };
            root.Children.Add(loading);
            var window = new Window
            {
                Title = "JuniGrid · 内置 Nexus 网页账号", Width = 1180, Height = 880,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = root,
                ShowInTaskbar = true,
            };
            _accountWindow = window;
            _accountView = view;
            window.Closed += (_, _) =>
            {
                _accountWindow = null; _accountView = null;
                try { view.Dispose(); } catch { }
            };
            // 关窗前再探一次：用户可能刚在里面点了 Sign out 就顺手关窗，
            // 2 秒的轮询未必赶得上（v1.3.42）
            window.Closing += async (_, _) =>
            {
                try { if (_accountView is not null) await TryCaptureAccountNameAsync(view); } catch { }
            };
            window.Show();
            if (!view.IsLoaded)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler? handler = null;
                handler = (_, _) =>
                {
                    view.Loaded -= handler;
                    loaded.TrySetResult();
                };
                view.Loaded += handler;
                await loaded.Task;
            }
            await view.EnsureCoreWebView2Async(env);
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            view.CoreWebView2.NavigationStarting += (_, e) =>
                AppLog.Info("NexusWeb", "网页登录窗口开始导航：" + e.Uri);
            view.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    var message = $"Nexus 页面加载失败：{e.WebErrorStatus}\n地址：{view.Source}";
                    AppLog.Warn("NexusWeb", message);

                    // v1.3.39：一次失败不代表网络断了 —— 根路径和分区页在这台机器上的
                    // Cloudflare 待遇不同（2026-11-05 01:35 实测根路径 Unknown）。两个入口都试过再报错。
                    var here = view.Source?.AbsolutePath ?? "/";
                    var next = here.StartsWith("/stardewvalley") ? AccountStartUrlFallback : AccountStartUrl;
                    if (!_accountRetried && !string.Equals(here, new Uri(next).AbsolutePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _accountRetried = true;
                        AppLog.Info("NexusWeb", "换入口重试：" + next);
                        loading.Text = "Nexus 页面没打开，换个入口重试…";
                        loading.Visibility = Visibility.Visible;
                        view.CoreWebView2.Navigate(next);
                        return;
                    }

                    loading.Text = message + "\n请检查网络后关闭此窗口，再点登录 / 切换重试。";
                    loading.Visibility = Visibility.Visible;
                    return;
                }
                AppLog.Info("NexusWeb", "网页登录窗口导航完成：" + view.Source);
                loading.Visibility = Visibility.Collapsed;
                await TryCaptureAccountNameAsync(view);
            };

            // v1.3.39：登录不是「一次导航」就完事 —— 点完登录还要跳转、还要过 Cloudflare 挑战，
            // 甚至登录后是前端路由（未必触发 NavigationCompleted）。只在导航完成那一下抓一次，
            // 结果就是「明明登录了，设置页还写着登录」。窗口开着期间每 2 秒探一次，拿到就停。
            var probeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            probeTimer.Tick += async (_, _) =>
            {
                // v1.3.42：以前「认到名字就停」+「最多 5 分钟」——两条都会让「后来退出登录」
                // 没人发现。现在只要窗口还开着就一直看（每 2 秒一次轻量 JS，代价可以忽略）。
                if (_accountView is null) { probeTimer.Stop(); return; }
                await TryCaptureAccountNameAsync(view);
            };
            window.Closed += (_, _) => probeTimer.Stop();
            probeTimer.Start();

            view.CoreWebView2.ProcessFailed += (_, e) =>
            {
                var message = $"内置浏览器进程异常：{e.ProcessFailedKind}";
                AppLog.Error("NexusWeb", message);
                loading.Text = message;
                loading.Visibility = Visibility.Visible;
            };
            // v1.3.39：先开游戏分区页 —— 隐藏下载窗口一直靠这类页面过 Cloudflare，
            // 而根路径在这台机器上会被挡（实测 Unknown）。
            // v1.3.59：没登录就直接进登录页（否则用户面对首页找不到入口）。
            var startUrl = SignedIn ? AccountStartUrl : LoginStartUrl;
            view.CoreWebView2.Navigate(startUrl);
            AppLog.Info("NexusWeb", "内置 Nexus 网页账号窗口已显示，入口=" + startUrl);
        }).Task.Unwrap();
    }

    /// <summary>v1.3.39：页面里「当前登录账号」的探测脚本。
    /// <para>以前只读 <c>a[href^="/users/"]</c> 的 <b>文字</b>，但 Nexus 页头那个用户链接里放的是
    /// 头像图片，文字是空的 ⇒ 永远取不到名字（点完登录界面还是写着「登录」）。现在把能拿的都拿回来：
    /// href 里的 id、链接文字、头像 alt、title/aria-label、meta，交给 C# 按可靠性挑。</para></summary>
    private const string AccountProbeJs =
        """
        (function () {
          function tidy(s) { return (s || '').replace(/\s+/g, ' ').trim(); }
          var out = { id: '', text: '', alt: '', tip: '', meta: '', href: '', auth: '', rsc: '' };

          // 页头用户入口：老版是 <a href="/users/<id>">，新版是纯图标按钮（只能靠 #login-status 判状态）
          var a = document.querySelector('header a[href^="/users/"], nav a[href^="/users/"]');
          if (!a) {
            var img = document.querySelector('a[href^="/users/"] img');
            if (img) a = img.closest('a');
          }
          if (a) {
            out.href = a.getAttribute('href') || '';
            var m = /\/users\/([^\/?#]+)/.exec(out.href);
            out.id = m ? decodeURIComponent(m[1]) : '';
            out.text = tidy(a.textContent);
            var im = a.querySelector('img');
            out.alt = im ? tidy(im.getAttribute('alt')) : '';
            out.tip = tidy(a.getAttribute('title') || a.getAttribute('aria-label'));
          }
          var meta = document.querySelector('meta[name="username"], meta[name="user"]');
          if (meta) out.meta = tidy(meta.getAttribute('content'));

          // 登录状态：页头有个隐藏的 #login-status（authenticated / guest）
          var st = document.getElementById('login-status');
          out.auth = st ? (st.textContent || '').trim() : '';

          // 页面内嵌的服务端数据里有时直接带着用户名
          try {
            var chunks = [];
            if (self.__next_f && self.__next_f.length) {
              for (var i = 0; i < self.__next_f.length; i++) {
                var c = self.__next_f[i];
                if (c && typeof c[1] === 'string') chunks.push(c[1]);
              }
            }
            var hit = /"(?:userName|username)"\s*:\s*"([^"]{2,40})"/.exec(chunks.join(''));
            out.rsc = hit ? hit[1] : '';
          } catch (e) { out.rsc = ''; }

          return JSON.stringify(out);
        })()
        """;

    /// <summary>v1.3.39：认出「当前登录的网页账号名」，认到就缓存 + 通知界面。
    /// <para>会在窗口打开期间被反复调用（每 2 秒），所以：认到就立刻返回 true；
    /// 认不到时只在与上次探测结果不同才写日志，免得日志被刷满。</para></summary>
    private async Task<bool> TryCaptureAccountNameAsync(WebView2 view)
    {
        // v1.3.42：这里以前有一句「已认到名字就直接返回 true」——于是用户在内置网页里点
        // Sign out 之后，轮询根本不再读页面，界面永远停在「已登录」。现在每次都探，
        // 名字只是顺带的结果。
        try
        {
            var core = view.CoreWebView2;
            if (core is null) return false;

            var raw = await core.ExecuteScriptAsync(AccountProbeJs);
            var json = JsonSerializer.Deserialize<string>(raw) ?? "";
            if (string.IsNullOrWhiteSpace(json)) return false;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Get(string k) => root.TryGetProperty(k, out var v) ? (v.GetString() ?? "").Trim() : "";

            var auth = Get("auth");
            if (auth.Length > 0)
            {
                var signed = string.Equals(auth, "authenticated", StringComparison.OrdinalIgnoreCase);

                // v1.3.42：落盘标记必须**每次都对齐**，不能只在「状态变了」时才写。
                // 反例就是实测踩到的：磁盘上留着上次的 signed-in，进程刚起 _signedIn=false，
                // 这次探测出「未登录」→ 因为 false == false 不走更新分支 → 旧文件永远留着
                // → 界面一直显示「已登录」。
                try
                {
                    if (signed)
                    {
                        Directory.CreateDirectory(StoragePaths.NexusWebDir);
                        File.WriteAllText(SignedInCachePath, "1");
                    }
                    else File.Delete(SignedInCachePath);
                }
                catch { }

                _signedIn = signed;
                if (!signed && !string.IsNullOrWhiteSpace(_accountName))
                {
                    _accountName = null;   // 退出登录了，旧名字不能再挂着
                    try { File.Delete(AccountNameCachePath); } catch { }
                    AccountNameChanged?.Invoke(null);
                }

                // 通知界面的判据是「界面上一次看到的值」，不是「内存里上次的值」。
                // 否则会出现：磁盘旧标记让界面显示「已登录」，探测出未登录时
                // 内存恰好也是 false（进程刚起）→ 判定"没变化"→ 界面没人刷新。
                if (_publishedSignedIn != signed)
                {
                    _publishedSignedIn = signed;
                    AppLog.Info("NexusWeb", signed ? "网页账号状态：已登录" : "网页账号状态：未登录");
                    SignedInChanged?.Invoke(signed);
                    // v1.3.43：刚登录成功就把账号窗口收掉 —— 用户点完登录不用再手动关一次。
                    // 留 1.2 秒让他看见页面已经变成登录态，不至于以为"点了就消失"。
                    if (signed && _closeAfterLogin)
                    {
                        _closeAfterLogin = false;
                        CloseAccountWindowSoon();
                    }
                }
            }

            if (_signedIn && !string.IsNullOrWhiteSpace(_accountName)) return true;

            var id = Get("id");
            var text = Get("text");
            var alt = Get("alt");
            var tip = Get("tip");
            var meta = Get("meta");

            string? name = null;
            // Nexus 用户名不含空格；带空格的都是菜单文案（如 "account_circle My profile"）
            if (text.Length is > 1 and <= 40 && !text.Contains(' ') && text != id) name = text;
            else if (alt.Length > 0 && alt != id) name = alt;
            else if (tip.Length > 0 && tip != id) name = tip;
            else if (meta.Length > 0) name = meta;
            else if (id.Length > 0 && !id.All(char.IsDigit)) name = id;

            // 页面内嵌的服务端数据里常常直接带着用户名
            if (name is null)
            {
                var rsc = Get("rsc");
                if (rsc.Length > 1 && rsc.Length <= 40) name = rsc;   // 页面数据里直接带着用户名时用它
            }

            // 页头链接一般只给 memberId（纯数字）—— 去个人主页把用户名捞出来
            if (string.IsNullOrWhiteSpace(name))
            {
                if (json != _lastProbeLog)
                {
                    _lastProbeLog = json;
                    AppLog.Info("NexusWeb", $"网页账号未识别：{json}");
                }
                return false;
            }

            _accountName = name;
            try
            {
                Directory.CreateDirectory(StoragePaths.NexusWebDir);
                File.WriteAllText(AccountNameCachePath, name);
            }
            catch { }
            AppLog.Info("NexusWeb", $"识别到网页账号：{name}（探测：{json}）");
            AccountNameChanged?.Invoke(name);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("NexusWeb", "识别网页账号失败：" + ex.Message);
            return false;
        }
    }

    private static string? _lastProbeLog;

    /// <summary>v1.3.43：登录成功后延迟一点收起账号窗口。</summary>
    private static void CloseAccountWindowSoon()
    {
        var w = _accountWindow;
        var dispatcher = Application.Current?.Dispatcher;
        if (w is null || dispatcher is null)
        {
            // 账号窗口已经关了 / 或 WPF 应用没起来 —— 记录一下，免得以后又"没反应也没日志"
            AppLog.Info("NexusWeb", $"登录成功，但不关窗：window={(w is null ? "null" : "ok")} "
                + $"dispatcher={(dispatcher is null ? "null" : "ok")}");
            return;
        }
        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                AppLog.Info("NexusWeb", "登录成功，1.2 秒后关闭内置网页");
                await Task.Delay(1200);
                if (w.IsVisible) w.Close();
                AppLog.Info("NexusWeb", "登录成功，已自动关闭内置网页");
            }
            catch (Exception ex)
            {
                AppLog.Warn("NexusWeb", "自动关闭内置网页失败：" + ex.Message);
            }
        });
    }

    /// <summary>只清除内置 Nexus profile 的 Cookie，不影响 JuniGrid API token。</summary>
    public async Task LogoutWebAccountAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (_accountView is null) await OpenAccountWindowAsync();
        await dispatcher.InvokeAsync(async () =>
        {
            if (_accountView is null) throw new InvalidOperationException("内置 Nexus 网页窗口尚未准备好");
            _accountView.CoreWebView2.CookieManager.DeleteAllCookies();
            _accountName = null;
            _signedIn = false;
            try { File.Delete(AccountNameCachePath); File.Delete(SignedInCachePath); } catch { }
            AccountNameChanged?.Invoke(null);
            SignedInChanged?.Invoke(false);
            _accountView.CoreWebView2.Navigate("https://www.nexusmods.com/");
        }).Task.Unwrap();
    }

    /// <summary>总开关。关掉后 <see cref="TryGetDirectUrlAsync"/> 立即失败，
    /// 调用方自动退回打开系统浏览器的老路。默认开启（这是本版的产品选择）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>窗口池容量 = 同时最多几个隐藏窗口并行。
    /// <para>2026-11-04：从 1 提到 3。以前全局只允许一个窗口，批量更新十几个 mod 就是
    /// 「一个一个排队 × 每个十几秒」，用户看到的是「点了更新半天不动」。现在免费账号也能
    /// 三路并行（和 Premium 的下载并发一致）。再多意义不大：瓶颈在网络和 N 网限速，
    /// 而且每多一个窗口就多一个 Chromium 渲染进程。</para></summary>
    /// <summary>池子容量 = 同时最多几个隐藏窗口。
    /// <para><b>必须是 1</b>：这条通道只在免费账号上走，而 N 网免费账号一次只允许一个下载。
    /// 2026-11-04 曾经提到 3（理由写的是"免费账号也能三路并行，和 Premium 一致"），
    /// 2026-11-05 实机打脸：批量更新 4 个 mod，只有最先那个拿到直链，另外 3 个分别卡在
    /// 「60 秒没等到按钮」和「点了按钮但 100 秒没有下载事件」，最后全部退回开网页。</para></summary>
    private const int PoolSize = 1;

    private static readonly NexusDownloadSlotGate Gate = new(PoolSize);
    private static readonly NexusFetchSlotRegistry FetchSlots = new();

    /// <summary>隐藏窗口里截到的 nxm:// 链接 —— 进程内直送，不经过 Windows 协议处理器。
    /// <para>为什么需要它：Nexus 的「Slow download」在 <c>nmm=1</c> 页面里实际是导航到
    /// <c>nxm://…</c>。正常路径是 WebView2 把外部协议交给 Windows，Windows 再拉起本程序、
    /// 由单实例管道回流。但 WebView2 对外部协议默认会弹一个「是否打开」的确认框，而隐藏
    /// 窗口在屏幕外，那个框没人点得到 —— 链接就无声消失。2026-11-06 实机：同一个文件在
    /// Edge 里点必出 nxm://，应用内有时拿不到，任务行就卡在「正在从 Nexus 页面获取下载
    /// 链接…」直到超时。这里在进程内截住同一条链接，直接交给既有回流管线。</para>
    /// <para>订阅方：MainWindow 建好 DI 后挂到 <c>App.DispatchNxm</c>。</para></summary>
    public static event Action<string>? NxmLinkDetected;

    /// <summary>WebView2 环境按 profile 缓存 —— <b>同一个 user data folder 在一个进程里
    /// 只能建一个环境</b>，再建一次直接 <c>0x8007139F</c>。多窗口要共享它。</summary>
    private static readonly SemaphoreSlim EnvGate = new(1, 1);
    private static readonly Dictionary<string, CoreWebView2Environment> EnvCache = new();

    /// <summary>正常路径（页面加载 + 点按钮 + 5 秒倒计时）给 120 秒 —— N 网页面重
    /// （React + 大量图片 + CDN 上抖），首次进还要握手 Cloudflare，90 秒实测偏紧。</summary>
    private static readonly TimeSpan PassiveTimeout = TimeSpan.FromSeconds(120);

    /// <summary>已经把窗口交给用户（验证码/登录）之后，等他 5 分钟。</summary>
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(5);

    /// <summary>点完 Slow download 之后等下载事件的上限。
    /// <para>原先是 45 秒，实测偏短：窗口在屏幕外时 Chromium 仍可能节流 <c>setTimeout</c>，
    /// N 网那 5 秒倒计时会被拉成 60 秒，45 秒就判失败等于每单都冤枉它一次。100 秒能完整
    /// 覆盖一个节流周期，而且这时点已经每 3 秒扫一次页面锚点，正常情况几秒就返回了。</para></summary>
    private const int WaitAfterClickSeconds = 100;

    /// <summary>点完按钮、按钮又从 DOM 里消失之后，多少秒还没下载事件就重载页面重来。
    /// <para>2026-11-06 实机：这条通道的失败不是「N 网不给」，而是那一下点在了还没接上
    /// React 的服务端渲染按钮上 —— 页面随后重建下载面板，按钮消失，什么都没发生。
    /// nxm 回流正常 0.2~1 秒就到；12 秒还没动静就重载重试，比干等满 100 秒有用得多。</para></summary>
    private const int ReloadAfterClickSeconds = 12;

    /// <summary>重载页面重试几次。两次足够：真遇到 Cloudflare/限流时再多也是白搭。</summary>
    private const int MaxReloads = 2;

    /// <summary>忍耐期：页面刚打开的前 10 秒不因「登录」判定去弹窗。
    /// 实测 N 网首屏经常先给一张 Cloudflare 中间页（Just a moment…），3~8 秒才自己放行；
    /// 太早 Reveal 会让用户对着一个下一秒就消失的挑战页发呆。
    /// <para>2026-11-08：这个宽限<b>不再压人机验证</b>（见 <see cref="ChallengeRevealDelaySeconds"/>）——
    /// 验证页一旦真卡住，用户就得多等这 10 秒，纯属白等。</para></summary>
    private const int GraceSeconds = 10;

    /// <summary>验证页卡住时重载页面的次数。
    /// <para>控制台实测（同一 profile、同一参数、离屏无焦点）8 秒就能过 —— 卡住多半是单次
    /// 挑战的死循环，重载一次拿到的就是新挑战。</para>
    /// <para>v1.3.61 从 2 降到 1：每次重试要花 ~33 秒（等 25s + 清 cookie + 重载），两次就是
    /// 一分半，而这条通道在这种网络环境下真的过不去（2026-11-07 03:39 实机：3 轮循环后照样
    /// 交浏览器）。留一次兜住「偶发死循环」，剩下的时间交给浏览器那条稳的路。</para></summary>
    private const int MaxChallengeReloads = 1;

    /// <summary>看到验证页之后多久把窗口交给用户。
    /// <para>2026-11-08 22:44（用户反馈「人机验证窗口出现得太慢」）：以前这里是两道闸
    /// —— 10 秒忍耐期（<see cref="GraceSeconds"/>）+ 8 秒「完全静默」—— 实机日志
    /// <c>18.7s 开始取链 → 20.1s 探到验证页 → 37.7s 窗口才出现</c>，中间 19 秒用户对着
    /// 什么都没有的界面，只能以为卡死。现在只留一个去抖：挑战页连续存在 1.5 秒就弹窗，
    /// 既避开一闪而过的 CF 中间页，又不至于让人空等。</para>
    /// <para>原来那 8 秒静默的用意（别用重探针脚本打断 CF 的挑战）没丢 —— 挪到弹窗<b>之后</b>，
    /// 由 <see cref="HiddenHost.Reveal"/> 设一段静默期，那时用户已经在看着页面了。</para>
    /// <para>验证页上一闪而过的情况仍然不弹：探针判定挑战页消失后会立刻收窗（连续 3 次探不到）。</para></summary>
    private const double ChallengeRevealDelaySeconds = 1.5;

    /// <summary>验证窗口交到用户手上之后，给他多久自己过。这段时间里不重载、不清 cookie、
    /// 也不偷偷转浏览器 —— 他正在看着那个转圈的页面，任何自作主张都会把页面换掉。
    /// 45 秒足够：手动过一次 CF 托管挑战通常十几秒，卡到 45 秒基本是真过不去。</summary>
    private const int ChallengeManualSeconds = 45;

    private const string GameDomain = "stardewvalley";

    /// <summary>
    /// 取 <paramref name="modId"/> 的 <paramref name="fileId"/> 这个文件的一次性直链。
    /// 失败返回 <see cref="NexusWebDownloadResult.Error"/>，由调用方决定退回哪条路。
    /// <para><paramref name="report"/> 会收到「正在打开 / 正在按按钮 / 请完成验证」这类
    /// 过程消息，直接串进任务中心的步骤行。</para>
    /// </summary>
    public async Task<NexusWebDownloadResult> TryGetDirectUrlAsync(
        int modId, long fileId,
        Action<string, double?, double?>? report = null,
        CancellationToken ct = default,
        TaskItem? task = null)
    {
        if (!Enabled) return NexusWebDownloadResult.Fail("应用内自动下载已关闭");
        if (modId <= 0 || fileId <= 0) return NexusWebDownloadResult.Fail("modId/fileId 无效");
        LastFailureWasLoginWall = false;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return NexusWebDownloadResult.Fail("界面尚未初始化");

        var entered = false;
        NexusFetchSlot? slot = null;
        var ownsSlot = false;
        try
        {
            // 先登记 (mod,file)，再等全局窗口名额；排队期间到达的 nxm:// 也能并入原任务。
            var reservation = FetchSlots.Reserve(modId, fileId);
            slot = reservation.Slot;
            ownsSlot = reservation.IsOwner;
            if (!ownsSlot) return await slot.Completion.Task.WaitAsync(ct);

            using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var gateWait = Gate.WaitAsync(task, TimeSpan.FromSeconds(150), gateCts.Token);
            if (await Task.WhenAny(gateWait, slot.Completion.Task) == slot.Completion.Task)
            {
                // nxm 回流已经递来直链，不必再等一个隐藏窗口名额。
                gateCts.Cancel();
                try { entered = await gateWait; }
                catch (OperationCanceledException) { }
                return await slot.Completion.Task;
            }

            entered = await gateWait;
            if (!entered)
            {
                var timeout = NexusWebDownloadResult.Fail("另一个应用内下载页正在使用中，稍后再试");
                FetchSlots.Complete(modId, fileId, slot, timeout);
                return timeout;
            }

            var host = new HiddenHost(modId, fileId, report, slot)
            {
                LoginWallHit = () =>
                {
                    LastFailureWasLoginWall = true;
                    // v1.3.43：给这条任务打标记 —— 用户在内置网页登录成功后自动重跑，
                    // 不用回任务页手动点重试。
                    if (task is not null) task.NeedsLoginRetry = true;
                    try { _ = OpenAccountWindowAsync(closeAfterLogin: true); }
                    catch (Exception ex) { AppLog.Warn("NexusWeb", "打开登录窗口失败：" + ex.Message); }
                },
            };
            // 窗口与 WebView2 全是 STA 亲和对象：整段跑在 UI 线程上。
            // 外层 await 拿到的是 Task<Result>，再 await 一次取值。
            var result = await (await dispatcher.InvokeAsync(() => host.RunAsync(ct)));
            FetchSlots.Complete(modId, fileId, slot, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            var canceled = NexusWebDownloadResult.Fail("已取消");
            if (slot is not null && ownsSlot) FetchSlots.Complete(modId, fileId, slot, canceled);
            return canceled;
        }
        catch (Exception ex)
        {
            AppLog.Warn("NexusWeb", "应用内自动下载异常：" + ex);
            var failed = NexusWebDownloadResult.Fail(ex.Message);
            if (slot is not null && ownsSlot) FetchSlots.Complete(modId, fileId, slot, failed);
            return failed;
        }
        finally
        {
            if (slot is not null && ownsSlot) FetchSlots.Remove(modId, fileId, slot);
            if (entered) Gate.Release();
        }
    }

    public bool IsQueued(TaskItem task) => Gate.IsQueued(task);

    // ══════════════════════════════════════════════════════════════════
    // nxm 回流合流（同一 mod 只该有一行任务）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>正在为某个 (mod, file) 等直链的隐藏窗口。
    /// <para>为什么要它：文件页必须带 <c>&amp;nmm=1</c> 才会渲染下载面板，而 N 网那个面板
    /// 会顺手触发一次 <c>nxm://</c> —— Windows 把链接转回本进程，走「网页一键安装」那条路，
    /// 于是同一个 mod 同时冒出「更新 Mod「X」」和「下载并安装 X」两行任务（2026-11-04 用户反馈：
    /// 一个 mod 两个任务）。这里登记在跑的取链请求，nxm 回流时先问一句是不是自己撂的。</para></summary>
    /// <summary>这个 (mod, file) 现在有人在等直链吗？窗口可能还没开（排队阶段就先占位了）。
    /// </summary>
    public static bool HasPending(int modId, long fileId)
        => FetchSlots.HasPending(modId, fileId);

    /// <summary>把 nxm 回流解析出的真直链交给正在等它的那个槽位。
    /// 返回 true = 已接走（调用方不要再建任务）。</summary>
    public static bool TryDeliver(int modId, long fileId, string? directUrl,
        IReadOnlyList<string>? mirrors = null)
        => FetchSlots.TryDeliver(modId, fileId, directUrl, mirrors);

    // ══════════════════════════════════════════════════════════════════
    // 隐藏窗口本体
    // ══════════════════════════════════════════════════════════════════

    /// <summary>一次取链接的全过程。构造 → RunAsync → 自己 Close，不做成长期存活的服务。</summary>
    private sealed class HiddenHost
    {
        private readonly int _modId;
        private readonly long _fileId;
        private readonly Action<string, double?, double?>? _report;

        /// <summary>v1.3.42：撞登录墙时通知外层 —— 由外层置「本次失败是登录墙」并打开内置网页。
        /// HiddenHost 是嵌套类，拿不到外层实例，所以走回调。</summary>
        public Action? LoginWallHit { get; set; }

        private Window? _window;
        private WebView2? _view;
        private CoreWebView2? _core;

        /// <summary>拦到直链 / 用户关窗（null）都会让它完成。
        /// <para>槽位拥有直链任务源，这里只引用它 ——
        /// 占位（槽位）与开窗（本类）因此解耦，nxm 回流能在排队阶段就认领。</para></summary>
        private readonly TaskCompletionSource<string?> _urlTcs;
        /// <summary>本次取链所属的槽位（见 <see cref="Reserve"/>）。</summary>
        private readonly NexusFetchSlot _slot;

        private string? _revealedHint;
        private string? _lastProbeSig;
        private bool _closedByUser;
        private DateTime _lastClickUtc = DateTime.MinValue;
        /// <summary>上一次从页面里捞下载锚点的时间 —— 每 3 秒最多扫一次 DOM，避免每轮都扫。</summary>
        private DateTime _lastHarvestUtc = DateTime.MinValue;
        /// <summary>同一条 nxm 链接会从「外部协议」和「网络层」各来一次，只转交一次。</summary>
        private int _nxmRaised;
        /// <summary>点过按钮却没反应时重载页面的次数（见 <see cref="ReloadAfterClickSeconds"/>）。</summary>
        private int _reloads;
        /// <summary>连续多少次探不到挑战页 —— 用来给「验证已通过」的收窗去抖。</summary>
        private int _cfMisses;
        /// <summary>这次运行里见过人机验证页吗 —— 失败时用来把结论说得更准。</summary>
        private bool _sawChallenge;
        /// <summary>「正在自动校验，不需要操作」这句提示发过没有 —— 别每轮都往任务行推一遍。</summary>
        private bool _autoHintSent;
        /// <summary>当前页面是不是停在验证页上 —— 导航失败日志据此标注「（验证期间）」。</summary>
        private bool _challengeActive;
        /// <summary>验证过不去时，是否已经把文件页交给系统浏览器等 nxm 回流。</summary>
        private bool _browserHandoff;
        /// <summary>验证页卡住后已经重载过几次（见 <see cref="MaxChallengeReloads"/>）。</summary>
        private int _challengeReloads;
        /// <summary>本轮第一次探到验证页的时刻 —— 弹窗去抖用（见 <see cref="ChallengeRevealDelaySeconds"/>）。
        /// 挑战页消失或重载页面时清空，下一轮重新计时。</summary>
        private DateTime? _challengeSeenAtUtc;
        /// <summary>验证窗口是什么时候交到用户手上的（用户自己过的计时起点）。</summary>
        private DateTime _challengeRevealAtUtc;

        /// <summary>CF 的固定 cookie 名。只删这些（登录态原样保留）—— 见 <see cref="ClearCfCookiesAsync"/>。</summary>
        private static readonly string[] CfCookieNames =
            ["cf_clearance", "__cf_bm", "__cf_chl_2fa", "__cf_chl_prog", "__cf_chl_rc_m",
             "__cf_chl_js_t", "__cf_chl_tk", "__cfduid", "__cf_waitlist"];

        /// <summary>清掉 profile 里 Cloudflare 的验证 cookie。2026-11-07 03:16 实测：被污染的
        /// cf_clearance/__cf_bm 会让托管挑战永远不放行；清掉后同一 profile 7 秒通过。
        /// 走 CDP 的 Network.deleteCookies，逐个来，失败不炸。
        /// <para>2026-11-07 03:35 实机日志出现过「CDP 调用全部失败」：<c>Network.deleteCookies</c>
        /// 只给 <c>name</c> 会被判参数非法（缺 <c>url</c>/<c>domain</c>），所以这里带上
        /// <c>url</c>，并把第一个异常记下来 —— 不然只能看到「全部失败」，没法判断是参数问题
        /// 还是内核/通道真的废了。</para></summary>
        private async Task ClearCfCookiesAsync()
        {
            if (_core is null) return;
            var cleared = 0;
            string? firstError = null;
            foreach (var name in CfCookieNames)
            {
                try
                {
                    await _core.CallDevToolsProtocolMethodAsync("Network.deleteCookies",
                        JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["url"] = "https://www.nexusmods.com/",
                        }));
                    cleared++;
                }
                catch (Exception ex) { firstError ??= ex.Message; }
            }
            AppLog.Info("NexusWeb", cleared > 0
                ? $"已清除 CF 验证 Cookie（{cleared}/{CfCookieNames.Length} 个，含 cf_clearance/__cf_bm）"
                : $"清除 CF Cookie 没有生效（CDP 调用全部失败：{firstError ?? "未知"}）");
        }
        /// <summary>静默窗口到期时间：这段时间内不做任何探针注入（见 PumpAsync 里的说明）。</summary>
        private DateTime _quietUntilUtc = DateTime.MinValue;
        private string? _lastError;

        public HiddenHost(int modId, long fileId, Action<string, double?, double?>? report, NexusFetchSlot slot)
        {
            _modId = modId;
            _fileId = fileId;
            _report = report;
            _slot = slot;
            _urlTcs = slot.DirectUrl;
        }

        /// <summary>
        /// 文件页 + <c>&amp;nmm=1</c>。这个参数是<b>实测必需</b>：不带它时页面只渲染文件列表，
        /// 那颗 Slow download 按钮根本不在 DOM 里；带上它 N 网会直接弹出下载面板
        /// （标题 + Free / Slow download），按钮才出现。旧版（XDyno 那个年代）不需要，
        /// 现在必须带 —— 否则整条链路卡在「找不到按钮」。
        /// </summary>
        private string PageUrl =>
            $"https://www.nexusmods.com/{GameDomain}/mods/{_modId}?tab=files&file_id={_fileId}&nmm=1";

        public async Task<NexusWebDownloadResult> RunAsync(CancellationToken ct)
        {
            try
            {
                await CreateUiAsync(ct);
                ct.ThrowIfCancellationRequested();

                AppLog.Info("NexusWeb", $"开始获取直链 mod={_modId} file={_fileId}");
                _report?.Invoke("正在应用内打开 Nexus 文件页…", null, null);
                _core!.Navigate(PageUrl);

                var url = await PumpAsync(ct);
                if (!string.IsNullOrEmpty(url))
                    return NexusWebDownloadResult.Ok(url, _slot.Mirrors);
                var why = _closedByUser ? "已取消" : _lastError ?? "没能在 Nexus 页面拿到下载链接";
                AppLog.Warn("NexusWeb", $"获取直链失败：{why}");
                return NexusWebDownloadResult.Fail(why);
            }
            catch (OperationCanceledException)
            {
                return NexusWebDownloadResult.Fail("已取消");
            }
            catch (Exception ex)
            {
                AppLog.Warn("NexusWeb", "隐藏下载窗口失败：" + ex);
                return NexusWebDownloadResult.Fail("应用内下载页启动失败：" + ex.Message);
            }
            finally
            {
                Close();
            }
        }

        /// <summary>这个窗口已经拿到结果了吗？</summary>
        public bool Finished => _urlTcs.Task.IsCompleted;

        // --------------------------------------------------------------
        // 建窗
        // --------------------------------------------------------------

        private async Task CreateUiAsync(CancellationToken ct)
        {
            // 独立 profile：与主界面 BlazorWebView 的 StoragePaths.WebView2Dir 分开。
            // 好处是 N 网的登录态只活在这一个目录里，清缓存/换账号互不影响。
            var profile = Path.Combine(StoragePaths.NexusWebDir, "profile");
            Directory.CreateDirectory(profile);

            _view = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            const double offW = 1180, offH = 880;
            _window = new Window
            {
                Title = "JuniGrid · Nexus",
                Width = offW,
                Height = offH,
                WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,     // 全程 false：中途改它会重建窗口句柄，WebView2 内容会丢
                ShowActivated = false,     // 不抢焦点
                WindowStartupLocation = WindowStartupLocation.Manual,
                // 先丢到可视区之外。必须是「显示着但看不见」，不能用 Visibility.Hidden ——
                // 未显示的窗口里 WebView2 不会创建内核。
                Left = SystemParameters.VirtualScreenLeft - offW - 4000,
                Top = SystemParameters.VirtualScreenTop - offH - 4000,
                Content = _view,
            };
            _window.Closed += (_, _) =>
            {
                // 用户手关窗口 = 放弃这次自动获取（不是「页面出错」），语义要分清
                if (!_urlTcs.Task.IsCompleted) _closedByUser = true;
                _urlTcs.TrySetResult(null);
            };
            _window.Show();

            if (!_view.IsLoaded)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler? handler = null;
                handler = (_, _) =>
                {
                    _view.Loaded -= handler;
                    loaded.TrySetResult();
                };
                _view.Loaded += handler;
                await loaded.Task;
            }

            // 2026-11-06 21:31 实机：环境就绪后内核初始化无征兆挂死（33 秒+ 没有「内核就绪」，
            // 期间任务行一动不动，用户只能重启应用）。给它 45 秒上限：到点判失败，
            // 调用方照旧退回「打开系统浏览器」的老路，别让用户对着假死干等。
            // 注意：超时后底下那次 EnsureCoreWebView2Async 还在后台跑，本进程内的
            // EnvGate 可能被它占着 —— 后续重试同样会走到这个超时再兜底，重启后恢复。
            var init = EnsureIsolatedWebViewAsync(profile);
            if (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(45), ct)) != init)
            {
                ct.ThrowIfCancellationRequested();   // 取消引起的先行退场，走「已取消」语义
                throw new TimeoutException("内置浏览器内核初始化超时（45 秒）");
            }
            await init;   // WhenAny 只看谁先完成、不解包异常：失败要在这里照常炸出来
            _core = _view.CoreWebView2;
            AppLog.Info("NexusWeb", $"内核就绪 {_core.Environment?.BrowserVersionString} profile={profile}");

            var s = _core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsSwipeNavigationEnabled = false;

            _core.DownloadStarting += OnDownloadStarting;
            _core.NewWindowRequested += OnNewWindowRequested;
            _core.NavigationStarting += OnNavigationStarting;
            _core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            await HookNetworkAsync();
            _core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    _lastError = $"页面加载失败（{e.WebErrorStatus}）";
                    // 验证期间页面被反复掐断是「卡住」的信号之一 —— 日志里标出来，
                    // 配合 chunk 里那句「放弃应用内通道」能把整条时间线讲清楚。
                    AppLog.Warn("NexusWeb", $"导航失败：{e.WebErrorStatus}（{_core?.Source}）"
                        + (_challengeActive ? "（验证期间）" : ""));
                }
            };
        }

        /// <summary>
        /// 取这个 profile 的 WebView2 环境（首次创建，之后走缓存），再用它初始化控件。
        /// <para><b>为什么不能直接 <c>EnsureCoreWebView2Async()</c></b>：进程里已经有一个
        /// 「默认环境」了 —— 主界面那个 BlazorWebView 建的，用户数据目录是
        /// <c>StoragePaths.WebView2Dir</c>；更麻烦的是 <c>MainWindow</c> 还把
        /// <c>WEBVIEW2_USER_DATA_FOLDER</c> 设成了同一个目录（进程级环境变量，WebView2
        /// 在建环境时读它）。于是第二个控件无论怎么写 <c>CreationProperties.UserDataFolder</c>，
        /// 都会被这个变量拽回主界面那个目录 → 和已在用的环境撞车 →
        /// <c>CreateCoreWebView2ControllerAsync</c> 抛 <c>0x8007139F</c>
        /// （ERROR_INVALID_STATE，「组或资源的状态不是执行请求操作的正确状态」）。
        /// 2026-11-04 01:50 实机必现（mod 54988 / file 189797）。</para>
        /// <para><b>为什么环境要缓存</b>：池子里三个控件必须共享同一个环境。同一个
        /// user data folder 第二次 <c>CreateAsync</c> 同样报 0x8007139F —— 多窗口共享
        /// 一个环境是官方支持的多标签用法，各控件仍有独立的 <c>DownloadStarting</c>。</para>
        /// </summary>
        private async Task EnsureIsolatedWebViewAsync(string profile)
        {
            var env = await GetSharedEnvironmentAsync(profile);
            await _view!.EnsureCoreWebView2Async(env);
        }

        internal static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync(string profile)
        {
            lock (EnvCache) { if (EnvCache.TryGetValue(profile, out var hit)) return hit; }

            await EnvGate.WaitAsync();
            try
            {
                lock (EnvCache) { if (EnvCache.TryGetValue(profile, out var hit)) return hit; }

                // 隐藏/离屏窗口在 Chromium 眼里就是「被遮挡 / 不可见」→ 降帧、停渲染，
                // 更要命的是**定时器节流**：N 网点完 Slow download 后那 5 秒倒计时靠
                // setTimeout 走，被节流后直接变成「60 秒才跳一次」，于是页面一直停在
                // 倒计时上、下载事件永远不来（2026-11-04 04:18 实测：ready → 点击 →
                // 43 秒零日志 → 我们 45 秒超时判失败，用户看到的就是「需要 Premium」）。
                // 这一串就是 Electron 那边 backgroundThrottling:false 的等价物。
                // v1.3.42：调试口。设了 JUNIGRID_NEXUS_CDP_PORT 才给这组 WebView2 加远程调试端口，
                // 用来真机核对「账号窗口里退出登录后状态能不能跟着变」；不设变量时参数与以前完全一致。
                var nexusCdp = Environment.GetEnvironmentVariable("JUNIGRID_NEXUS_CDP_PORT");
                var cdpArg = int.TryParse(nexusCdp, out var cdpPort) && cdpPort > 0
                    ? $"--remote-debugging-port={cdpPort} "
                    : "";
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = cdpArg
                        + "--disable-background-timer-throttling "
                        + "--disable-backgrounding-occluded-windows "
                        + "--disable-renderer-backgrounding "
                        + "--disable-ipc-flooding-protection "
                        + "--disable-features=CalculateNativeWinOcclusion,"
                        + "IntensiveWakeUpThrottling,ThrottleDisplayNoneAndVisibilityHiddenCrossOriginIframes",
                };

                // ② 在这一个 await 期间把那个进程级变量临时摘掉再还原 —— 它只在建环境
                // 那一刻被读取，主界面自己的环境早已建好，摘一下不影响它。
                var saved = Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
                try
                {
                    Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", null);
                    CoreWebView2Environment env;
                    try
                    {
                        env = await CoreWebView2Environment.CreateAsync(null, profile, options);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("NexusWeb", $"建 WebView2 环境失败 profile={profile} hresult=0x{ex.HResult:X8}：{ex.Message}");
                        throw;
                    }
                    lock (EnvCache) EnvCache[profile] = env;
                    AppLog.Info("NexusWeb", $"WebView2 环境就绪 profile={profile} {env.BrowserVersionString}");
                    return env;
                }
                finally
                {
                    Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", saved);
                }
            }
            finally { EnvGate.Release(); }
        }

        private void Close()
        {
            try
            {
                if (_core is not null)
                {
                    _core.DownloadStarting -= OnDownloadStarting;
                    _core.NewWindowRequested -= OnNewWindowRequested;
                    _core.NavigationStarting -= OnNavigationStarting;
                    _core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
                    UnhookNetwork();
                }
            }
            catch { /* 内核已销毁 */ }
            try { _view?.Dispose(); } catch { }
            try { _window?.Close(); } catch { }
            _view = null;
            _core = null;
            _window = null;
        }

        // --------------------------------------------------------------
        // 事件：拦下载 / 拦弹窗
        // --------------------------------------------------------------

        // ── 网络层兜底 ────────────────────────────────────────────────
        // 为什么还要这一层：DownloadStarting 只在「Chromium 决定落盘」时才来。
        // 而 N 网给免费账号的下载落点常常是「一个 302 到 CDN 的请求」，某些组合下
        // （顶层导航到二进制被静默取消、下载被自动下载策略拦掉）事件根本不来 ——
        // 这时页面上什么都没发生，我们只能干等到超时。
        // 打开 Network 域后，只要那个请求真的发出去过，responseReceived 里就带着
        // 最终 URL（已经跟完 302），直接拿来用，不依赖浏览器是否落盘。
        private CoreWebView2DevToolsProtocolEventReceiver? _netRecv;
        private CoreWebView2DevToolsProtocolEventReceiver? _netReqRecv;

        private async Task HookNetworkAsync()
        {
            if (_core is null) return;
            try
            {
                _netRecv = _core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
                _netRecv.DevToolsProtocolEventReceived += OnNetworkResponse;
                // 请求层再挂一只：Nexus 的 Slow download 会发起 nxm:// 导航，它先出现在
                // requestWillBeSent 里，然后才轮到「外部协议」那道闸。外部协议在隐藏窗口里
                // 可能被确认框挡住，请求层这条是拿回链接的第二条证据（见 NxmLinkDetected）。
                _netReqRecv = _core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
                _netReqRecv.DevToolsProtocolEventReceived += OnNetworkRequest;
                await _core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            }
            catch (Exception ex) { AppLog.Warn("NexusWeb", "挂网络监听失败：" + ex.Message); }
        }

        private void UnhookNetwork()
        {
            try
            {
                if (_netRecv is not null) _netRecv.DevToolsProtocolEventReceived -= OnNetworkResponse;
            }
            catch { }
            _netRecv = null;
            try
            {
                if (_netReqRecv is not null) _netReqRecv.DevToolsProtocolEventReceived -= OnNetworkRequest;
            }
            catch { }
            _netReqRecv = null;
        }

        /// <summary>外链协议就绪的那一刻截住它。隐藏窗口没有用户能点那个「是否打开」
        /// 确认框，所以这里一律取消默认行为，把 nxm 链接自己交给回流管线。</summary>
        private void OnLaunchingExternalUriScheme(object? sender,
            CoreWebView2LaunchingExternalUriSchemeEventArgs e)
        {
            try
            {
                var uri = e.Uri ?? "";
                e.Cancel = true;
                AppLog.Info("NexusWeb", "外部协议命中：" + NxmSummary(uri));
                TryRaiseNxm(uri);
            }
            catch (Exception ex)
            {
                AppLog.Warn("NexusWeb", "处理外部协议失败：" + ex.Message);
            }
        }

        /// <summary>网络请求层截 nxm:// —— 外部协议被拦住时这条仍然会先发出来。</summary>
        private void OnNetworkRequest(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                if (!doc.RootElement.TryGetProperty("request", out var req)) return;
                var url = req.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)
                    || !url.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) return;
                AppLog.Info("NexusWeb", "网络层截到 nxm：" + NxmSummary(url));
                TryRaiseNxm(url);
            }
            catch { /* 事件参数格式异常不影响主流程 */ }
        }

        private void TryRaiseNxm(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)
                || !uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Exchange(ref _nxmRaised, 1) != 0) return;
            try
            {
                var handler = NxmLinkDetected;
                if (handler is null)
                {
                    AppLog.Warn("NexusWeb", "截到 nxm 但没有订阅方，链接会丢");
                    return;
                }
                handler(uri);
            }
            catch (Exception ex)
            {
                AppLog.Warn("NexusWeb", "转交 nxm 链接失败：" + ex.Message);
            }
        }

        /// <summary>日志里只留 scheme + 路径：链接带的 <c>key</c> 是一次性凭据，
        /// 和 InstallService 那条回流日志的规矩一致，不落盘。</summary>
        private static string NxmSummary(string uri)
        {
            var q = uri.IndexOf('?');
            return q > 0 ? uri[..q] : uri;
        }

        /// <summary>只认「N 网自家域名 + 压缩包类 MIME」的响应。
        /// 判据收窄是刻意的：页面里第三方统计、字体被错标成 octet-stream 的情况不罕见，
        /// 放宽就会把一个 .woff2 当成 zip 存下来。</summary>
        private void OnNetworkResponse(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            if (_urlTcs.Task.IsCompleted) return;
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                if (!doc.RootElement.TryGetProperty("response", out var resp)) return;
                var url = resp.TryGetProperty("url", out var u) ? u.GetString() : null;
                var mime = resp.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(mime)) return;

                var isPack = mime.Contains("zip", StringComparison.OrdinalIgnoreCase)
                          || mime.Contains("7z", StringComparison.OrdinalIgnoreCase)
                          || mime.Contains("rar", StringComparison.OrdinalIgnoreCase)
                          || mime.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
                          || mime.Contains("x-download", StringComparison.OrdinalIgnoreCase);
                if (!isPack) return;

                if (!Uri.TryCreate(url, UriKind.Absolute, out var abs)) return;
                var host = abs.Host.ToLowerInvariant();
                var path = abs.AbsolutePath.ToLowerInvariant();

                // 只认**文件 CDN** 主机，不再「域里含 nexusmods.com 就算」。
                // 2026-11-04 04:43 实机：avatars.nexusmods.com/<uid>/100 这张头像被 Chromium 标成
                // application/octet-stream，旧判据直接当直链收下 → 两条更新下回来的都是 100×100 的
                // 头像，解压必报「压缩包损坏」。头像/静态资源全在 www/static/images 这些主机上。
                // 主机收窄后这里不必再要求「压缩包扩展名」：CDN 常常不带宽表扩展名，
                // 硬抠会让真正的直链漏判，退回 DownloadStarting 那条慢路。
                var isFileHost = host == "nexus-cdn.com" || host.EndsWith(".nexus-cdn.com")
                              || host.StartsWith("cf-files.") || host.StartsWith("files.")
                              || host.StartsWith("filedelivery.");
                if (!isFileHost) return;

                // 明显的静态资源路径排除（被错标成 octet-stream 的图/字体）
                if (path.EndsWith(".js") || path.EndsWith(".css") || path.EndsWith(".png")
                    || path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".webp")
                    || path.EndsWith(".svg") || path.EndsWith(".woff2") || path.EndsWith(".woff")
                    || path.EndsWith(".ico")) return;

                AppLog.Info("NexusWeb", $"网络层命中：mime={mime} url={url}");
                _urlTcs.TrySetResult(url);
            }
            catch { /* 事件参数格式异常不影响主流程 */ }
        }

        /// <summary>
        /// 核心一步：Chromium 已经知道「这个 URL 是个文件下载」但还没写盘，
        /// 此刻 <c>Cancel = true</c> 把文件扣下，只取 URL 走自己的下载器。
        /// 取不到 URL 时（极罕见）就不拦 —— 宁可让浏览器落一份盘，也别让用户白等。
        /// </summary>
        private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                var uri = e.DownloadOperation?.Uri;
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    e.Cancel = true;
                    // 直链只在日志里留域名级别信息没必要 —— 这条 URL 带签名但会秒过期，
                    // 写全也就便于排查「拦到的是 CDN 还是 N 网中间页」。
                    AppLog.Info("NexusWeb", "已拦下 Nexus 下载：" + uri);
                    _urlTcs.TrySetResult(uri);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("NexusWeb", "拦截下载失败：" + ex.Message);
            }
        }

        /// <summary>页面里 target=_blank / window.open（登录、条款页）一律在本窗口内导航，
        /// 不弹新窗 —— 隐藏窗口弹出来的子窗用户根本看不见。
        /// <para><b>但下载落点例外</b>：登录态下 N 网会把 «Slow download» 的落点甩给一个
        /// 新窗口。以前这里无条件塞回本窗口导航，而当目标是 CDN 直链时，Chromium 对
        /// 「顶层导航到二进制」的处理是<b>静默取消</b> —— <c>DownloadStarting</c> 永远不来，
        /// 页面就钉在 “Your download is starting” 上不动（2026-11-04 02:54 实测：
        /// ready → 点击 → 之后 120 秒零日志，最后超时）。认出是文件就直接当结果收下。</para></summary>
        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            try
            {
                e.Handled = true;                       // 一律不弹真窗口
                var uri = e.Uri;
                AppLog.Info("NexusWeb", "新窗口请求：" + (string.IsNullOrWhiteSpace(uri) ? "(空)" : uri));
                if (string.IsNullOrWhiteSpace(uri)) return;
                if (LooksLikeDownloadUrl(uri))
                {
                    AppLog.Info("NexusWeb", "新窗口直链命中：" + uri);
                    _urlTcs.TrySetResult(uri);
                    return;
                }
                _core?.Navigate(uri);                   // 登录页 / 条款页：本窗口内打开
            }
            catch { }
        }

        /// <summary>同窗口导航到直链（<c>location.href = fileUrl</c>）—— 和上面同一种情况，
        /// 早一步拦住：让导航别真跑，直接把 URL 当结果。</summary>
        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(e.Uri)) return;
                if (e.Uri.IndexOf("DownloadPopUp", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppLog.Info("NexusWeb", "导航到下载弹页：" + e.Uri);
                if (!LooksLikeDownloadUrl(e.Uri)) return;
                e.Cancel = true;
                AppLog.Info("NexusWeb", "导航直链命中：" + e.Uri);
                _urlTcs.TrySetResult(e.Uri);
            }
            catch { }
        }

        /// <summary>这条 URL 像不像「真正的文件下载」。
        /// <para>判据刻意收窄 —— 只认 CDN 主机和明确的压缩包扩展名。宁可漏判交回
        /// <c>DownloadStarting</c> 兜底，也不能把普通页面（含 <c>/download</c> 这种
        /// 中转页）误当直链，否则会把一张 HTML 当 zip 存下来。</para></summary>
        private static bool LooksLikeDownloadUrl(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
            var host = u.Host.ToLowerInvariant();
            if (host.Contains("nexus-cdn.com")) return true;
            var path = u.AbsolutePath.ToLowerInvariant();
            return path.EndsWith(".zip") || path.EndsWith(".7z") || path.EndsWith(".rar")
                || path.EndsWith(".tar") || path.EndsWith(".gz") || path.EndsWith(".xz")
                || path.EndsWith(".zipx");
        }

        // --------------------------------------------------------------
        // 主循环：探测页面 → 必要时显形 → 代按按钮
        // --------------------------------------------------------------

        private async Task<string?> PumpAsync(CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            var clicks = 0;
            var interactive = false;
            // 这一轮是不是刚经历过验证页（用来在它退场时重置计时预算）
            var sawChallenge = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_urlTcs.Task.IsCompleted) return _urlTcs.Task.Result;

                var limit = interactive ? InteractiveTimeout : PassiveTimeout;
                if (DateTime.UtcNow - started > limit)
                {
                    _lastError = interactive
                        ? (_browserHandoff
                            ? "浏览器里没有收到下载回流（没有点下载按钮，或点了但没回到本应用）"
                            : "等待你在 Nexus 页面完成验证超时")
                        : "没能在 Nexus 页面触发下载（账号未登录，或页面结构已变化）";
                    return null;
                }

                // 静默窗口：刚把验证页交到用户眼前的那几秒里，一次探针都不打。
                // 每次探针都是一次 CDP Runtime.evaluate 注入，而 CF 的反自动化最爱抓这个 ——
                // 「一直有人在页面上执行脚本」正是托管挑战迟迟不自动放行的常见原因
                // （2026-11-07 01:06 实机：验证页连续 11 次刷新失败，而同一时刻普通浏览器
                //  秒过；差别不在网络——裸连 0.67s 就拿到质询页——而在指纹与注入）。
                if (DateTime.UtcNow < _quietUntilUtc)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                var probe = await ProbeAsync();
                if (probe is null)
                {
                    await Task.Delay(700, ct);
                    continue;
                }

                // 只在状态跳变时记一行：这张页面每 0.6 秒探一次，全记就是刷屏
                var sig = $"{probe.Button}/cf={probe.Challenge}/login={probe.Login}/act={probe.NeedsClick}";
                if (sig != _lastProbeSig)
                {
                    _lastProbeSig = sig;
                    AppLog.Info("NexusWeb", "页面状态 " + sig);
                }

                // 忍耐期只压「登录」：N 网的首屏经常先给一张 Cloudflare 中间页
                // （"Just a moment…"），3~8 秒后自己放行。这期间弹窗只会让用户对着
                // 一张下一秒就消失的页面发呆，而窗口还 Topmost 悬在整个屏幕上。
                var inGrace = (DateTime.UtcNow - started).TotalSeconds < GraceSeconds;

                // ── 人机验证：这是【唯一】会让窗口出现在用户眼前的情形 ──
                // 2026-11-08：不再吃 GraceSeconds 的 10 秒宽限 —— 那个宽限是给「登录墙判断」用的，
                // 拿它压验证页只会让用户对着空界面干等（见 ChallengeRevealDelaySeconds 的说明）。
                if (probe.Challenge)
                {
                    _cfMisses = 0;
                    _sawChallenge = true;
                    sawChallenge = true;
                    _challengeActive = true;
                    _challengeSeenAtUtc ??= DateTime.UtcNow;

                    // 去抖：挑战页连续存在一小会儿才弹窗。一闪而过的 CF 中间页不值得
                    // 把置顶窗口甩到用户脸上；真卡住的则要立刻给他看。
                    if ((DateTime.UtcNow - _challengeSeenAtUtc.Value).TotalSeconds < ChallengeRevealDelaySeconds)
                    {
                        await Task.Delay(300, ct);
                        continue;
                    }

                    // 交窗。两种形态走同一处 —— 差别只在提示文案：
                    //  · 有可见 Turnstile 复选框 → 必须用户点一下；
                    //  · 只有转圈（自动校验）→ 也把窗口给他。2026-11-07 03:52 用户原话
                    //    「如果是人机验证，为什么不直接弹窗让咱们自己验证？」：托管挑战确有
                    //    「窗口要有焦点 / 要被真人碰一下」才放行的情形，那是我们替不了的，
                    //    而背着用户偷偷转去浏览器等于把主动权也拿走了。
                    if (_revealedHint != "cf")
                    {
                        var needClick = probe.NeedsClick;
                        Reveal("cf", needClick
                            ? "Nexus 要求人机验证 —— 请在弹出的窗口里点一下完成，之后会自动继续"
                            : "Nexus 要验证一下浏览器 —— 窗口已弹出，等它转完会自动继续；"
                              + "要是停着不动，点一下页面任意位置");
                        interactive = true;
                        _autoHintSent = true;
                        _challengeRevealAtUtc = DateTime.UtcNow;
                        AppLog.Info("NexusWeb", needClick
                            ? $"检测到人机验证（有可点的 Turnstile）→ 弹窗交给用户点"
                            : $"检测到 Cloudflare 验证页（只有转圈）→ 弹窗交给用户看着，窗口内静默让它自己跑");
                        await Task.Delay(800, ct);
                        continue;
                    }

                    // 已经交到用户手上了：给他 ChallengeManualSeconds 亲手过，
                    // 期间不催、不重载、不偷偷转浏览器。
                    var age = (DateTime.UtcNow - started).TotalSeconds;
                    var manualAge = (DateTime.UtcNow - _challengeRevealAtUtc).TotalSeconds;
                    if (manualAge > ChallengeManualSeconds)
                    {
                        // 2026-11-07 03:16 实锤：同一个真实 profile，挑战页 35s+ 不放行；
                        // 删掉 Cookies 库后 7 秒通过 —— profile 里被污染的 CF cookie
                        // （过期 cf_clearance / 坏 __cf_bm）会让托管挑战永远不放行。
                        // 重载前先清这几个 CF 专用 cookie（Nexus 登录 cookie 原样保留）。
                        if (_challengeReloads < MaxChallengeReloads)
                        {
                            _challengeReloads++;
                            await ClearCfCookiesAsync();
                            AppLog.Warn("NexusWeb",
                                $"验证页 {age:F0}s 未放行（act=False），已清 CF Cookie，重载页面重试（第 {_challengeReloads} 次）");
                            _report?.Invoke($"验证没通过，正在重试（第 {_challengeReloads} 次）…", null, null);
                            started = DateTime.UtcNow;
                            _autoHintSent = false;
                            _challengeSeenAtUtc = null;   // 新一轮挑战重新走去抖
                            // 手动计时也要重来：窗口一直在屏幕上，这个起点还是上一次弹窗的时刻，
                            // 不清就会「重载完 2 秒」直接判超时、把刚给用户的第二次机会白扔掉
                            // （2026-11-09 00:14 自测日志：重载后 2s 就跳到「改开系统浏览器」）。
                            _challengeRevealAtUtc = DateTime.UtcNow;
                            _core?.Navigate(PageUrl);
                            await Task.Delay(800, ct);
                            continue;
                        }

                        if (!_browserHandoff)
                        {
                            _browserHandoff = true;
                            interactive = true;          // 交接之后给足 5 分钟等他点
                            _lastError = "已打开浏览器下载页 —— 点一下页面上的下载按钮，会自动接回本任务";
                            _report?.Invoke("已用浏览器打开下载页 —— 点一下下载按钮，任务会自动继续…", null, null);
                            AppLog.Warn("NexusWeb",
                                $"验证页 {age:F0}s 仍未放行（重载 {_challengeReloads} 次后），改开系统浏览器等 nxm 回流");
                            try { UpdateService.OpenUrl(PageUrl); }
                            catch (Exception ex) { AppLog.Warn("NexusWeb", "打开浏览器失败：" + ex.Message); }
                        }
                        await Task.Delay(1500, ct);
                        continue;
                    }

                    if (!_autoHintSent)
                    {
                        // 不弹窗（没东西可点），但任务行得说清楚它在自救 —— 不然看着像卡住。
                        // 措辞别写「一般几秒钟」：这台机器上挑战经常一卡就是几十秒，
                        // 说「几秒钟」等于骗人（2026-11-07 03:39 用户截图）。
                        _autoHintSent = true;
                        _report?.Invoke($"Nexus 风控校验中…（第 {_challengeReloads + 1} 次尝试，最多 {MaxChallengeReloads + 1} 次）", null, null);
                    }
                    await Task.Delay(1500, ct);
                    continue;
                }

                // 挑战页退场：CF 花掉的那几十秒不该算进「页面多久没出现按钮」的预算里，
                // 否则一次慢验证就能把后面的正常加载时间挤没。
                // ⚠ 只重置计时，不重置 _challengeReloads：探针在重载间隙会闪变 cf=False 走到这里，
                // 把重载计数也清零的话就变成「永远在重试第 1 次」的死循环（2026-11-07 03:11 实机）。
                // 计数只在窗口生命周期里累加，到 MaxChallengeReloads 就走外跳兜底。
                if (sawChallenge)
                {
                    sawChallenge = false;
                    _autoHintSent = false;
                    _challengeActive = false;
                    _challengeSeenAtUtc = null;   // 挑战页退了，下一张重新计时
                    started = DateTime.UtcNow;
                }

                // 验证过了（或页面已经自己恢复）→ 立刻搬回屏幕外。
                // 「除了人机验证，其它时候内置浏览器都是隐藏的」—— 收窗这一步就是兑现它。
                // 2026-11-06 21:38 实机：CF 中间页在「英文原文 / 本地化文案」之间闪变
                // （21:38:58 cf=1 弹窗 → 21:39:01 cf=0 收窗 → 21:39:38 失败现场 title=请稍候…
                //   页面仍停在验证页），单次 cf=0 就收窗会把正卡在验证页的窗口抽走，
                // 用户对着「请在弹出的窗口里点一下」干等一个已经不存在的弹窗。
                // 连续 3 次探不到挑战页（约 4 秒）才算真的过了。
                if (_revealedHint == "cf")
                {
                    if (++_cfMisses < 3)
                    {
                        await Task.Delay(700, ct);
                        continue;
                    }
                    Conceal();
                    _revealedHint = null;
                    _cfMisses = 0;
                    AppLog.Info("NexusWeb", "人机验证已通过，窗口收回屏幕外");
                }

                // ── 登录墙：不弹窗，直接判失败 ──
                // 以前这里会把窗口搬出来让用户登录。但用户明确要求「除了人机验证不显示」，
                // 而且一个停在登录页的窗口对没登录的人也只是个空白折磨 —— 直接交回调用方，
                // 让它退回「打开文件页」那条本来就能装的老路，比干等 2 分钟有用。
                // 点击之后再出现的登录跳转给 8 秒宽限：那一下多半是 N 网自己的中转，
                // 下载事件可能随后就到。
                if (probe.Login && !inGrace)
                {
                    if (clicks == 0 || (DateTime.UtcNow - _lastClickUtc).TotalSeconds > 8)
                    {
                        // v1.3.42：撞登录墙就把内置网页摆出来让用户登 —— 以前这里直接判失败，
                        // 调用方于是退回「打开系统浏览器」，新用户根本不知道应用内通道存在。
                        // 窗口里登录后，每 2 秒的轮询会把状态改成「已登录」，重试就能走应用内。
                        _lastError = "需要先在内置网页登录 Nexus 账号 —— 已打开登录窗口，登录后再试一次";
                        _report?.Invoke(_lastError, null, null);
                        AppLog.Warn("NexusWeb", "命中登录墙：打开内置网页让用户登录");
                        LoginWallHit?.Invoke();
                        return null;
                    }
                    await Task.Delay(1200, ct);
                    continue;
                }

                // 按过按钮却迟迟等不到下载事件。
                // ① 先每 3 秒从页面里捞一次落点：倒计时页 / 中转页上常挂着一个直指 CDN
                //    的手动下载链接，捞到就直接用，不必等浏览器自己发起下载。
                // ② 实在捞不到才判失败，而且给到 100 秒 —— 覆盖一次 60 秒的定时器节流周期
                //    （窗口在屏幕外时 Chromium 可能把 setTimeout 压到一分钟一次，
                //    N 网那 5 秒倒计时会变成 60 秒；45 秒就放弃会冤枉它）。
                if (clicks > 0)
                {
                    var waited = (DateTime.UtcNow - _lastClickUtc).TotalSeconds;
                    var harvestDue = (DateTime.UtcNow - _lastHarvestUtc).TotalSeconds > 3;
                    if (waited > 2 && harvestDue && probe.Button == "none")
                    {
                        _lastHarvestUtc = DateTime.UtcNow;
                        var anchor = await HarvestAsync(logScene: false);
                        if (!string.IsNullOrWhiteSpace(anchor))
                        {
                            AppLog.Info("NexusWeb", "页面锚点命中：" + anchor);
                            return anchor;
                        }
                    }
                    // 按钮从 DOM 里消失、又没有下载事件 —— 实测是那一下点在了还没接上
                    // React 的服务端渲染按钮上（面板随后被重建）。以前在这里干等 100 秒
                    // 再判失败，用户看到的就是「批量更新卡了 2 分钟」。重载后通常一次就成。
                    if (waited > ReloadAfterClickSeconds && probe.Button == "none"
                        && _reloads < MaxReloads)
                    {
                        _reloads++;
                        clicks = 0;
                        _lastClickUtc = DateTime.MinValue;
                        _report?.Invoke("页面没反应，正在重试获取下载链接…", null, null);
                        AppLog.Warn("NexusWeb",
                            $"点过按钮 {waited:F0}s 没有下载事件，重载文件页重试（第 {_reloads} 次）");
                        _core!.Navigate(PageUrl);
                        await Task.Delay(800, ct);
                        continue;
                    }
                    if (waited > WaitAfterClickSeconds)
                    {
                        await HarvestAsync(logScene: true);   // 把现场（URL/标题/正文）写进日志
                        _lastError = _reloads > 0
                            ? "点了 Slow download，但 Nexus 没把文件交出来（重载重试后仍然没有下载事件）"
                            : "点了 Slow download，但 Nexus 没把文件交出来（页面没给下载事件，也没给落点链接）";
                        AppLog.Warn("NexusWeb", _lastError);
                        return null;
                    }
                }

                // 5 秒内不重复按：点完 N 网可能先走一段倒计时（按钮变 disabled），
                // 连按会触发第二次下载请求，白耗一次配额。
                if (probe.Button == "ready" && (DateTime.UtcNow - _lastClickUtc).TotalSeconds >= 5)
                {
                    clicks++;
                    _lastClickUtc = DateTime.UtcNow;
                    _report?.Invoke(clicks <= 2
                        ? "正在从 Nexus 页面获取下载链接…"
                        : "正在重试获取下载链接…", null, null);
                    var clicked = await ClickAsync(clicks);
                    await Task.Delay(clicked ? 1500 : 600, ct);
                    continue;
                }

                // 页面加载完了，但下载面板一直没出现 —— 实测有两种：① 真的慢，SVE/POV
                // 这类大页面按钮要 20 秒才渲染；② SPA 渲染失败，只有导航和页脚，正文空白
                // （2026-11-06 实机：POV 文件页两次都卡在「标题对、内容没渲染」，干等 60 秒
                // 后失败；同一条 URL 重开能出按钮）。30 秒还没按钮就重载一次，给第二种一条活路，
                // 又不打断第一种的慢加载（正常 22 秒内就出来了）。
                if (clicks == 0 && probe.Button == "none" && !probe.Challenge && !probe.Login
                    && (DateTime.UtcNow - started).TotalSeconds > 30
                    && _reloads < MaxReloads)
                {
                    _reloads++;
                    AppLog.Warn("NexusWeb",
                        $"页面 {((DateTime.UtcNow - started).TotalSeconds):F0}s 还没出现下载按钮，重载重试（第 {_reloads} 次）");
                    _report?.Invoke("Nexus 页面没加载出来，正在重试…", null, null);
                    _core!.Navigate(PageUrl);
                    started = DateTime.UtcNow;   // 重载后重新计时，否则立刻撞上 60 秒上限
                    await Task.Delay(800, ct);
                    continue;
                }

                // 认不出按钮 60 秒 = 这页多半不是我们以为的样子（N 网改版 / 文件已下架 /
                // 落到了中转页）。不弹窗让它自己躲着，直接收工给结论，别干等满 120 秒。
                if (clicks == 0 && (DateTime.UtcNow - started).TotalSeconds > GraceSeconds + 50)
                {
                    // 把现场（URL/标题/正文前 180 字）写进日志：这条分支以前什么都不留，
                    // 复发时只能猜「页面结构变了」还是「正文根本没渲染」。
                    await HarvestAsync(logScene: true);
                    // 2026-11-06 实机：真实的死法是 CF 验证页一直没过（网络反复中断），
                    // 别把它含糊地归成「页面结构变化」。
                    _lastError = _sawChallenge
                        ? "Nexus 的 Cloudflare 人机验证一直没通过（网络不稳会让验证反复重来）"
                        : "Nexus 页面迟迟没出现下载按钮（页面结构可能已变化，或这个文件需要先在网页上确认）";
                    AppLog.Warn("NexusWeb", _lastError);
                    return null;
                }

                // 按钮还没渲染出来 / 处于倒计时 disabled：继续等
                await Task.Delay(600, ct);
            }
        }

        /// <summary>把窗口搬到屏幕中央并置顶 —— 只在需要用户动手时调用。
        /// <para>同一原因只搬一次：CF 过完变登录墙时窗口已经在屏幕上了，那时只更新
        /// 任务行的提示文案，不再重复挪窗口（否则每轮都 Activate 一次，用户的输入焦点
        /// 会被反复抢走）。</para>
        /// <para>必须先按工作区夹一次尺寸：窗口建的时候是 1180×880 DIP，125% 缩放下
        /// 就是 1475×1100 物理像素，比 1080p 的工作区还高 —— 直接居中会让标题栏和
        /// 底部按钮都跑到屏幕外，用户连登录框都点不到。</para></summary>
        private void Reveal(string reason, string hint)
        {
            var firstTime = _revealedHint is null;
            _revealedHint = reason;
            _report?.Invoke(hint, null, null);
            // 交窗之后先静默几秒：让 CF 的托管挑战在没有脚本注入的干净环境里跑一次，
            // 很多挑战就是这样自己过的（用户的原话：「有时不用点就通过了」）。
            // 2026-11-08：这段静默从「弹窗前的 8 秒」搬到这里 —— 窗口已经上屏，用户看得见进度，
            // 而探针仍然不打断挑战。4 秒够了：静默越久，验证过了之后窗口在屏幕上多留的时间越长。
            _quietUntilUtc = DateTime.UtcNow.AddSeconds(4);
            if (!firstTime || _window is null) return;
            try
            {
                var wa = SystemParameters.WorkArea;
                var w = Math.Min(_window.Width, Math.Max(720, wa.Width - 48));
                var h = Math.Min(_window.Height, Math.Max(520, wa.Height - 48));
                _window.Width = w;
                _window.Height = h;
                _window.Left = wa.Left + Math.Max(0, (wa.Width - w) / 2);
                _window.Top = wa.Top + Math.Max(0, (wa.Height - h) / 2);
                _window.Topmost = true;
                _window.Activate();
            }
            catch (Exception ex)
            {
                AppLog.Warn("NexusWeb", "显现下载窗口失败：" + ex.Message);
            }
        }

        /// <summary>把窗口搬回屏幕外并取消置顶 —— 人机验证一过就立刻隐身。
        /// 之所以是「搬到屏幕外」而不是 <c>Hide()</c>/<c>Visibility.Hidden</c>：那样
        /// WebView2 会把内核挂起甚至销毁，后面还要接着用它点按钮。</summary>
        private void Conceal()
        {
            if (_window is null) return;
            try
            {
                _window.Topmost = false;
                _window.Left = SystemParameters.VirtualScreenLeft - _window.Width - 4000;
                _window.Top = SystemParameters.VirtualScreenTop - _window.Height - 4000;
            }
            catch (Exception ex)
            {
                AppLog.Warn("NexusWeb", "收起下载窗口失败：" + ex.Message);
            }
        }

        // --------------------------------------------------------------
        // 页面探测与点击（CDP）
        // --------------------------------------------------------------

        /// <param name="NeedsClick">这张挑战页上有可见的 Turnstile 控件（必须由用户点一下）；
    /// false = 自动校验页（只有转圈），通常自己会过。</param>
    private sealed record ProbeState(string Button, bool Challenge, bool Login, bool NeedsClick);

        /// <summary>从当前页面里捞下载落点，<paramref name="logScene"/> 时顺带把
        /// URL / 标题 / 正文前 180 字写进日志（失败后用来看它到底停在哪个页面）。</summary>
        private async Task<string?> HarvestAsync(bool logScene)
        {
            var raw = await EvalAsync(HarvestScript, userGesture: false);
            if (raw is null) return null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (logScene)
                {
                    string Get(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : "";
                    AppLog.Warn("NexusWeb", $"失败现场 url={Get("url")} title={Get("title")} text={Get("text")}");
                }
                return root.TryGetProperty("anchor", out var a) && a.ValueKind == JsonValueKind.String
                    ? (string.IsNullOrWhiteSpace(a.GetString()) ? null : a.GetString())
                    : null;
            }
            catch { return null; }
        }

        private async Task<ProbeState?> ProbeAsync()
        {
            var json = await EvalAsync(ProbeScript, userGesture: false);
            if (json is null) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var state = root.TryGetProperty("s", out var sv) ? sv.GetString() ?? "none" : "none";
                var cf = root.TryGetProperty("cf", out var cv) && cv.GetInt32() == 1;
                var login = root.TryGetProperty("login", out var lv) && lv.GetInt32() == 1;
                var act = root.TryGetProperty("act", out var av) && av.GetInt32() == 1;
                return new ProbeState(state, cf, login, act);
            }
            catch { return null; }
        }

        /// <summary>
        /// 前两次用 <c>Runtime.evaluate</c> + <c>userGesture:true</c>（等价于 Electron 的
        /// <c>executeJavaScript(code, true)</c>）。仍不生效就退到 <c>Input.dispatchMouseEvent</c>
        /// —— 那是 Chromium 内部合成出来的真鼠标事件，<c>isTrusted</c> 一定为 true，
        /// 专门伺候对「点击是否可信」有要求的按钮。
        /// </summary>
        private async Task<bool> ClickAsync(int attempt)
        {
            if (_core is null) return false;
            try
            {
                if (attempt <= 2)
                {
                    var raw = await EvalAsync(ClickScript, userGesture: true);
                    if (raw is not null && raw.Contains("\"ok\":1", StringComparison.Ordinal))
                    {
                        AppLog.Info("NexusWeb", "点击慢速下载：" + raw);
                        return true;
                    }
                    AppLog.Info("NexusWeb", "点击未命中：" + (raw ?? "(null)"));
                    return false;
                }

                var pos = await EvalAsync(ScrollAndMeasureScript, userGesture: false);
                if (pos is null) return false;
                using var doc = JsonDocument.Parse(pos);
                if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.GetInt32() != 1) return false;
                var x = doc.RootElement.GetProperty("x").GetDouble();
                var y = doc.RootElement.GetProperty("y").GetDouble();
                if (x <= 0 || y <= 0) return false;

                var pressed = JsonSerializer.Serialize(new
                {
                    type = "mousePressed", x, y, button = "left", clickCount = 1,
                });
                var released = JsonSerializer.Serialize(new
                {
                    type = "mouseReleased", x, y, button = "left", clickCount = 1,
                });
                await _core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pressed);
                await Task.Delay(40);
                await _core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", released);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("NexusWeb", "注入点击失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>跑一段 JS 并把返回值（字符串）取回来。异常/无返回值一律 null。
        /// <para><b>注意返回结构</b>：<c>CallDevToolsProtocolMethodAsync</c> 给的是 CDP 消息里
        /// <c>result</c> 字段的<b>内容</b>，即 <c>{"result":{"type":"string","value":"..."}}</c>，
        /// 比标准 CDP 响应少一层 <c>result</c>。早期写成 <c>root.result.result.value</c>
        /// 会永远取不到值（探针实测：页面明明加载好了，probe 却一直是 null）。</para></summary>
        private async Task<string?> EvalAsync(string expression, bool userGesture)
        {
            if (_core is null) return null;
            try
            {
                var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["expression"] = expression,
                    ["returnByValue"] = true,
                    ["awaitPromise"] = false,
                    ["userGesture"] = userGesture,
                });
                var raw = await _core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", payload);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("exceptionDetails", out _)) return null;
                if (!root.TryGetProperty("result", out var val)) return null;
                if (val.ValueKind == JsonValueKind.Null) return null;
                if (val.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
                return val.ToString();
            }
            catch (Exception ex)
            {
                // 探针实测踩到过：CDP 偶发 "Value does not fall within the expected range"。
                // 一次注入失败不该升级成整条下载失败 —— 返回 null，主循环下一轮重试。
                AppLog.Warn("NexusWeb", "注入脚本执行失败：" + ex.Message);
                return null;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 注入脚本
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 「找 Slow download 按钮」的公共实现。N 网的下载区历史上换过好几版：
    /// 有时是 <c>#slowDownloadButton</c>，有时是 class 里带 slow+download，
    /// 有时整块塞在 Shadow DOM 里 —— 所以既按 id/class/文案匹配，也递归进 shadowRoot。
    /// 三个脚本共用这一份，改一处即可。
    /// </summary>
    private const string SlowFinderJs = """
        function () {
          var hits = [];
          var scan = function (root, depth) {
            if (!root || depth > 6) return;
            var nodes;
            try { nodes = root.querySelectorAll('a,button,[role="button"]'); } catch (e) { return; }
            for (var i = 0; i < nodes.length; i++) {
              var el = nodes[i];
              var id = (el.id || '').toLowerCase();
              var c = el.className;
              if (c && typeof c === 'object' && c.baseVal !== undefined) c = c.baseVal;
              c = (typeof c === 'string' ? c : '').toLowerCase();
              var t = (el.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
              if (id === 'slowdownloadbutton'
                  || (c.indexOf('slow') >= 0 && c.indexOf('download') >= 0)
                  || t === 'slow download' || t === 'slow downloads'
                  || t.indexOf('slow download') === 0) hits.push(el);
            }
            var all;
            try { all = root.querySelectorAll('*'); } catch (e) { return; }
            for (var j = 0; j < all.length && j < 5000; j++) {
              if (all[j].shadowRoot) scan(all[j].shadowRoot, depth + 1);
            }
          };
          scan(document, 0);
          window.__jgHits = hits.length;
          return hits.length > 0 ? hits[0] : null;
        }
        """;

    /// <summary>一次调用同时回答三个问题：是不是人机验证、是不是登录墙、下载按钮能不能点。
    /// <para>判据顺序是探针实测调出来的，别随手改：</para>
    /// <list type="number">
    /// <item><b>users.nexusmods.com / /login</b> 是登录墙的最强信号 —— 未登录时点
    /// Slow download 一定会被甩到这个独立登录站（还自带 Cloudflare Turnstile）。</item>
    /// <item><b>先找按钮，再猜登录</b>：只要 Slow download 在 DOM 里就说明下载面板已就绪，
    /// 这时哪怕页头还挂着 <c>#login</c> 也照点不误 —— 反过来（先信 <c>#login</c>）一旦
    /// 登录后该节点没被 React 摘掉，就会永远判成未登录、一次都不点。</item>
    /// <item>人机验证排最后，且只在「没按钮也没登录」时才认，否则登录页的 Turnstile
    /// 会被误报成「需要过验证」，用户对着登录框找不到验证码。</item>
    /// </list></summary>
    private static readonly string ProbeScript = $$"""
        (function () {
          var out = { s: 'none', cf: 0, login: 0, act: 0 };
          try {
            var host = (location.hostname || '').toLowerCase();
            var href = (location.href || '').toLowerCase();

            if (host.indexOf('users.nexusmods.com') >= 0 || href.indexOf('/login') >= 0) {
              out.login = 1;
              return JSON.stringify(out);
            }

            var el = ({{SlowFinderJs}})();
            if (el) {
              var c = el.className;
              if (c && typeof c === 'object' && c.baseVal !== undefined) c = c.baseVal;
              c = (typeof c === 'string' ? c : '').toLowerCase();
              var dis = el.hasAttribute('disabled')
                        || el.getAttribute('aria-disabled') === 'true'
                        || c.indexOf('disabled') >= 0;
              out.s = dis ? 'disabled' : 'ready';
            } else {
              var pw = document.querySelector('input[type="password"]');
              if (pw) {
                var pr = pw.getBoundingClientRect();
                if (pr.width > 0 && pr.height > 0) out.login = 1;
              }
              if (!out.login && document.querySelector('#login')) out.login = 1;
            }

            if (!out.login && !el) {
              var text = '';
              try { text = ((document.body && document.body.innerText) || '').slice(0, 8000); } catch (e) { }
              var lc = ((document.title || '') + ' ' + text).toLowerCase();
              // 别把 iframe[src*=challenges.cloudflare.com] 算进来：N 网每张页面都预载了
              // 用于登录表单的 Turnstile 隐藏 iframe，一加就是 100% 误判成人机验证。
              // 2026-11-06 21:39 实机：中文系统的 CF 中间页是「请稍候…」+「正在进行安全验证」，
              // 英文正则一个都匹配不上 → 探针闪变 cf=0 → 窗口被误收回，验证页永远等不到弹窗。
              // N 网站本身是纯英文站，页面上出现中文只可能是 CF 的中间页，按中文匹配是安全的。
              if (/just a moment|checking your browser|verify you are human|attention required|enable javascript and cookies/.test(lc)
                  || lc.indexOf('正在进行安全验证') >= 0
                  || lc.indexOf('验证您不是自动程序') >= 0
                  || lc.indexOf('请确认您是真人') >= 0
                  || lc.indexOf('由 cloudflare 提供的性能和安全服务') >= 0
                  || (document.title || '').trim().toLowerCase().indexOf('请稍候') === 0
                  || document.querySelector('#challenge-form, #cf-challenge-running, .cf-browser-verification'))
                out.cf = 1;

              // 这张挑战页到底要不要人动手？CF 的「托管挑战」有两种长相：
              //  · 自动校验：只有一个转圈 + 「请稍候…」，自己几秒后就放行 → 不该弹窗；
              //  · 需要点击：页面上有可见的 Turnstile 复选框 → 必须把窗口交到用户手上。
              // 判据只看「可见尺寸」的 Turnstile 控件：N 网每页预载的登录用 iframe 是 0×0，
              // 按尺寸排除掉（历史误报就是它造成的）。
              if (out.cf) {
                try {
                  var fr = document.querySelectorAll('iframe');
                  for (var i = 0; i < fr.length; i++) {
                    var src = (fr[i].src || '');
                    if (src.indexOf('challenges.cloudflare.com') < 0) continue;
                    var rr = fr[i].getBoundingClientRect();
                    if (rr.width >= 40 && rr.height >= 25) { out.act = 1; break; }
                  }
                  if (!out.act) {
                    var stage = document.querySelector('#challenge-stage, #challenge-form, .cf-browser-verification');
                    if (stage) {
                      var hit = stage.querySelector('input[type=checkbox], button, [role=button], label');
                      if (hit) {
                        var hr = hit.getBoundingClientRect();
                        if (hr.width >= 8 && hr.height >= 8) out.act = 1;
                      }
                    }
                  }
                } catch (e) { }
              }
            }
          } catch (e) { }
          return JSON.stringify(out);
        })()
        """;

    /// <summary>从页面上捞一个「真的指向文件」的链接。
    /// <para>N 网点完 Slow download 后有时停在倒计时 / "Your download is starting"
    /// 中转页，那页上通常就有这么一条 <c>&lt;a&gt;</c> —— 直接拿它的 href 比等浏览器
    /// 自己发起下载可靠得多。正则只认 CDN 主机和压缩包后缀，<c>/download/</c> 这种
    /// 中转链接故意不算（拿它当 zip 下回来只会得到一张 HTML）。</para></summary>
    private const string HarvestScript = """
        (function () {
          try {
            var out = { url: location.href || '', title: document.title || '', anchor: '', text: '' };
            var as = document.querySelectorAll('a[href]');
            for (var i = 0; i < as.length; i++) {
              var h = as[i].href || '';
              if (/nexus-cdn\.com|\.zip(\?|#|$)|\.7z(\?|#|$)|\.rar(\?|#|$)/i.test(h)) { out.anchor = h; break; }
            }
            try { out.text = ((document.body && document.body.innerText) || '').replace(/\s+/g, ' ').slice(0, 180); } catch (e) { }
            return JSON.stringify(out);
          } catch (e) { return null; }
        })()
        """;

    /// <summary>滚到视口中央后程序化点击。返回坐标只为日志/兜底用。</summary>
    private static readonly string ClickScript = $$"""
        (function () {
          try {
            var el = ({{SlowFinderJs}})();
            if (!el) return JSON.stringify({ ok: 0, why: 'not-found' });
            try { el.scrollIntoView({ block: 'center', inline: 'center' }); } catch (e) { }
            var r = el.getBoundingClientRect();
            el.click();
            return JSON.stringify({
              ok: 1,
              hits: (window.__jgHits || 0),
              tag: el.tagName,
              id: el.id || '',
              cls: String(el.className && el.className.baseVal !== undefined ? el.className.baseVal : el.className || '').slice(0, 60),
              href: el.getAttribute ? (el.getAttribute('href') || '') : '',
              vis: !!(el.offsetParent || el.getClientRects().length),
              rs: document.readyState,
              // React 挂上去之后元素上才会出现 __reactProps$… 这种键。没有它就说明
              // 点的是服务端渲染那颗还没接线的按钮 —— 点了也白点（失败时用来定性）。
              react: Object.keys(el).some(function (k) { return k.indexOf('__react') >= 0; }),
              x: Math.round(r.left + r.width / 2),
              y: Math.round(r.top + r.height / 2)
            });
          } catch (e) {
            return JSON.stringify({ ok: 0, why: String(e) });
          }
        })()
        """;

    /// <summary>只滚动 + 量坐标，不点击 —— 给 CDP 真鼠标事件那一路用。</summary>
    private static readonly string ScrollAndMeasureScript = $$"""
        (function () {
          try {
            var el = ({{SlowFinderJs}})();
            if (!el) return JSON.stringify({ ok: 0 });
            try { el.scrollIntoView({ block: 'center', inline: 'center' }); } catch (e) { }
            var r = el.getBoundingClientRect();
            if (r.width <= 1 || r.height <= 1) return JSON.stringify({ ok: 0 });
            return JSON.stringify({
              ok: 1,
              x: Math.round(r.left + r.width / 2),
              y: Math.round(r.top + r.height / 2)
            });
          } catch (e) {
            return JSON.stringify({ ok: 0 });
          }
        })()
        """;
}
