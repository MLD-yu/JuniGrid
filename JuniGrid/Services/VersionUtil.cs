namespace JuniGrid.Services;

/// <summary>语义化版本比较工具（自 Mods.razor / ModDetail.razor 的重复私有副本收敛而来）。</summary>
internal static class VersionUtil
{
    /// <summary>v0.71.8：语义化版本逐段数值比较。旧逻辑在 Version.TryParse 失败时退化成
    /// "字符串不等 = 有更新"——带预发布后缀（0.2.8-beta）、四段式（0.2.8.1）、
    /// 或空格/连字符差异都会让"同版本"被误判为可更新（Event Lookup 已是最新却
    /// 显示更新按钮就是这个根因）。现在剥离 v/V 前缀与 -beta 类后缀后逐段比数值，
    /// 段数不齐按 0 补齐；完全相等 → 不更新。</summary>
    public static bool IsNewer(string remote, string local)
    {
        static int[] Parts(string v)
        {
            var s = v.Trim().TrimStart('v', 'V');
            var dash = s.IndexOfAny(new[] { '-', '+', ' ' });   // 剥离预发布/构建元数据
            if (dash >= 0) s = s[..dash];
            var segs = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var nums = new int[segs.Length];
            for (var i = 0; i < segs.Length; i++)
            {
                var digits = new string(segs[i].TakeWhile(char.IsDigit).ToArray());
                nums[i] = int.TryParse(digits, out var n) ? n : 0;
            }
            return nums;
        }
        var rp = Parts(remote); var lp = Parts(local);
        var len = Math.Max(rp.Length, lp.Length);
        for (var i = 0; i < len; i++)
        {
            var r = i < rp.Length ? rp[i] : 0;
            var l = i < lp.Length ? lp[i] : 0;
            if (r != l) return r > l;
        }
        return false;   // 数值完全相等 → 不是更新
    }
}
