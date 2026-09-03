// JuniGrid — JS interop helpers, callable from Razor via IJSRuntime.
window.junigridJs = {
    popAnimate(selector) {
        if (!window.gsap) return;
        window.gsap.fromTo(
            selector,
            { scale: 1.0 },
            { scale: 1.08, duration: 0.18, yoyo: true, repeat: 1, ease: 'power2.inOut' }
        );
    },
    // 顶栏选中高亮滑块：把 .jg-topnav-thumb 平移到当前 .active 项（滑动/水平移动式切换）
    placeNavThumb() {
        const nav = document.querySelector('.jg-topnav');
        const thumb = document.querySelector('.jg-topnav-thumb');
        const active = nav && nav.querySelector('.jg-topnav-item.active');
        if (!thumb || !active) return;
        thumb.style.width = active.offsetWidth + 'px';
        thumb.style.left = active.offsetLeft + 'px';
    },
    // v0.33.0：排序下拉 —— open/close 都清干净初态，杜绝残留白块
    // v1.05.0：wrap 带 .jg-dd-right 时菜单右对齐（贴窗口右缘的下拉不再超出界面），基点用 top right
    // v1.07.0：①外部遮罩 .jg-dd-overlay 的开合在本函数内与菜单【同帧】翻转 —— 遮罩原先靠 Blazor
    //            重渲染补上，比 JS 慢一拍，「菜单已开、遮罩未铺」的窗口期 hover/点击穿透到下方 mod 卡；
    //          ②动画作用于 .jg-sort-menu-in 视觉内层（Nexus 页），外层盒子保持最终矩形，
    //            命中区域从打开第一帧起就是最终位置；无内层的页面（Logs/Mods）自动回退动画 menu 本身
    dropdownToggle(wrapSel, open) {
        const wrap = document.querySelector(wrapSel);
        if (!wrap) return;
        document.querySelectorAll('.jg-dd-overlay').forEach(o => o.classList.toggle('open', !!open));
        const menu  = wrap.querySelector('.jg-sort-menu');
        const arrow = wrap.querySelector('.jg-sort-arrow');
        const items = wrap.querySelectorAll('.jg-sort-item');
        if (!menu) return;
        const vis = menu.querySelector(':scope > .jg-sort-menu-in') || menu;
        const fromRight = wrap.classList.contains('jg-dd-right');
        const originY = fromRight ? 'top right' : 'top left';

        // 兜底：无 gsap 时靠 .open + CSS 完成开关，避免白块残留
        if (!window.gsap) {
            wrap.classList.toggle('open', !!open);
            return;
        }
        const hasER = parseFloat(gsap.version) >= 3.13;
        const er = v => hasER ? v : undefined;

        if (wrap.__ddTl) { wrap.__ddTl.kill(); wrap.__ddTl = null; }
        gsap.killTweensOf([vis, menu, arrow, items]);

        if (open) {
            wrap.classList.add('open');
            gsap.set(arrow, { rotation: 0 });
            gsap.set(vis,  { autoAlpha: 0, y: -10, scale: 0.92, transformOrigin: originY });
            gsap.set(items, { opacity: 0, x: -14 });
            const tl = gsap.timeline();
            tl.to(arrow, { rotation: 180, duration: 0.5, ease: 'back.out(2)', easeReverse: er('power2.inOut') }, 0)
              .to(vis,   { autoAlpha: 1, y: 0, scale: 1, duration: 0.45, ease: 'back.out(1.7)', easeReverse: er('power3.out') }, 0)
              .to(items, { opacity: 1, x: 0, duration: 0.28, ease: 'back.out(2)', easeReverse: er('power2.out'), stagger: 0.05 }, 0.08);
            wrap.__ddTl = tl;
        } else {
            const tl = gsap.timeline({
                onComplete() {
                    // 关键：动画完全结束再彻底清 inline style + 移除 open 类，白块杜绝
                    gsap.set([vis, arrow, items], { clearProps: 'all' });
                    wrap.classList.remove('open');
                }
            });
            tl.to(items, { opacity: 0, x: -8, duration: 0.16, ease: 'power2.in', stagger: 0.03 }, 0)
              .to(vis,   { autoAlpha: 0, y: -10, scale: 0.92, duration: 0.22, ease: 'power2.out' }, 0)
              .to(arrow, { rotation: 0, duration: 0.28, ease: 'power2.inOut' }, 0);
            wrap.__ddTl = tl;
        }
    },

    // v0.33.0：展开式搜索 —— width 收放 + 关时 clearProps:'width' 让回 CSS 40px
    searchToggle(sel, open) {
        const wrap = document.querySelector(sel);
        if (!wrap) return;
        const field = wrap.querySelector('.jg-search-field');
        const input = wrap.querySelector('.jg-search-input');
        if (!field) return;

        if (!window.gsap) {
            wrap.classList.toggle('open', !!open);
            return;
        }
        const hasER = parseFloat(gsap.version) >= 3.13;
        const er = v => hasER ? v : undefined;

        if (wrap.__srTl) { wrap.__srTl.kill(); wrap.__srTl = null; }
        gsap.killTweensOf([wrap, field]);

        if (open) {
            wrap.classList.add('open');
            gsap.set(wrap,  { width: 40 });
            gsap.set(field, { autoAlpha: 0 });
            const tl = gsap.timeline();
            tl.to(wrap,  { width: 220, duration: 0.48, ease: 'back.out(1.6)', easeReverse: er('power2.out') }, 0)
              .to(field, { autoAlpha: 1, duration: 0.24, ease: 'power2.out' }, 0.1);
            wrap.__srTl = tl;
            if (input) setTimeout(() => { try { input.focus(); } catch (e) {} }, 260);
        } else {
            const tl = gsap.timeline({
                onComplete() {
                    // 交还给 CSS：width 由 .jg-search-x（无 .open）40px 定义
                    gsap.set([wrap, field], { clearProps: 'all' });
                    wrap.classList.remove('open');
                }
            });
            tl.to(field, { autoAlpha: 0, duration: 0.16, ease: 'power2.in' }, 0)
              .to(wrap,  { width: 40, duration: 0.32, ease: 'power2.out' }, 0.05);
            wrap.__srTl = tl;
        }
    },

    // 启动/关闭等状态切换时，立即清掉启动按钮上残留的像素溶解蒙版（.px-grid）与倾斜 transform，
    // 否则残留的白色「点击启动！」会盖住新的按钮文案（如「启动中…」）。
    clearLaunchFx(selector) {
        const btn = document.querySelector(selector);
        if (!btn) return;
        btn.querySelectorAll('.px-grid').forEach(function (grid) {
            if (grid.__anims) grid.__anims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
            if (grid.parentNode) grid.parentNode.removeChild(grid);
        });
        if (window.gsap) window.gsap.killTweensOf(btn);
        if (window.gsap) window.gsap.set(btn, { clearProps: 'transform' });
    },
    // v0.31.0: PCL 式页面入场 —— 给 <main.jg-main> 打上 .jg-page-enter，触发 CSS 关键帧
    playPageEnter() {
        const el = document.querySelector('.jg-main');
        if (!el) return;
        el.classList.remove('jg-page-enter');
        // 强制回流一次，再加回来，才能重新触发动画
        // eslint-disable-next-line no-unused-expressions
        void el.offsetWidth;
        el.classList.add('jg-page-enter');
        // 500ms 后清掉，避免与后续交互动画冲突（子项最长 delay 290 + duration 420 ≈ 710）
        clearTimeout(el.__peTimer);
        el.__peTimer = setTimeout(() => el.classList.remove('jg-page-enter'), 900);
    },
    scrollToBottom(selector) {
        const el = document.querySelector(selector);
        if (el) el.scrollTop = el.scrollHeight;
    },
    // Custom titlebar drag: forward mousedown to .NET which calls Window.DragMove().
    // (CSS -webkit-app-region is unreliable inside WebView2, so we do it manually.)
    enableWindowDrag(el, dotNetRef) {
        if (!el) return;
        el.addEventListener('mousedown', e => {
            if (e.button !== 0) return;              // left button only
            if (e.target.closest && e.target.closest('.jg-upd-btn')) return;   // 更新按钮不能顺带拖动窗口
            dotNetRef.invokeMethodAsync('BeginDrag');
        });
        el.addEventListener('dblclick', e => {
            if (e.target.closest && e.target.closest('.jg-upd-btn')) return;
            dotNetRef.invokeMethodAsync('ToggleMaximize');
        });
    }
};

// ---- v0.5.0 新增 ----
// v0.39.0：Pixel Reveal —— 登录成功卡片被像素幕布盖住，
// 像素块从左到右、带随机抖动地消散，露出下方的头像/昵称/欢迎语
window.junigridJs.pixelReveal = function (canvasSel) {
    const canvas = document.querySelector(canvasSel);
    if (!canvas) return;
    const card = canvas.parentElement;
    const dpr = window.devicePixelRatio || 1;
    const w = card.clientWidth, h = card.clientHeight;
    canvas.width = w * dpr; canvas.height = h * dpr;
    canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
    const ctx = canvas.getContext('2d');
    ctx.scale(dpr, dpr);

    const cell = 14;
    const cols = Math.ceil(w / cell), rows = Math.ceil(h / cell);
    const bg = '#161616';

    // 每个像素的揭示时刻：x 归一化 + 随机抖动 → 0..1 区间
    const reveal = [];
    for (let r = 0; r < rows; r++) {
        reveal[r] = [];
        for (let c2 = 0; c2 < cols; c2++) {
            reveal[r][c2] = (c2 / cols) * 0.72 + Math.random() * 0.28;
        }
    }

    const DURATION = 1100; // ms
    const t0 = performance.now();
    function frame(now) {
        const t = Math.min((now - t0) / DURATION, 1);
        // ease: power2.out 收尾更快露出内容
        const p = 1 - (1 - t) * (1 - t);
        ctx.clearRect(0, 0, w, h);
        for (let r = 0; r < rows; r++) {
            for (let c2 = 0; c2 < cols; c2++) {
                const rt = reveal[r][c2];
                if (p < rt) {
                    // 未揭示：实心像素
                    ctx.fillStyle = bg;
                    ctx.fillRect(c2 * cell, r * cell, cell, cell);
                } else if (p < rt + 0.10) {
                    // 揭示边缘：像素缩小淡出
                    const k = (p - rt) / 0.10;
                    const sz = cell * (1 - k);
                    ctx.fillStyle = bg;
                    ctx.globalAlpha = 1 - k;
                    ctx.fillRect(c2 * cell + (cell - sz) / 2, r * cell + (cell - sz) / 2, sz, sz);
                    ctx.globalAlpha = 1;
                }
            }
        }
        if (t < 1) requestAnimationFrame(frame);
        else canvas.remove();
    }
    requestAnimationFrame(frame);
};

// v0.36.0：AnimatedList 滚动效果（React Bits AnimatedList 的 Blazor 移植）
// - 行进入视口 50% 时 scale 0.7→1 + opacity 0→1（0.2s），离开视口收回
// - 滚动容器顶/底渐变遮罩随滚动位置淡入淡出
window.junigridJs.animatedListInit = function (scrollSel, listSel) {
    const scroller = document.querySelector(scrollSel);
    const list = document.querySelector(listSel);
    if (!scroller || !list) return;

    // ── 行入场动画：IntersectionObserver，amount≈0.5，离开视口收回（triggerOnce:false）──
    if (!list.__alObs) {
        list.__alObs = new IntersectionObserver(entries => {
            for (const e of entries) {
                const el = e.target;
                if (e.intersectionRatio >= 0.5) {
                    el.style.opacity = '1';
                    el.style.transform = 'scale(1)';
                } else {
                    el.style.opacity = '0';
                    el.style.transform = 'scale(0.7)';
                }
            }
        }, { root: scroller, threshold: [0, 0.5, 1] });
    }
    list.querySelectorAll('[data-al]').forEach(el => {
        if (el.__alBound) return;
        el.__alBound = true;
        // 初始态：未进视口前收起（仅对当前不在视口内的；在视口内的立刻展开避免闪缩）
        el.style.transition = 'opacity .2s ease, transform .2s ease';
        el.style.transformOrigin = 'center center';
        const r = el.getBoundingClientRect();
        const sr = scroller.getBoundingClientRect();
        const visible = r.top < sr.bottom && r.bottom > sr.top;
        if (!visible) { el.style.opacity = '0'; el.style.transform = 'scale(0.7)'; }
        list.__alObs.observe(el);
    });

    // ── 顶/底渐变遮罩 ──
    if (!scroller.__alGrad) {
        scroller.__alGrad = true;
        const pos = getComputedStyle(scroller).position;
        if (pos === 'static') scroller.style.position = 'relative';
        const top = document.createElement('div');
        const bot = document.createElement('div');
        top.className = 'jg-al-gradient jg-al-gradient-top';
        bot.className = 'jg-al-gradient jg-al-gradient-bottom';
        scroller.appendChild(top);
        scroller.appendChild(bot);
        const onScroll = () => {
            const st = scroller.scrollTop;
            const sh = scroller.scrollHeight;
            const ch = scroller.clientHeight;
            top.style.opacity = Math.min(st / 50, 1);
            const bottomDist = sh - (st + ch);
            bot.style.opacity = sh <= ch ? 0 : Math.min(bottomDist / 50, 1);
        };
        scroller.addEventListener('scroll', onScroll, { passive: true });
        onScroll();
    }
};
// v1.08：过滤/搜索切换时调用 —— 清掉旧行的入场动画内联样式（IntersectionObserver
// 写入的 opacity/transform），让新过滤结果直接显示，不重播整表动画
window.junigridJs.animatedListReset = function (listSel) {
    const list = document.querySelector(listSel);
    if (!list) return;
    list.querySelectorAll('[data-al]').forEach(el => {
        el.style.opacity = '';
        el.style.transform = '';
        el.style.transition = '';
        if (el.__alBound && list.__alObs) list.__alObs.unobserve(el);
        el.__alBound = false;
    });
};

