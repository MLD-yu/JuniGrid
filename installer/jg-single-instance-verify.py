#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
安装器单实例闸的回归防线（对应 2026-10-18 的报障：Win11 双击安装包「没反应」）。

为什么必须跑真进程：这个 bug 的形状是"代码找错了窗口列表"——第二次双击时去翻
本进程的 App.Current.Windows，而那一刻窗口还没造出来，列表恒空，于是既不拉起旧窗口、
也不弹窗，直接静默退出。文本上看不出任何异常，组件夹具也全绿；只有"起两个实例、
把第一个最小化、看第二个有没有把它拉回屏上"能证明闸的出口是可见的。

检查分两类：
  · 行为（S1-S5）—— 真的起两个进程，用 Win32 读窗口状态
  · 形状（S6）—— 断言源码不再用本进程的窗口列表找旧实例

用法：
    python jg-single-instance-verify.py [--exe <JuniGridSetup.exe>]
不传 --exe 时按 bin/Release/net10.0-windows/JuniGridSetup.exe 找。

退出码：0 全过，1 有失败。
"""

import ctypes
import io
import os
import shutil
import subprocess
import sys
import tempfile
import time

try:  # Windows 控制台默认 GBK：打印中文会 UnicodeEncodeError，把防线自己弄崩
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
    print("FAIL 找不到被测的安装器 exe：" + EXE)
    raise SystemExit(1)


def safe_rmtree(path):
    """只删自己建的夹具目录：必须落在系统 TEMP 下、名字以 jg-si- 开头，否则拒绝。
    （这条防线曾经有过一次 finally 里递归删掉了整个仓库，所以这里不留"顺手删"的余地。）"""
    real = os.path.realpath(path)
    tmp = os.path.realpath(tempfile.gettempdir())
    if not (real.lower().startswith(tmp.lower() + os.sep)
            and os.path.basename(real).startswith("jg-si-")):
        raise RuntimeError("拒绝删除非夹具路径：" + real)
    shutil.rmtree(real, ignore_errors=True)


# 私有 TEMP：安装器的日志名（jgsetup-log.txt）是全机器共用的一串，别的会话同时在跑
# 就会把对方的行读进来（2026-10-18 第一次跑就被并行会话的 7 次启动污染成红）。
FIXTURE = os.path.join(tempfile.gettempdir(), "jg-si-fixture")
if os.path.isdir(FIXTURE):
    safe_rmtree(FIXTURE)
os.makedirs(FIXTURE)
ENV = dict(os.environ)
ENV["TEMP"] = FIXTURE
ENV["TMP"] = FIXTURE
LOG = os.path.join(FIXTURE, "jgsetup-log.txt")
SRC = os.path.join(HERE, "JuniGridInstaller", "App.xaml.cs")
APPXAML = os.path.join(HERE, "JuniGridInstaller", "App.xaml")

user32 = ctypes.windll.user32
EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
SW_HIDE, SW_MINIMIZE, SW_RESTORE = 0, 6, 9

res = []


def check(name, ok, detail=""):
    res.append((name, bool(ok), detail))


def log_bytes():
    if not os.path.exists(LOG):
        return b""
    return io.open(LOG, "rb").read()


def since(off):
    return log_bytes()[off:].decode("utf-8", errors="replace")


def wait_until(fn, timeout, step=0.4):
    end = time.time() + timeout
    while time.time() < end:
        v = fn()
        if v:
            return v
        time.sleep(step)
    return None


TITLE = "JuniGrid 安装"    # 与 App.WindowTitle 逐字相等；S9 钉住这条
GW_OWNER = 4


def caption(h):
    n = user32.GetWindowTextLengthW(ctypes.c_void_p(h))
    buf = ctypes.create_unicode_buffer(max(n, 1) + 2)
    user32.GetWindowTextW(ctypes.c_void_p(h), buf, len(buf))
    return buf.value


def windows_of(pid, strict):
    """strict=True 用产品那套判据（顶层、无 owner、标题逐字相等、正在显示或被最小化）；
    strict=False 只要求「正在显示或被最小化」——差集就是会被误当安装窗口的东西。"""
    out = []

    def cb(h, _l):
        owner = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(ctypes.c_void_p(h), ctypes.byref(owner))
        if owner.value != pid:
            return True
        if not (user32.IsWindowVisible(ctypes.c_void_p(h)) or user32.IsIconic(ctypes.c_void_p(h))):
            return True
        if strict:
            if user32.GetWindow(ctypes.c_void_p(h), GW_OWNER):
                return True
            if caption(h) != TITLE:
                return True
        out.append((h, caption(h)))
        return not strict      # 严判据只要第一个；宽判据全收集

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def find_window_of(pid):
    w = windows_of(pid, True)
    return w[0][0] if w else 0


procs = []


def launch():
    p = subprocess.Popen([EXE], env=ENV)
    procs.append(p)
    return p


def kill_all():
    for p in procs:
        try:
            p.kill()
            p.wait(timeout=10)
        except Exception:
            pass


# ══════════════════ 行为检查：真的起两个实例 ══════════════════
base_off = len(log_bytes())
p1 = launch()

h1 = wait_until(lambda: find_window_of(p1.pid), 120) or 0
# ContentRendered 比句柄晚到一瞬间，先等到它再读，否则这条前置会被自己抢跑误判
rendered = bool(wait_until(lambda: "ContentRendered" in since(base_off), 60))
check("前置 实例#1 起来了并画出窗口（否则整条检查是空的）",
      h1 != 0 and rendered, "hwnd=%s ContentRendered=%s" % (h1, rendered))
if h1 == 0:
    print("FAIL 实例#1 没有窗口，后面全没法测")
    kill_all()
    safe_rmtree(FIXTURE)
    raise SystemExit(1)

# ── S1 正向对照：最小化必须真的生效，不然"没再最小化"是白过的 ──
user32.ShowWindowAsync(ctypes.c_void_p(h1), SW_MINIMIZE)
time.sleep(1.5)
check("S1 正向对照：实例#1 的窗口已被最小化（IsIconic=True）",
      user32.IsIconic(ctypes.c_void_p(h1)), "hwnd=%d IsIconic=%s" % (h1, user32.IsIconic(ctypes.c_void_p(h1))))

# ── 第二次双击：这就是报障用户做的动作 ──
off2 = len(log_bytes())
p2 = launch()
raised = wait_until(lambda: "拉起旧窗口" in since(off2), 60)
new2 = since(off2)

# ── S2 闸的出口必须可见：旧窗口从最小化回到屏上 ──
time.sleep(1.0)
still_min = bool(user32.IsIconic(ctypes.c_void_p(h1)))
check("S2 第二次双击把旧窗口从最小化拉回屏上（IsIconic=False）",
      not still_min, "最小化残留=%s 日志=%r" % (still_min, new2.strip().splitlines()[-1:]))

# ── S3 不许静默：这一轮窗口是在的，必须写明拉起了哪个窗口 ──
check("S3 第二个实例在日志里交代了拉起哪个窗口（不允许既不拉起也不弹窗）",
      "拉起旧窗口 pid=" in new2, (new2.strip().splitlines() or ["(空)"])[-1])

# ── S4 闸还在：第二个实例自己退场，而不是再开一个安装器 ──
gone = wait_until(lambda: p2.poll() is not None, 30)
check("S4 闸没被改坏：第二个实例自己退场（没有第二个安装窗口）",
      bool(gone), "p2=%s p1=%s" % (p2.poll(), p1.poll()))

# ── S5 不再"闪一下就没了"：第二个实例根本不该造窗口 ──
check("S5 第二个实例不再造出窗口再关掉（日志里没有它那一行的 ctor begin）",
      "InstallerWindow..ctor begin" not in new2,
      "新日志片段=%r" % (new2.replace("\r\n", " | ")[:220],))

# ── S6 兜底路径也得可见：把 #1 的窗口彻底藏掉 ⇒ 第二个实例必须弹窗 ──
# 先还原再隐藏：最小化状态会留在 IsIconic 上，那样"窗口不见了"这个前提就是假的
#（2026-10-18 变异跑那一轮正是这么让 S6 拿到一个隐藏但仍最小化的窗口，白过成"找到了"）。
user32.ShowWindowAsync(ctypes.c_void_p(h1), SW_RESTORE)
time.sleep(1.0)
user32.ShowWindowAsync(ctypes.c_void_p(h1), SW_HIDE)
time.sleep(1.0)
strict_left = windows_of(p1.pid, True)
loose_left = windows_of(p1.pid, False)
every = []


def _every(h, _l):
    o = ctypes.c_ulong()
    user32.GetWindowThreadProcessId(ctypes.c_void_p(h), ctypes.byref(o))
    if o.value == p1.pid:
        every.append((h, caption(h)))
    return True


user32.EnumWindows(EnumWindowsProc(_every), 0)
check("S6a 夹具正向对照：隐藏后按产品的判据已无任何窗口过关",
      not strict_left, "宽判据下还在的=%s ‖ 产品判据=%s" % (loose_left, strict_left))
# 隐藏不改标题，所以 h1 自己仍带着我们的标题 —— 要断言的是"没**别人**冒这个标题"。
# 这一轮实测同进程 11 个顶层窗口里有 9 个标题非空（SystemResourceNotifyWindow /
# MediaContextNotificationWindow / CiceroUIWndFrame / MSCTFIME UI / Default IME / WISPTIS），
# 只要求"标题非空"就会挑到它们 —— 2026-10-18 打包那轮正是这么被骗走的。
fake = [w for w in every if w[1] == TITLE and w[0] != h1]
other_titled = [w for w in every if w[1] and w[1] != TITLE]
check("S6a2 除安装窗口本身外，该进程没有别的顶层窗口冒用安装标题（全部顶层=%d，其中标题非空又不是我们的=%d）"
      % (len(every), len(other_titled)), not fake,
      "冒用标题的=%s" % (fake,))
off3 = len(log_bytes())
p3 = launch()
fell = wait_until(lambda: "没找到它的窗口 弹窗告知" in since(off3), 60)
new3 = since(off3)
check("S6 找不到旧窗口时走弹窗，而不是静默退出",
      bool(fell), (new3.strip().splitlines() or ["(空)"])[-1])
alive3 = p3.poll() is None
check("S6b 弹窗是模态的：用户按确定之前那个进程不会自己跑掉",
      bool(fell) and alive3, "p3 还在=%s" % alive3)

# ══════════════════ 形状检查 ══════════════════
# 只断言代码，不断言散文：这个 bug 的说明注释里就写着 "Current.Windows" 和 "StartupUri"
#（2026-10-18 第一版就是这么被自己的注释骗成 FAIL 的），先剥掉注释再比。
def strip_cs(text):
    return "\n".join(ln.split("//")[0] for ln in text.splitlines())


def strip_xaml(text):
    import re
    return re.sub(r"<!--.*?-->", "", text, flags=re.S)


src = strip_cs(io.open(SRC, encoding="utf-8-sig").read()) if os.path.exists(SRC) else ""
appx = strip_xaml(io.open(APPXAML, encoding="utf-8-sig").read()) if os.path.exists(APPXAML) else ""
check("S7 形状：源码不许再用本进程的窗口列表找旧实例（Current.Windows）",
      "Current.Windows" not in src, "")
check("S8 形状：App.xaml 不带 StartupUri（窗口由 OnStartup 显式创建）",
      "StartupUri" not in appx, "")

# 标题是这个防线的判据本身：找窗口、验窗口都靠它。XAML 和 C# 各写一份就会漂移，
# 漂了就是"永远找不到安装窗口"⇒ 每次双击都退到弹窗那条路上。
WINXAML = os.path.join(HERE, "JuniGridInstaller", "InstallerWindow.xaml")
wx = io.open(WINXAML, encoding="utf-8-sig").read() if os.path.exists(WINXAML) else ""
check("S9 形状：标题只有一份（InstallerWindow.xaml 绑 App.WindowTitle，不另写字面量）",
      "{x:Static local:App.WindowTitle}" in wx and 'Title="JuniGrid 安装"' not in wx, "")

kill_all()
left = [p for p in procs if p.poll() is None]
check("收尾 自己起的实例全收掉了（不给用户留看不见的窗口）",
      not left, "残留 pids=%s" % [p.pid for p in left])
safe_rmtree(FIXTURE)

txt = "\n".join(("PASS " if ok else "FAIL ") + n + ("  -- " + str(d) if d else "")
                for n, ok, d in res)
print(txt)
bad = sum(1 for _, ok, _ in res if not ok)
print("fails: %d | total: %d" % (bad, len(res)))
raise SystemExit(1 if bad else 0)
