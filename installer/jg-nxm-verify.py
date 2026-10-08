import sys
import os, subprocess, io, json
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
TMP = os.environ["TEMP"]
out = []

cases = [
    ("N1 notepad（Vortex 那种活着的管理器）⇒ 默认不勾", '"C:\\Windows\\System32\\notepad.exe" -d "%1"', "不勾"),
    ("N2 我们自己的 JuniGrid.exe ⇒ 默认勾", '"E:\\JuniGrid1\\JuniGrid.exe" "%1"', "勾"),
    ("N3 exe 已经不存在（旧版本留下的死路径）⇒ 默认勾", '"E:\\nope\\Gone.exe" "%1"', "勾"),
    ("N4 空命令 ⇒ 默认勾", "", "勾"),
]

for name, probe, expect in cases:
    f = os.path.join(TMP, "jg-nxm-one.txt")
    if os.path.exists(f):
        os.remove(f)
    env = dict(os.environ)
    if probe:
        env["JG_NXM_PROBE_CMD"] = probe
    else:
        env.pop("JG_NXM_PROBE_CMD", None)
    subprocess.run([EXE, "--nxm-default", f], env=env, timeout=120)
    txt = io.open(f, encoding="utf-8").read() if os.path.exists(f) else "(no report)"
    first = txt.splitlines()[0] if txt.splitlines() else ""
    got = "不勾" if "不勾" in first else "勾"
    out.append((name, got == expect, first + " ‖ 期望=" + expect))

io.open(os.path.join(TMP, "jg-nxm-report.txt"), "w", encoding="utf-8").write(
    "\n".join(f'{"PASS " if ok else "FAIL "}{n}  -- {d}' for n, ok, d in out))
print("\n".join(f'{"PASS " if ok else "FAIL "}{n}' for n, ok, d in out))
print("fails:", sum(1 for _, ok, _ in out if not ok))
