"""把 JuniGrid 窗口提到前台并截图（不点击、不动鼠标），用于确定弹窗按钮的坐标。"""
import ctypes, struct, sys, time
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

@ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
def cb(hwnd, _):
    n = user32.GetWindowTextLengthW(hwnd)
    if n:
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if "junigrid" in buf.value.lower() and user32.IsWindowVisible(hwnd):
            r = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            if r.right - r.left > 200:
                hits.append((hwnd, buf.value, (r.left, r.top, r.right, r.bottom)))
    return True

hits = []
user32.EnumWindows(cb, 0)
if not hits:
    print("window not found"); sys.exit(2)
hwnd, title, rect = hits[0]
print("rect:", rect)

user32.keybd_event(0x12, 0, 0, 0)
user32.SetForegroundWindow(hwnd)
user32.keybd_event(0x12, 0, 2, 0)
time.sleep(0.5)
print("foreground ok:", user32.GetForegroundWindow() == hwnd)

x, y = user32.GetSystemMetrics(76), user32.GetSystemMetrics(77)
w, h = user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)
hdc = user32.GetDC(0); mem = gdi32.CreateCompatibleDC(hdc); bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
gdi32.SelectObject(mem, bmp); gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)
stride = (w * 3 + 3) & ~3
buf = ctypes.create_string_buffer(stride * h)
bmi = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 24, 0, stride * h, 0, 0, 0, 0)
gdi32.GetDIBits(mem, bmp, 0, h, buf, bmi, 0)
with open(sys.argv[1], "wb") as f:
    f.write(b"BM" + struct.pack("<IHHI", 14 + 40 + len(buf), 0, 0, 54)); f.write(bmi); f.write(buf)
gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem); user32.ReleaseDC(0, hdc)
print("shot ->", sys.argv[1])
