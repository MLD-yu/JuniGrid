// ─── 设置页开关：挤压 + 回弹 ────────────────────────────────────────────
// React Bits SquishSwitch 的手感移植。静止位置归 CSS（.jg-switch-row.on 的 translateX），
// JS 只在交互那一段覆盖 inline transform，动画跑完清掉交还 —— 所以 Blazor 什么时候重渲染
// 都不用通知这边，也不用每次渲染发一次 interop。
// 注意：这里刻意不用 setPointerCapture —— 它会把 click 的 target 改写成捕获元素，
// Blazor 的委托 @onclick（挂在整行上）就再也匹配不到了（版本列表那边已经踩过一次）。
// 也刻意没有悬停放大：滑块原地涨 3.5% 看着像"自己动了一下"，用户明确要求去掉，别加回来。
(function () {
    var MAX_STRETCH = 0.36;   // 挤压上限
    var V_REF = 220;          // 到这个速度(px/s)就吃满挤压；按 18px 行程标定的，不是参考里 76px 的 600
    var K = 170, C = 21.5, M = 0.9;   // 对齐 motion 的 spring(stiffness 170, damping 21.5, mass 0.9)
    var SLOP = 4;
    var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    var clamp = function (v, lo, hi) { return Math.min(hi, Math.max(lo, v)); };
    var rows = new Map();   // .jg-switch 元素 -> 状态
    // 宿主 = 承载 .on 状态、并且点击要落到它身上的那一层：
    // 设置页是整行（.jg-switch-row），Mod 列表是开关自己那颗按钮（.jg-squish-host）。
    var HOST_SEL = '.jg-switch-row, .jg-squish-host';
    function hostOf(sw) { return sw.closest(HOST_SEL); }
    function trackOf(host) { return host.querySelector('.jg-switch') || (host.classList.contains('jg-switch') ? host : null); }

    function stateOf(sw) {
        var st = rows.get(sw);
        if (!st) {
            var knob = sw.querySelector('.jg-switch-knob');
            if (!knob) return null;
            st = { sw: sw, knob: knob, row: hostOf(sw), x: 0, v: 0, flow: 0, target: 0, travel: 0, half: 0, raf: 0, drag: null, swallow: false };
            if (!st.row) return null;
            rows.set(sw, st);
        }
        return st;
    }
    // 行程从实测几何来，CSS 里只留一个未跑 JS 前的兜底值
    function measure(st) {
        var inset = st.knob.offsetLeft;
        var thumb = st.knob.offsetWidth;
        var w = st.sw.clientWidth;
        if (!inset || !thumb || !w) return;
        st.travel = Math.max(1, w - inset * 2 - thumb);
        st.half = thumb / 2;
        st.sw.style.setProperty('--sw-travel', st.travel + 'px');
    }
    function restX(st) { return st.row && st.row.classList.contains('on') ? st.travel : 0; }

    function paint(st) {
        // 端点挤不动、中段挤最多：速度峰值本来就在中段，物理上也对
        var room = Math.min(st.x + st.half, st.travel - st.x + st.half) / (st.half || 1);
        var sq = Math.min(1 + (st.flow / V_REF) * MAX_STRETCH * (reduce ? 0 : 1), 1 + room);
        var s = st.knob.style;
        // 只有弹簧/拖拽在跑的那段由 JS 拥有位移；落定后交还 CSS，否则状态从别处翻过来旋钮会钉住不动
        s.setProperty('--sw-x', st.x.toFixed(2) + 'px');
        s.setProperty('--sw-sx', sq.toFixed(4));
        s.setProperty('--sw-sy', (1 / sq).toFixed(4));
    }
    function tick(st, now) {
        var dt = Math.min(0.034, (now - st.last) / 1000 || 0.016);
        st.last = now;
        if (!st.drag) {
            var acc = (-K * (st.x - st.target) - C * st.v) / M;
            st.v += acc * dt;
            st.x += st.v * dt;
        }
        st.flow += (Math.abs(st.v) - st.flow) * Math.min(1, dt * 14);
        paint(st);
        if (!st.drag && Math.abs(st.x - st.target) < 0.08 && Math.abs(st.v) < 1.5 && st.flow < 0.6) {
            stop(st);
            st.x = st.target; st.flow = 0;
            st.knob.style.removeProperty('--sw-x');
            st.knob.style.removeProperty('--sw-sx');
            st.knob.style.removeProperty('--sw-sy');
            return;
        }
        st.raf = requestAnimationFrame(function (t) { tick(st, t); });
    }
    function start(st) { if (!st.raf) { st.last = performance.now(); st.raf = requestAnimationFrame(function (t) { tick(st, t); }); } }
    function stop(st) { if (st.raf) { cancelAnimationFrame(st.raf); st.raf = 0; } }

    function springTo(st, target) {
        measure(st);
        st.target = target;
        if (reduce) { st.x = target; st.v = 0; st.flow = 0; stop(st); st.knob.style.removeProperty('--sw-x'); return; }
        start(st);
    }
    // 提交一次切换：复用整行现成的 @onclick，不让 JS 直接跟 .NET 说话
    function commit(st) { if (st.row) st.row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window })); }

    document.addEventListener('pointerdown', function (e) {
        if (e.button !== 0) return;
        var row = e.target.closest(HOST_SEL);
        if (!row || row.classList.contains('disabled')) return;
        var sw = trackOf(row);
        var st = sw ? stateOf(sw) : null;
        if (!st) return;
        measure(st);
        // 按在整行任意处都给挤压；只有按在开关上才可以拖着走
        st.drag = { id: e.pointerId, x0: st.x || restX(st), startX: e.clientX, moved: false, draggable: sw.contains(e.target), committed: row.classList.contains('on') };
        st.x = st.drag.x0;
        st.v = 0;
        start(st);
    });
    document.addEventListener('pointermove', function (e) {
        rows.forEach(function (st) {
            var d = st.drag;
            if (!d || d.id !== e.pointerId || !d.draggable) return;
            var dx = e.clientX - d.startX;
            if (!d.moved && Math.abs(dx) > SLOP) d.moved = true;
            if (!d.moved) return;
            var nx = clamp(d.x0 + dx, 0, st.travel);
            var now = performance.now();
            st.v = (nx - st.x) / Math.max(0.001, (now - (d.t || now - 16)) / 1000);
            d.t = now;
            st.x = nx;
            var want = nx > st.travel / 2;
            if (want !== d.committed) { d.committed = want; st.target = want ? st.travel : 0; commit(st); }
        });
    });
    function endDrag(e, cancelled) {
        rows.forEach(function (st) {
            var d = st.drag;
            if (!d || d.id !== e.pointerId) return;
            st.drag = null;
            if (!d.moved) {
                // 只是点了一下：那一击照常冒到整行，由 @onclick 翻状态，这边补一段挤压动画
                st.swallow = false;
                springTo(st, d.committed ? 0 : st.travel);
                return;
            }
            st.swallow = true;   // 拖过就算数，浏览器随后自己那一下 click 不能再翻一次
            setTimeout(function () { st.swallow = false; }, 0);
            var to = cancelled ? (d.committed ? st.travel : 0) : (st.x > st.travel / 2 ? st.travel : 0);
            if ((to > st.travel / 2) !== d.committed) { d.committed = to > st.travel / 2; commit(st); }
            springTo(st, to);
        });
    }
    document.addEventListener('pointerup', function (e) { endDrag(e, false); });
    document.addEventListener('pointercancel', function (e) { endDrag(e, true); });
    document.addEventListener('click', function (e) {
        rows.forEach(function (st) {
            // 按行判，不按开关判：拖到开关外面松手，click 的 target 是行
            if (!st.swallow || !st.row.contains(e.target)) return;
            e.preventDefault();
            e.stopPropagation();
        });
    }, true);
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;
        rows.forEach(function (st) { if (st.drag) endDrag({ pointerId: st.drag.id }, true); });
    });

    window.junigridJs.squishSwitchInit = function () {
        document.querySelectorAll('.jg-switch').forEach(function (sw) { var st = stateOf(sw); if (st) measure(st); });
    };
})();
