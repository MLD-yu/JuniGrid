using System.IO;
using System.Runtime.CompilerServices;

// 假 DepotDownloader：把真 DD 的三种扫码输出剧本演出来 —— 出码后 CM 掉线、张张都掉线、
// 压根没码。这样「死码要撤、要重取一张」的判定能在离线、不碰真实缓存/配置/游戏目录的前提下验。
//
// 子进程就是测试台自己的 exe 改名成 <bin>\tools\DepotDownloader\DepotDownloader.exe 被启动器拉起来的
// （启动器只认这个固定路径，且 EnsureDepotDownloaderAsync 见文件即返回，不会去 GitHub 下载真的）。
// 逻辑必须待在 [ModuleInitializer] 里、不能待在 Program.cs：那个子目录没有 JuniGrid.dll，
// 而 Main 是 async 方法，JIT 一进来就要解析它 —— 实测会先抛 FileNotFoundException。
internal static class FakeDepotDownloader
{
    [ModuleInitializer]
    internal static void Init()
    {
        var script = Environment.GetEnvironmentVariable("JG_FAKE_DD");
        if (string.IsNullOrEmpty(script)) return;

        var nFile = Path.Combine(Path.GetTempPath(), "jg-fake-dd-attempt.txt");
        int n;
        try { int.TryParse(File.ReadAllText(nFile), out n); } catch { n = 0; }
        n++;
        try { File.WriteAllText(nFile, n.ToString()); } catch { }

        Console.Out.WriteLine($"[fake-dd] attempt={n} script={script}");

        // hang = 连 CM 时一句话都不吐（真实表现就是 DD 自己闷头退避 10 轮 ≈64 秒）：
        // 用来验「登录阶段到点没进展就重开一路」这条，而不是让人对着 0% 干等。
        if (script == "hang")
        {
            Console.Out.Flush();
            Thread.Sleep(60_000);
            Environment.Exit(1);
        }

        // 「码画完了但没有下一行」/「Success! 之后 DD 再也不退出」两种真事：
        // art   = 只出码，出完直接退出（旧代码要等下一行非码文才结算 ⇒ 一张码都推不出去）
        // art2  = 出两张码（DD 每 ~22 秒换 challenge），两张都得推到界面
        // authhang = Success! 之后 DD 卡在枚举 license，永远不退出（07:25:48 那次）
        if (script is "art" or "art2" or "authhang")
        {
            if (script == "authhang")
            {
                Console.Out.WriteLine("Success! Next time you can login with -username fakster -remember-password");
                Console.Out.Flush();
                Thread.Sleep(120_000);      // 故意挂住：启动器必须在几秒内自己掐掉
                Environment.Exit(0);
            }
            var files = (Environment.GetEnvironmentVariable("JG_FAKE_DD_ART") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            Console.Out.WriteLine("Use the Steam Mobile App to sign in with this QR code:");
            Console.Out.Flush();
            if (files.Length > 0) PrintArt(files[0]);
            Thread.Sleep(1600);             // 让「最后一行后再无新行」的判定有机会触发
            if (script == "art2" && files.Length > 1)
            {
                Console.Out.WriteLine("The QR code has changed:");
                Console.Out.WriteLine("Use the Steam Mobile App to sign in with this QR code:");
                Console.Out.Flush();
                PrintArt(files[1]);
                Thread.Sleep(1600);
            }
            Console.Out.Flush();
            Environment.Exit(0);            // 注意：码之后不再打任何一行
        }

        if (script != "noqr")
        {
            // 「Use the Steam Mobile App…」= 启动器判定「出过码」的锚点；URL 行走 s.team 正则
            Console.Out.WriteLine("Use the Steam Mobile App to confirm the following sign in request:");
            Console.Out.WriteLine($"https://s.team/q/FAKE{n:X2}CHALLENGE");
        }
        Console.Out.Flush();
        Thread.Sleep(400);

        if (script == "recover" && n >= 2 || script == "ok")
        {
            Console.Out.WriteLine("Success! Next time you can login with -username fakster -remember-password");
            Console.Out.Flush();
            Environment.Exit(0);
        }
        // 下面四行是 2026-09-19 03:45:35 真实日志里的死法（先掉线、再认证失败、最后没拿到凭据）
        Console.Out.WriteLine("Retrying Steam3 connection (TryAnotherCM)...Lost connection to Steam. Reconnecting");
        Console.Out.WriteLine("Failed to authenticate with Steam: SteamKit2.AsyncJobFailedException");
        Console.Out.WriteLine("Unable to get steam3 credentials.");
        Console.Out.WriteLine("ERROR: InitializeSteam failed");
        Console.Out.Flush();
        Environment.Exit(1);
    }

    // 码行原样吐出去：行尾空格是白模块，不能 Trim。父进程按 GBK(936) 读，子进程默认也是系统 ANSI，
    // 方块字符直接写就行（这里刻意不 RegisterProvider，那个程序集在假 DD 的目录下解析不到）。
    static void PrintArt(string file)
    {
        string[] rows;
        try { rows = File.ReadAllLines(file); } catch { return; }
        foreach (var r in rows) Console.Out.WriteLine(r);
    }
}
