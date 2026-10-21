# 隐私政策 / Privacy Policy

生效日期：2026-10-21
Effective: October 21, 2026

JuniGrid 是一个在你自己的电脑上运行的开源 Windows 桌面程序（GPL-3.0）。项目没有自建服务器，也没有账号体系。**我们不会收集、上传或保存任何属于你的数据。** 下面列出程序为了完成你要求的功能而必须进行的外部通信。

JuniGrid is an open-source Windows desktop application (GPL-3.0) that runs entirely on your computer. The project operates no server and has no account system. **We do not collect, upload, or store any data about you.** The sections below list the external communication the application performs in order to do what you ask of it.

## 本机保存的数据 / Data stored on your computer

| 内容 / Content | 位置 / Location |
|---|---|
| 设置、Nexus OAuth access/refresh token（`junigrid.config.json`） | `%APPDATA%\JuniGrid\` |
| 运行日志 `juni-grid.log`（可能包含本机路径与错误信息） | `%APPDATA%\JuniGrid\` |
| 翻译缓存（仅在开启翻译时） | `%APPDATA%\JuniGrid\junigrid.translate-cache.json` |
| 下载缓存、SMAPI 安装包、Mods 备份、WebView2 数据、游戏版本缓存 | 默认在安装目录下；安装目录不可写、位于同步盘或临时目录时回落到 `%LOCALAPPDATA%\JuniGrid\cache`；可在设置中指定 |
| 切换版本时收起的存档、Steam userdata 备份 | `%LOCALAPPDATA%\JuniGrid\` |
| Steam 登录票据（仅在使用 Steam 下载并扫码登录后，由 DepotDownloader 保存） | DepotDownloader 的本地配置目录 |
| `nxm://` 协议关联与既有 Mod 管理器的原命令备份 | 当前用户注册表：`HKCU\Software\Classes\nxm`、`HKCU\Software\JuniGrid` |

这些数据只留在本机，不会自动上传。卸载程序不会清理 `%APPDATA%\JuniGrid`；如需彻底删除，请在卸载后手动删除上方列出的目录，并移除上述当前用户注册表项。

## 会离开你电脑的请求 / Outbound requests

| 目的 / Purpose | 接收方 / Recipient | 发送内容 / Data sent | 时机 / When |
|---|---|---|---|
| 浏览与下载 Mod、读取账号信息 | Nexus Mods（`nexusmods.com`、`api.nexusmods.com`、`users.nexusmods.com`、图片 CDN） | 你的 Nexus OAuth token；你要访问的 Mod 与文件编号 | 你浏览、下载或以 Nexus 身份操作时 |
| Mod 兼容性预查 | smapi.io（`https://smapi.io/api/v4.0.0/mods`） | 已安装 Mod 的 id、updateKeys、已安装版本号（只发带有 updateKeys 的条目） | 每次打开 Mod 页时自动一次 |
| 检查更新、下载组件 | GitHub（`api.github.com`、`github.com`） | 无账号或个人数据 | 检查更新、下载发布文件时 |
| 自动翻译（默认关闭） | Google 翻译、Microsoft Edge 翻译、有道 | 待翻译的页面文本（如 Mod 描述、更新日志） | 仅在你打开翻译开关后 |
| Steam 下载（可选） | Steam（`api.steampowered.com` 及 Steam CM 服务器） | 扫码登录后的 Steam 登录票据、要下载的游戏与清单编号 | 仅在你使用 Steam 下载功能时 |
| 打开外部链接 | YouTube、抖音、Nexus 等 | 由你的浏览器发起 | 你点击链接时 |

普通 HTTPS 请求会携带你的 IP 地址与客户端标识，这是网络通信的固有部分；第三方对数据的处理适用它们各自的隐私政策。

## 我们不做什么 / What we do not do

- 不接入广告、统计分析、崩溃上报或遥测 SDK。
- 不把 Mod 列表、日志、配置或使用记录上传到项目服务器——项目没有服务器。
- 不向第三方出售、出租或共享你的数据。
- 不读取与所请求功能无关的文件。

## 你的控制 / Your controls

- 自动翻译默认关闭，可随时关闭；关闭时会清空本地翻译缓存。
- 可随时退出 Nexus 登录，删除本机保存的 OAuth token。
- 不使用 Steam 下载功能时，程序不会与 Steam 通信。
- 删除上方列出的本地目录与当前用户注册表项即可清除全部本地数据。

## 第三方服务 / Third-party services

- Nexus Mods: https://www.nexusmods.com/
- smapi.io: https://smapi.io/
- GitHub: https://github.com/
- Google: https://policies.google.com/privacy
- Microsoft: https://privacy.microsoft.com/
- 有道: https://www.youdao.com/
- Steam: https://store.steampowered.com/

## 变更与联系 / Changes and contact

本政策随仓库更新，历史版本见 git 提交记录。问题请通过 GitHub Issues 联系维护者：
https://github.com/MLD-yu/JuniGrid/issues
