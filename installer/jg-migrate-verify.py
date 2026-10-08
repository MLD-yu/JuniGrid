import sys
import os, shutil, subprocess, sys, json, io, re
try:  # Windows 控制台默认 GBK：打印中文/⇒ 会 UnicodeEncodeError，把防线自己弄崩（2026-10-04 打包时踩中）
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "JuniGridInstaller", "bin", "Release",
                   "net10.0-windows", "JuniGridSetup.exe")
if "--exe" in sys.argv:
    EXE = sys.argv[sys.argv.index("--exe") + 1]
if not os.path.exists(EXE):
    print("FAIL 找不到被测的安装器 exe：" + EXE + "（先 dotnet build installer/JuniGridInstaller -c Release）")
    sys.exit(1)
ROOT = os.path.join(os.environ["TEMP"], "jg-mg-fx")

# 期望被搬走的数据目录 —— 从 InstallMigrate.DataDirs 读源码，**不再自己抄一份**。
# 自己抄的后果实测过（2026-10-04 16:35 故障注入）：从 DataDirs 里删掉 webview2，
# 本文件早于那份副本写、且没跟上 nexus-browse，于是 20 项夹具全绿，
# "少搬一个目录"这件事只有另一份脚本的漂移检查能发现。这里改成读源码后，
# 少搬就会直接反映成下面 M2a/M2b 失败 —— 行为夹具自己也能兜住清单漂移。
MIGRATE_CS = os.path.join(HERE, "JuniGridInstaller", "InstallMigrate.cs")


def _declared(var):
    m = re.search(var + r"\s*=\s*\{(.*?)\}", io.open(MIGRATE_CS, encoding="utf-8-sig").read(), re.S)
    return re.findall(r'"([^"]+)"', m.group(1)) if m else []


DATA = _declared("DataDirs")
DATA_FILES = _declared("DataFiles")     # 散装文件走另一条分支，单独归一份
DECLARED_ALL = set(DATA) | set(DATA_FILES)

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ("  -- " + detail if detail else ""))


# 清单自身的前置检查：清单与应用源码对不上就别往下跑了。
# 判据是【应用侧每个 InCache("x") 都在清单里】，不是"清单里有没有 nexus-browse"这种
# 点名 —— 点名只能挡住那一个名字，别的目录漏了照样全绿（2026-10-04 故障注入实测：
# 删掉 webview2 后本文件 21 项全绿）。这里必须做全集合比对。
APP_DIR = os.path.join(os.path.dirname(HERE), "JuniGrid")


def _used_incache():
    used = set()
    for root, dirs, files in os.walk(APP_DIR):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj", ".git", ".vs")]
        for fn in files:
            if fn.endswith(".cs"):
                try:
                    used |= set(re.findall(r'InCache\(\s*"([^"]+)"',
                                           io.open(os.path.join(root, fn), encoding="utf-8-sig").read()))
                except Exception:
                    pass
    used.discard("")
    return used


def check_declared():
    if not DATA:
        check("M0 读得到 InstallMigrate.DataDirs", False, "读不到 DataDirs，本文件全部检查失效")
        return
    used = _used_incache()
    # 比 DataDirs ∪ DataFiles —— 散装文件（_smapi-install.dat）在 DataFiles 里，
    # 拿它跟 DataDirs 比会误报"漏搬"（第一版就踩了这个）。
    missing = sorted(used - DECLARED_ALL)
    check("M0 搬家清单 ⊇ 应用全部 InCache（DataDirs %d + DataFiles %d / 应用用到 %d）"
          % (len(DATA), len(DATA_FILES), len(used)),
          not missing,
          ("应用在用但清单里没有: " + ", ".join(missing)) if missing else
          ("清单覆盖: " + ", ".join(sorted(used)) if used else "应用侧没扫到 InCache 调用"))


def w(path, text):
    d = os.path.dirname(path)
    if d:
        os.makedirs(d, exist_ok=True)
    io.open(path, "w", encoding="utf-8", newline="\n").write(text)


