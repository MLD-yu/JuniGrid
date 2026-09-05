// ============================================================
// 通用控件：日志筛选下拉、GSAP 按钮 hover、PixelCard 像素 hover、
// 折叠面板、弹性滑杆、CursorGrid、LogoLoop
// ============================================================
// ------------------ v1.07.0：日志页筛选下拉（GSAP easeReverse demo「Dropdown」同款） ------------------
// 展开：箭头 elastic 旋转 180° + 面板 elastic 弹出（yPercent -30→0 / scale .7→1）+ 菜单项 back.out(3) 交错入场；
// 收起：demo 的 easeReverse/timeScale(2.5) 语义 —— ≈2.5× 速度的平滑 power 缓出（本地 gsap 3.12 无 easeReverse
// 属性，用独立收场时间线等效），动画完全结束再移除 .open / 清 inline style，杜绝残留白块。
(function () {
    function parts(wrapSel) {
        var wrap = document.querySelector(wrapSel);
        if (!wrap) return null;
        return {
            wrap: wrap,
            menu: wrap.querySelector('.jg-sort-menu'),
            arrow: wrap.querySelector('.jg-sort-arrow'),
            items: wrap.querySelectorAll('.jg-sort-item')
        };
    }
    function kill(p) { if (p.wrap.__tl) { p.wrap.__tl.kill(); p.wrap.__tl = null; } }

    function open(p) {
        kill(p);
        p.wrap.__ddOpen = true;
        p.wrap.classList.add('open');
        gsap.set(p.arrow, { rotation: 0 });
        gsap.set(p.menu, { autoAlpha: 0, yPercent: -30, scale: 0.7, transformOrigin: 'top center' });
        gsap.set(p.items, { opacity: 0, x: -20 });
        p.wrap.__tl = gsap.timeline()
            .to(p.arrow, { rotation: 180, duration: 0.9, ease: 'elastic.out(1.2, 0.3)' }, 0)
            .to(p.menu, { autoAlpha: 1, yPercent: 0, scale: 1, duration: 1, ease: 'elastic.out(1.2, 0.3)' }, 0)
            .fromTo(p.items, { opacity: 0, x: -20 },
                { opacity: 1, x: 0, duration: 0.5, ease: 'back.out(3)', stagger: 0.07 }, 0.1);
    }
    function close(p) {
        if (!p.wrap.__ddOpen) return;
        p.wrap.__ddOpen = false;
        kill(p);
        p.wrap.__tl = gsap.timeline({
            onComplete: function () {
                p.wrap.classList.remove('open');
                gsap.set([p.menu, p.arrow, p.items], { clearProps: 'all' });
            }
        });
        p.wrap.__tl
            .to(p.menu, { autoAlpha: 0, yPercent: -14, scale: 0.86, duration: 0.26, ease: 'power3.out' }, 0)
            .to(p.arrow, { rotation: 0, duration: 0.3, ease: 'power2.inOut' }, 0);
    }

    window.junigridJs.logsFilterInit = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p || !p.menu || p.wrap.__filterBound) return;
        p.wrap.__filterBound = true;
        if (!window.gsap) return;   // 无 gsap：靠 .open + CSS 兜底开关
        document.addEventListener('click', function (e) {
            if (p.wrap.__ddOpen && !p.wrap.contains(e.target)) close(p);
        });
    };
    window.junigridJs.logsFilterToggle = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p) return;
        if (!window.gsap) { p.wrap.classList.toggle('open', !p.wrap.__ddOpen); p.wrap.__ddOpen = !p.wrap.__ddOpen; return; }
        p.wrap.__ddOpen ? close(p) : open(p);
    };
    window.junigridJs.logsFilterClose = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p) return;
        if (!window.gsap) { p.wrap.classList.remove('open'); p.wrap.__ddOpen = false; return; }
        close(p);
    };
})();

