namespace JuniGrid.Services;

public sealed class ModEntry
{
    public string Folder { get; set; } = "";
    /// <summary>mod 文件夹的最后写入时间，用于"按时间排序"。</summary>
    public DateTime LastWrite { get; set; }
    public bool Disabled { get; set; }
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string UniqueID { get; set; } = "";           // manifest 里的唯一标识，更新包校验用
    public int? NexusModId { get; set; }   // from manifest UpdateKeys "Nexus:<id>"
    public string? GitHubRepo { get; set; }  // from manifest UpdateKeys "GitHub:<owner>/<repo>"（免费直下）
    public List<string> Dependencies { get; set; } = new();  // 本 mod 依赖的 UniqueID（含 ContentPackFor 宿主）
    /// <summary>manifest 里作者标了 IsRequired=false 的依赖：缺了不影响用，不进「缺少依赖」红字，
    /// 只在行尾给一条可忽略的浅色提示。</summary>
    public List<string> OptionalDependencies { get; set; } = new();
    /// <summary>依赖 UID → manifest 声明的 MinimumVersion（只收写了版本的条目）。
    /// 在 SMAPI 那边「装了但版本低于它」等于没装 —— 依赖方整条被 Skipped。</summary>
    public Dictionary<string, string> DependencyMinVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ContentPackIds { get; set; } = new(); // 它作为内容包时依赖的宿主 mod UniqueID（合并进 Dependencies 用于缺失检测）
    public bool HasManifest { get; set; } = true;   // false = 该文件夹没有有效 manifest（用文件夹名兜底）
    /// <summary>v0.45.0：分类标签（仿 PCL2），在列表行简介前显示。代码 Mod / 内容包 / 代码·内容包；都不是则为空。</summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// JuniGrid 自己转出来的 CP 包：立绘那条是 <c>JuniGrid.PortraitPack.*</c>，
    /// 按目录名转的裸素材包（界面/小马/其它画面）是 <c>JuniGrid.AssetPack.*</c>。
    /// manifest Version 固定 1.0.0，NexusModId 只是原素材包的来源边车 ——
    /// 绝不能拿 N 网原包版本号去比本地 1.0.0，否则永远显示「可更新」。
    /// </summary>
    public bool IsConvertedPack =>
        !string.IsNullOrEmpty(UniqueID)
        && (UniqueID.StartsWith("JuniGrid.PortraitPack.", StringComparison.OrdinalIgnoreCase)
            || UniqueID.StartsWith(ModService.AssetPackUidPrefix, StringComparison.OrdinalIgnoreCase));
}
