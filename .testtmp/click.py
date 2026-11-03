"""在 JuniGrid 窗口内按相对坐标点击，然后截全屏。
用法: python click.py <相对x 0-1> <相对y 0-1> <输出bmp>
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


rx, ry, out = float(sys.argv[1]), float(sys.argv[2]), sys.argv[3]
win = find_window()
if not win:
    print("window not found")
    sys.exit(2)
hwnd, title, r = win
px = int(r.left + (r.right - r.left) * rx)
py = int(r.top + (r.bottom - r.top) * ry)
print(f"window='{title}' rect=({r.left},{r.top},{r.right},{r.bottom}) click=({px},{py})")
user32.SetForegroundWindow(hwnd)
time.sleep(0.3)
user32.SetCursorPos(px, py)
time.sleep(0.2)
user32.mouse_event(0x0002, 0, 0, 0, 0)   # left down
time.sleep(0.05)
user32.mouse_event(0x0004, 0, 0, 0, 0)   # left up
time.sleep(1.5)
shoot(out)
print("shot ->", out)
