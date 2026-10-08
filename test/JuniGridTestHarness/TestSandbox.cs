using System.Runtime.CompilerServices;

namespace JuniGridTestHarness;

/// <summary>
/// 测试沙箱：把 JUNIGRID_CONFIG_DIR 指进临时目录 —— ConfigService 注释明说
/// 这个环境变量「只给测试舱用」，设了它配置/覆盖记录全部落在沙箱里，
/// 不会碰运行中应用的真配置（%APPDATA%\JuniGrid\junigrid.config.json）。
/// </summary>
internal static class TestSandbox
{
    public static readonly string Dir =
        Path.Combine(Path.GetTempPath(), "jg-test-config");

    public static string ConfigPath => Path.Combine(Dir, "junigrid.config.json");

    /// <summary>清空沙箱里的配置文件（在每个配置相关测试开头调用）。</summary>
    public static void ResetConfigDir()
    {
        Directory.CreateDirectory(Dir);
        foreach (var f in Directory.GetFiles(Dir))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    // 必须在进程早期、任何静态字段读 JUNIGRID_CONFIG_DIR 之前执行：
    // ConfigService.ConfigDir 是 static readonly，进程内只初始化一次。
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("JUNIGRID_CONFIG_DIR", Dir);
        ResetConfigDir();
    }
}
