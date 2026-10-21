using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace JuniGrid.Services;

/// <summary>
/// 直接注册在窗口 HWND 上的 OLE 拖放目标 —— 接管从资源管理器拖进来的文件。
/// <para><b>为什么不用 WPF 的 AllowDrop/Drop 事件</b>（2026-10-21 实机证伪）：
/// Composition 宿主在窗口里只有一个 0×0 的 <c>Chrome_WidgetWin_0</c> 子窗、
/// 不覆盖客户区。主窗 HWND 上本来就有一个 OLE target：旧构建里是我们自己开
/// <c>Window.AllowDrop</c> 时 WPF 注册的（<c>OleDropTargetInterface</c> 窗口属性
/// 可读，vtable 落在 coreclr 托管 thunk）；当前构建不设 AllowDrop，init 时
/// <c>revoke(main)</c> 仍返回 S_OK —— WebView2 自己也会在主窗注册一个。
/// 两版都轮不到窗口的 DragEnter/Drop：WPF 的 target 以<b>命中元素</b>的
/// <c>AllowDrop</c> 为准，光标下是 WebView2 控件，它没开，于是 DragEnter
/// 直接判 None —— 事件一次都不会触发，光标全程禁止。</para>
/// <para>这里的做法：在 WebView2 初始化完成后 <see cref="RevokeDragDrop"/> 把既有
/// 目标摘掉，再 <see cref="RegisterDragDrop"/> 换上我们自己的（不经过 WPF 的
/// 命中判定）。注册与回调都发生在 WPF UI 线程（STA + 消息泵），可以直接碰 WPF 元素。</para>
/// <para><see cref="IDropTarget"/> / <see cref="POINTL"/> / <see cref="DropEffects"/>
/// 不在 .NET BCL 里，按 Win32 头文件的 IID 与 vtable 顺序自行声明。</para>
/// </summary>
[ComVisible(true)]
internal sealed class WindowFileDropTarget : IDropTarget
{
    private const int CF_HDROP = 15;   // 标准"文件列表"剪贴板格式（HDROP 内存块）

    private readonly Action<bool> _setHover;     // 蒙层显隐（已在 UI 线程）
    private readonly Action<string[]> _onFiles;  // 松手，交路径（已在 UI 线程）
    private readonly Action<string>? _log;       // 诊断日志（startup.log）
    private int _depth;                          // Enter/Leave 计数，防拖拽中途经过子区域闪烁

    public WindowFileDropTarget(Action<bool> setHover, Action<string[]> onFiles, Action<string>? log = null)
    {
        _setHover = setHover;
        _onFiles = onFiles;
        _log = log;
    }

    /// <summary>拖拽正压在本窗口上。重注册 target 会打断进行中的 OLE 拖拽，调用方必须先看这个。</summary>
    public bool IsDragging => _depth > 0;

    public void DragEnter(IDataObject? dataObj, uint keyState, POINTL pt, ref DropEffects effect)
    {
        bool has = HasFiles(dataObj);
        effect = has ? DropEffects.Copy : DropEffects.None;
        _log?.Invoke($"[drag] DragEnter hasFiles={has} => {(has ? "Copy" : "None")} at=({pt.x},{pt.y})");
        if (has)
        {
            _depth++;
            _setHover(true);
        }
    }

    public void DragOver(uint keyState, POINTL pt, ref DropEffects effect)
        => effect = _depth > 0 ? DropEffects.Copy : DropEffects.None;

    public void DragLeave()
    {
        _log?.Invoke($"[drag] DragLeave depth={_depth}");
        if (_depth > 0) _depth--;
        if (_depth == 0) _setHover(false);
    }

    public void Drop(IDataObject? dataObj, uint keyState, POINTL pt, ref DropEffects effect)
    {
        _log?.Invoke($"[drag] Drop depth={_depth} at=({pt.x},{pt.y})");
        _depth = 0;
        _setHover(false);
        var files = ReadFiles(dataObj);
        _log?.Invoke($"[drag] Drop files={files.Length}" + (files.Length > 0 ? " first=" + files[0] : ""));
        effect = files.Length > 0 ? DropEffects.Copy : DropEffects.None;
        if (files.Length > 0) _onFiles(files);
    }

    /// <summary>拖拽中途窗口失焦等异常路径：强制收蒙层，防止卡死。</summary>
    public void ForceLeave()
    {
        _depth = 0;
        _setHover(false);
    }

    // ── 子 HWND / HRESULT 诊断 ───────────────────────────────

    internal readonly record struct ChildWindowInfo(
        IntPtr Hwnd, string ClassName, int Width, int Height, bool Visible, int RevokeHr);

