using System.Diagnostics;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 消息泵线程本身的可观察行为。装 WH_KEYBOARD_LL 的线程必须有消息泵，否则回调一次也不触发
/// 且无任何报错。
///
/// 「钩子真的跟着这条线程活」由 KeyboardHookTests 验（那里有合成按键）；本类只钉这条线程自己的
/// 契约：起得来、能把活儿派回自己身上、起不来时调用方拿得到失败、停得下来。
/// </summary>
public sealed class MessagePumpThreadTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public void Start_RunsOnStartOnAThreadOfItsOwn()
    {
        var callerThreadId = Kernel32.GetCurrentThreadId();
        uint startThreadId = 0;

        using var pump = new MessagePumpThread(
            "test-pump", () => startThreadId = Kernel32.GetCurrentThreadId(), () => { },
            NullLogger.Instance);
        pump.Start();

        Assert.NotEqual(0u, startThreadId);
        Assert.NotEqual(callerThreadId, startThreadId);
    }

    [Fact]
    public void Post_RunsTheActionOnThePumpThreadItself()
    {
        // 看门狗重装钩子所依赖的能力：重装必须回到装它的那条线程上做。实测线程池线程上重装
        // 会得到有效句柄、lastError=0，而回调再不触发——没有这个能力，存活自检本身就是
        // 杀死钩子的凶手。
        uint startThreadId = 0;
        uint actionThreadId = 0;
        using var ran = new ManualResetEventSlim();

        using var pump = new MessagePumpThread(
            "test-pump", () => startThreadId = Kernel32.GetCurrentThreadId(), () => { },
            NullLogger.Instance);
        pump.Start();

        Assert.True(pump.Post(() =>
        {
            actionThreadId = Kernel32.GetCurrentThreadId();
            ran.Set();
        }));

        Assert.True(ran.Wait(Patience, TestContext.Current.CancellationToken), "投递的动作没在期限内跑到");
        Assert.Equal(startThreadId, actionThreadId);
    }

    [Fact]
    public void Post_RunsActionsInTheOrderTheyWerePosted()
    {
        var order = new List<int>();
        using var done = new ManualResetEventSlim();

        using var pump = new MessagePumpThread("test-pump", () => { }, () => { }, NullLogger.Instance);
        pump.Start();

        for (var i = 0; i < 20; i++)
        {
            var n = i;
            pump.Post(() =>
            {
                lock (order)
                {
                    order.Add(n);
                    if (order.Count == 20)
                    {
                        done.Set();
                    }
                }
            });
        }

        Assert.True(done.Wait(Patience, TestContext.Current.CancellationToken), "投递的动作没在期限内全部跑完");
        Assert.Equal(Enumerable.Range(0, 20), order);
    }

    [Fact]
    public void Start_WhenOnStartThrows_HandsTheExceptionToTheCaller()
    {
        // onStart 抛出必须变成调用方手上一个能 catch 的异常。丢掉 _ready 那个等待的返回值，
        // 后果比「白等 5 秒后若无其事返回」更糟：泵线程上逃逸的托管异常直接终结进程
        // （Unhandled exception … at Thread.StartCallback，Start() 根本没机会返回）。
        using var pump = new MessagePumpThread(
            "test-pump",
            () => throw new InvalidOperationException("安装钩子失败，Win32 错误码 1428"),
            () => { },
            NullLogger.Instance);

        var error = Assert.Throws<InvalidOperationException>(pump.Start);

        // 断言原文而非类型：包成 AggregateException 或换成一条自造消息都算没把原因交出去。
        Assert.Equal("安装钩子失败，Win32 错误码 1428", error.Message);
    }

    [Fact]
    public void Start_WhenOnStartNeverFinishes_ThrowsInsteadOfReturningQuietly()
    {
        using var block = new ManualResetEventSlim();
        using var pump = new MessagePumpThread(
            "test-pump", () => block.Wait(Patience, CancellationToken.None), () => { }, NullLogger.Instance,
            startTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Throws<TimeoutException>(pump.Start);

        // 别把那条线程晾在 Wait 上，否则测试跑完留下孤儿。
        block.Set();
    }

    [Fact]
    public void OnStop_IsNotRunWhenOnStartThrew()
    {
        // onStart 失败意味着什么也没装起来；再跑 onStop 就是对着没申请到的资源做释放。
        var stopRan = false;
        var pump = new MessagePumpThread(
            "test-pump", () => throw new InvalidOperationException("装不上"), () => stopRan = true,
            NullLogger.Instance);

        Assert.Throws<InvalidOperationException>(pump.Start);
        pump.Dispose();

        Assert.False(stopRan);
    }

    [Fact]
    public void Dispose_AfterAFailedStart_ReturnsPromptly()
    {
        var pump = new MessagePumpThread(
            "test-pump", () => throw new InvalidOperationException("装不上"), () => { },
            NullLogger.Instance);
        Assert.Throws<InvalidOperationException>(pump.Start);

        var elapsed = Stopwatch.StartNew();
        pump.Dispose();
        elapsed.Stop();

        // 泵线程早已死了，Dispose 不该在 Join 上把整个关停流程拖住。
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Dispose 用了 {elapsed.Elapsed}");
    }

    [Fact]
    public void Dispose_RunsOnStopBeforeItReturns()
    {
        // 顺序是本类唯一的硬保证：Dispose 返回之后，调用方就可以认为钩子已被摘除。
        var stopRan = false;
        var pump = new MessagePumpThread("test-pump", () => { }, () => stopRan = true, NullLogger.Instance);
        pump.Start();

        pump.Dispose();

        Assert.True(stopRan);
    }

    [Fact]
    public void Post_AfterDispose_IsRefusedRatherThanFiredAtADeadThreadId()
    {
        // 线程 id 会被系统回收再分配。往一个已死的 id 上 PostThreadMessage 不是无害的，
        // 撞上复用就是把消息塞进别人的队列。
        var ran = 0;
        var pump = new MessagePumpThread("test-pump", () => { }, () => { }, NullLogger.Instance);
        pump.Start();
        pump.Dispose();

        Assert.False(pump.Post(() => Interlocked.Increment(ref ran)));
        Thread.Sleep(150);
        Assert.Equal(0, Volatile.Read(ref ran));
    }

    [Fact]
    public void StopAndJoin_WhenThePumpIsStuck_ReportsFailureAndOnStopHasNotRun()
    {
        // 这条钉的是 KeyboardHook 赖以决定「撤不撤 GC root」的那个契约：
        // 「Dispose 返回时 onStop 已跑完」**不成立**——onStop 在 Run() 的 finally 里，
        // 而 Join 超时只是放弃等待。泵线程被堵住时 Dispose 照样返回，而钩子仍挂在系统里指着
        // 那个委托，此刻撤 root 就是「按键即 AccessViolation」。
        using var blocked = new ManualResetEventSlim();
        var stopRan = false;
        var pump = new MessagePumpThread("test-pump", () => { }, () => stopRan = true, NullLogger.Instance);
        pump.Start();

        Assert.True(pump.Post(() => blocked.Wait(TimeSpan.FromSeconds(10), CancellationToken.None)));

        // 等那个动作真的开始跑，否则可能在它被取出之前 WM_QUIT 就先到了。
        Thread.Sleep(200);

        var elapsed = Stopwatch.StartNew();
        var stoppedCleanly = pump.StopAndJoin();
        elapsed.Stop();

        // 放行放在 finally 里：任一断言先红时，泵线程会一直堵到 10 秒超时才脱身，
        // 把一条本该秒回的失败拖成十秒。
        try
        {
            Assert.False(stoppedCleanly);
            Assert.False(stopRan);

            // 顺带钉住「它确实等过」，免得日后有人把超时改成 0 让这条测试变得不痛不痒。
            Assert.True(elapsed.Elapsed > TimeSpan.FromSeconds(2), $"只等了 {elapsed.Elapsed}");
        }
        finally
        {
            blocked.Set();
        }
    }

    [Fact]
    public void StopAndJoin_OnAHealthyPump_ReportsSuccess()
    {
        // 上一条的阳性对照。没有它，「返回 false」既可能是真检出了卡住，
        // 也可能是这个方法压根永远返回 false。
        var pump = new MessagePumpThread("test-pump", () => { }, () => { }, NullLogger.Instance);
        pump.Start();

        Assert.True(pump.StopAndJoin());
    }

    [Fact]
    public void AnExceptionFromAPostedAction_DoesNotTakeThePumpThreadDownWithIt()
    {
        // 存活自检就是靠 Post 跑的。它抛一次就把泵线程弄死，等于连钩子一起赔进去——
        // 而钩子的宿主正是这条线程。
        using var second = new ManualResetEventSlim();
        using var pump = new MessagePumpThread("test-pump", () => { }, () => { }, NullLogger.Instance);
        pump.Start();

        pump.Post(() => throw new InvalidOperationException("自检里炸了"));
        Assert.True(pump.Post(() => second.Set()));

        Assert.True(second.Wait(Patience, TestContext.Current.CancellationToken), "第一个动作抛异常之后，泵线程不再干活");
    }

    [Fact]
    public void WhenLoggingItselfThrows_ThePumpThreadStillSurvives()
    {
        // 上一条只验了「动作抛异常不要紧」，而那个 catch 体里的 _logger.LogError 不许是裸的：
        // 记日志本身一抛（磁盘满、文件句柄没了、sink 自己抛），异常就从 catch 里逃出去，
        // 泵线程当场死掉——连同挂在它身上的钩子（键盘钩子与会话监听各占一条这样的线程）。
        //
        // 预言机不是日志内容，是「泵线程还接不接活」：第二个动作跑得到，它就还活着。
        var logger = new RecordingLogger<MessagePumpThreadTests> { ThrowOnEveryEntry = true };
        using var second = new ManualResetEventSlim();
        using var pump = new MessagePumpThread("test-pump", () => { }, () => { }, logger);
        pump.Start();

        pump.Post(() => throw new InvalidOperationException("自检里炸了"));
        Assert.True(pump.Post(() => second.Set()));

        Assert.True(second.Wait(Patience, TestContext.Current.CancellationToken),
            "记日志自己抛了一次，泵线程就再也不干活了");

        // 阳性对照：确认它**真的试过**记那一条，否则「线程还活着」也可能只是因为
        // 那个 sink 压根没被调用过，本条断言便什么也没验。
        Assert.True(logger.CountOf(LogLevel.Error) >= 1, $"日志 sink 根本没被调用过：{logger.Describe()}");
    }

    [Fact]
    public void PostToADeadPump_ReportsFailureInsteadOfThrowing_EvenWhenLoggingThrows()
    {
        // Post 失败时那句「投递动作失败」不许是裸的：它一抛，Post 就**抛**而不是返回 false，
        // 而接住它的是 KeyboardHook.RequestHealthCheck 的 catch，那里跑在
        // System.Threading.Timer 回调上——异常逃出去即
        // `Unhandled exception … at TimerQueueTimer.Fire`，进程当场终结。
        //
        // **可达性不是假想**：泵线程在 GetMessage 返回 -1 那条路上会**安静地**结束，
        // 而 _disposed 仍为 false、_threadId 仍非零，此后每次 Post 都落进这一支，
        // 自检定时器几秒一跳随后就踩上去。这里用「直接给泵线程投 WM_QUIT」制造同一个状态：
        // 线程退出了，但 MessagePumpThread 自己并不知道。
        uint pumpThreadId = 0;
        var logger = new RecordingLogger<MessagePumpThreadTests>
        {
            ThrowOnMessageContaining = "投递动作失败",
        };

        using var pump = new MessagePumpThread(
            "test-pump-dying", () => pumpThreadId = Kernel32.GetCurrentThreadId(), () => { }, logger);
        pump.Start();
        Assert.NotEqual(0u, pumpThreadId);

        Assert.True(User32.PostThreadMessage(pumpThreadId, User32.WM_QUIT, nint.Zero, nint.Zero));
        Assert.True(WaitFor(() => !ThreadStillTakesMessages(pumpThreadId), Patience), "泵线程没退出，前提不成立");

        var posted = pump.Post(() => { });

        Assert.False(posted);
        Assert.True(logger.CountOf(LogLevel.Warning) >= 1, $"没走到那条失败日志上：{logger.Describe()}");
    }

    /// <summary>
    /// 线程还收不收得下消息。线程一退出，PostThreadMessage 就失败（err 1444
    /// ERROR_INVALID_THREAD_ID）——这是「它真的死了」的操作系统级判据，
    /// 不是我们自己记的账。用 WM_NULL 探，不打扰任何人。
    /// </summary>
    private static bool ThreadStillTakesMessages(uint threadId) =>
        User32.PostThreadMessage(threadId, 0x0000, nint.Zero, nint.Zero);

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }
}
