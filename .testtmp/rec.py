"""被动录屏：只截 JuniGrid 窗口内的弹窗区域，连续采样，供事后检查切屏动画有没有裁切内容。
用法: python rec.py <输出目录> <秒数>
"""
import ctypes, os, struct, sys, time
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

TARGET = "JuniGrid"
outdir, secs = sys.argv[1], float(sys.argv[2])
os.makedirs(outdir, exist_ok=True)


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


win = find_window()
if not win:
    print("window not found")
    sys.exit(2)
hwnd, title, r = win
# 弹窗是窗口里居中的 420 CSS px 卡片；留足上下余量以覆盖最高一屏
w = int((r.right - r.left) * 0.42)
h = int((r.bottom - r.top) * 0.72)
x0 = int((r.left + r.right) / 2 - w / 2)
y0 = int(r.top + (r.bottom - r.top) * 0.14)
print(f"rect=({r.left},{r.top},{r.right},{r.bottom}) crop=({x0},{y0},{w},{h})")


def grab(path):
    hdc = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x0, y0, 0x00CC0020)
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


t0 = time.time()
i = 0
while time.time() - t0 < secs:
    grab(os.path.join(outdir, f"f{i:03d}.bmp"))
    i += 1
    time.sleep(0.18)
print("frames:", i)
