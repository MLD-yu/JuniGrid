import os, subprocess, io, json, shutil

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "JuniGridInstaller", "bin", "Release",
                   "net10.0-windows", "JuniGridSetup.exe")
if "--exe" in os.sys.argv:
    EXE = os.sys.argv[os.sys.argv.index("--exe") + 1]
try:  # Windows 控制台默认 GBK：打印中文/⇒ 会 UnicodeEncodeError，把防线自己弄崩
    os.sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    os.sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass
if not os.path.exists(EXE):
    print("FAIL 找不到被测的安装器 exe：" + EXE)
    raise SystemExit(1)

TMP = os.environ["TEMP"]
ROOT = os.path.join(TMP, "jg-oc-fx")
res = []


def check(name, ok, detail=""):
    res.append((name, bool(ok), detail))


def w(p, t):
    os.makedirs(os.path.dirname(p), exist_ok=True)
    io.open(p, "w", encoding="utf-8", newline="\n").write(t)


def run(cfg, val):
    out = os.path.join(ROOT, "rep.txt")
    if os.path.exists(out):
        os.remove(out)
    subprocess.run([EXE, "--one-click-write", cfg, val, out], timeout=120)
    return io.open(out, encoding="utf-8").read() if os.path.exists(out) else "(no report)"


def read(p):
    return io.open(p, encoding="utf-8").read()


if os.path.isdir(ROOT):
    shutil.rmtree(ROOT, ignore_errors=True)
os.makedirs(ROOT)

# ── O1 全新用户（配置文件不存在）⇒ 建出来，写上检测到的初值 ──
c = os.path.join(ROOT, "o1", "junigrid.config.json")
r = run(c, "false")
j = json.loads(read(c))
check("O1 配置还不存在（首装）⇒ 建文件并写初值 false",
      j == {"enableOneClickInstall": False} and "写成=True" in r, r.splitlines()[0])

# ── O2 配置里有别的键、但没有这个键 ⇒ 只补这一个键，其余原样 ──
c = os.path.join(ROOT, "o2", "junigrid.config.json")
w(c, json.dumps({"gamePath": "E:\\Steam\\sdvalley", "lockGameVersion": True,
                 "activeProfileByGame": {"1.6.15": "默认"}}, ensure_ascii=False, indent=2))
before = read(c)
r = run(c, "true")
j = json.loads(read(c))
check("O2 键不存在 ⇒ 补上 true，其它键一个不少",
      j.get("enableOneClickInstall") is True and j.get("gamePath") == "E:\\Steam\\sdvalley"
      and j.get("lockGameVersion") is True and (j.get("activeProfileByGame") or {}).get("1.6.15") == "默认"
      and "写成=True" in r, r.splitlines()[0])

# ── O3 用户已经在设置页关过 ⇒ 装机绝不替他改回来（这条就是去掉装机复选框的理由）──
c = os.path.join(ROOT, "o3", "junigrid.config.json")
w(c, json.dumps({"gamePath": "E:\\x", "enableOneClickInstall": False}))
before = read(c)
r = run(c, "true")
j = json.loads(read(c))
check("O3 已有值 false ⇒ 装机不覆盖（升级安装不得把用户关掉的开关悄悄打开）",
      j.get("enableOneClickInstall") is False and "写成=False" in r, r.splitlines()[0])

# ── O4 已有值 true，装机算出 false ⇒ 同样不动 ──
c = os.path.join(ROOT, "o4", "junigrid.config.json")
w(c, json.dumps({"gamePath": "E:\\y", "enableOneClickInstall": True}))
r = run(c, "false")
j = json.loads(read(c))
check("O4 已有值 true ⇒ 同样不覆盖（两个方向都不许改）",
      j.get("enableOneClickInstall") is True and j.get("gamePath") == "E:\\y", r.splitlines()[0])

# ── O5 不写的时候不该留下任何痕迹（备份/临时文件）──
c = os.path.join(ROOT, "o5", "junigrid.config.json")
w(c, json.dumps({"enableOneClickInstall": False}))
run(c, "true")
d = os.path.dirname(c)
check("O5 决定不写 ⇒ 不产生 .before-move.bak，也不留 .tmp",
      not os.path.exists(c + ".before-move.bak")
      and not any(f.endswith('.junigrid-move.tmp') for f in os.listdir(d)),
      str(sorted(os.listdir(d))))

# ── O6 坏 JSON ⇒ 不改写、不崩、不留临时文件 ──
c = os.path.join(ROOT, "o6", "junigrid.config.json")
w(c, "{ 坏 json ")
before = read(c)
r = run(c, "true")
check("O6 坏配置：不改写、不崩、没留临时文件",
      read(c) == before and "写成=False" in r
      and not os.path.exists(c + ".junigrid-move.tmp"), r.splitlines()[0])

txt = "\n".join(('PASS ' if ok else 'FAIL ') + n + ("  -- " + d if d else "") for n, ok, d in res)
io.open(os.path.join(ROOT, "report.txt"), "w", encoding="utf-8").write(txt)
bad = sum(1 for _, ok, _ in res if not ok)
print("fails: %d | total: %d" % (bad, len(res)))
raise SystemExit(1 if bad else 0)
