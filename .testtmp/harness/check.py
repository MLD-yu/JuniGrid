"""离屏验证赞助弹窗换屏：用真实 shell.css 渲染三屏，逐帧量按钮底边与卡片底边的关系。
cut = 按钮底边 - 卡片底边；cut > 0 就是"按钮被切到卡片外面"。
"""
import pathlib
from playwright.sync_api import sync_playwright

root = pathlib.Path(r"C:\Users\21092\OneDrive\Desktop\JuniGrid\.testtmp\harness")
url = (root / "dialog.html").as_uri()
out = root

with sync_playwright() as p:
    b = p.chromium.launch()
    pg = b.new_page(viewport={"width": 760, "height": 640}, device_scale_factor=2)
    pg.goto(url)
    pg.wait_for_timeout(800)
    pg.screenshot(path=str(out / "h-step0.png"))
    print("step0", pg.evaluate("__cardBox()"))

    for i, tag in ((1, "step1"), (2, "step2"), (1, "back1")):
        pg.evaluate(f"__show({i})")
        t = 0
        for ms in (60, 140, 260, 700):
            pg.wait_for_timeout(ms - t)
            t = ms
            box = pg.evaluate("__cardBox()")
            print(f"{tag} @{ms}ms  cut={box['cut']}  panelBottom={box['panelBottom']} actionsBottom={box['actionsBottom']}")
            if ms in (140, 700):
                pg.screenshot(path=str(out / f"h-{tag}-{ms}.png"))
        pg.wait_for_timeout(200)
    b.close()
print("done")
