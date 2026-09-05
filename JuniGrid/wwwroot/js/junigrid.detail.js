// ============================================================
// Mod 详情页：图片点击放大、手风琴动效、任务时间线变形、
// 下拉「点外部收回」兜底、返回顶部
// ============================================================
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
// ============ v1.1.3：任务时间线 —— 像素方块 → 对勾 变形（smooth-morph 风格）============
// Blazor 重渲染会把「上一行」的像素网格直接换成对勾 SVG；这里用 MutationObserver
// 抓住这一瞬间：先用一幅 3x3 像素残影盖住图标（Blazor 换下来的瞬间它还在视觉上），
// 让残影像素向中心聚拢消散，同时新对勾以 back.out 弹出 + 描边画入 —— 观感即平滑形变。
window.junigridJs.taskTimelineWatch = function (panelSel) {
    var panel = document.querySelector(panelSel);
    if (!panel || panel.__tlWatch) return;
    panel.__tlWatch = true;
    var hadGrid = new WeakMap();   // icon 元素 → 上一帧是像素方块

    function scan() {
        panel.querySelectorAll('.jg-tl-icon').forEach(function (ic) {
            var check = ic.querySelector('.jg-tl-check');
            if (check) {
                if (hadGrid.get(ic) && !check.__morphed) { check.__morphed = true; morph(ic, check); }
                hadGrid.delete(ic);
            } else if (ic.querySelector('.jg-pxgrid')) {
                hadGrid.set(ic, true);
            }
        });
    }

    function morph(icon, check) {
        if (!window.gsap) return;
        var row = icon.closest('.jg-tl-row');
        var color = (row && getComputedStyle(row).getPropertyValue('--c')) || '#1a9c5b';
        // ① 像素残影：盖在图标正上方的完整 3x3 方块，向中心聚拢消散
        var ghost = document.createElement('div');
        ghost.className = 'jg-pxgrid';
        ghost.style.cssText = 'position:absolute;left:50%;top:50%;width:max-content;transform:translate(-50%,-50%);pointer-events:none;filter:drop-shadow(0 0 4px ' + color + ');';
        var cells = [];
        for (var c = 0; c < 9; c++) {
            var sp = document.createElement('i');
            sp.style.cssText = 'opacity:1;transform:scale(1);animation:none;--c:' + color + ';';
            ghost.appendChild(sp); cells.push(sp);
        }
        icon.appendChild(ghost);
        var tl = gsap.timeline({ onComplete: function () { ghost.remove(); } });
        cells.forEach(function (sp, i) {
            tl.to(sp, {
                x: (1 - i % 3) * 4, y: (1 - Math.floor(i / 3)) * 4,
                scale: 0, opacity: 0, duration: .3, ease: 'power2.in'
            }, i * 0.018);
        });
        // ② 对勾弹出 + 描边画入（弧线先画、勾后画）
        gsap.fromTo(check, { scale: .2, opacity: 0, rotation: -30 },
            { scale: 1, opacity: 1, rotation: 0, duration: .45, ease: 'back.out(2.4)', clearProps: 'transform,opacity' });
        var shapes = check.querySelectorAll('path');
        shapes.forEach(function (p, i) {
            var len = p.getTotalLength ? p.getTotalLength() : 60;
            gsap.fromTo(p, { strokeDasharray: len, strokeDashoffset: len },
                { strokeDashoffset: 0, duration: .3, ease: 'power2.out', delay: .06 + i * .15 });
        });
    }

    var obs = new MutationObserver(function () { scan(); });
    obs.observe(panel, { childList: true, subtree: true });
    scan();
};


// ============ v1.1.2：下拉"点外部收回"全局兜底 ============
// 遮罩(.jg-dd-overlay)在某些场景可能拦不到真实点击（悬停元素提升层级、命中时序等），
// 这里在 document 上兜底：有打开的遮罩且点击落在所有下拉容器之外 → 主动点一下
// 打开着的遮罩，走它自己的 @onclick（CloseSort/CloseProfile/CloseAllDd）收回。
// 两类点击不干预：落在下拉容器（触发器+菜单）内的、落在遮罩自身的（后者已由
// 遮罩自己的 @onclick 处理；跳过还可避免合成点击再进本监听的递归）。
(function () {
    if (window.__ddOutsideBound) return;
    window.__ddOutsideBound = true;
    document.addEventListener('click', function (e) {
        var openOvs = document.querySelectorAll('.jg-dd-overlay.open');
        if (!openOvs.length) return;
        var t = e.target;
        if (!t || !t.closest) return;
        if (t.closest('.jg-sort-dd, .jg-profile-dd')) return;   // 点在下拉自身内
        if (t.closest('.jg-dd-overlay')) return;                // 点在遮罩上（它自己会收）
        openOvs.forEach(function (o) { o.click(); });
    });
})();

