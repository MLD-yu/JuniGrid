// ============================================================
// 任务中心悬浮窗（TaskDock）：光标倾斜、胶囊↔圆形 morph、下载信息下拉、拖动
// ============================================================
// ─── v1.06.7：任务悬浮窗光标透视倾斜（gsap cursor-driven-perspective-tilt demo 同款）───
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
        if (e.button !== 0) return;   // v1.1.4：只拖左键 —— 右键留给「隐藏悬浮窗」
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

