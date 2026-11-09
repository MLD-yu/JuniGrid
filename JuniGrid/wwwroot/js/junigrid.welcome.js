// ============================================================
// 新手引导（.jg-wg）—— 指示条滑块定位 + 步骤切换的高度/横向补间
// ------------------------------------------------------------
// 两件事 CSS 自己做不到，只能在 DOM 量完再由这里补：
//   ① 选中项背后那块「滑块」的落点 = 那一段的 offsetLeft/Width，
//      窗口尺寸一变就得重算（所以 JS 侧也监听 resize）；
//   ② 内容区高度随步骤变化，要先把旧高度写进去再补间到新高度，
//      否则面板是一下子弹过去的。
// 调用方：WelcomeGuide.razor 的 OnAfterRenderAsync（挂载一次 + 每次切步）。
// ============================================================
(function () {
    window.junigridJs = window.junigridJs || {};

    function q(sel) { return document.querySelector(sel); }

    function reduced() {
        return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
    }

    // 滑块：贴着当前那一段（含完整的 tile 尺寸与圆角）
    function placeGlider(animate) {
        var track = q('.jg-wg-track');
        var glider = q('.jg-wg-glider');
        if (!track || !glider) return;
        var segs = track.querySelectorAll('.jg-wg-seg');
        var idx = (parseInt(track.dataset.active || '1', 10) || 1) - 1;
        var seg = segs[idx] || segs[0];
        if (!seg) return;
        var spring = getComputedStyle(glider.parentNode).getPropertyValue('--jg-wg-spring').trim()
            || 'cubic-bezier(.22,1,.36,1)';
        glider.style.transition = (animate && !reduced())
            ? 'transform 520ms ' + spring + ', width 520ms ' + spring + ', height 520ms ' + spring
            : 'none';
        glider.style.transform = 'translate3d(' + seg.offsetLeft + 'px,' + seg.offsetTop + 'px,0)';
        glider.style.width = seg.offsetWidth + 'px';
        glider.style.height = seg.offsetHeight + 'px';
        glider.style.opacity = '1';
    }

    // 量一次内容高度并落到 view 上；返回本次高度
    function measure() {
        var view = q('.jg-wg-view');
        var pane = q('.jg-wg-pane');
        if (!view || !pane) return 0;
        var h = pane.offsetHeight;
        view.dataset.h = h;
        view.style.height = h + 'px';
        return h;
    }

    // 首次挂载：定高 + 摆滑块（不做动画，避免刚出来就抖一下）
    window.junigridJs.welcomeMount = function () {
        measure();
        placeGlider(false);
    };

    // 切步：从旧高度补间到新高度，内容从切换方向滑进来。
    // dir > 0 前进（从右来），dir < 0 后退（从左来）。
    window.junigridJs.welcomeStep = function (dir) {
        var view = q('.jg-wg-view');
        var pane = q('.jg-wg-pane');
        if (!view || !pane) return;
        var from = parseFloat(view.dataset.h || '0') || 0;
        var to = pane.offsetHeight;
        view.dataset.h = to;
        view.style.height = to + 'px';
        if (!reduced() && typeof view.animate === 'function') {
            if (from > 0 && Math.abs(to - from) > 0.5) {
                view.animate([{ height: from + 'px' }, { height: to + 'px' }],
                    { duration: 520, easing: 'cubic-bezier(.22,1,.36,1)' });
            }
            var shift = (dir >= 0 ? 1 : -1) * 26;
            pane.animate([
                { opacity: 0, transform: 'translateX(' + shift + 'px)' },
                { opacity: 0, offset: 0.18 },
                { opacity: 1, transform: 'none' }
            ], { duration: 360, easing: 'cubic-bezier(.22,1,.36,1)' });
        }
        placeGlider(true);
    };

    // 剪贴板兜底：C# 的 Clipboard.SetText 被别的程序占着时会抛，
    // 这时退回 DOM 选区 + execCommand（在点击手势内同步调用仍然有效）。
    window.junigridJs.copyText = function (text) {
        try {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.setAttribute('readonly', 'readonly');
            ta.style.position = 'fixed';
            ta.style.top = '-1000px';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            ta.setSelectionRange(0, ta.value.length);
            var ok = document.execCommand('copy');
            document.body.removeChild(ta);
            return ok;
        } catch (e) { return false; }
    };

    // 窗口宽高变化 → 正文换行数变 → 高度与滑块都得重算
    window.addEventListener('resize', function () {
        if (!q('.jg-wg-view')) return;
        measure();
        placeGlider(false);
    });
})();
