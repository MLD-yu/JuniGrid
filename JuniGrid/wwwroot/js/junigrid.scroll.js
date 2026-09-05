// ============================================================
// 滚动管理：滚动位置记忆/恢复（/mods 专用 + 全局 URL 级）、popstate 处理
// ============================================================
// scrollTop 设置无效/被重置。先摘掉 transform，再轮询等列表真实撑高后落位。
// scrollTop 设置无效/被重置。先摘掉 transform，再轮询等列表真实撑高后落位。
// v0.72.0：重写为「就绪门控 + 离开时到底标记」——
// ① 旧 "y > limit" 分支在返回时【猜】用户离开前在底部，列表被封面懒加载
//    (<img loading=lazy>) 撑高的过渡期会误触发：先落到偏小的 limit 并收工，
//    行高膨胀后停错位置。现在到底与否由离开时 getScrollState 记录的 atBottom
//    决定，不再猜：target = atBottom ? limit : min(y, limit)。
// ② 就绪门控：列表已渲染（limit>0）且 scrollHeight 连续 ~4 轮不变才算就绪，
//    懒加载/数据渲染的渐进撑高期绝不动手。
// ③ 落位后若高度继续变化（图片迟到）且用户未动，持续跟随重落位；
//    wheel/touchstart/pointerdown 一律让位收工。
// v0.72.4：返回恢复期间隐藏行列表，落位后同帧显示 —— 消除「列表先在顶部闪一帧」。
// 标记打在 MainLayout 的 .jg-main 上（该元素跨导航持久，class 不会因切页被 Blazor 重写）。
// CSS: .jg-restore-pending .jg-modrows { visibility: hidden; }
window.junigridJs.markScrollRestorePending = function (sel) {
    var el = document.querySelector(sel);
    if (el) el.classList.add('jg-restore-pending');
};
window.junigridJs.cancelScrollRestore = function (sel) {
    var el = document.querySelector(sel);
    if (el) el.classList.remove('jg-restore-pending');
};

window.junigridJs.setScrollWhenReady = function (sel, y, atBottom, report) {
    var el = document.querySelector(sel);
    if (!el) return;
    // v0.72.2：诊断回报 —— 结束时向 C# 回一次 (target, 最终scrollTop, 轮数, 原因)
    var reportFn = report && report.invokeMethodAsync
        ? function (t, f, tr, r) { try { report.invokeMethodAsync('Report', t, f, tr, r); } catch (e) { } }
        : function () { };
    var revealIfPending = function () { el.classList.remove('jg-restore-pending'); };
    var tries = 0, done = 0, lastH = -1, userTouched = false;
    var onUser = function () { userTouched = true; };
    el.addEventListener('wheel', onUser, { passive: true });
    el.addEventListener('touchstart', onUser, { passive: true });
    el.addEventListener('pointerdown', onUser, { passive: true });
    var finish = function () {
        el.removeEventListener('wheel', onUser);
        el.removeEventListener('touchstart', onUser);
        el.removeEventListener('pointerdown', onUser);
    };
    var tick = function () {
        // 每轮持续压制入场动画 transform（CSS 关键帧会把它覆盖回去，必须反复压）
        el.classList.remove('jg-page-enter');
        if (el.style.transform !== 'none') el.style.transform = 'none';

        if (userTouched) { revealIfPending(); reportFn(y, el.scrollTop, tries, 'user-touched'); finish(); return; }   // 用户手动滚动，立即让位
        tries++;
        var limit = el.scrollHeight - el.clientHeight;
        if (limit < 0) limit = 0;
        var h = el.scrollHeight;
        // 真实行列表已显示（骨架屏期 .jg-modrows 是 display:none）
        var rows = document.querySelector('.jg-modrows');
        var rowsShown = rows && rows.offsetHeight > 0;

        // v0.72.3：第一帧就落位，不等高度稳定 —— 旧的"稳定 4 轮才动手"让列表在顶部
        // 停 1~2s 再瞬移（用户看到"先顶后跳"的闪屏）。现在每轮都重落位：
        // 高度随后续图片加载增长时跟着重落，用户无感；收工仍要求落位精确且高度稳定。
        if (rowsShown && limit > 0) {
            var target = atBottom ? limit : Math.min(y, limit);
            el.scrollTop = target;
            var placed = Math.abs(el.scrollTop - target) <= 1;
            var heightStable = (h === lastH);
            // v0.72.4：首次成功落位的同一帧就显示行列表 —— 顶部状态从未被绘制过
            if (placed) revealIfPending();
            done = (placed && heightStable) ? done + 1 : 0;
            if (done >= 16) { reportFn(target, el.scrollTop, tries, 'placed-stable'); finish(); return; }   // 稳定约 1.5s，收工
        } else {
            done = 0;
        }
        lastH = h;
        if (tries >= 300) {           // 兜底：~30s 仍未就绪则取当前可达最大，不无条件顶回
            // v0.72.1：limit===0（列表始终没渲染出高度）时绝不能落 0 —— 那就是「回到顶部」
            var fbTarget = limit > 0 ? Math.min(y, limit) : el.scrollTop;
            if (limit > 0) el.scrollTop = fbTarget;
            revealIfPending();
            reportFn(fbTarget, el.scrollTop, tries, 'timeout');
            finish();
            return;
        }
        setTimeout(tick, 90);
    };
    tick();
};

