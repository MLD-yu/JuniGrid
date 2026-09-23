// ============================================================
// 启动动画：居中 logo → 左移 → JuniGrid 字样描边填充 → 淡出 → UI 从底部弹起
// ============================================================
(function () {
    window.junigridJs = window.junigridJs || {};

    // 兜底：gsap 没加载 / 找不到元素时，直接放行 UI（绝不卡死应用）
    window.junigridJs.splashInit = function () {
        // v0.19.0：前端 splash 已退化为空壳（display:none），logo 由 WPF SplashWindow 显示。
        // 这里只负责在 Blazor 挂载完成前把 shell 藏起，避免闪出主界面。
        document.body.classList.add('jg-booting');
    };

    // v0.20.0：等 Blazor 首帧真正稳定（两帧 rAF + 100ms）再通知 WPF。
    // 这样避免主窗淡入时看到深色兜底 (#app 背景色) 而不是浅色主题。
    window.junigridJs.splashUiReadyWhenStable = function () {
        function stable() {
            requestAnimationFrame(function () {
                requestAnimationFrame(function () {
                    setTimeout(function () { window.junigridJs.splashUiReady(); }, 100);
                });
            });
        }
        // 再等 shell DOM 出现（Blazor 挂载完但布局可能还没算完）
        if (document.querySelector('#app .jg-shell')) stable();
        else setTimeout(function () { window.junigridJs.splashUiReadyWhenStable(); }, 30);
    };

    window.junigridJs.splashUiReady = function () {
        // v0.19.0：透明启动动画已完全由 WPF SplashWindow 负责，前端这里只做两件事：
        // 1) 把 ui-ready 通知给 WPF 宿主（SplashWindow / App），由它淡出 Splash 并显示主窗；
        // 2) 立即解除 jg-booting，放行 .jg-shell —— 否则若上一步的动画路径缺元素提前
        //    退出、never 清理 jg-booting,整个主界面会一直 opacity:0/visibility:hidden，
        //    表现为“主界面黑屏”。
        document.body.classList.remove('jg-booting');
        try {
            if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
                window.chrome.webview.postMessage('ui-ready');
            }
        } catch (e) { /* ignore */ }
    };
})();
