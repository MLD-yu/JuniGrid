// JuniGrid JS 契约冒烟测试（node test/js-smoke/js-contract.mjs）
//
// 1) 扫描 .razor/.cs 里所有 JS interop 调用的第一参数字面量
//    （IJSRuntime InvokeVoidAsync / InvokeAsync<T> / InvokeStreamAsync），
//    断言每个 "junigridJs.xxx" 都真实定义在 window.junigridJs 上。
//    —— 抓的就是 Settings.razor 调 junigridJs.eval（从未定义、异常被吞）这类幽灵绑定。
// 2) 在 stub 沙箱里加载全部 wwwroot/js/junigrid.*.js：任何文件加载期抛错都算 FAIL。
// 3) index.html 引用的 css/js 与磁盘文件双向核对。
//
// 退出码：0 = 全过，1 = 有 FAIL。
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const REPO = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const results = [];
const check = (ok, name, detail = '') => {
  results.push({ ok, name, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  -- ' + detail : ''}`);
};

// ---------- 1) 收集 C#/razor 侧的 interop 调用名 ----------
const sources = [];
const walk = (dir, exts) => {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name === 'bin' || e.name === 'obj' || e.name === 'wwwroot') continue;
    const p = join(dir, e.name);
    if (e.isDirectory()) walk(p, exts);
    else if (exts.some(x => e.name.endsWith(x))) sources.push(p);
  }
};
walk(join(REPO, 'JuniGrid'), ['.razor', '.cs']);

// 只认 IJSRuntime 的三个真实方法名 InvokeVoidAsync / InvokeAsync / InvokeStreamAsync
//（注意不能写成 Invoke(?:Void|Async)?——那既放不进 InvokeVoidAsync，又放进普通委托 .Invoke）
const invokeRe = /\bInvoke(?:VoidAsync|Async|StreamAsync)(?:<[^>()]*>)?\(\s*"([^"]+)"/g;
const invoked = new Map(); // name -> [file:line]
for (const f of sources) {
  const lines = readFileSync(f, 'utf8').split('\n');
  lines.forEach((line, i) => {
    for (const m of line.matchAll(invokeRe)) {
      const arr = invoked.get(m[1]) ?? [];
      arr.push(`${relative(REPO, f)}:${i + 1}`);
      invoked.set(m[1], arr);
    }
  });
}
console.log(`扫描 ${sources.length} 个 .razor/.cs，发现 ${invoked.size} 个不同的 interop 绑定名\n`);

// ---------- 2) stub 沙箱加载全部项目 JS ----------
const jsDir = join(REPO, 'JuniGrid', 'wwwroot', 'js');
const projectJs = readdirSync(jsDir).filter(f => /^junigrid\..+\.js$/.test(f));

const makeStub = () => new Proxy(function () {}, {
  get(t, p) {
    if (p === Symbol.toPrimitive) return () => '';
    if (p === 'then') return () => stubInstance;   // 可链式（document.fonts.ready.then），不喂 await
    return stubInstance;
  },
  set() { return true; },
  apply() { return stubInstance; },
  construct() { return stubInstance; },
  has() { return true; },
});
const stubInstance = makeStub();

// window 必须就是 vm 的全局对象：浏览器里 window.junigridJs = {...} 会同时创建
// 全局 junigridJs，后续文件（nexus/taskdock/widgets）裸引用它。core.js 会整体
// 替换 window.junigridJs，所以统计导出时读最终值而不是持有初始引用。
const sandbox = {
  document: makeStub(),
  gsap: makeStub(),
  console,
  setTimeout, clearTimeout, setInterval, clearInterval,
  requestAnimationFrame: () => 0,
  cancelAnimationFrame() {},
  localStorage: { getItem: () => null, setItem() {}, removeItem() {}, },
  matchMedia: () => ({ matches: false, addEventListener() {}, addListener() {} }),
  navigator: { userAgent: 'contract-test', clipboard: { writeText() {} } },
  location: { href: 'http://localhost/', reload() {}, protocol: 'http:' },
  history: { pushState() {}, replaceState() {}, back() {}, scrollRestoration: 'auto' },
  performance: { now: () => Date.now() },
  addEventListener() {}, removeEventListener() {}, dispatchEvent() {},
  ResizeObserver: class { observe() {} disconnect() {} unobserve() {} },
  MutationObserver: class { observe() {} disconnect() {} },
  getComputedStyle: () => stubInstance,
  CustomEvent: class {},
};
sandbox.window = sandbox;
sandbox.self = sandbox;
vm.createContext(sandbox);

