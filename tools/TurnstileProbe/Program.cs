using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TurnstileProbe;

public static class Program
{
    private const string DiagJs = """
        (function(){
          var q = function(sel){ return document.querySelectorAll(sel).length; };
          var iframes = document.querySelectorAll('iframe');
          var srcs = [];
          for (var i=0;i<iframes.length;i++){
            var s = iframes[i].getAttribute('src') || iframes[i].src || '';
            srcs.push(s.slice(0,160));
          }
          var bodyText = '';
          try { bodyText = (document.body ? (document.body.innerText || '') : '').slice(0,200); } catch(e){}
            return JSON.stringify({
            all: iframes.length,
            turnstile: q('iframe[src*="challenges.cloudflare.com"]') + q('.cf-turnstile') + q('#turnstile-wrapper') + q('[data-turnstile-callback]') + q('[data-sitekey]'),
            cfSrc: srcs,
            vis: document.visibilityState,
            focus: document.hasFocus(),
            w: window.innerWidth,
            h: window.innerHeight,
            sx: window.screenX,
            sy: window.screenY,
            title: document.title,
            href: location.href,
            bodyLen: document.body ? document.body.innerHTML.length : 0,
            text: bodyText,
            ua: navigator.userAgent,
            brands: (navigator.userAgentData && navigator.userAgentData.brands
                     ? navigator.userAgentData.brands : []).map(function(b){ return b.brand + '/' + b.version; }).join(' | '),
            uadPlatform: (navigator.userAgentData && navigator.userAgentData.platform) || '',
            uadMobile: (navigator.userAgentData && navigator.userAgentData.mobile) ? 1 : 0
          });
        })()
        """;