// v0.35.0：导航滑块实时同步 —— 路由变化/窗口缩放/刷新都立即重定位（双重 rAF 等布局稳定）
window.junigridJs.placeNavThumb = function () {
    const nav = document.querySelector('.jg-topnav');
    const thumb = document.querySelector('.jg-topnav-thumb');
    if (!nav || !thumb) return;
    const place = () => {
        const active = nav.querySelector('.jg-topnav-item.active');
        if (!active) { thumb.style.width = '0px'; return; }
        const nr = nav.getBoundingClientRect();
        const r = active.getBoundingClientRect();
        thumb.style.left = (r.left - nr.left) + 'px';
        thumb.style.width = r.width + 'px';
    };
    // 双 rAF：等 Blazor 把 .active 挪到目标项 + 布局回流完成后再量
    requestAnimationFrame(() => requestAnimationFrame(place));
};
// 缩放/字体加载等导致宽度变化时，滑块实时跟随（不带动画错位：transition 会平滑过渡）
(function () {
    if (window.__navThumbBound) return; window.__navThumbBound = true;
    let raf = 0;
    const re = () => { cancelAnimationFrame(raf); raf = requestAnimationFrame(() => window.junigridJs.placeNavThumb()); };
    window.addEventListener('resize', re);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(re);
})();
window.junigridJs.setMaximized = function (isMax) {
    document.body.classList.toggle('jg-max', !!isMax);
};
window.junigridJs.restored = function () {
    document.body.classList.remove('jg-minimizing');
};
window.junigridJs.animateMinimize = function () {
    document.body.classList.add('jg-minimizing');
};

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

// ------------------ 全局 toast（黑底白字默认；kind="err" 红底白字；2.6s 自动消失） ------------------
// 直接挂 document.body，避开 Blazor 组件树里 transform/filter 祖先破坏 position:fixed 的坑。
// 同类不叠加：上一个还在就先移除再显示新的。
(function () {
    window.junigridJs = window.junigridJs || {};
    var current = null;
    var hideTimer = null;

    window.junigridJs.toast = function (msg, kind) {
        try {
            if (current) {
                if (hideTimer) clearTimeout(hideTimer);
                if (current.parentNode) current.parentNode.removeChild(current);
            }
            var el = document.createElement('div');
            el.className = 'jg-toast-live' + (kind === 'err' ? ' err' : '');
            el.textContent = msg;
            document.body.appendChild(el);
            current = el;
            if (window.gsap) {
                gsap.fromTo(el, { y: -14, opacity: 0, scale: 0.96 },
                    { y: 0, opacity: 1, scale: 1, duration: 0.28, ease: 'back.out(2)' });
            }
            hideTimer = setTimeout(function () {
                if (window.gsap) {
                    gsap.to(el, { y: -10, opacity: 0, duration: 0.3, ease: 'power2.in',
                        onComplete: function () {
                            if (el.parentNode) el.parentNode.removeChild(el);
                            if (current === el) current = null;
                        } });
                } else {
                    el.style.transition = 'opacity .4s ease';
                    el.style.opacity = '0';
                    setTimeout(function () {
                        if (el.parentNode) el.parentNode.removeChild(el);
                        if (current === el) current = null;
                    }, 450);
                }
            }, 2300);
        } catch (e) { /* toast 失败不影响主流程 */ }
    };

    // v0.67.0：spring 胶囊已删除，导航项 tooltip 统一由后面的 .jg-cursor-tip 处理
})();

// 光标驱动透视倾斜（GSAP quickTo，效果同 demos.gsap.com 的 cursor-driven perspective tilt）
window.junigridJs.tiltPerspective = function (selector, opts) {
    opts = opts || {};
    if (!window.gsap) return;
    var el = document.querySelector(selector);
    if (!el) return;

    gsap.set(el, { transformPerspective: 650, transformStyle: 'preserve-3d', willChange: 'transform' });

    var outerRX = gsap.quickTo(el, 'rotationX', { ease: 'power3', duration: opts.duration || 0.35 });
    var outerRY = gsap.quickTo(el, 'rotationY', { ease: 'power3', duration: opts.duration || 0.35 });
    var innerX = gsap.quickTo(el, 'x', { ease: 'power3', duration: opts.duration || 0.35 });
    var innerY = gsap.quickTo(el, 'y', { ease: 'power3', duration: opts.duration || 0.35 });

    // 启动中/运行中这类非空闲状态下，关闭 3D 倾斜（按钮 disabled 或处于 .running）
    function busy() {
        return el.disabled || !!el.closest('.jg-launch-row.running');
    }
    function onMove(e) {
        if (busy()) { onLeave(); return; }
        var r = el.getBoundingClientRect();
        var nx = (e.clientX - r.left) / r.width;      // 0..1 相对按钮自身
        var ny = (e.clientY - r.top) / r.height;
        outerRX(gsap.utils.interpolate(10, -10, ny));
        outerRY(gsap.utils.interpolate(-10, 10, nx));
        innerX(gsap.utils.interpolate(-6, 6, nx));
        innerY(gsap.utils.interpolate(-6, 6, ny));
    }
    function onLeave() {
        outerRX(0); outerRY(0); innerX(0); innerY(0);
    }

    el.addEventListener('pointermove', onMove);
    el.addEventListener('pointerleave', onLeave);
};


// ---- PixelSwap 像素溶解：第二阶段内容以像素块逐个 reveal/收合。（纯 JS，WAAPI）----
// pixelSwap(btn, maskEl, activate, opts)：把 maskEl 作为第二态，以网格像素 reveal(进入)或收回(离开)。
(function () {
    if (typeof document === 'undefined') return;

    var clamp = function (v, a, b) { return v < a ? a : (v > b ? b : v); };
    var noise = function (s) { var v = Math.sin(s * 127.1 + 311.7) * 43758.5453; return v - Math.floor(v); };
    var MAXP = 240;

    function buildGrid(w, h, size, gap, randomness) {
        var cols = Math.max(1, Math.ceil((w + gap) / (size + gap)));
        var rows = Math.max(1, Math.ceil((h + gap) / (size + gap)));
        if (cols * rows > 240) {
            size = Math.ceil(size * Math.sqrt((cols * rows) / 240));
            cols = Math.max(1, Math.ceil((w + gap) / (size + gap)));
            rows = Math.max(1, Math.ceil((h + gap) / (size + gap)));
        }
        var stride = size + gap;
        var ox = (w - (cols * stride - gap)) / 2;
        var oy = (h - (rows * stride - gap)) / 2;
        var mix = clamp(randomness, 0, 1);
        var pos = [], i = 0;
        for (var r = 0; r < rows; r++) for (var c = 0; c < cols; c++) {
            var x = cols <= 1 ? 0.5 : c / (cols - 1);
            var y = rows <= 1 ? 0.5 : r / (rows - 1);
            var base = (x + y) / 2;
            var rst = noise(i + 1);
            pos.push({ left: ox + c * stride, top: oy + r * stride, off: base * (1 - mix) + rst * mix });
            i++;
        }
        return { pos: pos, size: size };
    }

    window.junigridJs.pixelSwap = function (btn, mask, activate, opts) {
        opts = opts || {};
        if (!btn) return;
        // 清除旧网格
        var old = btn.querySelector('.px-grid');
        var oldAnims = old ? old.__anims : null;
        if (oldAnims) oldAnims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
        if (old) { old.parentNode && old.parentNode.removeChild(old); }

        var w = Math.max(20, btn.clientWidth);
        var h = Math.max(20, btn.clientHeight);
        var gg = opts.gap || 0;
        var grid = buildGrid(w, h, opts.pixelSize || Math.round(Math.max(8, w / 13)), gg, opts.randomness || 0.2);
        btn.style.position = 'relative';

        var ms = Math.max(180, opts.duration || 420);
        var pixMs = clamp(opts.pixelDuration || 250, 60, ms);
        var spread = Math.max(0, ms - pixMs);
        var s0 = opts.pixelScale || 0.3;

        // 生成 keyframes（放大揭示）
        var kf = [];
        for (var s = 0; s <= 10; s++) {
            var p = s / 10, t = p;
            var sc = s0 + (1 - s0) * t;
            kf.push({ offset: p, opacity: t, transform: 'scale(' + sc + ')' });
        }

        // 若 activate=false(收回)：反向收缩 + 淡出（scale 1→s0, opacity 1→0）
        var outKf = [];
        for (var q = 0; q <= 10; q++) {
            var pr = q / 10;
            var sc2 = 1 + (s0 - 1) * pr;
            outKf.push({ offset: pr, opacity: 1 - pr, transform: 'scale(' + sc2 + ')' });
        }

        var gridEl = document.createElement('div');
        gridEl.className = 'px-grid';
        gridEl.style.cssText = 'position:absolute;inset:0;z-index:6;pointer-events:none;overflow:hidden;';
        btn.appendChild(gridEl);

        var anims = [];
        grid.pos.forEach(function (p) {
            var px = document.createElement('div');
            px.style.cssText = 'position:absolute;left:' + p.left + 'px;top:' + p.top + 'px;width:' + grid.size + 'px;height:' + grid.size + 'px;border-radius:' + (opts.pixelRadius || 3) + '%;overflow:hidden;';

            // 每个像素内放 mask 的窗口版
            var win = document.createElement('div');
            win.style.cssText = 'width:100%;height:100%;';
            var clone = mask.cloneNode(true);
            clone.style.cssText = 'position:absolute;left:' + (-p.left) + 'px;top:' + (-p.top) + 'px;width:' + w + 'px;height:' + h + 'px;transform-origin:' + (p.left + grid.size / 2) + 'px ' + (p.top + grid.size / 2) + 'px;';
            win.appendChild(clone);
            px.appendChild(win);
            gridEl.appendChild(px);
            var timing = { duration: pixMs, delay: p.off * spread, easing: 'linear', fill: 'both' };
            try { anims.push(px.animate(activate ? kf : outKf, timing)); } catch (e) {}
        });
        gridEl.__anims = anims;

        // 非激活（收回时）：动画结束移除网格；激活则保留住呈现第二态，稍后清理由 leave 触发收回
        if (!activate) {
            setTimeout(function () {
                if (gridEl.parentNode) gridEl.parentNode.removeChild(gridEl);
            }, Math.max(ms, 600));
        }
    };

    // hover 绑定：etriz进入 显示"点击启动！"白蒙版；离开 收回到按钮原样
    window.junigridJs.launchHover = function (sel) {
        var btn = sel ? document.querySelector(sel) : document.getElementById('launch-btn');
        if (!btn) return;
        var mask;   // 缓存的第二态内容
        var show = false;
        function buildMask() {
            var m = document.createElement('div');
            m.className = 'px-launch-mask';
            var txt = document.createElement('span');
            txt.className = 'px-launch-txt';
            txt.textContent = '点击启动！';
            m.appendChild(txt);
            return m;
        }
        // 启动中/运行中（disabled 或 .running）时，不做像素溶解
        function busy() {
            return btn.disabled || !!btn.closest('.jg-launch-row.running');
        }
        btn.addEventListener('mouseenter', function () {
            if (busy()) {
                // 非空闲（启动中/运行中）：即使此前残留了蒙版也一并清掉，保证显示原始按钮文案
                btn.querySelectorAll('.px-grid').forEach(function (grid) {
                    if (grid.__anims) grid.__anims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
                    if (grid.parentNode) grid.parentNode.removeChild(grid);
                });
                show = false;
                return;
            }
            if (show) return;
            show = true;
            if (!mask) mask = buildMask();
            window.junigridJs.pixelSwap(btn, mask, true);
        });
        btn.addEventListener('mouseleave', function () {
            // 启动中/运行中：不做像素「收回」动画，直接复位并清掉残留，避免移开鼠标时闪出反转动效
            if (busy()) {
                show = false;
                btn.querySelectorAll('.px-grid').forEach(function (grid) {
                    if (grid.__anims) grid.__anims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
                    if (grid.parentNode) grid.parentNode.removeChild(grid);
                });
                return;
            }
            if (!show) return;
            show = false;
            if (mask) window.junigridJs.pixelSwap(btn, mask, false);
        });
    };
})();
(function () {
    window.junigridJs = window.junigridJs || {};
    var tracked = null, trackedKey = null, ticking = false;

    window.junigridJs.scrollSpy = function (selector, key, restore) {
        var el = document.querySelector(selector);
        if (!el) return;
        // 先恢复上次位置（详情页返回时回到原滚动高度）。双 rAF 等内容渲染稳定。
        if (restore !== false) try {
            var saved = sessionStorage.getItem('jg-scroll:' + key);
            if (saved !== null) {
                var y = parseFloat(saved);
                if (!isNaN(y) && y > 0) {
                    requestAnimationFrame(function () {
                        requestAnimationFrame(function () { el.scrollTop = y; });
                    });
                }
            }
        } catch (e) { }
        if (tracked === el && trackedKey === key) return;   // 已挂载不重复监听
        tracked = el; trackedKey = key;
        // v1.06.8：双重门控 —— 监听挂在跨页共享的 .jg-main 上，组件销毁后监听仍在：
        // ① 只在绑定时的页面 URL 上才写（否则在下载页滚动会把下载页的位置写进 modslist，
        //    返回列表就回不到原位）；② 页面切换过渡期（__jgScrollLock）不写。
        var pagePath = location.pathname + location.search;
        el.addEventListener('scroll', function () {
            if (ticking) return;
            ticking = true;
            requestAnimationFrame(function () {
                ticking = false;
                if (window.__jgScrollLock) return;
                if (location.pathname + location.search !== pagePath) return;
                try { sessionStorage.setItem('jg-scroll:' + trackedKey, String(el.scrollTop)); } catch (e) { }
            });
        }, { passive: true });
    };
})();


// ------------------ 存档下拉（GSAP easeReverse UI interactions 同款弹性开合） ------------------
(function () {
    window.junigridJs = window.junigridJs || {};

    window.junigridJs.profileDropdown = function (wrapSel, open) {
        var wrap = document.querySelector(wrapSel);
        if (!wrap) return;
        var menu = wrap.querySelector('.jg-profile-menu');
        var arrow = wrap.querySelector('.jg-sort-arrow');
        var items = wrap.querySelectorAll('.jg-profile-item');
        if (!menu || !window.gsap) { wrap.classList.toggle('open', open); return; }

        gsap.killTweensOf([menu, arrow]);
        if (open) {
            wrap.classList.add('open');
            var tl = gsap.timeline();
            tl.to(arrow, { rotation: 180, duration: 0.7, ease: 'elastic.out(1.2, 0.32)' }, 0)
              .fromTo(menu,
                  { autoAlpha: 0, yPercent: -22, scale: 0.72, transformOrigin: 'top center' },
                  { autoAlpha: 1, yPercent: 0, scale: 1, duration: 0.7, ease: 'elastic.out(1.2, 0.32)' }, 0)
              .from(items, { opacity: 0, x: -16, duration: 0.32, ease: 'back.out(2.6)', stagger: 0.05 }, 0.08);
        } else {
            // 退出用 timeScale 加速 + 平滑缓出（demo 里 easeReverse/timeScale 的用意）
            var tl2 = gsap.timeline({
                onComplete: function () {
                    wrap.classList.remove('open');
                    gsap.set(menu, { autoAlpha: 0 });
                }
            });
            tl2.to(arrow, { rotation: 0, duration: 0.28, ease: 'power2.inOut' }, 0)
               .to(menu, { autoAlpha: 0, yPercent: -14, scale: 0.86, duration: 0.24, ease: 'power2.in' }, 0);
        }
    };
})();

