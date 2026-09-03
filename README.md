# JuniGrid

一个面向 **星露谷物语** 的桌面 Mod 管理器与启动器，基于 .NET（WPF + Blazor WebView2）构建。
本仓库为中文原版；英文版见 [JuniGrid-en](https://github.com/MLD-yu/JuniGrid-en)。

## 安全说明

- 源码中不内嵌任何 API Key / 密钥。Nexus 凭据由用户在登录时输入，仅保存在本机当前用户的本地配置文件中。
- 所有下载严格以当前登录的 Nexus 用户身份进行；应用不代理、不分发、不为其他用户缓存 Mod 文件。

## 功能

- **Nexus Mods 集成** —— 支持 SSO（`wss://sso.nexusmods.com`）、OAuth2 授权码 + PKCE（回调地址 `http://localhost:49162/auth/callback`）与个人 API Key 三种登录方式；通过 Nexus GraphQL API 浏览 Mod，以登录用户本人的身份通过官方下载接口获取文件。
- **一键安装** —— 注册为 `nxm://` 协议处理器，Nexus 页面的 "Mod Manager Download" 按钮直接拉起 JuniGrid。
- **Mod 管理** —— 扫描 Mods 文件夹（含嵌套 manifest）、启用/禁用/卸载、依赖检查、存档与配置管理。
- **任务中心** —— 下载、安装、更新统一进度视图，支持断点续传。
- **SMAPI 支持** —— 安装/更新 SMAPI，SMAPI 控制台接入应用内日志查看器。
- **自动更新** —— 标题栏 logo 旁的更新按钮：仅当 GitHub Releases 出现新版本时出现（logo 绿色圆环图标），悬停显示版本，点击后环形进度从 12 点方向顺时针填充，填满自动弹出静默安装；下载中再次点击可取消，已下载部分断点保留。

## 编译与运行

环境要求：Windows 10 1809+，[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（含 Windows Desktop 工作负载）。

```bash
# 还原 + 编译
dotnet build JuniGrid.sln

# 仓库根目录直接运行
dotnet run --project JuniGrid/JuniGrid.csproj

# 或用 Visual Studio 2022+ 打开 JuniGrid.sln 按 F5

# 发布版（输出位于 JuniGrid/bin/Release/net10.0-windows10.0.17763.0/）
dotnet publish JuniGrid/JuniGrid.csproj -c Release
```
## 下载或安装被拦截怎么办

小众桌面软件、未签名安装包有时会被浏览器、Windows Defender 或 SmartScreen 提示风险。请确认安装包来自官方 GitHub Release，文件名是JuniGrid-cn-v1.1.0-setup.exe。

1. 浏览器下载栏提示风险时，打开下载列表，点这条下载右侧的`···`三个点，选择`保留` / `仍要保留` / `显示更多` 后继续保留。
2. Windows SmartScreen 弹出蓝色拦截窗口时，点`更多信息`，再点`仍要运行`。
3. 如果杀毒软件明确显示木马、高危或已经隔离，不要强行运行;删除该文件后重新从官方 GitHub Release 下载，仍然异常请带截图向作者反馈



## 支持赞助

如果对你有帮助，可以请作者喝杯咖啡吗

<img src="https://github.com/MLD-yu/JuniGrid/releases/download/v1.1.0/sponsor-cards.png" alt="赞助收款码" width="640" />

<sub>MLD/MLD（\*禺）——扫码前请确定收款人信息</sub>

## 许可

保留所有权利。本项目同时作为 Nexus Mods API 团队注册审核的源码材料。