def mkitem(path, text):
    if os.path.isdir(path):
        shutil.rmtree(path)
    if os.path.exists(path):
        os.remove(path)
    w(path, text)


def run(old, new, cfg, apply=True):
    out = os.path.join(ROOT, "report.txt")
    if os.path.exists(out):
        os.remove(out)
    env = dict(os.environ)
    env["JGINSTALLER_APPLY"] = "1" if apply else "0"
    p = subprocess.run([EXE, "--migrate-preview", old, new, out, cfg], env=env,
                       capture_output=True, timeout=180)
    txt = io.open(out, encoding="utf-8").read() if os.path.exists(out) else "(no report) " + str(p.returncode)
    return p.returncode, txt


def fresh_case(name):
    base = os.path.join(ROOT, name)
    if os.path.isdir(base):
        shutil.rmtree(base, ignore_errors=True)
    os.makedirs(base)
    return base


def build_old(base, with_data=True, prog=True, user=True):
    old = os.path.join(base, "old")
    os.makedirs(old, exist_ok=True)
    if with_data:
        # 按 DataDirs 逐个造 —— DataDirs 新增一个目录，这里自动跟着造，
        # 断言也就自动跟着验；写死一份的话，清单加了新目录而夹具不知道，就等于没验。
        for d in DATA:
            mkitem(os.path.join(old, d, "probe.bin"), "probe:" + d)
        # 下面几个造得细一点，供 M2c/M2g/M3b 等具体断言用
        mkitem(os.path.join(old, "depot-staging", "1.6.15", "body.bin"), "x" * 4096)
        mkitem(os.path.join(old, "depot-staging", "1.6.15", "meta.txt"), "meta")
        mkitem(os.path.join(old, "downloads", "nxm-1310-5131.zip"), "zipbytes")
        mkitem(os.path.join(old, "webview2", "Local State"), "{}")
        mkitem(os.path.join(old, "portrait-covers", "a.png"), "png")
        mkitem(os.path.join(old, "_smapi-install.dat"), "legacy-dat")
    if prog:
        mkitem(os.path.join(old, "wwwroot", "index.html"), "html")
        mkitem(os.path.join(old, "JuniGrid.exe"), "MZ")
    if user:
        mkitem(os.path.join(old, "用户自己的", "笔记.txt"), "别删我")
    return old


def cfgpath(base, payload):
    p = os.path.join(base, "junigrid.config.json")
    w(p, json.dumps(payload, ensure_ascii=False, indent=2))
    return p