// ------------------ 问号帮助按钮的 GSAP 弹性 tooltip ------------------
(function () {
    window.junigridJs = window.junigridJs || {};
    var bound = {};
})();


// ------------------ data-tip 跟随鼠标胶囊提示（与导航栏一致） ------------------
(function () {
    window.junigridJs = window.junigridJs || {};
    var tip = null, curTarget = null;
    function ensure() {
        if (tip) return tip;
        tip = document.createElement('div');
        tip.className = 'jg-cursor-tip';
        document.body.appendChild(tip);
        return tip;
    }
    function show(t, x, y) {
        var el = ensure();
        el.textContent = t;
        el.style.opacity = '1';
        el.style.visibility = 'visible';
        move(x, y);
    }
    function move(x, y) {
        if (!tip) return;
        var w = tip.offsetWidth, h = tip.offsetHeight;
        var px = x + 14, py = y - h - 10;
        if (px + w + 8 > window.innerWidth) px = x - w - 14;
        if (py < 8) py = y + 18;
        tip.style.left = Math.round(px) + 'px';
        tip.style.top = Math.round(py) + 'px';
    }
    function hide() {
        if (!tip) return;
        tip.style.opacity = '0';
        tip.style.visibility = 'hidden';
    }
    document.addEventListener('mouseover', function (e) {
        var t = e.target.closest ? e.target.closest('[data-tip]') : null;
        if (t) { curTarget = t; show(t.getAttribute('data-tip'), e.clientX, e.clientY); }
        else if (curTarget) { curTarget = null; hide(); }
    });
    document.addEventListener('mousemove', function (e) {
        if (curTarget) move(e.clientX, e.clientY);
    }, { passive: true });
    document.addEventListener('mousedown', hide, true);
})();

// ------------------ v1.x：设置页「?」帮助问号的 GSAP 弹性 tooltip ------------------
// hover 弹入（elastic），移开立即消失（不走反向动画）；事件委托，Blazor 重渲染无需重新绑定
(function () {
    function bubbleOf(wrap) { return wrap.querySelector('.jg-help-tip-bubble'); }
    function btnOf(wrap) { return wrap.querySelector('.jg-help-tip-btn'); }
    function close(wrap) {
        if (wrap._tipTl) { wrap._tipTl.kill(); wrap._tipTl = null; }
        var b = bubbleOf(wrap), btn = btnOf(wrap);
        if (b) gsap.set(b, { autoAlpha: 0, y: 14, scale: 0.4, xPercent: -50 });
        if (btn) gsap.set(btn, { scale: 1 });
    }
    document.addEventListener('mouseover', function (e) {
        var wrap = e.target.closest ? e.target.closest('.jg-help-tip') : null;
        if (!wrap) return;
        var bubble = bubbleOf(wrap), btn = btnOf(wrap);
        if (!bubble) return;
        if (wrap._tipTl) wrap._tipTl.kill();
        wrap._tipTl = gsap.timeline({ paused: true })
            .to(bubble, {
                autoAlpha: 1, y: 0, scale: 1, duration: 1,
                ease: 'elastic.out(1.2, 0.3)'
            }, 0)
            .to(btn, {
                scale: 1.3, duration: 0.8,
                ease: 'elastic.out(1.2, 0.3)'
            }, 0);
        wrap._tipTl.timeScale(1).play();
    });
    document.addEventListener('mouseout', function (e) {
        var wrap = e.target.closest ? e.target.closest('.jg-help-tip') : null;
        if (!wrap) return;
        // 仍在问号/气泡内部移动时不关闭
        if (e.relatedTarget && wrap.contains(e.relatedTarget)) return;
        close(wrap);   // 要求：移开直接关闭，不要 GSAP 反向动画
    });
})();



// ============ v0.59.0：mod 详情页图片点击放大（含原位置占位符防塌陷）============
// 点击图片 → 原位置留一个同尺寸占位符（保住盒子）→ 图片移到 modal 正中简单放大；
// 关闭 → 图片放回原位，移除占位符。
(function () {
    if (!window.junigridJs) window.junigridJs = {};

    function initZoom() {
        var modal = document.getElementById('jgFlipModal');
        if (!modal) return;
        var content = modal.querySelector('.jg-flip-content');
        var overlay = modal.querySelector('.jg-flip-overlay');
        var openEl = null, openParent = null, openNext = null, placeholder = null;

        function close() {
            if (!openEl) return;
            var el = openEl;
            modal.classList.remove('open');
            el.classList.remove('jg-zoom-open');
            // 放回原位置（用占位符定位）
            if (placeholder && placeholder.parentNode) {
                placeholder.parentNode.insertBefore(el, placeholder);
                placeholder.remove();
            } else if (openNext && openNext.parentNode === openParent) {
                openParent.insertBefore(el, openNext);
            } else {
                openParent.appendChild(el);
            }
            openEl = null; placeholder = null;
        }

        function open(el) {
            openParent = el.parentNode;
            openNext = el.nextSibling;
            // 建占位符：撑住原盒子尺寸，防塌陷
            var rect = el.getBoundingClientRect();
            placeholder = document.createElement('div');
            placeholder.className = 'jg-zoom-placeholder';
            placeholder.style.width = rect.width + 'px';
            placeholder.style.height = rect.height + 'px';
            // 继承 margin，让上下间距一致
            var cs = window.getComputedStyle(el);
            placeholder.style.margin = cs.margin;
            placeholder.style.display = cs.display === 'inline' ? 'inline-block' : cs.display;
            openParent.insertBefore(placeholder, el);
            // 移动图片到 modal 中央
            content.appendChild(el);
            el.classList.add('jg-zoom-open');
            openEl = el;
            modal.classList.add('open');
        }

        function bind(el) {
            if (el.__zoomBound) return;
            el.__zoomBound = true;
            el.addEventListener('click', function () {
                var car = el.closest && el.closest('.jg-carousel');
                if (car && car.__justDragged) return;   // 拖拽松手不触发放大
                if (openEl === el) close();
                else if (!openEl) open(el);
            });
        }

        document.querySelectorAll('.jg-desc-with-imgs img, .jg-flip-cover, .jg-carousel-img').forEach(bind);
        if (!overlay.__zoomBound) {
            overlay.__zoomBound = true;
            overlay.addEventListener('click', close);
        }
    }

    window.junigridJs.modDetailInit = function () {
        requestAnimationFrame(function () {
            requestAnimationFrame(initZoom);
        });
    };
})();


// ============ v0.69.0：详情页视差图片轮播（滚轮横滚 + 拖拽；放大时暂停滚动）============
(function () {
    if (!window.junigridJs) window.junigridJs = {};
    })();

// v0.69.9：手风琴按需滚动 —— 展开后测量真实内容高度，>360px 才给 .jg-acc 加 .jg-acc-scroll
window.junigridJs = window.junigridJs || {};
window.junigridJs.accMeasureScroll = function () {
    var accs = document.querySelectorAll(".jg-acc.open");
    for (var i = 0; i < accs.length; i++) {
        var acc = accs[i];
        var inner = acc.querySelector(".jg-acc-inner");
        if (!inner) continue;
        // 头部 + 内容真实高度（scrollHeight 忽略 max-height）
        if (inner.scrollHeight > 360) acc.classList.add("jg-acc-scroll");
        else acc.classList.remove("jg-acc-scroll");
    }
    var closed = document.querySelectorAll(".jg-acc:not(.open)");
    for (var j = 0; j < closed.length; j++) closed[j].classList.remove("jg-acc-scroll");
};

// v0.70.0：手风琴 GSAP 弹性动效（参考 easeReverse demo）——
// 展开：箭头 elastic 旋转 + 面板 elastic 展开 + 行 back.out 交错入场；
// 收起：power 系快速回缩（≈2.5x 退出速度），完成后按需测量滚动条。
window.junigridJs = window.junigridJs || {};
window.junigridJs.accAnimate = function (id, opening) {
    var acc = document.getElementById(id);
    if (!acc) return;
    var inner = acc.querySelector(".jg-acc-inner");
    var arrow = acc.querySelector(".jg-acc-arrow");
    if (!inner) return;
    if (typeof gsap === "undefined") {           // 无 GSAP 时退化为直接显隐
        inner.style.height = opening ? "" : "52px";
        if (window.junigridJs.accMeasureScroll) window.junigridJs.accMeasureScroll();
        return;
    }
    if (acc.__tl) { acc.__tl.kill(); acc.__tl = null; }
    var rows = acc.querySelectorAll(".jg-acc-body .jg-acc-row, .jg-acc-body .jg-req-table, .jg-acc-body > span, .jg-acc-body > div");
    if (opening) {
        var target = Math.min(inner.scrollHeight, 360);
        acc.__tl = gsap.timeline({
            onComplete: function () {
                gsap.set(inner, { clearProps: "height" });
                if (window.junigridJs.accMeasureScroll) window.junigridJs.accMeasureScroll();
            }
        })
        .to(arrow, { rotation: 180, duration: 0.9, ease: "elastic.out(1.2,0.3)" }, 0)
        .fromTo(inner, { height: 52 }, { height: target, duration: 1.0, ease: "elastic.out(1.2,0.45)" }, 0)
        .from(rows, { opacity: 0, x: -18, duration: 0.45, ease: "back.out(2.5)", stagger: 0.05, clearProps: "opacity,transform" }, 0.12);
    } else {
        acc.__tl = gsap.timeline({
            onComplete: function () {
                gsap.set(inner, { clearProps: "height" });
                if (window.junigridJs.accMeasureScroll) window.junigridJs.accMeasureScroll();
            }
        })
        .to(arrow, { rotation: 0, duration: 0.4, ease: "power2.inOut" }, 0)
        .to(inner, { height: 52, duration: 0.45, ease: "power3.out" }, 0);
    }
};

// v0.70.1：用户头像卡片 —— easeReverse 源码同款：头像 elastic 放大 + 气泡弹出
// v1.08.0：hover 自动开关废除 —— 移向气泡途中鼠标会扫过下方 mod 卡，离开头像即开始关闭倒计时，
// 开启动画期间气泡命中区域又小（scale 0.4 起步），「卡片开着却自己关了」且概率性复现。
// 改纯手动：点击头像开启、再点头像关闭；点击卡片外任意处收回；卡片内（查看主页/退出登录）不关。
// v1.08.1：头像 hover 动画保留 —— 悬停弹性放大 / 移开还原，仅作反馈，不带动卡片开关。
window.junigridJs.userTipInit = function (wrapId, bubbleId) {
    var wrap = document.getElementById(wrapId);
    var bubble = document.getElementById(bubbleId);
    if (!wrap || !bubble || wrap.__tipBound) return;
    wrap.__tipBound = true;
    var avatar = wrap.querySelector(".jg-user-tip-avatar");
    if (typeof gsap === "undefined") { wrap.classList.add("jg-user-tip-nogsap"); return; }
    gsap.set(bubble, { autoAlpha: 0, y: 14, scale: 0.4, transformOrigin: "top right" });
    gsap.set(avatar, { scale: 1, transformOrigin: "center center" });
    // v1.08.1：开卡时间线只管气泡 —— 头像缩放独立出来给 hover 用，两边不再互相打架
    var tl = gsap.timeline({ paused: true })
        .to(bubble, { autoAlpha: 1, y: 0, scale: 1, duration: 1.0, ease: "elastic.out(1.2, 0.3)" }, 0);

    // hover 动画保留：悬停头像 elastic 放大，移开快速还原（纯反馈，不带动卡片开关）
    // v1.08.2：每次现查当前头像元素 —— 头像数据到位后 Blazor 会把首字母兜底 div 换成 img，
    // 绑定时抓到的旧元素已脱离 DOM（这就是「Y 头像有动画、真头像没动画」的原因）
    function avatarEl() { return wrap.querySelector(".jg-user-tip-avatar"); }
    function avatarScale(v, quick) {
        var el = avatarEl();
        if (!el) return;
        gsap.killTweensOf(el);
        gsap.to(el, { scale: v, transformOrigin: "center center",
            duration: quick ? 0.35 : 0.9, ease: quick ? "power2.out" : "elastic.out(1.2, 0.3)" });
    }
    wrap.addEventListener("mouseenter", function () { if (!isOpen) avatarScale(1.15); });
    wrap.addEventListener("mouseleave", function () { if (!isOpen) avatarScale(1, true); });

    var isOpen = false;
    function setOpen(v) {
        if (v === isOpen) return;
        isOpen = v;
        // 气泡默认 pointer-events:none（.open 时才放开，见 app.css）—— 点击开合必须同步，否则卡片开着点不了按钮
        wrap.classList.toggle("open", v);
        if (v) { avatarScale(1.15); tl.timeScale(1).play(); return; }
        // 收回沿用既有约定：不做反向动画，瞬间归位
        tl.pause(0);
        gsap.set(bubble, { autoAlpha: 0, y: 14, scale: 0.4 });
        var el = avatarEl();
        if (el) { gsap.killTweensOf(el); gsap.set(el, { scale: 1, transformOrigin: "center center" }); }
    }
    // 绑在 wrap 上而非 avatar 元素本身：头像数据到位后 img/fallback 兄弟互换，绑 wrap 不丢监听
    wrap.addEventListener("click", function (e) {
        if (bubble.contains(e.target)) return;
        e.stopPropagation();
        setOpen(!isOpen);
    });
    document.addEventListener("click", function (e) {
        if (isOpen && !wrap.contains(e.target)) setOpen(false);
    });
};

