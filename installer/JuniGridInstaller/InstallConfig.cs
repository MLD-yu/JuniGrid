using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JuniGridInstaller;

/// <summary>安装时改应用配置的那只手。
/// 配置里键是 camelCase（<c>ConfigService</c> 用缩写策略序列化），我们<b>只碰要碰的那一个键</b>：
/// 整份配置先解析成 <see cref="JsonObject"/> 再改再写回 —— 换别的写法（字符串替换、重新序列化整个类）
/// 都会把不认识它的键丢掉，而安装器根本不知道配置里该有什么。
/// 写之前先落到临时文件并再解析一遍，解析不过就绝不覆盖真配置；覆盖前留 <c>.before-move.bak</c>。</summary>
internal static class InstallConfig
{
    public const string CacheRootKey = "cacheRoot";
    public const string OneClickInstallKey = "enableOneClickInstall";

    /// <summary>把 <c>cacheRoot</c> 改指新安装目录。只在它原本指旧安装目录、或压根没设过却确实搬了数据时才改；
    /// 指向别处（用户自己划的 D:\JuniGridCache）一个字节都不动。</summary>
    public static bool PointCacheRootAt(string configPath, string oldDir, string newDir)
        => Patch(configPath, obj =>
        {
            var cur = TryStr(obj, CacheRootKey);
            if (string.IsNullOrWhiteSpace(cur)) { obj[CacheRootKey] = newDir; return true; }
            if (!Norm(cur).Equals(Norm(oldDir), StringComparison.OrdinalIgnoreCase)) return false;
            obj[CacheRootKey] = newDir;
            return true;
        });

    /// <summary>写「Nexus 网页上的下载按钮归谁」这个开关的<b>初值</b> —— 只在配置里还没有这个键的时候写。
    /// 已有值一律不动：那是用户在设置页里亲手做过的决定，装机不是问他意见的地方。
    /// （之前无条件覆盖过一次：升级安装会把用户关掉的开关悄悄重新打开。）</summary>
    public static bool SetOneClickInstallIfAbsent(string configPath, bool claim)
        => Patch(configPath, obj =>
        {
            if (obj.ContainsKey(OneClickInstallKey)) return false;
            obj[OneClickInstallKey] = claim;
            return true;
        });

    private delegate bool Edit(JsonObject obj);

    private static bool Patch(string configPath, Edit edit)
    {
        try
        {
            JsonObject obj;
            var exists = File.Exists(configPath);
            if (exists)
            {
                if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject parsed) return false;
                obj = parsed;
            }
            else
            {
                // 首装还没配置文件的场景：没有就照这个键新建一份最小配置，
                // 应用读到不认识的形状会按自己的默认值补齐（ConfigService 就是这么设计的）。
                obj = new JsonObject();
            }

            if (!edit(obj)) return false;

            var tmp = configPath + ".junigrid-move.tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            JsonNode.Parse(File.ReadAllText(tmp));      // 解析不过就别拿去覆盖真配置
            if (exists)
                try { File.Copy(configPath, configPath + ".before-move.bak", true); } catch { }
            File.Move(tmp, configPath, true);
            return true;
        }
        catch { return false; }
    }

    private static string? TryStr(JsonObject obj, string key)
    {
        try { return obj[key]?.GetValue<string>(); } catch { return null; }
    }

    private static string Norm(string p)
        => Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
