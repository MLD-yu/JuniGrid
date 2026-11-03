"""抓换屏动画的中间帧，肉眼确认"卡片在长高，内容已完整"。"""
import pathlib
from playwright.sync_api import sync_playwright

root = pathlib.Path(r"C:\Users\21092\OneDrive\Desktop\JuniGrid\.testtmp\harness")

with sync_playwright() as p:
    b = p.chromium.launch()
    pg = b.new_page(viewport={"width": 760, "height": 700}, device_scale_factor=2)
    pg.goto((root / "morph.html").as_uri())
    pg.wait_for_timeout(600)
    pg.evaluate("__firstOpen(0)")
    pg.wait_for_timeout(500)
    pg.screenshot(path=str(root / "m0.png"))

    for tag, step, shots in (("grow", 1, (205, 300, 520)), ("shrink", 2, (205, 300, 520)), ("back", 1, (205, 300))):
        pg.evaluate(f"__go({step})")
        t = 0
        for ms in shots:
            pg.wait_for_timeout(ms - t)
            t = ms
            pg.screenshot(path=str(root / f"mf-{tag}-{ms}.png"))
        pg.wait_for_timeout(750)
    b.close()
print("done")