// v1.0.17：标题栏自更新按钮悬浮气泡 —— easeReverse demo 问号气泡同款：
// elastic 弹入；移开不做反向动画，瞬间归位（约定同 userTipInit 的收回）。
// 入参接受 ElementReference（元素对象）或 id 字符串。
window.junigridJs.updTipInit = function (wrap, bubble) {
    if (typeof wrap === "string") wrap = document.getElementById(wrap);
    if (typeof bubble === "string") bubble = document.getElementById(bubble);
    if (!wrap || !bubble || wrap.__updTipBound) return;
    wrap.__updTipBound = true;
    if (typeof gsap === "undefined") { wrap.classList.add("jg-upd-tip-nogsap"); return; }
    // 居中用 xPercent:-50 交给 GSAP 托管 —— CSS translateX(-50%) 会被 GSAP 的 transform 覆盖
    gsap.set(bubble, { autoAlpha: 0, xPercent: -50, y: -14, scale: 0.4, transformOrigin: "top center" });
    var tl = gsap.timeline({ paused: true })
        .to(bubble, { autoAlpha: 1, y: 0, scale: 1, duration: 1.0, ease: "elastic.out(1.2, 0.3)" }, 0);
    wrap.addEventListener("mouseenter", function () { tl.timeScale(1).play(); });
    wrap.addEventListener("mouseleave", function () {
        tl.pause(0);
        gsap.set(bubble, { autoAlpha: 0, xPercent: -50, y: -14, scale: 0.4 });
    });
};

// v1.04.0：聚焦任意元素（搜索框叉号清空内容后重新获得焦点用）
window.junigridJs.focusElement = function (sel) {
    var el = typeof sel === "string" ? document.querySelector(sel) : sel;
    if (el) { try { el.focus(); } catch (e) { } }
};

// v1.04.0：详情页 by 作者名 —— blur 高光（深色 #3d3d3d / 浅色 #ffffff 扫入）+ hover 头像预览气泡。
// easeReverse demo 同款：elastic 弹入 + 反向快速退场（exit timeScale 2.5x）。
window.junigridJs.authorTipInit = function (wrapId, bubbleId) {
    var wrap = document.getElementById(wrapId);
    var bubble = document.getElementById(bubbleId);
    if (!wrap || !bubble) return;
    var nameBtn = wrap.querySelector(".jg-author-name");
    var hl = wrap.querySelector(".jg-author-hl");
    if (typeof gsap === "undefined") { wrap.classList.add("jg-author-nogsap"); return; }
    if (wrap.__authorBound) return;   // 已绑定：不重复绑定，也不打断进行中的 hover 动画
    wrap.__authorBound = true;

    gsap.set(hl, { scaleX: 0, transformOrigin: "left center" });
    // v1.05.0：xPercent:-50 让气泡水平居中在作者名正上方（箭头才指向名字，不再偏到封面上去）
    gsap.set(bubble, { autoAlpha: 0, y: 10, scale: 0.5, xPercent: -50, transformOrigin: "bottom center" });

    // hover 时间线：高光扫过 + 气泡 elastic 弹出
    var tl = gsap.timeline({ paused: true })
        .to(hl, { scaleX: 1, duration: 0.55, ease: "back.out(1.7)", easeReverse: "power2.out" }, 0)
        .to(bubble, { autoAlpha: 1, y: 0, scale: 1, duration: 0.9, ease: "elastic.out(1.2, 0.3)", easeReverse: "power3.in" }, 0.08);

    // 首次渲染高光自动扫入一次（blur-highlight 加载动效），随后回零等 hover
    gsap.timeline({ delay: 0.35 })
        .to(hl, { scaleX: 1, duration: 0.6, ease: "back.out(1.7)" })
        .to(hl, {
            scaleX: 0, transformOrigin: "right center", duration: 0.35, ease: "power2.in",
            onComplete: function () { gsap.set(hl, { transformOrigin: "left center" }); }
        }, "+=0.9");

    var closeTimer = null;
    function openTl() { if (closeTimer) { clearTimeout(closeTimer); closeTimer = null; } tl.timeScale(1).play(); }
    function closeTl() {
        if (closeTimer) clearTimeout(closeTimer);
        closeTimer = setTimeout(function () {
            // v1.06.3：取消关闭动画 —— pause(0) 把时间线瞬间拨回起点（气泡/高光直接回到初始态）；
            // 打开时的 elastic 弹出动画不受影响
            tl.pause(0);
        }, 160);
    }
    wrap.addEventListener("mouseenter", openTl);
    wrap.addEventListener("mouseleave", closeTl);
    bubble.addEventListener("mouseenter", openTl);
    bubble.addEventListener("mouseleave", closeTl);
};
window.junigridJs.setScroll = function (sel, y) { var el = document.querySelector(sel); if (el) el.scrollTop = y; };

// v0.71.1：等容器 scrollHeight 足够再设 scrollTop（图片未加载导致高度不够时不会被钳回顶部）
// v0.71.6：抗双重干扰 —— ①更新检测把列表 display:none（骨架屏期 scrollHeight 塌成 0）
// ②返回瞬间 playPageEnter 给 .jg-main 套了 transform 入场动画，transform 会让
// scrollTop 设置无效/被重置。先摘掉 transform，再轮询等列表真实撑高后落位。
// v0.72.0：重写为「就绪门控 + 离开时到底标记」——
// ① 旧 "y > limit" 分支在返回时【猜】用户离开前在底部，列表被封面懒加载
//    (<img loading=lazy>) 撑高的过渡期会误触发：先落到偏小的 limit 并收工，
//    行高膨胀后停错位置。现在到底与否由离开时 getScrollState 记录的 atBottom
//    决定，不再猜：target = atBottom ? limit : min(y, limit)。
// ② 就绪门控：列表已渲染（limit>0）且 scrollHeight 连续 ~4 轮不变才算就绪，
//    懒加载/数据渲染的渐进撑高期绝不动手。
// ③ 落位后若高度继续变化（图片迟到）且用户未动，持续跟随重落位；
//    wheel/touchstart/pointerdown 一律让位收工。
// v0.72.4：返回恢复期间隐藏行列表，落位后同帧显示 —— 消除「列表先在顶部闪一帧」。
// 标记打在 MainLayout 的 .jg-main 上（该元素跨导航持久，class 不会因切页被 Blazor 重写）。
// CSS: .jg-restore-pending .jg-modrows { visibility: hidden; }
window.junigridJs.markScrollRestorePending = function (sel) {
    var el = document.querySelector(sel);
    if (el) el.classList.add('jg-restore-pending');
};
window.junigridJs.cancelScrollRestore = function (sel) {
    var el = document.querySelector(sel);
    if (el) el.classList.remove('jg-restore-pending');
};

window.junigridJs.setScrollWhenReady = function (sel, y, atBottom, report) {
    var el = document.querySelector(sel);
    if (!el) return;
    // v0.72.2：诊断回报 —— 结束时向 C# 回一次 (target, 最终scrollTop, 轮数, 原因)
    var reportFn = report && report.invokeMethodAsync
        ? function (t, f, tr, r) { try { report.invokeMethodAsync('Report', t, f, tr, r); } catch (e) { } }
        : function () { };
    var revealIfPending = function () { el.classList.remove('jg-restore-pending'); };
    var tries = 0, done = 0, lastH = -1, userTouched = false;
    var onUser = function () { userTouched = true; };
    el.addEventListener('wheel', onUser, { passive: true });
    el.addEventListener('touchstart', onUser, { passive: true });
    el.addEventListener('pointerdown', onUser, { passive: true });
    var finish = function () {
        el.removeEventListener('wheel', onUser);
        el.removeEventListener('touchstart', onUser);
        el.removeEventListener('pointerdown', onUser);
    };
    var tick = function () {
        // 每轮持续压制入场动画 transform（CSS 关键帧会把它覆盖回去，必须反复压）
        el.classList.remove('jg-page-enter');
        if (el.style.transform !== 'none') el.style.transform = 'none';

        if (userTouched) { revealIfPending(); reportFn(y, el.scrollTop, tries, 'user-touched'); finish(); return; }   // 用户手动滚动，立即让位
        tries++;
        var limit = el.scrollHeight - el.clientHeight;
        if (limit < 0) limit = 0;
        var h = el.scrollHeight;
        // 真实行列表已显示（骨架屏期 .jg-modrows 是 display:none）
        var rows = document.querySelector('.jg-modrows');
        var rowsShown = rows && rows.offsetHeight > 0;

        // v0.72.3：第一帧就落位，不等高度稳定 —— 旧的"稳定 4 轮才动手"让列表在顶部
        // 停 1~2s 再瞬移（用户看到"先顶后跳"的闪屏）。现在每轮都重落位：
        // 高度随后续图片加载增长时跟着重落，用户无感；收工仍要求落位精确且高度稳定。
        if (rowsShown && limit > 0) {
            var target = atBottom ? limit : Math.min(y, limit);
            el.scrollTop = target;
            var placed = Math.abs(el.scrollTop - target) <= 1;
            var heightStable = (h === lastH);
            // v0.72.4：首次成功落位的同一帧就显示行列表 —— 顶部状态从未被绘制过
            if (placed) revealIfPending();
            done = (placed && heightStable) ? done + 1 : 0;
            if (done >= 16) { reportFn(target, el.scrollTop, tries, 'placed-stable'); finish(); return; }   // 稳定约 1.5s，收工
        } else {
            done = 0;
        }
        lastH = h;
        if (tries >= 300) {           // 兜底：~30s 仍未就绪则取当前可达最大，不无条件顶回
            // v0.72.1：limit===0（列表始终没渲染出高度）时绝不能落 0 —— 那就是「回到顶部」
            var fbTarget = limit > 0 ? Math.min(y, limit) : el.scrollTop;
            if (limit > 0) el.scrollTop = fbTarget;
            revealIfPending();
            reportFn(fbTarget, el.scrollTop, tries, 'timeout');
            finish();
            return;
        }
        setTimeout(tick, 90);
    };
    tick();
};

// v0.72.0：离开列表页前取滚动快照 —— scrollTop + 是否在底部（恢复时不再靠猜）。
// 底部容差 4px，覆盖亚像素舍入。
window.junigridJs.getScrollState = function (sel) {
    var el = document.querySelector(sel);
    if (!el) return { y: 0, atBottom: false };
    var limit = el.scrollHeight - el.clientHeight;
    return { y: el.scrollTop, atBottom: limit > 0 && el.scrollTop >= limit - 4 };
};

// v0.71.2：滚动位置双写 sessionStorage（scrollSpy 同 key 格式，返回恢复时双保险）。
// v0.72.0：atBottom 另存独立 key —— 原 key 是纯数字格式，scrollSpy 用 parseFloat 读它，不能动。
window.junigridJs.saveScrollKey = function (key, y, atBottom) {
    try {
        sessionStorage.setItem("jg-scroll:" + key, String(y));
        sessionStorage.setItem("jg-scroll:" + key + ":ab", atBottom ? "1" : "0");
    } catch (e) { }
};

// ─── v0.74.0：Nexus 搜索岛（GSAP easeReverse：back.out(2) 展开 / power2.out 收起）───
// v1.06.2：恢复放大镜按钮（按钮紧跟 Nexus logo，点击输入框向右展开）；无按钮时退化为常驻展开模式。
junigridJs.searchIslandInit = function (islandId, btnId, inputId) {
    var island = document.getElementById(islandId);
    if (!island || island.dataset.islandBound) return;
    island.dataset.islandBound = "1";
    var field = island.querySelector('.jg-island-field');
    var input = document.getElementById(inputId);
    var btn = document.getElementById(btnId);
    var isOpen = false;
    // 无按钮（常驻展开模式）：只绑搜索历史显隐（bindHistory 为函数声明，提升可用）。
    if (!btn) { bindHistory(); return; }

    if (typeof gsap === 'undefined') { // 无 GSAP 降级：class 切换
        btn.addEventListener('click', function () {
            isOpen = !isOpen;
            if (!isOpen && input && input.value && input.value.trim().length > 0) { isOpen = true; return; } // v1.01.0：有内容不收起
            island.classList.toggle('open', isOpen);
            if (isOpen && input) input.focus();
        });
        bindHistory();
        return;
    }
    // easeReverse 需 GSAP 3.13+；低版本自动降级为对称缓动
    var erOK = parseFloat(gsap.version || '0') >= 3.13;
    gsap.set(island, { width: 40 });
    gsap.set(field, { autoAlpha: 0, width: 0 });
    var tl = gsap.timeline({ paused: true })
        .to(island, { width: 300, duration: 0.7, ease: 'back.out(2)', easeReverse: erOK ? 'power2.out' : undefined }, 0)
        .to(field, { autoAlpha: 1, width: 244, duration: 0.35, ease: 'power2.out', easeReverse: erOK ? 'power2.in' : undefined }, 0.18);

    function hasContent() { return !!(input && input.value && input.value.trim().length > 0); }
    function toggle(force) {
        isOpen = (typeof force === 'boolean') ? force : !isOpen;
        if (!isOpen && hasContent()) return; // v1.01.0：有内容时不允许关闭，避免误丢输入
        btn.setAttribute('aria-expanded', isOpen);
        if (isOpen) {
            tl.timeScale(1).play();
            setTimeout(function () { if (input) input.focus(); }, 380);
        } else {
            tl.timeScale(1.5).reverse(); // 收起稍快，跟手
        }
    }
    btn.addEventListener('click', function (e) { e.stopPropagation(); toggle(); });
    document.addEventListener('click', function (e) { if (isOpen && !island.contains(e.target)) toggle(false); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && isOpen) { toggle(false); btn.focus(); } });
    bindHistory();   // v1.06.2：按钮模式的展开逻辑恢复后，历史面板绑定也要接回（v1.05.4 起只在无按钮路径调用）

    // ─── v1.05.1：搜索历史面板显隐 —— 完全由 JS 驱动。
    // 实测：JS input.focus() 触发的 focus 事件到不了 Blazor（@onfocus 永不触发），
    // 所以面板显隐不再走 C# 状态，改为 toggle 外层容器的 .history-open class。
    // v1.05.4：抽出为 bindHistory()，无搜索按钮的常驻模式也要绑定。───
    function bindHistory() {
    var histWrap = island.closest('.jg-island-wrap');
    if (histWrap && !island.dataset.histBound) {
        island.dataset.histBound = '1';
        var hideTimer = null;
        function showHist() { if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; } histWrap.classList.add('history-open'); }
        function hideHist() { histWrap.classList.remove('history-open'); }
        function hideHistSoon() { if (hideTimer) clearTimeout(hideTimer); hideTimer = setTimeout(hideHist, 220); }
        input.addEventListener('focus', showHist);
        input.addEventListener('click', showHist);
        input.addEventListener('blur', hideHistSoon);
        input.addEventListener('keydown', function (e) { if (e.key === 'Escape') hideHist(); });
        // 鼠标在面板内保持展开（focus 去了面板也不会闪关）
        histWrap.addEventListener('mouseover', function (e) {
            if (e.target.closest && e.target.closest('.jg-search-history')) { if (hideTimer) clearTimeout(hideTimer); }
        });
        // 点历史行 / 清空历史 → 执行完动作即收起（删除单条不收，方便连续删）
        histWrap.addEventListener('click', function (e) {
            if (!e.target.closest) return;
            if (e.target.closest('.jg-search-history-row') || e.target.closest('.jg-search-history-clearall')) hideHist();
        });
    }
    } // bindHistory()
};

