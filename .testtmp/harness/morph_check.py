"""验证换屏高度弹簧：只要新一屏可见（opacity>0.05），它的底部按钮就不能超出裁剪框。
cut = 按钮底边 - 视口底边；cut > 0 就是"按钮被切掉"。
"""
import pathlib
from playwright.sync_api import sync_playwright

root = pathlib.Path(r"C:\Users\21092\OneDrive\Desktop\JuniGrid\.testtmp\harness")
url = (root / "morph.html").as_uri()


def report(tag, samples, shot=None, pg=None):
    visible = [s for s in samples if s["op"] > 0.05]
    worst = max((s["cut"] for s in visible), default=None)
    first = next((s for s in samples if s["op"] > 0.05), None)
    print(f"--- {tag} ---")
    print(f"  内容开始可见: t={first['t']}ms  vp={first['vp']}  cut={first['cut']}")
    print(f"  可见期间最大 cut = {worst}（>0 即为被裁）")
    for s in samples:
        if s["t"] % 60 < 12:
            print(f"    t={s['t']:>3}  op={s['op']:.2f}  vp={s['vp']:>3}  cut={s['cut']:>6}")
    if shot and pg:
        pg.screenshot(path=str(root / shot))


with sync_playwright() as p:
    b = p.chromium.launch()
    pg = b.new_page(viewport={"width": 760, "height": 700}, device_scale_factor=2)
    pg.goto(url)
    pg.wait_for_timeout(600)

    # 首开：无离场层，不播弹簧
    pg.evaluate("__firstOpen(0)")
    pg.wait_for_timeout(400)
    pg.screenshot(path=str(root / "m-first.png"))

    # 0 → 1（长高 130px，最容易裁）
    pg.evaluate("__go(1)")
    report("0→1 长高", pg.evaluate("__probe()"), "m-grow-200.png", pg)
    pg.wait_for_timeout(300)

    # 1 → 2（收缩 43px）
    pg.evaluate("__go(2)")
    report("1→2 收缩", pg.evaluate("__probe()"), "m-shrink-200.png", pg)
    pg.wait_for_timeout(300)

    # 2 → 1（返回，长高）
    pg.evaluate("__go(1)")
    report("2→1 返回", pg.evaluate("__probe()"))
    pg.wait_for_timeout(300)
    pg.screenshot(path=str(root / "m-final.png"))
    b.close()
print("done")
