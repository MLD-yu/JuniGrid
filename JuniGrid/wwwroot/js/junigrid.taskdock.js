// ============================================================
// 任务中心悬浮窗（TaskDock）：光标倾斜、胶囊↔圆形 morph、下载信息下拉、拖动
// ============================================================
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
        // v1.2.x：Motion accordion 同款手感 —— 干脆的弹簧高度展开，行只轻淡入（原 elastic 大幅弹跳与横移已去）
        // 动画结束后清掉内联样式，稳态显示交给 CSS .open（Blazor 重渲染不丢状态）
        panel.__tl = gsap.timeline({
            onComplete: function () {
                panel.style.height = '';
                panel.style.visibility = '';
                panel.style.opacity = '';
                panel.__tl = null;
            }
        })
            .to(arrow, { rotation: 180, duration: 0.45, ease: 'back.out(1.6)', easeReverse: 'power2.inOut' }, 0)
            .fromTo(panel,
                { height: 0, autoAlpha: 0 },
                { height: 'auto', autoAlpha: 1, duration: 0.5, ease: 'back.out(1.2)', easeReverse: 'power3.out' }, 0)
            .from(panel.querySelectorAll('.jg-taskdrop-item'), {
                opacity: 0, duration: 0.25,
                ease: 'power1.out', easeReverse: 'power2.out', stagger: 0.04
            }, 0.1);
    } else {
        panel.__tl = gsap.timeline({
            onComplete: function () {
                panel.classList.remove('open');
                panel.style.height = '';
                panel.style.visibility = '';
                panel.style.opacity = '';
            }
        })
            .to(arrow, { rotation: 0, duration: 0.3, ease: 'power2.inOut' }, 0)
            // immediateRender：收起这支用的是 to()，GSAP 默认到下一帧才写起始值。
            // 调用方（Blazor）会在同一批里把 .open 摘掉，面板 CSS 的 height:0 就抢在动画之前
            // 把它拍平（实测 12ms 拍平 vs 21ms 才挂内联样式），于是 0→0 空播、看着就是"啪"地跳。
            // 立刻把当前高度写成内联，class 再怎么翻都压不住它。展开那支的 fromTo 本来就有这个语义。
            .to(panel, { height: 0, autoAlpha: 0, duration: 0.32, ease: 'power2.in', immediateRender: true }, 0);
    }
};

// ─── v1.3.49：分类段手风琴（与 taskDrop 同款弹簧，但稳态类用 .collapsed）───
// 段头点击后 Blazor 会翻 .collapsed，先在这里用 GSAP 播 height 0↔auto 的过渡；
// 与 taskDrop 同样的时序约束：收起时 immediateRender 先挂内联高度，避免 CSS 的 height:0 抢跑。
// v1.3.84：入参从「选择器」改成「元素 id」，并且用 getElementById 取元素 ——
// 段体 id 由分类名映射而来（可能是中文/空格/引号），拼进 querySelector 会当选择器语法解析；
// 更要紧的是 id 撞车时 querySelector 只会拿到第一个，点上空分类却折叠了别人那一段。
junigridJs.catToggle = function (panelId, open) {
    var panel = panelId ? document.getElementById(panelId) : null;
    if (!panel) return;
    if (panel.__catTl) { panel.__catTl.kill(); panel.__catTl = null; }
    if (!window.gsap) {
        panel.style.visibility = open ? 'visible' : 'hidden';
        panel.style.opacity = open ? '1' : '0';
        panel.style.height = open ? 'auto' : '0px';
        return;
    }
    if (open) {
        panel.classList.remove('collapsed');
        panel.__catTl = gsap.timeline({
            onComplete: function () {
                panel.style.height = '';
                panel.style.visibility = '';
                panel.style.opacity = '';
                panel.__catTl = null;
            }
        })
            .fromTo(panel,
                { height: 0, autoAlpha: 0 },
                { height: 'auto', autoAlpha: 1, duration: 0.5, ease: 'back.out(1.2)', easeReverse: 'power3.out' }, 0);
    } else {
        panel.__catTl = gsap.timeline({
            onComplete: function () {
                panel.classList.add('collapsed');
                panel.style.height = '';
                panel.style.visibility = '';
                panel.style.opacity = '';
                panel.__catTl = null;
            }
        })
            .to(panel, { height: 0, autoAlpha: 0, duration: 0.32, ease: 'power2.in', immediateRender: true }, 0);
    }
};

