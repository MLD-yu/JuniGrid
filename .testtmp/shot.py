"""全屏截图（纯 ctypes/GDI，无第三方依赖）: python shot.py out.bmp"""
import ctypes, struct, sys

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        user32.SetProcessDPIAware()
    except Exception:
        pass

x = user32.GetSystemMetrics(76)
y = user32.GetSystemMetrics(77)
w = user32.GetSystemMetrics(78)
h = user32.GetSystemMetrics(79)

hdc_screen = user32.GetDC(0)
hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
gdi32.SelectObject(hdc_mem, hbmp)
gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, x, y, 0x00CC0020)

stride = (w * 3 + 3) & ~3
buf = ctypes.create_string_buffer(stride * h)
bmi = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 24, 0, stride * h, 0, 0, 0, 0)
gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, bmi, 0)

path = sys.argv[1] if len(sys.argv) > 1 else "shot.bmp"
with open(path, "wb") as f:
    f.write(b"BM" + struct.pack("<IHHI", 14 + 40 + len(buf), 0, 0, 54))
    f.write(bmi)
    f.write(buf)

gdi32.DeleteObject(hbmp)
gdi32.DeleteDC(hdc_mem)
user32.ReleaseDC(0, hdc_screen)
print(f"{w}x{h} -> {path}")
