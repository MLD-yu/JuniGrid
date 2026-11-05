import sys
import os, io, json, base64, shutil, subprocess
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

# ── 行为检查 B：彻底卸载 ────────────────────────────────────────────
root2, app2, plan2, plan2_path = fixture("purge", purge=True)
r2 = run_cleanup(plan2_path)
check("U9 彻底卸载：安装目录整棵删掉（含用户文件）", not os.path.exists(app2))
check("U10 彻底卸载：设置目录与缓存目录一起删",
      not os.path.exists(plan2["AppDataDir"]) and not os.path.exists(plan2["CustomCacheDirs"][0]))
check("U11 彻底卸载：报告写明已清空",
      os.path.exists(plan2["ReportPath"])
      and "清空" in io.open(plan2["ReportPath"], encoding="utf-8-sig").read())

print("\n".join(f'{"PASS " if ok else "FAIL "}{n}' + (f"  -- {d}" if d and not ok else "")
                for n, ok, d in out))
fails = sum(1 for _, ok, _ in out if not ok)
print("fails:", fails, "| total:", len(out))
sys.exit(1 if fails else 0)
