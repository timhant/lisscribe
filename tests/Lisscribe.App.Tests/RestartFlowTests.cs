using Lisscribe.App.Composition;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 用户点了「立即重启」之后那一段：先等转写停下，再投回 UI 线程重启，失败时告诉用户。另有确认窗的文案。
///
/// <para>
/// 投到 UI 线程的活儿先排进 <see cref="_uiQueue"/>、不当场跑，由测试决定何时排空。这样「重启经由 postToUi 执行」
/// 与「等转写在重启之前完成」才各自验得出来。期望文案逐字手写，不调用 RestartPrompt 来算。
/// </para>
/// </summary>
public sealed class RestartFlowTests
{
    /// <summary>等异步那一步的上限。被测代码坏了时让测试红在这里，而不是整个测试进程挂死。</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();

    private readonly List<string> _log = [];

    private readonly Queue<Action> _uiQueue = new();

    private readonly List<string> _notices = [];

    private readonly RecordingLogger<RestartFlow> _logger = new();

    /// <summary>停转写的假件，记账由 <see cref="NewFlow"/> 做。默认当场报「已收尾」。</summary>
    private Func<Task<bool>> _stopTranscription = () => Task.FromResult(true);

    /// <summary>重启的结局。null 即成功，否则是拉不起来的原因。</summary>
    private string? _restartError;

    private bool _isTranscribing;

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

    private string[] Notices
    {
        get
        {
            lock (_gate)
            {
                return [.. _notices];
            }
        }
    }

    [Fact]
    public async Task TheRestart_WaitsForTheTranscriptionToStop_AndThenRunsOnTheUiThread()
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopTranscription = () => stopped.Task;

        var run = NewFlow().RunAsync();

        // 转写还没停：既没投递，也没重启。
        Assert.Equal(["transcription:00:00:10"], Log);
        Assert.Empty(UiQueueSnapshot());
        Assert.False(run.IsCompleted);

        stopped.SetResult(true);
        await run.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // 转写停了：投递了一次，但重启要等 UI 线程跑到那一格才发生。
        Assert.Single(UiQueueSnapshot());
        Assert.Equal(["transcription:00:00:10"], Log);

        DrainUiQueue();

        Assert.Equal(["transcription:00:00:10", "restart"], Log);
        Assert.Empty(Notices);
    }

    [Fact]
    public async Task WhenTheTranscriptionStopTimesOut_ItRestartsAnyway()
    {
        // 等不到也照样重启：这一段在拉起后继进程之前，不占后继进程那 30 秒。
        _stopTranscription = () => Task.FromResult(false);

        await NewFlow().RunAsync();
        DrainUiQueue();

        Assert.Equal(["transcription:00:00:10", "restart"], Log);
        Assert.Empty(Notices);
    }

    [Fact]
    public async Task WhenTheRestartFails_OneNoticeSaysWhy()
    {
        // 拉不起来时本进程原样活着，故还报得出话来。
        _restartError = "找不到 lisscribe.exe";

        await NewFlow().RunAsync();
        DrainUiQueue();

        Assert.Equal(["重启失败：找不到 lisscribe.exe"], Notices);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task WhenWaitingForTheTranscriptionThrows_OneNoticeSaysWhy_AndNothingRestarts()
    {
        var failure = new InvalidOperationException("转写停不下来");
        _stopTranscription = () => Task.FromException<bool>(failure);

        await NewFlow().RunAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);
        DrainUiQueue();

        Assert.Equal(["重启失败：转写停不下来"], Notices);
        Assert.DoesNotContain("restart", Log);
        Assert.Equal(1, _logger.TimesRecorded(failure));
    }

    [Fact]
    public void TheConfirmMessage_MentionsTheCancellation_OnlyWhileTranscribing()
    {
        var flow = NewFlow();

        _isTranscribing = false;
        Assert.Equal("配置已修改，重启 lisscribe 之后才会全部生效。", flow.ConfirmMessage);

        // 每次现读：同一个实例，转写开始之后再问，文案跟着变。
        _isTranscribing = true;
        Assert.Equal("配置已修改，重启 lisscribe 之后才会全部生效。正在进行的转写会被取消。", flow.ConfirmMessage);
    }

    /// <summary>停转写在这里记账，记下收到的超时值，再交给测试换上的假件。</summary>
    private RestartFlow NewFlow() => new(
        isTranscribing: () => _isTranscribing,
        stopTranscription: timeout =>
        {
            Record($"transcription:{timeout}");

            return _stopTranscription();
        },
        tryRestart: (out string? error) =>
        {
            Record("restart");
            error = _restartError;

            return error is null;
        },
        postToUi: action =>
        {
            lock (_gate)
            {
                _uiQueue.Enqueue(action);
            }
        },
        showNotice: message =>
        {
            lock (_gate)
            {
                _notices.Add(message);
            }
        },
        _logger);

    private Action[] UiQueueSnapshot()
    {
        lock (_gate)
        {
            return [.. _uiQueue];
        }
    }

    /// <summary>扮演 UI 线程：把排着的活儿按顺序跑完。</summary>
    private void DrainUiQueue()
    {
        while (true)
        {
            Action? next;

            lock (_gate)
            {
                if (!_uiQueue.TryDequeue(out next))
                {
                    return;
                }
            }

            next();
        }
    }

    private void Record(string entry)
    {
        lock (_gate)
        {
            _log.Add(entry);
        }
    }
}
