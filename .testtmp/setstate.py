"""改写赞助记账的 lastPromptedAt（真机测试用）: python setstate.py "2026-10-26T23:00:00+08:00" """
import json, os, sys

p = os.path.join(os.environ["APPDATA"], "JuniGrid", "sponsor-prompt.json")
with open(p, encoding="utf-8") as f:
    d = json.load(f)
d["lastPromptedAt"] = sys.argv[1]
if len(sys.argv) > 2 and sys.argv[2] == "clear":
    d["supportedAt"] = None
    d["disabled"] = False
with open(p, "w", encoding="utf-8") as f:
    json.dump(d, f, ensure_ascii=False, indent=2)
print(json.dumps(d, ensure_ascii=False))
