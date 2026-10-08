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

    /// <summary>两个写法是否是同一个版本号。N 网「文件版本」栏与 smapi.io 索引常是
    /// 同一版的两种写法（实测 mod 30482：文件写 "1.4"、索引写 "1.4.0"），
    /// 用字面相等判「已拥有这一版」就会漏。</summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        // 带预发布/构建后缀时退回字面比较：1.6.4 不得把 1.6.4-beta.2 吞成已装
        if (HasSuffix(a) || HasSuffix(b)) return false;
        var ap = Parts(a); var bp = Parts(b);
        var len = Math.Max(ap.Length, bp.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < ap.Length ? ap[i] : 0;
            var y = i < bp.Length ? bp[i] : 0;
            if (x != y) return false;
        }
        return len > 0;
    }

    private static bool HasSuffix(string v)
        => v.Trim().TrimStart('v', 'V').IndexOfAny(new[] { '-', '+', ' ' }) >= 0;

    /// <summary>v1.7.x：a 是否严格低于 b —— 给 manifest 的依赖 MinimumVersion 用。
    /// 与 <see cref="IsNewer"/> 的区别就在预发布标签上：SMAPI 按 SemVer 判依赖，
    /// <c>0.6.4-beta</c> 在它眼里<b>低于</c> <c>0.6.4</c>，而 IsNewer 把后缀整段剥掉，
    /// 那种场合会判成"够用"而跟着 SMAPI 一起沉默（这次 完美手册 被跳过就是沉默的另一种写法）。
    /// 任一侧读不出版本号一律返回 false：判不准就不指控，宁可漏报也不给一条假的「版本过旧」。</summary>
    public static bool IsOlderThan(string? a, string? b)
        => TrySplit(a, out var ap, out var apre) && TrySplit(b, out var bp, out var bpre)
           && Compare(ap, apre, bp, bpre) < 0;

    /// <summary>拆成「核心数字段」+「预发布段」。构建元数据（+ 之后）不参与比较。</summary>
    private static bool TrySplit(string? v, out int[] core, out string[] pre)
    {
        core = Array.Empty<int>(); pre = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(v)) return false;
        var s = v.Trim();
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var preRaw = dash > 0 ? s[(dash + 1)..] : "";
        if (dash > 0) s = s[..dash];
        var segs = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length is 0 or > 3) return false;
        var nums = new int[segs.Length];
        for (var i = 0; i < segs.Length; i++)
            if (!int.TryParse(segs[i], out nums[i])) return false;
        core = nums;
        pre = preRaw.Length == 0 ? Array.Empty<string>() : preRaw.Split('.');
        return true;
    }

    private static int Compare(int[] ac, string[] ap, int[] bc, string[] bp)
    {
        for (var i = 0; i < Math.Max(ac.Length, bc.Length); i++)
        {
            var x = i < ac.Length ? ac[i] : 0;
            var y = i < bc.Length ? bc[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (ap.Length == 0) return bp.Length == 0 ? 0 : 1;      // 同号：正式版 > 预发布版
        if (bp.Length == 0) return -1;
        for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            var an = int.TryParse(ap[i], out var na);
            var bn = int.TryParse(bp[i], out var nb);
            if (an && bn) { var c = na.CompareTo(nb); if (c != 0) return c; }
            else if (an) return -1;                              // 数字段 < 字母段
            else if (bn) return 1;
            else { var c = string.CompareOrdinal(ap[i], bp[i]); if (c != 0) return c; }
        }
        return ap.Length.CompareTo(bp.Length);                   // 前缀全等：段少的更低
    }

    private static int[] Parts(string v)
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
}
