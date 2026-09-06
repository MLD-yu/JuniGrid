/* ═══ v1.1.2：全局自动翻译观察器 ═══
   浏览器整页翻译同款原理：MutationObserver 盯住整个 WebView 文档，任何页面、任何时刻
   冒出来的外文文本节点都被抓到，去重后整批送进 C# TranslationService（谷歌引擎 + 磁盘
   缓存 + 微批），译文回来原地替换成中文。页面无需各自接入 —— 内容一出现就翻。

   规则：
   · 行首 [HH:MM:SS LEVEL SMAPI] 式日志前缀原样保留（SMAPI 分类筛选依赖它）；
   · SCRIPT/STYLE/CODE/PRE/TEXTAREA/INPUT/IFRAME 与 contenteditable、[data-notrans] 不翻；
   · 纯数字/纯链接/纯 Windows 路径/已是中文/堆栈帧不翻（C# 侧 NeedsTranslation 再兜一层）；
   · 自身写回的中文不再触发下一轮（写回后中文主导，worth() 直接拒绝，天然无回环）。
   开关：junigridJs.transSetEnabled(bool)；初始化：junigridJs.transInit(dotNetRef)。 */
(function (w, d) {
    'use strict';
    var jg = w.junigridJs = w.junigridJs || {};
    if (jg.transInit) return;                 // 防重复注入

    var dotNet = null;
    var enabled = true;
    var started = false;
    var pending = new Map();                  // 消息文本 -> Set<{node, prefix}>
    var timer = null;

    var SKIP_TAGS = { SCRIPT: 1, STYLE: 1, CODE: 1, PRE: 1, TEXTAREA: 1, INPUT: 1, NOSCRIPT: 1, IFRAME: 1, OPTION: 1 };
    var LOG_PREFIX = /^\[\d{1,2}:\d{2}:\d{2}[^\]]*\]\s*/;   // [21:23:10 INFO  SMAPI] 
    var HAS_WORD = /[A-Za-z]{3,}/;

    function isSkip(el) {
        if (!el || el.nodeType !== 1) return true;
        if (SKIP_TAGS[el.tagName]) return true;
        if (el.isContentEditable) return true;
        if (el.closest && el.closest('[data-notrans]')) return true;
        return false;
    }

    function worth(text) {
        if (!text) return false;
        if (/^\s{2,}at\s+\S/.test(text)) return false;             // 堆栈帧（用原文判定，trim 会吃掉缩进）
        var t = text.trim();
        if (t.length < 3 || !HAS_WORD.test(t)) return false;
        var cjk = (t.match(/[\u4e00-\u9fff]/g) || []).length;
        var latin = (t.match(/[A-Za-z]/g) || []).length;
        if (cjk > 0 && cjk * 2 >= latin) return false;             // 中文已主导（含半中文混合行）
        if (/^https?:\/\/\S+$/i.test(t)) return false;             // 纯链接
        if (/^[A-Za-z]:\\[\w\\ .:()\-&']+$/.test(t)) return false; // 纯 Windows 路径
        return true;
    }

    /* 从文本节点提取待翻消息（剥掉日志前缀），登记进 pending */
    function collectText(node) {
        if (!dotNet || !enabled) return;
        if (!node || node.nodeType !== 3) return;
        var p = node.parentElement;
        if (isSkip(p)) return;
        var text = node.nodeValue;
        if (!text || !worth(text)) return;
        var prefix = '';
        var rest = text;
        var m = text.match(LOG_PREFIX);
        if (m && m[0].length < text.length) { prefix = m[0]; rest = text.slice(m[0].length); }
        if (!worth(rest)) return;
        var set = pending.get(rest);
        if (!set) { set = new Set(); pending.set(rest, set); }
        set.add({ node: node, prefix: prefix, expected: text });   // expected = 该节点当前完整原文（写回校验用）
    }

    /* 扫一段新插入的子树（元素或文本节点） */
    function walk(root) {
        if (!dotNet || !enabled || !root) return;
        if (root.nodeType === 3) { collectText(root); return; }
        if (root.nodeType !== 1 || isSkip(root)) return;
        var walker = d.createTreeWalker(root, 4 /* SHOW_TEXT */, {
            acceptNode: function (n) {
                var p = n.parentElement;
                if (!p || isSkip(p) || !worth(n.nodeValue)) return 2;   // REJECT
                return 1;                                               // ACCEPT
            }
        });
        for (var n; (n = walker.nextNode());) collectText(n);
    }

    function schedule() {
        if (timer) return;
        timer = setTimeout(flush, 220);      // 微批窗口：聚合同一波渲染的新文本
    }

    function flush() {
        timer = null;
        if (!dotNet || !enabled || pending.size === 0) return;

        var texts = [];
        var sets = [];
        var i = 0;
        for (var kv of pending) {
            if (texts.length >= 120) break;  // 单轮上限，剩余留到下一轮
            texts.push(kv[0]);
            sets.push(kv[1]);
            pending.delete(kv[0]);
        }
        if (pending.size > 0) setTimeout(flush, 400);

        dotNet.invokeMethodAsync('TransBatch', texts).then(function (zh) {
            for (var k = 0; k < texts.length; k++) {
                var out = zh && zh[k] ? zh[k] : texts[k];
                if (out === texts[k]) continue;            // 翻译失败/无需翻译：保留原文
                var set = sets[k];
                set.forEach(function (job) {
                    try {
                        var node = job.node;
                        if (node.nodeValue !== job.expected) return;  // 文本已被 Blazor 改写，这轮作废
                        if (!node.__jgTrans) node.__jgTrans = job.expected;  // 记住完整原文，关开关时还原
                        node.nodeValue = job.prefix + out;
                    } catch (e) { /* 节点已 detach：忽略 */ }
                });
            }
        }).catch(function () { /* 引擎暂不可用：保留原文，重开开关/页面重渲会重试 */ });
    }

    /* 关闭开关：把所有被替换过的文本节点还原成原文 */
    function restoreAll() {
        try {
            var walker = d.createTreeWalker(d.body, 4, null);
            var hits = [];
            for (var n; (n = walker.nextNode());) if (n.__jgTrans) hits.push(n);
            hits.forEach(function (n) {
                try { n.nodeValue = n.__jgTrans; delete n.__jgTrans; } catch (e) { }
            });
        } catch (e) { }
    }

    function start() {
        if (started || !d.body) return;
        started = true;
        var obs = new MutationObserver(function (muts) {
            for (var i = 0; i < muts.length; i++) {
                var m = muts[i];
                if (m.type === 'characterData') collectText(m.target);
                else if (m.type === 'childList')
                    for (var k = 0; k < m.addedNodes.length; k++) walk(m.addedNodes[k]);
            }
            schedule();
        });
        obs.observe(d.body, { childList: true, subtree: true, characterData: true });
    }

    jg.transInit = function (ref) {
        dotNet = ref;
        start();                                     // 挂观察器（幂等），此后新增内容自动被抓
        if (enabled) { walk(d.body); schedule(); }   // 存量内容先扫一遍
    };

    jg.transSetEnabled = function (on) {
        enabled = !!on;
        if (enabled) {
            start();           // 兜底挂观察器（正常已在 transInit 挂过）
            walk(d.body);      // 开启（含中途重开）：全页重扫，屏幕上现存的英文立即送翻
            schedule();
        } else {
            if (timer) { clearTimeout(timer); timer = null; }
            pending.clear();   // 丢弃排队中的任务
            restoreAll();      // 已替换成中文的文本全部还原成原文
            // 用户约定：关开关 = 清空全部翻译缓存（内存+磁盘）
            if (dotNet) dotNet.invokeMethodAsync('ClearTransCache').catch(function () { });
        }
    };
})(window, document);