// ─── v1.3.86：图标选择器的懒加载 ───
// 物品有 600 多件，一次全铺出来会卡，所以 C# 那边只画前 N 个。
// 滚到快见底就回调 .NET 续一批 —— 用户不用点「显示更多」了。
// 元素是对话框里的 .jg-icon-grid（每次开对话框都是新元素，标记跟着元素走，不用手动解绑）。
junigridJs.iconGridInfinite = function (dotNetRef) {
    var grid = document.querySelector('.jg-icon-grid');
    // 没网格（还在加载态显示跳动圆点）就返回 false —— 调用方据此**不要**标记「已挂」，
    // 否则等网格真渲染出来时那个标记已经是 true，监听永远挂不上，滚到底也不加载更多。
    if (!grid) return false;
    grid.__jgDotNet = dotNetRef;
    if (grid.__jgBound) return true;
    grid.__jgBound = true;
    grid.addEventListener('scroll', function () {
        // 离底 200px 就续 —— 等真正到底再加载，用户会看到一闪而过的空白
        if (grid.scrollTop + grid.clientHeight < grid.scrollHeight - 200) return;
        var ref = grid.__jgDotNet;
        if (!ref) return;
        ref.invokeMethodAsync('LoadMoreIcons').catch(function () { /* 对话框已关 */ });
    }, { passive: true });
    return true;
};

// ------------------ 2026-11-12：图标选择器的页签滑块 + 切换变形 ------------------
// 与新手引导（junigrid.welcome.js 的 welcomeMount / welcomeStep）同一套手法：
//   ① 选中项背后一块滑块贴着那一项定位，切换时滑过去；
//   ② 内容区高度从旧值补到新值、内容从切换方向滑进来。
// 不这么做的话，从「默认」（只有一件）切到「物品」（几百件）高度是猛跳的，很突兀。
// 关键点同引导窗：Blazor 已经把新 DOM 换上来了，此刻量到的就是**新**高度 ——
// 旧高度只能提前记在 body.dataset.h 上，补间才有起点。
function jgIconReduced() {
    return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
}

function jgPlaceIconGlider(animate) {
    var tabs = document.querySelector('.jg-icon-tabs');
    var glider = tabs && tabs.querySelector('.jg-icon-glider');
    if (!tabs || !glider) return;
    var segs = tabs.querySelectorAll('.jg-icon-tab');
    var idx = parseInt(tabs.dataset.active || '0', 10) || 0;
    var seg = segs[idx] || segs[0];
    if (!seg) return;
    var ease = 'cubic-bezier(.4,0,.2,1)';   // 与 React Bits 那个 tabs 示例同一套缓动
    glider.style.transition = (animate && !jgIconReduced())
        ? 'transform 300ms ' + ease + ', width 300ms ' + ease
        : 'none';
    // 下划线贴当前页签的底边（2026-11-12 由「胶囊滑块」改成下划线，见 mods.css 注释）
    glider.style.transform = 'translate3d(' + seg.offsetLeft + 'px,' + (seg.offsetTop + seg.offsetHeight) + 'px,0)';
    glider.style.width = seg.offsetWidth + 'px';
    glider.style.height = '2px';
    glider.style.opacity = '1';
}

// 对话框刚打开：记下当前高度 + 无动画摆好滑块（刚出来就animate会抖一下）
junigridJs.iconMount = function () {
    var body = document.querySelector('.jg-icon-body');
    if (body) body.dataset.h = body.offsetHeight;
    jgPlaceIconGlider(false);
};