for (const f of projectJs) {
  const code = readFileSync(join(jsDir, f), 'utf8');
  try {
    vm.runInContext(code, sandbox, { filename: f });
    check(true, `加载 ${f}`);
  } catch (err) {
    check(false, `加载 ${f}`, `${err.constructor.name}: ${String(err.message).slice(0, 120)}`);
  }
}
const exported = new Set(Object.keys(sandbox.junigridJs ?? {}));
console.log(`\nwindow.junigridJs 实际导出 ${exported.size} 个成员\n`);

// ---------- 3) 契约核对 ----------
for (const [name, sites] of [...invoked].sort()) {
  if (name.startsWith('junigridJs.')) {
    const member = name.slice('junigridJs.'.length);
    const ok = exported.has(member);
    check(ok, `绑定 ${name}`, ok ? sites.join(', ') : `未定义！调用点: ${sites.join(', ')}`);
  } else {
    // 裸名（如 "eval"）或浏览器点路径（如 "navigator.clipboard.writeText"）
    let ok = true, cur = sandbox;
    if (name === 'eval') { ok = true; }
    else {
      for (const part of name.split('.')) {
        if (cur == null || typeof cur[part] === 'undefined') { ok = false; break; }
        cur = cur[part];
      }
    }
    check(ok, `绑定 ${name}(全局)`, ok ? sites.join(', ') : `window 上不存在！调用点: ${sites.join(', ')}`);
  }
}

// ---------- 4) index.html 资源双向核对 ----------
const html = readFileSync(join(REPO, 'JuniGrid', 'wwwroot', 'index.html'), 'utf8');
// 引用带 ?v= 缓存戳，核对存在性前剥掉
const refs = [...html.matchAll(/(?:src|href)="(?!http|_framework)([^"?]+)(?:\?[^"]*)?"/g)].map(m => m[1]);
const wwwroot = join(REPO, 'JuniGrid', 'wwwroot');
let missing = 0;
for (const r of refs) {
  const ok = existsSync(join(wwwroot, r));
  if (!ok) missing++;
  check(ok, `index.html → ${r}`);
}
const onDisk = [
  ...readdirSync(join(wwwroot, 'css')).map(f => `css/${f}`),
  ...readdirSync(jsDir).filter(f => /^junigrid\./.test(f)).map(f => `js/${f}`),
];
const unref = onDisk.filter(f => !refs.includes(f) && !html.includes(f));
check(unref.length === 0, '磁盘 css/js 均被 index.html 引用', unref.join(', ') || `${onDisk.length} 个文件全部命中`);

// ---------- 5) 列表滚动入场动画的判据 ----------
// 回归过的真实缺陷（2026-10-09 实测）：用 e.intersectionRatio >= 0.5 决定 opacity 0/1，
// 而 intersectionRatio 量的正是我们自己刚写下去的那个 scale —— 压在视口边上的行只要
// 滚动位置抖 2px 就在「完全可见 / 完全透明」之间整幅翻转（滚到底后顶上那行一直闪）。
// 判据必须从未变换的布局高度还原行盒，所以这里钉住两件事：不许再读 intersectionRatio，
// 并且要用 offsetHeight。把回调改回二值闸门，这两条立刻红。
const al = String(sandbox.junigridJs?.animatedListInit ?? '').replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, '');
check(al.length > 0, 'animatedListInit 已导出');
check(!/intersectionRatio/.test(al), '入场判据不读 intersectionRatio（被自身 scale 污染）');
check(/offsetHeight/.test(al), '入场判据用未变换的布局高度还原真实行盒');

// ---------- 汇总 ----------
const failed = results.filter(r => !r.ok);
console.log(`\n==== ${results.length - failed.length} PASS / ${failed.length} FAIL ====`);
process.exit(failed.length ? 1 : 0);
