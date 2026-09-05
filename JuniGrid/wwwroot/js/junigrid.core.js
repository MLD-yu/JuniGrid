// ============================================================
// interop 核心：window.junigridJs 基础对象 + 顶栏滑块 + 窗口状态
// （必须最先加载 —— 其它 junigrid.*.js 都往这个对象上挂方法）
// ============================================================
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
        // v1.1.2：按 data-dd 键配对同步 —— 一页可能有多个下拉遮罩（Mods 的排序/存档），
        // 无差别全开会互相拦截点击（后一个遮罩盖住前一个，@onclick 落到错误的 Close 上）
        const ddKey = wrap.dataset.dd;
        document.querySelectorAll('.jg-dd-overlay').forEach(o => o.classList.toggle('open', !!open && o.dataset.dd === ddKey));
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
        // v1.1.2：切页后刷新返回顶部按钮的显隐（路由变了，滚动位置也变了）
        if (window.junigridJs.backTopRefresh) window.junigridJs.backTopRefresh();
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
// v1.1.1：深浅主题切换 —— 与 element-plus.org 官网完全一致的
// View Transitions 圆形揭示（clip-path circle 从主题开关圆心起算）：
//   切到浅色：新(浅色)快照 circle 0 → 全屏，浅色从按钮处往四周发散；
//   切到深色：旧(浅色)快照 circle 全屏 → 0，浅色从四周往按钮处收回，
//             四周先入夜，直到深色充满整个窗口。
// 不支持 startViewTransition / 系统开了「减少动态效果」时直接换主题不播动画。
// ============================================================
window.junigridJs.getTheme = function () {
    return document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
};
// 无动画应用主题（启动时配置与 localStorage 对齐用）
window.junigridJs.applyTheme = function (theme) {
    var t = theme === 'dark' ? 'dark' : 'light';
    document.documentElement.dataset.theme = t;
    try { localStorage.setItem('jg-theme', t); } catch (e) { }
    return t;
};
// 带圆形揭示动画的切换。返回 Promise<string> = 切换后的主题。
window.junigridJs.toggleTheme = function () {
    var root = document.documentElement;
    var toDark = root.dataset.theme !== 'dark';
    var next = toDark ? 'dark' : 'light';

    // 圆心 = 标题栏主题开关的中心（找不到就退化为右上角）
    var x = window.innerWidth - 60, y = 20;
    var btn = document.querySelector('.jg-theme-toggle');
    if (btn) {
        var r = btn.getBoundingClientRect();
        x = r.left + r.width / 2;
        y = r.top + r.height / 2;
    }

    var apply = function () {
        root.dataset.theme = next;
        try { localStorage.setItem('jg-theme', next); } catch (e) { }
    };

    var reduce = window.matchMedia
        && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (!document.startViewTransition || reduce) {
        apply();
        return Promise.resolve(next);
    }

    // 与 element-plus.org 一致：切深色时把旧(浅色)快照提到最上层 ——
    // View Transition 默认新快照在上，不提升的话新深色视图会直接盖住
    // 「浅色向按钮收回」的收缩动画。
    root.dataset.themeTransition = toDark ? 'to-dark' : 'to-light';
    var transition = document.startViewTransition(apply);
    var endRadius = Math.hypot(
        Math.max(x, window.innerWidth - x),
        Math.max(y, window.innerHeight - y)
    );
    // ⚠️ WebView2（CompositionControl 渲染路径）里 ::view-transition 快照层的
    // clip-path 坐标按【物理像素】解释，而 getBoundingClientRect / innerWidth
    // 是 CSS 像素 —— 高 DPI 下圆心和半径会被除以 dpr，圆跑到窗口中部。
    // 圆心与半径统一乘 devicePixelRatio 修正；dpr=1 的环境不受影响。
    var dpr = window.devicePixelRatio || 1;
    x *= dpr; y *= dpr; endRadius *= dpr;
    var done = transition.ready.then(function () {
        var clipPath = [
            'circle(0px at ' + x + 'px ' + y + 'px)',
            'circle(' + endRadius + 'px at ' + x + 'px ' + y + 'px)'
        ];
        // 深色：旧(浅色)快照从全屏圆收缩回按钮；浅色：新(浅色)快照从按钮扩散开
        // fill:'both' 必须加 —— 动画结束到快照销毁之间若有间隔帧，无填充时 clip-path
        // 会回弹成全屏，浅色旧快照整个闪回来（高 DPI WebView2 上能看见一帧白闪）；
        // 保持终态（圆收缩到 0 / 扩散到全屏）直到快照被移除，与官网 CSS 的 both 一致
        document.documentElement.animate(
            { clipPath: toDark ? [clipPath[1], clipPath[0]] : clipPath },
            {
                duration: 400,
                easing: 'ease-in',
                fill: 'both',
                pseudoElement: toDark ? '::view-transition-old(root)' : '::view-transition-new(root)'
            }
        );
    });
    var cleanup = function () { delete root.dataset.themeTransition; };
    return done
        .then(function () { return transition.finished.catch(function () { }); })
        .then(function () { cleanup(); return next; })
        .catch(function () { cleanup(); return next; });
};