    /// <summary>枚举全部后代 HWND 并逐个 <see cref="RevokeDragDrop"/>。
    /// OLE 从光标下最深的窗口开始找 target、没找到才沿父链上溯 —— 子窗上有
    /// target 就轮不到主窗。当前 Composition 宿主的子窗是 0×0、实测没有 target，
    /// 但 WebView2 换版本/换宿主形态时这层要能自证，所以每次接管都扫一遍。</summary>
    public static List<ChildWindowInfo> RevokeChildDropTargets(IntPtr parent)
    {
        var list = new List<ChildWindowInfo>();
        try
        {
            EnumChildWindows(parent, (h, _) =>
            {
                var sb = new StringBuilder(256);
                GetClassNameW(h, sb, sb.Capacity);
                GetWindowRect(h, out var r);
                int hr = RevokeDragDrop(h);
                list.Add(new ChildWindowInfo(h, sb.ToString(),
                    Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top),
                    IsWindowVisible(h), hr));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { AppLog.Warn("Drop", "枚举子窗失败：" + ex.Message); }
        return list;
    }

    /// <summary>读 OLE 写在窗口上的 drop target 指针（<c>OleDropTargetInterface</c> 窗口属性）。
    /// 只读，用来判断当前挂在窗口上的还是不是我们自己的 target —— 不要拿它做任何写操作。</summary>
    public static IntPtr GetDropTargetPointer(IntPtr hwnd)
    {
        try { return GetPropW(hwnd, "OleDropTargetInterface"); }
        catch { return IntPtr.Zero; }
    }

    public static string HrName(int hr) => hr switch
    {
        0 => "S_OK",
        1 => "S_FALSE",
        unchecked((int)0x80040100) => "DRAGDROP_E_NOTREGISTERED",
        unchecked((int)0x80040101) => "DRAGDROP_E_ALREADYREGISTERED",
        unchecked((int)0x80040102) => "DRAGDROP_E_INVALIDHWND",
        _ => $"0x{hr:X8}",
    };

    // ── HDROP 读取 ────────────────────────────────────────────

    private static FORMATETC HdropFormat => new()
    {
        cfFormat = unchecked((short)CF_HDROP),
        dwAspect = (DVASPECT)1,    // DVASPECT_CONTENT
        lindex = -1,
        tymed = (TYMED)1,          // TYMED_HGLOBAL
    };

    private static bool HasFiles(IDataObject? dataObj)
    {
        if (dataObj is null) return false;
        var fe = HdropFormat;
        STGMEDIUM sm = default;
        bool got = false;
        try
        {
            dataObj.GetData(ref fe, out sm);
            got = true;
            return sm.unionmember != IntPtr.Zero;
        }
        catch (COMException) { return false; }          // 没有这个格式 → 不是文件拖放
        catch (Exception ex) { AppLog.Warn("Drop", "HasFiles 异常：" + ex.Message); return false; }
        finally { if (got) ReleaseStgMedium(ref sm); }
    }

    private static string[] ReadFiles(IDataObject? dataObj)
    {
        if (dataObj is null) return Array.Empty<string>();
        var fe = HdropFormat;
        STGMEDIUM sm = default;
        bool got = false;
        try
        {
            dataObj.GetData(ref fe, out sm);
            got = true;
            var hDrop = sm.unionmember;
            if (hDrop == IntPtr.Zero) return Array.Empty<string>();
            // iFile = -1 且 buffer = null → 问文件数量
            uint n = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            var list = new List<string>((int)n);
            for (uint i = 0; i < n; i++)
            {
                uint len = DragQueryFileW(hDrop, i, null, 0);   // buffer=null → 问长度（不含 NUL）
                if (len == 0) continue;
                var sb = new System.Text.StringBuilder((int)len + 1);
                DragQueryFileW(hDrop, i, sb, len + 1);
                list.Add(sb.ToString());
            }
            return list.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Drop", "ReadFiles 异常：" + ex.Message);
            return Array.Empty<string>();
        }
        finally { if (got) ReleaseStgMedium(ref sm); }
    }

    // ── P/Invoke ─────────────────────────────────────────────

    [DllImport("ole32.dll", PreserveSig = true)]
    public static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);

    [DllImport("ole32.dll", PreserveSig = true)]
    public static extern int RevokeDragDrop(IntPtr hwnd);

    /// <summary>RegisterDragDrop 要求线程已 OleInitialize（WPF UI 线程通常已初始化，
    /// 这里显式补一次；已初始化返回 S_FALSE，无害）。</summary>
    [DllImport("ole32.dll")]
    public static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile,
        System.Text.StringBuilder? lpszFile, uint cch);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetPropW(IntPtr hWnd, string lpString);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}

/// <summary>Win32 POINTL（拖放回调里的屏幕坐标，按值传递）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

/// <summary>Win32 IDropTarget（oleidl.h，IID 00000122-0000-0000-C000-000000000046）。
/// vtable 顺序 = 声明顺序，一个都不能多不能少。
/// <para>这个 IID 不能写错：RegisterDragDrop 本身不校验，注册会"成功"，但 OLE 在跨进程
/// 拖拽时按 IID_IDropTarget 封送目标接口 —— GUID 不符就 QueryInterface 失败，
/// 表现正是"光标禁止、回调一次不来"。</para></summary>
[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTarget
{
    void DragEnter(
        [MarshalAs(UnmanagedType.Interface)] IDataObject? dataObj,
        uint keyState, POINTL pt,
        [MarshalAs(UnmanagedType.U4)] ref DropEffects effect);
    void DragOver(uint keyState, POINTL pt,
        [MarshalAs(UnmanagedType.U4)] ref DropEffects effect);
    void DragLeave();
    void Drop(
        [MarshalAs(UnmanagedType.Interface)] IDataObject? dataObj,
        uint keyState, POINTL pt,
        [MarshalAs(UnmanagedType.U4)] ref DropEffects effect);
}

/// <summary>Win32 DROPEFFECTS（DWORD）。</summary>
[Flags]
internal enum DropEffects : uint
{
    None = 0,
    Copy = 1,
    Move = 2,
    Drag = 4,
    Resize = 8,
    Link = 16,
}
