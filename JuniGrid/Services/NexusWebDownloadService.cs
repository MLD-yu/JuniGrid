using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JuniGrid.Services;

/// <summary>应用内自动下载的结果：<see cref="Url"/> 非空即拿到了一次性 CDN 直链。</summary>
public sealed record NexusWebDownloadResult(string? Url, string? Error)
{
    public static NexusWebDownloadResult Ok(string url) => new(url, null);
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
/// <b>并发</b>：池子里最多同时 3 个窗口（见 <see cref="PoolSize"/>）。所有窗口共用
/// 同一个 WebView2 环境（同一个 profile → 登录态天然共享），只是各自一个控件，
/// 于是批量更新可以几路并行，而不是一个个排队。
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
    /// <summary>总开关。关掉后 <see cref="TryGetDirectUrlAsync"/> 立即失败，
    /// 调用方自动退回打开系统浏览器的老路。默认开启（这是本版的产品选择）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>窗口池容量 = 同时最多几个隐藏窗口并行。
    /// <para>2026-11-04：从 1 提到 3。以前全局只允许一个窗口，批量更新十几个 mod 就是
    /// 「一个一个排队 × 每个十几秒」，用户看到的是「点了更新半天不动」。现在免费账号也能
    /// 三路并行（和 Premium 的下载并发一致）。再多意义不大：瓶颈在网络和 N 网限速，
    /// 而且每多一个窗口就多一个 Chromium 渲染进程。</para></summary>
    private const int PoolSize = 3;

    private static readonly SemaphoreSlim Gate = new(PoolSize, PoolSize);

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

