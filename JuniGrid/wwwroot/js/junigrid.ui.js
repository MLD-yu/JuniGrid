// ============================================================
// 通用 UI 交互：toast、光标倾斜、
// scrollSpy、存档/头像/更新/作者气泡 tooltip、聚焦辅助
// ============================================================
// ------------------ 全局 toast（默认黑底白字；kind="err" 红底白字；kind="ok" 系统强调色底白字；2.6s 自动消失） ------------------
// 直接挂 document.body，避开 Blazor 组件树里 transform/filter 祖先破坏 position:fixed 的坑。
// 同类不叠加：上一个还在就先移除再显示新的。
(function () {
    window.junigridJs = window.junigridJs || {};
    var current = null;
    var hideTimer = null;


    // 颜色主题：给 <html> 打 data-accent 选色卡；仅 "system" 时用内联 --jg-accent 注入系统色
    // （内联优先级最高，故非 system 时必须 removeProperty，让 CSS 主题块生效）
    // 返回值 = "mode|解析后的实际强调色"（WPF 拖放蒙层用，避免在 C# 里复制一份主题色表）
    // 注意：system 模式的界面是中性黑白，系统色只作辅助强调；蒙层在 C# 侧按 mode 取白色。
    function applyAccentDom(mode, sysHex) {
        var m = mode || 'system';
        try {
            var r = document.documentElement;
            r.dataset.accent = m;
            if (m === 'system' && sysHex) r.style.setProperty('--jg-accent', sysHex);
            else r.style.removeProperty('--jg-accent');
            var got = getComputedStyle(r).getPropertyValue('--jg-accent').trim();
            return m + '|' + (got || sysHex || '#7aa2ff');
        } catch (e) { return m + '|' + (sysHex || '#7aa2ff'); }
    }
    window.junigridJs.applyAccent = applyAccentDom;

    // v1.2.11：主题切换的「圆形扩散」——View Transitions 给新旧两版各拍一张快照，
    // 新快照用 clip-path 从点击点长出圆形，新配色像墨水一样铺满全站（样式在 base.css 末尾）。
    // 只做视觉：不支持的旧 WebView2、或用户开了「减少动效」时，直接瞬间切，行为与原先一致。
    // 返回 Promise<"mode|强调色">，在「新快照拍完」就 resolve，不等 0.55s 动画 ——
    // C# 侧拿它去刷 WPF 拖放蒙层配色，没道理被动画拖住。
    window.junigridJs.applyAccentWave = function (mode, sysHex, x, y) {
        var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        if (!document.startViewTransition || reduced) {
            return Promise.resolve(applyAccentDom(mode, sysHex));
        }

        // 键盘 Enter 触发时没有坐标：退化成从这张色卡的圆心扩散
        if ((!x && !y)) {
            var card = document.querySelector('.jg-accent-card[data-key="' + (mode || 'system') + '"]');
            if (card) {
                var b = card.getBoundingClientRect();
                x = b.left + b.width / 2;
                y = b.top + b.height / 2;
            }
        }
        if (typeof x !== 'number' || typeof y !== 'number' || isNaN(x) || isNaN(y)) {
            return Promise.resolve(applyAccentDom(mode, sysHex));
        }

        // 半径 = 点击点到最远那个角的距离，保证圆最终铺满整个视口
        var r = Math.hypot(Math.max(x, window.innerWidth - x), Math.max(y, window.innerHeight - y)) + 4;
        var root = document.documentElement;
        root.style.setProperty('--jg-vt-x', x + 'px');
        root.style.setProperty('--jg-vt-y', y + 'px');
        root.style.setProperty('--jg-vt-r', r + 'px');

        var out = mode, vt;
        try {
            vt = document.startViewTransition(function () { out = applyAccentDom(mode, sysHex); });
        } catch (e) {
            root.style.removeProperty('--jg-vt-x');
            root.style.removeProperty('--jg-vt-y');
            root.style.removeProperty('--jg-vt-r');
            return Promise.resolve(applyAccentDom(mode, sysHex));
        }

        var done = vt.updateCallbackDone || Promise.resolve();
        // 动画收场后再清变量（被 skipTransition 打断也算收场）；变量留着不影响，只是别积脏值
        (vt.finished || Promise.resolve()).then(clean, clean);
        function clean() {
            root.style.removeProperty('--jg-vt-x');
            root.style.removeProperty('--jg-vt-y');
            root.style.removeProperty('--jg-vt-r');
        }
        return done.then(function () { return out; }, function () { return out; });
    };

    // 行尾「⋮」菜单：先按默认（向下）量一次，下方放不下就整块翻到按钮上方。
    // 必须在渲染后量 —— 菜单高度取决于这一行有几个条目（5~7 个不等）。
    window.junigridJs.fitModMenu = function () {
        var dd = document.querySelector('.jg-sort-dd.jg-modmenu.open');
        if (!dd) return;
        dd.classList.remove('up');
        var menu = dd.querySelector('.jg-sort-menu');
        if (!menu) return;
        var r = menu.getBoundingClientRect();
        if (r.bottom > window.innerHeight - 8) dd.classList.add('up');
    };

    window.junigridJs.fitPkVar = function () {
        // 素材包对象弹窗里的版本下拉：往下放会掉出弹窗 ⇒ 按实测矩形翻到上方。
        // 量的是最近的 .jg-modal 而不是窗口 —— 裁掉菜单的是弹窗自己。
        var dd = document.querySelector('.jg-pk-vardd.open');
        if (!dd) return;
        dd.classList.remove('up');
        var menu = dd.querySelector('.jg-sort-menu');
        if (!menu) return;
        var host = menu.closest('.jg-modal') || document.documentElement;
        if (menu.getBoundingClientRect().bottom > host.getBoundingClientRect().bottom - 8) dd.classList.add('up');
    };

    window.junigridJs.fitPickVdd = function () {
        // 详情页「选择要安装的版本」弹窗里的历史版本下拉：往下放会掉出弹窗 ⇒ 按实测矩形翻到上方。
        // 和 fitPkVar 同因不同框（那边是素材包对象弹窗的版本下拉）。
        var dd = document.querySelector('.jg-pick-vdd.open');
        if (!dd) return;
        dd.classList.remove('up');
        var menu = dd.querySelector('.jg-sort-menu');
        if (!menu) return;
        var host = menu.closest('.jg-modal') || document.documentElement;
        if (menu.getBoundingClientRect().bottom > host.getBoundingClientRect().bottom - 8) dd.classList.add('up');
    };

    window.junigridJs.toast = function (msg, kind) {
        try {
            if (current) {
                if (hideTimer) clearTimeout(hideTimer);
                if (current.parentNode) current.parentNode.removeChild(current);
            }
            var el = document.createElement('div');
            el.className = 'jg-toast-live' + (kind === 'err' ? ' err' : kind === 'ok' ? ' ok' : '');
            el.textContent = msg;
            document.body.appendChild(el);
            current = el;
            if (window.gsap) {
                // 从下方浮起：从上面落下来会先盖住顶导航（浮条落点已经让开导航，入场也别越过它）
                gsap.fromTo(el, { y: 12, opacity: 0, scale: 0.96 },
                    { y: 0, opacity: 1, scale: 1, duration: 0.28, ease: 'back.out(2)' });
            }
            hideTimer = setTimeout(function () {
                if (window.gsap) {
                    gsap.to(el, { y: 10, opacity: 0, duration: 0.3, ease: 'power2.in',
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

(function () {
    window.junigridJs = window.junigridJs || {};
    var tracked = null, trackedKey = null, ticking = false, scrollHandler = null;

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
        // v1.09：.jg-main 跨页持久 —— 切页换 key 时先摘掉旧 handler（否则导航 N 次后
        // 一次滚动会触发 N 个回调，sessionStorage 写入也重复 N 次）
        if (tracked && scrollHandler) tracked.removeEventListener('scroll', scrollHandler);
        tracked = el; trackedKey = key;
        // v1.06.8：双重门控 —— 监听挂在跨页共享的 .jg-main 上，组件销毁后监听仍在：
        // ① 只在绑定时的页面 URL 上才写（否则在下载页滚动会把下载页的位置写进 modslist，
        //    返回列表就回不到原位）；② 页面切换过渡期（__jgScrollLock）不写。
        var pagePath = location.pathname + location.search;
        scrollHandler = function () {
            if (ticking) return;
            ticking = true;
            requestAnimationFrame(function () {
                ticking = false;
                if (window.__jgScrollLock) return;
                if (location.pathname + location.search !== pagePath) return;
                try { sessionStorage.setItem('jg-scroll:' + trackedKey, String(el.scrollTop)); } catch (e) { }
            });
        };
        el.addEventListener('scroll', scrollHandler, { passive: true });
    };
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



// ------------------ v1.1.9：热力图月份悬停 —— 从当月 1 号到月末的阶梯描边（深色白线/浅色黑线，配色在 CSS） ------------------
// 月份标签带 data-month、格子带 data-m；悬停标签时把该月格子按周列聚合，
// 沿真实阶梯边界画 SVG 折线（首列从 1 号所在行起、末列到月末所在行止，
// 中间列贯通 7 行）—— 不是外接矩形，不会把相邻月份圈进去；
// 阶梯上下拐点落在列间隙中点，线整体外扩 2px 不贴格子；pointer-events:none 不挡格子 hover
(function () {
    var svg = null, path = null;
    var NS = 'http://www.w3.org/2000/svg';
    function ensure(parent) {
        if (!svg || svg.parentNode !== parent) {
            if (svg) svg.remove();
            svg = document.createElementNS(NS, 'svg');
            // SVG 元素的 className 是只读 SVGAnimatedString，直接赋值会被静默忽略，必须 setAttribute
            svg.setAttribute('class', 'jg-heat-month-outline');
            path = document.createElementNS(NS, 'path');
            svg.appendChild(path);
            parent.appendChild(svg);
        }
        return svg;
    }
    function show(label) {
        var scroll = label.closest('.jg-heat-scroll');
        var grid = label.closest('.jg-heat-grid');
        if (!scroll || !grid) return;
        var cells = grid.querySelectorAll('.jg-heat-cell[data-m="' + label.getAttribute('data-month') + '"]');
        if (!cells.length) return;
        // v1.2.1：用 offsetLeft/offsetTop（布局值）测量，不用 getBoundingClientRect ——
        // 格子有 :hover scale(1.35) 过渡，刚划过的格子 rect 是膨胀的，会把聚类撑裂、
        // gap 变负数，整个阶梯就画歪（变形与鼠标路径相关的根因）。
        // offset 累加到 .jg-heat-scroll 为止，得到内容坐标；transform 不影响布局值，测量永远稳定。
        var rects = [];
        for (var i = 0; i < cells.length; i++) {
            var el = cells[i], x = 0, y = 0, node = el;
            while (node && node !== scroll) { x += node.offsetLeft; y += node.offsetTop; node = node.offsetParent; }
            var rc = { el: el, l: x, t: y, r: x + el.offsetWidth, b: y + el.offsetHeight };
            el.__hr = rc;   // 聚焦缩放时反查格子自己的布局矩形
            rects.push(rc);
        }
        rects.sort(function (a, b) { return a.l - b.l; });
        // 按 left 聚类成周列区段，半格容差防分裂
        var cols = [];
        for (var j = 0; j < rects.length; j++) {
            var rc = rects[j], g = cols.length ? cols[cols.length - 1] : null;
            if (g && rc.l - g.l < (g.r - g.l) * 0.6) {
                if (rc.t < g.t) g.t = rc.t;
                if (rc.b > g.b) g.b = rc.b;
            } else cols.push({ l: rc.l, r: rc.r, t: rc.t, b: rc.b });
        }
        if (cols.length < 2) return;   // 一个月至少跨 4 列，防御性兜底
        var p = 2;                                   // 线整体外扩的呼吸空隙
        var gap = cols.length > 1 ? cols[1].l - cols[0].r : 0;
        var mid1 = cols[0].r + gap / 2;              // 首列→次列的上拐点（列间隙中点）
        var mid2 = cols[cols.length - 1].l - gap / 2; // 末列→前列的下拐点（列间隙中点）
        var c0 = cols[0], cl = cols[cols.length - 1];
        // 顺时针描边：首列顶 → 上拐点升至整月顶 → 右缘 → 末列底 → 下拐点降至整月底 → 闭合
        var d = 'M' + (c0.l - p) + ' ' + (c0.t - p)
            + 'L' + (mid1 - p) + ' ' + (c0.t - p)
            + 'L' + (mid1 - p) + ' ' + (cols[1].t - p)
            + 'L' + (cl.r + p) + ' ' + (cols[1].t - p)
            + 'L' + (cl.r + p) + ' ' + (cl.b + p)
            + 'L' + (mid2 + p) + ' ' + (cl.b + p)
            + 'L' + (mid2 + p) + ' ' + (c0.b + p)
            + 'L' + (c0.l - p) + ' ' + (c0.b + p) + 'Z';
        ensure(scroll);
        path.setAttribute('d', d);
        svg.classList.add('on');
        // 月份聚焦：整月作为刚体放大 —— 变换原点取「包围盒顶边中点」：顶边和月份标签纹丝不动，
        // 月块只向下长（不会上侵盖住标签）；描边 SVG 以同一原点同步缩放，圈跟着一起大。
        var ZOOM = 1.15;
        var bt = Infinity, bb = -Infinity;
        for (var k = 0; k < cols.length; k++) {
            if (cols[k].t < bt) bt = cols[k].t;
            if (cols[k].b > bb) bb = cols[k].b;
        }
        var cx = (cols[0].l + cl.r) / 2, cy = bt;
        focusMonth(grid, label.getAttribute('data-month'), cx, cy);
        svg.style.transformOrigin = cx + 'px ' + cy + 'px';
        svg.style.transform = 'scale(' + ZOOM + ')';
    }
    function focusMonth(grid, m, cx, cy) {
        grid.classList.add('heat-focus');
        // 放大溢出不顶出滚动条：聚焦期间锁掉滚动（聚焦的月块必在可视区内）
        var scroll = grid.closest('.jg-heat-scroll');
        if (scroll) scroll.classList.add('heat-zooming');
        var hit = grid.querySelectorAll('.jg-heat-cell[data-m="' + m + '"]');
        for (var i = 0; i < hit.length; i++) {
            var c = hit[i];
            var rc = c.__hr;
            if (rc) c.style.transformOrigin = (cx - rc.l) + 'px ' + (cy - rc.t) + 'px';
            c.classList.add('hl');
            c.classList.remove('dim');
        }
        var rest = grid.querySelectorAll('.jg-heat-cell:not([data-m="' + m + '"])');
        for (var j = 0; j < rest.length; j++) {
            rest[j].classList.add('dim');
            rest[j].classList.remove('hl');
        }
        var labels = grid.querySelectorAll('.jg-heat-month span[data-month]');
        for (var n = 0; n < labels.length; n++) {
            var l = labels[n];
            l.classList.toggle('hl', l.getAttribute('data-month') === m);
            l.classList.toggle('dim', l.getAttribute('data-month') !== m);
        }
    }
    function unfocusMonth() {
        var zs = document.querySelector('.jg-heat-scroll.heat-zooming');
        if (zs) zs.classList.remove('heat-zooming');
        var grid = document.querySelector('.jg-heat-grid.heat-focus');
        if (grid) {
            grid.classList.remove('heat-focus');
            var els = grid.querySelectorAll('.hl, .dim');
            for (var i = 0; i < els.length; i++) {
                els[i].classList.remove('hl', 'dim');
                els[i].style.transformOrigin = '';
            }
        }
        if (svg) svg.style.transform = '';
    }
    function hide() { unfocusMonth(); if (svg) svg.classList.remove('on'); }
    document.addEventListener('mouseover', function (e) {
        var label = e.target.closest ? e.target.closest('.jg-heat-month span[data-month]') : null;
        if (label) show(label); else hide();
    });
    document.addEventListener('mouseout', function (e) {
        var label = e.target.closest ? e.target.closest('.jg-heat-month span[data-month]') : null;
        if (!label) return;
        // 直接滑到相邻月份标签时不闪断，由下一次 mouseover 换线
        var next = e.relatedTarget;
        if (next && next.closest && next.closest('.jg-heat-month span[data-month]')) return;
        hide();
    });
    document.addEventListener('mousedown', hide, true);
})();

// ------------------ v1.x：设置页「?」帮助问号的 GSAP 弹性 tooltip ------------------
// hover 弹入（elastic），移开立即消失（不走反向动画）；事件委托，Blazor 重渲染无需重新绑定
(function () {
    function bubbleOf(wrap) { return wrap.querySelector('.jg-help-tip-bubble'); }
    function btnOf(wrap) { return wrap.querySelector('.jg-help-tip-btn'); }
    // 朝上的气泡靠 translateX(-50%) 居中，朝下的（--down）贴问号左缘、CSS 写的是 transform:none。
    // 这里必须跟着变体走：GSAP 的行内 transform 会盖掉那条 transform:none，
    // 写死 -50 会让朝下气泡从第二次 hover 起整体左移半个气泡宽（实测 130px）。
    function xPercentOf(wrap) {
        var cls = wrap.classList;
        // --down / --right 的气泡都用 CSS 的 transform:none 对齐，GSAP 这边就不能再给 -50%
        return (cls.contains('jg-help-tip--down') || cls.contains('jg-help-tip--right')) ? 0 : -50;
    }
    function close(wrap) {
        if (wrap._tipTl) { wrap._tipTl.kill(); wrap._tipTl = null; }
        var b = bubbleOf(wrap), btn = btnOf(wrap);
        if (b) gsap.set(b, { autoAlpha: 0, y: 14, scale: 0.4, xPercent: xPercentOf(wrap) });
        if (btn) gsap.set(btn, { scale: 1 });
    }
    document.addEventListener('mouseover', function (e) {
        var wrap = e.target.closest ? e.target.closest('.jg-help-tip') : null;
        if (!wrap) return;
        var bubble = bubbleOf(wrap), btn = btnOf(wrap);
        if (!bubble) return;
        if (wrap._tipTl) wrap._tipTl.kill();
        wrap._tipTl = gsap.timeline({ paused: true })
            // 透明度不进 elastic 缓动：elastic 会让 opacity 在弹跳里反复跌破 1（实测 0.84 / 0.98），
            // 奶油底就变半透明、透出底下那行文字。弹入第一帧直接拉满，只让缩放和位移去弹。
            .set(bubble, { autoAlpha: 1, xPercent: xPercentOf(wrap) }, 0)
            .to(bubble, {
                y: 0, scale: 1, duration: 1,
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



// ─── 对话框退场 ──────────────────────────────────────────────────────────
// Blazor 一清状态就把节点从 DOM 摘掉，CSS 的退场动画根本没机会播。
// 所以先拦下"关闭"那一击：演 180ms 退场，放完把同一击重新派发一次，让真正的 @onclick 去清状态。
// 只拦关闭路径（点遮罩、点取消）；确认按钮不拦 —— 动作已生效还先演一段，反馈就拖慢了。
(function () {
    var OUT_MS = 180;
    document.addEventListener("click", function (e) {
        if (!e.target || !e.target.closest) return;
        var overlay = e.target.closest(".jg-modal-overlay");
        if (!overlay || overlay.__modalClosing || overlay.__modalAllow) return;
        if (overlay.querySelector(".jg-ver-modal")) return;   // 版本弹窗自己走 FLIP，别抢它
        var btn = e.target.closest("button");
        var isBackdrop = e.target === overlay;
        var isCancel = btn && btn.hasAttribute("data-modal-close");
        if (!isBackdrop && !isCancel) return;
        e.preventDefault();
        e.stopPropagation();
        overlay.__modalClosing = true;
        overlay.classList.add("closing");
        var target = isCancel ? btn : overlay;
        setTimeout(function () {
            overlay.__modalClosing = false;
            overlay.__modalAllow = true;
            target.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
            overlay.__modalAllow = false;
            // v1.3.18 兜底：重派发这一击只要被处理器拒了（下载中 return 之类），
            // 遮罩就带着 .closing 留在 DOM 里 —— 退场动画是 fill:both，opacity 已是 0，
            // 肉眼看不见，但它 position:fixed 铺满整窗、照样吃指针事件，
            // 于是整页点什么都没反应（实测 12:50 用户连点遮罩三次都没关掉）。
            // 这里只掐指针事件，不动节点：真被挡住的时候页面至少还能用。
            setTimeout(function () {
                if (document.body.contains(overlay) && overlay.classList.contains("closing"))
                    overlay.classList.add("jg-modal-stale");
            }, 400);
        }, OUT_MS);
    }, true);
})();

// SwipeRow（vanilla）：左划露出删除。rail 和 surface 由同一个露出量驱动
// （surface 左移 ex，rail 从它右边缘下跟着露出），所以按钮永远贴着行、中间不露底色。
// 露出量到按钮宽封顶：再多划只有 22px 内容橡皮筋，不自动删，删除只认点击。

// 当前敞着的行记在这里而不是 swipeRowsInit 内部：那个函数每次渲染都会再跑一遍，
// 各自存一份的话，文档监听器和各行闭包握的就不是同一个变量（实测点外面收不回）。
window.junigridJs.__swipeOpen = null;
if (!window.junigridJs.__swipeDocBound) {
    window.junigridJs.__swipeDocBound = true;
    // 点这行外面（别的行、标题、空白）就先收回，不用非得往右划回来
    document.addEventListener("pointerdown", function (e) {
        window.junigridJs.__swipeSwallow = false;
        var o = window.junigridJs.__swipeOpen;
        if (o && !o.el.contains(e.target)) {
            o.close();
            // 这一下是用来收抽屉的，别再让它落到别的行上去切版本/下载
            window.junigridJs.__swipeSwallow = true;
        }
    }, true);
    document.addEventListener("click", function (e) {
        if (!window.junigridJs.__swipeSwallow) return;
        window.junigridJs.__swipeSwallow = false;
        e.preventDefault();
        e.stopPropagation();
    }, true);
    // 指针移出窗口再松手时 pointerup 不会送到，grip 会烂在那儿：那一行冻在橡皮筋位，
    // 而且之后所有行都划不动（down 见 grip 就 return）。失焦/切走时按“就地收手”结算。
    document.addEventListener("visibilitychange", function () {
        if (document.hidden && window.junigridJs.__swipeAbort) window.junigridJs.__swipeAbort();
    });
    window.addEventListener("blur", function () {
        if (window.junigridJs.__swipeAbort) window.junigridJs.__swipeAbort();
    });
}

window.junigridJs.swipeRowsInit = function (rootSel) {
    var root = document.querySelector(rootSel || ".jg-ver-vlist");
    if (!root) return;
    var HYST = 10, FLICK = 110, DECEL = 0.998;
    var clamp = function (v, lo, hi) { return Math.min(hi, Math.max(lo, v)); };
    var project = function (v) { return ((v / 1000) * DECEL) / (1 - DECEL); };
    function vel(hist) {
        if (hist.length < 2) return 0;
        var a = hist[0], b = hist[hist.length - 1];
        return ((b[1] - a[1]) / Math.max(1, b[0] - a[0])) * 1000;
    }

    root.querySelectorAll(".swipe-row").forEach(function (row) {
        if (row.__swipeBound) return;
        var surf = row.querySelector(".swipe-row__surface");
        var rail = row.querySelector(".swipe-row__rail");
        var act = row.querySelector(".swipe-row__action");
        if (!surf || !rail || !act) return;   // 没有本地包的行没有抽屉，不绑
        row.__swipeBound = true;

        var A = rail.offsetWidth || 80;
        var SOFT = 22, NEG = 28, K = 70;   // 越过终点的渐进阻力：只撑内容，按钮全露后钉在行右边
        var glyph = act.querySelector(".swipe-row__glyph");
        var ex = 0, grip = null, unwatch = null, swallow = false;

        var clampEx = function (v) { return clamp(v, -NEG, A + SOFT); };
        // 0..A 跟手；越界后越划越沉，行程渐近封顶（划到底也不会多露一颗按钮）
        function resist(raw) {
            if (raw > A) return A + SOFT * ((raw - A) / K) / (1 + (raw - A) / K);
            if (raw < 0) return -NEG * ((-raw) / K) / (1 + (-raw) / K);
            return raw;
        }

        function put(target, anim, v) {
            ex = target;
            var sx = -target;
            var rx = Math.max(0, A - target);   // 全露之后面板不再跟着往左跑
            if (target === A) { var o = window.junigridJs.__swipeOpen; if (!o || o.el !== row) window.junigridJs.__swipeOpen = { el: row, close: function () { put(0, true, 0); } }; }
            else if (window.junigridJs.__swipeOpen && window.junigridJs.__swipeOpen.el === row) window.junigridJs.__swipeOpen = null;
            if (typeof gsap === "undefined") {
                surf.style.transform = "translateX(" + sx + "px)";
                rail.style.transform = "translateX(" + rx + "px)";
                return;
            }
            if (!anim) {
                gsap.set(surf, { x: sx });
                gsap.set(rail, { x: rx });
                return;
            }
            // 越甩回弹越狠，慢放也带一次回弹；两条 tween 同参数 → 过冲的每一帧都还贴合
            var k = clamp(Math.abs(v) / 900, 0, 1);
            var opt = { duration: 0.45 + k * 0.15, ease: "back.out(" + (2.1 + k * 1.5) + ")", overwrite: "auto" };
            gsap.to(surf, Object.assign({ x: sx }, opt));
            gsap.to(rail, Object.assign({ x: rx }, opt));
            if (target === A && glyph) {
                gsap.fromTo(glyph, { scale: 0.8 }, { scale: 1, duration: 0.5, ease: "back.out(3)", overwrite: "auto", clearProps: "transform" });
            }
        }
        // 动画进行中被打断时以真实位置为准，别从上一帧的目标值跳走
        function current() {
            if (typeof gsap !== "undefined") ex = clampEx(-(gsap.getProperty(surf, "x") || 0));
            return ex;
        }

        function down(e) {
            if (e.button !== 0 || e.target.closest(".swipe-row__action")) return;
            // 上一笔没收到 up（窗口外松手）就先就地结算，绝不能让 grip 卡住所有行
            if (grip) abandon();
            if (typeof gsap !== "undefined") { gsap.killTweensOf(surf); gsap.killTweensOf(rail); }
            grip = { id: e.pointerId, cx: e.clientX, cy: e.clientY, base: current(), grab: false, hist: [], wasOpen: ex > 0 };
            window.junigridJs.__swipeAbort = abandon;
            swallow = false;
            unwatch && unwatch();
            unwatch = watch();
        }
        function move(e) {
            if (!grip || grip.id !== e.pointerId) return;
            var dx = e.clientX - grip.cx, dy = e.clientY - grip.cy;
            if (!grip.grab) {
                if (Math.abs(dx) < HYST || Math.abs(dx) < Math.abs(dy)) return;
                grip.grab = true;
                row.setAttribute("data-dragging", "");
            }
            var now = performance.now();
            // 露出量到按钮宽封顶，再多划只有 22px 内容橡皮筋（按钮不会多露一点）
            var next = resist(grip.base - dx);
            put(next, false, 0);
            grip.hist.push([now, next]);
            if (grip.hist.length > 4) grip.hist.shift();
        }
        function end(g) {
            grip = null;
            if (window.junigridJs.__swipeAbort === abandon) window.junigridJs.__swipeAbort = null;
            unwatch && unwatch();
            unwatch = null;
            row.removeAttribute("data-dragging");
            if (!g.grab) { if (ex !== 0) put(0, true, 0); return; }
            var v = vel(g.hist);
            // 甩左（露出量在涨）才开，甩右就关；慢放看惯性投影过没过半
            put(Math.abs(v) >= FLICK ? (v > 0 ? A : 0) : (ex + project(v) > A / 2 ? A : 0), true, v);
        }
        function up(e) {
            if (!grip || grip.id !== e.pointerId) return;
            var g = grip;
            // 划过一遍、或抽屉敞着的时候点行：只收抽屉，不把这一击交给「切换版本」
            swallow = g.grab || g.wasOpen;
            end(g);
        }
        // 按不住也松不开的那种死局：没有速度可算，就按当前露出量收手
        function abandon() { if (grip) end({ id: grip.id, grab: grip.grab, wasOpen: grip.wasOpen, hist: [] }); }
        function watch() {
            function om(ev) { if (ev.isTrusted) move(ev); }
            function ou(ev) { if (ev.isTrusted) up(ev); }
            window.addEventListener("pointermove", om);
            window.addEventListener("pointerup", ou);
            window.addEventListener("pointercancel", ou);
            return function () {
                window.removeEventListener("pointermove", om);
                window.removeEventListener("pointerup", ou);
                window.removeEventListener("pointercancel", ou);
            };
        }
        surf.addEventListener("pointerdown", down);
        act.addEventListener("click", function () { put(0, true, 0); });
        surf.addEventListener("click", function (e) {
            if (!swallow) return;
            swallow = false;
            e.preventDefault();
            e.stopPropagation();
        }, true);
    });
};

// App Store 卡片展开（motion.dev animate-view-app-store）：
// 源卡矩形 → 弹窗矩形 FLIP，spring ≈ visualDuration 0.35 / bounce 0.25
window.junigridJs.flipModal = function (srcId, open) {
    var src = document.getElementById(srcId);
    var panel = document.querySelector(".jg-ver-modal");
    var overlay = document.querySelector(".jg-modal-overlay");
    if (!panel || typeof gsap === "undefined") return;
    if (panel.__flipTl) { panel.__flipTl.kill(); panel.__flipTl = null; }
    var kids = panel.children;
    var s = src ? src.getBoundingClientRect() : null;
    var d = panel.getBoundingClientRect();
    // motion spring bounce 0.2~0.3 → back.out 轻微过冲
    var EASE = open ? "back.out(1.35)" : "power2.in";
    var DUR = open ? 0.38 : 0.28;
    var tl = gsap.timeline();
    panel.__flipTl = tl;

    if (open) {
        tl.fromTo(overlay, { opacity: 0 }, { opacity: 1, duration: 0.25, ease: "power2.out" }, 0);
        if (s && s.width > 0 && d.width > 0) {
            var sx = s.width / d.width, sy = s.height / d.height;
            var dx = (s.left + s.width / 2) - (d.left + d.width / 2);
            var dy = (s.top + s.height / 2) - (d.top + d.height / 2);
            tl.fromTo(panel,
                { x: dx, y: dy, scaleX: sx, scaleY: sy, transformOrigin: "center center" },
                { x: 0, y: 0, scaleX: 1, scaleY: 1, duration: DUR, ease: EASE }, 0);
        } else {
            tl.fromTo(panel, { scale: 0.92, opacity: 0, y: 14 },
                { scale: 1, opacity: 1, y: 0, duration: DUR, ease: EASE }, 0);
        }
        tl.fromTo(kids, { opacity: 0, y: 8 },
            { opacity: 1, y: 0, duration: 0.28, delay: 0.12, ease: "power2.out", stagger: 0.02 }, 0);
        return;
    }
    tl.to(kids, { opacity: 0, y: 6, duration: 0.12, ease: "power2.in", overwrite: "auto" }, 0);
    if (s && s.width > 0 && d.width > 0) {
        var sx2 = s.width / d.width, sy2 = s.height / d.height;
        var dx2 = (s.left + s.width / 2) - (d.left + d.width / 2);
        var dy2 = (s.top + s.height / 2) - (d.top + d.height / 2);
        tl.to(panel, {
            x: dx2, y: dy2, scaleX: sx2, scaleY: sy2,
            duration: DUR, ease: EASE, overwrite: "auto"
        }, 0.04);
    } else {
        tl.to(panel, { scale: 0.92, opacity: 0, y: 12,
            duration: 0.22, ease: EASE, overwrite: "auto" }, 0.04);
    }
    tl.to(overlay, { opacity: 0, duration: 0.2, ease: "power2.in", overwrite: "auto" }, 0.08);
};

// motion.dev Clerk User Button：layoutId 双形变
//  1) 容器：圆钮 → 卡片（同锚点胀出）
//  2) 头像：FLIP 从钮位飞进卡片头（原 DOM 不搬，fixed 飞，Blazor 安全）
//  3) 内容：blur 淡入（contentAnimations）
// spring ≈ bounce 0.15 / visualDuration 0.25
window.junigridJs.userTipInit = function (wrapId, bubbleId) {
    var wrap = document.getElementById(wrapId);
    var bubble = document.getElementById(bubbleId);
    if (!wrap || !bubble) return;
    if (typeof gsap === "undefined") { wrap.classList.add("jg-user-tip-nogsap"); return; }
    if (wrap.__tipBound) return;
    wrap.__tipBound = true;

    var btn = wrap.querySelector(".jg-user-tip-btn");
    var slot = wrap.querySelector(".jg-user-tip-ava-slot");
    var fades = wrap.querySelectorAll(".jg-user-tip-fade");
    // motion SPRING
    var EASE = "back.out(1.15)", DUR = 0.35;
    var isOpen = false, tl = null;

    function avatarEl() { return wrap.querySelector(".jg-user-tip-btn .jg-user-tip-avatar"); }
    function landEl() { return wrap.querySelector(".jg-user-tip-ava-land"); }

    // 幽灵头像挂 body、position:fixed —— 不受卡片 scale/overflow 影响，FLIP 一定可见
    function flyGhost(fromR, toR, srcEl, onDone) {
        var g = srcEl.cloneNode(true);
        g.id = "";
        g.style.cssText = "position:fixed;margin:0;z-index:100000;pointer-events:none;left:" +
            fromR.left + "px;top:" + fromR.top + "px;width:" + fromR.width + "px;height:" + fromR.height +
            "px;border-radius:99px;object-fit:cover;box-sizing:border-box;";
        document.body.appendChild(g);
        gsap.to(g, {
            left: toR.left, top: toR.top, width: toR.width, height: toR.height,
            duration: DUR, ease: EASE,
            onComplete: function () {
                g.remove();
                if (onDone) onDone();
            }
        });
    }

    function setOpen(v) {
        if (v === isOpen) return;
        isOpen = v;
        wrap.classList.toggle("open", v);
        if (tl) tl.kill();

        var A = avatarEl(), B = landEl();
        var fades = wrap.querySelectorAll(".jg-user-tip-fade");

        // 全尺寸量完再缩 —— 缩放后 slot 的 rect 是假位置
        gsap.set(bubble, {
            autoAlpha: 0, scaleX: 1, scaleY: 1,
            borderRadius: 16, transformOrigin: "top right"
        });
        var sx = 40 / (bubble.offsetWidth || 216);
        var sy = 40 / (bubble.offsetHeight || 200);
        var aR = A && A.getBoundingClientRect();
        var bR = B && B.getBoundingClientRect();

        if (v) {
            gsap.set(bubble, { scaleX: sx, scaleY: sy, borderRadius: 99, autoAlpha: 1 });
            bubble.style.visibility = "visible";
            if (B) gsap.set(B, { autoAlpha: 0 });
            tl = gsap.timeline();
            tl.fromTo(bubble,
                { scaleX: sx, scaleY: sy, borderRadius: 99 },
                { scaleX: 1, scaleY: 1, borderRadius: 16, duration: DUR, ease: EASE, overwrite: "auto" }, 0);
            tl.fromTo(fades, { opacity: 0, filter: "blur(8px)" },
                { opacity: 1, filter: "blur(0px)", duration: 0.28, delay: 0.12, ease: "power2.out", overwrite: "auto" }, 0);
            // 幽灵从钮位平移到卡头；A 遮住，落定后露出 B
            if (A && B && aR && bR && aR.width) {
                flyGhost(aR, bR, A, function () {
                    if (B) gsap.set(B, { autoAlpha: 1 });
                    if (A) gsap.set(A, { autoAlpha: 0 });
                });
            } else if (B) {
                gsap.set(B, { autoAlpha: 1 });
            }
            return;
        }
        // 关：幽灵从卡头飞回钮位，再缩回圆钮
        gsap.set(bubble, { scaleX: 1, scaleY: 1, borderRadius: 16, autoAlpha: 1 });
        aR = A && A.getBoundingClientRect();
        bR = B && B.getBoundingClientRect();
        tl = gsap.timeline();
        tl.to(fades, { opacity: 0, filter: "blur(6px)", duration: 0.12, ease: "power2.in", overwrite: "auto" }, 0);
        tl.to(bubble, {
            autoAlpha: 0, scaleX: sx, scaleY: sy, borderRadius: 99,
            duration: 0.28, ease: "back.in(1.1)", overwrite: "auto",
            onComplete: function () { gsap.set(bubble, { autoAlpha: 0, scaleX: sx, scaleY: sy, borderRadius: 99 }); }
        }, 0.04);
        if (A && B && aR && bR && bR.width) {
            gsap.set(B, { autoAlpha: 0 });
            flyGhost(bR, aR, A, function () {
                if (A) gsap.set(A, { autoAlpha: 1 });
            });
        }
    }

    wrap.addEventListener("click", function (e) {
        if (bubble.contains(e.target)) return;
        e.preventDefault();
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
    // 透明度不进 elastic 缓动（同帮助问号）：elastic 绕终值振荡，opacity 会反复跌破 1，奶油底透出底下内容。
    // 必须是 .to() 不能是 .set()：关闭靠 tl.pause(0) 回卷，时间线里的 set 回卷后仍停在 1（实测会常显）。
    var tl = gsap.timeline({ paused: true })
        .to(bubble, { autoAlpha: 1, duration: 0.001, ease: "none" }, 0)
        .to(bubble, { y: 0, scale: 1, duration: 1.0, ease: "elastic.out(1.2, 0.3)" }, 0);
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
        // 透明度单拎出来、不跟 elastic 一起振荡（同帮助问号）；仍排在 0.08，高光先扫、气泡再弹的节拍不变
        .to(bubble, { autoAlpha: 1, duration: 0.001, ease: "none" }, 0.08)
        .to(bubble, { y: 0, scale: 1, duration: 0.9, ease: "elastic.out(1.2, 0.3)", easeReverse: "power3.in" }, 0.08);

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


// ── Motion characters-remaining 同款：备注弹窗剩余数字弹簧 ──
// 每次输入数字 back.out 弹一下；满格（剩余 0）后尝试输入时弹得更明显（atLimit=true）。
// 颜色分级（剩 ≤4 黄 / 剩 0 红）由 Blazor 按 class 驱动，这里只负责弹。
window.junigridJs.remarkCountBump = function (atLimit, sel) {
    var el = document.querySelector(sel || '.jg-remark-count');
    if (!el || !window.gsap) return;
    if (el.__bumpTl) el.__bumpTl.kill();
    el.__bumpTl = gsap.timeline()
        .fromTo(el, { scale: atLimit ? 1.5 : 1.22 },
            { scale: 1, duration: 0.45, ease: 'back.out(2.5)', clearProps: 'transform' });
};
// 空格过滤后把输入框 DOM 值同步回去（Blazor 重渲染前先纠正，避免空格闪现）
window.junigridJs.setRemarkValue = function (v) {
    var i = document.querySelector('.jg-remark-input');
    if (i) i.value = v;
};

// ------------------ 赞助弹窗换屏：高度弹簧（小窗长大窗） ------------------
// measure：换屏前记下当前高度。morph：换屏渲染后从旧高度弹到新高度，并在
// "弹簧起跑"这一刻才给新一屏加入场动画（--in，带 165ms 延迟 ≈ 弹簧到位时刻），
// 于是内容浮现时框一定够高，不会再出现"卡片还在长高、底部按钮被裁掉半截"。
window.junigridJs.sponsorMeasure = function () {
    var el = document.querySelector('.jg-sponsor-viewport');
    window.__jgSponsorH = el ? el.offsetHeight : 0;
};

window.junigridJs.sponsorMorph = function () {
    var el = document.querySelector('.jg-sponsor-viewport');
    if (!el) return;
    var pane = el.querySelector('.jg-sponsor-pane:not(.jg-sponsor-pane--leaving)');
    var leave = el.querySelector('.jg-sponsor-pane--leaving');
    var from = window.__jgSponsorH || 0;

    // 先把框钉在旧高度：等二维码解码的这几十毫秒里，卡片不能先跳到新高度再弹回来。
    if (from) {
        el.classList.add('jg-sponsor-morph');
        el.style.height = from + 'px';
    }

    var start = function () {
        if (!pane) return;
        var to = pane.offsetHeight;              // 新一屏的自然高度（父层高度被钉住也不影响它）
        window.__jgSponsorH = to;
        if (leave) leave.style.animation = 'jg-sponsor-pane-out 150ms ease forwards';
        // 入场动画与弹簧同一刻起跑；弹簧 165ms 时已到/过冲目标高度。
        // 用 inline style 而不是加类名：Blazor 重渲染会把它没渲染过的类名冲掉。
        pane.classList.remove('jg-sponsor-pane--waiting');
        pane.style.animation = 'jg-sponsor-pane-in 300ms cubic-bezier(0.22, 1, 0.36, 1) 165ms both';

        var finish = function () {
            el.classList.remove('jg-sponsor-morph');
            el.style.height = '';
            el.__jgMorph = null;
        };
        if (!from || !to || Math.abs(from - to) < 0.5 || !el.animate ||
            (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches)) {
            finish();
            return;
        }
        // 只取消上一次的高度动画：el.getAnimations() 会连子树一起返回，
        // 把入场/二维码切换的动画半路 cancel 掉（"滑一半突然贴到位"）。
        if (el.__jgMorph) { try { el.__jgMorph.cancel(); } catch (e) { } }
        // 长高用带过冲的弹簧，但尾巴只从上方逼近 1（原来结尾会掉到 0.993，那一下
        // 会让框比内容矮 1~2px，把按钮底边裁掉一丝）；收缩反过来用不出界的缓动，
        // 保证任何时刻框都不小于内容 —— 于是"内容完整"是几何上成立的，不靠时序。
        var SPRING = 'linear(0, 0.0603, 0.2033, 0.3821, 0.5633, 0.7258, 0.8584, 0.9574, 1.0242, 1.0634, 1.0809, 1.0828, 1.0747, 1.0613, 1.046, 1.0313, 1.0186, 1.0085, 1.0043, 1.0025, 1.0016, 1.001, 1.0007, 1.0005, 1.0003, 1.0002, 1.0001, 1.0001, 1)';
        var SMOOTH = 'cubic-bezier(0.22, 1, 0.36, 1)';
        var anim = el.animate(
            [{ height: from + 'px' }, { height: to + 'px' }],
            { duration: 520, easing: to > from ? SPRING : SMOOTH }
        );
        el.__jgMorph = anim;
        anim.onfinish = finish;
        anim.oncancel = finish;
    };

    // 二维码是异步解码的：不等它，量到的目标高度会偏小（矮框 → 内容被裁）。
    // 400ms 兜底，图片异常也不会把弹窗卡住。
    var waits = [].slice.call(el.querySelectorAll('img')).map(function (img) {
        if (img.complete && img.naturalWidth > 0) return Promise.resolve();
        if (img.decode) return img.decode().catch(function () { });
        return new Promise(function (res) {
            img.addEventListener('load', res, { once: true });
            img.addEventListener('error', res, { once: true });
        });
    });
    var guard = new Promise(function (res) { setTimeout(res, 400); });
    Promise.race([Promise.all(waits), guard]).then(function () {
        requestAnimationFrame(start);
    });
};

// ══════════════════════════════════════════════════════════════════
// v1.3.17：点击探针（诊断用）。挂在 document 的捕获阶段 —— 比任何
// stopPropagation 都早，所以"点击被某个层吃掉"也照样记得到。
// 与 MainLayout 的 @onclick 冒泡日志配对使用：
//   有 dom 无 blazor  ⇒ 断在 Blazor 电路（事件没送进 .NET）
//   两个都没有        ⇒ 点击根本没落到页面（窗口/输入层问题）
// ══════════════════════════════════════════════════════════════════
window.junigridJs = window.junigridJs || {};
window.junigridJs.clickProbe = function () {
    if (window.__jgClickProbeHooked) return;
    window.__jgClickProbeHooked = true;
    document.addEventListener('click', function (e) {
        try {
            var t = e.target;
            var d = t ? (t.tagName || '?').toLowerCase() : '?';
            if (t && t.id) d += '#' + t.id;
            if (t && typeof t.className === 'string' && t.className.trim()) {
                d += '.' + t.className.trim().split(/\s+/).slice(0, 3).join('.');
            }
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage('click:' + d);
            }
        } catch (err) { }
    }, true);
};

// ══════════════════════════════════════════════════════════════════
// v1.3.18：常驻「进行中」胶囊。
// 删除挪到后台之后，界面不再有任何"它还在跑"的痕迹 —— 用户只能等一个
// 不知道会不会来的 toast。这里补一个：转圈 + 文案，一直挂着，删除真的
// 结束（成功/失败都算）才收走。
// 视觉与 toast 同落点同配色（accent 底白字），只多一个 1.5s 一圈的圆环，
// 节奏照 motion.dev 那个 loading bar 例子取（linear、匀速、不停顿）。
// ══════════════════════════════════════════════════════════════════
(function () {
    var el = null;

    function drop(node) {
        if (!node) return;
        if (window.gsap) {
            gsap.to(node, {
                y: -10, opacity: 0, duration: 0.22, ease: 'power2.in',
                onComplete: function () { if (node.parentNode) node.parentNode.removeChild(node); }
            });
        } else if (node.parentNode) {
            node.parentNode.removeChild(node);
        }
    }

    window.junigridJs.busyPill = function (msg) {
        try {
            if (!el) {
                el = document.createElement('div');
                el.className = 'jg-toast-live jg-busy';
                el.setAttribute('role', 'status');
                el.setAttribute('aria-live', 'polite');

                var sp = document.createElement('span');
                sp.className = 'jg-busy-spin';

                var txt = document.createElement('span');
                txt.className = 'jg-busy-text';

                el.appendChild(sp);
                el.appendChild(txt);
                document.body.appendChild(el);
                shownAt = Date.now();

                if (window.gsap) {
                    gsap.fromTo(el, { y: 12, opacity: 0, scale: 0.96 },
                        { y: 0, opacity: 1, scale: 1, duration: 0.28, ease: 'back.out(2)' });
                }
            }
            var t = el.querySelector('.jg-busy-text');
            if (t) t.textContent = msg || '';
        } catch (e) { /* 胶囊失败不影响删除本身 */ }
    };

    var shownAt = 0;

    window.junigridJs.busyPillHide = function () {
        try {
            var node = el;
            if (!node) return;
            el = null;
            // 删除现在只搬目录、毫秒级就返回 —— 胶囊可能刚亮就被收走，闪一下比不亮更糟。
            // 至少让它站满 420ms，人眼才认得出"刚刚在做一件事，而且做完了"。
            var age = Date.now() - shownAt;
            var wait = Math.max(0, 420 - age);
            if (wait > 0) setTimeout(function () { drop(node); }, wait);
            else drop(node);
        } catch (e) { }
    };
})();
