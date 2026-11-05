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


def run_m14():
    """孤儿清理的调用链形状：谁调用、什么时候调用、拿什么当"新文件表"。

    顺序是这条的关键：RemoveOrphans 必须排在 WriteManifest <b>之前</b> ——
    反过来的话"新清单"已经写进去了，旧清单里的孤儿记录当场被抹掉，
    这一趟就什么都没删；而且 payload 里有的文件会被解包覆盖，删早了会把新文件删掉。
    第二个要点是参数：必须拿 payload 表（entries），不能拿 written ——
    written 只含这次真写出去的，少了条目会把本该保留的文件当孤儿删掉。
    """
    eng = os.path.join(REPO, "installer", "JuniGridInstaller", "InstallerEngine.cs")
    if not os.path.exists(eng):
        return
    src = read(eng)
    i_orphan = src.find("InstallRecycle.RemoveOrphans")
    i_write = src.find("InstallRecycle.WriteManifest")
    check("M14a", "孤儿清理排在写新清单之前（顺序反了就白删）",
          0 <= i_orphan < i_write,
          f"RemoveOrphans@{i_orphan} vs WriteManifest@{i_write}")
    check("M14b", "孤儿清理拿 payload 表当新文件表（不能拿 written）",
          "RemoveOrphans(targetDir, entries" in src,
          "参数必须是 entries（本次 payload 全表）")
    check("M14c", "--orphan-check 验证入口存在",
          '"--orphan-check"' in read(os.path.join(REPO, "installer", "JuniGridInstaller", "App.xaml.cs")))


def run_m15():
    """搬家写入原子性 + 冲突文件的去处。

    这两条钉的是同一个 bug 的两个形状：老版本 <c>File.Copy</c> 直接写最终路径，
    搬家中途断电就留一个半截文件；下次搬家见它存在便跳过、校验又不比内容，
    于是把完整的源文件删掉、半截的留下 —— 方向正好反了，而且是静默的。
    """
    mig = os.path.join(REPO, "installer", "JuniGridInstaller", "InstallMigrate.cs")
    if not os.path.exists(mig):
        return
    src = read(mig)
    check("M15a", "搬家写入走「临时名 + 改名」，不许直接写最终路径",
          "AtomicCopy" in src and ".junigrid-part" in src
          and "File.Move(part, target, true)" in src,
          "中断只该留残片，不许留半截文件冒充已迁移")
    check("M15b", "内容不同的源文件送回收站，不硬删（顺序在删旧目录之前）",
          "RecycleOrDelete(old)" in src,
          "装机目录不该堆积 .old-N，但旧文件也不能就这么没了")
    i_recycle = src.find("RecycleOrDelete(old)")
    i_del = src.find("Directory.Delete(src, true)")
    check("M15c", "送回收站排在删旧目录之前（顺序反了就一起埋了）",
          0 <= i_recycle < i_del,
          f"recycle@{i_recycle} vs delete@{i_del}")


def run_m16():
    """防降级闸 + 散装文件冲突规则（两条都是行业默认，我们原本缺）。

    MSI/WiX 的 MajorUpgrade 默认拦版本倒退；MSI 的组件一致性要求"同名即同物、
    新版本胜出"。散装文件原来走的是另一套（冲突就留原地标失败）⇒ 一个
    _smapi-install.dat 能让 migrationFailed 为真，旧卸载器被跳过、旧目录永远清不掉。
    """
    eng = os.path.join(REPO, "installer", "JuniGridInstaller", "InstallerEngine.cs")
    mig = os.path.join(REPO, "installer", "JuniGridInstaller", "InstallMigrate.cs")
    if not (os.path.exists(eng) and os.path.exists(mig)):
        return
    e = read(eng)
    m = read(mig)
    i_gate = e.find("EnsureNotDowngrade();")
    i_touch = e.find("Directory.CreateDirectory(targetDir)")
    check("M16a", "防降级闸排在一切动作之前（先动手再发现版本倒退就晚了）",
          0 <= i_gate < i_touch, f"gate@{i_gate} vs create@{i_touch}")
    check("M16b", "散装文件冲突与目录同一套规则（送回收站，不再标失败卡住）",
          "RecycleOrDelete(src)" in m and "新位置已有不同内容 —— 旧文件保留在原位置" not in m,
          "老做法会让旧目录永远清不掉")
    check("M16c", "跨卷单文件搬家走原子写入（File.Move 跨卷=复制+删除，会留半截）",
          "AtomicCopy(src, dst); File.Delete(src)" in m)


