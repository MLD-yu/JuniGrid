"""模拟记账文件被写坏（GBK 编码的中文 JSON，等价于今天日志里的 0xE8 报错）"""
import os

p = os.path.join(os.environ["APPDATA"], "JuniGrid", "sponsor-prompt.json")
raw = '{"lastPromptedAt": "2026-10-26T23:00:00+08:00", "备注": "来自另一个副本"}'
with open(p, "wb") as f:
    f.write(raw.encode("gbk"))
print("wrote", p, "bytes:", len(raw.encode("gbk")))
