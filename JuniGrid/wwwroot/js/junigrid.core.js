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
    // v1.4.4：手风琴展开/收起（motion.dev accordion 风格：height 0↔auto + 淡入 + 箭头旋转）
    accordionToggle(panelSel, open) {
        const panel = document.querySelector(panelSel);
        if (!panel) return;
        const inner = panel.querySelector(':scope > .jg-acc-inner') || panel;
        const btn = document.querySelector(`[aria-controls="${panel.id}"]`) || panel.previousElementSibling;
        const chev = btn && btn.querySelector('.jg-acc-chev');
        if (btn) btn.setAttribute('aria-expanded', open ? 'true' : 'false');
        panel.classList.toggle('open', !!open);

        if (!window.gsap) {
            panel.style.height = open ? 'auto' : '0px';
            panel.style.opacity = open ? '1' : '0';
            if (chev) chev.style.transform = open ? 'rotate(180deg)' : '';
            return;
        }
        if (panel.__accTl) { panel.__accTl.kill(); panel.__accTl = null; }
        gsap.killTweensOf([panel, inner, chev]);

        if (open) {
            panel.style.height = '0px';
            panel.style.overflow = 'hidden';
            const h = inner.scrollHeight;
            gsap.set(inner, { opacity: 0, y: -8 });
            const tl = gsap.timeline();
            tl.to(panel, { height: h, duration: 0.38, ease: 'power3.out' }, 0)
              .to(inner, { opacity: 1, y: 0, duration: 0.32, ease: 'power2.out' }, 0.06)
              .to(chev,  { rotation: 180, duration: 0.35, ease: 'back.out(1.8)' }, 0)
              .eventCallback('onComplete', () => {
                  panel.style.height = 'auto';
                  panel.style.overflow = '';
              });
            panel.__accTl = tl;
        } else {
            const h = panel.scrollHeight;
            panel.style.height = h + 'px';
            panel.style.overflow = 'hidden';
            const tl = gsap.timeline();
            tl.to(inner, { opacity: 0, y: -6, duration: 0.2, ease: 'power2.in' }, 0)
              .to(panel,  { height: 0, duration: 0.3, ease: 'power2.inOut' }, 0.04)
              .to(chev,   { rotation: 0, duration: 0.28, ease: 'power2.inOut' }, 0)
              .eventCallback('onComplete', () => {
                  panel.style.overflow = '';
              });
            panel.__accTl = tl;
        }
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
            // 滚动位置与 item 透明度必须重置：上次滚到底再开，菜单会「空盒带滚动条」
            try { menu.scrollTop = 0; } catch (e) {}
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
                    try { menu.scrollTop = 0; } catch (e) {}
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

    // v0.31.0: PCL 式页面入场 —— 给 <main.jg-main> 打上 .jg-page-enter，触发 CSS 关键帧
    // v1.1.8 性能：不用 void offsetWidth 强制回流（同步布局是切页发卡源），
    // 改用 rAF 两帧后加回 class 重启动画；清理定时器从 900ms 收到 480ms（与 CSS 对齐）
    playPageEnter() {
        const el = document.querySelector('.jg-main');
        if (!el) return;
        el.classList.remove('jg-page-enter');
        if (el.__peRaf) cancelAnimationFrame(el.__peRaf);
        el.__peRaf = requestAnimationFrame(() => {
            el.__peRaf = requestAnimationFrame(() => {
                el.__peRaf = 0;
                el.classList.add('jg-page-enter');
            });
        });
        // v1.1.2：切页后刷新返回顶部按钮的显隐（路由变了，滚动位置也变了）
        if (window.junigridJs.backTopRefresh) window.junigridJs.backTopRefresh();
        // 480ms 后清掉（子项最长 delay 120 + duration 300 = 420）
        clearTimeout(el.__peTimer);
        el.__peTimer = setTimeout(() => el.classList.remove('jg-page-enter'), 480);
    },
    scrollToBottom(selector, force) {
        const el = document.querySelector(selector);
        if (!el) return;
        // 日志跟随用"记住的跟随状态"，不能靠渲染后的几何距离判断：SMAPI 启动期一次
        // 冲几十行，内容猛长几百 px，浏览器不会自动跟着长 —— 几何距离瞬间超阈，
        // 会被误判成"用户上翻"而永久停跟（实机：日志页从不跟随）。滚动监听维护状态：
        // 用户上翻 → false（停止跟随）；滚回底部 → true（恢复跟随）。force 无条件落底。
        if (el.__followInit !== true) {
            el.__followInit = true;
            el.__follow = true;
            el.addEventListener('scroll', () => {
                el.__follow = el.scrollHeight - el.scrollTop - el.clientHeight < 48;
            }, { passive: true });
        }
        if (force) el.__follow = true;
        if (el.__follow) el.scrollTop = el.scrollHeight;
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
        // v1.2.4：去掉逐行 getBoundingClientRect 预测量 —— 返回列表时几百行全是新 DOM，
        // 每行一次强制同步布局（layout thrash）本身就是可感知的停顿。观察器的初始回调
        // 自带首帧 intersectionRatio：视口内的行被置为展开（与旧"预测量后立即展开"等价），
        // 视口外的行被收起（用户看不到它们，状态切换发生在可绘制之前）。
        el.style.transition = 'opacity .2s ease, transform .2s ease';
        el.style.transformOrigin = 'center center';
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

// v0.35.0：导航滑块实时同步 —— 路由变化/窗口缩放/刷新都立即重定位（双重 rAF 等布局稳定）
window.junigridJs.placeNavThumb = function (opts) {
    const nav = document.querySelector('.jg-topnav');
    const thumb = document.querySelector('.jg-topnav-thumb');
    if (!nav || !thumb) return;
    // Motion tab-select 同款：滑块带轻微过冲地滑到目标项。窗口缩放/首帧定位传 {instant:true} 直接落位。
    const instant = !!(opts && opts.instant);
    const place = () => {
        const active = nav.querySelector('.jg-topnav-item.active');
        if (!active) { thumb.style.width = '0px'; return; }
        const nr = nav.getBoundingClientRect();
        const r = active.getBoundingClientRect();
        const left = r.left - nr.left, width = r.width;
        if (!window.gsap || instant || !thumb.__placed) {
            if (thumb.__tl) { thumb.__tl.kill(); thumb.__tl = null; }
            thumb.style.left = left + 'px';
            thumb.style.width = width + 'px';
        } else {
            if (thumb.__tl) thumb.__tl.kill();
            thumb.__tl = gsap.to(thumb, {
                left: left, width: width, duration: 0.28,
                ease: 'back.out(1.4)', overwrite: 'auto',
                onComplete: function () { thumb.__tl = null; }
            });
        }
        thumb.__placed = true;
    };
    // 双 rAF：等 Blazor 把 .active 挪到目标项 + 布局回流完成后再量
    requestAnimationFrame(() => requestAnimationFrame(place));
};
// 缩放/字体加载等导致宽度变化时，滑块实时跟随（缩放走即时落位，不弹）
(function () {
    if (window.__navThumbBound) return; window.__navThumbBound = true;
    let raf = 0;
    const re = () => { cancelAnimationFrame(raf); raf = requestAnimationFrame(() => window.junigridJs.placeNavThumb({ instant: true })); };
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
// v1.1.2：深浅主题（WPF 覆盖层圆形揭示方案的 JS 侧接口）
// 切换动画本体在宿主侧（MainWindow.RevealThemeSwitchAsync：
// CapturePreview 截旧主题 → 覆盖层挖圆洞），这里只提供状态读写与
// 无动画瞬时切换（由 C# 在覆盖层就位后调用）。
// ============================================================
window.junigridJs.getTheme = function () {
    return document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
};
// 无动画应用主题（瞬时生效，供宿主在覆盖层就位后调用）
window.junigridJs.applyTheme = function (theme) {
    var t = theme === 'dark' ? 'dark' : 'light';
    document.documentElement.dataset.theme = t;
    try { localStorage.setItem('jg-theme', t); } catch (e) { }
    return t;
};