// ------------------ v0.2.2：GSAP 弹性按钮 hover（easeReverse 平滑退出） ------------------
// 按钮加 class="jg-gsap-btn"
window.junigridJs.initGsapFx = function () {
    if (!window.gsap) return;
    const hasER = parseFloat(gsap.version) >= 3.13;
    const exitTs = 2.5;

    document.querySelectorAll('.jg-gsap-btn, .jg-path-btn, .jg-nexus-logout-btn, .jg-nexus-login-btn').forEach(function (btn) {
        if (btn.__jgFxBound) return;
        btn.__jgFxBound = true;
        btn.style.transformOrigin = 'center';
        const up = { scale: 1.12, duration: 1.0, ease: 'elastic.out(1.2, 0.3)' };
        if (hasER) up.easeReverse = 'power2.out';
        const tl = gsap.timeline({ paused: true }).to(btn, up, 0);
        btn.addEventListener('mouseenter', function () { tl.timeScale(1).play(); });
        btn.addEventListener('mouseleave', function () { tl.timeScale(exitTs).reverse(); });
    });
};

// ------------------ v0.2.2：禁用开关行的 PixelCard 像素 hover 特效（React Bits 移植） ------------------
window.junigridJs.initPixelHover = function () {
    const COLORS = ['#fecdd3', '#fda4af', '#e11d48'];
    const GAP = 6;

    function Pixel(ctx, x, y, color, speed, delay) {
        this.ctx = ctx; this.x = x; this.y = y; this.color = color;
        this.speed = speed; this.delay = delay;
        this.size = 0;
        this.sizeStep = Math.random() * 0.4;
        this.minSize = 0.5;
        this.maxSize = Math.random() * (this.minSize + 2 - 0.5) + 0.5;
        this.counter = 0;
        this.counterStep = Math.random() * 4 + 10;
        this.isIdle = false; this.isReverse = false; this.isShimmer = false;
    }
    Pixel.prototype.draw = function () {
        const off = 1 - this.size * 0.5;
        this.ctx.fillStyle = this.color;
        this.ctx.fillRect(this.x + off, this.y + off, this.size, this.size);
    };
    Pixel.prototype.appear = function () {
        this.isIdle = false;
        if (this.counter <= this.delay) { this.counter += this.counterStep; return; }
        if (this.size >= this.maxSize) this.isShimmer = true;
        if (this.isShimmer) this.shimmer(); else this.size += this.sizeStep;
        this.draw();
    };
    Pixel.prototype.disappear = function () {
        this.isShimmer = false; this.counter = 0;
        if (this.size <= 0) { this.isIdle = true; return; }
        this.size -= 0.1;
        this.draw();
    };
    Pixel.prototype.shimmer = function () {
        if (this.size >= this.maxSize) this.isReverse = true;
        else if (this.size <= this.minSize) this.isReverse = false;
        if (this.isReverse) this.size -= this.speed; else this.size += this.speed;
    };

    document.querySelectorAll('.jg-switch-row.disabled').forEach(function (row) {
        if (row.__pxBound) return;
        row.__pxBound = true;
        const canvas = document.createElement('canvas');
        canvas.className = 'jg-pixel-canvas';
        row.appendChild(canvas);
        const ctx = canvas.getContext('2d');
        let pixels = [], anim = null, prev = performance.now();

        function init() {
            const w = Math.floor(row.clientWidth), h = Math.floor(row.clientHeight);
            if (!w || !h) return;
            canvas.width = w; canvas.height = h;
            pixels = [];
            for (let x = 0; x < w; x += GAP) {
                for (let y = 0; y < h; y += GAP) {
                    const color = COLORS[Math.floor(Math.random() * COLORS.length)];
                    const dx = x - w / 2, dy = y - h / 2;
                    pixels.push(new Pixel(ctx, x, y, color, 0.08, Math.sqrt(dx * dx + dy * dy)));
                }
            }
        }
        function frame(fn) {
            anim = requestAnimationFrame(function () { frame(fn); });
            const now = performance.now();
            if (now - prev < 1000 / 60) return;
            prev = now;
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            let allIdle = true;
            for (let i = 0; i < pixels.length; i++) {
                pixels[i][fn]();
                if (!pixels[i].isIdle) allIdle = false;
            }
            if (allIdle) cancelAnimationFrame(anim);
        }
        function handle(name) {
            cancelAnimationFrame(anim);
            init();
            if (!pixels.length) return;
            anim = requestAnimationFrame(function () { frame(name); });
        }
        row.addEventListener('mouseenter', function () { handle('appear'); });
        row.addEventListener('mouseleave', function () { handle('disappear'); });
    });
};

