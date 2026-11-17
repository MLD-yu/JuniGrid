namespace JuniGrid.Services;

/// <summary>依赖判据里与界面无关的那一半：哪些依赖「装了但不够新」、manifest 要的 SMAPI 是否高于本机。
/// <para>抽出来的理由：这两条原先写在 Mods 页的 Razor 组件里，测试舱碰不到。可选依赖的版本判定
/// 就是这样漏了很久没人发现 —— 实机 2026-10-29，Iconic Framework 与 Self Checkout 的 GMCM 都标着
/// <c>IsRequired: false</c>，装着 1.12.0、要求 1.14.1，SMAPI 把这两条整条跳过，而界面报「缺依赖 0」。</para></summary>
/// <summary>一条「会让依赖方被 SMAPI 跳过」的依赖问题。四档的区别只在<b>出路</b>：
/// <see cref="Missing"/> 去装、<see cref="Disabled"/> 去开、<see cref="Stale"/> 去换版本、
/// <see cref="NotLoaded"/> 只能去换版本或修好它本身（它装着、也启用着，就是没起来）。</summary>
internal enum DepBlockKind { Missing, Disabled, Stale, NotLoaded }

/// <summary>依赖问题的一行：哪个 UID、哪一档。</summary>
internal readonly record struct DepBlock(string Uid, DepBlockKind Kind);

internal static class DependencyRules
{
    /// <summary>按 <b>SMAPI 的实际判据</b>列出这一行所有「依赖方会被整条跳过」的依赖问题。
    /// <para>为什么要独立于 <see cref="StaleDepUids"/>：外面那三条判据是分开写的（缺没缺 / 禁没禁 /
    /// 够不够新），谁忘了合并谁就漏报 —— 实机 2026-11-15，用户禁用 70 个 mod（含 SpaceCore、
    /// Json Assets、Farm Type Manager、Custom Companions、Shop Tile Framework、DaisyNiko.Tilesheets），
    /// SMAPI 因此跳过 25 个依赖方，而面板「缺依赖」只剩 1 条：其余 24 条的依赖在磁盘上<b>确实装着</b>，
    /// 只是关了 —— 在 SMAPI 眼里「关掉的 mod」和「没装的 mod」是同一件事。</para>
    /// <para>可选依赖（IsRequired=false）沿用同一套：没装或缺省不算问题（作者允许），
    /// 装着却低于 MinimumVersion 才算（SMAPI 照样跳过依赖方）。</para>
    /// <para>第四档 <see cref="DepBlockKind.NotLoaded"/> 要外面喂 <paramref name="notLoadedUids"/>：
    /// 「装着、启用着、上次启动却没加载起来」这件事只有 SMAPI 日志知道（AeroCore 不兼容、
    /// AntiSocial NPCs 缺 DLL），manifest 一个字都没提。不传就是三档老口径。</para></summary>
    public static List<DepBlock> BlockingDeps(ModEntry m,
        IReadOnlyDictionary<string, string> installedVersions,
        IReadOnlySet<string> disabledUids,
        IReadOnlySet<string>? notLoadedUids = null)
    {
        var list = new List<DepBlock>();
        if (m.Dependencies.Count == 0 && m.OptionalDependencies.Count == 0 && m.ContentPackIds.Count == 0)
            return list;

        void Add(string? raw, bool optional)
        {
            var dep = raw?.Trim();
            if (string.IsNullOrWhiteSpace(dep)) return;
            if (list.Any(x => x.Uid.Equals(dep, StringComparison.OrdinalIgnoreCase))) return;

            if (!installedVersions.TryGetValue(dep, out var have))
            {
                // 没装：必需依赖要报，可选依赖是作者允许的状态
                if (!optional) list.Add(new DepBlock(dep, DepBlockKind.Missing));
                return;
            }
            // 装着但低于 manifest 要求 —— 必需/可选一视同仁（SMAPI 都把依赖方 Skipped）
            if (m.DependencyMinVersions.TryGetValue(dep, out var need)
                && VersionUtil.IsOlderThan(have, need))
            {
                list.Add(new DepBlock(dep, DepBlockKind.Stale));
                return;
            }
            // 装着但被禁用（点前缀）：SMAPI 眼里的「没装」。可选依赖照样允许缺
            if (!optional && disabledUids.Contains(dep))
            {
                list.Add(new DepBlock(dep, DepBlockKind.Disabled));
                return;
            }
            // 装着、启用、版本也够，但上次启动它自己没加载起来 —— 依赖链上游断了。
            // 可选依赖不在此列：SMAPI 不会因为可选依赖没起来而跳过依赖方。
            if (!optional && notLoadedUids is not null && notLoadedUids.Contains(dep))
                list.Add(new DepBlock(dep, DepBlockKind.NotLoaded));
        }

        foreach (var d in m.Dependencies.Concat(m.ContentPackIds)) Add(d, optional: false);
        foreach (var d in m.OptionalDependencies) Add(d, optional: true);
        return list;
    }

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
