// ============================================================
// 通用 UI 交互：toast、光标倾斜、像素溶解（PixelSwap/启动按钮 hover）、
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
        // 启动中/运行中（disabled 或 .running/.launching）时，不做像素溶解
        function busy() {
            return btn.disabled || !!btn.closest('.jg-launch-row.running') || !!btn.closest('.jg-launch-row.launching');
        }
        function clearGrids() {
            btn.querySelectorAll('.px-grid').forEach(function (grid) {
                if (grid.__anims) grid.__anims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
                if (grid.parentNode) grid.parentNode.removeChild(grid);
            });
        }
        // 宽度过渡（关游戏/取消启动时 320ms 展回全宽）是否进行中
        function widthTransitioning() {
            var anims;
            try { anims = btn.getAnimations(); } catch (e) { return false; }
            for (var i = 0; i < anims.length; i++) {
                var a = anims[i];
                if (a && typeof CSSTransition !== 'undefined' && a instanceof CSSTransition
                    && a.transitionProperty === 'width' && a.playState === 'running') return true;
            }
            return false;
        }
        btn.addEventListener('mouseenter', function () {
            if (busy()) {
                // 非空闲（启动中/运行中）：即使此前残留了蒙版也一并清掉，保证显示原始按钮文案
                clearGrids();
                show = false;
                return;
            }
            if (show) return;
            show = true;   // 先占位，防快速 enter/leave 竞态重复铺
            // 拖一帧再铺：宽度过渡可能恰在本帧才开始（running/launching class 刚切换），
            // 当场量宽会缺一块；且宽度动画进行中 hover 完全无效果（产品要求）
            requestAnimationFrame(function () {
                if (!show || busy() || widthTransitioning() || !btn.matches(':hover')) { show = false; return; }
                if (!mask) mask = buildMask();
                window.junigridJs.pixelSwap(btn, mask, true);
            });
        });
        btn.addEventListener('mouseleave', function () {
            // 启动中/运行中：不做像素「收回」动画，直接复位并清掉残留，避免移开鼠标时闪出反转动效
            if (busy()) {
                show = false;
                clearGrids();
                return;
            }
            if (!show) return;
            show = false;
            // rAF 前就移开的话网格还没铺，无需收回动画
            if (!btn.querySelector('.px-grid')) return;
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

