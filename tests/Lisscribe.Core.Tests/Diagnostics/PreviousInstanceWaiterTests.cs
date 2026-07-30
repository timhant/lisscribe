using System.Diagnostics;
using Lisscribe.Core.Diagnostics;
using Xunit;

namespace Lisscribe.Core.Tests.Diagnostics;

/// <summary>
/// 换档重启时，新进程要先等旧进程死透再去抢单实例互斥体。这一组钉的就是那个等待。
///
/// <para>
/// <b>不设测试缝，一律拿真进程验。</b> 被测的东西本身就只有几行，真正会出错的是它与
/// <see cref="Process"/> 的交界处（pid 不存在时抛的是什么、超时返回什么）——那正是缝会盖住的地方。
/// 子进程复用 <see cref="CrossProcessGuardHost"/>：它本来就是「拉起一个活着不放的进程」的现成家伙。
/// </para>
/// </summary>
public sealed class PreviousInstanceWaiterTests
{
    private static string UniqueMutexName() => $"lisscribe-waiter-{Guid.NewGuid():N}";

    /// <summary>
    /// <b>pid 不存在时必须安静地返回，不许把异常放出去。</b>
    /// <c>Process.GetProcessById</c> 对不存在的 pid 抛 <see cref="ArgumentException"/>，
    /// 而这段代码跑在 <c>Main</c> 里、在任何日志装配起来之前——漏接的样子是双击程序一闪即没，
    /// 没有窗口、没有提示，正是本仓花过最多力气去消灭的那种失败形态。
    ///
    /// <para>
    /// 取一个大到不可能存在的 pid，而不是「刚退出的那个子进程的 pid」：后者的 pid 随时可能
    /// 被系统分配给别人，那条测试会偶发地测到另一个真进程头上。Windows 的 pid 实际远小于此。
    /// </para>
    /// </summary>
    [Fact]
    public void Wait_WhenThatProcessDoesNotExist_ReturnsGoneInsteadOfThrowing()
    {
        const int NoSuchPid = 0x7FFFFFFC;

        var outcome = PreviousInstanceWaiter.Wait(NoSuchPid, TimeSpan.FromSeconds(1));

        Assert.Equal(PreviousInstanceWaitOutcome.Gone, outcome);
    }

    /// <summary>旧进程赖着不走时，必须在给定的时限上放手，而不是一直等下去。</summary>
    [Fact]
    public void Wait_WhenThatProcessKeepsRunning_GivesUpAtTheDeadline()
    {
        using var previous = CrossProcessGuardHost.HoldInChildProcess(UniqueMutexName());

        var clock = Stopwatch.StartNew();
        var outcome = PreviousInstanceWaiter.Wait(previous.Pid, TimeSpan.FromMilliseconds(300));
        clock.Stop();

        Assert.Equal(PreviousInstanceWaitOutcome.TimedOut, outcome);

        // 时限给的是 300ms。放宽到 5s 是为了不被 CI 的调度抖动误伤，但「无限等」那种写法
        // （比如把超时参数丢了直接 WaitForExit()）会在这里挂到测试超时，看得见。
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(5),
            $"时限给的是 300ms，实际等了 {clock.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 正路：进去时旧进程还活着，等它退出之后才返回。
    ///
    /// <para>
    /// 中间那条 <c>Assert.False</c> 是阳性对照，**少不得**：没有它的话，一个「不管三七二十一
    /// 立刻返回 Gone」的实现照样能让最后两条断言全绿——而那个实现会让新进程直接撞上单实例检查。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Wait_WhenThatProcessExitsWhileWaiting_ReturnsGoneOnlyAfterItReallyExits()
    {
        var cancellation = TestContext.Current.CancellationToken;

        using var previous = CrossProcessGuardHost.HoldInChildProcess(UniqueMutexName());
        var pid = previous.Pid;

        var waiting = Task.Run(() => PreviousInstanceWaiter.Wait(pid, TimeSpan.FromSeconds(30)), cancellation);

        var early = await Task.WhenAny(
            waiting, Task.Delay(TimeSpan.FromMilliseconds(300), cancellation));

        Assert.NotSame(waiting, early);

        previous.Crash();

        var finished = await Task.WhenAny(
            waiting, Task.Delay(TimeSpan.FromSeconds(10), cancellation));

        Assert.Same(waiting, finished);
        Assert.Equal(PreviousInstanceWaitOutcome.Gone, await waiting);
    }

    /// <summary>
    /// pid 传 0 或负数时同样只能安静返回。这类值本不该出现（<c>RestartArguments</c> 已挡了一道），
    /// 但这里是启动路径，多一道不吃亏。
    ///
    /// <para>
    /// <b>这两个值走的是两条不同的路，本条第一版只猜对了一条</b>（Win11 22631 / .NET 10 实测）：
    /// <c>-1</c> 让 <c>GetProcessById</c> 抛 <see cref="ArgumentException"/>；而 <c>0</c>
    /// <b>根本不抛</b>——pid 0 是 System Idle Process，真找得到，炸的是后面 <c>WaitForExit</c>
    /// 内部的 <c>OpenProcess</c>，<c>Win32Exception: Access is denied</c>。
    /// 推广开就是「打不开的进程」（受保护的、别的用户的、提权跑着的那一份）都从那条路出来。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Wait_WithAnImpossiblePid_ReturnsGoneInsteadOfThrowing(int pid)
    {
        Assert.Equal(
            PreviousInstanceWaitOutcome.Gone,
            PreviousInstanceWaiter.Wait(pid, TimeSpan.FromSeconds(1)));
    }
}
