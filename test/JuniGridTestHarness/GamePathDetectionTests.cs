using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>游戏目录自动检测的两个纯函数：Steam 库列表解析、盘就绪性闸门。
/// 慢的根因是「在没就绪的盘上做 Directory.Exists 会阻塞到设备超时」，
/// 这两条决定了哪些路径会被真正摸一下。</summary>
public class GamePathDetectionTests
{
    // ---------------- ParseLibraryFolders：两种 vdf 格式都要认

    [Fact]
    public void NewFormat_ReadsAllPathEntries()
    {
        // 真实 libraryfolders.vdf 的片段（"path" 里的反斜杠是转义过的）
        var text = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"413150"		"123456789"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """;

        var libs = GameService.ParseLibraryFolders(text).ToList();

        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libs);
    }

    [Fact]
    public void LegacyFormat_ReadsNumberedEntries()
    {
        // 老版本 vdf 直接 "1" "D:\\SteamLibrary"，没有 path 键
        var text = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1234567890"
            	"ContentStatsID"		"-1234567890"
            	"1"		"D:\\SteamLibrary"
            	"2"		"E:\\Games\\SteamLibrary"
            }
            """;

        var libs = GameService.ParseLibraryFolders(text).ToList();

        Assert.Equal(new[] { @"D:\SteamLibrary", @"E:\Games\SteamLibrary" }, libs);
    }

    [Fact]
    public void AppsBlockNumbers_AreNotMistakenForLibraries()
    {
        // apps 里的 "413150" "12345" 是纯数字，不能当成库路径
        var text = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Steam"
            		"apps"
            		{
            			"413150"		"123456789"
            			"2408820"		"987654321"
            		}
            	}
            }
            """;

        Assert.Equal(new[] { @"C:\Steam" }, GameService.ParseLibraryFolders(text).ToList());
    }

    [Fact]
    public void EmptyText_YieldsNothing()
        => Assert.Empty(GameService.ParseLibraryFolders(""));

    // ---------------- DriveUsable：会阻塞的盘必须被拦在探测之前

    [Fact]
    public void UncPath_IsRejected()
        => Assert.False(GameService.DriveUsable(@"\\nas\games\Stardew Valley"));

    [Fact]
    public void EmptyPath_IsRejected()
    {
        Assert.False(GameService.DriveUsable(""));
        Assert.False(GameService.DriveUsable("   "));
    }

    [Fact]
    public void LocalReadyDrive_IsAllowed()
    {
        // 临时目录一定在本地就绪盘上（这条同时防止把「正常盘」也误杀）
        Assert.True(GameService.DriveUsable(Path.GetTempPath()));
    }

    [Fact]
    public void NoSuchDriveLetter_IsRejected()
    {
        // 不存在根目录的盘符（DriveType.NoRootDirectory）不该进探测队列
        var missing = @"Z:\jg-not-a-real-drive";
        if (DriveInfo.GetDrives().Any(d => string.Equals(d.Name, @"Z:\", StringComparison.OrdinalIgnoreCase)))
            return;   // 本机真有 Z: 就跳过这条，避免误报
        Assert.False(GameService.DriveUsable(missing));
    }
}
