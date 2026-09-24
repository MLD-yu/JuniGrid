// ============================================================
// 肖像页专属：长按选季 / ESC 关弹窗 / GSAP 进场与磁吸 / 锁定动效
// 设计语言：VueBits·ReactBits 弹簧物理 + 聚光灯卡片 + 进场 stagger
// ============================================================
(function () {
    window.junigridJs = window.junigridJs || {};

    var LP_MS = 900;                 // 长按阈值（进度条时长与判定共用这一个数）
    var TAP_MS = 250;                // 按下不到这么久就算「单击」：不画进度条、click 照常送给 Blazor
    var PRESS_SCALE = 1.08;          // 按住时肖像放大
    var HIT_PAD = 10;                // 指针离开卡片多远算取消（同 HoldButton）
    var lpState = { el: null, timer: null, showTimer: null, bar: null, rect: null, id: null, t0: 0 };
    var escHandler = null;
    var dotNet = { onLongPress: null, onEsc: null };

    // 长按成立后，「欠一次吞点击」必须记到玩家真正松手为止 —— click 是 pointerup 之后才派发的，
    // 按住不放几秒再松手也一样。之前用固定 350ms 解除，晚松手的那次 click 就漏给 Blazor 的
    // @onclick（OnSkinClick = 全季节换肤），表现成"季节固定完了，又顺手换了整套皮肤"。
    // 按 skin id 记（不是按 DOM 节点）：赋值后 Blazor 重渲染可能换掉节点，节点上的标记会丢。
    var swallowArmed = {};
    var pendingSwallowId = null;
    function disarmPendingSwallow() {
        if (!pendingSwallowId) return;
        var id = pendingSwallowId;
        pendingSwallowId = null;
        setTimeout(function () { delete swallowArmed[id]; }, 350);   // 留余量等紧随其后的 click
    }

    // ── 长按进度条：横扫填充 + 前缘波浪（移植 ReactBits HoldButton 的 fill/crest 两层）──
    // JS 只写一个进度量 --lp-p，宽度/波浪位置全在 CSS 里算；几何量一次测完写进变量，
    // 不引 ResizeObserver —— 皮肤卡是固定 92px，缩放(transform)也不改 offsetWidth。
    function ensureBar(card) {
        var bar = card.querySelector('.jg-pt-lp-bar');
        if (bar) return bar;
        bar = document.createElement('div');
        bar.className = 'jg-pt-lp-bar';
        bar.innerHTML = '<i class="jg-pt-lp-fill"></i><i class="jg-pt-lp-crest"></i>';
        card.appendChild(bar);
        bar.style.setProperty('--lp-w', bar.offsetWidth + 'px');
        bar.style.setProperty('--lp-h', bar.offsetHeight + 'px');
        bar.style.setProperty('--lp-cycles', (LP_MS / 1100).toFixed(3));
        bar.style.setProperty('--lp-ms', LP_MS + 'ms');
        return bar;
    }

    function barRun(bar) {
        if (!bar) return;
        bar.classList.add('on');
        // 进度条是在 TAP_MS 之后才开始画的，此刻时间已经走掉一段 —— 从应有的进度接着走，
        // 保证「条满」和「长按判定」仍然是同一瞬间。
        var from = TAP_MS / LP_MS;
        if (window.gsap) {
            gsap.killTweensOf(bar);
            gsap.fromTo(bar, { '--lp-p': from }, {
                '--lp-p': 1, duration: (LP_MS - TAP_MS) / 1000, ease: 'none', overwrite: true
            });
        } else {
            bar.style.setProperty('--lp-p', String(from));
        }
    }

    function barStop(bar) {
        if (!bar) return;
        if (window.gsap) gsap.killTweensOf(bar);
        bar.classList.remove('on');
        bar.style.setProperty('--lp-p', '0');
    }

    // 长按要吞掉紧随其后的那次 click（Blazor 的委托 @onclick 挂在它上面做全局换肤）。
    // untilPointerup=true：长按成立时玩家还按着，click 要等松手才派发 ⇒ 一直挂着，由下一次
    //   pointerup 解除（解除后再留 350ms 接住它）。
    // untilPointerup=false：取消的长按，松手已经发生、click 马上就到 ⇒ 只等 350ms 就自过期，
    //   不然会把下一次正常单击也吞掉。
    function armSwallow(id, untilPointerup) {
        if (!id) return;
        swallowArmed[id] = true;
        if (untilPointerup) {
            pendingSwallowId = id;
        } else {
            setTimeout(function () { delete swallowArmed[id]; }, 350);
        }
    }

    function lpCancel() {
        if (lpState.timer) { clearTimeout(lpState.timer); lpState.timer = null; }
        if (lpState.showTimer) { clearTimeout(lpState.showTimer); lpState.showTimer = null; }
        if (lpState.el) {
            lpState.el.classList.remove('jg-pt-lp-press');
            barStop(lpState.bar);
            if (window.gsap) gsap.to(lpState.el, { scale: 1, duration: 0.2, ease: 'power2.out', overwrite: 'auto' });
        }
        lpState.el = null;
        lpState.bar = null;
        lpState.rect = null;
        lpState.id = null;
        lpState.t0 = 0;
    }

    function lpStart(card, id) {
        lpCancel();
        if (card.classList.contains('busy') || card.classList.contains('unavailable') || card.classList.contains('locked')) return;
        lpState.el = card;
        lpState.id = id;
        lpState.t0 = performance.now();
        lpState.bar = ensureBar(card);
        lpState.rect = card.getBoundingClientRect();
        card.classList.add('jg-pt-lp-press');
        if (window.gsap) gsap.to(card, { scale: PRESS_SCALE, duration: 0.16, ease: 'back.out(2.6)', overwrite: 'auto' });
        // 前 TAP_MS 不画进度条：快速单击的时候根本不该看到它在走
        lpState.showTimer = setTimeout(function () { barRun(lpState.bar); }, TAP_MS);
        lpState.timer = setTimeout(function () {
            // 长按成立：抑制随后那次 click（Blazor 挂在它上面做全局换肤）
            armSwallow(id, true);
            if (window.gsap) {
                gsap.fromTo(card, { scale: PRESS_SCALE }, { scale: 1, duration: 0.35, ease: 'back.out(2.4)', overwrite: 'auto' });
            }
            lpCancel();
            if (dotNet.onLongPress) {
                try { dotNet.onLongPress.invokeMethodAsync('OnSkinLongPress', id); } catch (e) { }
            }
        }, LP_MS);
    }

    /**
     * 绑定皮肤格长按。cards = [{ el 选择器由 Blazor 侧 data-skin-id 提供 }]
     * dotNetRef 为 DotNetObjectReference，暴露 OnSkinLongPress(id)。
     */
    window.junigridJs.portraitsBind = function (dotNetRef) {
        dotNet.onLongPress = dotNetRef;
        // 事件委托：Blazor 重渲染后仍有效
        if (window.__jgPtLpBound) return;
        window.__jgPtLpBound = true;
        document.addEventListener('pointerdown', function (e) {
            var card = e.target.closest && e.target.closest('.jg-pt-skin[data-skin-id]');
            if (!card) return;
            if (e.button !== 0 && e.pointerType === 'mouse') return;
            lpStart(card, card.getAttribute('data-skin-id'));
        });
        // 取消的手势也要看时长：按过 TAP_MS 就说明玩家是在做长按（哪怕中途放弃），
        // 这一次松手派发的 click 不能算「换肤」；没到 TAP_MS 就是普通单击，照常放行。
        function cancelArmed(untilPointerup) {
            if (!lpState.el) return;
            var held = performance.now() - lpState.t0;
            var id = lpState.id;
            lpCancel();
            if (held >= TAP_MS) armSwallow(id, untilPointerup);
        }
        ['pointerup', 'pointercancel', 'pointerleave'].forEach(function (ev) {
            document.addEventListener(ev, function (e) {
                // 松手 = 欠的那次 click 马上就到，这时才解除「等松手」那档抑制
                if (ev !== 'pointerleave') disarmPendingSwallow();
                if (!lpState.el) return;
                // 按住划出格子也算取消
                if (ev === 'pointerleave' && e.target !== lpState.el && !lpState.el.contains(e.target)) return;
                cancelArmed(ev === 'pointerleave');
            }, true);
        });
        // 按住划出卡片矩形（留 10px 余量）也算取消。这里靠坐标判而不是 setPointerCapture：
        // 捕获会把随后 click 的 target 改写成捕获元素，Blazor 挂在子按钮上的委托 @onclick 就再也找不到了。
        document.addEventListener('pointermove', function (e) {
            if (!lpState.el || !lpState.rect) return;
            var r = lpState.rect;
            if (e.clientX < r.left - HIT_PAD || e.clientX > r.right + HIT_PAD ||
                e.clientY < r.top - HIT_PAD || e.clientY > r.bottom + HIT_PAD) cancelArmed(true);
        }, true);
        // 窗口失焦 / 标签页隐藏：松手事件根本不会送回来，不兜住就一直停在按压态
        window.addEventListener('blur', function () { disarmPendingSwallow(); lpCancel(); });
        document.addEventListener('visibilitychange', function () { if (document.hidden) { disarmPendingSwallow(); lpCancel(); } });
        // 长按触发后吞掉 click，避免误触全局换肤
        document.addEventListener('click', function (e) {
            var card = e.target.closest && e.target.closest('.jg-pt-skin[data-skin-id]');
            if (!card) return;
            var sid = card.getAttribute('data-skin-id');
            if (!swallowArmed[sid]) return;
            e.preventDefault();
            e.stopPropagation();
            delete swallowArmed[sid];
            if (pendingSwallowId === sid) pendingSwallowId = null;
        }, true);
    };

    /**
     * ESC 关闭肖像弹窗。dotNetRef 暴露 OnEscapeClose()。
     * 只在弹窗存在时挂钩；重复调用会先摘旧 handler。
     */
    window.junigridJs.portraitsEsc = function (dotNetRef) {
        if (escHandler) {
            document.removeEventListener('keydown', escHandler, true);
            escHandler = null;
        }
        dotNet.onEsc = dotNetRef;
        if (!dotNetRef) return;
        escHandler = function (e) {
            if (e.key !== 'Escape') return;
            // 输入框里的 Esc 交给输入框自己（清空/失焦），不关弹窗
            var t = e.target;
            if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) return;
            if (!document.querySelector('.jg-pt-modal')) return;
            e.preventDefault();
            e.stopPropagation();
            if (dotNet.onEsc) {
                try { dotNet.onEsc.invokeMethodAsync('OnEscapeClose'); } catch (err) { }
            }
        };
        document.addEventListener('keydown', escHandler, true);
    };

    // ── GSAP 视觉增强 ──────────────────────────────────────────

    /** 角色卡进场 stagger（总览网格） */
    window.junigridJs.ptGridEnter = function () {
        if (!window.gsap) return;
        var cards = document.querySelectorAll('.jg-pt-card');
        if (!cards.length) return;
        gsap.killTweensOf(cards);
        gsap.fromTo(cards,
            { autoAlpha: 0, y: 18, scale: 0.94 },
            {
                autoAlpha: 1, y: 0, scale: 1,
                duration: 0.45, ease: 'back.out(1.6)',
                stagger: { each: 0.018, from: 'start' },
                overwrite: 'auto'
            });
    };

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
            // 槽由 Portraits.razor 声明，卡是槽里唯一的子元素；骨架屏没有槽，天然被跳过
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

    /** 季节按钮切换时的微脉冲 */
    window.junigridJs.ptSeasonPulse = function () {
        if (!window.gsap) return;
        var active = document.querySelector('.jg-pt-season.active');
        if (!active) return;
        gsap.fromTo(active, { scale: 0.92 }, { scale: 1, duration: 0.36, ease: 'elastic.out(1, 0.55)' });
    };

    /** 锁定/解锁：锁图标弹跳 + 整卡压暗/恢复 */
    window.junigridJs.ptLockFlip = function (locked) {
        if (!window.gsap) return;
        var btn = document.querySelector('.jg-pt-lockbtn');
        var modal = document.querySelector('.jg-pt-modal');
        if (btn) {
            gsap.fromTo(btn, { scale: 0.7, rotation: -18 }, { scale: 1, rotation: 0, duration: 0.45, ease: 'back.out(2.2)' });
        }
        if (modal) {
            var targets = modal.querySelectorAll('.jg-pt-skin, .jg-pt-season');
            if (locked) {
                gsap.to(targets, { autoAlpha: 0.35, duration: 0.28, ease: 'power2.out', stagger: 0.012 });
            } else {
                gsap.to(targets, { autoAlpha: 1, duration: 0.32, ease: 'power2.out', stagger: 0.012 });
            }
        }
    };

    /** 工具栏动作按钮涟漪确认 */
    window.junigridJs.ptActionPop = function (selector) {
        if (!window.gsap) return;
        var el = document.querySelector(selector);
        if (!el) return;
        gsap.fromTo(el, { scale: 0.94 }, { scale: 1, duration: 0.4, ease: 'back.out(2.5)' });
    };

    /** 一键应用/恢复后的全网格闪烁确认 */
    window.junigridJs.ptGridFlash = function () {
        if (!window.gsap) return;
        var cards = document.querySelectorAll('.jg-pt-card');
        if (!cards.length) return;
        gsap.fromTo(cards,
            { boxShadow: '0 0 0 0 rgba(242,129,29,0)' },
            {
                boxShadow: '0 0 0 3px rgba(242,129,29,0.35)',
                duration: 0.28, ease: 'power2.out',
                stagger: 0.012, yoyo: true, repeat: 1
            });
    };

    // 清理（页面卸载）
    window.junigridJs.portraitsDispose = function () {
        lpCancel();
        if (escHandler) {
            document.removeEventListener('keydown', escHandler, true);
            escHandler = null;
        }
        dotNet.onLongPress = null;
        dotNet.onEsc = null;
    };
})();