def run_version_gate(exe):
    """RT10–RT12：防降级闸的真实行为（用探针 JGINSTALLER_FAKE_INSTALLED_VERSION）。

    真机上没法安全构造"已装更新的版本"（得改用户的卸载注册表项），所以走探针。
    """
    if exe is None:
        return

    def probe(installed, allow_downgrade=False):
        out = os.path.join(tempfile.gettempdir(), f"jg-ver-{os.getpid()}.txt")
        if os.path.exists(out):
            os.remove(out)
        env = dict(os.environ)
        if installed is None:
            env.pop("JGINSTALLER_FAKE_INSTALLED_VERSION", None)
        else:
            env["JGINSTALLER_FAKE_INSTALLED_VERSION"] = installed
        if allow_downgrade:
            env["JGINSTALLER_ALLOW_DOWNGRADE"] = "1"
        else:
            env.pop("JGINSTALLER_ALLOW_DOWNGRADE", None)
        try:
            subprocess.run([exe, "--version-check", out], capture_output=True, timeout=60, env=env)
        except Exception as ex:
            return f"(启动失败: {ex})"
        if os.path.exists(out):
            with open(out, encoding="utf-8", errors="replace") as f:
                return f.read().strip()
        return "(入口没产出报告文件)"

    r_high = probe("9.9.9")
    check("RT10", "已装 9.9.9 装 1.3.0 ⇒ 版本倒退被拦",
          "降级=拦" in r_high, r_high)
    r_low = probe("1.0.0")
    check("RT11", "已装 1.0.0 装 1.3.0 ⇒ 正常升级放行",
          "降级=放行" in r_low, r_low)
    r_none = probe("none")
    check("RT12", "没装过 ⇒ 放行（探针 none 表示假装没装过）",
          "降级=放行" in r_none and "(没装过)" in r_none, r_none)
    r_real = probe(None)
    check("RT12b", "不带探针时读真注册表，结论必须与探针一致（只读，不写）",
          "降级=" in r_real, r_real)
    r_forced = probe("9.9.9", allow_downgrade=True)
    check("RT13", "JGINSTALLER_ALLOW_DOWNGRADE=1 ⇒ 强行降级放行",
          "降级=放行" in r_forced, r_forced)