// 切页签：只做「容器高度补到新高度 + 下划线滑过去」。
// 2026-11-12：内容那一层淡出/滑入按用户要求去掉了（看着不像想要的效果）——
// 现在内容直接换，但高度是补间的，所以整体还是顺滑的，不是硬跳。
junigridJs.iconTabSwitch = function (dir) {
    var body = document.querySelector('.jg-icon-body');
    var inner = body && body.firstElementChild;
    if (!body || !inner) return;
    var from = parseFloat(body.dataset.h || '0') || 0;
    var to = inner.offsetHeight;
    body.dataset.h = to;
    if (!jgIconReduced() && from > 0 && Math.abs(to - from) > 0.5) {
        body.animate([{ height: from + 'px' }, { height: to + 'px' }],
            { duration: 300, easing: 'cubic-bezier(.4,0,.2,1)' });
    }
    jgPlaceIconGlider(true);
};

// ------------------ 2026-11-12：自定义分类图标（选图 → 方形裁剪） ------------------
// 背景：分类图标格是正方的，而用户传的图比例千奇百怪。直接 object-fit 要么留白要么切掉
// 关键部分，所以先让用户自己「框」出方形：拖动平移 + 滚轮/滑块缩放，方框内即所得。
// 裁剪在浏览器 canvas 里做（不引图片库、也不把几 MB 原图跨 interop 传两遍）：
// 导出 256×256 PNG dataURL → 交给 SaveCustomIcon 落盘。
var jgCrop = null;      // 待裁的图：{ url, name }
var jgCropSt = null;    // 舞台状态：{ stage, img, slider, nw, nh, base, zoom, x, y }

function jgCropClampZoom(z) { return Math.min(4, Math.max(1, z)); }

// 把状态写回 DOM。平移要钳住：图必须始终盖满方框，露出底色就是「裁到外面去了」。
function jgCropApply() {
    var s = jgCropSt;
    if (!s || !s.nw) return;
    var box = s.stage.clientWidth;
    var w = s.nw * s.base * s.zoom;
    var h = s.nh * s.base * s.zoom;
    s.x = Math.min(0, Math.max(box - w, s.x));
    s.y = Math.min(0, Math.max(box - h, s.y));
    s.img.style.width = w + 'px';
    s.img.style.height = h + 'px';
    s.img.style.transform = 'translate3d(' + s.x + 'px,' + s.y + 'px,0)';
}

// 以方框中心为锚点缩放（滑块和滚轮都走这里）—— 按左上角缩放的话放大时内容会往右下跑
function jgCropSetZoom(z) {
    var s = jgCropSt;
    if (!s || !s.nw) return;
    var box = s.stage.clientWidth;
    var old = s.zoom;
    var nz = jgCropClampZoom(z);
    if (Math.abs(nz - old) < 0.0001) return;
    var cx = (box / 2 - s.x) / (s.nw * s.base * old);
    var cy = (box / 2 - s.y) / (s.nh * s.base * old);
    s.zoom = nz;
    s.x = box / 2 - cx * s.nw * s.base * nz;
    s.y = box / 2 - cy * s.nh * s.base * nz;
    jgCropApply();
}

function jgCropBind(stage) {
    if (stage.__jgCropBound) return;
    stage.__jgCropBound = true;
    var drag = null;
    stage.addEventListener('pointerdown', function (e) {
        if (!jgCropSt || e.button !== 0) return;
        drag = { cx: e.clientX, cy: e.clientY, ox: jgCropSt.x, oy: jgCropSt.y };
        try { stage.setPointerCapture(e.pointerId); } catch (_) { }
        e.preventDefault();
    });
    stage.addEventListener('pointermove', function (e) {
        if (!drag || !jgCropSt) return;
        jgCropSt.x = drag.ox + (e.clientX - drag.cx);
        jgCropSt.y = drag.oy + (e.clientY - drag.cy);
        jgCropApply();
    });
    var end = function (e) {
        drag = null;
        try { stage.releasePointerCapture(e.pointerId); } catch (_) { }
    };
    stage.addEventListener('pointerup', end);
    stage.addEventListener('pointercancel', end);
    stage.addEventListener('wheel', function (e) {
        if (!jgCropSt) return;
        e.preventDefault();
        jgCropSetZoom(jgCropSt.zoom * (e.deltaY < 0 ? 1.08 : 1 / 1.08));
    }, { passive: false });
}