// v0.93.0：详情页返回 —— 回退到来源页（Blazor Router 监听 popstate 接管导航）
window.junigridJs.goBack = function () {
    if (window.history.length > 1) window.history.back();
    else window.location.href = "/mods";
};

// v0.93.0：DepthText 指针视差 + 空闲自动环绕（React Bits 原版逻辑的精简移植）
window.junigridJs.depthTextInit = function (el, tilt) {
    if (!el || el.__dtInit) return; el.__dtInit = true;
    var stage = el.querySelector(".depth-text__stage");
    if (!stage) return;
    var base = { x: -tilt * 0.32, y: tilt * 0.42 };
    var cur = { x: base.x, y: base.y }, tgt = { x: base.x, y: base.y };
    var t0 = performance.now();
    function loop(now) {
        if (!el.__dtHover) {
            var o = ((now - t0) / 1000) * 0.35 * Math.PI * 2;
            tgt.x = base.x + Math.sin(o) * tilt * 0.18;
            tgt.y = base.y + Math.cos(o * 0.85) * tilt * 0.18;
        }
        cur.x += (tgt.x - cur.x) * 0.14;
        cur.y += (tgt.y - cur.y) * 0.14;
        stage.style.transform = "rotateX(" + cur.x.toFixed(3) + "deg) rotateY(" + cur.y.toFixed(3) + "deg)";
        requestAnimationFrame(loop);
    }
    el.addEventListener("pointermove", function (ev) {
        var rc = el.getBoundingClientRect(); if (!rc.width || !rc.height) return;
        el.__dtHover = true;
        var x = Math.max(-1, Math.min(1, (ev.clientX - (rc.left + rc.width / 2)) / (rc.width * 0.8)));
        var y = Math.max(-1, Math.min(1, (ev.clientY - (rc.top + rc.height / 2)) / (rc.height * 0.8)));
        tgt.x = base.x - y * tilt; tgt.y = base.y + x * tilt;
    });
    el.addEventListener("pointerleave", function () { el.__dtHover = false; tgt.x = base.x; tgt.y = base.y; });
    requestAnimationFrame(loop);
};

/* ─── v1.00.0：Grainient 背景（React Bits 移植，原生 WebGL2 实现，无 ogl 依赖）─── */
window.junigridJs = window.junigridJs || {};
(function () {
    var VERT = "#version 300 es\nin vec2 position;\nvoid main() { gl_Position = vec4(position, 0.0, 1.0); }\n";
    var FRAG = `#version 300 es
precision highp float;
uniform vec2 iResolution;
uniform float iTime;
uniform float uTimeSpeed;
uniform float uColorBalance;
uniform float uWarpStrength;
uniform float uWarpFrequency;
uniform float uWarpSpeed;
uniform float uWarpAmplitude;
uniform float uBlendAngle;
uniform float uBlendSoftness;
uniform float uRotationAmount;
uniform float uNoiseScale;
uniform float uGrainAmount;
uniform float uGrainScale;
uniform float uGrainAnimated;
uniform float uContrast;
uniform float uGamma;
uniform float uSaturation;
uniform vec2 uCenterOffset;
uniform float uZoom;
uniform vec3 uColor1;
uniform vec3 uColor2;
uniform vec3 uColor3;
uniform float uLightMode;
out vec4 fragColor;
#define S(a,b,t) smoothstep(a,b,t)
mat2 Rot(float a){float s=sin(a),c=cos(a);return mat2(c,-s,s,c);} 
vec2 hash(vec2 p){p=vec2(dot(p,vec2(2127.1,81.17)),dot(p,vec2(1269.5,283.37)));return fract(sin(p)*43758.5453);} 
float noise(vec2 p){vec2 i=floor(p),f=fract(p),u=f*f*(3.0-2.0*f);float n=mix(mix(dot(-1.0+2.0*hash(i+vec2(0.0,0.0)),f-vec2(0.0,0.0)),dot(-1.0+2.0*hash(i+vec2(1.0,0.0)),f-vec2(1.0,0.0)),u.x),mix(mix(dot(-1.0+2.0*hash(i+vec2(0.0,1.0)),f-vec2(0.0,1.0)),dot(-1.0+2.0*hash(i+vec2(1.0,1.0)),f-vec2(1.0,1.0)),u.x),u.y);return 0.5+0.5*n;}
void mainImage(out vec4 o, vec2 C){
  float t=iTime*uTimeSpeed;
  vec2 uv=C/iResolution.xy;
  float ratio=iResolution.x/iResolution.y;
  vec2 tuv=uv-0.5+uCenterOffset;
  tuv/=max(uZoom,0.001);

  float degree=noise(vec2(t*0.1,tuv.x*tuv.y)*uNoiseScale);
  tuv.y*=1.0/ratio;
  tuv*=Rot(radians((degree-0.5)*uRotationAmount+180.0));
  tuv.y*=ratio;

  float frequency=uWarpFrequency;
  float ws=max(uWarpStrength,0.001);
  float amplitude=uWarpAmplitude/ws;
  float warpTime=t*uWarpSpeed;
  tuv.x+=sin(tuv.y*frequency+warpTime)/amplitude;
  tuv.y+=sin(tuv.x*(frequency*1.5)+warpTime)/(amplitude*0.5);

  vec3 colLav=uColor1;
  vec3 colOrg=uColor2;
  vec3 colDark=uColor3;
  float b=uColorBalance;
  float s=max(uBlendSoftness,0.0);
  mat2 blendRot=Rot(radians(uBlendAngle));
  float blendX=(tuv*blendRot).x;
  float edge0=-0.3-b-s;
  float edge1=0.2-b+s;
  float v0=0.5-b+s;
  float v1=-0.3-b-s;
  vec3 layer1=mix(colDark,colOrg,S(edge0,edge1,blendX));
  vec3 layer2=mix(colOrg,colLav,S(edge0,edge1,blendX));
  vec3 col=mix(layer1,layer2,S(v0,v1,tuv.y));

  vec2 grainUv=uv*max(uGrainScale,0.001);
  if(uGrainAnimated>0.5){grainUv+=vec2(iTime*0.05);} 
  float grain=fract(sin(dot(grainUv,vec2(12.9898,78.233)))*43758.5453);
  col+=(grain-0.5)*uGrainAmount;

  col=(col-0.5)*uContrast+0.5;
  float luma=dot(col,vec3(0.2126,0.7152,0.0722));
  col=mix(vec3(luma),col,uSaturation);
  col=pow(max(col,0.0),vec3(1.0/max(uGamma,0.001)));
  col=clamp(col,0.0,1.0);
  if(uLightMode>0.5){
    float energy=max(max(col.r,col.g),col.b);
    vec3 hue=col/max(energy,0.001);
    float chroma=length(col-vec3(dot(col,vec3(0.333333))));
    float coverage=clamp(0.12+chroma*1.15+energy*0.18,0.0,0.88);
    col=mix(vec3(1.0),clamp(hue*0.58+col*0.18,0.0,1.0),coverage);
  }

  o=vec4(col,1.0);
}
void main(){
  vec4 o=vec4(0.0);
  mainImage(o,gl_FragCoord.xy);
  fragColor=o;
}
`;
    function hexToRgb(h) {
        var r = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(h);
        if (!r) return [1, 1, 1];
        return [parseInt(r[1], 16) / 255, parseInt(r[2], 16) / 255, parseInt(r[3], 16) / 255];
    }
    var states = new WeakMap();
    window.junigridJs.grainientInit = function (sel, opts) {
        var el = document.querySelector(sel);
        if (!el || states.has(el)) return;
        var o = Object.assign({
            color1: '#FFFFFF', color2: '#FB923C', color3: '#F5F5DC',
            timeSpeed: 0.25, colorBalance: 0.0, warpStrength: 1.0, warpFrequency: 5.0,
            warpSpeed: 2.0, warpAmplitude: 50.0, blendAngle: 0.0, blendSoftness: 0.05,
            rotationAmount: 500.0, noiseScale: 2.0, grainAmount: 0.1, grainScale: 2.0,
            grainAnimated: false, contrast: 1.5, gamma: 1.0, saturation: 1.0,
            centerX: 0.0, centerY: 0.0, zoom: 0.9
        }, opts || {});
        var canvas = document.createElement('canvas');
        canvas.style.cssText = 'width:100%;height:100%;display:block;';
        el.appendChild(canvas);
        var gl = canvas.getContext('webgl2', { alpha: true, antialias: false });
        if (!gl) { try { el.removeChild(canvas); } catch (e) { } return; }
        function sh(type, src) {
            var s = gl.createShader(type);
            gl.shaderSource(s, src); gl.compileShader(s);
            return s;
        }
        var prog = gl.createProgram();
        gl.attachShader(prog, sh(gl.VERTEX_SHADER, VERT));
        gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, FRAG));
        gl.linkProgram(prog);
        if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) { try { el.removeChild(canvas); } catch (e) { } return; }
        gl.useProgram(prog);
        var buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buf);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        var loc = gl.getAttribLocation(prog, 'position');
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
        var U = {};
        ['iTime', 'iResolution', 'uTimeSpeed', 'uColorBalance', 'uWarpStrength', 'uWarpFrequency',
         'uWarpSpeed', 'uWarpAmplitude', 'uBlendAngle', 'uBlendSoftness', 'uRotationAmount', 'uNoiseScale',
         'uGrainAmount', 'uGrainScale', 'uGrainAnimated', 'uContrast', 'uGamma', 'uSaturation',
         'uCenterOffset', 'uZoom', 'uColor1', 'uColor2', 'uColor3', 'uLightMode'].forEach(function (n) {
            U[n] = gl.getUniformLocation(prog, n);
        });
        var c1 = hexToRgb(o.color1), c2 = hexToRgb(o.color2), c3 = hexToRgb(o.color3);
        gl.uniform1f(U.uTimeSpeed, o.timeSpeed);
        gl.uniform1f(U.uColorBalance, o.colorBalance);
        gl.uniform1f(U.uWarpStrength, o.warpStrength);
        gl.uniform1f(U.uWarpFrequency, o.warpFrequency);
        gl.uniform1f(U.uWarpSpeed, o.warpSpeed);
        gl.uniform1f(U.uWarpAmplitude, o.warpAmplitude);
        gl.uniform1f(U.uBlendAngle, o.blendAngle);
        gl.uniform1f(U.uBlendSoftness, o.blendSoftness);
        gl.uniform1f(U.uRotationAmount, o.rotationAmount);
        gl.uniform1f(U.uNoiseScale, o.noiseScale);
        gl.uniform1f(U.uGrainAmount, o.grainAmount);
        gl.uniform1f(U.uGrainScale, o.grainScale);
        gl.uniform1f(U.uGrainAnimated, o.grainAnimated ? 1 : 0);
        gl.uniform1f(U.uContrast, o.contrast);
        gl.uniform1f(U.uGamma, o.gamma);
        gl.uniform1f(U.uSaturation, o.saturation);
        gl.uniform2f(U.uCenterOffset, o.centerX, o.centerY);
        gl.uniform1f(U.uZoom, o.zoom);
        gl.uniform3f(U.uColor1, c1[0], c1[1], c1[2]);
        gl.uniform3f(U.uColor2, c2[0], c2[1], c2[2]);
        gl.uniform3f(U.uColor3, c3[0], c3[1], c3[2]);
        gl.uniform1f(U.uLightMode, 0);
        var dpr = Math.min(window.devicePixelRatio || 1, 2);
        function resize() {
            var r = el.getBoundingClientRect();
            var w = Math.max(1, Math.floor(r.width * dpr));
            var h = Math.max(1, Math.floor(r.height * dpr));
            if (canvas.width !== w || canvas.height !== h) {
                canvas.width = w; canvas.height = h;
                gl.viewport(0, 0, w, h);
            }
            gl.uniform2f(U.iResolution, w, h);
        }
        var ro = new ResizeObserver(resize);
        ro.observe(el);
        resize();
        var raf = 0, t0 = performance.now();
        function loop(t) {
            if (!el.isConnected) { ro.disconnect(); states.delete(el); return; }  // 元素被 Blazor 移除 → 自清理
            gl.uniform1f(U.iTime, (t - t0) * 0.001);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            raf = requestAnimationFrame(loop);
        }
        raf = requestAnimationFrame(loop);
        states.set(el, { stop: function () { cancelAnimationFrame(raf); ro.disconnect(); } });
    };
    })();




// ─── v1.06.7：任务悬浮窗光标透视倾斜（gsap cursor-driven-perspective-tilt demo 同款）───
// 外层 rotationX/Y 用 quickTo 平滑跟随光标，内层文字反向轻移产生视差；离开复位。
junigridJs.taskDockTilt = function (sel) {
    var el = document.querySelector(sel);
    if (!el || !window.gsap || el.__tiltBound) return;
    el.__tiltBound = true;
    gsap.set(el, { transformPerspective: 650, transformStyle: 'preserve-3d' });
    var inner = el.querySelector('.jg-taskdock-body');
    var outerRX = gsap.quickTo(el, 'rotationX', { ease: 'power3', duration: 0.35 });
    var outerRY = gsap.quickTo(el, 'rotationY', { ease: 'power3', duration: 0.35 });
    var innerX = inner ? gsap.quickTo(inner, 'x', { ease: 'power3', duration: 0.35 }) : null;
    var innerY = inner ? gsap.quickTo(inner, 'y', { ease: 'power3', duration: 0.35 }) : null;
    el.addEventListener('pointermove', function (e) {
        if (el.__dragging) {   // 拖动期间停用倾斜，避免 transform 干扰拖动定位
            outerRX(0); outerRY(0);
            if (innerX) innerX(0);
            if (innerY) innerY(0);
            return;
        }
        var r = el.getBoundingClientRect();
        if (!r.width || !r.height) return;
        var nx = (e.clientX - r.left) / r.width;
        var ny = (e.clientY - r.top) / r.height;
        outerRX(gsap.utils.interpolate(10, -10, ny));
        outerRY(gsap.utils.interpolate(-10, 10, nx));
        if (innerX) innerX(gsap.utils.interpolate(-4, 4, nx));
        if (innerY) innerY(gsap.utils.interpolate(-4, 4, ny));
    });
    el.addEventListener('pointerleave', function () {
        outerRX(0); outerRY(0);
        if (innerX) innerX(0);
        if (innerY) innerY(0);
    });
};

