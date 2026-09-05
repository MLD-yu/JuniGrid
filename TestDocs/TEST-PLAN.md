# JuniGrid 全项目测试计划与总结

> 测试日期：2026-09-05 ｜ 版本：v1.1.0（工作区未提交变更状态下）｜ 未修改任何代码

## 一、测试对象

| 项 | 内容 |
|---|---|
| 项目 | JuniGrid（六月网格）— Blazor Hybrid (WPF + WebView2) 星露谷物语 MOD 管理器 |
| 规模 | 23 个服务类（约 6,300 行 C#）+ 7 个页面 + 6 个布局组件（约 7,700 行 razor）+ 9 个 JS 模块 + 9 个 CSS |
| 运行环境 | Windows 11 10.0.26200 / .NET 10 / 真实游戏环境（E:\Steam\...\Stardew Valley，SMAPI 4.5.2，63 个 MOD，游戏运行中） |

## 二、测试方法矩阵

| 方法 | 执行内容 | 结果载体 |
|---|---|---|
| **白盒** | ① `dotnet build` 编译验证（0 警告 0 错误）② `dotnet list package --vulnerable`（无漏洞包）③ 静态风险模式扫描（async void / sync-over-async / 空 catch / HttpClient 实例化 / Timer / Process.Start / 路径拼接）④ 精读核心服务源码：ModService、ResumableDownload、ConfigService、TaskCenterService、PlayTimeService、TranslationService、LogLineClassifier | `test-cases-whitebox.md` + 缺陷清单 |
| **黑盒** | 真机 GUI 自动化遍历（computer-use）：首页 → MOD 管理 →（列表/筛选/搜索/多选/备注对话框/边界值 24 字输入）→ Nexus（浏览/搜索/详情/分页）→ 设置 → SMAPI 日志 → 任务悬浮窗；含关闭游戏二次确认验证 | `test-cases-blackbox.md` + 执行记录 |
| **灰盒** | ① C#→JS interop 契约核对（55 个调用 vs JS 定义，55/55 匹配）② index.html 脚本/CSS 引用完整性 ③ DI 生命周期审查（全部 AddSingleton，Blazor Hybrid 下一致）④ 跨模块冲突分析（页面翻译 ↔ LogLineClassifier 日志解析；搜索 ↔ 翻译缓存） | `test-cases-graybox.md` |

## 三、测试结论（TL;DR）

- **编译/依赖**：✅ 通过（0 警告 0 错误，无易受攻击的 NuGet 包）
- **GUI 黑盒**：✅ 7 个页面全部正常加载，核心交互（导航、筛选、搜索、多选、备注、二次确认、分页、空态回退）通过；数据与真实环境强一致（63 MOD / 61 启用与 SMAPI 实际 61 mods 吻合）
- **灰盒契约**：✅ JS interop 55/55 完整；DI 结构安全
- **白盒**：发现 **2 个 P1、7 个 P2、8 个 P3** 问题（详见 `TEST-REPORT.md`），最严重的是**更新安装先删旧版且无回滚**与 **UniqueID 判重直接永久删除不入回收站**
- **已知环境噪声**：测试启动命令的引号问题产生的系统弹窗（非程序缺陷）

## 四、文件清单

| 文件 | 内容 |
|---|---|
| `TEST-PLAN.md` | 本文件：计划、方法、结论 |
| `test-cases-whitebox.md` | 白盒测试用例（WB-01~WB-52） |
| `test-cases-blackbox.md` | 黑盒功能测试用例（BB-01~BB-64） |
| `test-cases-graybox.md` | 灰盒集成测试用例（GB-01~GB-32） |
| `TEST-REPORT.md` | 实际执行结果 + 缺陷清单（含代码位置证据） |