    /// <summary>与 JuniGrid 的 BrowserIdentity.HideEmbeddedScript 一字不差。</summary>
    private const string HideEmbeddedScript = """
        (() => {
          const hide = (obj, key) => {
            if (!obj) return;
            try {
              Object.defineProperty(obj, key, { get: () => undefined, configurable: true, enumerable: false });
            } catch (e) { }
          };
          try {
            hide(window.chrome, 'webview');
            hide(window.chrome, 'webviewBridge');
            hide(Navigator.prototype, 'webdriver');
            hide(navigator, 'webdriver');
          } catch (e) { }
        })();
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "visible";
        string outFile = args.Length > 1 ? args[1] : @"E:\junigrid\probe.jsonl";
        string url = args.Length > 2 ? args[2] : "https://www.nexusmods.com/stardewvalley";
        string profile = args.Length > 3 ? args[3] : @"E:\junigrid\probe-profile-" + mode;
        int seconds = args.Length > 4 && int.TryParse(args[4], out var sec) ? sec : 60;
        var extras = (args.Length > 5 ? args[5] : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        Directory.CreateDirectory(profile);

        var app = new Application();
        var view = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };

        const double W = 960, H = 720;
        Window window;
        if (mode == "offscreen")
        {
            // 与 JuniGrid 当前实现完全一致：丢到可视区之外 + 不抢焦点 + 不进任务栏
            window = new Window
            {
                Title = "Probe offscreen",
                Width = W,
                Height = H,
                WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - W - 4000,
                Top = SystemParameters.VirtualScreenTop - H - 4000,
                Content = view,
            };
        }
        else
        {
            window = new Window
            {
                Title = "Probe visible",
                Width = W,
                Height = H,
                WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = true,
                ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = view,
            };
        }

        var log = new StreamWriter(outFile, append: true) { AutoFlush = true };
        void Write(string stage, object payload)
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                mode,
                stage,
                payload,
            });
            log.WriteLine(line);
            Console.WriteLine(line);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var t0 = DateTime.Now;
        var passed = false;
        CoreWebView2? core = null;

        window.Loaded += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows " +
                        "--disable-renderer-backgrounding --disable-ipc-flooding-protection " +
                        "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,ThrottleDisplayNoneAndVisibilityHiddenCrossOriginIframes",
                });
                await view.EnsureCoreWebView2Async(env);
                core = view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.NavigationCompleted += (_, e) => Write("nav-completed", new { ok = e.IsSuccess, code = e.WebErrorStatus.ToString() });
                core.DOMContentLoaded += (_, _) => Write("dom-content-loaded", new { });
                Write("kernel-ready", new { version = core.Environment?.BrowserVersionString, profile, extras });

                // 隔离变量 ①：JuniGrid 的 HideEmbeddedScript —— 抹 window.chrome.webview / navigator.webdriver
                if (extras.Contains("hide"))
                {
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(HideEmbeddedScript);
                    Write("applied", new { what = "HideEmbeddedScript" });
                }

                // 隔离变量 ③：JuniGrid 的 BrowserIdentity —— Network.setUserAgentOverride 把身份修成普通 Edge
                if (extras.Contains("ua") || extras.Contains("ua2"))
                {
                    // 与 JuniGrid.ReadIdentityProbeAsync 一致：Runtime.evaluate + awaitPromise
                    var evalRaw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                        JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["expression"] = BrowserIdentity.ProbeScript,
                            ["awaitPromise"] = true,
                            ["returnByValue"] = true,
                        }));
                    string? probeJson = null;
                    using (var ed = JsonDocument.Parse(evalRaw!))
                    {
                        if (ed.RootElement.TryGetProperty("result", out var r)
                            && r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                            probeJson = v.GetString();
                    }
                    var payload = BrowserIdentity.Build(probeJson,
                        core.Environment?.BrowserVersionString, BrowserIdentity.GuessPlatformVersion());
                    // ua2：把 brands 补成真 Edge 的三品牌形态（Chromium + 熵增品牌 + Edge）
                    if (extras.Contains("ua2") && payload is not null)
                    {
                        var p = JsonDocument.Parse(payload).RootElement;
                        var major = p.GetProperty("userAgentMetadata").GetProperty("fullVersion").GetString()!.Split('.')[0];
                        var full = p.GetProperty("userAgentMetadata").GetProperty("fullVersion").GetString();
                        var fixedPayload = new Dictionary<string, object?>
                        {
                            ["userAgent"] = p.GetProperty("userAgent").GetString(),
                            ["acceptLanguage"] = p.GetProperty("acceptLanguage").GetString(),
                            ["platform"] = "Win32",
                            ["userAgentMetadata"] = new Dictionary<string, object?>
                            {
                                ["brands"] = new object[]
                                {
                                    new Dictionary<string,string>{ ["brand"]="Not)A;Brand", ["version"]="99" },
                                    new Dictionary<string,string>{ ["brand"]="Chromium",   ["version"]=major! },
                                    new Dictionary<string,string>{ ["brand"]="Microsoft Edge", ["version"]=major! },
                                },
                                ["fullVersionList"] = new object[]
                                {
                                    new Dictionary<string,string>{ ["brand"]="Not)A;Brand", ["version"]="99.0.0.0" },
                                    new Dictionary<string,string>{ ["brand"]="Chromium",   ["version"]=full! },
                                    new Dictionary<string,string>{ ["brand"]="Microsoft Edge", ["version"]=full! },
                                },
                                ["fullVersion"] = full,
                                ["platform"] = "Windows",
                                ["platformVersion"] = "15.0.0",
                                ["architecture"] = "x86",
                                ["model"] = "",
                                ["mobile"] = false,
                                ["bitness"] = "64",
                                ["wow64"] = false,
                            },
                        };
                        payload = JsonSerializer.Serialize(fixedPayload);
                    }
                    Write("identity-payload", new { payload });
                    try
                    {
                        await core.CallDevToolsProtocolMethodAsync("Network.setUserAgentOverride", payload!);
                    }
                    catch (Exception ex)
                    {
                        Write("ua-network-failed", new { error = ex.Message });
                        await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", payload!);
                    }
                    Write("applied", new { what = "Network.setUserAgentOverride" });
                }

                core.Navigate(url);

                // 隔离变量 ②：JuniGrid 挑战流程 —— 交窗后 6 秒清 CF cookie 并重载换新质询
                if (extras.Contains("reload"))
                {
                    var reloadAt = DateTime.UtcNow.AddSeconds(8);
                    var reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    var reloaded = false;
                    reloadTimer.Tick += async (_, _) =>
                    {
                        if (reloaded || DateTime.UtcNow < reloadAt) return;
                        reloaded = true;
                        reloadTimer.Stop();
                        try
                        {
                            foreach (var name in new[] { "cf_clearance", "__cf_bm", "__cf_chl_2fa", "__cf_chl_prog", "__cf_chl_rc_m", "__cf_chl_js_t", "__cf_chl_tk", "__cfduid", "__cf_waitlist" })
                            {
                                foreach (var dom in new[] { "nexusmods.com", ".nexusmods.com", "www.nexusmods.com" })
                                    await core.CallDevToolsProtocolMethodAsync("Network.deleteCookies",
                                        JsonSerializer.Serialize(new { name, domain = dom }));
                            }
                            Write("applied", new { what = "cleared cf cookies + reload" });
                            core.Navigate(url);
                        }
                        catch (Exception ex) { Write("reload-failed", new { error = ex.Message }); }
                    };
                    reloadTimer.Start();
                }
                timer.Start();
            }
            catch (Exception ex)
            {
                Write("init-failed", new { error = ex.ToString() });
                log.Close();
                app.Shutdown(1);
            }
        };

        timer.Tick += async (_, _) =>
        {
            try
            {
                if (core is null) return;
                var raw = await core.ExecuteScriptAsync(DiagJs);
                var json = JsonDocument.Parse(raw!).RootElement.GetString() ?? raw!;
                using var doc = JsonDocument.Parse(json);
                Write("diag", doc.RootElement);

                var title = doc.RootElement.GetProperty("title").GetString() ?? "";
                var href = doc.RootElement.GetProperty("href").GetString() ?? "";
                var bodyLen = doc.RootElement.GetProperty("bodyLen").GetInt32();
                var onChallenge = title.Contains("Just a moment") || title.Contains("请稍候")
                                  || href.Contains("__cf_chl") || href.Contains("cdn-cgi/challenge");
                if (bodyLen > 3000 && !onChallenge && !passed)
                {
                    passed = true;
                    Write("CHALLENGE_PASSED", new { title, href, elapsedSec = Math.Round((DateTime.Now - t0).TotalSeconds, 1) });
                }
            }
            catch (Exception ex)
            {
                Write("diag-failed", new { error = ex.Message });
            }
            if (DateTime.UtcNow > deadline)
            {
                timer.Stop();
                log.Close();
                app.Shutdown(0);
            }
        };

        return app.Run(window);
    }
}