def main():
    if os.path.isdir(ROOT):
        shutil.rmtree(ROOT, ignore_errors=True)
    os.makedirs(ROOT)

    check_declared()   # 清单读不到就别往下跑了

    # ── M1 同址：什么都不搬，配置一个字节都不动 ──
    b = fresh_case("m1")
    old = build_old(b)
    cfg = cfgpath(b, {"gamePath": "E:\\Steam\\sdvalley", "cacheRoot": old, "lockGameVersion": True})
    before = io.open(cfg, encoding="utf-8").read()
    rc, txt = run(old, old, cfg)
    check("M1 同址 ⇒ 不搬不改配置",
          "（没有可搬的数据目录）" in txt and io.open(cfg, encoding="utf-8").read() == before,
          "rc=%d" % rc)

    # ── M2 干净搬家（同盘、目标不存在 ⇒ Directory.Move 分支）──
    b = fresh_case("m2")
    old = build_old(b)
    new = os.path.join(b, "new")
    cfg = cfgpath(b, {"gamePath": "E:\\Steam\\sdvalley", "cacheRoot": old, "lockGameVersion": True,
                      "activeProfileByGame": {"1.6.15": "默认"}, "modLastDownload": {"1": "2026-10-01"}})
    rc, txt = run(old, new, cfg)
    # 断言【DataDirs 里的每一个】，而不是抽查四个 ——
    # 抽查写死的四个，清单少一个就察觉不到（2026-10-04 故障注入实测过）。
    moved_ok = all(os.path.isdir(os.path.join(new, d)) for d in DATA)
    left_ok = all(not os.path.exists(os.path.join(old, d)) for d in DATA)
    j = json.load(io.open(cfg, encoding="utf-8"))
    check("M2a DataDirs 全部 %d 个真的搬进了新目录" % len(DATA), moved_ok, txt.replace("\n", " | ")[:220])
    check("M2b 旧目录里它们没了（是搬不是拷）", left_ok)
    check("M2c 程序目录没被搬走（wwwroot/JuniGrid.exe 还在旧目录）",
          os.path.isfile(os.path.join(old, "wwwroot", "index.html"))
          and os.path.isfile(os.path.join(old, "JuniGrid.exe"))
          and not os.path.exists(os.path.join(new, "wwwroot")))
    check("M2d 用户自己放的目录原样留在旧目录",
          os.path.isfile(os.path.join(old, "用户自己的", "笔记.txt")))
    check("M2e cacheRoot 改指新目录，其它键一个不少",
          j.get("cacheRoot") == os.path.normpath(new)
          and j.get("gamePath") == "E:\\Steam\\sdvalley"
          and j.get("lockGameVersion") is True
          and "1" in (j.get("modLastDownload") or {})
          and (j.get("activeProfileByGame") or {}).get("1.6.15") == "默认",
          "cacheRoot=" + str(j.get("cacheRoot")) + " ‖ new=" + os.path.normpath(new))
    check("M2f 改写过留了 .before-move.bak（坏配置还能回去）",
          os.path.isfile(cfg + ".before-move.bak"))
    check("M12 缓存根下的散装文件（_smapi-install.dat）也一起搬，目录搬了文件漏下等于半搬",
          os.path.isfile(os.path.join(new, "_smapi-install.dat"))
          and not os.path.exists(os.path.join(old, "_smapi-install.dat")), "")
    check("M2g 内容跟着走（不是空目录）",
          io.open(os.path.join(new, "depot-staging", "1.6.15", "body.bin"), encoding="utf-8").read() == "x" * 4096)

    # ── M3 新目录已有同名数据 ⇒ 合并复制分支：已存在的不许被覆盖 ──
    b = fresh_case("m3")
    old = build_old(b)
    new = os.path.join(b, "new")
    mkitem(os.path.join(new, "depot-staging", "1.6.15", "body.bin"), "NEWER-不要被我覆盖")
    cfg = cfgpath(b, {"cacheRoot": old})
    rc, txt = run(old, new, cfg)
    same = io.open(os.path.join(new, "depot-staging", "1.6.15", "body.bin"), encoding="utf-8").read()
    check("M3a 新目录里已有的同名文件没被旧文件盖掉", same == "NEWER-不要被我覆盖", same[:40])
    check("M3b 缺的文件补齐了（meta.txt 过来了）",
          os.path.isfile(os.path.join(new, "depot-staging", "1.6.15", "meta.txt")))
    check("M3c 校验过了才删源：旧 depot-staging 已不在",
          not os.path.exists(os.path.join(old, "depot-staging")))
    check("M3d 别的数据目录照常搬",
          os.path.isdir(os.path.join(new, "downloads")) and os.path.isdir(os.path.join(new, "webview2")))

    # ── M4 目标位置被一个同名文件占着 ⇒ 建目录失败：必须记失败而且不许动源 ──
    b = fresh_case("m4")
    old = build_old(b)
    new = os.path.join(b, "new")
    os.makedirs(new)
    mkitem(os.path.join(new, "downloads"), "我不是目录")
    cfg = cfgpath(b, {"cacheRoot": old})
    rc, txt = run(old, new, cfg)
    check("M4a 撞名文件 ⇒ 记一条「没搬成」",
          "没搬成" in txt and "downloads" in txt, txt.replace("\n", " | ")[:200])
    check("M4b 旧目录里的 downloads 完整保留（绝不两头都没有）",
          os.path.isfile(os.path.join(old, "downloads", "nxm-1310-5131.zip")))

    # ── M5 cacheRoot 指向别处（用户自己划的目录）⇒ 数据照搬，配置不许改 ──
    b = fresh_case("m5")
    old = build_old(b)
    new = os.path.join(b, "new")
    cfg = cfgpath(b, {"cacheRoot": "D:\\JuniGridCache", "gamePath": "E:\\x"})
    before = io.open(cfg, encoding="utf-8").read()
    rc, txt = run(old, new, cfg)
    check("M5 配置里写的不是旧安装目录 ⇒ 一个字节都不改",
          io.open(cfg, encoding="utf-8").read() == before
          and os.path.isdir(os.path.join(new, "depot-staging")), txt.replace("\n", " | ")[:160])

    # ── M6 cacheRoot 键根本没有（数据住在历史默认位置之外）⇒ 搬了才补上 ──
    b = fresh_case("m6")
    old = build_old(b)
    new = os.path.join(b, "new")
    cfg = cfgpath(b, {"gamePath": "E:\\x", "lockGameVersion": False})
    rc, txt = run(old, new, cfg)
    j = json.load(io.open(cfg, encoding="utf-8"))
    check("M6 搬了东西 ⇒ 把 cacheRoot 写上，应用才会去新目录找",
          j.get("cacheRoot") == os.path.normpath(new) and j.get("gamePath") == "E:\\x",
          "cacheRoot=" + str(j.get("cacheRoot")))

    # ── M7 配置是坏 JSON ⇒ 不许崩、不许改、搬家照做 ──
    b = fresh_case("m7")
    old = build_old(b)
    new = os.path.join(b, "new")
    cfg = os.path.join(b, "junigrid.config.json")
    w(cfg, "{ this is ] not json")
    rc, txt = run(old, new, cfg)
    check("M7 坏配置：搬家照做、配置原样、没留 tmp",
          io.open(cfg, encoding="utf-8").read() == "{ this is ] not json"
          and os.path.isdir(os.path.join(new, "downloads"))
          and not os.path.exists(cfg + ".junigrid-move.tmp"), txt.replace("\n", " | ")[:200])

    # ── M8 旧位置没有数据目录 ⇒ 不 patch 配置（哪怕 cacheRoot 指着它）──
    b = fresh_case("m8")
    old = build_old(b, with_data=False)
    new = os.path.join(b, "new")
    cfg = cfgpath(b, {"cacheRoot": old, "gamePath": "E:\\x"})
    before = io.open(cfg, encoding="utf-8").read()
    rc, txt = run(old, new, cfg)
    check("M8 无数据可搬 ⇒ 配置不动（没有搬东西却改指新目录 = 把用户指向空目录）",
          io.open(cfg, encoding="utf-8").read() == before, txt.replace("\n", " | ")[:160])

    # ── M9 预览模式（不真搬）：报告要有、目录要原样 ──
    b = fresh_case("m9")
    old = build_old(b)
    new = os.path.join(b, "new")
    cfg = cfgpath(b, {"cacheRoot": old})
    rc, txt = run(old, new, cfg, apply=False)
    check("M9 只算不搬：报告写了 MB，源目录一个没动",
          "【只算不搬】" in txt and "MB" in txt
          and os.path.isdir(os.path.join(old, "depot-staging"))
          and not os.path.exists(new), txt.replace("\n", " | ")[:200])

    npass = sum(1 for _, ok, _ in results if ok)
    print("\n==== 合计: %d PASS / %d FAIL ====" % (npass, len(results) - npass))
    for n, ok, d in results:
        if not ok:
            print("FAIL:", n, "--", d)
    return 0 if npass == len(results) else 1


sys.exit(main())
