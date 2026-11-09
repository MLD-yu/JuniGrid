using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 文件夹选择对话框的统一入口。<b>浏览必须从输入框当前填的目录开始</b>——
/// 2026-11-09 用户反馈：引导里缓存目录明明已经指到 E 盘，点「浏览」却从别的盘起步，
/// 每次都得再手动点回去。此前四处 OpenFolderDialog（引导游戏/缓存、设置页游戏/缓存）
/// 都没设 InitialDirectory，起点全看 Windows 自己的心情。
/// 起始目录不存在（还没配 / 手滑填错）就交回系统默认，不拦人。
/// </summary>
public static class FolderPicker
{
    /// <summary>弹一个文件夹选择框。从 <paramref name="startDir"/> 开始浏览；
    /// 返回用户选中的目录，取消返回 null。</summary>
    public static string? Pick(string title, string? startDir)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        var start = NormalizeDir(startDir);
        if (start is not null) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    /// <summary>把输入框里的文本整理成可用的起始目录：不存在（或压根不是合法路径）就 null。
    /// 去掉尾部斜杠 —— InitialDirectory 停在「目录本身」而不是「目录里」，体验更对。</summary>
    private static string? NormalizeDir(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try { p = Path.GetFullPath(p.Trim().TrimEnd('\\', '/')); }
        catch { return null; }
        return Directory.Exists(p) ? p : null;
    }
}