function jgCropDispose() {
    if (jgCrop && jgCrop.url) { try { URL.revokeObjectURL(jgCrop.url); } catch (_) { } }
    jgCrop = null;
    jgCropSt = null;
    // 滑块状态也要清：裁剪层是 @if 渲染的，DOM 一销毁，残留的 rAF 会去碰不存在的柱子
    if (jgWake && jgWake.raf) { try { cancelAnimationFrame(jgWake.raf); } catch (_) { } }
    jgWake = null;
}

// 弹系统选图框。只把 blob URL 留在 JS 里，Blazor 侧只拿文件名（见 C# PickCustomIconFile）。
junigridJs.iconPickFile = function () {
    return new Promise(function (resolve) {
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = 'image/*';
        input.style.display = 'none';
        document.body.appendChild(input);
        var done = false;
        function finish(name) {
            if (done) return;
            done = true;
            try { input.remove(); } catch (_) { }
            resolve(name || null);
        }
        input.addEventListener('change', function () {
            var f = input.files && input.files[0];
            if (!f) { finish(null); return; }
            jgCropDispose();
            jgCrop = { url: URL.createObjectURL(f), name: (f.name || '').replace(/\.[^.]+$/, '') };
            finish(jgCrop.name);
        });
        // Chromium 支持 cancel：用户点了「取消」也要把 Promise 收掉，否则 C# 一直挂着
        input.addEventListener('cancel', function () { finish(null); });
        input.click();
    });
};

// 裁剪层出现后挂图。返回 Promise<boolean>：图读不出来（格式不支持/文件坏了）时 false。
junigridJs.iconCropMount = function () {
    return new Promise(function (resolve) {
        var stage = document.querySelector('.jg-crop-stage');
        var img = document.getElementById('jgCropImg');
        if (!jgCrop || !stage || !img) { resolve(false); return; }
        jgCropSt = {
            stage: stage, img: img,
            nw: 0, nh: 0, base: 1, zoom: 1, x: 0, y: 0
        };
        jgCropBind(stage);
        img.onload = function () {
            var s = jgCropSt;
            if (!s) { resolve(false); return; }
            s.nw = img.naturalWidth || 1;
            s.nh = img.naturalHeight || 1;
            var box = stage.clientWidth || 1;
            // 铺满方框的最小比例（cover）：短边先贴边，另一头可拖
            s.base = Math.max(box / s.nw, box / s.nh);
            s.zoom = 1;
            s.x = (box - s.nw * s.base) / 2;
            s.y = (box - s.nh * s.base) / 2;
            jgCropApply();
            resolve(true);
        };
        img.onerror = function () { resolve(false); };
        img.src = jgCrop.url;
        junigridJs.wakeMount();   // 缩放滑块：等裁剪层这一帧渲染完再挂
    });
};

junigridJs.iconCropZoom = function (v) {
    jgCropSetZoom(parseFloat(v) || 1);
    if (jgCropSt) jgWakeSet(jgCropSt.zoom);   // 外部改值（滑块只管界面），把滑块也带过去
};
junigridJs.iconCropZoomBy = function (f) {
    if (!jgCropSt) return;
    jgCropSetZoom(jgCropSt.zoom * (parseFloat(f) || 1));
    jgWakeSet(jgCropSt.zoom);
};
junigridJs.iconCropReset = function () {
    var s = jgCropSt;
    if (!s || !s.nw) return;
    var box = s.stage.clientWidth;
    s.zoom = 1;
    s.x = (box - s.nw * s.base) / 2;
    s.y = (box - s.nh * s.base) / 2;
    jgCropApply();
    jgWakeSet(1);
};

