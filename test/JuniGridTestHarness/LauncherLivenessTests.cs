using System.Diagnostics;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>判活读 <see cref="Process.HasExited"/> 必须永不抛。
/// <para>起因：星谷闪退后按钮永久停在「运行中…」、点 ✕ 没有任何反应，而系统里早就没有游戏进程。
/// 链条是 <c>LaunchSmapiCore</c> 里 <c>_smapiProcess</c> 先赋值、<c>proc.Start()</c> 才抛，
/// catch 只 return 失败不清字段 —— 之后每一次 <c>IsGameRunning</c> 都从这个关联不上进程的
/// 对象上抛 InvalidOperationException，而 <c>AccountPanel.PollRunningAsync</c> 当年只 catch
/// OperationCanceledException，于是轮询线程当场死掉，<c>_isRunning</c> 永久冻在最后一次读到的
/// true 上（本机 juni-grid.log 2026-10-16 03:48:24 记的就是这一条）。</para>
/// <para>下面四个用例把两端都钉住：两种「读不到」的形状必须返回 false 而不是抛，
/// 一个真活着的进程必须返回 true —— 少了后者，把 IsAlive 改成无脑 return false 也能全绿。</para></summary>
public class LauncherLivenessTests
{
    [Fact]
    public void IsAlive_Null_ReturnsFalse()
    {
        Assert.False(LauncherService.IsAlive(null));
    }

    [Fact]
    public void IsAlive_NeverStartedProcess_ReturnsFalseWithoutThrowing()
    {
        // Start() 失败后留在 _smapiProcess 里的那个对象就是这个形状：HasExited 直接抛
        // "No process is associated with this object."
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = @"C:\JuniGrid-Test-Does-Not-Exist\StardewModdingAPI.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        using (p) Assert.False(LauncherService.IsAlive(p));
    }

    [Fact]
    public void IsAlive_DisposedProcess_ReturnsFalseWithoutThrowing()
    {
        var p = new Process();
        p.Dispose();
        Assert.False(LauncherService.IsAlive(p));
    }

    [Fact]
    public void IsAlive_LiveProcess_ReturnsTrue()
    {
        Assert.True(LauncherService.IsAlive(Process.GetCurrentProcess()));
    }
}
