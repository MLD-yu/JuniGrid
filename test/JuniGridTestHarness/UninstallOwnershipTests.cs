using JuniGrid;

namespace JuniGridTestHarness;

/// <summary>
/// 卸载向导认不认"这条注册表登记说的是我"。
/// <para>起因：卸载项与快捷方式都是<b>全局唯一</b>的（同一个 AppId，后装的写同一个键），
/// 而换目录安装会把老目录留下 —— 老目录里的 <c>Uninstall.exe</c> 是主程序的整份副本，
/// 照样能双击。不加这道判，用户双击老副本就把指向新装的卸载项和桌面图标抹掉了，
/// 新装那份却还好端端跑着 ⇒ 控制面板里"卸载"从此消失。</para>
/// </summary>
public class UninstallOwnershipTests
{
    [Theory]
    [InlineData(@"E:\JuniGrid2", @"E:\JuniGrid2")]                      // 完全相同
    [InlineData(@"e:\junigrid2", @"E:\JuniGrid2")]                      // 盘符/大小写不同
    [InlineData(@"E:\JuniGrid2\", @"E:\JuniGrid2")]                     // 登记值带尾斜杠
    [InlineData(@"E:\JuniGrid2\\", @"E:\JuniGrid2\")]                   // 两边都带
    [InlineData(@"E:/JuniGrid2", @"E:\JuniGrid2")]                      // 混用分隔符
    [InlineData(@"E:\JuniGrid\..\JuniGrid2", @"E:\JuniGrid2")]          // 绕过来的写法
    public void SameDirectory_IsRecognised(string registered, string own)
        => Assert.True(UninstallWindow.KeyDescribesThisDir(registered, own));

    [Theory]
    [InlineData(@"E:\JuniGrid1", @"E:\JuniGrid2")]                      // 上一份安装留下的幽灵副本
    [InlineData(@"E:\JuniGrid2\new", @"E:\JuniGrid2")]                  // 新装套在老目录里
    [InlineData(@"E:\JuniGrid", @"E:\JuniGrid2")]                       // 只差一个后缀也算不同份
    public void AnotherDirectory_IsNotMine(string registered, string own)
        => Assert.False(UninstallWindow.KeyDescribesThisDir(registered, own));

    [Theory]
    [InlineData(null)]                                                  // 键没了 / 读不到
    [InlineData("")]
    [InlineData("   ")]
    public void MissingRegistration_IsNotMine(string? registered)
        => Assert.False(UninstallWindow.KeyDescribesThisDir(registered, @"E:\JuniGrid2"));
}
