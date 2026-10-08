// ============================================================
// 素材页复用的那一份动效：磁吸悬停 + 3D 倾斜 + 聚光灯坐标。
// 类名沿用立绘时代（.jg-pt-slot / .jg-pt-card / .jg-pt-tilt），
// 长按选季那套已随立绘页一起移除，这里不再带。
// ============================================================
(function () {
    window.junigridJs = window.junigridJs || {};

    /** 磁吸悬停 + 3D 倾斜 + 聚光灯/镭射膜坐标。
     *  只注册**一次**文档级委托：逐张卡 addEventListener 的话，列表一变（搜索/筛选/扫描完成）
     *  新出现的卡片就没绑定，只能靠每次渲染再打一次 JS —— 那正是切页卡顿的来源之一。 */
    window.junigridJs.ptMagnetic = function () {
        if (!window.gsap || window.__ptMagBound) return;
        window.__ptMagBound = true;
        var TILT = 12;            // 边缘处 ±6°（乘数是偏移量 ±0.5）
        if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
            TILT = 0;             // 减少动态偏好下不倾斜（彩膜在 CSS 里也已 display:none）
        }
        var memo = new WeakMap();     // slot → 这张卡的 quickTo 组（第一次悬停时才建）
        function motion(slot, el) {
            var m = memo.get(slot);
            if (m) return m;
            m = {
                qx: gsap.quickTo(el, 'x', { duration: 0.35, ease: 'power3' }),
                qy: gsap.quickTo(el, 'y', { duration: 0.35, ease: 'power3' }),
                // 倾斜也只能由 GSAP 写：卡片的 transform 整体归 GSAP 管，CSS 再写一条会被覆盖
                rx: gsap.quickTo(el, 'rotationX', { duration: 0.3, ease: 'power3' }),
                ry: gsap.quickTo(el, 'rotationY', { duration: 0.3, ease: 'power3' })
            };
            gsap.set(el, { transformPerspective: 700, transformStyle: 'preserve-3d' });
            memo.set(slot, m);
            return m;
        }
        document.addEventListener('pointermove', function (e) {
            var slot = e.target && e.target.closest && e.target.closest('.jg-pt-slot');
            if (!slot) return;
            // 槽由页面声明，卡是槽里唯一的子元素；骨架屏没有槽，天然被跳过
            var el = slot.firstElementChild;
            if (!el || !el.classList.contains('jg-pt-card')) return;
            if (el.classList.contains('locked') || el.classList.contains('busy')) return;
            var r = slot.getBoundingClientRect();     // 量的是不 transform 的槽 ⇒ 命中区恒定
            var nx = (e.clientX - r.left) / r.width - 0.5;
            var ny = (e.clientY - r.top) / r.height - 0.5;
            // 变换层是卡里的 .jg-pt-tilt，不是按钮本身：按钮一 transform，它的命中区就跟着挪走，
            // 边缘那几条像素的点击会落到槽上 ⇒ 挂在按钮上的 @onclick 收不到（「要点两遍才进」）。
            // 老 markup 没这层时退回按钮，至少动效还在。
            var kid = el.firstElementChild;
            var m = motion(slot, kid && kid.classList.contains('jg-pt-tilt') ? kid : el);
            slot.classList.add('hot');
            m.qx(nx * 6);
            m.qy(ny * 6);
            m.rx(-ny * TILT);
            m.ry(nx * TILT);
            el.style.setProperty('--mx', ((nx + 0.5) * 100) + '%');
            el.style.setProperty('--my', ((ny + 0.5) * 100) + '%');
        }, true);
        // pointerleave 不冒泡，但捕获阶段照样经过 document；只在离开的就是槽本身时收摊
        document.addEventListener('pointerleave', function (e) {
            var el = e.target;
            if (!el || !el.classList || !el.classList.contains('jg-pt-slot')) return;
            if (!el.classList.contains('hot')) return;
            el.classList.remove('hot');
            var m = memo.get(el);
            if (m) { m.qx(0); m.qy(0); m.rx(0); m.ry(0); }
        }, true);
    };
})();
