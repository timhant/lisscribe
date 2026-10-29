using Lisscribe.App.Composition;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 退出时的收尾顺序。每一步都往同一本账 <see cref="_log"/> 上记，期望的先后与超时值都手写，不取自被测类。
///
/// <para>
/// 「停状态机要等停转写」光看账上的先后不够：调用了却没 await 的写法，账上的先后照样对。
/// 故那几条让停转写返回一个没完成的任务，先断言状态机此刻还没开始关，再放行它。
/// </para>
/// </summary>
public sealed class ShutdownSequenceTests
{
    /// <summary>等异步那一步的上限。被测代码坏了时让测试红在这里，而不是整个测试进程挂死。</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>一次完整收尾在账上的样子，手写。</summary>
    private static readonly string[] FullRun =
    [
        "tray",
        "transcription:00:00:10",
        "controller:00:00:10",
        "wiring",
        "hook",
        "monitor",
        "capture",
        "loggerFactory",
    ];

    private readonly Lock _gate = new();

    private readonly List<string> _log = [];

    private readonly RecordingLogger<ShutdownSequence> _logger = new();

    /// <summary>停转写的假件，记账由 <see cref="NewSequence"/> 做。默认当场报「已收尾」。</summary>
    private Func<Task<bool>> _stopTranscription = () => Task.FromResult(true);

    /// <summary>停状态机的假件，记账由 <see cref="NewSequence"/> 做。默认当场完成。</summary>
    private Func<Task> _shutdownController = () => Task.CompletedTask;

    private string[] Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    [Fact]
    public async Task TheSteps_RunInTheFixedOrder_WithTenSecondLimits()
    {
        await NewSequence().RunAsync();

        Assert.Equal(FullRun, Log);
    }

    [Fact]
    public async Task TheTray_IsDetachedBeforeAnythingElse()
    {
        // 摘托盘必须在最前：收尾途中的状态变更不许再打到托盘菜单上。转写停不下来时，账上也只有摘托盘排在它前面。
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopTranscription = () => stopped.Task;

        var run = NewSequence().RunAsync();

        Assert.Equal(["tray", "transcription:00:00:10"], Log);

        stopped.SetResult(true);
        await run.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal("tray", Log[0]);
    }

    /// <summary>
    /// 状态机关停会释放引擎，而转写正用着它。故关停必须等停转写的任务完成之后才开始，不是调用之后。
    /// </summary>
    [Fact]
    public async Task TheController_IsShutDownOnlyAfterTheTranscriptionStopHasCompleted()
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopTranscription = () => stopped.Task;

        var run = NewSequence().RunAsync();

        // 停转写已调用、尚未完成：状态机与其余部件一个都不许动。
        Assert.Equal(["tray", "transcription:00:00:10"], Log);
        Assert.False(run.IsCompleted);

        stopped.SetResult(true);
        await run.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(FullRun, Log);
    }

    [Fact]
    public async Task WhenTheTranscriptionStopTimesOut_TheControllerIsShutDownAnyway()
    {
        // 等不到转写收尾也照常往下关：退出不能被一次卡死的转写无限拖住。
        _stopTranscription = () => Task.FromResult(false);

        await NewSequence().RunAsync();

        Assert.Equal(FullRun, Log);
    }

    /// <summary>
    /// 状态机关停失败不许把其余部件留着不拆：钩子不摘，进程退出前全局键盘钩子还挂在系统里。
    /// 同步抛与返回失败任务两种形状各验一次。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenTheControllerShutdownThrows_TheRestIsStillReleased_AndTheErrorIsLogged(bool throwsSynchronously)
    {
        var failure = new InvalidOperationException("状态机关不掉");

        _shutdownController = () => throwsSynchronously ? throw failure : Task.FromException(failure);

        await NewSequence().RunAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(FullRun, Log);

        Assert.Equal(1, _logger.TimesRecorded(failure));
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
    }

    /// <summary>
    /// 托盘「退出」与窗口关闭都可能走到收尾。第二次进来时第一次还在等转写，第二次必须当场返回，什么也不碰。
    /// </summary>
    [Fact]
    public async Task ASecondRun_DoesNothing_EvenWhileTheFirstIsStillWaiting()
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopTranscription = () => stopped.Task;

        var sequence = NewSequence();
        var first = sequence.RunAsync();
        var second = sequence.RunAsync();

        Assert.True(second.IsCompletedSuccessfully);

        stopped.SetResult(true);
        await first.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // 跑完之后再来一次，同样什么也不碰。
        await sequence.RunAsync();

        Assert.Equal(FullRun, Log);
    }

    /// <summary>
    /// 生产里收尾在 UI 线程上被同步等着（App 的 ShutdownRequested 处理器）。任何一步的续体投回调用方的同步上下文，
    /// 都是永远等不到的死锁。这里用一个只收不跑的同步上下文顶替那条被等住的 UI 线程。
    /// 停转写当场完成是生产里最常见的情形（没有转写在跑），那时停状态机那一步仍在 UI 线程上，两种情形各验一次。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoStep_ContinuesOnTheCallersSynchronizationContext(bool transcriptionStopsAtOnce)
    {
        var stopped = new TaskCompletionSource<bool>();
        var shutDown = new TaskCompletionSource();

        _stopTranscription = () => transcriptionStopsAtOnce ? Task.FromResult(true) : stopped.Task;
        _shutdownController = () => shutDown.Task;

        var uiThread = new SwallowingSynchronizationContext();
        var sequence = NewSequence();
        Task? run = null;
        Exception? escaped = null;

        // 专用线程装上黑洞上下文再起收尾。逃出的异常会带走整个测试进程，故接住，回到测试线程再报。
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(uiThread);

                run = sequence.RunAsync();
            }
            catch (Exception ex)
            {
                escaped = ex;
            }
        });

        thread.Start();
        Assert.True(thread.Join(Patience), "专用线程没有按时结束");
        Assert.Null(escaped);

        // 从测试线程放行，不从黑洞所在的线程：同一上下文上的续体会被就地执行，那样测不出投递。
        stopped.TrySetResult(true);
        shutDown.SetResult();

        await run!.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(0, uiThread.PostCount);
        Assert.Equal(FullRun, Log);
    }

    /// <summary>两个异步步骤在这里记账，记下收到的超时值，再交给测试换上的假件。</summary>
    private ShutdownSequence NewSequence() => new(
        detachTray: () => Record("tray"),
        stopTranscription: timeout =>
        {
            Record($"transcription:{timeout}");

            return _stopTranscription();
        },
        shutdownController: timeout =>
        {
            Record($"controller:{timeout}");

            return _shutdownController();
        },
        releaseInOrder:
        [
            new RecordingDisposable("wiring", Record),
            new RecordingDisposable("hook", Record),
            new RecordingDisposable("monitor", Record),
            new RecordingDisposable("capture", Record),
            new RecordingDisposable("loggerFactory", Record),
        ],
        _logger);

    private void Record(string entry)
    {
        lock (_gate)
        {
            _log.Add(entry);
        }
    }

    /// <summary>被释放时往账上记一笔自己的名字。</summary>
    private sealed class RecordingDisposable(string name, Action<string> record) : IDisposable
    {
        public void Dispose() => record(name);
    }

    /// <summary>投进来的续体一律吞掉不跑，并计数。代替生产里那条被同步等住的 UI 线程。</summary>
    private sealed class SwallowingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _postCount);

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("收尾不许同步回到调用方的线程");
    }
}
