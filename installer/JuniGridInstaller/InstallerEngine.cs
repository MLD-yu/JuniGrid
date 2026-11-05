using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using SharpCompress.Compressors.LZMA;

namespace JuniGridInstaller;

/// <summary>一次进度上报：Status 为界面文案，Fraction ∈ [0,1]；DoneBytes/TotalBytes 仅在释放文件阶段有效。</summary>
public sealed record InstallProgress(string Status, double Fraction, long DoneBytes = 0, long TotalBytes = 0);

/// <summary>
/// 安装核心流程（对齐旧 Inno Setup 脚本 installer.iss 的行为）：
///   1. 结束正在运行的 JuniGrid（必须在动旧版卸载器之前，否则文件锁会让它失败）；
///   2. 发现旧版（同 AppId 的 Inno 安装）→ 静默卸载 unins000.exe；自己的 GUI 卸载向导不在此列，直接跳过；
///   3. 释放内嵌 payload.lz（publish\sc 自包含输出的 LZMA 固实容器，见 PayloadTool）到目标目录；
///   4. 生成 uninstall.ps1 + 写 HKCU 卸载注册表（沿用旧 AppId，控制面板可卸载）；
///   5. 开始菜单 + 可选桌面快捷方式。
/// 全程 HKCU / %LocalAppData%，与 PrivilegesRequired=lowest 的旧版一致，无需管理员。
/// </summary>
public sealed class InstallerEngine
{
    /// <summary>旧 Inno 脚本里的 AppId（installer.iss: AppId={{7E1B2C64-...}）。</summary>
    public const string LegacyKey = "{7E1B2C64-9A4D-4C0E-9F61-3A5D8B2C4E10}_is1";
    private static readonly string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + LegacyKey;

    private const string ResourceName = "JuniGridInstaller.payload.lz";

    public static readonly string Version =
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    /// <summary>装完了、但有事没办成（目前只有"数据目录没搬成"）。完成页必须显示它 ——
    /// 只写进 jgsetup-log.txt 等于没说：没人会去 %TEMP% 翻日志。</summary>
    public string? MigrationWarning { get; private set; }

    /// <summary>旧位置回收后还剩东西（不是程序文件）。以前只写日志，用户会以为"怎么还有这么多"。</summary>
    public string? OldDirLeftoverNote { get; private set; }

