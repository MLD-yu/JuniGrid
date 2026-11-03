"""关于页验证：① 赞助卡片多出来的「忘记备注」按钮位置；② 弹窗只有关闭按钮、二维码完整不裁切。"""
import pathlib
from playwright.sync_api import sync_playwright

root = pathlib.Path(r"C:\Users\21092\OneDrive\Desktop\JuniGrid\.testtmp\harness")
url = (root / "about.html").as_uri()

with sync_playwright() as p:
    b = p.chromium.launch()
    pg = b.new_page(viewport={"width": 820, "height": 720}, device_scale_factor=2)
    pg.goto(url)
    pg.wait_for_timeout(700)
    pg.screenshot(path=str(root / "a-card.png"))

    info = pg.evaluate("""() => {
        const btn = document.querySelector('.jg-sponsor-forgot');
        const sp  = document.querySelector('.jg-sponsor');
        const b = btn.getBoundingClientRect(), s = sp.getBoundingClientRect();
        const cs = getComputedStyle(btn);
        return {btnBottom: b.bottom, sponsorBottom: s.bottom, inline: cs.display,
                centered: Math.abs((b.left + b.right) / 2 - (s.left + s.right) / 2) < 2};
    }""")
    print("卡片内按钮:", info)

    # 弹窗：只有关闭按钮，且二维码/按钮都没被面板底边切到
    pg.evaluate("document.getElementById('dlg').style.display='flex'")
    pg.wait_for_timeout(500)
    pg.screenshot(path=str(root / "a-dialog.png"))
    box = pg.evaluate("""() => {
        const panel = document.querySelector('#dlg .jg-sponsor-panel');
        const btns  = [...panel.querySelectorAll('.jg-sponsor-btn')].map(e => e.textContent.trim());
        const qr    = panel.querySelector('.jg-sponsor-qr-shell').getBoundingClientRect();
        const act   = panel.querySelector('.jg-sponsor-actions').getBoundingClientRect();
        const pr    = panel.getBoundingClientRect();
        return {buttons: btns, cut: +(act.bottom - pr.bottom).toFixed(1),
                panelTop: Math.round(pr.top), panelH: Math.round(pr.height),
                viewportH: innerHeight, qrH: Math.round(qr.height),
                panelW: Math.round(pr.width)};
    }""")
    print("弹窗:", box)
    b.close()
print("done")