// ─── v1.07：TaskDock 胶囊 ↔ 圆 平滑形变（gsap smooth-morph demo 同款观感）───
// 全部完成时整颗胶囊收缩成 56px 圆、文字淡出、白色对号弹出；来任务时再展开回胶囊。
// 形变本体 = 宽/高/内边距的缓动（border-radius 恒 999px，宽=高时自然成圆），
// 配 power3.inOut 得到 smooth-morph 的「果冻变形」质感。文字留在 DOM 里只动透明度，
// 既保住 Blazor 重渲染不换节点，也保证收圆后量回胶囊自然尺寸有内容可依。
junigridJs.taskDockMorph = function (sel, done, animate) {
    var el = document.querySelector(sel);
    if (!el) return;
    if (el.__morphTl) { el.__morphTl.kill(); el.__morphTl = null; }
    var body = el.querySelector('.jg-taskdock-body');
    var check = el.querySelector('.jg-taskdock-check');
    if (!window.gsap) {
        // 无 gsap 兜底：直接切最终态，靠 CSS 过渡
        el.classList.toggle('done', !!done);
        return;
    }
    var SIZE = 56;
    if (done) {
        el.classList.add('done');
        if (!animate) {
            gsap.set(el, { width: SIZE, minWidth: SIZE, height: SIZE, minHeight: SIZE, paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0 });
            gsap.set(body, { opacity: 0, scale: 0.6 });
            gsap.set(check, { opacity: 1, scale: 1, rotation: 0 });
            return;
        }
        var tl = gsap.timeline();
        tl.set(body, { opacity: 0 })   // 文字瞬间消失，不做渐隐 —— 完成就是直接变对号
          .to(el, { width: SIZE, minWidth: SIZE, height: SIZE, minHeight: SIZE,
                    paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0,
                    duration: 0.55, ease: 'power3.inOut' }, 0)
          .fromTo(check, { opacity: 0, scale: 0.4, rotation: -30 },
                         { opacity: 1, scale: 1, rotation: 0, duration: 0.45, ease: 'back.out(2.2)' }, 0.28);
        el.__morphTl = tl;
    } else {
        el.classList.remove('done');
        if (!animate) { gsap.set(body, { opacity: 1, scale: 1 }); gsap.set(check, { opacity: 0 }); return; }
        // 收圆时内联样式盖住了自然尺寸 —— 先摘掉量一次真实胶囊大小，再从圆展开过去
        var props = ['width', 'min-width', 'height', 'min-height', 'padding-top', 'padding-bottom', 'padding-left', 'padding-right'];
        var saved = props.map(function (p) { return [p, el.style.getPropertyValue(p), el.style.getPropertyPriority(p)]; });
        props.forEach(function (p) { el.style.removeProperty(p); });
        var w = el.offsetWidth, h = el.offsetHeight;
        var cs = getComputedStyle(el);
        var padT = parseFloat(cs.paddingTop) || 10, padB = parseFloat(cs.paddingBottom) || 10;
        var padL = parseFloat(cs.paddingLeft) || 22, padR = parseFloat(cs.paddingRight) || 22;
        saved.forEach(function (s) { el.style.setProperty(s[0], s[1], s[2]); });
        var back = gsap.timeline({
            onComplete: function () {
                props.forEach(function (p) { el.style.removeProperty(p); });
                el.__morphTl = null;
            }
        });
        back.to(check, { opacity: 0, scale: 0.4, duration: 0.18, ease: 'power2.in' }, 0)
            .fromTo(el, { width: SIZE, minWidth: SIZE, height: SIZE, minHeight: SIZE,
                          paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0 },
                       { width: w, minWidth: w, height: h, minHeight: h,
                         paddingTop: padT, paddingBottom: padB, paddingLeft: padL, paddingRight: padR,
                         duration: 0.55, ease: 'power3.inOut' }, 0)
            .to(body, { opacity: 1, scale: 1, duration: 0.35, ease: 'back.out(1.6)' }, 0.3);
        el.__morphTl = back;
    }
};

// ─── v1.06.7：任务卡「下载信息」下拉（gsap easeReverse UI interactions 的 Dropdown 同款）───
// 弹性箭头旋转 + 面板 height 0→auto 弹性展开 + 信息行 stagger；收起 easeReverse 2.5×。
// 每次点击都 kill 旧时间线、基于当前 DOM 重建 —— 下载中 Blazor 频繁重渲染可能替换节点，
// 缓存时间线会指向旧节点导致「点开就收不回」。
junigridJs.taskDrop = function (panelSel, arrowSel, open) {
    var panel = document.querySelector(panelSel);
    var arrow = document.querySelector(arrowSel);
    if (!panel) return;
    if (panel.__tl) { panel.__tl.kill(); panel.__tl = null; }
    if (!window.gsap) {
        panel.style.visibility = open ? 'visible' : 'hidden';
        panel.style.opacity = open ? '1' : '0';
        panel.style.height = open ? 'auto' : '0px';
        if (arrow) arrow.style.transform = open ? 'rotate(180deg)' : 'none';
        return;
    }
    if (open) {
        panel.classList.add('open');
        // v1.07：动画结束后清掉内联样式，稳态显示交给 CSS .open（Blazor 重渲染不丢状态）
        panel.__tl = gsap.timeline({
            onComplete: function () {
                panel.style.height = '';
                panel.style.visibility = '';
                panel.style.opacity = '';
                panel.__tl = null;
            }
        })
            .to(arrow, { rotation: 180, duration: 0.9, ease: 'elastic.out(1.2, 0.3)', easeReverse: 'power2.inOut' }, 0)
            .fromTo(panel,
                { height: 0, autoAlpha: 0 },
                { height: 'auto', autoAlpha: 1, duration: 1, ease: 'elastic.out(1.2, 0.3)', easeReverse: 'power3.out' }, 0)
            .from(panel.querySelectorAll('.jg-taskdrop-item'), {
                opacity: 0, x: -20, duration: 0.5,
                ease: 'back.out(3)', easeReverse: 'power2.out', stagger: 0.05
            }, 0.12);
    } else {
        panel.__tl = gsap.timeline({
            onComplete: function () {
                panel.classList.remove('open');
                panel.style.height = '';
                panel.style.visibility = '';
                panel.style.opacity = '';
            }
        })
            .to(arrow, { rotation: 0, duration: 0.4, ease: 'power2.inOut' }, 0)
            .to(panel, { height: 0, autoAlpha: 0, duration: 0.4, ease: 'power2.in' }, 0);
    }
};

// ─── v1.06.8：全局滚动管理（全新导航归顶 + 返回恢复原位）───
// .jg-main 是跨页共享的滚动容器 —— 本模块解决两件事：
//  ① 全新导航（点链接/导航图标/编程导航）→ 滚动归零：旧版没人归零，
//     从 Mod 列表中间进 Nexus 会直接开在底部（位置被带过去）；
//  ② 返回/前进（popstate）→ 回到离开时的位置：离开那一刻把 scrollTop 存进
//     sessionStorage（按 URL），返回后轮询等新页内容撑高再落位。
// 关键防污染：页面切换期间（骨架屏渲染、浏览器钳制 scrollTop 会触发 scroll 事件）
// 用 __jgScrollLock 锁住所有滚动写入 —— 否则钳制产生的 scroll 事件会把 0 写进存档，
// 「回原位」就变成了「回顶部」（之前关下载页回列表顶端的根因之一）。
// /mods 自带专用恢复系统，恢复阶段跳过它避免双重落位。
(function () {
    var KEY = 'jg:urlscroll';
    function loadMap() { try { return JSON.parse(sessionStorage.getItem(KEY) || '{}'); } catch (e) { return {}; } }
    function saveMap(m) { try { sessionStorage.setItem(KEY, JSON.stringify(m)); } catch (e) { } }
    function urlKey() { return location.pathname + location.search; }
    function isMods(u) { return u === '/mods' || u.indexOf('/mods?') === 0 || u.indexOf('/mods/') === 0; }
    function main() { return document.querySelector('.jg-main'); }

    // 过渡锁：导航后 700ms 内一切滚动写入静默（骨架/钳制期）
    function lock() {
        window.__jgScrollLock = true;
        clearTimeout(window.__jgScrollLockTimer);
        window.__jgScrollLockTimer = setTimeout(function () { window.__jgScrollLock = false; }, 700);
    }

    // ① 全新导航：存好旧页位置 → 清目标页存档 → 归零滚动
    // vNext：/nexus 属「记忆页」—— 前进导航离开再回来必须停在走之前的位置：
    // 不再清掉它的存档，并在渲染后按存档落位（restoreFor）。
    // /mods 走页面组件自己的专用恢复系统（restoreFor 对 /mods 跳过），存档保留与否无影响。
    // 其它页面维持「新导航 = 回顶部」。
    var origPush = history.pushState.bind(history);
    history.pushState = function (s, t, u) {
        var targetPath = null;
        try {
            var el = main();
            var m = loadMap();
            // 离开前：把当前位置存到【旧 URL】名下（返回时恢复的就是它）
            if (el) m[urlKey()] = el.scrollTop;
            var target = new URL(u, location.href);
            targetPath = target.pathname + target.search;
            if (targetPath !== '/nexus') delete m[targetPath];
            saveMap(m);
        } catch (e) { }
        var r = origPush(s, t, u);
        lock();
        var el2 = null;
        try {
            el2 = main();
            if (el2) {
                el2.classList.remove('jg-restore-pending');   // 清掉上一轮未走完的「待恢复」隐藏
                el2.scrollTop = 0;
            }
        } catch (e) { }
        if (targetPath === '/nexus') {
            // vNext：记忆页且有滚动存档 → 新页渲染【前】就挂「待恢复」隐藏（.jg-main 跨导航
            // 持久，nexus 内容一渲染出来就是隐藏态，「先画顶部」的那一帧根本画不出来），
            // restoreFor 落位成功的同一帧揭开（与 /mods v0.72.4 专用系统同思路）
            try {
                var sy = loadMap()['/nexus'];
                if (sy && sy > 1 && el2) el2.classList.add('jg-restore-pending');
            } catch (e) { }
            setTimeout(restoreFor, 150);
        }
        return r;
    };

    // ② 返回/前进：立「返回导航」标记（/mods 兜底恢复用）+ 锁 + 排队恢复
    // vNext：返回 /nexus 且有存档 → 本监听注册早于 Blazor 路由，同步挂隐藏标记，
    // 新 nexus 内容首帧即隐藏，restoreFor 落位同帧揭开（消除「先顶部后中间」闪屏）
    window.addEventListener('popstate', function () {
        try { window.__jgBackNav = true; } catch (e) { }
        lock();
        try {
            if (urlKey() === '/nexus') {
                var sy = loadMap()['/nexus'];
                if (sy && sy > 1) { var eln = main(); if (eln) eln.classList.add('jg-restore-pending'); }
            }
        } catch (e) { }
        setTimeout(restoreFor, 120);
    });
        window.junigridJs.readScrollKey = function (key) {
        try {
            var v = parseFloat(sessionStorage.getItem('jg-scroll:' + key));
            return isNaN(v) ? 0 : v;
        } catch (e) { return 0; }
    };

    // 常规滚动采集（过渡锁期间不写）
    function bindCapture() {
        var el = main();
        if (!el) { setTimeout(bindCapture, 400); return; }
        if (el.__gscrollBound) return;
        el.__gscrollBound = true;
        var pending = false;
        el.addEventListener('scroll', function () {
            if (pending) return;
            pending = true;
            requestAnimationFrame(function () {
                pending = false;
                if (window.__jgScrollLock) return;
                var m = loadMap();
                m[urlKey()] = el.scrollTop;
                saveMap(m);
            });
        }, { passive: true });
    }
    bindCapture();

    // vNext：把指定值（或当前实际位置）写进当前 URL 的滚动存档。
    // 手动刷新（顶栏点当前页图标）归顶后调用 —— 不写的话存档里还是刷新前的旧位置，
    // 下次离开再回来会恢复到旧位置而非刷新后的顶部。
    window.junigridJs.saveCurrentUrlScroll = function (y) {
        try {
            var el = main();
            var m = loadMap();
            m[urlKey()] = typeof y === 'number' ? y : (el ? el.scrollTop : 0);
            saveMap(m);
        } catch (e) { }
    };

    // ③ 恢复：最多 ~4s 轮询，内容撑高即落位；用户手动滚动立即让位
    // vNext：配合导航瞬间挂上的「待恢复」隐藏 —— 落位成功 / 放弃 / 用户接管，
    // 三种收场都在同一帧揭开页面：顶部状态从未被绘制，「先顶后跳」的闪屏帧不存在。
    // restoreSeq 令牌：快速连续导航时旧一轮轮询静默让位，避免两轮互相改写 scrollTop。
    var restoreSeq = 0;
    function restoreFor() {
        var url = urlKey();
        if (isMods(url)) return;   // /mods 专用系统负责
        var el = main();
        if (!el) return;
        var y = loadMap()[url];
        if (!y || y < 1) { el.classList.remove('jg-restore-pending'); return; }   // 无可恢复值：顺带确保不残留隐藏
        var my = ++restoreSeq;
        var tries = 0, lastH = -1, stable = 0, userTouched = false;
        var reveal = function () { el.classList.remove('jg-restore-pending'); };
        var onUser = function () { userTouched = true; };
        el.addEventListener('wheel', onUser, { passive: true });
        el.addEventListener('touchstart', onUser, { passive: true });
        el.addEventListener('pointerdown', onUser, { passive: true });
        var timer = null;
        var tick = function () {
            tries++;
            if (my !== restoreSeq) { cleanup(); return; }   // 被新一轮恢复取代：静默退场，不动新一轮的隐藏标记
            if (userTouched || tries > 45 || !el.isConnected) { reveal(); cleanup(); return; }
            // 压制页面入场动画的 transform（会让 scrollTop 设置无效）
            el.classList.remove('jg-page-enter');
            if (el.style.transform !== 'none') el.style.transform = 'none';
            var h = el.scrollHeight;
            var limit = h - el.clientHeight;
            stable = (h === lastH) ? stable + 1 : 0;   // 高度稳定轮数（骨架/图片撑高期自动归零）
            lastH = h;
            if (limit > 0) {
                el.scrollTop = y;
                if (Math.abs(el.scrollTop - y) <= 2) { reveal(); cleanup(); return; }
            }
            // 高度已稳定多轮却仍够不到 y（内容比离开时短）→ 按可达位置收工，
            // 别让页面一直藏着（高度仍在变化的加载期 stable 会归零，不会误伤）
            if (stable >= 8) { reveal(); cleanup(); return; }
            timer = setTimeout(tick, 90);
        };
        tick();   // 首轮立即跑（setInterval 要再等 90ms 才动手，白屏期会无谓拉长）
        function cleanup() {
            clearTimeout(timer);
            el.removeEventListener('wheel', onUser);
            el.removeEventListener('touchstart', onUser);
            el.removeEventListener('pointerdown', onUser);
        }
    }
})();

