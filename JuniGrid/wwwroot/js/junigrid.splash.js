// ============================================================
// 启动动画 / 启动屏（splash）：logo → 描边字 → 淡出 → UI 弹起
// ============================================================
// ============================================================
// ============================================================
// 启动动画：居中 logo → 左移 → JuniGrid 字样描边填充 → 淡出 → UI 从底部弹起
// ============================================================
(function () {
    window.junigridJs = window.junigridJs || {};
    var _splashDone = false;   // 动画播完
    var _uiReady = false;      // Blazor UI 已挂载

    function el(id) { return document.getElementById(id); }

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
        _uiReady = true;
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
        maybeReveal();
    };

    function buildWordmark() {
        var splash = el('jg-splash');
        var logo = el('jg-splash-logo');
        var svg = el('jg-splash-word');
        if (!splash || !svg || !window.gsap) { hideSplash(); return; }

        // logo 立刻浮现，不等字体测量 —— 启动画面要第一时间出现
        gsap.fromTo(logo, { opacity: 0, scale: 0.75 }, { opacity: 1, scale: 1, duration: 0.45 });

        var text = 'JuniGrid';
        var fs = Math.round(Math.max(72, Math.min(window.innerWidth, window.innerHeight) * 0.11));
        var dash = Math.max(fs * 7, 200);

        // 描边文字 + 填色文字（裁剪用）
        var NS = 'http://www.w3.org/2000/svg';
        var strokeText = mkText(true);
        var fillText = mkText(false);

        function mkText(isStroke) {
            var t = document.createElementNS(NS, 'text');
            t.setAttribute('x', 0); t.setAttribute('y', 0);
            t.setAttribute('fill', isStroke ? 'none' : '#4def7b');
            t.setAttribute('stroke', isStroke ? '#F8FAFC' : 'none');
            t.setAttribute('stroke-width', '1.4');
            t.setAttribute('stroke-linejoin', 'round');
            t.setAttribute('stroke-linecap', 'round');
            t.setAttribute('font-size', fs);
            t.setAttribute('font-weight', '800');
            t.setAttribute('letter-spacing', '-3px');
            for (var i = 0; i < text.length; i++) {
                var ts = document.createElementNS(NS, 'tspan');
                ts.textContent = text[i];
                if (isStroke) ts.setAttribute('data-draw', '1');  // 只描边字参与逐笔绘
                t.appendChild(ts);
            }
            return t;
        }

        svg.appendChild(strokeText);
        svg.appendChild(fillText);

        // 等字体加载完成后测字宽并开播；若 fonts.ready 迟迟不触发（字体被拦/离线），
        // 1.4s 后强行按估算开播，避免“动画永不开始、页面卡在深色封面”。
        var ran = false;
        function measureAndPlay() {
            var bbox;
            try { bbox = strokeText.getBBox(); } catch (e) { bbox = null; }
            if (!bbox || !bbox.width) { hideSplash(); return; }
            var pad = Math.max(1.4, fs * 0.1);
            var vx = bbox.x - pad, vy = bbox.y - pad,
                vw = bbox.width + pad * 2, vh = bbox.height + pad * 2;
            svg.setAttribute('viewBox', vx + ' ' + vy + ' ' + vw + ' ' + vh);
            svg.style.height = 'clamp(56px, 9.5vmin, 104px)';

            // 填色文字的裁剪片（左→右遮罩浮现）
            var defs = document.createElementNS(NS, 'defs');
            var cp = document.createElementNS(NS, 'clipPath');
            cp.setAttribute('id', 'jgs-wipe');
            var rect = document.createElementNS(NS, 'rect');
            rect.id = 'jgs-wipeRect';
            rect.setAttribute('x', vx); rect.setAttribute('y', vy);
            rect.setAttribute('width', '0'); rect.setAttribute('height', vh);
            cp.appendChild(rect); defs.appendChild(cp);
            svg.insertBefore(defs, svg.firstChild);
            fillText.setAttribute('clip-path', 'url(#jgs-wipe)');

            playSplash(splash, logo, svg, vx, vy, vw, vh, fs, dash);
        }

        function start() {
            if (ran) return;
            ran = true;
            measureAndPlay();
        }
        document.fonts.ready.then(start).catch(start);
        setTimeout(start, 1400);
    }

    function playSplash(splash, logo, svg, vx, vy, vw, vh, fs, dash) {
        var strokes = svg.querySelectorAll('[data-draw]');
        var wipeRect = el('jgs-wipeRect');
        if (!strokes.length) { hideSplash(); return; }

        // 描边初始：整段虚线藏在后面，再逐段 0 绘出
        gsap.set(strokes, { strokeDasharray: dash, strokeDashoffset: dash });
        gsap.set(wipeRect, { attr: { width: 0 } });
        // 字体初始完全透明 —— logo 动画完成前不显示
        gsap.set(svg, { opacity: 0 });

        // 让「logo+字」整体居中时，logo 恰好覆盖在整组中心；
        // 启动时 logo 先停在屏幕正中，动画里向左滑（restX）回到它应在的槽位。
        var stage = el('jg-splash-stage');
        var gap = stage ? (parseFloat(getComputedStyle(stage).gap) || 14) : 14;
        var restX = (vw + gap) + vx * 0;   // vx 已含 padding，额外位移 = 字宽 + 间距
        gsap.set(logo, { x: restX / 2 });

        // logo 滑动时长（正常节奏）
        var slideDur = 0.9;

        var tl = gsap.timeline({
            defaults: { ease: 'power2.out' },
            onComplete: function () { _splashDone = true; maybeReveal(); }
        });

        // 0) logo 由 buildWordmark 立即浮现 → 在中心停 0.5s → 再向左滑动到位
        //    修复：原先只等 0.05s，视觉上"一浮现就跑"，现在给 0.5s 定格再走
        var startDelay = 0.5;
        tl.to(logo, { x: 0, duration: slideDur, ease: 'linear' }, startDelay);
        // 1) logo 到位后，字体才淡入，再逐笔描边 + 填色
        tl.to(svg, { opacity: 1, duration: 0.35 }, startDelay + slideDur);
        tl.to(strokes, { strokeDashoffset: 0, duration: 1.5, ease: 'power2.inOut', stagger: 0.03 }, startDelay + slideDur + 0.25);
        tl.to(wipeRect, { attr: { width: vw }, duration: 0.9, ease: 'power2.inOut' }, startDelay + slideDur + 0.9);
        // 尾部停顿：确保描边（1.5s）与填色（0.9s）完全结束再淡出，避免主界面提前露出（图三 bug 修复）
        tl.to({}, { duration: 0.8 });
    }

    function maybeReveal() {
        if (!(_splashDone && _uiReady)) return;
        if (!window.gsap) { hideSplash(); return; }
        // 解除启动态：让应用外壳可见。先去掉 .jg-shell 的 opacity:0!important，
        // gsap 同一帧设 opacity:0 再滑入，不会闪白。
        document.body.classList.remove('jg-booting');
        var splash = el('jg-splash'); if (!splash) return;
        var shell = document.querySelector('#app .jg-shell');
        if (shell) {
            gsap.fromTo(shell, { y: 36, opacity: 0 }, { y: 0, opacity: 1, duration: 0.5, ease: 'power2.out' });
        }
        gsap.to(splash, {
            opacity: 0, duration: 0.55, ease: 'power2.inOut',
            onComplete: function () {
                splash.classList.add('hidden');
                if (splash.parentNode) splash.parentNode.removeChild(splash);
            }
        });
    }

    function hideSplash() {
        document.body.classList.remove('jg-booting');   // 兜底：别把外壳藏死
        var splash = el('jg-splash');
        if (splash) { splash.classList.add('hidden'); if (splash.parentNode) splash.parentNode.removeChild(splash); }
    }
})();

// ---- 顶栏选中块随窗口尺寸自适应 ----
// 改变窗口大小会重新分配顶栏 flex 项，.active 项的 offsetLeft/offsetWidth 随之变化，
// 但 Blazor 不会因 resize 重新 render → thumb 的 left/width 会停留在旧值而错位。
// 监听 resize 事件，重新读一次真实几何再定位。
window.addEventListener('resize', function () {
    if (window.junigridJs && typeof window.junigridJs.placeNavThumb === 'function')
        window.junigridJs.placeNavThumb();
});

