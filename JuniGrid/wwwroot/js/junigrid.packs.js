// ============================================================
// 素材包页专属：大图弹层的缩放与平移 + ESC 关闭弹窗
//   Ctrl + 滚轮  以光标为中心缩放（100% ~ 800%）
//   左键拖动     平移查看（任何缩放级别都可拖）
//   左键双击     恢复原始大小（右下角倍率胶囊点击同效）
//
// 为什么不糊了：缩放改用 CSS zoom 而不是 transform: scale()。
//   transform 缩放走的是合成器 —— 图层按 1x 栅格化后由 GPU 双线性放大，
//   image-rendering: pixelated 在合成阶段被忽略，于是越大越糊。
//   zoom 是布局级缩放，内容按新尺寸重新栅格化，最近邻逐像素生效。
//   实现上：canvas 只做视口 + 整数 translate（平移不产生缩放糊），
//   内部包一层 .jg-pk-lb-inner 承载 zoom；inner 宽度钉死为视口宽，
//   换行/居中布局在各倍率下与 100% 完全一致（与旧 transform 行为同形）。
//
// 设计要点：变换状态全部留在前端 DOM，不走 Blazor 往返 ——
//   mousemove/wheel 每次一帧一次 RPC 会卡成幻灯片（立绘页磁吸同款教训）。
// 生命周期：Blazor 每次重渲染都会调一次 pkZoomInit，靠 __pkz 标记幂等；
//   弹窗关闭时节点被 Blazor 销毁，监听器随之消亡，无需手动 dispose。
// ============================================================
(function () {
    window.junigridJs = window.junigridJs || {};

    var Z_MIN = 1, Z_MAX = 8, Z_STEP = 0.0016;

    window.junigridJs.pkZoomInit = function (sel) {
        var root = document.querySelector(sel || '.jg-pk-lb-body');
        if (!root || root.__pkz) return;
        var canvas = root.querySelector('.jg-pk-lb-canvas');
        if (!canvas) return;

        // inner 在 razor 标记里（Blazor 拥有 DOM，JS 只写它的 style ——
        // 把节点搬进自建容器会让 Blazor 差分找不到子节点）
        var inner = canvas.querySelector('.jg-pk-lb-inner');
        if (!inner) return;
        root.__pkz = true;

        var s = 1, x = 0, y = 0, dirty = false;

        // 倍率胶囊：只在放大时出现，点击回 100%
        var badge = document.createElement('button');
        badge.type = 'button';
        badge.className = 'jg-pk-lb-zoom';
        badge.title = '重置缩放';
        badge.style.display = 'none';
        root.appendChild(badge);

        function apply() {
            inner.style.zoom = s;
            // translate 取整：亚像素平移会让最近邻的硬边再糊一层
            var rx = Math.round(x), ry = Math.round(y);
            canvas.style.transform = 'translate(' + rx + 'px,' + ry + 'px)';
            if (s > 1.001) {
                badge.textContent = Math.round(s * 100) + '%';
                badge.style.display = 'block';
            } else {
                badge.style.display = 'none';
            }
        }

        // 量 inner 的固有（未缩放）尺寸：临时把 zoom 拨回 1 量完再拨回去，
        // 同步 JS 中间不会绘制，无闪烁。zoom 下 offsetHeight 的口径各版本不稳，不依赖它。
        function naturalSize() {
            var prev = inner.style.zoom;
            inner.style.zoom = 1;
            var w = inner.offsetWidth, h = inner.offsetHeight;
            inner.style.zoom = prev;
            return { w: w, h: h };
        }

        // 回 100% 并重新居中（inner 宽度钉视口宽，水平天然居中；垂直按内容高居中）
        function reset() {
            s = 1;
            dirty = false;
            inner.style.width = canvas.clientWidth + 'px';
            var n = naturalSize();
            x = 0;
            y = Math.round((canvas.clientHeight - n.h) / 2);
            apply();
        }
        badge.addEventListener('click', reset);

        // 缩略图后台就绪后 Blazor 会往 inner 里换图，内容高度会变 ——
        // 还没动手缩放/拖动过就跟着重新居中，动过手就不抢控制权
        if (window.ResizeObserver) {
            try {
                new ResizeObserver(function () { if (!dirty) reset(); }).observe(inner);
            } catch (_) { }
        }

        // 滚轮：以光标为不动点缩放（2026-10-07 他要求"缩放只用滚轮"，所以不再要 Ctrl；
        // 按住 Ctrl 滚同样走这里，行为不变）。屏幕坐标 = x + s·p（canvas 平移 + inner zoom，
        // 两者共用 canvas 左上角原点），保持光标下内容不动 ⇒ x' = mx − (mx−x)·(s'/s)。
        root.addEventListener('wheel', function (e) {
            e.preventDefault();
            e.stopPropagation();
            var r = root.getBoundingClientRect();
            var mx = e.clientX - r.left, my = e.clientY - r.top;
            var ns = Math.min(Z_MAX, Math.max(Z_MIN, s * Math.exp(-e.deltaY * Z_STEP)));
            if (ns === s) return;
            dirty = true;
            x = mx - (mx - x) * (ns / s);
            y = my - (my - y) * (ns / s);
            s = ns;
            // 缩回 100% 顺手回中 —— 不留"缩回去了但内容还偏在角落"的隐形状态
            if (s <= 1.001) { reset(); return; }
            apply();
        }, { passive: false });

        // 左键双击：恢复原始大小（与点倍率胶囊同效）
        canvas.addEventListener('dblclick', function (e) {
            if (e.button !== 0) return;
            e.preventDefault();
            reset();
        });

        // 左键拖动平移（pointer capture 保证拖出窗口也不丢）
        var dragging = false, lx = 0, ly = 0, moved = 0;
        canvas.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) return;
            dragging = true;
            moved = 0;
            lx = e.clientX; ly = e.clientY;
            try { canvas.setPointerCapture(e.pointerId); } catch (_) { }
            root.classList.add('dragging');
            e.preventDefault();
        });
        canvas.addEventListener('pointermove', function (e) {
            if (!dragging) return;
            x += e.clientX - lx;
            y += e.clientY - ly;
            moved += Math.abs(e.clientX - lx) + Math.abs(e.clientY - ly);
            if (moved > 3) dirty = true;
            lx = e.clientX; ly = e.clientY;
            apply();
        });
        function drop() {
            if (!dragging) return;
            dragging = false;
            root.classList.remove('dragging');
        }
        canvas.addEventListener('pointerup', drop);
        canvas.addEventListener('pointercancel', drop);

        reset();
    };

    // ── 分类 chips 单行 + 左右拖动（工具行）──
    // 文档级委托、一次性绑定：切「按包 / 按对象」时 chips 整批重渲染，绑在具体节点上会丢。
    // 只在真溢出时接管（scrollWidth > clientWidth），没溢出就完全不碰正常点击。
    // 拖过阈值的那一次点要掐掉：chip 是按钮，松手时 Blazor 的 @onclick 会当成一次选中。
    window.junigridJs.chipsDrag = function (sel) {
        if (window.__chipsDrag) return;
        window.__chipsDrag = true;
        var box = null, x0 = 0, left0 = 0, moved = 0;
        document.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) return;
            var b = e.target && e.target.closest ? e.target.closest(sel) : null;
            if (!b || b.scrollWidth <= b.clientWidth + 1) return;
            box = b; x0 = e.clientX; left0 = b.scrollLeft; moved = 0;
        });
        document.addEventListener('pointermove', function (e) {
            if (!box) return;
            var dx = e.clientX - x0;
            if (!moved && Math.abs(dx) <= 5) return;
            moved = Math.abs(dx);
            box.classList.add('dragging');
            box.scrollLeft = left0 - dx;
            e.preventDefault();
        });
        function end() {
            if (!box) return;
            var wasDrag = moved > 5;
            box.classList.remove('dragging');
            box = null; moved = 0;
            if (!wasDrag) return;
            var kill = function (ev) {
                ev.stopPropagation(); ev.preventDefault();
                document.removeEventListener('click', kill, true);
            };
            document.addEventListener('click', kill, true);
            setTimeout(function () { document.removeEventListener('click', kill, true); }, 350);
        }
        document.addEventListener('pointerup', end);
        document.addEventListener('pointercancel', end);
    };

    // ── ESC 关闭本页弹层（大图 → 对象弹窗 → 包详情 → 卡片菜单，逐层退）──
    // 与立绘页 portraitsEsc 同款：dotNetRef 暴露 OnEscapeClose()，重复调用先摘旧 handler。
    var escHandler = null;
    var escDotNet = null;
    window.junigridJs.packsEsc = function (dotNetRef) {
        if (escHandler) {
            document.removeEventListener('keydown', escHandler, true);
            escHandler = null;
        }
        escDotNet = dotNetRef;
        if (!dotNetRef) return;
        escHandler = function (e) {
            if (e.key !== 'Escape') return;
            // 输入框里的 Esc 交给输入框自己（清空/失焦），不关弹窗
            var t = e.target;
            if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) return;
            // 本页没有任何弹层（含卡片三点菜单）时不吞按键
            if (!document.querySelector('.jg-packs-page .jg-modal-overlay, .jg-packs-page .jg-dd-overlay.open')) return;
            e.preventDefault();
            e.stopPropagation();
            if (escDotNet) {
                try { escDotNet.invokeMethodAsync('OnEscapeClose'); } catch (err) { }
            }
        };
        document.addEventListener('keydown', escHandler, true);
    };
})();
