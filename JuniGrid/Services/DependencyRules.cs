namespace JuniGrid.Services;

/// <summary>依赖判据里与界面无关的那一半：哪些依赖「装了但不够新」、manifest 要的 SMAPI 是否高于本机。
/// <para>抽出来的理由：这两条原先写在 Mods 页的 Razor 组件里，测试舱碰不到。可选依赖的版本判定
/// 就是这样漏了很久没人发现 —— 实机 2026-10-29，Iconic Framework 与 Self Checkout 的 GMCM 都标着
/// <c>IsRequired: false</c>，装着 1.12.0、要求 1.14.1，SMAPI 把这两条整条跳过，而界面报「缺依赖 0」。</para></summary>
internal static class DependencyRules
{
    /// <summary>「装了但版本低于 manifest 要求」的依赖 UID。
    /// 必需与可选<b>一视同仁</b>：SMAPI 对「可选依赖装着却低于 MinimumVersion」照样跳过依赖方。
    /// 没装的依赖不在 <paramref name="installedVersions"/> 里，天然不会进结果。</summary>
    public static List<string> StaleDepUids(ModEntry m, IReadOnlyDictionary<string, string> installedVersions)
    {
        var list = new List<string>();
        if (m.DependencyMinVersions.Count == 0) return list;
        foreach (var d in m.Dependencies.Concat(m.OptionalDependencies).Concat(m.ContentPackIds))
        {
            var dep = d?.Trim();
            if (string.IsNullOrWhiteSpace(dep) || !InstalledButOld(m, dep, installedVersions)) continue;
            if (list.Any(x => x.Equals(dep, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(dep);
        }
        return list;
    }

    /// <summary>这个依赖装着、且低于本行 manifest 要求的最低版本。</summary>
    public static bool InstalledButOld(ModEntry m, string dep,
        IReadOnlyDictionary<string, string> installedVersions)
        => installedVersions.TryGetValue(dep, out var have)
           && m.DependencyMinVersions.TryGetValue(dep, out var need)
           && VersionUtil.IsOlderThan(have, need);

    /// <summary>这一行「需要动手」的依赖（一键安装的输入）：必需依赖缺了要装、太旧要换；
    /// 可选依赖只在<b>装着但太旧</b>时才算 —— 没装的可选依赖是作者允许的状态（SMAPI 不拦）。</summary>
    public static List<string> DepsToProcess(ModEntry m, IReadOnlyDictionary<string, string> installedVersions)
    {
        var list = new List<string>();
        void Add(string? raw, bool optional)
        {
            var dep = raw?.Trim();
            if (string.IsNullOrWhiteSpace(dep)) return;
            var installed = installedVersions.ContainsKey(dep);
            if (!installed && optional) return;
            if (installed && !InstalledButOld(m, dep, installedVersions)) return;
            if (list.Any(x => x.Equals(dep, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(dep);
        }
        foreach (var d in m.Dependencies.Concat(m.ContentPackIds)) Add(d, optional: false);
        foreach (var d in m.OptionalDependencies) Add(d, optional: true);
        return list;
    }

    /// <summary>manifest 要求的 SMAPI 版本高于本机 → 返回要求的那个串，否则 null。
    /// 读不到本机版本（null / <c>"installed"</c> 占位）不指控。</summary>
    public static string? SmapiTooOldNeed(string? minApiVersion, string? currentSmapi)
    {
        var need = minApiVersion?.Trim();
        var have = currentSmapi?.Trim();
        if (string.IsNullOrWhiteSpace(need) || string.IsNullOrWhiteSpace(have)) return null;
        if (have == "installed") return null;
        return VersionUtil.IsOlderThan(have, need) ? need : null;
    }
}
