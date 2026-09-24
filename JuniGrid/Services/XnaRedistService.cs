using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace JuniGrid.Services;

/// <summary>
/// XNA 4.0 Refresh 运行库（xnafx40_redist.msi）的检测与安装。
/// 星露谷 1.0–1.4（XNA 时代）的 Microsoft.Xna.Framework.* 不随游戏文件夹分发，
/// 装在系统 GAC —— Steam 官方安装流程会跑 xnafx40_redist.msi，而 JuniGrid 的
/// depot 通道拉的纯游戏文件不含它。新电脑上缺它时：SMAPI 能起、游戏本体必崩
///（FileNotFoundException: Microsoft.Xna.Framework）。1.5+ 已换 MonoGame、
/// 运行库随游戏走，与系统无关。
/// </summary>
public static class XnaRedistService
{
    // 微软官方直链（winget-pkgs 引用的正式源，已验证 200 / 约 6.7MB）
    private const string MsiUrl =
        "https://download.microsoft.com/download/5/3/A/53A804C8-EC78-43CD-A0F0-2FB4D45603D3/xnafx40_redist.msi";

    // 备用源（itch.io 官方 redist 镜像）；装前统一做微软签名校验，坏包装不进去
    private const string MsiUrlFallback =
        "http://dl.itch.ovh/itch-redists/xna-4.0/xnafx40_redist.msi";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromMinutes(10);
        return h;
    }

    /// <summary>目标游戏版本是否为 XNA 时代（1.0–1.4）。1.5+ 是 MonoGame，无需系统 XNA。</summary>
    public static bool GameNeedsXna(string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion)) return false;
        var parts = gameVersion.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var ma) ? ma : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
        return major == 1 && minor <= 4;
    }

    /// <summary>系统是否已装 XNA 4.0 运行库（GAC 里存在 Microsoft.Xna.Framework 即可用）。</summary>
    public static bool IsInstalled()
    {
        try
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var gac in new[] { "GAC_MSIL", "GAC_32" })
            {
                foreach (var root in new[]
                {
                    Path.Combine(win, "Microsoft.NET", "assembly", gac),   // .NET 4 新 GAC
                    Path.Combine(win, "assembly", gac),                    // 旧版 GAC
                })
                {
                    if (Directory.Exists(Path.Combine(root, "Microsoft.Xna.Framework")))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 确保 XNA 运行库可用：已装返回 null；否则下载官方 redist → 校验微软签名 →
    /// msiexec /passive 静默安装（会弹一次 UAC 管理员确认）。
    /// 返回 null = 成功/已装，否则为给用户看的错误文案。
    /// </summary>
    public static async Task<string?> EnsureInstalledAsync(
        Action<string, double?>? progress, CancellationToken ct = default)
    {
        if (IsInstalled()) return null;
        try
        {
            var msi = Path.Combine(Path.GetTempPath(), "junigrid-xnafx40_redist.msi");
            // v1.1.8：优先用安装包自带的 MSI（新机免联网也能补 XNA）
            var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "XnaRedist", "xnafx40_redist.msi");
            if (File.Exists(bundled) && new FileInfo(bundled).Length > 1024 * 1024)
            {
                progress?.Invoke("正在使用安装包自带的 XNA 运行库…", 50);
                File.Copy(bundled, msi, true);
            }
            else
            {
                foreach (var url in new[] { MsiUrl, MsiUrlFallback })
                {
                    try
                    {
                        progress?.Invoke("正在下载 XNA 运行库（老版本游戏依赖，约 7MB）…", 0);
                        await ResumableDownload.RunAsync(Http, url, msi,
                            (m, p, _) => progress?.Invoke(m, p), ct: ct);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLog.Warn("XNA", $"下载失败 {url}：{ex.Message}");
                        try { File.Delete(msi); } catch { }   // 半截包不能给下一个源续传
                    }
                }
            }

            if (!File.Exists(msi) || new FileInfo(msi).Length < 1024 * 1024)
                return "XNA 运行库下载失败（微软源与镜像都不通）—— 老版本游戏可能无法启动";
            if (!IsMicrosoftSigned(msi))
            {
                try { File.Delete(msi); } catch { }
                return "下载的 XNA 运行库没有有效的微软签名，已中止安装";
            }

            progress?.Invoke("正在安装 XNA 运行库（请在弹出的窗口确认管理员授权）…", 99);
            using var p = Process.Start(new ProcessStartInfo(
                "msiexec", $"/i \"{msi}\" /passive /norestart")
            { UseShellExecute = true })!;
            await p.WaitForExitAsync(ct);
            var code = p.ExitCode;

            if (code is 0 or 3010 or 1638)   // 成功 / 需重启 / 已装更高版本
            {
                AppLog.Warn("XNA", $"XNA 运行库安装完成（msiexec 退出码 {code}）");
                try { File.Delete(msi); } catch { }
                return null;
            }
            return code == 1602
                ? "XNA 运行库安装被取消（未确认管理员授权）—— 老版本游戏可能无法启动"
                : $"XNA 运行库安装失败（msiexec 退出码 {code}）—— 老版本游戏可能无法启动";
        }
        catch (OperationCanceledException) { return "XNA 运行库安装被取消"; }
        catch (Exception ex)
        {
            AppLog.Warn("XNA", "安装异常: " + ex.Message);
            return "XNA 运行库安装异常：" + ex.Message;
        }
    }

    // ─── Authenticode 校验：redist 装进系统前，只认完整有效的微软签名包 ───
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;          // 2 = WTD_UI_NONE
        public uint fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
        public uint dwUnionChoice;       // 1 = WTD_CHOICE_FILE
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hWnd, ref Guid actionId, ref WINTRUST_DATA data);

    private static bool IsMicrosoftSigned(string file)
    {
        try
        {
            var pfi = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = file,
                }, pfi, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,
                    dwUnionChoice = 1,
                    pFile = pfi,
                };
                var actionId = GenericVerifyV2;   // static readonly 不能直接按 ref 传
                return WinVerifyTrust(IntPtr.Zero, ref actionId, ref data) == 0;
            }
            finally { Marshal.FreeHGlobal(pfi); }
        }
        catch { return false; }
    }
}