// ------------------ 缩放滑块：React Bits WakeSlider 的 vanilla 版 ------------------
// 原版是 React + motion；这里只搬它的视觉与手感：一排柱子，值以左的那段点亮，
// 手一快，柱子按余弦钟形隆起一道「尾迹」，松手后慢慢回落。
// 值域就是裁剪的缩放倍数（1~4 倍），所以拖动它等于直接缩放方框里的图。
var jgWake = null;

function jgWakeValueToHead(w) {
    var pct = (w.value - w.min) / (w.max - w.min || 1);
    return Math.max(0, Math.min(1, pct)) * (w.bars.length - 1);
}
function jgWakeHeadToValue(w, head) {
    var pct = w.bars.length > 1 ? head / (w.bars.length - 1) : 0;
    return w.min + Math.max(0, Math.min(1, pct)) * (w.max - w.min);
}

function jgWakePaint(force) {
    var w = jgWake;
    if (!w || !w.bars.length) return;
    var n = w.bars.length;
    var head = w.head;
    var amp = w.amp;
    var lit = Math.round(head);
    // 尾迹半宽：随手速变宽，静止时收成很窄的一条
    var reach = 2 + 4 * amp;
    for (var i = 0; i < n; i++) {
        var el = w.bars[i];
        var on = i <= lit ? 'true' : 'false';
        if (el.dataset.on !== on) el.dataset.on = on;
        if (!force && amp < 0.002 && el.__lift === 0) continue;
        var d = i - head;
        var lift = Math.abs(d) < reach ? amp * Math.pow(Math.cos((Math.PI * d) / (2 * reach)), 2) : 0;
        el.__lift = lift;
        el.style.transform = 'scaleY(' + (w.rest + lift * (1 - w.rest)) + ')';
    }
    if (w.handle) w.handle.setAttribute('aria-valuenow', w.value.toFixed(2));
}

function jgWakeTick() {
    var w = jgWake;
    if (!w) return;
    var targetHead = jgWakeValueToHead(w);
    // 头部用简单弹簧逼近目标（原版 shape 是 motion 的 useSpring，glide=0.3s）
    var k = 0.22, damp = 0.7;
    w.vel = (w.vel || 0) * damp + (targetHead - w.head) * k;
    var prev = w.head;
    w.head += w.vel;
    if (Math.abs(w.head - targetHead) < 0.01 && Math.abs(w.vel) < 0.01) {
        w.head = targetHead;
        w.vel = 0;
    }
    // 速度 → 振幅（原版 FULL_SPEED=320、smoothstep 映射）
    var speed = Math.abs(w.head - prev);
    var norm = Math.min(1, speed / 0.9);
    var target = norm * norm * (3 - 2 * norm);
    w.amp += (target - w.amp) * 0.25;
    if (w.amp < 0.002) w.amp = 0;
    jgWakePaint(false);
    var busy = w.dragging || Math.abs(w.head - targetHead) > 0.01 || w.amp > 0;
    w.raf = busy ? requestAnimationFrame(jgWakeTick) : 0;
}

function jgWakeKick() { var w = jgWake; if (w && !w.raf) w.raf = requestAnimationFrame(jgWakeTick); }

// 外部（±/重置 按钮）改值：直接落位，不播尾迹
function jgWakeSet(v) {
    var w = jgWake;
    if (!w) return;
    w.value = Math.max(w.min, Math.min(w.max, v));
    w.head = jgWakeValueToHead(w);
    w.vel = 0; w.amp = 0;
    jgWakePaint(true);
}

function jgWakeFromX(clientX) {
    var w = jgWake;
    if (!w) return;
    var r = w.track.getBoundingClientRect();
    if (!r.width) return;
    var pct = Math.max(0, Math.min(1, (clientX - r.left) / r.width));
    var v = w.min + pct * (w.max - w.min);
    v = Math.round(v * 100) / 100;
    if (v === w.value) return;
    w.value = v;
    jgCropSetZoom(v);      // 拖动滑块 = 直接缩放裁剪框（应用里真在用），滑块只做「界面」
    jgWakeKick();
}

