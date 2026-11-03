"""BMP(24bpp) -> PNG，纯标准库。用法: python bmp2png.py in.bmp out.png"""
import struct, sys, zlib


def read_bmp(path):
    d = open(path, "rb").read()
    off = struct.unpack_from("<I", d, 10)[0]
    w, h = struct.unpack_from("<ii", d, 18)
    bpp = struct.unpack_from("<H", d, 28)[0]
    assert bpp == 24, bpp
    top_down = h < 0
    h = abs(h)
    stride = (w * 3 + 3) & ~3
    rows = []
    for y in range(h):
        base = off + y * stride
        row = d[base:base + w * 3]
        rows.append(b"".join(row[i + 2:i + 3] + row[i + 1:i + 2] + row[i:i + 1]
                             for i in range(0, w * 3, 3)))
    if not top_down:
        rows.reverse()
    return w, h, rows


def write_png(path, w, h, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(t, data):
        c = struct.pack(">I", len(data)) + t + data
        return c + struct.pack(">I", zlib.crc32(t + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 6))
    png += chunk(b"IEND", b"")
    open(path, "wb").write(png)


src, dst = sys.argv[1], sys.argv[2]
w, h, rows = read_bmp(src)
write_png(dst, w, h, rows)
print(f"{w}x{h} -> {dst}")
