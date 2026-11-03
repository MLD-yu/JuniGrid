"""点一下弹窗里的按钮，然后以最快速度连拍面板区域，用于观察换屏动画中间帧。
用法: python burst.py <x> <y> <x0> <y0> <x1> <y1> <out前缀>
"""
import ctypes, struct, sys, time
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

hdc = user32.GetDC(0)


def shoot_region(x0, y0, x1, y1, path):
    w, h = x1 - x0, y1 - y0
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x0, y0, 0x00CC0020)
    stride = (w * 3 + 3) & ~3
    buf = ctypes.create_string_buffer(stride * h)
    bmi = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 24, 0, stride * h, 0, 0, 0, 0)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, bmi, 0)
    with open(path, "wb") as f:
        f.write(b"BM" + struct.pack("<IHHI", 14 + 40 + len(buf), 0, 0, 54)); f.write(bmi); f.write(buf)
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem)


cx, cy = int(sys.argv[1]), int(sys.argv[2])
x0, y0, x1, y1 = map(int, sys.argv[3:7])
prefix = sys.argv[7]

user32.SetCursorPos(cx, cy)
time.sleep(0.12)
user32.mouse_event(0x0002, 0, 0, 0, 0)
time.sleep(0.03)
user32.mouse_event(0x0004, 0, 0, 0, 0)
t0 = time.perf_counter()
n = 0
while time.perf_counter() - t0 < 0.9:
    ms = int((time.perf_counter() - t0) * 1000)
    shoot_region(x0, y0, x1, y1, f"{prefix}-{ms:03d}.bmp")
    n += 1
print("frames:", n)