function jgWakeBind(root) {
    var w = jgWake;
    w.track = root.querySelector('.jg-wake-track');
    w.handle = root.querySelector('.jg-wake-handle');
    if (!w.track || w.track.__jgWakeBound) return;
    w.track.__jgWakeBound = true;

    w.track.addEventListener('pointerdown', function (e) {
        if (e.button !== 0) return;
        w.dragging = true;
        try { w.track.setPointerCapture(e.pointerId); } catch (_) { }
        w.track.classList.add('drag');
        jgWakeFromX(e.clientX);
        jgWakeKick();
        e.preventDefault();
    });
    w.track.addEventListener('pointermove', function (e) {
        if (w.dragging) jgWakeFromX(e.clientX);
    });
    var end = function (e) {
        w.dragging = false;
        w.track.classList.remove('drag');
        try { w.track.releasePointerCapture(e.pointerId); } catch (_) { }
        jgWakeKick();
    };
    w.track.addEventListener('pointerup', end);
    w.track.addEventListener('pointercancel', end);

    if (w.handle) {
        w.handle.addEventListener('keydown', function (e) {
            var step = e.shiftKey ? 0.5 : 0.1;
            var v = w.value;
            if (e.key === 'ArrowRight' || e.key === 'ArrowUp') v += step;
            else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') v -= step;
            else if (e.key === 'Home') v = w.min;
            else if (e.key === 'End') v = w.max;
            else return;
            e.preventDefault();
            v = Math.max(w.min, Math.min(w.max, Math.round(v * 100) / 100));
            if (v === w.value) return;
            w.value = v;
            jgCropSetZoom(v);
            jgWakeKick();
        });
    }
}

junigridJs.wakeMount = function () {
    var root = document.getElementById('jgCropWake');
    if (!root) { jgWake = null; return false; }
    var bars = root.querySelectorAll('.jg-wake-bar');
    if (!bars.length) { jgWake = null; return false; }
    // 静止时柱高比例（原版 restHeight/height）
    var h = parseFloat(getComputedStyle(root).getPropertyValue('--jw-height')) || 40;
    var restH = parseFloat(getComputedStyle(root).getPropertyValue('--jw-rest')) || 10;
    jgWake = {
        root: root, bars: bars, track: null, handle: null,
        min: parseFloat(root.dataset.min) || 1,
        max: parseFloat(root.dataset.max) || 4,
        value: jgCropSt ? jgCropSt.zoom : (parseFloat(root.dataset.min) || 1),
        head: 0, vel: 0, amp: 0, raf: 0, dragging: false,
        rest: Math.max(0.12, Math.min(1, restH / h))
    };
    jgWakeBind(root);
    jgWakeSet(jgWake.value);
    return true;
};

junigridJs.wakeSet = function (v) { jgWakeSet(parseFloat(v) || 1); };


// 方框里的部分 → 方形画布 → PNG dataURL（尺寸由 C# 侧固定 256）
junigridJs.iconCropExport = function (size) {
    var s = jgCropSt;
    if (!s || !s.nw) return null;
    var box = s.stage.clientWidth || 1;
    var out = size || 256;
    var k = out / box;
    var c = document.createElement('canvas');
    c.width = c.height = out;
    var ctx = c.getContext('2d');
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(s.img, s.x * k, s.y * k, s.nw * s.base * s.zoom * k, s.nh * s.base * s.zoom * k);
    return c.toDataURL('image/png');
};

junigridJs.iconCropSave = function (ref) {
    var url = junigridJs.iconCropExport(256);
    if (!url) return Promise.resolve(false);
    var name = (jgCrop && jgCrop.name) || '';
    return ref.invokeMethodAsync('SaveCustomIcon', url, name).then(function (spec) {
        if (!spec) return false;
        jgCropDispose();
        return true;
    }).catch(function () { return false; });
};

junigridJs.iconCropClose = function () { jgCropDispose(); };

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