    /// <summary>忍耐期：页面刚打开的前 10 秒不因「验证 / 登录」判定去弹窗。
    /// 实测 N 网首屏经常先给一张 Cloudflare 中间页（Just a moment…），3~8 秒才自己放行；
    /// 太早 Reveal 会让用户对着一个下一秒就消失的挑战页发呆。</summary>
    private const int GraceSeconds = 10;

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
        CancellationToken ct = default)
    {
        if (!Enabled) return NexusWebDownloadResult.Fail("应用内自动下载已关闭");
        if (modId <= 0 || fileId <= 0) return NexusWebDownloadResult.Fail("modId/fileId 无效");

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return NexusWebDownloadResult.Fail("界面尚未初始化");

        var entered = false;
        try
        {
            // 排队上限：池子里 3 个窗口都在忙时最多等 150 秒，等不到就明说
            entered = await Gate.WaitAsync(TimeSpan.FromSeconds(150), ct);
            if (!entered) return NexusWebDownloadResult.Fail("另一个应用内下载页正在使用中，稍后再试");

            var host = new HiddenHost(modId, fileId, report);
            // 窗口与 WebView2 全是 STA 亲和对象：整段跑在 UI 线程上。
            // 外层 await 拿到的是 Task<Result>，再 await 一次取值。
            return await (await dispatcher.InvokeAsync(() => host.RunAsync(ct)));
        }
        catch (OperationCanceledException)
        {
            return NexusWebDownloadResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            AppLog.Warn("NexusWeb", "应用内自动下载异常：" + ex);
            return NexusWebDownloadResult.Fail(ex.Message);
        }
        finally
        {
            if (entered) Gate.Release();
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // nxm 回流合流（同一 mod 只该有一行任务）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>正在为某个 (mod, file) 等直链的隐藏窗口。
    /// <para>为什么要它：文件页必须带 <c>&amp;nmm=1</c> 才会渲染下载面板，而 N 网那个面板
    /// 会顺手触发一次 <c>nxm://</c> —— Windows 把链接转回本进程，走「网页一键安装」那条路，
    /// 于是同一个 mod 同时冒出「更新 Mod「X」」和「下载并安装 X」两行任务（2026-11-04 用户反馈：
    /// 一个 mod 两个任务）。这里登记在跑的取链请求，nxm 回流时先问一句是不是自己撂的。</para></summary>
    private static readonly ConcurrentDictionary<string, HiddenHost> Active = new();

    private static string FetchKey(int modId, long fileId) => modId + ":" + fileId;

    /// <summary>这个 (mod, file) 现在有隐藏窗口在等直链吗？</summary>
    public static bool HasPending(int modId, long fileId)
        => Active.TryGetValue(FetchKey(modId, fileId), out var h) && !h.Finished;

    /// <summary>把 nxm 回流解析出的真直链交给正在等它的隐藏窗口。
    /// 返回 true = 已接走（调用方不要再建任务）。</summary>
    public static bool TryDeliver(int modId, long fileId, string? directUrl)
        => Active.TryGetValue(FetchKey(modId, fileId), out var h) && h.Deliver(directUrl);

    // ══════════════════════════════════════════════════════════════════
    // 隐藏窗口本体
    // ══════════════════════════════════════════════════════════════════

    /// <summary>一次取链接的全过程。构造 → RunAsync → 自己 Close，不做成长期存活的服务。</summary>
    private sealed class HiddenHost
    {
        private readonly int _modId;
        private readonly long _fileId;
        private readonly Action<string, double?, double?>? _report;

        private Window? _window;
        private WebView2? _view;
        private CoreWebView2? _core;

        /// <summary>拦到直链 / 用户关窗（null）都会让它完成。</summary>
        private readonly TaskCompletionSource<string?> _urlTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private string? _revealedHint;
        private string? _lastProbeSig;
        private bool _closedByUser;
        private DateTime _lastClickUtc = DateTime.MinValue;
        /// <summary>上一次从页面里捞下载锚点的时间 —— 每 3 秒最多扫一次 DOM，避免每轮都扫。</summary>
        private DateTime _lastHarvestUtc = DateTime.MinValue;
        private string? _lastError;

        public HiddenHost(int modId, long fileId, Action<string, double?, double?>? report)
        {
            _modId = modId;
            _fileId = fileId;
            _report = report;
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
            var fetchKey = FetchKey(_modId, _fileId);
            Active[fetchKey] = this;   // 先去登记：nxm 回流要能认领到这一个
            try
            {
                await CreateUiAsync();
                ct.ThrowIfCancellationRequested();

                AppLog.Info("NexusWeb", $"开始获取直链 mod={_modId} file={_fileId}");
                _report?.Invoke("正在应用内打开 Nexus 文件页…", null, null);
                _core!.Navigate(PageUrl);

                var url = await PumpAsync(ct);
                if (!string.IsNullOrEmpty(url)) return NexusWebDownloadResult.Ok(url);
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
                if (Active.TryGetValue(fetchKey, out var cur) && ReferenceEquals(cur, this))
                    Active.TryRemove(fetchKey, out _);
                Close();
            }
        }

        /// <summary>这个窗口已经拿到结果了吗？</summary>
        public bool Finished => _urlTcs.Task.IsCompleted;

        /// <summary>外部（nxm 回流）送进来的真直链：直接当结果交回，不再等页面自己发起下载。</summary>
        public bool Deliver(string? directUrl)
        {
            if (string.IsNullOrWhiteSpace(directUrl) || _urlTcs.Task.IsCompleted) return false;
            AppLog.Info("NexusWeb", $"采用 nxm 回流直链 mod={_modId} file={_fileId}");
            return _urlTcs.TrySetResult(directUrl);
        }

        // --------------------------------------------------------------
        // 建窗
        // --------------------------------------------------------------

        private async Task CreateUiAsync()
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

            await EnsureIsolatedWebViewAsync(profile);
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
            await HookNetworkAsync();
            _core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    _lastError = $"页面加载失败（{e.WebErrorStatus}）";
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

        private static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync(string profile)
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
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--disable-background-timer-throttling "
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

        private async Task HookNetworkAsync()
        {
            if (_core is null) return;
            try
            {
                _netRecv = _core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
                _netRecv.DevToolsProtocolEventReceived += OnNetworkResponse;
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

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_urlTcs.Task.IsCompleted) return _urlTcs.Task.Result;

                var limit = interactive ? InteractiveTimeout : PassiveTimeout;
                if (DateTime.UtcNow - started > limit)
                {
                    _lastError = interactive
                        ? "等待你在 Nexus 页面完成验证超时"
                        : "没能在 Nexus 页面触发下载（账号未登录，或页面结构已变化）";
                    return null;
                }

                var probe = await ProbeAsync();
                if (probe is null)
                {
                    await Task.Delay(700, ct);
                    continue;
                }

                // 只在状态跳变时记一行：这张页面每 0.6 秒探一次，全记就是刷屏
                var sig = $"{probe.Button}/cf={probe.Challenge}/login={probe.Login}";
                if (sig != _lastProbeSig)
                {
                    _lastProbeSig = sig;
                    AppLog.Info("NexusWeb", "页面状态 " + sig);
                }

                // 忍耐期只压「人机验证」：N 网的首屏经常先给一张 Cloudflare 中间页
                // （"Just a moment…"），3~8 秒后自己放行。这期间弹窗只会让用户对着
                // 一张下一秒就消失的页面发呆，而窗口还 Topmost 悬在整个屏幕上。
                var inGrace = (DateTime.UtcNow - started).TotalSeconds < GraceSeconds;

                // ── 人机验证：这是【唯一】会让窗口出现在用户眼前的情形 ──
                if (probe.Challenge && !inGrace)
                {
                    if (_revealedHint != "cf")
                    {
                        Reveal("cf", "Nexus 要求人机验证 —— 请在弹出的窗口里点一下完成，之后会自动继续");
                        interactive = true;
                    }
                    await Task.Delay(1500, ct);
                    continue;
                }

                // 验证过了（或页面已经自己恢复）→ 立刻搬回屏幕外。
                // 「除了人机验证，其它时候内置浏览器都是隐藏的」—— 收窗这一步就是兑现它。
                if (_revealedHint == "cf")
                {
                    Conceal();
                    _revealedHint = null;
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
                        _lastError = "Nexus 要求登录 —— 应用内自动下载需要先登录一次 Nexus 账号";
                        _report?.Invoke(_lastError, null, null);
                        AppLog.Warn("NexusWeb", "命中登录墙，放弃应用内通道");
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
                    if (waited > WaitAfterClickSeconds)
                    {
                        await HarvestAsync(logScene: true);   // 把现场（URL/标题/正文）写进日志
                        _lastError = "点了 Slow download，但 Nexus 没把文件交出来（页面没给下载事件，也没给落点链接）";
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

                // 认不出按钮 60 秒 = 这页多半不是我们以为的样子（N 网改版 / 文件已下架 /
                // 落到了中转页）。不弹窗让它自己躲着，直接收工给结论，别干等满 120 秒。
                if (clicks == 0 && (DateTime.UtcNow - started).TotalSeconds > GraceSeconds + 50)
                {
                    _lastError = "Nexus 页面迟迟没出现下载按钮（页面结构可能已变化，或这个文件需要先在网页上确认）";
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

        private sealed record ProbeState(string Button, bool Challenge, bool Login);

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
                return new ProbeState(state, cf, login);
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
                    return raw is not null && raw.Contains("\"ok\":1", StringComparison.Ordinal);
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
          var out = { s: 'none', cf: 0, login: 0 };
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
              if (/just a moment|checking your browser|verify you are human|attention required|enable javascript and cookies/.test(lc)
                  || document.querySelector('#challenge-form, #cf-challenge-running, .cf-browser-verification'))
                out.cf = 1;
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
