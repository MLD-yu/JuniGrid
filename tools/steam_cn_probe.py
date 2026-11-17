# -*- coding: utf-8 -*-
"""Steam 国内下载根治候选探针（窗口感知）。

背景：国内到 Steam CM/CDN 是间歇性死窗口；官方 CDN 按 cell 分发边缘节点，
境内节点（网宿/万网等承载）理论上不吃跨境墙。本探针在每个活窗口自动：
  1) 用 CM 列表接口判定窗口开/关；
  2) 窗口开时向 IContentServerDirectoryService/GetServersForSteamPipe 要三份节点列表
     （本机 cell 自动 /  spoof 国内 IP / 日志里出现过的 cell 170）；
  3) 对去重后的边缘逐个测：DNS(系统+阿里)、TCP+TLS+HTTP 延迟、depot chunk 路径响应码
     （dummy chunk id，404/400/403 都算"边缘活着且在serve depot 命名空间"）；
  4) 一轮一条 jsonl 留档 + 一行摘要，供后续决定"DD 优先境内节点"怎么落地。

只读探测，不写任何 Steam 状态；chunk 用全 0 dummy id，不拉真实内容。
用法：python tools/steam_cn_probe.py [--interval 60] [--hours 3] [--out 路径]
"""
import argparse
import json
import re
import socket
import ssl
import subprocess
import sys
import time
import urllib.request
import urllib.parse
from datetime import datetime, timezone

CM_URL = ("https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/"
          "?cellid=0&format=json")
DIR_URL = "https://api.steampowered.com/IContentServerDirectoryService/GetServersForSteamPipe/v1/"
CN_IP = "114.114.114.114"          # 国内公共 IP，用于 ip_override 拿境内 cell 的节点
DEPOT = "413151"
DUMMY_CHUNK = "0" * 40
QUERIES = [
    ("auto", {"cell_id": 0, "max_servers": 10, "current_connections": []}),
    ("cn-ip", {"cell_id": 0, "max_servers": 10, "current_connections": [], "ip_override": CN_IP}),
    ("cell170", {"cell_id": 170, "max_servers": 10, "current_connections": []}),
]


def http_json(url, timeout=8):
    req = urllib.request.Request(url, headers={"User-Agent": "JuniGrid-probe/1.0"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8", "replace"))


def directory_servers(payload):
    q = urllib.parse.urlencode({"input_json": json.dumps(payload), "format": "json"})
    try:
        d = http_json(DIR_URL + "?" + q)
    except Exception as ex:
        return [], f"{type(ex).__name__}: {ex}"
    servers = d.get("response", {}).get("servers", []) or []
    keep = [{k: s.get(k) for k in ("host", "vhost", "type", "cell_id", "weighted_load", "priority", "use_https")}
            for s in servers]
    return keep, None


def dns_ips(host, resolver=None):
    if resolver is None:
        try:
            return sorted({ai[4][0] for ai in socket.getaddrinfo(host, 443, socket.AF_INET)})
        except Exception:
            return []
    try:
        out = subprocess.run(["nslookup", host, resolver], capture_output=True, timeout=8).stdout.decode("gbk", "replace")
        ips = set(re.findall(r"\b(\d{1,3}(?:\.\d{1,3}){3})\b", out))
        ips.discard(resolver)
        return sorted(ips)
    except Exception:
        return []


def edge_probe(host, use_https):
    """TCP+TLS+HTTP 一次量完；返回 (ok, 毫秒, status或错误, 302去向)。"""
    import http.client
    t0 = time.perf_counter()
    try:
        if use_https:
            conn = http.client.HTTPSConnection(host, 443, timeout=6, context=ssl.create_default_context())
        else:
            conn = http.client.HTTPConnection(host, 80, timeout=6)
        conn.request("GET", f"/depot/{DEPOT}/chunk/{DUMMY_CHUNK}", headers={"Host": host, "User-Agent": "JuniGrid-probe/1.0"})
        resp = conn.getresponse()
        ms = round((time.perf_counter() - t0) * 1000)
        code = resp.status
        loc = resp.getheader("Location")
        resp.read(64)
        conn.close()
        return True, ms, code, loc
    except Exception as ex:
        return False, round((time.perf_counter() - t0) * 1000), f"{type(ex).__name__}", None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--interval", type=int, default=60)
    ap.add_argument("--hours", type=float, default=3.0)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    out = args.out or __file__.replace(".py", "-log.jsonl")
    deadline = time.time() + args.hours * 3600
    rnd = 0
    while time.time() < deadline:
        rnd += 1
        rec = {"ts": datetime.now(timezone.utc).isoformat(timespec="seconds"), "round": rnd}
        t0 = time.perf_counter()
        try:
            http_json(CM_URL, timeout=6)
            rec["window"] = "open"
            rec["cm_ms"] = round((time.perf_counter() - t0) * 1000)
        except Exception as ex:
            rec["window"] = "closed"
            rec["cm_err"] = type(ex).__name__
        if rec["window"] == "open":
            edges = {}
            for name, payload in QUERIES:
                servers, err = directory_servers(payload)
                rec["dir_" + name] = {"err": err, "servers": servers}
                for s in servers:
                    h = s.get("host")
                    if h:
                        edges.setdefault(h, {"queries": [], "cell_ids": set()})
                        edges[h]["queries"].append(name)
                        if s.get("cell_id") is not None:
                            edges[h]["cell_ids"].add(s["cell_id"])
            probes = []
            for h, meta in list(edges.items())[:14]:
                ok443, ms443, code443, loc443 = edge_probe(h, True)
                ok80, ms80, code80, loc80 = edge_probe(h, False)
                probes.append({
                    "host": h, "queries": meta["queries"], "cell_ids": sorted(meta["cell_ids"]),
                    "sys_dns": dns_ips(h), "ali_dns": dns_ips(h, "223.5.5.5"),
                    "https": {"ok": ok443, "ms": ms443, "status": code443, "loc": loc443},
                    "http": {"ok": ok80, "ms": ms80, "status": code80, "loc": loc80},
                })
            rec["edges"] = probes
        line = json.dumps(rec, ensure_ascii=False, default=list)
        with open(out, "a", encoding="utf-8") as f:
            f.write(line + "\n")
        if rec["window"] == "closed":
            print(f"[{rec['ts']}] round {rnd}: 窗口关（{rec.get('cm_err')}）", flush=True)
        else:
            good = [p for p in rec.get("edges", []) if p["https"]["ok"] or p["http"]["ok"]]
            fast = sorted(good, key=lambda p: min(p["https"]["ms"], p["http"]["ms"]))[:4]
            desc = ", ".join(
                f"{p['host']} 443:{p['https']['status']}/{p['https']['ms']}ms 80:{p['http']['status']}/{p['http']['ms']}ms"
                for p in fast)
            print(f"[{rec['ts']}] round {rnd}: 窗口开 cm={rec['cm_ms']}ms 边缘{len(good)}/{len(rec.get('edges', []))}活 最快: {desc}", flush=True)
        time.sleep(args.interval)
    print("probe done", flush=True)


if __name__ == "__main__":
    main()