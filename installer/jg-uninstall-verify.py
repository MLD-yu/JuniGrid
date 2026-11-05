import sys
import os, io, json, base64, shutil, subprocess, time, ctypes, string
try:  # Windows 控制台默认 GBK：打印中文/⇒ 会 UnicodeEncodeError，把防线自己弄崩（2026-10-04 打包时踩中）
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# 卸载收尾防线（2026-11-05）。
#
# 为什么要有这个文件：卸载残留的直接原因是「窗口还开着就删文件」—— 加载中的 dll 删不掉，
# 失败又被静默吞掉；窗口关掉之后旧收尾进程只重试删两个 exe，dll 就永久留在目录里。
# 修法是把清理挪到进程退出之后、并且换成一个能删掉"卸载器自己"的执行者（PowerShell）。
# 但这条路有个隐蔽坑：脚本原来以 <c>无 BOM 的 UTF-8 .ps1</c> 落盘，PS 5.1 在 CP936 机器上
# 按 ANSI 读，中文串变乱码、撑破引号 ⇒ 整段脚本语法错、一行都不执行，卸载后文件全留着。
# 所以这里同时钉两件事：脚本必须能解析执行（行为），以及脚本不许再落盘（形状）。

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
SRC = os.path.join(REPO, "JuniGrid", "UninstallWindow.xaml.cs")
TMP = os.path.join(os.environ.get("TEMP") or "/tmp", "jg-uninst-fx")
PS = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                  r"System32\WindowsPowerShell\v1.0\powershell.exe")

out = []
def check(name, ok, detail=""):
    out.append((name, bool(ok), detail))

if not os.path.exists(SRC):
    print("FAIL 找不到 " + SRC)
    sys.exit(1)
src = io.open(SRC, encoding="utf-8").read()

# ── 形状检查：怎么把脚本交给 PowerShell ────────────────────────────────
check("S1 脚本用 -EncodedCommand 内联传递，不再落 .ps1",
      "-EncodedCommand" in src and "WriteAllText(scriptPath" not in src)
check("S2 计划路径走环境变量（EncodedCommand 拿不到位置参数）",
      "$env:JG_UNINSTALL_PLAN" in src and "$args[0]" not in src.split("CleanupScript")[1])
check("S3 显式 -ExecutionPolicy Bypass（企业机默认策略会拦）",
      '"Bypass"' in src)

marker = 'private const string CleanupScript = """'
i = src.index(marker) + len(marker)
script = src[i:src.index('""";', i)]

TMPBASE = os.path.join(os.environ.get("TEMP") or "/tmp")
TMP = os.path.join(TMPBASE, "jg-uninst-fx")
if os.path.isdir(TMP):
    shutil.rmtree(TMP)

def fixture(tag, purge):
    root = os.path.join(TMP, tag)
    app = os.path.join(root, "app")
    os.makedirs(os.path.join(app, "data"))
    files = {
        "JuniGrid.exe": "exe",
        "Uninstall.exe": "un",
        "data/a.dll": "dll",
        "junigrid-files.txt": "JGMF1\nJuniGrid.exe\nUninstall.exe\ndata/a.dll\n",
        "UserFile.txt": "用户自己放的文件",
    }
    for rel, text in files.items():
        p = os.path.join(app, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        io.open(p, "w", encoding="utf-8").write(text)
    os.makedirs(os.path.join(app, "emptydir"))          # 空目录：收尾要连带收掉
    os.makedirs(os.path.join(root, "appdata"))
    io.open(os.path.join(root, "appdata", "cfg.json"), "w", encoding="utf-8").write("{}")
    os.makedirs(os.path.join(root, "localappdata"))
    os.makedirs(os.path.join(root, "legacytemp"))
    plan = {
        "ParentPid": 999999,                            # 不存在的 pid：等父进程那步立即返回
        "InstallDir": app,
        "ManifestFiles": ["JuniGrid.exe", "Uninstall.exe", "data/a.dll"],
        "PurgeUserData": purge,
        "AppDataDir": os.path.join(root, "appdata"),
        "LocalAppDataDir": os.path.join(root, "localappdata"),
        "LegacyTempDir": os.path.join(root, "legacytemp"),
        "CustomCacheDirs": [os.path.join(root, "cachedir")],
        "ReportPath": os.path.join(root, "report.txt"),
    }
    os.makedirs(os.path.join(root, "cachedir"))
    io.open(os.path.join(root, "cachedir", "x.bin"), "w", encoding="utf-8").write("x")
    plan_path = os.path.join(root, "plan.json")
    io.open(plan_path, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False))
    return root, app, plan, plan_path

