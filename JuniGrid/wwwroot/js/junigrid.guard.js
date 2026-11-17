// junigrid.guard.js — Steam Guard 验证码槽位输入（React Bits CodeSlots 形状的原生移植）
// 视觉：落位过冲 / caret 滑行 / 错误倒序 drain；实现：原生 JS + CSS transition。
// Blazor 只通过 DotNetObjectReference 收两个回调：输满（OnGuardSlotsComplete）、
// 错误后重新开始输入（OnGuardSlotsEdit，用来撤掉红色提示）。
(function () {
    const LEN = 5;                 // Steam Guard 码固定 5 位（字母+数字）
    const CASCADE = 20;            // 多位同时落位/退场时每槽间隔 ms
    const SETTLE = 320;            // 与 CSS transition 时长对齐

    let root = null, row = null, input = null, caret = null;
    let slotEls = [], fillEls = [], digitEls = [];
    let chars = [], active = 0, busy = false, dotnet = null;
    let drainTimer = 0;

    function setDelay(i, ms) {
        fillEls[i].style.transitionDelay = ms + 'ms';
        digitEls[i].style.transitionDelay = ms + 'ms';
    }

    function paint() {
        for (let i = 0; i < LEN; i++) {
            const filled = !!chars[i];
            slotEls[i].toggleAttribute('data-filled', filled);
            slotEls[i].toggleAttribute('data-active', i === active && !busy);
            digitEls[i].textContent = chars[i] || '';
            fillEls[i].style.transform = filled ? 'scale(1)' : 'scale(0)';
            digitEls[i].style.opacity = filled ? '1' : '0';
            digitEls[i].style.transform = filled ? 'translateY(0)' : 'translateY(8px)';
        }
        const s = slotEls[active];
        caret.style.transform = 'translateX(' + (s.offsetLeft + s.offsetWidth / 2 - 0.75) + 'px)';
        caret.toggleAttribute('data-show', !busy && !chars[active]);
        row.toggleAttribute('data-busy', busy);
    }

    function clearError() {
        if (!row.hasAttribute('data-status')) return;
        row.removeAttribute('data-status');
        for (let i = 0; i < LEN; i++) setDelay(i, 0);
        dotnet && dotnet.invokeMethodAsync('OnGuardSlotsEdit');
    }

    function insert(raw, from) {
        if (busy) return;
        const ds = String(raw == null ? '' : raw).toUpperCase().replace(/[^A-Z0-9]/g, '');
        if (!ds) return;
        clearError();
        let i = from == null ? active : from;
        const landed = [];
        for (const ch of ds) {
            if (i >= LEN) break;
            chars[i] = ch;
            landed.push(i);
            i++;
        }
        if (!landed.length) return;
        landed.forEach((idx, k) => setDelay(idx, k * CASCADE));
        active = Math.min(i, LEN - 1);
        paint();
        setTimeout(() => landed.forEach(idx => setDelay(idx, 0)), SETTLE + landed.length * CASCADE);
        if (chars.every(Boolean)) {
            busy = true;
            paint();
            dotnet && dotnet.invokeMethodAsync('OnGuardSlotsComplete', chars.join(''));
        }
    }

    function clearSlot(i, stepBack) {
        if (busy) return;
        if (!chars[i]) {
            if (stepBack) { active = i; paint(); }
            return;
        }
        chars[i] = '';
        if (stepBack) active = i;
        paint();
    }

    function firstEmpty() {
        const i = chars.indexOf('');
        return i === -1 ? LEN - 1 : i;
    }

    function onKey(e) {
        if (busy || e.metaKey || e.ctrlKey || e.altKey) return;
        const k = e.key;
        if (/^[0-9a-zA-Z]$/.test(k)) { e.preventDefault(); insert(k); }
        else if (k === 'Backspace') {
            e.preventDefault();
            if (chars[active]) clearSlot(active);
            else if (active > 0) clearSlot(active - 1, true);
        }
        else if (k === 'Delete') { e.preventDefault(); clearSlot(active); }
        else if (k === 'ArrowLeft') { e.preventDefault(); active = Math.max(active - 1, 0); paint(); }
        else if (k === 'ArrowRight') { e.preventDefault(); active = Math.min(active + 1, LEN - 1); paint(); }
        else if (k === 'Home') { e.preventDefault(); active = 0; paint(); }
        else if (k === 'End') { e.preventDefault(); active = LEN - 1; paint(); }
    }

    function onPaste(e) {
        if (busy) return;
        e.preventDefault();
        const text = (e.clipboardData || window.clipboardData).getData('text');
        const ds = String(text).replace(/[^A-Za-z0-9]/g, '');
        insert(text, ds.length === 1 ? active : 0);
    }

    function onInput() {
        // 移动端/IME 兜底：keydown 拦不住的输入从这里进
        const v = input.value;
        input.value = '';
        if (v) insert(v, v.replace(/[^A-Za-z0-9]/g, '').length === 1 ? active : 0);
    }

    function onRowDown(e) {
        if (busy) return;
        e.preventDefault();
        const rect = row.getBoundingClientRect();
        const zoom = rect.width / (row.offsetWidth || rect.width) || 1;
        const pitch = slotEls.length > 1 ? slotEls[1].offsetLeft - slotEls[0].offsetLeft : 54;
        const i = Math.floor((e.clientX - rect.left) / zoom / pitch);
        active = Math.max(0, Math.min(i, firstEmpty()));
        paint();
        input.focus();
    }

    function destroy() {
        clearTimeout(drainTimer);
        if (input) {
            input.removeEventListener('keydown', onKey);
            input.removeEventListener('paste', onPaste);
            input.removeEventListener('input', onInput);
        }
        if (row) row.removeEventListener('mousedown', onRowDown);
        root = row = input = caret = null;
        slotEls = fillEls = digitEls = [];
        dotnet = null;
        busy = false;
    }

    window.junigridJs = window.junigridJs || {};

    junigridJs.guardSlotsInit = function (sel, ref) {
        destroy();
        root = document.querySelector(sel);
        if (!root) return;
        dotnet = ref;
        row = root.querySelector('.gs-row');
        input = root.querySelector('.gs-input');
        caret = root.querySelector('.gs-caret');
        slotEls = Array.from(root.querySelectorAll('.gs-slot'));
        fillEls = slotEls.map(s => s.querySelector('.gs-fill'));
        digitEls = slotEls.map(s => s.querySelector('.gs-digit'));
        chars = Array(LEN).fill('');
        active = 0;
        busy = false;
        input.addEventListener('keydown', onKey);
        input.addEventListener('paste', onPaste);
        input.addEventListener('input', onInput);
        row.addEventListener('mousedown', onRowDown);
        paint();
        input.focus();
    };

    // 上一条码被 Steam 判错：红染 + 从最后一位倒序退场，退完放开输入
    junigridJs.guardSlotsError = function () {
        if (!row) return;
        const filled = chars.map((c, i) => (c ? i : -1)).filter(i => i >= 0).reverse();
        row.setAttribute('data-status', 'error');
        busy = true;
        filled.forEach((idx, k) => { setDelay(idx, k * CASCADE); chars[idx] = ''; });
        paint();
        clearTimeout(drainTimer);
        drainTimer = setTimeout(() => {
            filled.forEach(idx => setDelay(idx, 0));
            // 红染保留到下一次输入（clearError 撤掉并通知 Blazor 撤提示），与 CodeSlots 一致
            busy = false;
            active = 0;
            paint();
            input && input.focus();
        }, filled.length * CASCADE + SETTLE);
    };

    // 验证通过：整行洗白 + 对勾升起（动画在 CSS），锁输入，等 Blazor 收弹窗
    junigridJs.guardSlotsSuccess = function () {
        if (!row) return;
        busy = true;
        row.setAttribute('data-status', 'success');
        paint();
    };

    junigridJs.guardSlotsDestroy = destroy;
})();