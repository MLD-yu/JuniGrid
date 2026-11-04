using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace JuniGrid.Services;

public sealed partial class ModService
{
    private static readonly ConcurrentDictionary<string, string> CleanedModsTree = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, object> CleanLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 清理重复 Mod 需要完整解析所有 manifest。普通重扫只在 Mods 顶层目录结构改变时重做；
    /// 手动刷新可传 force=true，仍会执行完整清理。这样切页/Watcher 的重复通知不再反复扫两遍。
    /// </summary>
    public int CleanupDuplicateCopiesIfChanged(string gamePath, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return 0;
        var key = Path.GetFullPath(Path.Combine(gamePath, "Mods"));
        var gate = CleanLocks.GetOrAdd(key, static _ => new object());
        lock (gate)
        {
            var before = ModsStructureSignature(key);
            if (!force && CleanedModsTree.TryGetValue(key, out var last) && last == before) return 0;
            var removed = CleanupDuplicateCopies(gamePath);
            CleanedModsTree[key] = ModsStructureSignature(key);
            return removed;
        }
    }

    private static string ModsStructureSignature(string modsDir)
    {
        try
        {
            if (!Directory.Exists(modsDir)) return "missing";
            var sb = new StringBuilder();
            foreach (var dir in Directory.EnumerateDirectories(modsDir)
                         .Where(d => !Path.GetFileName(d).Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(Path.GetFileName(dir)).Append('\0')
                    .Append(Directory.GetLastWriteTimeUtc(dir).Ticks).Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            // If metadata enumeration failed, never trust a prior signature and skip cleanup.
            return "unavailable:" + ex.GetType().FullName + ":" + DateTime.UtcNow.Ticks;
        }
    }
}