def run_cleanup(plan_path):
    b64 = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
    env = dict(os.environ)
    env["JG_UNINSTALL_PLAN"] = plan_path
    return subprocess.run(
        [PS, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
         "-EncodedCommand", b64],
        env=env, timeout=180, capture_output=True, text=True,
        encoding="utf-8", errors="replace")

# ── 行为检查 A：普通卸载（保数据）────────────────────────────────────
root, app, plan, plan_path = fixture("normal", purge=False)
r = run_cleanup(plan_path)
check("U0 脚本能被 PS 5.1 解析并跑完（这正是原来挂掉的地方）",
      r.returncode == 0 and "ParserError" not in (r.stderr or ""),
      ("rc=%s " % r.returncode) + (r.stderr or "").strip()[:160])
check("U1 清单内程序文件全部删除（含 Uninstall.exe 自己）",
      not os.path.exists(os.path.join(app, "JuniGrid.exe"))
      and not os.path.exists(os.path.join(app, "Uninstall.exe"))
      and not os.path.exists(os.path.join(app, "data", "a.dll")))
check("U2 清单文件本身也删掉", not os.path.exists(os.path.join(app, "junigrid-files.txt")))
check("U3 用户自己放的文件保留（清单外一律不碰）",
      os.path.isfile(os.path.join(app, "UserFile.txt")))
check("U4 空目录被收掉", not os.path.exists(os.path.join(app, "emptydir")))
check("U5 安装目录因还留着用户文件而保留", os.path.isdir(app))
report = io.open(plan["ReportPath"], encoding="utf-8-sig").read() if os.path.exists(plan["ReportPath"]) else ""
check("U6 报告生成且如实列出保留项",
      "UserFile.txt" in report and "仍在磁盘上" in report, report.replace("\n", " / ")[:120])
check("U7 计划文件自我清理", not os.path.exists(plan_path))
check("U8 普通卸载不动缓存目录/设置",
      os.path.isdir(plan["CustomCacheDirs"][0]) and os.path.isdir(plan["AppDataDir"]))

# ── 行为检查 B：彻底删除 ────────────────────────────────────────────
root2, app2, plan2, plan2_path = fixture("purge", purge=True)
r2 = run_cleanup(plan2_path)
check("U9 彻底删除：安装目录整棵删掉（含用户文件）", not os.path.exists(app2))
check("U10 彻底删除：设置目录与缓存目录一起删",
      not os.path.exists(plan2["AppDataDir"]) and not os.path.exists(plan2["CustomCacheDirs"][0]))
check("U11 彻底删除：报告写明已清空",
      os.path.exists(plan2["ReportPath"])
      and "清空" in io.open(plan2["ReportPath"], encoding="utf-8-sig").read())

# ── 行为检查 C：父进程还活着、文件真被锁住 ⇒ 必须等它退出再删 ────────
# A/B 两段用的是 ParentPid=999999（不存在的进程），所以"等父进程退出"这条<b>根因</b>路径
# 一次都没真跑过 —— 谁把 Wait-Process 删了，上面 12 条照样全绿。这里用一个真从安装目录里
# 跑起来的 exe 当父进程：Windows 会锁住它的映像文件，先证明直接删删不掉（负对照），
# 再证明脚本会等它退出、然后把同一个文件删掉。
root3 = os.path.join(TMP, "locked")
app3 = os.path.join(root3, "app")
os.makedirs(app3)
shutil.copyfile(PS, os.path.join(app3, "JuniGrid.exe"))
io.open(os.path.join(app3, "Uninstall.exe"), "w", encoding="utf-8").write("un")
io.open(os.path.join(app3, "UserFile.txt"), "w", encoding="utf-8").write("别删我")
plan3 = {
    "ParentPid": 0,                       # 下面换成真 pid
    "InstallDir": app3,
    "ManifestFiles": ["JuniGrid.exe", "Uninstall.exe"],
    "PurgeUserData": False,
    "AppDataDir": os.path.join(root3, "appdata"),
    "LocalAppDataDir": os.path.join(root3, "localappdata"),
    "LegacyTempDir": os.path.join(root3, "legacytemp"),
    "CustomCacheDirs": [],
    "ReportPath": os.path.join(root3, "report.txt"),
}
parent = subprocess.Popen([os.path.join(app3, "JuniGrid.exe"), "-NoProfile",
                           "-Command", "Start-Sleep -Seconds 8"])