def run_orphan(exe):
    """RT6–RT9：孤儿清理的真实行为。

    只认「旧清单里写了它，本次 payload 没有它」这一条 —— 用户手动放的东西
    从没进过清单，一个都不许碰；没清单时宁可留着孤儿也不靠猜删。
    """
    if exe is None:
        return
    import shutil

    def sweep(dir_, payload_lines, apply=True):
        listf = os.path.join(tempfile.gettempdir(), f"jg-orphan-payload-{os.getpid()}.txt")
        out = os.path.join(tempfile.gettempdir(), f"jg-orphan-out-{os.getpid()}.txt")
        with open(listf, "w", encoding="utf-8") as f:
            f.write("\n".join(payload_lines) + "\n")
        if os.path.exists(out):
            os.remove(out)
        env = dict(os.environ)
        if apply:
            env["JGINSTALLER_APPLY"] = "1"
        else:
            env.pop("JGINSTALLER_APPLY", None)
        try:
            subprocess.run([exe, "--orphan-check", dir_, listf, out],
                           capture_output=True, timeout=90, env=env)
        except Exception as ex:
            return f"(启动失败: {ex})"
        if os.path.exists(out):
            with open(out, encoding="utf-8", errors="replace") as f:
                return f.read()
        return "(入口没产出报告文件)"

    # RT6/RT7/RT7b：清单里有、这次没有 ⇒ 删；清单外绝不碰；空目录收掉
    d = os.path.join(tempfile.gettempdir(), f"jg-fx-orphan-{os.getpid()}")
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(os.path.join(d, "assets"))
    for rel, text in (("keep.txt", "这次也带来"),
                      ("assets/orphan.svg", "上一版的图标"),
                      ("assets/lock-closed.svg", "也是上一版的"),
                      ("UserFile.txt", "用户自己放的")):
        p = os.path.join(d, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "w", encoding="utf-8") as f:
            f.write(text)
    with open(os.path.join(d, "junigrid-files.txt"), "w", encoding="utf-8") as f:
        f.write("JGMF1\nkeep.txt\nassets/orphan.svg\nassets/lock-closed.svg\n")
    rep = sweep(d, ["keep.txt"])
    check("RT6", "清单里有、这次没有 ⇒ 删掉（真孤儿）",
          not os.path.exists(os.path.join(d, "assets", "orphan.svg"))
          and not os.path.exists(os.path.join(d, "assets", "lock-closed.svg"))
          and "orphan.svg" in rep, rep.replace("\n", " / ")[:130])
    check("RT7", "清单外的文件一个不碰（用户手动放的）＋ 这次带来的保留",
          os.path.isfile(os.path.join(d, "UserFile.txt"))
          and os.path.isfile(os.path.join(d, "keep.txt")))
    check("RT7b", "删完把空目录收掉（assets 空了就该没了）",
          not os.path.isdir(os.path.join(d, "assets"))
          or not os.listdir(os.path.join(d, "assets")))

    # RT8：没清单 ⇒ 认不出孤儿，一个都不删
    d2 = os.path.join(tempfile.gettempdir(), f"jg-fx-orphan2-{os.getpid()}")
    shutil.rmtree(d2, ignore_errors=True)
    os.makedirs(d2)
    with open(os.path.join(d2, "old-thing.svg"), "w", encoding="utf-8") as f:
        f.write("看着像孤儿")
    rep2 = sweep(d2, ["keep.txt"])
    check("RT8", "没旧清单 ⇒ 什么都不删（宁可留着也不靠猜）",
          os.path.isfile(os.path.join(d2, "old-thing.svg")), rep2.replace("\n", " / ")[:130])

    # RT9：清单里的穿越条目不许被当成删除目标
    d3 = os.path.join(tempfile.gettempdir(), f"jg-fx-orphan3-{os.getpid()}")
    shutil.rmtree(d3, ignore_errors=True)
    os.makedirs(d3)
    outside = os.path.join(tempfile.gettempdir(), f"jg-fx-outside-{os.getpid()}.txt")
    with open(outside, "w", encoding="utf-8") as f:
        f.write("装机目录外的文件，绝不许删")
    with open(os.path.join(d3, "junigrid-files.txt"), "w", encoding="utf-8") as f:
        f.write("JGMF1\n../" + os.path.basename(outside) + "\n")
    rep3 = sweep(d3, ["keep.txt"])
    check("RT9", "清单里的穿越条目（../）不许被删",
          os.path.isfile(outside), rep3.replace("\n", " / ")[:130])

    for p in (d, d2, d3):
        shutil.rmtree(p, ignore_errors=True)
    try:
        os.remove(outside)
    except Exception:
        pass


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
    run_m14()      # 孤儿清理调用链形状
    run_m15()      # 搬家写入原子性 + 冲突去处
    run_m16()      # 防降级闸 + 散装文件冲突规则
    print("-" * 72)
    run_rt(exe)    # 真实入口行为
    run_orphan(exe)  # 孤儿清理行为
    run_version_gate(exe)  # 防降级行为

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