// ------------------ v0.2.2：内存管理滑杆面板开合动画（与下拉菜单同款弹性曲线） ------------------
// collapseSet：无动画直接定状态（页面首帧用）；collapseToggle：带 GSAP 弹性开合
(function () {
    window.junigridJs = window.junigridJs || {};

    function setInstant(el, open) {
        if (!window.gsap) { el.style.display = open ? '' : 'none'; return; }
        gsap.set(el, { display: open ? '' : 'none', height: 'auto', autoAlpha: open ? 1 : 0 });
    }

    window.junigridJs.collapseSet = function (el, open) {
        if (el) setInstant(el, open);
    };

    // 返回 Promise：C# await 它可以等到动画真正播完再提交状态
    // （v0.2.2 修复「收不回去」：原先 clearProps:'all' 会把 Blazor 写的 display:none 一并清掉，
    //  面板在动画结束后又冒出来；现在只清 height/opacity/visibility，display 交给 Blazor 管）
    window.junigridJs.collapseToggle = function (el, open) {
        return new Promise(function (resolve) {
            if (!el) { resolve(); return; }
            if (!window.gsap) { el.style.display = open ? '' : 'none'; resolve(); return; }
            gsap.killTweensOf(el);   // 快速连点开关时，掐掉上一次未完成的开合动画防状态打架
            if (open) {
                gsap.set(el, { display: '' });
                gsap.fromTo(el, { height: 0, autoAlpha: 0 },
                    { height: 'auto', autoAlpha: 1, duration: 0.9, ease: 'elastic.out(1.2, 0.3)',
                      clearProps: 'height', onComplete: resolve });
            } else {
                gsap.to(el, { height: 0, autoAlpha: 0, duration: 0.3, ease: 'power2.in',
                    onComplete: function () {
                        gsap.set(el, { clearProps: 'height,opacity,visibility' });
                        el.style.display = 'none';
                        resolve();
                    } });
            }
        });
    };
})();

