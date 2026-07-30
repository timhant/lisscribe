using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 锁屏 / 休眠 / 前台窗口变化三个通知源。锁屏与休眠也是自动化的：往本进程自己的顶级窗口投
/// WM_WTSSESSION_CHANGE / WM_POWERBROADCAST，<c>SystemEvents</c> 的隐藏窗口就在其中，收到之后照常
/// 引发 SessionSwitch / PowerModeChanged——走的是生产那条链，不是把处理器抠出来直接调。真锁屏、真
/// 休眠仍须手工验（见 PlatformIntegrationManualChecklist.md）：这里证明的是「消息到了我们做得对」，
/// 不是「Windows 真会发这条消息」。与注入型测试同一个 Collection：本类抢前台，且投递的消息会驱动
/// 进程内所有 SessionMonitor 实例，并行跑必然互相串味。
/// </summary>
[Collection(InjectedInputCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class SessionMonitorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const nint WTS_SESSION_LOCK = 0x7;
    private const nint WTS_SESSION_UNLOCK = 0x8;

    private const uint WM_POWERBROADCAST = 0x0218;
    private const nint PBT_APMSUSPEND = 0x4;

    /// <summary>
    /// 不是 PBT_APMRESUMEAUTOMATIC(0x12)，虽然那个名字看着更贴切：<c>SystemEvents</c> 只翻译一部分
    /// PBT_*，0x12 一条也不发。用它的话，「恢复不该触发 SystemSuspending」这句根本走不到我们的过滤器
    /// ——把过滤器整条删掉照样绿。
    /// </summary>
    private const nint PBT_APMRESUMESUSPEND = 0x7;

    [Fact]
    public void ForegroundWindowChanged_ArrivesEvenThoughStartRanOnAThreadWithNoMessagePump()
    {
        // WINEVENT_OUTOFCONTEXT 的回调是从安装线程的消息队列上取的，安装线程没有消息泵就一次也不触发
        // ——而 SetWinEventHook 照样返回有效句柄、lastError = 0，零报错。实测同一个钩子装在有泵的线程
        // 上收到 2 次 EVENT_SYSTEM_FOREGROUND，装在只 Sleep 不取消息的线程上收到 0 次。
        //
        // xunit 的测试线程正是没有消息泵的那种，故被测类必须自带一条泵线程，否则本条不可能通过。
        using var monitor = NewMonitor();
        var log = new EventLog(monitor);

        using var first = new TestWindow("session-fg-1");
        Assert.True(first.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        monitor.Start();

        using var second = new TestWindow("session-fg-2");
        Assert.True(second.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        Assert.True(WaitFor(() => log.ForegroundChanges >= 1, Patience),
            $"前台窗口换了，却一个 ForegroundWindowChanged 也没收到（收到 {log.Describe()}）");
    }

    [Fact]
    public void SessionLocked_FiresWhenTheSessionIsLocked()
    {
        using var monitor = NewMonitor();
        var log = new EventLog(monitor);
        monitor.Start();

        PostSessionChange(WTS_SESSION_LOCK);

        Assert.True(WaitFor(() => log.SessionLocks >= 1, Patience),
            $"会话锁定没有引发 SessionLocked（收到 {log.Describe()}）");
    }

    [Fact]
    public void SessionLocked_DoesNotFireWhenTheSessionIsUnlocked()
    {
        // 解锁也是一次 SessionSwitch。不过滤 Reason 就会在用户回到机器前的那一刻取消他刚开始的录音。
        //
        // 判据是「消息有序」而不是「等得够久」：先投 UNLOCK 再 Sleep 一段然后快照计数的话，负载高时
        // UNLOCK 还没处理完，一个不过滤 Reason 的实现照样绿。PostMessage 对同一个窗口是 FIFO，两条
        // 消息都投给 SystemEvents 那同一个隐藏窗口，故「后一条被处理了」蕴含「前一条已处理完」。
        using var monitor = NewMonitor();
        var log = new EventLog(monitor);
        monitor.Start();

        // 屏障用的是另一个计数器：拿 SessionLocks 当屏障再断言 SessionLocks，屏障与被测量同一个量，
        // 中间留着一个取样窗口。换成「等休眠到达」再看锁定计数，那个窗口就没有了。
        PostSessionChange(WTS_SESSION_UNLOCK);
        PostPowerBroadcast(PBT_APMSUSPEND);

        // 兼作阳性对照：连休眠都没送到的话，下面那条断言就什么也没验。
        Assert.True(WaitFor(() => log.Suspends >= 1, Patience),
            "阳性对照失败：连休眠都没送到，本条断言无效");

        Assert.Equal(0, log.SessionLocks);
    }

    [Fact]
    public void SystemSuspending_FiresWhenTheSystemIsAboutToSuspend()
    {
        using var monitor = NewMonitor();
        var log = new EventLog(monitor);
        monitor.Start();

        PostPowerBroadcast(PBT_APMSUSPEND);

        Assert.True(WaitFor(() => log.Suspends >= 1, Patience),
            $"系统即将休眠没有引发 SystemSuspending（收到 {log.Describe()}）");
    }

    [Fact]
    public void SystemSuspending_DoesNotFireWhenTheSystemResumes()
    {
        // 同上一条：靠「同一窗口的消息有序」定序，且屏障用另一个计数器。
        using var monitor = NewMonitor();
        var log = new EventLog(monitor);
        monitor.Start();

        PostPowerBroadcast(PBT_APMRESUMESUSPEND);
        PostSessionChange(WTS_SESSION_LOCK);

        Assert.True(WaitFor(() => log.SessionLocks >= 1, Patience),
            "阳性对照失败：连锁定都没送到，本条断言无效");

        Assert.Equal(0, log.Suspends);
    }

    [Fact]
    public void ASubscriberThatThrowsOnForegroundChange_NeitherKillsTheProcessNorStopsLaterEvents()
    {
        // 前台变化的回调跑在我们自己的泵线程上，且是系统反向调进来的。普通宿主上异常逃出 WinEvent
        // 回调就是进程当场终结（IsTerminating=True，0xC000041D）。
        //
        // 但在 xunit 的测试宿主里进程活了下来：打几条 [FATAL ERROR]，测试照常跑完，后续回调仍然到达。
        // 也就是说「进程还在不在」这个本该最有力的预言机在测试里取不到样，「后面还有没有事件」同样区分
        // 不出来（退出码只在 `dotnet test` 那条路上告发它）。故本条改钉一件事：catch 块必须把异常记
        // 下来——那是被测代码自己记的账，证明力弱一层，但在本宿主里稳稳杀得掉变异。
        var recorded = new RecordingLogger<SessionMonitor>();
        using var monitor = new SessionMonitor(recorded);
        var log = new EventLog(monitor);
        var thrown = 0;
        var boom = new InvalidOperationException("订阅者炸了");
        monitor.ForegroundWindowChanged += (_, _) =>
        {
            Interlocked.Increment(ref thrown);
            throw boom;
        };

        monitor.Start();

        using var first = new TestWindow("session-throw-1");
        Assert.True(first.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");
        Assert.True(WaitFor(() => Volatile.Read(ref thrown) >= 1, Patience), "第一次前台变化没送到");

        using var second = new TestWindow("session-throw-2");
        Assert.True(second.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        Assert.True(WaitFor(() => log.ForegroundChanges >= 2, Patience),
            $"一个订阅者抛异常之后就不再有前台变化事件了（收到 {log.Describe()}）");

        Assert.True(recorded.TimesRecorded(boom) >= 1,
            $"订阅者的异常没有被回调咽下并记账，它是逃出去的：{recorded.Describe()}");
    }

    [Fact]
    public void ASubscriberThatThrowsOnSessionLock_DoesNotStopLaterEvents()
    {
        // 这一处跑在 SystemEvents 自己的线程上，与上一条不同：把被测类的 try/catch 拿掉，进程不崩、
        // 后续事件照来，因为 SystemEvents 自己的 RaiseEvent 也包了一层 catch。也就是说这层 catch 在
        // 今天的 SystemEvents 实现下是第二道防线——那是别人家的实现细节不是契约，且没有它连日志都
        // 没有：用户的订阅者悄悄炸了而我们一个字都不知道。
        var recorded = new RecordingLogger<SessionMonitor>();
        using var monitor = new SessionMonitor(recorded);
        var log = new EventLog(monitor);
        var boom = new InvalidOperationException("订阅者炸了");
        monitor.SessionLocked += (_, _) => throw boom;

        monitor.Start();

        PostSessionChange(WTS_SESSION_LOCK);
        Assert.True(WaitFor(() => log.SessionLocks >= 1, Patience), "第一次锁定没送到");

        PostPowerBroadcast(PBT_APMSUSPEND);

        Assert.True(WaitFor(() => log.Suspends >= 1, Patience),
            $"锁定的订阅者抛异常之后休眠事件也不来了（收到 {log.Describe()}）");

        Assert.True(recorded.TimesRecorded(boom) >= 1,
            $"订阅者的异常没有被处理器咽下并记账：{recorded.Describe()}");
    }

    [Fact]
    public void TwoMonitorsInTheSameProcess_BothGetTheirEvents()
    {
        // 把实例本身也做成静态单例的话，一个进程只能有一个监听器，且 Start 中途失败会留下脏静态。
        // root 住委托与「实例单例」是两件事。
        using var first = NewMonitor();
        using var second = NewMonitor();
        var firstLog = new EventLog(first);
        var secondLog = new EventLog(second);

        first.Start();
        second.Start();

        PostSessionChange(WTS_SESSION_LOCK);

        Assert.True(WaitFor(() => firstLog.SessionLocks >= 1, Patience),
            $"第一个监听器没收到（{firstLog.Describe()}）");
        Assert.True(WaitFor(() => secondLog.SessionLocks >= 1, Patience),
            $"第二个监听器没收到（{secondLog.Describe()}）");
    }

    [Fact]
    public void AfterDispose_NoMoreEvents()
    {
        // SystemEvents 的事件是**静态**的。忘了退订，这个实例连同它订上的一切就永久留在
        // 进程里：配置窗改热键重建一次监听器，旧的还在替一个已经不用的会话取消录音。
        var monitor = NewMonitor();
        var log = new EventLog(monitor);
        monitor.Start();

        // 阳性对照：Dispose 之前它必须真的收得到，否则下面的「没收到」什么也证明不了。
        PostSessionChange(WTS_SESSION_LOCK);
        Assert.True(WaitFor(() => log.SessionLocks >= 1, Patience), "Dispose 之前就收不到，本条断言无效");

        monitor.Dispose();
        log.Clear();

        PostSessionChange(WTS_SESSION_LOCK);
        PostPowerBroadcast(PBT_APMSUSPEND);
        using var window = new TestWindow("session-after-dispose");
        window.BringToForeground();
        Thread.Sleep(600);

        Assert.Equal(0, log.SessionLocks + log.Suspends + log.ForegroundChanges);
    }

    [Fact]
    public void Start_IsIdempotent_AndDisposeReleasesTheDelegateRoot()
    {
        // 装第二次钩子既漏一个句柄，也让每次前台变化引发两遍事件。委托必须被静态字段 root 住，
        // 故这里数的是那张静态表。
        var baseline = SessionMonitor.RootedCallbackCount;

        using (var monitor = NewMonitor())
        {
            monitor.Start();
            monitor.Start();

            Assert.Equal(baseline + 1, SessionMonitor.RootedCallbackCount);
        }

        Assert.Equal(baseline, SessionMonitor.RootedCallbackCount);
    }

    [Fact]
    public void WhenLoggingTheLockItselfThrows_TheEventStillReachesSubscribers()
    {
        // 那句「会话中断」的日志挡在 SessionLocked?.Invoke 前面：它一抛，异常被外层 catch 咽掉、进程
        // 好端端的，而订阅者一次也没被调到——后果是 ForgetHeldMainKey 不跑、锁屏后主键卡在 held、
        // 用户解锁后下一次热键被静默吞掉。「记日志失败」把整个安全网悄悄拆了。
        //
        // sink 只在这一句上抛，不是无条件抛：无条件抛会在更早的地方先炸，把要验的这条链当场打断。
        var sink = new RecordingLogger<SessionMonitor> { ThrowOnMessageContaining = "会话中断" };
        using var monitor = new SessionMonitor(sink);
        var log = new EventLog(monitor);
        monitor.Start();

        PostSessionChange(WTS_SESSION_LOCK);

        Assert.True(WaitFor(() => log.SessionLocks >= 1, Patience),
            $"记日志失败把 SessionLocked 静默吃掉了（收到 {log.Describe()}，日志 {sink.Describe()}）");
    }

    [Fact]
    public void WhenLoggingTheSuspendItselfThrows_TheEventStillReachesSubscribers()
    {
        // 同上，另一条通道。两条都验，是因为它们是各自独立的一句裸日志。
        var sink = new RecordingLogger<SessionMonitor> { ThrowOnMessageContaining = "系统即将休眠" };
        using var monitor = new SessionMonitor(sink);
        var log = new EventLog(monitor);
        monitor.Start();

        PostPowerBroadcast(PBT_APMSUSPEND);

        Assert.True(WaitFor(() => log.Suspends >= 1, Patience),
            $"记日志失败把 SystemSuspending 静默吃掉了（收到 {log.Describe()}，日志 {sink.Describe()}）");
    }

    [Fact]
    public void Start_SurvivesASinkThatThrowsOnEveryEntry()
    {
        // 「SetWinEventHook 失败」那句日志若是裸的，sink 一抛，异常就从 onStart 逃到泵线程的 catch、
        // 再由 Start() 重抛，整个程序被拦在门外——而那条路的设计决定正是「装不上是功能降级不是启动
        // 失败」，会被静默反转。
        //
        // 必须用测试缝把「装钩子失败」那条路造出来（SetWinEventHook 的失败无法从外部制造）：Start()
        // 的成功路径一句日志都不写，不造这条路，sink 永远不会被调到，去掉 Safe() 它照样绿。
        var sink = new RecordingLogger<SessionMonitor> { ThrowOnEveryEntry = true };
        using var monitor = new SessionMonitor(sink) { FailWinEventHookInstallForTest = true };

        // 两件事一起钉：装不上不抛（设计决定），且那条路上的日志抛出也不许把 Start 变成失败。
        monitor.Start();

        // 阳性对照：确认真的走到了那句日志上，否则本条又成空测。
        Assert.True(sink.CountOf(LogLevel.Warning) >= 1, $"没走到装钩子失败那条路上：{sink.Describe()}");

        // 而且确实是「降级」而不是「装上了」——锁屏 / 休眠两条链不受影响。
        Assert.Equal(nint.Zero, monitor.CurrentWinEventHook);
    }

    [Fact]
    public void WhenTheHandoverFails_TheRootIsKeptRatherThanAssumedClean()
    {
        // `?? false` 的回归钉子：把它改回 `?? true`，整套测试一条也不红。
        //
        // 现场是委托已经 root 上了、泵还没造出来就失败。此时收尾面对「已 started 而 _pump 为 null」，
        // 问不出「泵停干净了没有」——默认必须落在「没停干净」那一侧。这里确实白泄漏一个委托（钩子根本
        // 没装，撤了也安全），但同一个默认值也是「问不出结论」时的答案，那时它必须是 false。
        var baseline = SessionMonitor.RootedCallbackCount;

        using var monitor = NewMonitor();
        monitor.FailBeforeCreatingPumpForTest = true;

        Assert.Throws<InvalidOperationException>(monitor.Start);
        Assert.Equal(baseline + 1, SessionMonitor.RootedCallbackCount);

        monitor.Dispose();

        Assert.Equal(baseline + 1, SessionMonitor.RootedCallbackCount);
    }

    [Fact]
    public void WhenTheTeardownThrowsHalfway_ItStillFinishesReleasingWhatItPromised()
    {
        // `finally` 的回归钉子：把收尾的 finally 拆成顺序语句，中途一抛就留下半截状态——委托非空、
        // 仍算 started、GC root 没释放，而那正是「第二次 Dispose 撤掉一个仍在用的 root」的土壤。
        //
        // 抛点刻意放在 StopAndJoin 之后：放在之前泵就真没停，会留下一条挂着 WinEvent 钩子的孤儿线程
        // 漏进后面的测试。
        var baseline = SessionMonitor.RootedCallbackCount;

        using var monitor = NewMonitor();
        monitor.Start();
        Assert.Equal(baseline + 1, SessionMonitor.RootedCallbackCount);

        monitor.ThrowAfterStoppingThePumpForTest =
            () => throw new InvalidOperationException("测试：收尾走到一半炸了");

        Assert.Throws<InvalidOperationException>(monitor.Dispose);

        // 拆掉引信，免得 using 收尾时再炸一次——那样红灯给的是「异常逃出 using」，
        // 而不是下面这条真正的判据。
        monitor.ThrowAfterStoppingThePumpForTest = null;

        // 泵是干净停下的，故 root 应当**已经**被释放——即 finally 真的跑完了。
        Assert.Equal(baseline, SessionMonitor.RootedCallbackCount);
    }

    [Fact]
    public void WhenThePumpCannotBeStopped_TheDelegateRootIsDeliberatelyLeaked()
    {
        // 泵没停干净 => UninstallWinEventHook 没跑完 => 钩子仍挂在系统里指着那个委托。此刻撤 GC root，
        // 系统下一次回调就是 catch 不到的 AccessViolation。
        //
        // 这条分支上有过一条真实的漏路：「泵没停干净」这个事实随 `_pump = null` 一起蒸发，第二次
        // Dispose 时 `_pump?.StopAndJoin() ?? true` 便得出「停干净了」。
        var baseline = SessionMonitor.RootedCallbackCount;

        // 阳性对照：泵停得干净时 root 必须被撤掉。没有它，「count 没变」也可能只是因为
        // 这个类从来就不撤 root，本条断言便什么也没验。
        using (var clean = NewMonitor())
        {
            clean.Start();
            Assert.Equal(baseline + 1, SessionMonitor.RootedCallbackCount);
            clean.Dispose();
            Assert.Equal(baseline, SessionMonitor.RootedCallbackCount);
        }

        using var release = new ManualResetEventSlim();
        var stuck = NewMonitor();
        stuck.Start();
        Assert.Equal(baseline + 1, SessionMonitor.RootedCallbackCount);

        // 堵住泵线程，让 StopAndJoin 的 Join 必然超时。用可释放的信号而不是定长 Sleep：
        // 断言完即放行，不把一条仍然装着 WinEvent 钩子的线程漏进后面的测试。
        Assert.True(stuck.PumpForTest!.Post(
            () => release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None)));
        Thread.Sleep(200);

        stuck.Dispose();

        Assert.Equal(baseline + 1, SessionMonitor.RootedCallbackCount);

        // 再 Dispose 一次：第二次绝不能把第一次故意留下的 root 撤掉。
        //
        // 说准这条断言证明什么：上面那条漏路现在被三样东西一起堵死（收尾搬进 finally、`?? false`
        // 默认、日志走 SafeLog），已经不能从测试里复现——本条走的是正常路径，第一次 Dispose 已把状态
        // 置假、第二次从守卫返回。它钉的是幂等性，那条 bug 由代码形状排除，不由本条排除。
        stuck.Dispose();
        Assert.Equal(baseline + 1, SessionMonitor.RootedCallbackCount);

        // 放行并等它真的收完尾。这既是收尾，本身也是一条断言——泵最终必须退得掉。
        release.Set();
        Assert.True(WaitFor(() => stuck.CurrentWinEventHook == nint.Zero, Patience),
            "泵线程最终没能收尾，WinEvent 钩子被漏给了后面的测试");
    }

    [Fact]
    public void ADisposedMonitor_RefusesToStartAgain()
    {
        var monitor = NewMonitor();
        monitor.Dispose();

        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }

    private static SessionMonitor NewMonitor() => new(NullLogger<SessionMonitor>.Instance);

    private static void PostSessionChange(nint what) =>
        TestNative.PostToOwnTopLevelWindows(WM_WTSSESSION_CHANGE, what, lParam: 1);

    private static void PostPowerBroadcast(nint what) =>
        TestNative.PostToOwnTopLevelWindows(WM_POWERBROADCAST, what, lParam: 0);

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

    private sealed class EventLog
    {
        private int _locks;
        private int _suspends;
        private int _foreground;

        public EventLog(SessionMonitor monitor)
        {
            monitor.SessionLocked += (_, _) => Interlocked.Increment(ref _locks);
            monitor.SystemSuspending += (_, _) => Interlocked.Increment(ref _suspends);
            monitor.ForegroundWindowChanged += (_, _) => Interlocked.Increment(ref _foreground);
        }

        public int SessionLocks => Volatile.Read(ref _locks);

        public int Suspends => Volatile.Read(ref _suspends);

        public int ForegroundChanges => Volatile.Read(ref _foreground);

        public void Clear()
        {
            Interlocked.Exchange(ref _locks, 0);
            Interlocked.Exchange(ref _suspends, 0);
            Interlocked.Exchange(ref _foreground, 0);
        }

        public string Describe() =>
            $"锁定 {SessionLocks} 次、休眠 {Suspends} 次、前台变化 {ForegroundChanges} 次";
    }
}
