using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// OLE 拖放目标的路径解析层 —— 直接喂一个带 CF_HDROP 的 <see cref="IDataObject"/>，
/// 钉住三件事：只认文件列表格式、HDROP 的宽字符路径解析（含中文/空格）、
/// Enter/Leave 驱动蒙层。OLE 注册本身要在真机上验，这里管不到。
/// </summary>
public class WindowFileDropTargetTests
{
    private const int CF_HDROP = 15;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);

    [Fact]
    public void Drop_WithSingleZip_DeliversPath()
    {
        var path = @"C:\Users\me\Downloads\Stardew Mod 1.2.zip";
        string[]? delivered = null;
        var target = new WindowFileDropTarget(_ => { }, files => delivered = files);
        var effect = DropEffects.None;

        target.Drop(new HdropDataObject(path), 0, default, ref effect);

        Assert.Equal(DropEffects.Copy, effect);
        Assert.NotNull(delivered);
        Assert.Equal(new[] { path }, delivered);
    }

    [Fact]
    public void Drop_WithUnicodePath_ParsesExactly()
    {
        var path = @"D:\游戏 mods\星露谷 测试包.7z";
        string[]? delivered = null;
        var target = new WindowFileDropTarget(_ => { }, files => delivered = files);
        var effect = DropEffects.None;

        target.Drop(new HdropDataObject(path), 0, default, ref effect);

        Assert.Equal(DropEffects.Copy, effect);
        Assert.Equal(new[] { path }, delivered);
    }

    [Fact]
    public void DragEnter_WithFiles_ShowsHint_DragLeave_HidesIt()
    {
        var hints = new List<bool>();
        var target = new WindowFileDropTarget(hints.Add, _ => { });
        var effect = DropEffects.None;

        target.DragEnter(new HdropDataObject(@"C:\x.rar"), 0, default, ref effect);
        Assert.Equal(DropEffects.Copy, effect);
        Assert.True(target.IsDragging);
        Assert.Equal(new[] { true }, hints);

        target.DragLeave();
        Assert.False(target.IsDragging);
        Assert.Equal(new[] { true, false }, hints);
    }

    [Fact]
    public void DragEnter_WithoutFileList_IsNoneAndNoHint()
    {
        var hints = new List<bool>();
        var target = new WindowFileDropTarget(hints.Add, _ => { });
        var effect = DropEffects.Copy;

        target.DragEnter(new NonFileDataObject(), 0, default, ref effect);

        Assert.Equal(DropEffects.None, effect);
        Assert.False(target.IsDragging);
        Assert.Empty(hints);
    }

    /// <summary>最小 CF_HDROP 数据对象：每次 GetData 现分配一块 HGLOBAL，
    /// 交给被测方按 STGMEDIUM 规则释放（不能复用 —— Drop 前往往先被 DragEnter 取走一次）。</summary>
    private sealed class HdropDataObject : IDataObject
    {
        private readonly string[] _paths;
        public HdropDataObject(params string[] paths) => _paths = paths;

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (format.cfFormat != CF_HDROP)
                throw new COMException("not a file drop", DV_E_FORMATETC);
            medium = new STGMEDIUM
            {
                tymed = TYMED.TYMED_HGLOBAL,
                unionmember = CreateHDrop(_paths),
                pUnkForRelease = null,
            };
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int QueryGetData(ref FORMATETC format) => format.cfFormat == CF_HDROP ? 0 : DV_E_FORMATETC;
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        { formatOut = formatIn; return unchecked((int)0x80040003); }   // DATA_S_SAMEFORMATETC
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC? EnumFormatEtc(DATADIR direction) => null;
        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) { enumAdvise = null; return unchecked((int)0x80040003); }

        /// <summary>DROPFILES 头（x64 下 20 字节）+ 双 NUL 结尾的 UTF-16 路径列表。</summary>
        private static IntPtr CreateHDrop(string[] paths)
        {
            var text = new List<byte>();
            foreach (var p in paths)
                text.AddRange(Encoding.Unicode.GetBytes(p + "\0"));
            text.AddRange(new byte[] { 0, 0 });

            int total = 20 + text.Count;
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)total);
            if (h == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed");
            IntPtr pMem = GlobalLock(h);
            try
            {
                Marshal.WriteInt32(pMem, 0, 20);    // pFiles：路径列表相对块首的偏移
                Marshal.WriteInt32(pMem, 16, 1);    // fWide = TRUE
                Marshal.Copy(text.ToArray(), 0, pMem + 20, text.Count);
            }
            finally { GlobalUnlock(h); }
            return h;
        }
    }

    /// <summary>剪贴板里只有文本、没有 CF_HDROP 的那种拖拽。</summary>
    private sealed class NonFileDataObject : IDataObject
    {
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
            => throw new COMException("not a file drop", DV_E_FORMATETC);
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int QueryGetData(ref FORMATETC format) => DV_E_FORMATETC;
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        { formatOut = formatIn; return unchecked((int)0x80040003); }
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC? EnumFormatEtc(DATADIR direction) => null;
        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) { enumAdvise = null; return unchecked((int)0x80040003); }
    }

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
