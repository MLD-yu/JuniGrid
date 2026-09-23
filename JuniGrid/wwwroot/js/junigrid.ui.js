// ============================================================
// 通用 UI 交互：toast、光标倾斜、
// scrollSpy、存档/头像/更新/作者气泡 tooltip、聚焦辅助
// ============================================================
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

        // v1.1.2：存档下拉同步外部遮罩（按 data-dd 键配对，见 dropdownToggle 内注释）——
        // 此前遮罩永远没有 .open，点外部收不掉（既有 bug）
        document.querySelectorAll('.jg-dd-overlay').forEach(function (o) {
            o.classList.toggle('open', !!open && o.dataset.dd === wrap.dataset.dd);
        });

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


// ── Motion characters-remaining 同款：备注弹窗剩余数字弹簧 ──
// 每次输入数字 back.out 弹一下；满格（剩余 0）后尝试输入时弹得更明显（atLimit=true）。
// 颜色分级（剩 ≤4 黄 / 剩 0 红）由 Blazor 按 class 驱动，这里只负责弹。
window.junigridJs.remarkCountBump = function (atLimit) {
    var el = document.querySelector('.jg-remark-count');
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
