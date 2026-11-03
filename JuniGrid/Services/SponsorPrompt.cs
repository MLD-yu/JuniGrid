namespace JuniGrid.Services;

/// <summary>
/// 「已经为你启动了 N 次游戏，要不要赞助一下」的触发规则。
/// <para>现行口径（v1.2.14 起统一）：不足 <b>10 次</b>永不弹；达到<b>里程碑</b>
/// （10 / 100 / 200 / 500，之后每 1000 次）且这一档还没提示过 → 必弹；
/// 两次提示之间满 <b>7 天</b>、期间没碰到里程碑 → 兜底弹一次。
/// 里程碑弹窗和兜底弹窗都会重置 7 天计时。</para>
/// <para><b>「已支持」不参与频率判定</b>：它只是收款码页上一次点击，无法验证是否真的付款，
/// 曾经按 30 天降频，现在取消 —— 支持过的人同样 7 天一次。
/// <see cref="SponsorPromptState.State.SupportedAt"/> 仍然照实记录，
/// 留给将来「已捐赠用户专用无弹窗版」做区分。</para>
/// <para>这里只做纯判断，不碰配置、不碰 UI，方便回归测试把边界钉住。</para>
/// </summary>
public static class SponsorPrompt
{
    /// <summary>第一次提示的启动次数。新用户在第 10 次成功启动时看到；
    /// 已经用过很久的老用户（次数早就过了 10）在更新后第一次打开启动器时看到。</summary>
    public const int FirstPromptLaunchCount = 10;

    /// <summary>没有里程碑时，两次提示之间的最大间隔（天）。</summary>
    public const int IntervalDays = 7;

    /// <summary>
    /// 是否里程碑：10、100、200、500；从 1000 起每 1000 次一个（1000/2000/3000…）。
    /// </summary>
    public static bool IsMilestone(int launchCount)
        => launchCount == 10
           || launchCount == 100
           || launchCount == 200
           || launchCount == 500
           || (launchCount >= 1000 && launchCount % 1000 == 0);

    /// <summary>
    /// 该不该弹。
    /// <list type="number">
    /// <item>不足 <b>10</b> 次（或历史永静标记）→ 不弹，任何情况都不弹；</item>
    /// <item>里程碑到了、且这一档还没提示过 → 必弹（不受 7 天限制）；</item>
    /// <item>从没弹过（老配置没有该字段）→ 更新后第一次打开就弹一次；</item>
    /// <item>否则离上次提示满 <b>7 天</b> → 兜底弹一次。</item>
    /// </list>
    /// <para><paramref name="lastMilestone"/> 是已经提示到的最大里程碑，用来防止
    /// 同一档（例如第 10 次）在重启后重复弹。时钟被回拨时差值为负 → 不弹，宁可不打扰。</para>
    /// <para><paramref name="disabled"/> 是<b>历史</b>的永久静默标记：老版本「已捐赠」按钮写过它。
    /// 迁移会把它转成「已支持」并清零，判定仍尊重它只是为了不惊吓老数据。</para>
    /// </summary>
    public static bool ShouldPrompt(int launchCount, DateTime? lastPromptedAt, int lastMilestone,
                                    DateTime now, bool disabled)
    {
        if (disabled || launchCount < FirstPromptLaunchCount) return false;

        if (IsMilestone(launchCount) && launchCount > lastMilestone) return true;

        if (lastPromptedAt is null) return true;

        return (now - lastPromptedAt.Value).TotalDays >= IntervalDays;
    }
}