// ------------------ v0.2.2：弹性滑杆（React Bits ElasticSlider 的 GSAP 复刻） ------------------
// 胶囊轨道 hover 增高；拖到两端轨道橡皮筋拉伸、图标跟随位移；松手弹性弹回。
// markup 约定：.e-slider[data-min,data-max,data-step,data-suffix] > .es-track-wrap > .es-track > .es-fill，右侧 .es-value 显示数值
// 数值变化经 dotNetRef.OnElasticValue(id, value) 回调 Blazor 落配置。
(function () {
    window.junigridJs = window.junigridJs || {};

    function esDecay(value, max) {
        if (max === 0) return 0;
        var entry = value / max;
        var sigmoid = 2 * (1 / (1 + Math.exp(-entry)) - 0.5);
        return sigmoid * max;
    }

    window.junigridJs.elasticInit = function (root, id, startValue, dotNetRef) {
        if (!root || root.__esBound) return;
        root.__esBound = true;

        var min = parseFloat(root.dataset.min) || 0;
        var max = parseFloat(root.dataset.max) || 100;
        var step = parseFloat(root.dataset.step) || 1;
        var suffix = root.dataset.suffix || '';
        var MAX_OVER = 50;

        var track = root.querySelector('.es-track');
        var fill = root.querySelector('.es-fill');
        var valEl = root.querySelector('.es-value');

        var value = Math.min(Math.max(startValue, min), max);
        var dragging = false;
        var region = 'middle';
        var proxy = { o: 0 };

        function round(v) {
            if (step > 0) v = Math.round(v / step) * step;
            return Math.min(Math.max(v, min), max);
        }
        function render() {
            var pct = max > min ? (value - min) / (max - min) * 100 : 0;
            if (fill) fill.style.width = pct + '%';
            if (valEl) valEl.textContent = Math.round(value) + ' ' + suffix;
        }
        function setOverflow(o) {
            if (!track) return;
            if (o <= 0.5 || region === 'middle') {
                track.style.transform = '';
                return;
            }
            var w = track.getBoundingClientRect().width || 1;
            var sy = 1 - (o / MAX_OVER) * 0.2;
            if (region === 'left') {
                track.style.transformOrigin = 'right';
                track.style.transform = 'scaleX(' + (1 + o / w) + ') scaleY(' + sy + ')';
            } else {
                track.style.transformOrigin = 'left';
                track.style.transform = 'scaleX(' + (1 + o / w) + ') scaleY(' + sy + ')';
            }
        }
        function moveTo(e) {
            var rect = track.getBoundingClientRect();
            value = round(min + (e.clientX - rect.left) / rect.width * (max - min));
            var over = 0;
            if (e.clientX < rect.left) { region = 'left'; over = rect.left - e.clientX; }
            else if (e.clientX > rect.right) { region = 'right'; over = e.clientX - rect.right; }
            else region = 'middle';
            proxy.o = esDecay(Math.min(over, 200), MAX_OVER);
            setOverflow(proxy.o);
            render();
        }
        function release() {
            if (!dragging) return;
            dragging = false;
            if (window.gsap && proxy.o > 0.5) {
                gsap.to(proxy, {
                    o: 0, duration: 0.8, ease: 'elastic.out(1, 0.4)',
                    onUpdate: function () { setOverflow(proxy.o); },
                    onComplete: function () { setOverflow(0); }
                });
            } else {
                proxy.o = 0;
                setOverflow(0);
            }
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnElasticValue', id, value);
        }

        root.addEventListener('pointerdown', function (e) {
            dragging = true;
            try { root.setPointerCapture(e.pointerId); } catch (err) { }
            moveTo(e);
        });
        root.addEventListener('pointermove', function (e) { if (dragging) moveTo(e); });
        root.addEventListener('pointerup', release);
        root.addEventListener('pointercancel', release);
        root.addEventListener('lostpointercapture', release);

        render();
    };
})();