time.sleep(1.5)
locked = os.path.join(app3, "JuniGrid.exe")
try:
    os.remove(locked)
    direct_delete_failed = False
except OSError:
    direct_delete_failed = True
check("U12 负对照：父进程活着时那个 exe 真被锁住（直接删必须失败，否则这段什么都没验）",
      direct_delete_failed and parent.poll() is None and os.path.exists(locked))
plan3["ParentPid"] = parent.pid
plan3_path = os.path.join(root3, "plan.json")
io.open(plan3_path, "w", encoding="utf-8").write(json.dumps(plan3, ensure_ascii=False))
t0 = time.time()
run_cleanup(plan3_path)
took = time.time() - t0
check("U13 等父进程退出后才动手：被锁的 exe 最终删掉了，而且确实等过（>5s）",
      not os.path.exists(locked) and took > 5, "用时 %.1fs" % took)
check("U14 等的这段时间里没有误删清单外的东西",
      os.path.isfile(os.path.join(app3, "UserFile.txt")) and os.path.isdir(app3))
try:
    parent.wait(timeout=30)
except Exception:
    parent.kill()

# ── 行为检查 D：安装目录是磁盘根 ⇒ 一个文件都不许动 ──────────────────
# 彻底删除那支是整棵递归删，传进来一个盘根就等于格盘。用 subst 把一个临时文件夹挂成盘符：
# X:\ 于是真的是"磁盘根"，但闸要是失效，被删的只是那个临时文件夹，伤不到真盘。
def free_drive_letter():
    used = ctypes.windll.kernel32.GetLogicalDrives()
    return next((L for i, L in enumerate(string.ascii_uppercase) if not (used >> i) & 1), None)

drv = free_drive_letter()
if drv:
    root4 = os.path.join(TMP, "driveroot")
    app4 = os.path.join(root4, "app")
    os.makedirs(app4)
    io.open(os.path.join(app4, "keep.txt"), "w", encoding="utf-8").write("别删我")
    plan4 = {
        "ParentPid": 999999,
        "InstallDir": drv + ":\\",
        "ManifestFiles": ["keep.txt"],
        "PurgeUserData": True,
        "AppDataDir": os.path.join(root4, "appdata"),
        "LocalAppDataDir": os.path.join(root4, "localappdata"),
        "LegacyTempDir": os.path.join(root4, "legacytemp"),
        "CustomCacheDirs": [],
        "ReportPath": os.path.join(root4, "report.txt"),
    }
    os.makedirs(plan4["AppDataDir"])
    plan4_path = os.path.join(root4, "plan.json")
    io.open(plan4_path, "w", encoding="utf-8").write(json.dumps(plan4, ensure_ascii=False))
    subprocess.run(["subst", drv + ":", app4], capture_output=True, timeout=30)
    try:
        run_cleanup(plan4_path)
    finally:
        subprocess.run(["subst", drv + ":", "/D"], capture_output=True, timeout=30)
    rep4 = io.open(plan4["ReportPath"], encoding="utf-8-sig").read() if os.path.exists(plan4["ReportPath"]) else ""
    check("U15 安装目录是磁盘根 ⇒ 拒绝动手，盘符后面那个文件夹一个文件不少",
          os.path.isfile(os.path.join(app4, "keep.txt"))
          and os.path.isdir(plan4["AppDataDir"])
          and "磁盘根" in rep4, rep4.replace("\n", " / ")[:140])
else:
    check("U15 安装目录是磁盘根 ⇒ 拒绝动手", False, "找不到空闲盘符，subst 夹具搭不起来")

print("\n".join(f'{"PASS " if ok else "FAIL "}{n}' + (f"  -- {d}" if d and not ok else "")
                for n, ok, d in out))
fails = sum(1 for _, ok, _ in out if not ok)
print("fails:", fails, "| total:", len(out))
sys.exit(1 if fails else 0)
