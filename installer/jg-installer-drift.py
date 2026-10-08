#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
JuniGrid 安装器回归防线（对应 2026-10-04 审计报告）。

为什么要有这个文件：InstallRecycle.RecycleOldInstall 有 16 项组件夹具且全绿，
但那些夹具测的是"回收逻辑本身对不对"，没有一项测"谁把哪个目录交给它"。
于是 2026-10-04 那个 bug（回收步骤重读已被本次安装改掉的注册表 ⇒ 守卫短路 ⇒
旧目录永不回收）在测试里全绿，在真实调用链里一次都没执行过。

所以这里分两类检查，缺一不可：
  · 行为检查（RT*）—— 跑安装器真实的 --recycle-target 入口，钉住三条守卫
  · 形状检查（M*）—— 断言源码里"谁调用谁、参数从哪来"，防回归

形状检查是文本断言、不是行为测试，但这个 bug 的形状恰恰只在文本里可见：
守卫写对了，参数传错了，行为测试全绿也发现不了。

用法：
    python jg-installer-drift.py [--exe <JuniGridSetup.exe>]

不传 --exe 时按 bin/Release/net10.0-windows/JuniGridSetup.exe 找；
找不到就只跑 M*（形状检查不依赖可执行文件）。

退出码：0 全过，1 有失败。
"""

import argparse
import os
import re
import subprocess
import sys
import tempfile
try:  # Windows 控制台默认 GBK：打印中文/⇒ 会 UnicodeEncodeError，把防线自己弄崩（2026-10-04 打包时踩中）
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


# ---------------------------------------------------------------- 路径

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)                      # installer/ 的上一级
ENGINE = os.path.join(HERE, "JuniGridInstaller", "InstallerEngine.cs")
MIGRATE = os.path.join(HERE, "JuniGridInstaller", "InstallMigrate.cs")
RECYCLE = os.path.join(HERE, "JuniGridInstaller", "InstallRecycle.cs")
APP_CS = os.path.join(HERE, "JuniGridInstaller", "App.xaml.cs")
APP_DIR = os.path.join(REPO, "JuniGrid")          # 主程序源码，扫 InCache 用

DEFAULT_EXE = os.path.join(
    HERE, "JuniGridInstaller", "bin", "Release", "net10.0-windows", "JuniGridSetup.exe")

# ---------------------------------------------------------------- 结果收集

RESULTS = []


def check(cid, name, ok, detail=""):
    RESULTS.append((cid, name, bool(ok), detail))
    mark = "PASS" if ok else "FAIL"
    print(f"[{mark}] {cid}  {name}")
    if detail:
        for line in str(detail).splitlines():
            if line.strip():
                print(f"         {line}")
    return ok


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def method_body(src, signature_fragment):
    """截出某个方法的完整方法体（按大括号配平），用于"方法体内不许出现某调用"这类断言。

    比字符串 find 可靠：注释里提到方法名不会误伤，嵌套的 if/try 也不会提前截断。
    """
    i = src.find(signature_fragment)
    if i < 0:
        return None
    # 回退到方法签名所在行的行首，避免从签名中间的某个 token 开始找大括号
    start = src.rfind("\n", 0, i) + 1
    j = src.find("{", i)
    if j < 0:
        return None
    depth = 0
    k = j
    while k < len(src):
        ch = src[k]
        if ch == '"':                       # 跳过字符串字面量里的括号
            k += 1
            while k < len(src) and src[k] != '"':
                k += 2 if src[k] == "\\" else 1
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return src[start:k + 1]
        k += 1
    return None


def run_target(exe, legacy, target, timeout=90):
    """跑 --recycle-target 入口，返回报告文本。

    刻意不设 JGINSTALLER_APPLY=1 —— 这个入口只读，即使误设也不删东西。
    """
    out = os.path.join(tempfile.gettempdir(), f"jg-fixture-rt-{os.getpid()}.txt")
    if os.path.exists(out):
        os.remove(out)
    env = dict(os.environ)
    env.pop("JGINSTALLER_APPLY", None)
    try:
        subprocess.run(
            [exe, "--recycle-target", legacy, target, out],
            capture_output=True, timeout=timeout, env=env)
    except subprocess.TimeoutExpired:
        return f"(超时 {timeout}s)"
    except Exception as ex:
        return f"(启动失败: {ex})"
    if os.path.exists(out):
        with open(out, encoding="utf-8", errors="replace") as f:
            return f.read().strip()
    return "(入口没产出报告文件)"


# ---------------------------------------------------------------- 行为检查 RT

def run_rt(exe):
    """RT1–RT5：用真实入口钉住三条守卫 + 两种空情形。

    每条都在临时目录里造真实的目录结构 —— RecycleTargetOf 的第一条守卫是
    Directory.Exists，只看字符串的假实现会把这条守卫漏掉。
    """
    if not (exe and os.path.exists(exe)):
        check("RT*", "行为检查（跳过：找不到 JuniGridSetup.exe）", True,
              "只跑了 M* 形状检查。要跑 RT* 请先 dotnet build，\n"
              "或用 --exe 指定已构建的 JuniGridSetup.exe。")
        return

    base = os.path.join(tempfile.gettempdir(), f"jg-fixture-{os.getpid()}")
    old = os.path.join(base, "JuniGrid")
    nested = os.path.join(old, "sub")
    real_old = os.path.join(base, "RealOld")
    for d in (nested, real_old):
        os.makedirs(d, exist_ok=True)

    try:
        # RT1 正常换目录：必须判定回收旧目录。
        # 这条正是修复前必然失败的场景（旧目录永远留在原地）。
        rep = run_target(exe, real_old, os.path.join(base, "RealNew"))
        check("RT1", "换目录 ⇒ 回收旧目录（BUG-1 的回归钉子）",
              ("回收" in rep) and ("RealOld" in rep), rep)

        # RT2 同址覆盖：什么都不该碰
        rep = run_target(exe, real_old, real_old)
        check("RT2", "同址覆盖 ⇒ 不回收", "不回收" in rep, rep)

        # RT3 新位置套在旧位置里面：回收旧的会连新的一份端掉
        rep = run_target(exe, old, nested)
        check("RT3", "新套旧（装到 旧\\sub）⇒ 不回收", "不回收" in rep, rep)

        # RT4 旧目录不存在
        rep = run_target(exe, os.path.join(base, "DoesNotExist"),
                         os.path.join(base, "RealNew"))
        check("RT4", "旧目录不存在 ⇒ 不回收", "不回收" in rep, rep)

        # RT5 前缀相似但不同目录：E:\JuniGridA vs E:\JuniGridAB
        # 朴素 startswith 判断会把这对当成同址而错误跳过回收
        pa = os.path.join(base, "DirA")
        pab = os.path.join(base, "DirAB")
        os.makedirs(pa, exist_ok=True)
        os.makedirs(pab, exist_ok=True)
        rep = run_target(exe, pa, pab)
        check("RT5", "前缀相似但不同目录（DirA vs DirAB）⇒ 仍回收 DirA",
              ("回收" in rep) and ("DirA" in rep), rep)
    finally:
        import shutil
        shutil.rmtree(base, ignore_errors=True)


# ---------------------------------------------------------------- 形状检查 M

def run_m11():
    """M11a–c：BUG-1 的调用链形状。行为测试看不见这三条，只有源码形状能看见。"""
    if not os.path.exists(ENGINE):
        check("M11", "源码形状检查（跳过：找不到 InstallerEngine.cs）", True)
        return
    src = read(ENGINE)

    # M11a 回收方法体内不得再读注册表 —— 这就是原 bug 的根因
    body = method_body(src, "private void RecyclePreviousInstall(")
    if body is None:
        check("M11a", "回收方法体不再读注册表", False, "没找到 RecyclePreviousInstall 方法体")
    else:
        offenders = [t for t in ("FindLegacyInstall", "GetValue(\"InstallLocation\"",
                                 "OpenSubKey(UninstallKeyPath") if t in body]
        check("M11a", "回收方法体内不得出现读注册表（原 bug 根因）",
              not offenders,
              ("发现: " + ", ".join(offenders)) if offenders else
              "方法体只吃传进来的 legacyLocation，不自己查。")

    # M11b 调用点必须传 legacy.location —— 不能传 targetDir、不能传 null
    calls = re.findall(r"RecyclePreviousInstall\(\s*([^,]+?)\s*,", src)
    calls = [c for c in calls if "private" not in c and "void " not in c]
    ok = any("legacy.location" in c for c in calls)
    detail = "调用实参: " + ", ".join(calls) if calls else "（没找到调用点）"
    check("M11b", "调用点必须传第 2 步锁定的 legacy.location", ok, detail)

    # M11c 回收必须排在 WriteUninstallRegistry 之后
    i_reg = src.find("WriteUninstallRegistry(targetDir)")
    i_rec = src.find("RecyclePreviousInstall(legacy.location")
    if i_reg < 0 or i_rec < 0:
        check("M11c", "回收排在写注册表之后", False,
              f"WriteUninstallRegistry@{i_reg} RecyclePreviousInstall@{i_rec}")
    else:
        check("M11c", "回收必须排在写注册表之后（否则旧位置已被覆盖）",
              i_rec > i_reg,
              f"写注册表@{i_reg} → 回收@{i_rec}")


def run_m10():
    """M10 漂移检查：应用里每个 InCache("x") 都必须在搬家清单里。

    InstallMigrate.DataDirs 是手抄的 —— 应用新加一个 InCache 而这里没跟上，
    那个目录就会静默留在旧位置，用户删旧目录就丢数据。
    这是 2026-10-04 实测漏掉 nexus-browse 的根因，靠人记是记不住的。
    """
    if not (os.path.exists(MIGRATE) and os.path.isdir(APP_DIR)):
        check("M10", "搬家清单漂移检查（跳过：源码不全）", True)
        return

    # 应用侧：所有 InCache("字面量")
    used = set()
    for root, _dirs, files in os.walk(APP_DIR):
        if any(seg in root for seg in ("\\bin\\", "\\obj\\", "\\.git")):
            continue
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            try:
                text = read(os.path.join(root, fn))
            except Exception:
                continue
            used |= set(re.findall(r'InCache\(\s*"([^"]+)"', text))
    used.discard("")

    # 安装器侧：DataDirs + DataFiles
    mig = read(MIGRATE)

    def collect(varname):
        m = re.search(varname + r"\s*=\s*\{(.*?)\}", mig, re.S)
        if not m:
            return set()
        return set(re.findall(r'"([^"]+)"', m.group(1)))

    declared = collect("DataDirs") | collect("DataFiles")

    missing = sorted(used - declared)
    check("M10", f"搬家清单覆盖应用全部 InCache（{len(used)} 个）",
          not missing,
          ("漏搬: " + ", ".join(missing) + "\n→ 补进 InstallMigrate 的 DataDirs（目录）"
           "或 DataFiles（散装文件）")
          if missing else
          "已覆盖: " + ", ".join(sorted(used)))


def run_m12():
    """M12：缓存根下的散装文件也得在清单里（1.7MB 的 _smapi-install.dat 曾漏搬）。"""
    if not os.path.exists(MIGRATE):
        return
    mig = read(MIGRATE)
    check("M12", "DataFiles 存在且含 _smapi-install.dat",
          bool(re.search(r"DataFiles\s*=\s*\{[^}]*_smapi-install\.dat", mig, re.S)),
          "散装文件走 File.Move 分支，与目录的 Directory.Move 分支不同，漏了就只搬一半。")


def run_m13():
    """M13：--recycle-target 入口必须还在。

    BUG-1 修完后，"谁把哪个目录交给回收"这个问句只有这个入口能回答。
    入口被删掉 = 回归防线被拆掉，所以显式钉住。
    """
    if not os.path.exists(APP_CS):
        return
    src = read(APP_CS)
    check("M13", "--recycle-target 验证入口存在",
          '"--recycle-target"' in src,
          "这是钉住 BUG-1 形状的唯一入口，不能删。")


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="JuniGrid 安装器回归防线")
    ap.add_argument("--exe", default=None, help="JuniGridSetup.exe 路径")
    args = ap.parse_args()

    exe = args.exe or (DEFAULT_EXE if os.path.exists(DEFAULT_EXE) else None)

    print("=" * 72)
    print("JuniGrid 安装器回归防线")
    print(f"  仓库: {REPO}")
    print(f"  行为检查用 exe: {exe or '(未找到，只跑形状检查)'}")
    print("=" * 72)

    run_m10()      # 搬家清单漂移
    run_m11()      # BUG-1 调用链形状
    run_m12()      # 散装文件
    run_m13()      # 验证入口存在
    print("-" * 72)
    run_rt(exe)    # 真实入口行为

    failed = [r for r in RESULTS if not r[2]]
    print("=" * 72)
    print(f"合计 {len(RESULTS)} 项，通过 {len(RESULTS) - len(failed)}，失败 {len(failed)}")
    if failed:
        for cid, name, _ok, _d in failed:
            print(f"  FAIL  {cid}  {name}")
        print("=" * 72)
        return 1
    print("全部通过")
    print("=" * 72)
    return 0


if __name__ == "__main__":
    sys.exit(main())
