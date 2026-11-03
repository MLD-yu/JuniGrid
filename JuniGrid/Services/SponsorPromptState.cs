using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// 赞助提示的记账：上次弹的时间、已提示到的启动里程碑、点「已支持」的时间、
/// 老版本遗留的「已捐赠」永久静默标记（读到就转成「已支持」，不再永久闭嘴）。
///
/// <para><b>为什么单独一个文件，不跟 <c>junigrid.config.json</c> 放一起：</b>
/// 那份配置是<b>整机共享</b>的 —— 机器上可能同时跑着好几份 JuniGrid（开发构建、
/// 安装副本、别人手动起的另一份），而配置是「整份序列化覆盖文件」。
/// 任何一份存回去，都会把它<b>不认识或知道得比现在旧</b>的字段整块覆盖掉。
/// 实机 2026-11-03 就这样把「上次弹窗时间」从 <c>09:38:40</c> 抹回 <c>null</c>，
/// 抹掉 = 弹窗重新武装，于是「每次启动都弹」。</para>
///
/// <para>独立文件只有认识它的版本会写，旧版本碰不到它，别的 JuniGrid 也不会覆盖它。
/// 主配置里那三个字段保留着（只读不认），迁移时用一次。</para>
/// </summary>
public static class SponsorPromptState
{
    private static string FilePath =>
        FilePathOverride ?? Path.Combine(StoragePaths.AppDataDir, "sponsor-prompt.json");

    /// <summary>测试用的落盘位置覆盖（生产代码不碰它）。让白盒测试能钉住
    /// 「保存→读回」「只迁移一次」这些边界，而不碰用户真的那份记账。</summary>
    internal static string? FilePathOverride { get; set; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>记账内容。缺字段 = 没记过（当 null/0/false 处理）。</summary>
    public sealed class State
    {
        /// <summary>上一次弹出赞助提示的时间。null = 从没弹过。</summary>
        public DateTime? LastPromptedAt { get; set; }
        /// <summary>已经提示到的最大启动里程碑（10/100/200/500/1000…）。</summary>
        public int LastMilestone { get; set; }
        /// <summary>用户最后一次点「已支持」的时间。null = 没点过。
        /// <para><b>「已支持」不等于「已捐赠」：</b>那只是收款码页上的一下点击，没法验证
        /// 他到底付没付钱，所以它<b>不参与</b>弹窗频率判定（提示节奏对所有人一致）。
        /// 这个字段只作为账本记录，留给将来「已捐赠用户专用无弹窗版」做区分。</para></summary>
        public DateTime? SupportedAt { get; set; }
        /// <summary>老版本「已捐赠」按钮的永久静默标记 —— <b>历史字段，已废弃</b>。
        /// <para>v1.2.13 起 <see cref="LoadOrMigrate"/> 读到它（或主配置里的
        /// <c>SponsorPromptDisabled</c>）就转成 <see cref="SupportedAt"/> 并清零，
        /// 判定逻辑只在「还没转过」的一瞬间才可能看到 true。</para></summary>
        public bool Disabled { get; set; }
        /// <summary>是否已经从主配置迁移过一次。迁移只做一次，
        /// 免得主配置被别的副本抹成 null 之后又把 null 搬回来。</summary>
        public bool Migrated { get; set; }
    }

    /// <summary>读记账。文件不存在/损坏都当「没记过」，绝不因此让启动流程崩。</summary>
    public static State Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new State();
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return new State();
            return JsonSerializer.Deserialize<State>(json, Opts) ?? new State();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Sponsor", "赞助记账读取失败（按没记过处理）: " + ex.Message);
            return new State();
        }
    }

    /// <summary>写记账。tmp + 原子替换，避免半截文件把下次读取带沟里。</summary>
    public static void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(state, Opts);
            var tmp = FilePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Sponsor", "赞助记账写入失败: " + ex.Message);
        }
    }

    /// <summary>读记账，并顺手做两件一次性的修复：老「已捐赠」标记转「已支持」、主配置旧记账搬家。
    /// <para>不搬的后果：已经在用这功能的用户升级后会<b>重新弹一次</b>（时间戳丢了）。
    /// 只搬一次的后果：主配置后来被别的副本抹成 null，也不会再被搬回来 ——
    /// 这正是今天这个 bug 的成因，不能让它再发生第二次。</para></summary>
    public static State LoadOrMigrate(JuniGridConfig cfg)
    {
        var state = Load();
        var changed = false;

        // v1.2.13：老版本「已捐赠」按钮留下的永久静默标记要解除 ——
        // 用户可能只是误点、或者只是口头支持（根本没付钱），凭一下点击就永不弹太武断；
        // 真正捐过的人也不该被永久关掉页面（想再支持一次都找不到入口）。
        // 转成「已支持」：只作账本记录，弹窗节奏与所有人一致（里程碑 + 7 天兜底）。
        // 幂等（转完 Disabled 清零）且不受 Migrated 约束 —— 在跑 v1.2.12 的用户
        // 文件里 Migrated 早就是 true 了，把这段塞进 !Migrated 分支里就永远跑不到。
        if (state.Disabled || cfg.SponsorPromptDisabled)
        {
            state.SupportedAt ??= DateTime.Now;   // 没有当时的点击时间，只能从升级这一刻起算
            state.Disabled = false;
            changed = true;
            AppLog.Info("Sponsor",
                "老版本「已捐赠」永久静默已解除 → 转为「已支持」记录，按正常节奏提示");
        }

        if (!state.Migrated)
        {
            state.Migrated = true;
            changed = true;
            if (cfg.LastSponsorPromptAt is not null || cfg.LastSponsorMilestone > 0)
            {
                state.LastPromptedAt = cfg.LastSponsorPromptAt;
                state.LastMilestone = cfg.LastSponsorMilestone;
                AppLog.Info("Sponsor", "赞助记账已从主配置迁移到独立文件");
            }
        }

        if (changed) Save(state);
        return state;
    }
}