    /// <summary>默认安装目录：优先沿用旧版安装位置，否则 %LocalAppData%\Programs\JuniGrid。</summary>
    public static string GetDefaultInstallDir()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            if (k?.GetValue("InstallLocation") is string loc && loc.Length > 4 && Directory.Exists(loc))
                return loc;
        }
        catch { }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Programs", "JuniGrid");
    }

    /// <summary>现在浏览器点「Mod Manager Download」会交给谁 —— 读 nxm 那棵注册表的 open\command，
    /// 剥出 exe 路径。有一个【还活着的、不是我们的】程序在接（Vortex / MO2 / NMM），
    /// 那就是用户已经在用的管理器，装 JuniGrid 不该把它顶掉：多数用户根本不知道 nxm 是什么，
    /// 他们只知道「我点下载，平时那个程序会开」。
    /// 没人接、或接的那个 exe 已经不存在（旧版本留下的死路径）⇒ 算空位，默认接管。</summary>
    public static bool ForeignNxmHandlerInUse()
    {
        // 测试逃生口：设了这条就用它判，一个注册表键都不读不动 ——
        // "别的管理器在接"这条分支必须能在只有一台机器的情况下证伪，不然它就是没验过的假设。
        // 注意"设成空串"= 模拟"注册表里那条命令是空的"，必须和"没设"区分开：
        // GetEnvironmentVariable 对两者都返回 null/""，区分不了 —— 结果就是夹具在这台
        // 真装着 Vortex 的机器上永远红（2026-11-05 实测 N4）。
        if (TryGetNxmProbe(out var probe)) return IsForeignCommand(probe);

        foreach (var cmd in NxmCommandsToCheck())
            if (IsForeignCommand(cmd))
            {
                App.Log($"nxm 有别的管理器在接: {cmd} ⇒ 装机默认不接管");
                return true;
            }
        return false;
    }

    /// <summary>探针是否被设置过（设成空串也算设过）。</summary>
    private static bool TryGetNxmProbe(out string probe)
    {
        probe = "";
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (string.Equals(e.Key as string, "JG_NXM_PROBE_CMD", StringComparison.OrdinalIgnoreCase))
            {
                probe = e.Value as string ?? "";
                return true;
            }
        }
        return false;
    }

    /// <summary>这条命令指向的是"别人"吗：exe 还在、而且不是我们的 JuniGrid.exe。</summary>
    internal static bool IsForeignCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var exe = ExeOf(cmd);
        if (exe.Length == 0 || !File.Exists(exe)) return false;
        return !Path.GetFileName(exe).Equals("JuniGrid.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证入口：把"装机时那个勾会怎么默认"打在报告里（只读注册表，什么都不写）。</summary>
    public static string DescribeNxmDefault()
    {
        var sb = new System.Text.StringBuilder();
        var hasProbe = TryGetNxmProbe(out var probe);
        var foreign = ForeignNxmHandlerInUse();
        sb.AppendLine("装机默认勾「Nexus 网页上的下载按钮打开 JuniGrid」 = " + (foreign ? "不勾" : "勾")
            + (hasProbe ? "  【按 JG_NXM_PROBE_CMD 判，没读注册表】" : ""));
        if (hasProbe) sb.AppendLine("  探针命令: " + (probe.Length == 0 ? "(空)" : probe));
        foreach (var cmd in NxmCommandsToCheck())
        {
            if (string.IsNullOrWhiteSpace(cmd)) { sb.AppendLine("  （无）"); continue; }
            var exe = ExeOf(cmd);
            sb.AppendLine($"  命令: {cmd}");
            sb.AppendLine($"    exe: {exe} ‖ 存在={File.Exists(exe)} ‖ 是我们={(Path.GetFileName(exe)
                .Equals("JuniGrid.exe", StringComparison.OrdinalIgnoreCase))}");
        }
        return sb.ToString();
    }

    /// <summary>两个要看的命令：① 现在注册表里那条；② 我们自己压掉别人时存下的那条原命令。
    /// 第二条是给"升级安装"准备的 —— 上一版抢过注，注册表里只剩我们，光看 ① 就永远以为用户没有别的管理器，
    /// 而那条被压掉的 Vortex 命令还在，说明这台机器上确实有他装了的东西。</summary>
    private static List<string?> NxmCommandsToCheck()
    {
        var list = new List<string?>();
        foreach (var hive in new[] { Registry.ClassesRoot, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
                list.Add(k?.GetValue(null) as string);
            }
            catch { list.Add(null); }
        }
        try
        {
            using var bk = Registry.CurrentUser.OpenSubKey(@"Software\JuniGrid");
            list.Add(bk?.GetValue("NxmDisplacedCommand") as string);
        }
        catch { list.Add(null); }
        return list;
    }

    /// <summary>从注册表命令里剥出 exe 路径：带引号取引号内，不带取第一个空格前。</summary>
    private static string ExeOf(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : "";
        }
        var sp = cmd.IndexOf(' ');
        return sp > 0 ? cmd[..sp] : cmd;
    }

    public (string? uninstallCmd, string? location) FindLegacyInstall()    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            var cmd = (k?.GetValue("QuietUninstallString") ?? k?.GetValue("UninstallString")) as string;
            var loc = k?.GetValue("InstallLocation") as string;
            return (string.IsNullOrWhiteSpace(cmd) ? null : cmd, loc);
        }
        catch { return (null, null); }
    }

    /// <summary>已装版本（注册表 DisplayVersion）；没装过/读不到返回 null。
    /// <para><c>JGINSTALLER_FAKE_INSTALLED_VERSION</c> 是给回归防线用的探针（与
    /// <c>JG_NXM_PROBE_CMD</c> 同款）：防降级这条闸只能靠"已装版本高于本包"才触发，
    /// 真机上没法安全构造，探针让它可测。传 <c>none</c> 表示"假装没装过"。
    /// 只读取，不写注册表。</para></summary>
    public string? InstalledDisplayVersion()
    {
        var probe = Environment.GetEnvironmentVariable("JGINSTALLER_FAKE_INSTALLED_VERSION");
        if (!string.IsNullOrWhiteSpace(probe))
        {
            var p = probe.Trim();
            return p.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : p;
        }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return k?.GetValue("DisplayVersion") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// 防降级：拿旧安装包盖住新版本是被拦的。
    ///
    /// <para>MSI / WiX 的 <c>MajorUpgrade</c> 默认就是这个行为（旧版安装包跑过来会报
    /// "已有另一版本"），Chrome / VS Code 那类自动更新也不允许版本倒退。
    /// 这里只拦<b>真降级</b>（已装版本高于本安装包）；同版本放行，当修复安装处理。</para>
    /// <para>要强行降级：设 <c>JGINSTALLER_ALLOW_DOWNGRADE=1</c>。</para>
    /// <para><b>注册表可能是残局</b>：卸载后手删目录、整目录搬走、盘符变了，那条卸载登记都还在。
    /// 这种"已装 1.3.44"没有对应的程序可覆盖，拦下来等于让用户永远装不上 —— 提示里的
    /// "先卸载当前版本"也没有卸载器可点。所以只有登记的那份程序确实还在，才算真的"已装更高版本"。
    /// 探针（FAKE_INSTALLED_VERSION）构造的是完整的"已装"状态，不套这条前提，否则夹具在
    /// 没有装机记录的 CI 上会自己变红。</para>
    /// </summary>
    internal (bool Blocked, string Reason, string? Installed) EvaluateDowngrade()
    {
        if (Environment.GetEnvironmentVariable("JGINSTALLER_ALLOW_DOWNGRADE") == "1")
            return (false, "JGINSTALLER_ALLOW_DOWNGRADE=1", null);
        var probed = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("JGINSTALLER_FAKE_INSTALLED_VERSION"));
        var installed = InstalledDisplayVersion();
        if (string.IsNullOrWhiteSpace(installed)) return (false, "没装过", null);
        if (CompareVersion(installed, Version) <= 0)
            return (false, $"已装 {installed} ≤ 本包 {Version}", installed);
        if (!probed)
        {
            var (_, loc) = FindLegacyInstall();
            if (string.IsNullOrWhiteSpace(loc) || !File.Exists(Path.Combine(loc, "JuniGrid.exe")))
                return (false, $"注册表说已装 {installed}，但登记目录里没有 JuniGrid.exe（残局，不按降级拦）", installed);
        }
        return (true, $"已装 {installed} > 本包 {Version}", installed);
    }

    private void EnsureNotDowngrade()
    {
        var (blocked, reason, installed) = EvaluateDowngrade();
        if (!blocked)
        {
            App.Log("防降级闸放行：" + reason);
            return;
        }
        throw new InvalidOperationException(
            $"这台机器上已装的是 {installed}，不能用 {Version} 的安装包覆盖（版本倒退）。"
            + "确实要降级，请先卸载当前版本，或设置环境变量 JGINSTALLER_ALLOW_DOWNGRADE=1 后重试。");
    }

    /// <summary>版本比较：先按宽松版本号比（认 v 前缀、2~4 段、-预发布后缀），
    /// 两边都认不出才退回字符串比较。
    /// <para>为什么不能只靠 <see cref="System.Version.TryParse"/>：注册表里的 DisplayVersion
    /// 不是我们控制的（手改、老安装器、别的工具），它一失败就退回字符串比较，而
    /// <c>"v1.3.0" &gt; "1.3.0"</c>（'v' 排在 '1' 后面）会把<b>同一个版本</b>判成版本倒退，
    /// 用户直接被关在门外、只能卸载。</para></summary>
    internal static int CompareVersion(string a, string b)
    {
        var left = LooseVersion.Parse(a);
        var right = LooseVersion.Parse(b);
        if (left is not null && right is not null) return left.Value.CompareTo(right.Value);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>宽松版本号。预发布（<c>1.3.0-beta</c>）小于同号正式版。</summary>
    private readonly record struct LooseVersion(int Major, int Minor, int Build, int Revision, bool Prerelease)
        : IComparable<LooseVersion>
    {
        public int CompareTo(LooseVersion o)
        {
            var c = Major.CompareTo(o.Major); if (c != 0) return c;
            c = Minor.CompareTo(o.Minor); if (c != 0) return c;
            c = Build.CompareTo(o.Build); if (c != 0) return c;
            c = Revision.CompareTo(o.Revision); if (c != 0) return c;
            if (Prerelease == o.Prerelease) return 0;
            return Prerelease ? -1 : 1;
        }

        public static LooseVersion? Parse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim().TrimStart('v', 'V');
            var plus = t.IndexOf('+');
            if (plus >= 0) t = t[..plus];
            var dash = t.IndexOf('-');
            var pre = dash >= 0;
            if (pre) t = t[..dash];
            var parts = t.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return null;
            var nums = new int[4];
            for (var i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                    return null;
            return new LooseVersion(nums[0], nums[1], nums[2], nums[3], pre);
        }
    }

    public Task InstallAsync(string targetDir, bool desktopShortcut,
        IProgress<InstallProgress> progress, CancellationToken ct)
        => Task.Run(() => InstallCore(targetDir, desktopShortcut, progress, ct), ct);

    private void InstallCore(string targetDir, bool desktopShortcut,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        // 最先做：版本倒退必须拦在一切动作之前（MSI/WiX 的 MajorUpgrade 默认就是这么拦的）
        EnsureNotDowngrade();
        targetDir = Path.GetFullPath(targetDir);
        App.Log($"[装机] 开始 → {targetDir} ‖ 本包 v{Version}"
              + $" ‖ 已装 v{InstalledDisplayVersion() ?? "(没装过)"}"
              + $" ‖ 探针={Environment.GetEnvironmentVariable("JGINSTALLER_FAKE_INSTALLED_VERSION") ?? "-"}");
        // 选 Program Files 这类目录时，CreateDirectory 本身就会先抛 UnauthorizedAccessException ——
        // 友好提示得罩住它，不能只罩后面那次写探测（否则用户看到的是原始系统异常）。
        try { Directory.CreateDirectory(targetDir); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法创建所选安装目录。请选择当前账户有写入权限的文件夹。", ex);
        }
        EnsureWritableDirectory(targetDir);

        // 先关掉正在运行的 JuniGrid，再动旧版卸载器 —— 顺序反了文件被占用，
        // 旧版卸载器会弹「文件正在使用」错误或卡在拦截页，静默卸载等于失败。
        progress.Report(new InstallProgress("正在关闭正在运行的 JuniGrid…", 0.02));
        CloseRunningApp();

        var legacy = FindLegacyInstall();
        App.Log($"[装机] 旧安装: location={legacy.location ?? "(无)"} ‖ 卸载入口={(legacy.uninstallCmd is null ? "(无)" : "有")}");

        // 先确认安装包和跨盘迁移所需空间，再动旧目录。否则空间不足可能发生在数据已搬走之后。
        long payloadBytes;
        using (var res = OpenResource())
            _ = ReadPayloadHeader(res, out payloadBytes);
        var migrationBytes = Environment.GetEnvironmentVariable("JGINSTALLER_NOMIGRATE") == "1"
            ? 0 : InstallMigrate.EstimateAdditionalBytes(legacy.location, targetDir,
                InstallMigrate.DefaultConfigPath());
        var freeNow = (Path.GetPathRoot(targetDir) is { } root && new DriveInfo(root) is { } d)
            ? d.AvailableFreeSpace / (1024 * 1024) : -1;
        App.Log($"[装机] 空间预检: payload={payloadBytes / (1024 * 1024)} MB"
              + $" ‖ 迁移额外≈{migrationBytes / (1024 * 1024)} MB"
              + $" ‖ 需要 {(payloadBytes + migrationBytes + 256L * 1024 * 1024) / (1024 * 1024)} MB"
              + $" ‖ 目标盘可用 {freeNow} MB");
        EnsureFreeSpace(targetDir, checked(payloadBytes + migrationBytes + 256L * 1024 * 1024));

        var migrationFailed = false;

        // 换目录安装＝搬家：先搬数据 → 再解包 → 最后清旧目录。顺序不能反 ——
        // 先删旧的就没东西可搬了；不搬的话新装看不见 886 MB 版本缓存和 WebView2 里的登录态，
        // 用户只会觉得"我换了个目录，怎么一切重来"。
        // 必须排在旧版卸载器前面：老卸载器是延时 <c>rd /s /q</c> 整目录删，数据还躺在里面就会被它端走。
        // 测试逃生口：JGINSTALLER_NOMIGRATE=1 跳过（与 NOLEGACY 同款）。
        if (Environment.GetEnvironmentVariable("JGINSTALLER_NOMIGRATE") != "1")
        {
            var mig = InstallMigrate.Run(legacy.location, targetDir,
                InstallMigrate.DefaultConfigPath(), dryRun: false, progress);
            App.Log($"[搬家] {mig.OldDir ?? "(无旧目录)"} → {mig.NewDir}"
                  + $" ‖ 搬成 {mig.Moved.Count} 项[{string.Join("、", mig.Moved)}]"
                  + $" ‖ {mig.Bytes / (1024 * 1024)} MB"
                  + $" ‖ 配置改指新位置={mig.ConfigPatched}"
                  + (mig.Failed.Count > 0 ? $" ‖ 没搬成 {mig.Failed.Count} 项[{string.Join("、", mig.Failed)}]" : ""));
            if (mig.Moved.Count > 0)
                progress.Report(new InstallProgress(
                    $"已把 {mig.Moved.Count} 个数据目录搬到新位置（{mig.Bytes / (1024 * 1024)} MB）"
                    + (mig.ConfigPatched ? "，缓存目录也改指新位置" : ""), 0.04));
            // 搬不动不是"安装失败"，但也不是没事：新位置已经装好、cacheRoot 也改指新位置了，
            // 留在旧位置的那几个目录应用<b>看不见</b>（进度行一闪而过，完成页又把状态文字收起来，
            // 等于用户永远不会知道）。所以记成 MigrationWarning 交给完成页常驻显示。
            if (mig.Failed.Count > 0)
            {
                migrationFailed = true;
                foreach (var f in mig.Failed) App.Log("迁移未完成：" + f);
                progress.Report(new InstallProgress("这个数据目录没搬成：" + mig.Failed[0], 0.04));
                MigrationWarning =
                    $"有 {mig.Failed.Count} 个数据目录没能搬到新位置，仍留在 {mig.OldDir}：\n"
                    + string.Join("\n", mig.Failed.Select(f => "· " + f))
                    + "\n\n" + (mig.ConfigPatched
                        ? "应用现在读的是新位置，所以这些目录在应用里会显示为空。旧目录我们一个文件都没删，"
                          + "要找回就手动把它们复制到 " + mig.NewDir + " 对应位置。"
                        : "应用仍然读原来的位置，这些数据照常可用。");
            }
        }

        // 测试逃生口：设 JGINSTALLER_NOLEGACY=1 可跳过旧版静默卸载
        if (Environment.GetEnvironmentVariable("JGINSTALLER_NOLEGACY") != "1")
        {
            if (legacy.uninstallCmd is not null)
            {
                var (exe, _) = SplitCommand(legacy.uninstallCmd);
                var ownGui = exe.Length > 0 && Path.GetFileName(exe)
                    .Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase);
                if (!ownGui && !migrationFailed)
                {
                    progress.Report(new InstallProgress("正在移除旧版本…", 0.05));
                    App.Log("[装机] 走旧版卸载器清旧目录（非 GUI 入口）");
                    RunLegacyUninstaller(legacy.uninstallCmd);
                }
                else if (!ownGui && migrationFailed)
                {
                    App.Log("数据迁移有未解决项，跳过旧卸载器以保留旧位置数据");
                }
                else if (ownGui)
                {
                    App.Log("[装机] 旧卸载入口是我们自己的 GUI 向导 ⇒ 跳过（会卡在确认页）");
                }
                // 卸载入口是我们自己的 GUI 卸载向导（Uninstall.exe = 主程序副本）时，
                // 绝不能在安装中途拉起：它会停在确认页等用户点击，应用还开着时只剩
                // 拦截页，确认后的延时 rd /s /q 自删还可能把刚解压的新文件删掉。
                // 它的职责（关应用/删快捷方式/清注册表）本次安装的同址覆盖 +
                // 注册表/快捷方式重写已完整覆盖，直接跳过。
            }
        }

        long totalBytes = 0;
        using (var res = OpenResource())
        {
            var entries = ReadPayloadHeader(res, out long total);
            totalBytes = total;
            EnsureFreeSpace(targetDir, total + 256L * 1024 * 1024);

            var props = new byte[5];
            res.ReadExactly(props);
            // 解码在 LZMA 结束标记处自然终止（编码端未知大小模式必带标记）；
            // 每个条目按头部记录的大小精确写出，流提前耗尽会在下方 Read 抛错
            using var lzma = LzmaStream.Create(props, res, leaveOpen: true);

            long done = 0, lastReport = 0;
            var buf = new byte[1 << 16];
            var written = new List<string>(entries.Count);
            foreach (var (rel, size) in entries)
            {
                ct.ThrowIfCancellationRequested();
                var dest = SafePath(targetDir, rel);
                if (dest is null)
                    continue;
                written.Add(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var dst = File.Create(dest);
                long remaining = size;
                while (remaining > 0)
                {
                    int n = lzma.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                    if (n <= 0)
                        throw new IOException("安装包数据不完整：" + rel);
                    dst.Write(buf, 0, n);
                    done += n;
                    remaining -= n;
                    if (done - lastReport > 3_500_000)
                    {
                        lastReport = done;
                        progress.Report(new InstallProgress("正在安装文件…", 0.10 + 0.87 * done / total, done, total));
                    }
                }
            }

            // 清掉上一版写出去、这版不再带来的文件（孤儿）：老版本发过、后来被删掉的
            // svg/png 会一直躺着，而清单一换代就连"曾经是我们写的"这条记录也断了。
            // 只认旧清单里的条目，用户手动放进去的东西一个都不碰；没旧清单就什么都不删。
            try
            {
                var orphans = InstallRecycle.RemoveOrphans(targetDir, entries.Select(x => x.rel).ToList());
                if (orphans.Count > 0)
                    App.Log($"清理上一版遗留文件 {orphans.Count} 个"
                        + (orphans.Count <= 5 ? "：" + string.Join("、", orphans)
                           : "：" + string.Join("、", orphans.Take(5)) + " …"));
            }
            catch (Exception ex) { App.Log("清理遗留文件失败: " + ex.Message); }

            // 落一份文件清单：换目录安装时据此回收旧位置、卸载时据此只删自己写出去的文件。
            // 写失败不影响安装（最多是以后回收退回「顶层白名单」模式）。
            try { InstallRecycle.WriteManifest(targetDir, written); }
            catch (Exception ex) { App.Log("写安装清单失败: " + ex.Message); }
        }
        progress.Report(new InstallProgress("正在安装文件…", 0.97));

        // 独立卸载器：复制主程序为 Uninstall.exe，程序内按文件名进入卸载模式
        progress.Report(new InstallProgress("正在配置卸载程序…", 0.97));
        File.Copy(Path.Combine(targetDir, "JuniGrid.exe"), Path.Combine(targetDir, "Uninstall.exe"), true);

        progress.Report(new InstallProgress("正在创建快捷方式…", 0.985));
        CreateShortcuts(targetDir, desktopShortcut);
        WriteUninstallRegistry(targetDir);

        // 「Nexus 网页上的下载按钮点了之后打开谁」的<b>初值</b>在这里落定 —— 装机这一刻是唯一能看出
        // 用户意图的时机（应用每次开机都无条件抢注，就是把不知道 nxm 是什么的用户的东西抢走）。
        // 只在这个键还不存在（全新用户）时写；已有值一律不动 —— 那是他在设置页里亲手做过的决定，
        // 升级装机没资格替他改回来。之后这个开关只归设置页管。
        InstallConfig.SetOneClickInstallIfAbsent(InstallMigrate.DefaultConfigPath(),
            !ForeignNxmHandlerInUse());

        // v1.1.8：新机免装依赖 —— 缺 WebView2 运行时就用 payload 里的引导补装一次
        TryEnsureWebView2(targetDir, progress);

        // 换目录安装 = 搬家：新位置已经装好、注册表与快捷方式也已指向它，最后一步才回收旧位置。
        // 放在最后是有意的 —— 中途任何失败都不会出现「新的没装成、旧的也没了」。
        // 旧位置用的是第 2 步（搬家之前）抓的那一份 —— 这里不能再查注册表，那时它已被我们改掉。
        // 搬家有没办成的项时整段跳过：旧目录可能还是那些数据的唯一副本，把里面的程序文件删掉
        // 只会让用户既看不见、也回不去（旧卸载器在同一个条件下已经跳过了）。
        if (migrationFailed)
            App.Log("数据迁移未完成，跳过旧位置回收以保留旧位置数据：" + (legacy.location ?? "(无)"));
        else
            RecyclePreviousInstall(legacy.location, targetDir, progress);

        App.Log($"[装机] 完成 → {targetDir} ‖ 解包 {totalBytes / (1024 * 1024)} MB ‖ 快捷方式={desktopShortcut}");
        progress.Report(new InstallProgress("安装完成", 1.0, totalBytes, totalBytes));
    }

    private static void EnsureFreeSpace(string targetDir, long requiredBytes)
    {
        var root = Path.GetPathRoot(targetDir);
        if (root is not null && new DriveInfo(root).AvailableFreeSpace < requiredBytes)
            throw new InvalidOperationException("目标磁盘空间不足，请清理后重试。");
    }

    private static void EnsureWritableDirectory(string targetDir)
    {
        var probe = Path.Combine(targetDir, ".junigrid-write-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            throw new InvalidOperationException("无法写入所选安装目录。请选择当前账户有写入权限的文件夹。", ex);
        }
    }

    private static string NormPath(string p)
    {
        try
        {
            return Path.GetFullPath(p)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return ""; }
    }

    /// <summary>回收上一次安装的位置 —— 只删我们自己写出去的文件，其余原样留着。
    /// <b>旧位置必须由调用方传进来，绝不能在这里重读注册表</b>：
    /// 这一步跑在 <see cref="WriteUninstallRegistry"/> 之后，注册表里的 InstallLocation
    /// 那时已经是新目录了，重读回来就等于 oldN == newN，同址守卫会把整段回收
    /// 静默短路（2026-10-04 实测：装到 E:\JuniGrid2 后，E:\JuniGrid1 那 182 MB 程序体原地留着）。
    /// 组件夹具当时全绿，因为它测的是 <see cref="InstallRecycle.RecycleOldInstall"/>，
    /// 没测"谁把哪个目录交给它"。回归钉子在 <c>installer/jg-installer-drift.py</c>
    /// （M11a–c 查形状、RT1–RT5 跑真实入口），别删。</summary>
    private void RecyclePreviousInstall(string? legacyLocation, string targetDir, IProgress<InstallProgress> progress)
    {
        try
        {
            var oldN = RecycleTargetOf(legacyLocation, targetDir);
            if (oldN is null) return;

            progress.Report(new InstallProgress("正在清理旧版本…", 0.995));
            var r = InstallRecycle.RecycleOldInstall(oldN, PayloadRelativePaths(), dryRun: false);
            App.Log($"回收旧位置 {r.OldDir}：删 {r.Deleted} 项（清单={(r.HadManifest ? "有" : "无→按确切文件路径")}）"
                    + $"，失败 {r.Failed.Count} 项，保留 {r.Remaining.Count} 项"
                    + (r.DirRemoved ? "，目录已空并删除" : ""));
            if (r.Failed.Count > 0)
                App.Log("旧目录有文件未能删除：" + string.Join("、", r.Failed.Take(3)));
            if (r.Remaining.Count > 0)
            {
                App.Log($"旧目录仍有未清理内容，已保留在 {oldN}：" + string.Join("、", r.Remaining.Take(5)));
                OldDirLeftoverNote =
                    $"旧目录 {oldN} 还剩 {r.Remaining.Count} 项没删。它们不是这次安装写出去的程序文件"
                    + "（你自己的备份、调试产物，或者更早版本留下的），按规矩不动。"
                    + "确认不需要的话，可以把整个旧目录删掉。";
            }
        }
        catch (Exception ex) { App.Log("回收旧位置失败: " + ex.Message); }
    }

    /// <summary>该回收哪个旧目录；null = 这次不回收。三条守卫：
    /// ① 旧位置没记录或已不存在；② 同址覆盖（本来就该什么都不碰）；
    /// ③ 新位置套在旧位置里面（装到 <c>C:\JuniGrid\new</c> 这种）⇒ 回收旧的会连新的一份一起端掉。
    /// 单独抽出来是为了能被钉住 —— 上面那条 bug 的教训就是"守卫对、传参错"没人测得到。</summary>
    internal static string? RecycleTargetOf(string? legacyLocation, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(legacyLocation) || !Directory.Exists(legacyLocation)) return null;
        var oldN = NormPath(legacyLocation);
        var newN = NormPath(targetDir);
        if (oldN.Length == 0 || newN.Length == 0) return null;
        if (oldN.Equals(newN, StringComparison.OrdinalIgnoreCase)) return null;
        if (newN.StartsWith(oldN + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return oldN;
    }

    /// <summary>
    /// 缺 WebView2 时跑安装包自带的 Evergreen 引导（targetDir\tools\WebView2\…）。
    /// 不引用 WebView2 SDK：安装器只做「文件在不在 + 注册表/版本目录探测」，保持零额外依赖。
    /// </summary>
    private static void TryEnsureWebView2(string targetDir, IProgress<InstallProgress> progress)
    {
        if (WebView2Present()) return;
        try
        {
            var setup = Path.Combine(targetDir, "tools", "WebView2", "MicrosoftEdgeWebView2Setup.exe");
            if (!File.Exists(setup)) return;
            progress.Report(new InstallProgress("正在安装 WebView2 运行时（界面依赖，约 1 分钟）…", 0.99));
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = setup,
                Arguments = "/silent /install",
                UseShellExecute = true,
            });
            p?.WaitForExit(5 * 60 * 1000);
        }
        catch { /* 补装失败不阻塞安装；应用启动时还有一次自检 */ }
    }

    /// <summary>探测本机是否已有 WebView2 Evergreen 运行时（不依赖 WebView2 SDK）。</summary>
    private static bool WebView2Present()
    {
        try
        {
            // ① 注册表（x64 / WOW6432Node 两处）
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = baseKey.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}")
                          ?? baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
                if (k?.GetValue("pv") is string pv && pv.Length > 1 && pv != "0.0.0.0") return true;
            }
            // ② 用户级安装痕迹
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (Directory.Exists(Path.Combine(local, "Microsoft", "EdgeWebView"))) return true;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (Directory.Exists(Path.Combine(pf, "Microsoft", "EdgeWebView"))) return true;
        }
        catch { }
        return false;
    }

    /// <summary>本次安装包内含文件的相对路径。旧位置没有清单时只按这些确切路径逐文件回收。</summary>
    public static List<string> PayloadRelativePaths()
    {
        try
        {
            using var res = OpenResource();
            return ReadPayloadHeader(res, out _)
                .Select(e => e.rel.Replace('\\', '/')).ToList();
        }
        catch { return new List<string>(); }
    }

    private static Stream OpenResource()
        => typeof(InstallerEngine).Assembly.GetManifestResourceStream(ResourceName)
           ?? throw new InvalidOperationException($"找不到内置安装内容 {ResourceName}");

    /// <summary>读取 JGP1 容器头（格式见 PayloadTool/Program.cs），返回文件条目表与解压后总大小。</summary>
    private static List<(string rel, long size)> ReadPayloadHeader(Stream stream, out long totalSize)
    {
        Span<byte> head = stackalloc byte[8];
        stream.ReadExactly(head);
        if (!head[..4].SequenceEqual("JGP1"u8))
            throw new InvalidOperationException("安装包格式不符（应为 JGP1 容器）");
        int count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
        var entries = new List<(string, long)>(count);
        long total = 0;
        Span<byte> entry = stackalloc byte[3 + sizeof(long)];
        for (int i = 0; i < count; i++)
        {
            stream.ReadExactly(entry);
            if (entry[0] != 0)
                throw new InvalidOperationException("安装包条目类型未知");
            var pathBytes = new byte[BinaryPrimitives.ReadUInt16LittleEndian(entry[1..3])];
            stream.ReadExactly(pathBytes);
            long size = BinaryPrimitives.ReadInt64LittleEndian(entry[3..]);
            entries.Add((Encoding.UTF8.GetString(pathBytes), size));
            total += size;
        }
        totalSize = total;
        return entries;
    }

    /// <summary>zip 内路径拼接，拒绝越出目标目录的恶意路径；返回 null 表示该条目是根目录本身，可跳过。</summary>
    private static string? SafePath(string root, string rel)
    {
        rel = rel.Replace('\\', '/');
        while (rel.StartsWith("./")) rel = rel[2..];
        if (rel.Length == 0 || rel == "." || rel.EndsWith('/')) rel = rel.TrimEnd('/');
        if (rel.Length == 0 || rel == ".") return null;
        var combined = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("安装包内出现非法路径：" + rel);
        return combined;
    }

    /// <summary>拆开「"C:\path\xxx.exe" args」形式的卸载命令；解析不出 exe 时 exe 为空串。</summary>
    private static (string exe, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end < 0 ? ("", "") : (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    private static void RunLegacyUninstaller(string cmd)
    {
        try
        {
            var (exe, args) = SplitCommand(cmd);
            if (exe.Length == 0) return;
            // 旧 Inno 卸载器（unins000.exe）没有给参数时补上静默参数
            if (args.Length == 0 && Path.GetFileName(exe).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                args = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES";
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(90_000);
        }
        catch
        {
            // 旧卸载器失败不阻塞新安装（同目录覆盖 + 注册表覆盖写）
        }
    }

    /// <summary>把正在运行的 JuniGrid 关掉（不关掉会占着待替换的 exe/dll）。
    /// <para><b>不能连游戏一起杀</b>：SMAPI 和游戏本体是应用拉起来的子进程，
    /// <c>Kill(entireProcessTree: true)</c> 会把玩家正在玩的那一局硬掐掉（没保存的进度就没了）。
    /// 所以先看父链：游戏确实挂在某个 JuniGrid 下面时，宁可拒绝安装并让用户自己保存退出。
    /// Steam 直接拉起的游戏不是我们的后代，整树杀本来就碰不到它，也不该拦安装。</para></summary>
    private static void CloseRunningApp()
    {
        var running = new List<Process>();
        try { running.AddRange(Process.GetProcessesByName("JuniGrid")); } catch { }
        try
        {
            if (NativeMethods.AnyNamedProcessDescendsFrom(running.Select(p => p.Id).ToList(),
                    "StardewModdingAPI.exe", "Stardew Valley.exe"))
                throw new InvalidOperationException(
                    "游戏正在运行（由 JuniGrid 启动），直接结束会丢失这一局没保存的进度。"
                    + "请先在游戏里保存并退出，再安装。");
            foreach (var p in running)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
        finally
        {
            foreach (var p in running) p.Dispose();
        }
        Thread.Sleep(400);
    }

    private void CreateShortcuts(string dir, bool desktop)
    {
        var exe = Path.Combine(dir, "JuniGrid.exe");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)
            ?? throw new InvalidOperationException("无法创建快捷方式（WScript.Shell 不可用）");

        void Make(string path)
        {
            var lnk = shell.CreateShortcut(path);
            lnk.TargetPath = exe;
            lnk.WorkingDirectory = dir;
            lnk.IconLocation = exe + ",0";
            lnk.Description = "JuniGrid — 星露谷物语小助手";
            lnk.Save();
        }

        var group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "JuniGrid");
        Directory.CreateDirectory(group);
        Make(Path.Combine(group, "JuniGrid.lnk"));

        if (desktop)
        {
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Make(Path.Combine(desk, "JuniGrid.lnk"));
        }
    }

    private void WriteUninstallRegistry(string dir)
    {
        var exe = Path.Combine(dir, "JuniGrid.exe");
        var uninstallExe = Path.Combine(dir, "Uninstall.exe");
        long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(f).Length; } catch { }
        }

        // 卸载入口：安装目录里的独立 Uninstall.exe（双击即进入卸载向导）
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        k.SetValue("DisplayName", "JuniGrid");
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", "JuniGrid");
        k.SetValue("InstallLocation", dir);
        k.SetValue("DisplayIcon", exe);
        k.SetValue("UninstallString", $"\"{uninstallExe}\"");
        k.SetValue("QuietUninstallString", $"\"{uninstallExe}\"");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
    }
}
