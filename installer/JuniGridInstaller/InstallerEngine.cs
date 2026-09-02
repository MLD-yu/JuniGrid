using System.Buffers.Binary;
using System.Diagnostics;
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

    public (string? uninstallCmd, string? location) FindLegacyInstall()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            var cmd = (k?.GetValue("QuietUninstallString") ?? k?.GetValue("UninstallString")) as string;
            var loc = k?.GetValue("InstallLocation") as string;
            return (string.IsNullOrWhiteSpace(cmd) ? null : cmd, loc);
        }
        catch { return (null, null); }
    }

    public Task InstallAsync(string targetDir, bool desktopShortcut, IProgress<InstallProgress> progress, CancellationToken ct)
        => Task.Run(() => InstallCore(targetDir, desktopShortcut, progress, ct), ct);

    private void InstallCore(string targetDir, bool desktopShortcut, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        targetDir = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(targetDir);

        // 先关掉正在运行的 JuniGrid，再动旧版卸载器 —— 顺序反了文件被占用，
        // 旧版卸载器会弹「文件正在使用」错误或卡在拦截页，静默卸载等于失败。
        progress.Report(new InstallProgress("正在关闭正在运行的 JuniGrid…", 0.02));
        CloseRunningApp();

        // 测试逃生口：设 JGINSTALLER_NOLEGACY=1 可跳过旧版静默卸载
        if (Environment.GetEnvironmentVariable("JGINSTALLER_NOLEGACY") != "1")
        {
            var legacy = FindLegacyInstall();
            if (legacy.uninstallCmd is not null)
            {
                var (exe, _) = SplitCommand(legacy.uninstallCmd);
                var ownGui = exe.Length > 0 && Path.GetFileName(exe)
                    .Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase);
                if (!ownGui)
                {
                    progress.Report(new InstallProgress("正在移除旧版本…", 0.05));
                    RunLegacyUninstaller(legacy.uninstallCmd);
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
            var root = Path.GetPathRoot(targetDir);
            if (root is not null && new DriveInfo(root).AvailableFreeSpace < total + 256L * 1024 * 1024)
                throw new InvalidOperationException("目标磁盘空间不足，请清理后重试。");

            var props = new byte[5];
            res.ReadExactly(props);
            // 解码在 LZMA 结束标记处自然终止（编码端未知大小模式必带标记）；
            // 每个条目按头部记录的大小精确写出，流提前耗尽会在下方 Read 抛错
            using var lzma = LzmaStream.Create(props, res, leaveOpen: true);

            long done = 0, lastReport = 0;
            var buf = new byte[1 << 16];
            foreach (var (rel, size) in entries)
            {
                ct.ThrowIfCancellationRequested();
                var dest = SafePath(targetDir, rel);
                if (dest is null)
                    continue;
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
        }
        progress.Report(new InstallProgress("正在安装文件…", 0.97));

        // 独立卸载器：复制主程序为 Uninstall.exe，程序内按文件名进入卸载模式
        progress.Report(new InstallProgress("正在配置卸载程序…", 0.97));
        File.Copy(Path.Combine(targetDir, "JuniGrid.exe"), Path.Combine(targetDir, "Uninstall.exe"), true);

        progress.Report(new InstallProgress("正在创建快捷方式…", 0.985));
        CreateShortcuts(targetDir, desktopShortcut);
        WriteUninstallRegistry(targetDir);

        progress.Report(new InstallProgress("安装完成", 1.0, totalBytes, totalBytes));
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

    private static void CloseRunningApp()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("JuniGrid"))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }
        catch { }
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