// ── 关于卡片：CursorGrid 光标网格（React Bits 移植，canvas 铺底）──
junigridJs.initCursorGrid = function (selector) {
    var CFG = { cellSize: 17.5, color: '#d3d3d3', radius: 140, falloff: 'smooth',
        holdTime: 400, fadeDuration: 800, lineWidth: 1.2, maxOpacity: 1,
        fillOpacity: 0, gridOpacity: 0, cellRadius: 0, clickPulse: true, pulseSpeed: 600 };
    var CURVES = {
        linear: function (t) { return t; },
        smooth: function (t) { return t * t * (3 - 2 * t); },
        sharp: function (t) { return t * t * t; }
    };
    var rgb = CFG.color.replace('#', '');
    var col = [parseInt(rgb.slice(0, 2), 16), parseInt(rgb.slice(2, 4), 16), parseInt(rgb.slice(4, 6), 16)];

    document.querySelectorAll(selector || '.jg-cursor-grid').forEach(function (container) {
        if (container.dataset.cgridBound) return;
        container.dataset.cgridBound = '1';
        var canvas = document.createElement('canvas');
        canvas.className = 'jg-cgrid-canvas';
        container.insertBefore(canvas, container.firstChild);
        var ctx = canvas.getContext('2d');
        var dpr = Math.min(window.devicePixelRatio || 1, 2);

        var cols = 0, rows = 0, offX = 0, offY = 0, w = 0, h = 0;
        var alphas = new Float32Array(0), touched = new Float64Array(0);
        var pulses = [], raf = 0, running = false, lastFrame = 0;

        function rebuild() {
            w = container.offsetWidth; h = container.offsetHeight;
            canvas.width = Math.max(1, Math.round(w * dpr));
            canvas.height = Math.max(1, Math.round(h * dpr));
            canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            cols = Math.ceil(w / CFG.cellSize) + 1;
            rows = Math.ceil(h / CFG.cellSize) + 1;
            offX = (w - cols * CFG.cellSize) / 2;
            offY = (h - rows * CFG.cellSize) / 2;
            alphas = new Float32Array(cols * rows);
            touched = new Float64Array(cols * rows);
        }
        function center(i) {
            return [offX + (i % cols) * CFG.cellSize + CFG.cellSize / 2,
                    offY + Math.floor(i / cols) * CFG.cellSize + CFG.cellSize / 2];
        }
        function energize(x, y) {
            var r = Math.max(CFG.radius, 1), ease = CURVES[CFG.falloff], now = performance.now();
            var minC = Math.max(0, Math.floor((x - r - offX) / CFG.cellSize));
            var maxC = Math.min(cols - 1, Math.floor((x + r - offX) / CFG.cellSize));
            var minR = Math.max(0, Math.floor((y - r - offY) / CFG.cellSize));
            var maxR = Math.min(rows - 1, Math.floor((y + r - offY) / CFG.cellSize));
            for (var cR = minR; cR <= maxR; cR++) for (var cC = minC; cC <= maxC; cC++) {
                var i = cR * cols + cC, c = center(i);
                var d = Math.hypot(c[0] - x, c[1] - y);
                if (d > r) continue;
                var lv = ease(1 - d / r) * CFG.maxOpacity;
                if (lv > alphas[i]) { alphas[i] = lv; touched[i] = now; }
                else if (lv > 0) touched[i] = now;
            }
        }
        function draw(now) {
            var dt = Math.min(now - lastFrame, 50); lastFrame = now;
            ctx.clearRect(0, 0, w, h);
            for (var pi = pulses.length - 1; pi >= 0; pi--) {
                var pu = pulses[pi], ringR = ((now - pu.t0) / 1000) * CFG.pulseSpeed;
                if (ringR > Math.hypot(w, h)) { pulses.splice(pi, 1); continue; }
                var band = CFG.cellSize;
                var minC = Math.max(0, Math.floor((pu.x - ringR - band - offX) / CFG.cellSize));
                var maxC = Math.min(cols - 1, Math.floor((pu.x + ringR + band - offX) / CFG.cellSize));
                var minR = Math.max(0, Math.floor((pu.y - ringR - band - offY) / CFG.cellSize));
                var maxR = Math.min(rows - 1, Math.floor((pu.y + ringR + band - offY) / CFG.cellSize));
                for (var cR = minR; cR <= maxR; cR++) for (var cC = minC; cC <= maxC; cC++) {
                    var i = cR * cols + cC, c = center(i);
                    var d = Math.hypot(c[0] - pu.x, c[1] - pu.y);
                    if (Math.abs(d - ringR) < band / 2 && CFG.maxOpacity > alphas[i]) {
                        alphas[i] = CFG.maxOpacity; touched[i] = now;
                    }
                }
            }
            var anyVisible = pulses.length > 0;
            var fadeStep = dt / Math.max(CFG.fadeDuration, 16);
            var half = CFG.cellSize / 2;
            for (var i = 0; i < alphas.length; i++) {
                var a = alphas[i];
                if (a <= 0) continue;
                if (now - touched[i] > CFG.holdTime) {
                    a = Math.max(0, a - fadeStep); alphas[i] = a;
                    if (a <= 0) continue;
                }
                anyVisible = true;
                var cc = center(i);
                var g = ctx.createRadialGradient(cc[0], cc[1], half * 0.1, cc[0], cc[1], CFG.cellSize);
                g.addColorStop(0, 'rgba(' + col + ', ' + a + ')');
                g.addColorStop(1, 'rgba(' + col + ', 0)');
                ctx.beginPath();
                ctx.rect(cc[0] - half + 0.5, cc[1] - half + 0.5, CFG.cellSize - 1, CFG.cellSize - 1);
                if (CFG.fillOpacity > 0) { ctx.fillStyle = 'rgba(' + col + ', ' + (a * CFG.fillOpacity) + ')'; ctx.fill(); }
                ctx.strokeStyle = g; ctx.lineWidth = CFG.lineWidth; ctx.stroke();
            }
            if (anyVisible) raf = requestAnimationFrame(draw);
            else { running = false; ctx.clearRect(0, 0, w, h); }
        }
        function wake() {
            if (running) return;
            running = true; lastFrame = performance.now();
            raf = requestAnimationFrame(draw);
        }
        function local(e) {
            var r = canvas.getBoundingClientRect();
            return [e.clientX - r.left, e.clientY - r.top];
        }
        container.addEventListener('pointermove', function (e) {
            var p = local(e); energize(p[0], p[1]); wake();
        });
        container.addEventListener('pointerdown', function (e) {
            var p = local(e); pulses.push({ x: p[0], y: p[1], t0: performance.now() }); wake();
        });
        if (window.ResizeObserver) new ResizeObserver(function () { rebuild(); wake(); }).observe(container);
        rebuild();
    });
};

