# Nexus 人机验证卡死 —— 根因报告

日期：2026-11-11　｜　结论推翻了此前三个归因，以下为对照实验实测。

## 一句话结论

**问题不在 WebView2、不在网络、不在 Cloudflare 对「嵌入浏览器」的限制。
是我们自己那层「伪装成普通 Edge」的身份改写，把 `Sec-CH-UA` 削成了孤零零一个品牌，
Cloudflare 一眼判异常，托管挑战就永远停在「请稍候…」。**

## 证据矩阵

同一个 WebView2 内核（Chromium 157），窗口位置/大小/浏览器参数与 JuniGrid 完全一致
（960×720、丢屏幕外、`ShowActivated=false`、`ShowInTaskbar=false`），一次只改一个变量。
URL 用真实流程的 mod 文件页：`/stardewvalley/mods/2400?tab=files&file_id=...&nmm=1`。

| # | 条件 | 结果 | 耗时 |
|---|---|---|---|
| A | 干净 WebView2（不打任何补丁） | **过** | 13.5s |
| B | + 抹 `window.chrome.webview` / `navigator.webdriver` | **过** | 11.8s |
| C | + 8 秒后清 CF cookie 并重载 | **过** | 13.4s |
| D | + 用真实持久 profile（拷自 `nexus-web/profile`，175MB） | **过** | 22s |
| — | 不伪装对照 ×5 | **4/5 过** | — |
| **L/K** | **+ 单品牌 `Sec-CH-UA`（旧行为）** | **0/6 全卡死** | — |
| M | + 补全成规范三品牌 | **4/5 过** | 12~14s |
| FIX | 修复后的 `BrowserIdentity`（真实流程全量） | **3/3 过** | 各 11.8s |

（5 次里偶发 1 次失败是 Cloudflare 的随机硬挑战，与本问题无关，基线同样有。）

## 根因细节

WebView2 原生自报**四条**品牌：

```
Chromium/157 | Microsoft Edge WebView2/157 | Not-A.Brand/99 | Microsoft Edge/157
```

旧 `NormalizeBrands` 的逻辑是「在输入上把 WebView2 换成 Microsoft Edge」。但当身份探针
取不到数据时（`BrowserIdentity.Build` 的兜底分支），`brands` 是空列表，修补完就只剩：

```
Microsoft Edge/157          ← 单条
```

真 Edge / Chrome **恒定发三条**（熵增品牌 + Chromium + Microsoft Edge）。单条品牌是任何
正常浏览器都不会有的形态，`Sec-CH-UA` 请求头一眼就是伪造品 → Cloudflare 托管挑战不放行。

页面实际下发的品牌可以直接验：

```
旧行为（页面看到）： Microsoft Edge/157                              ← 卡死
修复后（页面看到）： Not-A.Brand/99 | Chromium/157 | Microsoft Edge/157   ← 放行
```

## 顺带纠正的两个误判

1. **「Turnstile 控件不渲染」是读错了诊断。** 这类托管挑战本来就是**隐形**的，页面上没有
   可点控件、也没有 `challenges.cloudflare.com` 的 iframe。`all:0` 不代表失败——
   **只有停在「请稍候…」才是失败**。
2. **「窗口丢在屏幕外 / 焦点为假」不是元凶。** `focus:false`、`screenX:-4953` 的条件下
   照样 12 秒过。窗口位置那套逻辑可以保留（它解决的是别的体验问题），但不是这个 bug。

## XDyno 为什么行

XDyno 是 Electron（Chromium 120，比我们的 WebView2 还旧）。查证：
- `app.asar` 里搜 `loadExtension` → **0 次**，它的浏览器扩展**没有**加载进自己的窗口；
- `chrome.exe` / `msedge.exe` / `--app=` / `load-extension` → 全 0，也没调外部浏览器；
- `%APPDATA%\xdyno-mod-manager\Partitions\nexus-auth-<uuid>\Network\Cookies` 里躺着
  `.nexusmods.com` 的 **`cf_clearance`** + `__cf_bm` —— 验证确实在它自己的 Electron 窗口里过的。

**它压根没做身份伪装**，所以没有我们这个破绽。扩展只管下载拦截（`WebSocketServer` 15 处），
跟验证无关——本机 Chrome/Edge 里根本没装那个扩展，你照样秒过，就是证据。

## 改动清单

| 文件 | 改了什么 |
|---|---|
| `JuniGrid/Services/NexusWebDownloadService.cs` | `NormalizeBrands` 重写：无条件产出规范三条，不再在输入上修补；低熵给大版本号、`fullVersionList` 给完整版本号 |
| `test/JuniGridTestHarness/NexusChallengeMemoryTests.cs` | 新增 `BrandsAlwaysComeInThreesLikeRealEdge`（4 种输入形态）+ `FullVersionListCarriesPatchVersionBrandsDoNot`，共 12 项全过 |
| `CHANGELOG.md` | 1.3.4 那节的归因改写（原文是错的，留着会误导以后排查） |
| `tools/TurnstileProbe/` | **新增对照探针**，可复现本报告全部数据 |

## 复现方式

```bash
dotnet build tools/TurnstileProbe
TurnstileProbe.exe offscreen <out.jsonl> "<mod 文件页 URL>" <profile> <秒> <extra>
# extra 可选：ua / ua2 / hide / reload（逗号分隔）
```

输出每 2 秒一行 jsonl：页面标题、URL、iframe 数、`document.hasFocus()`、
**页面实际看到的 `navigator.userAgent` 与 `userAgentData.brands`**、正文长度。
判定「过了」的条件是 `bodyLen>3000 && 不在挑战页`，而不是「有没有 Turnstile 控件」。
