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
        const items = wrap.querySelectorAll('.jg-sort-item, .jg-profile-item');
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
// - 行按「露出多少」连续 scale 0.7→1 + opacity 0→1（0.2s），离开视口收回
// - 滚动容器顶/底渐变遮罩随滚动位置淡入淡出
window.junigridJs.animatedListInit = function (scrollSel, listSel) {
    const scroller = document.querySelector(scrollSel);
    const list = document.querySelector(listSel);
    if (!scroller || !list) return;

    // ── 行入场动画：IntersectionObserver，露一半即全亮，再少就按比例淡出 ──
    // 这里曾经是「过半才亮、否则 opacity 0」的二值闸门，实测两个后果：压在视口边上的行
    // 明明露出三成却完全看不见（滚动途中这类样本占压边行的 50%），而且滚动位置抖 2px
    // 就能让它在 0↔1 之间整幅翻转 —— 滚到底后顶上那行一直闪，闪的就是它。
    if (!list.__alObs) {
        // 观察范围向外扩 EDGE px：收缩后的行盒一旦完全离开视口，观察器就不再回调，
        // 那一行会卡在最后一次的比例上（实测在露出约 11% 处留下 0.27 的台阶）。
        const EDGE = 24;
        const th = [];
        for (let i = 0; i <= 20; i++) th.push(i / 20);
        list.__alObs = new IntersectionObserver(entries => {
            // 先量完再写：写样式会让下一行的 offsetHeight 读退化成强制同步布局
            const plan = [];
            for (const e of entries) {
                const el = e.target, R = e.rootBounds, H = el.offsetHeight;
                let a = 0;
                if (R && H && e.isIntersecting) {
                    // 比例不能取 e.intersectionRatio —— 那是我们自己刚写下去的 scale 量出来的，
                    // 量→改→再量会打转。scale 以中心为原点 ⇒ 变换后的中心 + 未变换的布局高度
                    // 就是真实行盒，这个比例与动画此刻处于哪一帧无关。
                    const b = e.boundingClientRect;
                    const top = b.top + b.height / 2 - H / 2;
                    const vis = Math.min(top + H, R.bottom - EDGE) - Math.max(top, R.top + EDGE);
                    a = Math.max(0, Math.min(1, vis / H * 2));
                }
                plan.push([el, Math.round(a * 100) / 100]);
            }
            for (const [el, a] of plan) {
                el.style.opacity = a;
                el.style.transform = 'scale(' + (0.7 + 0.3 * a) + ')';
            }
        }, { root: scroller, rootMargin: EDGE + 'px', threshold: th });
    }
    list.querySelectorAll('[data-al]').forEach(el => {
        if (el.__alBound) return;
        el.__alBound = true;
        // v1.2.4：去掉逐行 getBoundingClientRect 预测量 —— 返回列表时几百行全是新 DOM，
        // 每行一次强制同步布局（layout thrash）本身就是可感知的停顿。观察器的初始回调
        // 自带首帧的露出比例：视口内的行被置为展开（与旧"预测量后立即展开"等价），
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
// Mod 管理页的「Mod 管理 / 回收站」视图开关：与顶导航同一支滑块（GSAP tab-select 弹簧）。
window.junigridJs.placeViewThumb = function (opts) {
    const bar = document.querySelector('.jg-viewtabs');
    const thumb = document.querySelector('.jg-viewtabs-thumb');
    if (!bar || !thumb) return;
    const instant = !!(opts && opts.instant);
    const place = () => {
        const active = bar.querySelector('.jg-viewtabs-item.active');
        if (!active) { thumb.style.width = '0px'; return; }
        const br = bar.getBoundingClientRect();
        const r = active.getBoundingClientRect();
        const left = r.left - br.left, width = r.width;
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
    requestAnimationFrame(() => requestAnimationFrame(place));
};
// 视图切换时只让列表入场（与 jg-child-in 同一套 300ms 淡入+上移），不碰整页 ——
// 用 playPageEnter 会把 .jg-main 的 opacity 从 0 拉起，切换时整页会闪一下。
window.junigridJs.playViewEnter = function () {
    const lists = Array.from(document.querySelectorAll('.jg-modrows:not(.jg-modrows-skeleton)'));
    const list = lists.find(el => el.offsetHeight > 0);
    if (!list) return;
    list.classList.remove('jg-view-enter');
    requestAnimationFrame(() => requestAnimationFrame(() => {
        list.classList.add('jg-view-enter');
        setTimeout(() => list.classList.remove('jg-view-enter'), 340);
    }));
};
// 缩放/字体加载等导致宽度变化时，滑块实时跟随（缩放走即时落位，不弹）
(function () {
    if (window.__navThumbBound) return; window.__navThumbBound = true;
    let raf = 0;
    const re = () => { cancelAnimationFrame(raf); raf = requestAnimationFrame(() => {
        window.junigridJs.placeNavThumb({ instant: true });
        window.junigridJs.placeViewThumb({ instant: true });
    }); };
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
// CapturePreview 截旧主题 → 覆盖层挖圆洞），这里只提供
// 无动画瞬时切换（由 C# 在覆盖层就位后调用）。
// ============================================================
// 无动画应用主题（瞬时生效，供宿主在覆盖层就位后调用）
window.junigridJs.applyTheme = function (theme) {
    var t = theme === 'dark' ? 'dark' : 'light';
    document.documentElement.dataset.theme = t;
    try { localStorage.setItem('jg-theme', t); } catch (e) { }
    return t;
};


// ============================================================
// v1.3.52：长按拖拽（pointer + mouse 双通道）
//
// 上一版只挂 pointer 事件，实机「长按没反应」：合成 pointerdown 都进不了处理器。
// 而 WebView2 里 pointer 事件是否转发给页面并不由页面说了算，所以这次两种都挂 ——
// 同一次物理按下会先后产生 pointerdown 与 mousedown，靠 `st` 去重（第一个把 st 占住，
// 第二个直接忽略），不会重复计时。
//
// 全流程带内部计数器（dbg），dragSelfTest() 直接把数字读回来 —— 不再依赖 .NET 回调，
// 回调本身失败也能在 err 里看到原因。
//
// DOM 契约：可拖元素 data-drag-key / data-drag-label；
//          投放目标 .jg-cat-head[data-cat-key]（"" = 未分类段）
// ============================================================
(function () {
    var HOLD = 320;      // 按住多久算「拿起」
    var TOL = 8;         // 拿起前允许的抖动
    var ref = null;      // DotNetObjectReference（C# 首帧交进来）
    var st = null;       // 当前手势
    var dbg = { down: 0, guard: 0, armed: 0, up: 0, drop: 0, miss: 0, err: '', ev: '' };

    function clearHl() {
        var prev = document.querySelectorAll('.jg-cat-head.drop-target, .jg-cat-head.drop-above, .jg-cat-head.drop-below');
        for (var i = 0; i < prev.length; i++) {
            prev[i].classList.remove('drop-target');
            prev[i].classList.remove('drop-above');
            prev[i].classList.remove('drop-below');
        }
    }
    function headAt(x, y) {
        var el = document.elementFromPoint(x, y);
        return el && el.closest ? el.closest('.jg-cat-head[data-cat-key]') : null;
    }

    // 分类重排的落点 = 指针在目标段头的上半 / 下半 ⇒ 插到它前面 / 后面。
    // 只看"落在哪个分类头上"没法表达顺序（放上半和放下半意思完全不同），
    // 所以画一条横线标出插入缝，而不是把整块高亮。
    function posInHead(h, y) {
        var r = h.getBoundingClientRect();
        return y < r.top + r.height / 2 ? 'before' : 'after';
    }
    function place(g, x, y) { g.style.left = x + 'px'; g.style.top = y + 'px'; }

    // 调 .NET：把失败也记下来（上一版就是这里静默失败，看起来像"什么都没发生"）
    function call(name, a, b, c) {
        if (!ref) { dbg.err = 'no-ref'; return; }
        try {
            var p = c === undefined ? ref.invokeMethodAsync(name, a, b)
                                    : ref.invokeMethodAsync(name, a, b, c);
            if (p && p.catch) p.catch(function (e) { dbg.err = name + ':' + e; });
        } catch (e) { dbg.err = name + ':sync:' + e; }
    }

    function reset() {
        var s = st;
        st = null;
        if (scrollRaf) { cancelAnimationFrame(scrollRaf); scrollRaf = null; }
        if (!s) return null;
        clearTimeout(s.timer);
        document.body.classList.remove('jg-dragging');
        document.body.classList.remove('jg-dragging-cat');
        if (s.el) s.el.classList.remove('jg-drag-src');
        if (s.ghost && s.ghost.parentNode) s.ghost.parentNode.removeChild(s.ghost);
        clearHl();
        return s;
    }

    // v1.3.79：拖到上下边缘自动滚动 + 落点刷新
    // 指针停在边缘不动时 onMove 根本不触发，所以用 rAF 自走；滚动后必须重算落点，
    // 否则"滚了但高亮还停在原来那一行"。
    var lastPt = { x: 0, y: 0 };
    var scrollRaf = null;

    function refreshDrop(x, y) {
        if (!st || !st.armed) return;
        if (st.ghost) place(st.ghost, x, y);
        var h = headAt(x, y);
        clearHl();
        st.pos = '';
        if (!h) return;
        var tgt = h.getAttribute('data-cat-key');
        if (st.key.indexOf('c:') === 0) {
            // 分类重排：拖的是分类 → 画插入线（不画整块高亮）。
            // 自己的头上不画；「未分类」段不参与排序，也不能当落点。
            if (tgt === '' || tgt === st.key.slice(2)) return;
            st.pos = posInHead(h, y);
            h.classList.add(st.pos === 'before' ? 'drop-above' : 'drop-below');
        } else {
            // mod 归类：整段都要接（放这块区域里就行），所以整块点亮
            h.classList.add('drop-target');
        }
    }

    function autoScrollTick() {
        if (!st || !st.armed) { scrollRaf = null; return; }
        var sc = document.querySelector('.jg-main');
        if (sc && sc.scrollHeight > sc.clientHeight + 1) {
            var r = sc.getBoundingClientRect();
            var EDGE = 100, MAX = 26;
            var dy = 0;
            if (lastPt.y < r.top + EDGE)
                dy = -MAX * Math.min(1, (r.top + EDGE - lastPt.y) / EDGE);
            else if (lastPt.y > r.bottom - EDGE)
                dy = MAX * Math.min(1, (lastPt.y - (r.bottom - EDGE)) / EDGE);
            if (dy !== 0) {
                var before = sc.scrollTop;
                sc.scrollTop = before + dy;
                if (sc.scrollTop !== before) refreshDrop(lastPt.x, lastPt.y);
            }
        }
        scrollRaf = requestAnimationFrame(autoScrollTick);
    }

    function arm() {
        if (!st) return;
        st.armed = true;
        dbg.armed++;
        document.body.classList.add('jg-dragging');
        if (st.key.indexOf('c:') === 0) document.body.classList.add('jg-dragging-cat');
        st.el.classList.add('jg-drag-src');
        var g = document.createElement('div');
        g.className = 'jg-drag-ghost';
        g.textContent = st.el.getAttribute('data-drag-label') || '';
        document.body.appendChild(g);
        st.ghost = g;
        place(g, st.x, st.y);
        lastPt.x = st.x; lastPt.y = st.y;
        if (!scrollRaf) scrollRaf = requestAnimationFrame(autoScrollTick);
        call('OnDragProbe', 'armed', st.key);
    }

    function onDown(e) {
        dbg.down++;
        dbg.ev = e.type;
        if (e.button !== 0) return;
        if (st) return;                             // pointerdown / mousedown 去重
        var t = e.target;
        if (!t || !t.closest) { dbg.guard = -2; return; }
        if (t.closest('button, input, select, textarea, a')) { dbg.guard = -3; return; }
        var el = t.closest('[data-drag-key]');
        if (!el) { dbg.guard = -4; return; }
        var key = el.getAttribute('data-drag-key');
        if (!key) { dbg.guard = -5; return; }
        dbg.guard = 1;
        st = { el: el, key: key, x: e.clientX, y: e.clientY, armed: false, ghost: null, timer: null };
        st.timer = setTimeout(arm, HOLD);
    }

    function onMove(e) {
        if (!st) return;
        if (!st.armed) {
            if (Math.abs(e.clientX - st.x) > TOL || Math.abs(e.clientY - st.y) > TOL) reset();
            return;
        }
        if (e.cancelable) e.preventDefault();
        lastPt.x = e.clientX; lastPt.y = e.clientY;
        refreshDrop(e.clientX, e.clientY);
    }

    function onUp(e) {
        if (!st) return;
        dbg.up++;
        var armed = st.armed, key = st.key;
        var h = armed ? headAt(e.clientX, e.clientY) : null;
        var tgt = h ? h.getAttribute('data-cat-key') : null;
        // 落点在目标段头的上半 / 下半 ⇒ 插到前面 / 后面（只有分类重排用得上）
        var pos = (tgt !== null && key.indexOf('c:') === 0)
            ? posInHead(h, e.clientY) : '';
        reset();
        if (!armed) return;

        // 拖完这一击不该顺手选中行 / 折叠分段 —— 一次捕获吞掉（带超时兜底，
        // 有些落点不产生 click，留着的监听会吃掉下一次点击）
        var swallow = function (ev) { ev.stopPropagation(); ev.preventDefault(); off(); };
        var tid = setTimeout(off, 400);
        function off() { clearTimeout(tid); window.removeEventListener('click', swallow, true); }
        window.addEventListener('click', swallow, true);

        if (tgt !== null && key) {
            dbg.drop++;
            call('OnDragProbe', 'drop', key + ' → ' + (tgt === '' ? '未分类' : tgt) + (pos ? ' @' + pos : ''));
            call('OnLongPressDrop', key, tgt, pos);
        } else {
            dbg.miss++;
            call('OnDragProbe', 'drop-miss', key);
        }
    }

    // pointer 与 mouse 双挂（去重靠 st）
    document.addEventListener('pointerdown', onDown, { passive: true });
    document.addEventListener('mousedown', onDown, { passive: true });
    document.addEventListener('pointermove', onMove, { passive: false });
    document.addEventListener('mousemove', onMove, { passive: false });
    document.addEventListener('pointerup', onUp, { passive: true });
    document.addEventListener('mouseup', onUp, { passive: true });
    document.addEventListener('pointercancel', function () { reset(); }, { passive: true });

    window.junigridJs.longPressDragInit = function (dotNetRef) { ref = dotNetRef; };

    // 自检：合成一次「按下 →（过长按阈值）→ 抬起」，只落在 mod 行自己身上（不改数据）。
    // 直接回计数器，不依赖 .NET 回调。
    window.junigridJs.dragSelfTest = function () {
        return new Promise(function (resolve) {
            dbg.down = dbg.armed = dbg.up = dbg.guard = 0;
            dbg.err = '';
            var el = document.querySelector('.jg-modrow[data-drag-key]');
            if (!el) { resolve('no-target'); return; }
            var r = el.getBoundingClientRect();
            var x = r.left + r.width / 2, y = r.top + r.height / 2;
            var o = { bubbles: true, cancelable: true, clientX: x, clientY: y,
                      pointerId: 7, pointerType: 'mouse', isPrimary: true, button: 0 };
            try { el.dispatchEvent(new PointerEvent('pointerdown', o)); } catch (e) { dbg.err = 'dispatch:' + e; }
            setTimeout(function () {
                try { document.dispatchEvent(new PointerEvent('pointerup', o)); } catch (e) { }
                setTimeout(function () {
                    resolve('rows=' + document.querySelectorAll('[data-drag-key]').length
                        + ' down=' + dbg.down + ' guard=' + dbg.guard + ' armed=' + dbg.armed
                        + ' up=' + dbg.up + ' ref=' + (ref ? 1 : 0) + (dbg.err ? ' err=' + dbg.err : ''));
                }, 120);
            }, 520);
        });
    };
})();