// ============ v1.1.2：返回顶部（左下角，丝滑滚动；Mod管理/Mod详情/Nexus 全系）============
// v1.1.3：支持上下拖动改位 —— 默认位置会压住列表最后一个 mod 封面，按住可拖到任意高度。
// 位置存 localStorage（记「距内容区底部的偏移」，窗口高度变化时按偏移重算并夹回屏内）。
// 挪动距离 <5px 仍算点击，不触发滚动回顶；真拖动过则吞掉随后的 click。
window.junigridJs.backTopInit = function () {
    var scroller = document.querySelector('.jg-main');
    var host = document.querySelector('.jg-content');
    var btn = document.getElementById('jgBackTop');
    if (!scroller || !btn || btn.__backTopBound) return;
    btn.__backTopBound = true;
    var POS_KEY = 'jg:backtop:bottom';
    // 夹取后应用距底偏移（top:auto 保持 bottom 定位）
    function applyBottom(b) {
        var h = host || document.body;
        var minB = 8;
        var maxB = Math.max(minB, h.clientHeight - btn.offsetHeight - 8);
        b = Math.min(Math.max(b, minB), maxB);
        btn.style.top = 'auto';
        btn.style.bottom = b + 'px';
        return b;
    }
    // 恢复上次拖动后的位置
    try {
        var saved = parseFloat(localStorage.getItem(POS_KEY));
        if (!isNaN(saved)) applyBottom(saved);
    } catch (e) { }
    // 窗口高度变化 → 按存的偏移重夹一次，别让按钮悬在屏外
    window.addEventListener('resize', function () {
        try {
            var v = parseFloat(localStorage.getItem(POS_KEY));
            if (!isNaN(v)) applyBottom(v);
        } catch (e) { }
    });
    function update() {
        var p = location.pathname || '/';
        // 只在 Mod 管理 / Mod 详情 / Nexus（含全部子视图）出现；且不在顶部才出现
        var ok = p === '/mods' || p.indexOf('/mod/') === 0 || p.indexOf('/nexus') === 0;
        btn.classList.toggle('show', ok && scroller.scrollTop > 260);
    }
    scroller.addEventListener('scroll', update, { passive: true });
    window.junigridJs.backTopRefresh = update;

    // ── 垂直拖动（pointer capture；CSS 里 .dragging 关过渡防跳变）──
    var dragging = false, moved = false, startY = 0, startB = 0;
    btn.addEventListener('pointerdown', function (e) {
        if (e.button !== 0) return;
        dragging = true; moved = false;
        startY = e.clientY;
        var r = btn.getBoundingClientRect();
        var hr = (host || document.body).getBoundingClientRect();
        startB = hr.bottom - r.bottom;   // 按钮当前等效 bottom 偏移
        try { btn.setPointerCapture(e.pointerId); } catch (err) { }
    });
    btn.addEventListener('pointermove', function (e) {
        if (!dragging) return;
        var dy = e.clientY - startY;
        if (!moved && Math.abs(dy) < 5) return;   // 微动不算拖，留给 click
        if (!moved) { moved = true; btn.classList.add('dragging'); }
        applyBottom(startB - dy);   // 上拖 dy<0 → bottom 增大
        e.preventDefault();
    });
    function endDrag(e) {
        if (!dragging) return;
        dragging = false;
        try { btn.releasePointerCapture(e.pointerId); } catch (err) { }
        if (!moved) return;
        btn.__jgDragged = true;   // 吞掉松手后的合成 click，别误触发回顶
        try { localStorage.setItem(POS_KEY, String(parseFloat(btn.style.bottom) || 0)); } catch (err) { }
        // 下一帧再去 dragging：同帧移除会立刻恢复过渡，transform 跳一下
        requestAnimationFrame(function () { btn.classList.remove('dragging'); });
    }
    btn.addEventListener('pointerup', endDrag);
    btn.addEventListener('pointercancel', endDrag);

    btn.addEventListener('click', function () {
        if (btn.__jgDragged) { btn.__jgDragged = false; return; }
        // GSAP 补间代理对象的 y → 每帧写回 scrollTop（丝滑滚到顶；
        // GSAP 对 DOM 元素不能直接补间 scrollTop 这种非样式属性）
        if (window.gsap) {
            var proxy = { y: scroller.scrollTop };
            gsap.to(proxy, {
                y: 0, duration: 0.6, ease: 'power2.inOut',
                onUpdate: function () { scroller.scrollTop = proxy.y; }
            });
        } else {
            scroller.scrollTo({ top: 0, behavior: 'smooth' });
        }
    });
    update();
};


// v0.71.1：等容器 scrollHeight 足够再设 scrollTop（图片未加载导致高度不够时不会被钳回顶部）
// v0.71.6：抗双重干扰 —— ①更新检测把列表 display:none（骨架屏期 scrollHeight 塌成 0）
// ②返回瞬间 playPageEnter 给 .jg-main 套了 transform 入场动画，transform 会让