// ------------------ v1.07.0：日志页筛选下拉（GSAP easeReverse demo「Dropdown」同款） ------------------
// 展开：箭头 elastic 旋转 180° + 面板 elastic 弹出（yPercent -30→0 / scale .7→1）+ 菜单项 back.out(3) 交错入场；
// 收起：demo 的 easeReverse/timeScale(2.5) 语义 —— ≈2.5× 速度的平滑 power 缓出（本地 gsap 3.12 无 easeReverse
// 属性，用独立收场时间线等效），动画完全结束再移除 .open / 清 inline style，杜绝残留白块。
(function () {
    function parts(wrapSel) {
        var wrap = document.querySelector(wrapSel);
        if (!wrap) return null;
        return {
            wrap: wrap,
            menu: wrap.querySelector('.jg-sort-menu'),
            arrow: wrap.querySelector('.jg-sort-arrow'),
            items: wrap.querySelectorAll('.jg-sort-item')
        };
    }
    function kill(p) { if (p.wrap.__tl) { p.wrap.__tl.kill(); p.wrap.__tl = null; } }

    function open(p) {
        kill(p);
        p.wrap.__ddOpen = true;
        p.wrap.classList.add('open');
        gsap.set(p.arrow, { rotation: 0 });
        gsap.set(p.menu, { autoAlpha: 0, yPercent: -30, scale: 0.7, transformOrigin: 'top center' });
        gsap.set(p.items, { opacity: 0, x: -20 });
        p.wrap.__tl = gsap.timeline()
            .to(p.arrow, { rotation: 180, duration: 0.9, ease: 'elastic.out(1.2, 0.3)' }, 0)
            .to(p.menu, { autoAlpha: 1, yPercent: 0, scale: 1, duration: 1, ease: 'elastic.out(1.2, 0.3)' }, 0)
            .fromTo(p.items, { opacity: 0, x: -20 },
                { opacity: 1, x: 0, duration: 0.5, ease: 'back.out(3)', stagger: 0.07 }, 0.1);
    }
    function close(p) {
        if (!p.wrap.__ddOpen) return;
        p.wrap.__ddOpen = false;
        kill(p);
        p.wrap.__tl = gsap.timeline({
            onComplete: function () {
                p.wrap.classList.remove('open');
                gsap.set([p.menu, p.arrow, p.items], { clearProps: 'all' });
            }
        });
        p.wrap.__tl
            .to(p.menu, { autoAlpha: 0, yPercent: -14, scale: 0.86, duration: 0.26, ease: 'power3.out' }, 0)
            .to(p.arrow, { rotation: 0, duration: 0.3, ease: 'power2.inOut' }, 0);
    }

    window.junigridJs.logsFilterInit = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p || !p.menu || p.wrap.__filterBound) return;
        p.wrap.__filterBound = true;
        if (!window.gsap) return;   // 无 gsap：靠 .open + CSS 兜底开关
        document.addEventListener('click', function (e) {
            if (p.wrap.__ddOpen && !p.wrap.contains(e.target)) close(p);
        });
    };
    window.junigridJs.logsFilterToggle = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p) return;
        if (!window.gsap) { p.wrap.classList.toggle('open', !p.wrap.__ddOpen); p.wrap.__ddOpen = !p.wrap.__ddOpen; return; }
        p.wrap.__ddOpen ? close(p) : open(p);
    };
    window.junigridJs.logsFilterClose = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p) return;
        if (!window.gsap) { p.wrap.classList.remove('open'); p.wrap.__ddOpen = false; return; }
        close(p);
    };
})();

// ------------------ v0.2.2：GSAP 弹性按钮 hover（easeReverse 平滑退出） ------------------
// 按钮加 class="jg-gsap-btn"
window.junigridJs.initGsapFx = function () {
    if (!window.gsap) return;
    const hasER = parseFloat(gsap.version) >= 3.13;
    const exitTs = 2.5;

    document.querySelectorAll('.jg-gsap-btn, .jg-path-btn, .jg-nexus-logout-btn, .jg-nexus-login-btn').forEach(function (btn) {
        if (btn.__jgFxBound) return;
        btn.__jgFxBound = true;
        btn.style.transformOrigin = 'center';
        const up = { scale: 1.12, duration: 1.0, ease: 'elastic.out(1.2, 0.3)' };
        if (hasER) up.easeReverse = 'power2.out';
        const tl = gsap.timeline({ paused: true }).to(btn, up, 0);
        btn.addEventListener('mouseenter', function () { tl.timeScale(1).play(); });
        btn.addEventListener('mouseleave', function () { tl.timeScale(exitTs).reverse(); });
    });
};

// ------------------ v0.2.2：禁用开关行的 PixelCard 像素 hover 特效（React Bits 移植） ------------------
window.junigridJs.initPixelHover = function () {
    const COLORS = ['#fecdd3', '#fda4af', '#e11d48'];
    const GAP = 6;

    function Pixel(ctx, x, y, color, speed, delay) {
        this.ctx = ctx; this.x = x; this.y = y; this.color = color;
        this.speed = speed; this.delay = delay;
        this.size = 0;
        this.sizeStep = Math.random() * 0.4;
        this.minSize = 0.5;
        this.maxSize = Math.random() * (this.minSize + 2 - 0.5) + 0.5;
        this.counter = 0;
        this.counterStep = Math.random() * 4 + 10;
        this.isIdle = false; this.isReverse = false; this.isShimmer = false;
    }
    Pixel.prototype.draw = function () {
        const off = 1 - this.size * 0.5;
        this.ctx.fillStyle = this.color;
        this.ctx.fillRect(this.x + off, this.y + off, this.size, this.size);
    };
    Pixel.prototype.appear = function () {
        this.isIdle = false;
        if (this.counter <= this.delay) { this.counter += this.counterStep; return; }
        if (this.size >= this.maxSize) this.isShimmer = true;
        if (this.isShimmer) this.shimmer(); else this.size += this.sizeStep;
        this.draw();
    };
    Pixel.prototype.disappear = function () {
        this.isShimmer = false; this.counter = 0;
        if (this.size <= 0) { this.isIdle = true; return; }
        this.size -= 0.1;
        this.draw();
    };
    Pixel.prototype.shimmer = function () {
        if (this.size >= this.maxSize) this.isReverse = true;
        else if (this.size <= this.minSize) this.isReverse = false;
        if (this.isReverse) this.size -= this.speed; else this.size += this.speed;
    };

    document.querySelectorAll('.jg-switch-row.disabled').forEach(function (row) {
        if (row.__pxBound) return;
        row.__pxBound = true;
        const canvas = document.createElement('canvas');
        canvas.className = 'jg-pixel-canvas';
        row.appendChild(canvas);
        const ctx = canvas.getContext('2d');
        let pixels = [], anim = null, prev = performance.now();

        function init() {
            const w = Math.floor(row.clientWidth), h = Math.floor(row.clientHeight);
            if (!w || !h) return;
            canvas.width = w; canvas.height = h;
            pixels = [];
            for (let x = 0; x < w; x += GAP) {
                for (let y = 0; y < h; y += GAP) {
                    const color = COLORS[Math.floor(Math.random() * COLORS.length)];
                    const dx = x - w / 2, dy = y - h / 2;
                    pixels.push(new Pixel(ctx, x, y, color, 0.08, Math.sqrt(dx * dx + dy * dy)));
                }
            }
        }
        function frame(fn) {
            anim = requestAnimationFrame(function () { frame(fn); });
            const now = performance.now();
            if (now - prev < 1000 / 60) return;
            prev = now;
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            let allIdle = true;
            for (let i = 0; i < pixels.length; i++) {
                pixels[i][fn]();
                if (!pixels[i].isIdle) allIdle = false;
            }
            if (allIdle) cancelAnimationFrame(anim);
        }
        function handle(name) {
            cancelAnimationFrame(anim);
            init();
            if (!pixels.length) return;
            anim = requestAnimationFrame(function () { frame(name); });
        }
        row.addEventListener('mouseenter', function () { handle('appear'); });
        row.addEventListener('mouseleave', function () { handle('disappear'); });
    });
};

// ------------------ v0.2.2：任务管理悬浮窗拖动（拖动超过 5px 时吞掉本次点击） ------------------
window.junigridJs.makeTaskDockDraggable = function (sel) {
    const el = document.querySelector(sel);
    if (!el || el.__dragBound) return;
    el.__dragBound = true;
    el.style.touchAction = 'none';
    let dragging = false, moved = false;
    let sx = 0, sy = 0, baseL = 0, baseT = 0, w = 0, h = 0;

    // 把悬浮窗左上角放到 (l, t)，按「就近角」记偏移；不再读元素矩形（倾斜 transform 会污染它）
    function place(l, t) {
        const pr = (el.offsetParent || document.body).getBoundingClientRect();
        const maxL = Math.max(0, pr.width - w);
        const maxT = Math.max(0, pr.height - h);
        l = Math.min(Math.max(0, l), maxL);
        t = Math.min(Math.max(0, t), maxT);
        // 关键：反向偏移必须显式置 auto —— 样式表里有 right:20px/bottom:20px，
        // 只内联 left 的话 left+right 同时生效，绝对定位元素会被强行拉满宽度（巨型椭圆 bug）
        el.style.left = el.style.right = el.style.top = el.style.bottom = '';
        if (l + w / 2 <= pr.width / 2) { el.style.left = l + 'px'; el.style.right = 'auto'; }
        else { el.style.right = (pr.width - l - w) + 'px'; el.style.left = 'auto'; }
        if (t + h / 2 <= pr.height / 2) { el.style.top = t + 'px'; el.style.bottom = 'auto'; }
        else { el.style.bottom = (pr.height - t - h) + 'px'; el.style.top = 'auto'; }
    }

    // 窗口缩放时按当前锚定角重新钳制
    function reclamp() {
        const r = el.getBoundingClientRect();
        if (!r.width) return;
        const pr = (el.offsetParent || document.body).getBoundingClientRect();
        const maxL = Math.max(0, pr.width - r.width);
        const maxT = Math.max(0, pr.height - r.height);
        const l = r.left - pr.left, t = r.top - pr.top;
        if (l < 0 || l > maxL || t < 0 || t > maxT) place(Math.min(Math.max(0, l), maxL), Math.min(Math.max(0, t), maxT));
    }
    window.addEventListener('resize', reclamp);

    el.addEventListener('pointerdown', function (e) {
        dragging = true; moved = false;
        sx = e.clientX; sy = e.clientY;
        const r = el.getBoundingClientRect();
        const pr = (el.offsetParent || document.body).getBoundingClientRect();
        baseL = r.left - pr.left; baseT = r.top - pr.top;
        w = r.width; h = r.height;
        try { el.setPointerCapture(e.pointerId); } catch (err) { }
    });
    el.addEventListener('pointermove', function (e) {
        if (!dragging) return;
        const dx = e.clientX - sx, dy = e.clientY - sy;
        if (!moved && (Math.abs(dx) > 5 || Math.abs(dy) > 5)) {
            moved = true;
            el.__dragging = true;   // 停用 tilt 的光标倾斜
            if (window.gsap) {
                gsap.killTweensOf(el, 'rotationX,rotationY');
                gsap.set(el, { rotationX: 0, rotationY: 0 });
            }
        }
        if (moved) place(baseL + dx, baseT + dy);
    });
    function endDrag() {
        if (!dragging) return;
        dragging = false;
        el.__dragging = false;
        // 让 tilt 平滑回正（指针还悬在按钮上时由下一次 move 重新接管）
        el.dispatchEvent(new Event('pointerleave'));
    }
    el.addEventListener('pointerup', endDrag);
    el.addEventListener('pointercancel', endDrag);
    // 拖动结束时吞掉点击，避免拖完误进任务页
    el.addEventListener('click', function (e) {
        if (moved) {
            e.stopImmediatePropagation();
            e.preventDefault();
            moved = false;
        }
    }, true);
};

// ------------------ v0.2.2：内存管理滑杆面板开合动画（与下拉菜单同款弹性曲线） ------------------
// collapseSet：无动画直接定状态（页面首帧用）；collapseToggle：带 GSAP 弹性开合
(function () {
    window.junigridJs = window.junigridJs || {};

    function setInstant(el, open) {
        if (!window.gsap) { el.style.display = open ? '' : 'none'; return; }
        gsap.set(el, { display: open ? '' : 'none', height: 'auto', autoAlpha: open ? 1 : 0 });
    }

    window.junigridJs.collapseSet = function (el, open) {
        if (el) setInstant(el, open);
    };

    // 返回 Promise：C# await 它可以等到动画真正播完再提交状态
    // （v0.2.2 修复「收不回去」：原先 clearProps:'all' 会把 Blazor 写的 display:none 一并清掉，
    //  面板在动画结束后又冒出来；现在只清 height/opacity/visibility，display 交给 Blazor 管）
    window.junigridJs.collapseToggle = function (el, open) {
        return new Promise(function (resolve) {
            if (!el) { resolve(); return; }
            if (!window.gsap) { el.style.display = open ? '' : 'none'; resolve(); return; }
            gsap.killTweensOf(el);   // 快速连点开关时，掐掉上一次未完成的开合动画防状态打架
            if (open) {
                gsap.set(el, { display: '' });
                gsap.fromTo(el, { height: 0, autoAlpha: 0 },
                    { height: 'auto', autoAlpha: 1, duration: 0.9, ease: 'elastic.out(1.2, 0.3)',
                      clearProps: 'height', onComplete: resolve });
            } else {
                gsap.to(el, { height: 0, autoAlpha: 0, duration: 0.3, ease: 'power2.in',
                    onComplete: function () {
                        gsap.set(el, { clearProps: 'height,opacity,visibility' });
                        el.style.display = 'none';
                        resolve();
                    } });
            }
        });
    };
})();

