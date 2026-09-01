# JuniGrid v0.15.0 — 修 Q1-Q6

## 本版修复
1. **SMAPI 更新还是失败 + 任务页右边看不见**（Q1/Q6）
   - 任务卡加 word-break/overflow-wrap，长错误信息自动换行不再超出窗口
   - SMAPI 失败时任务卡出现两个按钮：
     · 📁 打开安装目录 —— 方便你加 Windows Defender 白名单
     · 🌐 到 smapi.io 手动下载 —— 直接跳官方站下载 zip 自己装
   - 根因还是杀软锁 clrjit.dll，非代码 bug，但至少给出可操作路径

2. **胶囊挡住底部内容**（Q2）
   TaskDock 全部任务完成 8 秒后自动隐藏 + body.jg-has-task class 同步移除，
   底部 96px 让位空间跟着回收，Mod 列表可以贴到底。

3. **CurseForge/论坛 mod 不自动进 Mods**（Q3）
   Mod 管理页顶部加提示："本启动器仅支持 N 网 Mod Manager Download 一键接管，
   CurseForge / SDV 论坛的下载需手动放进 Mods 文件夹"。
   技术上：只有 nxm:// 协议能被 JG 接管，其他站点没有对应协议。

4. **每次进设置都要重新加载账号信息**（Q4）
   ConfigService 加 NexusAvatarDataUri 字段，头像 base64 缓存到 config。
   Settings 页 LoadAccountsAsync 从缓存瞬读，只有首次登录时才拉一次网络。

5. **Mods 文件夹 19 个只显示 13 个**（Q5）
   ModService.Scan 只在根目录找 manifest.json → 双层文件夹结构的 mod
   （如 zip 解压多套一层）全部漏掉。改为：根目录找不到就递归子目录里
   所有 manifest.json，每个都算一个 mod。
   现在 13 → 应该 19 个全出。

## 运行
dotnet restore && dotnet run --project JuniGrid