// ── 关于卡片：LogoLoop 图标跑马灯（React Bits 移植）──
junigridJs.initLogoLoop = function (selector) {
    var SPEED = 35, TAU = 0.25;
    document.querySelectorAll(selector || '.jg-logoloop').forEach(function (container) {
        if (container.dataset.loopBound) return;
        container.dataset.loopBound = '1';
        var track = container.querySelector('.jg-ll-track');
        var seq = container.querySelector('.jg-ll-seq');
        if (!track || !seq) return;

        var seqW = 0, offset = 0, velocity = 0, target = SPEED;
        var raf = 0, last = null, hovered = false;

        function measure() {
            seqW = seq.getBoundingClientRect().width;
            if (seqW <= 0) return;
            var need = Math.max(2, Math.ceil(container.clientWidth / seqW) + 2);
            var copies = track.querySelectorAll('.jg-ll-seq');
            for (var i = copies.length; i < need; i++) {
                var c = seq.cloneNode(true);
                c.setAttribute('aria-hidden', 'true');
                track.appendChild(c);
            }
        }
        function frame(ts) {
            if (last === null) last = ts;
            var dt = Math.max(0, ts - last) / 1000;
            last = ts;
            var want = hovered ? 0 : SPEED;
            velocity += (want - velocity) * (1 - Math.exp(-dt / TAU));
            if (seqW > 0) {
                offset = ((offset + velocity * dt) % seqW + seqW) % seqW;
                track.style.transform = 'translate3d(' + (-offset) + 'px, 0, 0)';
            }
            raf = requestAnimationFrame(frame);
        }
        container.addEventListener('pointerenter', function () { hovered = true; });
        container.addEventListener('pointerleave', function () { hovered = false; });
        container.addEventListener('click', function (e) {
            var copy = e.target.closest('[data-copy]');
            if (!copy) return;
            e.preventDefault();
            var text = copy.getAttribute('data-copy');
            function toast() {
                var t = document.querySelector('.jg-copy-toast');
                if (!t) {
                    t = document.createElement('div');
                    t.className = 'jg-copy-toast';
                    document.body.appendChild(t);
                }
                t.textContent = '邮箱已复制';
                t.classList.add('show');
                clearTimeout(t._timer);
                t._timer = setTimeout(function () { t.classList.remove('show'); }, 1800);
            }
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).then(toast, toast);
            } else {
                var ta = document.createElement('textarea');
                ta.value = text; document.body.appendChild(ta); ta.select();
                try { document.execCommand('copy'); } catch (err) { }
                document.body.removeChild(ta);
                toast();
            }
        });
        if (window.ResizeObserver) new ResizeObserver(measure).observe(container);
        measure(); // 立即量一次；图片加载完成后再校准
        var imgs = seq.querySelectorAll('img');
        imgs.forEach(function (im) {
            if (!im.complete) {
                im.addEventListener('load', measure, { once: true });
                im.addEventListener('error', measure, { once: true });
            }
        });
        setTimeout(measure, 500); // 兜底
        raf = requestAnimationFrame(frame);
    });
};
