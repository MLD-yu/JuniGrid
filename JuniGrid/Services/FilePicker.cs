using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 单文件选择对话框。<b>目前唯一的用途是让用户挑「启动项」那个 exe</b> ——
/// 同 <see cref="FolderPicker"/> 一条约定：从当前填的路径起步（填的是文件就从它所在目录起步），
/// 起点无效就交回系统默认，不拦人。填的是目录时也当起步目录，用户不必先把输入框清空。
/// </summary>
public static class FilePicker
{
    /// <summary>挑一个可执行文件；取消返回 null。</summary>
    public static string? PickExe(string title, string? startPath)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = LocService.Tr("可执行文件 (*.exe)") + "|*.exe|" + LocService.Tr("所有文件 (*.*)") + "|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        var start = StartDir(startPath);
        if (start is not null) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>起点目录：给的就是目录 → 它自己；给的是文件 → 它所在目录；都不成立 → null。</summary>
    private static string? StartDir(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            var full = Path.GetFullPath(p.Trim().Trim('"'));
            if (Directory.Exists(full)) return full;
            var dir = Path.GetDirectoryName(full);
            return dir is not null && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }
}
