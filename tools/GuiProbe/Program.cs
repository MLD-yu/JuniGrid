using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

// 真机界面驱动工具：截屏 + 枚举窗口 + UIA 树 + 鼠标键盘注入。
// 用途：在没有 computer-use 通道时，等效地驱动本机 GUI 程序做实测。
namespace GuiProbe;

public static class Program
{
    private const int MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const int MOUSEEVENTF_LEFTUP = 0x0004;
    private const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const int MOUSEEVENTF_RIGHTUP = 0x0010;

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static int Main(string[] args)
    {
        SetProcessDPIAware();
        if (args.Length == 0) { Console.WriteLine(Usage); return 1; }
        try
        {
            switch (args[0])
            {
                case "shot": Shot(args.Length > 1 ? args[1] : @"E:\junigrid\shot.png"); return 0;
                case "crop": Crop(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6])); return 0;
                case "grid": Grid(args[1], args[2], int.Parse(args[3])); return 0;
                case "pix": Pix(args[1], int.Parse(args[2]), int.Parse(args[3])); return 0;
                case "findcolor": FindColor(args[1], args[2], int.Parse(args[3])); return 0;
                case "wins": Wins(); return 0;
                case "tree": Tree(args); return 0;
                case "find": Find(args); return 0;
                case "click": Click(args); return 0;
                case "clickxy": ClickXY(int.Parse(args[1]), int.Parse(args[2]), args.Length > 3 && args[3] == "right"); return 0;
                case "front": Front(FindHwnd(args[1])); return 0;
                case "keys": Keys(string.Join(' ', args.Skip(1))); return 0;
                default: Console.WriteLine(Usage); return 1;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR " + ex.Message); return 2; }
    }

    private static string Usage =>
        "GuiProbe <命令>\n" +
        "  shot [out.png]                  全屏截图\n" +
        "  wins                            列出顶层窗口\n" +
        "  tree <窗口名子串> [maxDepth]     打印 UIA 树\n" +
        "  find <窗口名子串> <名字子串>      查找元素\n" +
        "  click <窗口名子串> <名字或Id>     点击元素\n" +
        "  clickxy <x> <y> [right]         按坐标点击\n" +
        "  front <窗口名子串>               置前\n" +
        "  keys <文本>                      发送按键文本";

