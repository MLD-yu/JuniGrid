using System.Diagnostics;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>本机 SMAPI 版本必须以「真正被加载的程序集」为准。
/// <para>实机 2026-10-29：4.5.2 的官方安装载荷（install.dat）里根本没有 exe，只有
/// StardewModdingAPI.dll + smapi-internal；壳（Stardew Valley.exe / StardewModdingAPI.exe）
/// 的版本资源是**装它那一刻**的安装包版本，升级后永远停旧。按壳读会报 4.5.1，而
/// SMAPI 自己启动日志写的是 4.5.2。</para></summary>
public sealed class SmapiVersionProbeTests
{
    private static string DllVersionString(string dllPath)
    {
        var v = System.Reflection.AssemblyName.GetAssemblyName(dllPath).Version!;
        return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }

    [Fact]
    public void ManagedAssembly_WinsOverLauncherVersionResource()
    {
        var dir = TempDirs.Create("smapi-ver");
        try
        {
            // 壳：拿测试宿主 exe 冒名（版本资源 1.0.x）
            File.Copy(Environment.ProcessPath!, Path.Combine(dir, "StardewModdingAPI.exe"));
            // 真身：主程序集冒充 SMAPI 的 dll
            var dll = Path.Combine(dir, "StardewModdingAPI.dll");
            File.Copy(typeof(GameService).Assembly.Location, dll);

            var exeVersion = ThreePart(FileVersionInfo.GetVersionInfo(
                Path.Combine(dir, "StardewModdingAPI.exe")).FileVersion);
            // 前提：两个来源必须真的不一样，否则"dll 优先"这条断言测不出东西
            if (exeVersion.Length > 0) Assert.NotEqual(DllVersionString(dll), exeVersion);

            Assert.Equal(DllVersionString(dll), new GameService().ProbeSmapiVersion(dir));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void OldInstallWithoutManagedDll_FallsBackToLauncherVersion()
    {
        var dir = TempDirs.Create("smapi-ver-old");
        try
        {
            var exe = Path.Combine(dir, "StardewModdingAPI.exe");
            File.Copy(Environment.ProcessPath!, exe);
            var expected = ThreePart(FileVersionInfo.GetVersionInfo(exe).FileVersion);
            var v = new GameService().ProbeSmapiVersion(dir);
            if (expected.Length > 0) Assert.Equal(expected, v);
            else Assert.Null(v);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>与生产读取口径一致：取前三段（"1.0.0.0" → "1.0.0"）。</summary>
    private static string ThreePart(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parts = raw.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
    }
}