// ------------------ v0.2.2：弹性滑杆（React Bits ElasticSlider 的 GSAP 复刻） ------------------
// 胶囊轨道 hover 增高；拖到两端轨道橡皮筋拉伸、图标跟随位移；松手弹性弹回。
// markup 约定：.e-slider[data-min,data-max,data-step,data-suffix] > .es-track-wrap > .es-track > .es-fill，右侧 .es-value 显示数值
// 数值变化经 dotNetRef.OnElasticValue(id, value) 回调 Blazor 落配置。
(function () {
    window.junigridJs = window.junigridJs || {};

    function esDecay(value, max) {
        if (max === 0) return 0;
        var entry = value / max;
        var sigmoid = 2 * (1 / (1 + Math.exp(-entry)) - 0.5);
        return sigmoid * max;
    }

    window.junigridJs.elasticInit = function (root, id, startValue, dotNetRef) {
        if (!root || root.__esBound) return;
        root.__esBound = true;

        var min = parseFloat(root.dataset.min) || 0;
        var max = parseFloat(root.dataset.max) || 100;
        var step = parseFloat(root.dataset.step) || 1;
        var suffix = root.dataset.suffix || '';
        var MAX_OVER = 50;

        var track = root.querySelector('.es-track');
        var fill = root.querySelector('.es-fill');
        var valEl = root.querySelector('.es-value');

        var value = Math.min(Math.max(startValue, min), max);
        var dragging = false;
        var region = 'middle';
        var proxy = { o: 0 };

        function round(v) {
            if (step > 0) v = Math.round(v / step) * step;
            return Math.min(Math.max(v, min), max);
        }
        function render() {
            var pct = max > min ? (value - min) / (max - min) * 100 : 0;
            if (fill) fill.style.width = pct + '%';
            if (valEl) valEl.textContent = Math.round(value) + ' ' + suffix;
        }
        function setOverflow(o) {
            if (!track) return;
            if (o <= 0.5 || region === 'middle') {
                track.style.transform = '';
                return;
            }
            var w = track.getBoundingClientRect().width || 1;
            var sy = 1 - (o / MAX_OVER) * 0.2;
            if (region === 'left') {
                track.style.transformOrigin = 'right';
                track.style.transform = 'scaleX(' + (1 + o / w) + ') scaleY(' + sy + ')';
            } else {
                track.style.transformOrigin = 'left';
                track.style.transform = 'scaleX(' + (1 + o / w) + ') scaleY(' + sy + ')';
            }
        }
        function moveTo(e) {
            var rect = track.getBoundingClientRect();
            value = round(min + (e.clientX - rect.left) / rect.width * (max - min));
            var over = 0;
            if (e.clientX < rect.left) { region = 'left'; over = rect.left - e.clientX; }
            else if (e.clientX > rect.right) { region = 'right'; over = e.clientX - rect.right; }
            else region = 'middle';
            proxy.o = esDecay(Math.min(over, 200), MAX_OVER);
            setOverflow(proxy.o);
            render();
        }
        function release() {
            if (!dragging) return;
            dragging = false;
            if (window.gsap && proxy.o > 0.5) {
                gsap.to(proxy, {
                    o: 0, duration: 0.8, ease: 'elastic.out(1, 0.4)',
                    onUpdate: function () { setOverflow(proxy.o); },
                    onComplete: function () { setOverflow(0); }
                });
            } else {
                proxy.o = 0;
                setOverflow(0);
            }
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnElasticValue', id, value);
        }

        root.addEventListener('pointerdown', function (e) {
            dragging = true;
            try { root.setPointerCapture(e.pointerId); } catch (err) { }
            moveTo(e);
        });
        root.addEventListener('pointermove', function (e) { if (dragging) moveTo(e); });
        root.addEventListener('pointerup', release);
        root.addEventListener('pointercancel', release);
        root.addEventListener('lostpointercapture', release);

        render();
    };
})();


// ── 关于卡片：CursorGrid 光标网格（React Bits 移植，canvas 铺底）──
junigridJs.initCursorGrid = function (selector) {
    var CFG = { cellSize: 17.5, color: '#d3d3d3', radius: 140, falloff: 'smooth',
        holdTime: 400, fadeDuration: 800, lineWidth: 1.2, maxOpacity: 1,
        fillOpacity: 0, gridOpacity: 0, cellRadius: 0, clickPulse: true, pulseSpeed: 600 };
    var CURVES = {
        linear: function (t) { return t; },
        smooth: function (t) { return t * t * (3 - 2 * t); },
        sharp: function (t) { return t * t * t; }
    };
    var rgb = CFG.color.replace('#', '');
    var col = [parseInt(rgb.slice(0, 2), 16), parseInt(rgb.slice(2, 4), 16), parseInt(rgb.slice(4, 6), 16)];

    document.querySelectorAll(selector || '.jg-cursor-grid').forEach(function (container) {
        if (container.dataset.cgridBound) return;
        container.dataset.cgridBound = '1';
        var canvas = document.createElement('canvas');
        canvas.className = 'jg-cgrid-canvas';
        container.insertBefore(canvas, container.firstChild);
        var ctx = canvas.getContext('2d');
        var dpr = Math.min(window.devicePixelRatio || 1, 2);

        var cols = 0, rows = 0, offX = 0, offY = 0, w = 0, h = 0;
        var alphas = new Float32Array(0), touched = new Float64Array(0);
        var pulses = [], raf = 0, running = false, lastFrame = 0;

        function rebuild() {
            w = container.offsetWidth; h = container.offsetHeight;
            canvas.width = Math.max(1, Math.round(w * dpr));
            canvas.height = Math.max(1, Math.round(h * dpr));
            canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            cols = Math.ceil(w / CFG.cellSize) + 1;
            rows = Math.ceil(h / CFG.cellSize) + 1;
            offX = (w - cols * CFG.cellSize) / 2;
            offY = (h - rows * CFG.cellSize) / 2;
            alphas = new Float32Array(cols * rows);
            touched = new Float64Array(cols * rows);
        }
        function center(i) {
            return [offX + (i % cols) * CFG.cellSize + CFG.cellSize / 2,
                    offY + Math.floor(i / cols) * CFG.cellSize + CFG.cellSize / 2];
        }
        function energize(x, y) {
            var r = Math.max(CFG.radius, 1), ease = CURVES[CFG.falloff], now = performance.now();
            var minC = Math.max(0, Math.floor((x - r - offX) / CFG.cellSize));
            var maxC = Math.min(cols - 1, Math.floor((x + r - offX) / CFG.cellSize));
            var minR = Math.max(0, Math.floor((y - r - offY) / CFG.cellSize));
            var maxR = Math.min(rows - 1, Math.floor((y + r - offY) / CFG.cellSize));
            for (var cR = minR; cR <= maxR; cR++) for (var cC = minC; cC <= maxC; cC++) {
                var i = cR * cols + cC, c = center(i);
                var d = Math.hypot(c[0] - x, c[1] - y);
                if (d > r) continue;
                var lv = ease(1 - d / r) * CFG.maxOpacity;
                if (lv > alphas[i]) { alphas[i] = lv; touched[i] = now; }
                else if (lv > 0) touched[i] = now;
            }
        }
        function draw(now) {
            var dt = Math.min(now - lastFrame, 50); lastFrame = now;
            ctx.clearRect(0, 0, w, h);
            for (var pi = pulses.length - 1; pi >= 0; pi--) {
                var pu = pulses[pi], ringR = ((now - pu.t0) / 1000) * CFG.pulseSpeed;
                if (ringR > Math.hypot(w, h)) { pulses.splice(pi, 1); continue; }
                var band = CFG.cellSize;
                var minC = Math.max(0, Math.floor((pu.x - ringR - band - offX) / CFG.cellSize));
                var maxC = Math.min(cols - 1, Math.floor((pu.x + ringR + band - offX) / CFG.cellSize));
                var minR = Math.max(0, Math.floor((pu.y - ringR - band - offY) / CFG.cellSize));
                var maxR = Math.min(rows - 1, Math.floor((pu.y + ringR + band - offY) / CFG.cellSize));
                for (var cR = minR; cR <= maxR; cR++) for (var cC = minC; cC <= maxC; cC++) {
                    var i = cR * cols + cC, c = center(i);
                    var d = Math.hypot(c[0] - pu.x, c[1] - pu.y);
                    if (Math.abs(d - ringR) < band / 2 && CFG.maxOpacity > alphas[i]) {
                        alphas[i] = CFG.maxOpacity; touched[i] = now;
                    }
                }
            }
            var anyVisible = pulses.length > 0;
            var fadeStep = dt / Math.max(CFG.fadeDuration, 16);
            var half = CFG.cellSize / 2;
            for (var i = 0; i < alphas.length; i++) {
                var a = alphas[i];
                if (a <= 0) continue;
                if (now - touched[i] > CFG.holdTime) {
                    a = Math.max(0, a - fadeStep); alphas[i] = a;
                    if (a <= 0) continue;
                }
                anyVisible = true;
                var cc = center(i);
                var g = ctx.createRadialGradient(cc[0], cc[1], half * 0.1, cc[0], cc[1], CFG.cellSize);
                g.addColorStop(0, 'rgba(' + col + ', ' + a + ')');
                g.addColorStop(1, 'rgba(' + col + ', 0)');
                ctx.beginPath();
                ctx.rect(cc[0] - half + 0.5, cc[1] - half + 0.5, CFG.cellSize - 1, CFG.cellSize - 1);
                if (CFG.fillOpacity > 0) { ctx.fillStyle = 'rgba(' + col + ', ' + (a * CFG.fillOpacity) + ')'; ctx.fill(); }
                ctx.strokeStyle = g; ctx.lineWidth = CFG.lineWidth; ctx.stroke();
            }
            if (anyVisible) raf = requestAnimationFrame(draw);
            else { running = false; ctx.clearRect(0, 0, w, h); }
        }
        function wake() {
            if (running) return;
            running = true; lastFrame = performance.now();
            raf = requestAnimationFrame(draw);
        }
        function local(e) {
            var r = canvas.getBoundingClientRect();
            return [e.clientX - r.left, e.clientY - r.top];
        }
        container.addEventListener('pointermove', function (e) {
            var p = local(e); energize(p[0], p[1]); wake();
        });
        container.addEventListener('pointerdown', function (e) {
            var p = local(e); pulses.push({ x: p[0], y: p[1], t0: performance.now() }); wake();
        });
        if (window.ResizeObserver) new ResizeObserver(function () { rebuild(); wake(); }).observe(container);
        rebuild();
    });
};

// ── 关于卡片：LogoLoop 图标跑马灯（React Bits 移植）──
junigridJs.initLogoLoop = function (selector) {
    var SPEED = 35, TAU = 0.25;
    document.querySelectorAll(selector || '.jg-logoloop').forEach(function (container) {
        if (container.dataset.loopBound) return;
        container.dataset.loopBound = '1';
        var track = container.querySelector('.jg-ll-track');
        var seq = container.querySelector('.jg-ll-seq');
        if (!track || !seq) return;

        var seqW = 0, offset = 0, velocity = 0, target = SPEED;
        var raf = 0, last = null, hovered = false;

        function measure() {
            seqW = seq.getBoundingClientRect().width;
            if (seqW <= 0) return;
            var need = Math.max(2, Math.ceil(container.clientWidth / seqW) + 2);
            var copies = track.querySelectorAll('.jg-ll-seq');
            for (var i = copies.length; i < need; i++) {
                var c = seq.cloneNode(true);
                c.setAttribute('aria-hidden', 'true');
                track.appendChild(c);
            }
        }
        function frame(ts) {
            if (last === null) last = ts;
            var dt = Math.max(0, ts - last) / 1000;
            last = ts;
            var want = hovered ? 0 : SPEED;
            velocity += (want - velocity) * (1 - Math.exp(-dt / TAU));
            if (seqW > 0) {
                offset = ((offset + velocity * dt) % seqW + seqW) % seqW;
                track.style.transform = 'translate3d(' + (-offset) + 'px, 0, 0)';
            }
            raf = requestAnimationFrame(frame);
        }
        container.addEventListener('pointerenter', function () { hovered = true; });
        container.addEventListener('pointerleave', function () { hovered = false; });
        container.addEventListener('click', function (e) {
            var copy = e.target.closest('[data-copy]');
            if (!copy) return;
            e.preventDefault();
            var text = copy.getAttribute('data-copy');
            function toast() {
                var t = document.querySelector('.jg-copy-toast');
                if (!t) {
                    t = document.createElement('div');
                    t.className = 'jg-copy-toast';
                    document.body.appendChild(t);
                }
                t.textContent = '邮箱已复制';
                t.classList.add('show');
                clearTimeout(t._timer);
                t._timer = setTimeout(function () { t.classList.remove('show'); }, 1800);
            }
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).then(toast, toast);
            } else {
                var ta = document.createElement('textarea');
                ta.value = text; document.body.appendChild(ta); ta.select();
                try { document.execCommand('copy'); } catch (err) { }
                document.body.removeChild(ta);
                toast();
            }
        });
        if (window.ResizeObserver) new ResizeObserver(measure).observe(container);
        measure(); // 立即量一次；图片加载完成后再校准
        var imgs = seq.querySelectorAll('img');
        imgs.forEach(function (im) {
            if (!im.complete) {
                im.addEventListener('load', measure, { once: true });
                im.addEventListener('error', measure, { once: true });
            }
        });
        setTimeout(measure, 500); // 兜底
        raf = requestAnimationFrame(frame);
    });
};