// v0.72.0：离开列表页前取滚动快照 —— scrollTop + 是否在底部（恢复时不再靠猜）。
// 底部容差 4px，覆盖亚像素舍入。
window.junigridJs.getScrollState = function (sel) {
    var el = document.querySelector(sel);
    if (!el) return { y: 0, atBottom: false };
    var limit = el.scrollHeight - el.clientHeight;
    return { y: el.scrollTop, atBottom: limit > 0 && el.scrollTop >= limit - 4 };
};

// v0.71.2：滚动位置双写 sessionStorage（scrollSpy 同 key 格式，返回恢复时双保险）。
// v0.72.0：atBottom 另存独立 key —— 原 key 是纯数字格式，scrollSpy 用 parseFloat 读它，不能动。
window.junigridJs.saveScrollKey = function (key, y, atBottom) {
    try {
        sessionStorage.setItem("jg-scroll:" + key, String(y));
        sessionStorage.setItem("jg-scroll:" + key + ":ab", atBottom ? "1" : "0");
    } catch (e) { }
};
// ─── v1.06.8：全局滚动管理（全新导航归顶 + 返回恢复原位）───
// .jg-main 是跨页共享的滚动容器 —— 本模块解决两件事：
//  ① 全新导航（点链接/导航图标/编程导航）→ 滚动归零：旧版没人归零，
//     从 Mod 列表中间进 Nexus 会直接开在底部（位置被带过去）；
//  ② 返回/前进（popstate）→ 回到离开时的位置：离开那一刻把 scrollTop 存进
//     sessionStorage（按 URL），返回后轮询等新页内容撑高再落位。
// 关键防污染：页面切换期间（骨架屏渲染、浏览器钳制 scrollTop 会触发 scroll 事件）
// 用 __jgScrollLock 锁住所有滚动写入 —— 否则钳制产生的 scroll 事件会把 0 写进存档，
// 「回原位」就变成了「回顶部」（之前关下载页回列表顶端的根因之一）。
// /mods 自带专用恢复系统，恢复阶段跳过它避免双重落位。
(function () {
    var KEY = 'jg:urlscroll';
    function loadMap() { try { return JSON.parse(sessionStorage.getItem(KEY) || '{}'); } catch (e) { return {}; } }
    function saveMap(m) { try { sessionStorage.setItem(KEY, JSON.stringify(m)); } catch (e) { } }
    function urlKey() { return location.pathname + location.search; }
    function isMods(u) { return u === '/mods' || u.indexOf('/mods?') === 0 || u.indexOf('/mods/') === 0; }
    function main() { return document.querySelector('.jg-main'); }

    // 过渡锁：导航后 700ms 内一切滚动写入静默（骨架/钳制期）
    function lock() {
        window.__jgScrollLock = true;
        clearTimeout(window.__jgScrollLockTimer);
        window.__jgScrollLockTimer = setTimeout(function () { window.__jgScrollLock = false; }, 700);
    }

    // ① 全新导航：存好旧页位置 → 清目标页存档 → 归零滚动
    // vNext：/nexus 属「记忆页」—— 前进导航离开再回来必须停在走之前的位置：
    // 不再清掉它的存档，并在渲染后按存档落位（restoreFor）。
    // /mods 走页面组件自己的专用恢复系统（restoreFor 对 /mods 跳过），存档保留与否无影响。
    // 其它页面维持「新导航 = 回顶部」。
    var origPush = history.pushState.bind(history);
    history.pushState = function (s, t, u) {
        var targetPath = null;
        try {
            var el = main();
            var m = loadMap();
            // 离开前：把当前位置存到【旧 URL】名下（返回时恢复的就是它）
            if (el) m[urlKey()] = el.scrollTop;
            var target = new URL(u, location.href);
            targetPath = target.pathname + target.search;
            if (targetPath !== '/nexus') delete m[targetPath];
            saveMap(m);
        } catch (e) { }
        var r = origPush(s, t, u);
        lock();
        var el2 = null;
        try {
            el2 = main();
            if (el2) {
                el2.classList.remove('jg-restore-pending');   // 清掉上一轮未走完的「待恢复」隐藏
                el2.scrollTop = 0;
            }
        } catch (e) { }
        if (targetPath === '/nexus') {
            // vNext：记忆页且有滚动存档 → 新页渲染【前】就挂「待恢复」隐藏（.jg-main 跨导航
            // 持久，nexus 内容一渲染出来就是隐藏态，「先画顶部」的那一帧根本画不出来），
            // restoreFor 落位成功的同一帧揭开（与 /mods v0.72.4 专用系统同思路）
            try {
                var sy = loadMap()['/nexus'];
                if (sy && sy > 1 && el2) el2.classList.add('jg-restore-pending');
            } catch (e) { }
            setTimeout(restoreFor, 150);
        }
        return r;
    };

    // ② 返回/前进：立「返回导航」标记（/mods 兜底恢复用）+ 锁 + 排队恢复
    // vNext：返回 /nexus 且有存档 → 本监听注册早于 Blazor 路由，同步挂隐藏标记，
    // 新 nexus 内容首帧即隐藏，restoreFor 落位同帧揭开（消除「先顶部后中间」闪屏）
    window.addEventListener('popstate', function () {
        try { window.__jgBackNav = true; } catch (e) { }
        lock();
        try {
            if (urlKey() === '/nexus') {
                var sy = loadMap()['/nexus'];
                if (sy && sy > 1) { var eln = main(); if (eln) eln.classList.add('jg-restore-pending'); }
            }
        } catch (e) { }
        setTimeout(restoreFor, 120);
    });
        window.junigridJs.readScrollKey = function (key) {
        try {
            var v = parseFloat(sessionStorage.getItem('jg-scroll:' + key));
            return isNaN(v) ? 0 : v;
        } catch (e) { return 0; }
    };

    // 常规滚动采集（过渡锁期间不写）
    function bindCapture() {
        var el = main();
        if (!el) { setTimeout(bindCapture, 400); return; }
        if (el.__gscrollBound) return;
        el.__gscrollBound = true;
        var pending = false;
        el.addEventListener('scroll', function () {
            if (pending) return;
            pending = true;
            requestAnimationFrame(function () {
                pending = false;
                if (window.__jgScrollLock) return;
                var m = loadMap();
                m[urlKey()] = el.scrollTop;
                saveMap(m);
            });
        }, { passive: true });
    }
    bindCapture();

    // vNext：把指定值（或当前实际位置）写进当前 URL 的滚动存档。
    // 手动刷新（顶栏点当前页图标）归顶后调用 —— 不写的话存档里还是刷新前的旧位置，
    // 下次离开再回来会恢复到旧位置而非刷新后的顶部。
    window.junigridJs.saveCurrentUrlScroll = function (y) {
        try {
            var el = main();
            var m = loadMap();
            m[urlKey()] = typeof y === 'number' ? y : (el ? el.scrollTop : 0);
            saveMap(m);
        } catch (e) { }
    };

    // ③ 恢复：最多 ~4s 轮询，内容撑高即落位；用户手动滚动立即让位
    // vNext：配合导航瞬间挂上的「待恢复」隐藏 —— 落位成功 / 放弃 / 用户接管，
    // 三种收场都在同一帧揭开页面：顶部状态从未被绘制，「先顶后跳」的闪屏帧不存在。
    // restoreSeq 令牌：快速连续导航时旧一轮轮询静默让位，避免两轮互相改写 scrollTop。
    var restoreSeq = 0;
    function restoreFor() {
        var url = urlKey();
        if (isMods(url)) return;   // /mods 专用系统负责
        var el = main();
        if (!el) return;
        var y = loadMap()[url];
        if (!y || y < 1) { el.classList.remove('jg-restore-pending'); return; }   // 无可恢复值：顺带确保不残留隐藏
        var my = ++restoreSeq;
        var tries = 0, lastH = -1, stable = 0, userTouched = false;
        var reveal = function () { el.classList.remove('jg-restore-pending'); };
        var onUser = function () { userTouched = true; };
        el.addEventListener('wheel', onUser, { passive: true });
        el.addEventListener('touchstart', onUser, { passive: true });
        el.addEventListener('pointerdown', onUser, { passive: true });
        var timer = null;
        var tick = function () {
            tries++;
            if (my !== restoreSeq) { cleanup(); return; }   // 被新一轮恢复取代：静默退场，不动新一轮的隐藏标记
            if (userTouched || tries > 45 || !el.isConnected) { reveal(); cleanup(); return; }
            // 压制页面入场动画的 transform（会让 scrollTop 设置无效）
            el.classList.remove('jg-page-enter');
            if (el.style.transform !== 'none') el.style.transform = 'none';
            var h = el.scrollHeight;
            var limit = h - el.clientHeight;
            stable = (h === lastH) ? stable + 1 : 0;   // 高度稳定轮数（骨架/图片撑高期自动归零）
            lastH = h;
            if (limit > 0) {
                el.scrollTop = y;
                if (Math.abs(el.scrollTop - y) <= 2) { reveal(); cleanup(); return; }
            }
            // 高度已稳定多轮却仍够不到 y（内容比离开时短）→ 按可达位置收工，
            // 别让页面一直藏着（高度仍在变化的加载期 stable 会归零，不会误伤）
            if (stable >= 8) { reveal(); cleanup(); return; }
            timer = setTimeout(tick, 90);
        };
        tick();   // 首轮立即跑（setInterval 要再等 90ms 才动手，白屏期会无谓拉长）
        function cleanup() {
            clearTimeout(timer);
            el.removeEventListener('wheel', onUser);
            el.removeEventListener('touchstart', onUser);
            el.removeEventListener('pointerdown', onUser);
        }
    }
})();

