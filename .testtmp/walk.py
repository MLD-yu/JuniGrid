"""自动走一遍赞助弹窗：文案页 → 收款码页 → 忘记备注页 → 返回，并在每次点击后
取 180ms / 380ms / 1000ms 三帧，用来检查切屏高度动画有没有把内容裁掉。
用法: python walk.py <输出前缀>
"""
import ctypes, struct, sys, time
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

TARGET = "JuniGrid"
prefix = sys.argv[1]


def find_window():
    hits = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if TARGET.lower() in buf.value.lower() and user32.IsWindowVisible(hwnd):
                r = wintypes.RECT()
                user32.GetWindowRect(hwnd, ctypes.byref(r))
                if r.right - r.left > 200:
                    hits.append((hwnd, buf.value, r))
        return True

    user32.EnumWindows(cb, 0)
    return hits[0] if hits else None


def shoot(path):
    x, y = user32.GetSystemMetrics(76), user32.GetSystemMetrics(77)
    w, h = user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)
    hdc = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)
    stride = (w * 3 + 3) & ~3
    buf = ctypes.create_string_buffer(stride * h)
    bmi = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 24, 0, stride * h, 0, 0, 0, 0)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, bmi, 0)
    with open(path, "wb") as f:
        f.write(b"BM" + struct.pack("<IHHI", 14 + 40 + len(buf), 0, 0, 54))
        f.write(bmi)
        f.write(buf)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, hdc)


def click_and_capture(tag, rx, ry):
    win = find_window()
    if not win:
        print("window not found")
        return False
    hwnd, title, r = win
    px = int(r.left + (r.right - r.left) * rx)
    py = int(r.top + (r.bottom - r.top) * ry)
    # SetForegroundWindow 会被前台锁静默忽略：先按一下 ALT 再抢，成功率才高
    user32.keybd_event(0x12, 0, 0, 0)
    user32.SetForegroundWindow(hwnd)
    user32.keybd_event(0x12, 0, 2, 0)
    time.sleep(0.35)
    fg = user32.GetForegroundWindow()
    if fg != hwnd:
        user32.ShowWindow(hwnd, 6)        # SW_MINIMIZE
        time.sleep(0.15)
        user32.ShowWindow(hwnd, 9)        # SW_RESTORE
        user32.keybd_event(0x12, 0, 0, 0)
        user32.SetForegroundWindow(hwnd)
        user32.keybd_event(0x12, 0, 2, 0)
        time.sleep(0.35)
    print('foreground ok:', user32.GetForegroundWindow() == hwnd)
    user32.SetCursorPos(px, py)
    time.sleep(0.15)
    t0 = time.time()
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.04)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    for ms in (180, 380, 1000):
        wait = ms / 1000.0 - (time.time() - t0)
        if wait > 0:
            time.sleep(wait)
        shoot(f"{prefix}-{tag}-{ms}.bmp")
    print(f"{tag}: click=({px},{py})")
    return True


# 先把鼠标挪开，避免 hover 态干扰
click_and_capture("step1", 0.607, 0.590)   # 支持作者
time.sleep(0.6)
click_and_capture("step2", 0.489, 0.756)   # 忘记备注
time.sleep(0.6)
click_and_capture("step3", 0.461, 0.705)   # 返回
print("done")
