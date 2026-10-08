using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 一条「盖在游戏本体 Content 上」的记录 = 一个资产文件。
/// 键是 <c>资产|槽</c>：同一个资产可以留着 N 个包的账，但同时只能有一个在位
///（一张表只能有一份内容），新来的占槽、其余转成 Enabled=false 且账与副本都不动。
/// </summary>
public sealed class ContentOverrideRecord
{
    /// <summary>形如 <c>Portraits/Abigail</c>：不带 Content/ 前缀、不带 .xnb，固定 '/' 分隔。</summary>
    public string Asset { get; set; } = "";
    /// <summary>包标识：有 Nexus 页号就用页号，否则用包名规范化。同一包的所有记录共享它。</summary>
    public string Slot { get; set; } = "";
    public string PackName { get; set; } = "";
    public int? NexusModId { get; set; }
    /// <summary>被盖掉那张原版图的哈希。空串 = Content 里原本没这个文件（新增），删账时要删掉而不是还原。</summary>
    public string OriginalSha256 { get; set; } = "";
    /// <summary>我们放上去那份的哈希 —— 拿它跟磁盘现值比，才能证明"还在不在"。</summary>
    public string AppliedSha256 { get; set; } = "";
    /// <summary>我们写进去那份的副本目录名。有它，"关掉再打开"才不必重新下载、PNG 包也不必重编一遍。</summary>
    public string StoreId { get; set; } = "";
    /// <summary>这条账是<b>照着哪个游戏版本</b>的本体编出来、原版又存进了哪一层
    /// （<c>content-originals/&lt;版本&gt;/</c> 的那个「版本」）。
    /// <para>为什么必须有：替图本身与版本无关，但<b>写进本体那份 xnb 是按当时那张原版编的</b>——
    /// 行距、帧数、画布尺寸都跟着原版走。换了版本还直接回放旧副本，就是把 1.6.15 编的表画进
    /// 1.6.16 的本体，症状是"图错位/角色缺表情"而不是任何报错。版本不同的行必须拿原包备份
    /// 对<b>当前</b>本体重编一遍才能再打开。</para>
    /// <para>空串 = 老账，当时没记。回填只认证据（原版实际躺在哪一层），猜不出就留空。</para>
    /// <para>不写进 KeyOf(asset, slot)：键里加版本会让老账整体读不回（同一个包同一个资产 = 同一条槽，
    /// 这是"一张表只能有一份内容"那条规矩的落点）。</para>
    /// </summary>
    public string Version { get; set; } = "";
    /// <summary>原包备份的文件夹名（asset-packs 下；空 = 老账或备份失败，打开文件夹时回落到副本目录）。</summary>
    public string PackDir { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>true = 由包里的 PNG 编码而来；false = 包自带的 XNB 原样拷。</summary>
    public bool Encoded { get; set; }
    /// <summary>这个包【没装上】的文件数与头几个的名字。装成功不等于全装上了：
    /// 一个给 1.5 画的立绘包在 1.6 上会有十几张尺寸对不上，只报"装了 88 张"的话，
    /// 人只会去问"为什么那几个角色没变"，而那时账上已经没有答案了。</summary>
    public int SkippedCount { get; set; }
    public string SkippedNote { get; set; } = "";
    /// <summary>装上了、但尺寸与本体那张不同的条目数与逐条说明。
    /// 它们【不是】失败：游戏按固定格位切帧，格位不变就只是少了/多了几帧表情，不会画错人。
    /// 早先这类直接算"不适配"被拒掉，可实测 Content Patcher 照装照用
    /// （用户 Mods 里的 [CP] Portrait Anime Mods OhoDavi，Elliott_Beach.png 是 128×128，
    /// 而本体 Portraits/Elliott_Beach 是 128×320）。既然装了，就得让玩家看得见，
    /// 否则"这个角色表情少了"会变成一个无处可问的怪现象。</summary>
    public int ResizedCount { get; set; }
    public string ResizedNote { get; set; } = "";
    /// <summary>这一个包里【撞到同一张目标的其它配色】的标签（按包内所在文件夹名）。
    /// <para>为什么要它：实测 Yomi's Golden Princess Hairstyle 把 4 个配色（三色/可染白/金/黑）
    /// 各自带一份 png+xnb，8 个文件全指 <c>Characters/Farmer/hairstyles</c> —— 作者给的就是
    /// "同一张目标的多个版本"。老判定是"多张撞一个就整组不装"，等于一个都用不上；
    /// 现在取排序后第一个当默认，其余全记在这里，界面上在对象卡的版本行里换
    /// （<see cref="VariantLabel"/> 是此刻在位的那个）。</para>
    /// <para>空表 = 这目标包里只有一份，界面上不出配色条。字节不另存，
    /// 换配色时从 <see cref="PackDir"/> 那份原包备份重新解析，所以老账（没备份）换不了。</para>
    /// </summary>
    public List<string> Variants { get; set; } = new();
    /// <summary>在位那份是 <see cref="Variants"/> 里的哪个标签；空 = 单版本，没有可换的。</summary>
    public string VariantLabel { get; set; } = "";
    /// <summary>每个配色自己的说明（尺寸与本体不同那句）。界面上"按配色分行"必须有这个：
    /// 实测 Yomi 那包 4 个配色的文件全叫 hairstyles.png，从拼成一整串的 ResizedNote 里
    /// 倒推不出哪句属于哪个配色，于是界面只能按文件列 8 行、看着像重复。</summary>
    public List<ContentOverrideVariant> VariantFacts { get; set; } = new();
    public DateTime AppliedAtUtc { get; set; } = DateTime.UtcNow;
    public string PackTitle { get; set; } = "";
    public string PackCover { get; set; } = "";
    /// <summary>N 网官方分类名（立绘 / UI 修改 …），由素材包页从 mod 页号补一次后写回。
    /// 存名字不存 id：分类表本身也缓存在配置里，两处都要联网才能读出一句话，
    /// 而这一页离线也得能按分类分组。没有页号的包留空 ⇒ 归进「未分类」，不编一个分类给它。</summary>
    public string CategoryName { get; set; } = "";
}

/// <summary>一个配色的界面事实：标签 + 它自己的说明。顺序与 <see cref="ContentOverrideRecord.Variants"/> 一致。</summary>
public sealed record ContentOverrideVariant(string Label, string Note);