    private static void Shot(string path)
    {
        var b = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(b.Width, b.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine(JsonSerializer.Serialize(new { path, x = b.X, y = b.Y, width = b.Width, height = b.Height }));
    }

    private static void Crop(string src, string dst, int x, int y, int w, int h)
    {
        using var full = new Bitmap(src);
        var rect = new Rectangle(x, y, w, h);
        rect.Intersect(new Rectangle(0, 0, full.Width, full.Height));
        using var part = full.Clone(rect, full.PixelFormat);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
        part.Save(dst, ImageFormat.Png);
        Console.WriteLine(JsonSerializer.Serialize(new { dst, x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height }));
    }

    private static void Grid(string src, string dst, int step)
    {
        using var full = new Bitmap(src);
        using var g = Graphics.FromImage(full);
        using var pen = new Pen(Color.FromArgb(255, 255, 0, 0), 1);
        using var font = new Font("Consolas", 12, FontStyle.Bold);
        using var bg = new SolidBrush(Color.FromArgb(200, 255, 255, 0));
        using var fg = new SolidBrush(Color.Black);
        for (int x = 0; x < full.Width; x += step)
        {
            g.DrawLine(pen, x, 0, x, full.Height);
            g.FillRectangle(bg, x, 0, 42, 18);
            g.DrawString(x.ToString(), font, fg, x + 1, 1);
        }
        for (int y = 0; y < full.Height; y += step)
        {
            g.DrawLine(pen, 0, y, full.Width, y);
            g.FillRectangle(bg, 0, y, 52, 18);
            g.DrawString(y.ToString(), font, fg, 1, y + 1);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
        full.Save(dst, ImageFormat.Png);
        Console.WriteLine(JsonSerializer.Serialize(new { dst, width = full.Width, height = full.Height, step }));
    }

    private static void Pix(string src, int x, int y)
    {
        using var bmp = new Bitmap(src);
        var c = bmp.GetPixel(x, y);
        Console.WriteLine(JsonSerializer.Serialize(new { x, y, r = c.R, g = c.G, b = c.B, hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}" }));
    }

    private static void FindColor(string src, string hex, int tol)
    {
        int tr = Convert.ToInt32(hex.Substring(1, 2), 16);
        int tg = Convert.ToInt32(hex.Substring(3, 2), 16);
        int tb = Convert.ToInt32(hex.Substring(5, 2), 16);
        using var bmp = new Bitmap(src);
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var buf = new byte[w * h * 4];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(data);

        var mask = new bool[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int b = buf[i * 4], g = buf[i * 4 + 1], r = buf[i * 4 + 2];
            if (Math.Abs(r - tr) <= tol && Math.Abs(g - tg) <= tol && Math.Abs(b - tb) <= tol) mask[i] = true;
        }

        var seen = new bool[w * h];
        var boxes = new List<(int X, int Y, int W, int H, int Area)>();
        var stack = new Stack<int>();
        for (int i = 0; i < w * h; i++)
        {
            if (!mask[i] || seen[i]) continue;
            stack.Push(i); seen[i] = true;
            int minX = w, minY = h, maxX = 0, maxY = 0, area = 0;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                area++;
                int px = p % w, py = p / w;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
                if (px > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (px < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (py > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (py < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            if (area >= 200) boxes.Add((minX, minY, maxX - minX + 1, maxY - minY + 1, area));
        }
        boxes.Sort((a, b) => b.Area.CompareTo(a.Area));
        Console.WriteLine($"目标 #{tr:X2}{tg:X2}{tb:X2} 容差 {tol}，命中 {boxes.Count} 块");
        foreach (var bx in boxes.Take(25))
            Console.WriteLine($"  x={bx.X} y={bx.Y} w={bx.W} h={bx.H} area={bx.Area} center={bx.X + bx.W / 2},{bx.Y + bx.H / 2}");
    }

    private static void Wins()
    {
        foreach (var (hwnd, title, cls, pid) in EnumTop())
        {
            if (string.IsNullOrWhiteSpace(title)) continue;
            string pname = "";
            try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { }
            Console.WriteLine($"{hwnd.ToInt64(),10} pid={pid,7} {cls,-32} [{pname}] {title}");
        }
    }

    private static List<(IntPtr Hwnd, string Title, string Class, uint Pid)> EnumTop()
    {
        var list = new List<(IntPtr, string, string, uint)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            var cb = new StringBuilder(256);
            GetClassName(h, cb, 256);
            GetWindowThreadProcessId(h, out var pid);
            list.Add((h, sb.ToString(), cb.ToString(), pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static IntPtr FindHwnd(string sub)
    {
        foreach (var (h, t, _, _) in EnumTop())
            if (t.Contains(sub, StringComparison.OrdinalIgnoreCase)) return h;
        throw new InvalidOperationException("找不到窗口: " + sub);
    }

    private static void Front(IntPtr h)
    {
        ShowWindow(h, 9);
        SetForegroundWindow(h);
        Console.WriteLine("focused " + h.ToInt64());
    }

    private static void Tree(string[] args)
    {
        var h = FindHwnd(args[1]);
        Front(h);
        int depth = args.Length > 2 ? int.Parse(args[2]) : 6;
        var el = AutomationElement.FromHandle(h);
        Dump(el, 0, depth);
    }

    private static void Dump(AutomationElement el, int level, int maxDepth)
    {
        if (level > maxDepth) return;
        if (el == null) return;
        var c = el.Current;
        var r = c.BoundingRectangle;
        string rect = r.IsEmpty ? "-" : $"{(int)r.X},{(int)r.Y} { (int)r.Width}x{(int)r.Height}";
        Console.WriteLine($"{new string(' ', level * 2)}[{c.ControlType.ProgrammaticName.Replace("ControlType.", "")}] name={Quote(c.Name)} id={Quote(c.AutomationId)} class={Quote(c.ClassName)} {rect}");
        var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement child in children) Dump(child, level + 1, maxDepth);
    }

    private static string Quote(string? s) => "\"" + (s ?? "").Replace("\n", " ").Replace("\"", "'") + "\"";

    private static void Find(string[] args)
    {
        var h = FindHwnd(args[1]);
        Front(h);
        var root = AutomationElement.FromHandle(h);
        var cond = new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, args[2], PropertyConditionFlags.IgnoreCase),
            new PropertyCondition(AutomationElement.AutomationIdProperty, args[2], PropertyConditionFlags.IgnoreCase));
        var found = root.FindAll(TreeScope.Descendants, cond);
        Console.WriteLine($"命中 {found.Count} 个");
        foreach (AutomationElement e in found)
        {
            var c = e.Current;
            var r = c.BoundingRectangle;
            Console.WriteLine($"  [{c.ControlType.ProgrammaticName.Replace("ControlType.", "")}] name={Quote(c.Name)} id={Quote(c.AutomationId)} {Center(r)}");
        }
    }

    private static string Center(System.Windows.Rect r) => r.IsEmpty ? "-" : $"center={(int)(r.X + r.Width / 2)},{(int)(r.Y + r.Height / 2)}";

    private static void Click(string[] args)
    {
        var h = FindHwnd(args[1]);
        Front(h);
        Thread.Sleep(250);
        var root = AutomationElement.FromHandle(h);
        AutomationElement? target = null;
        var exact = new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, args[2], PropertyConditionFlags.IgnoreCase),
            new PropertyCondition(AutomationElement.AutomationIdProperty, args[2], PropertyConditionFlags.IgnoreCase));
        target = root.FindFirst(TreeScope.Descendants, exact);
        if (target == null)
        {
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                if (e.Current.Name?.Contains(args[2], StringComparison.OrdinalIgnoreCase) == true) { target = e; break; }
            }
        }
        if (target == null) { Console.WriteLine("NOT FOUND"); return; }
        var c = target.Current;
        var r = c.BoundingRectangle;
        Console.WriteLine($"target [{c.ControlType.ProgrammaticName.Replace("ControlType.", "")}] name={Quote(c.Name)} {Center(r)}");
        if (r.IsEmpty) { Console.WriteLine("EMPTY RECT"); return; }
        int x = (int)(r.X + r.Width / 2), y = (int)(r.Y + r.Height / 2);
        SetCursorPos(x, y);
        Thread.Sleep(120);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Console.WriteLine("clicked " + x + "," + y);
    }

    private static void ClickXY(int x, int y, bool right)
    {
        SetCursorPos(x, y);
        Thread.Sleep(120);
        int down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        int up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        mouse_event(down, 0, 0, 0, IntPtr.Zero);
        mouse_event(up, 0, 0, 0, IntPtr.Zero);
        Console.WriteLine($"clicked {x},{y} right={right}");
    }

    private static void Keys(string text)
    {
        SendKeys.SendWait(text);
        Console.WriteLine("sent " + text.Length + " chars");
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
