using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lisscribe.Core.Tests.Session;

/// <summary>
/// 「转写中」与热键听写的互斥。
///
/// <para>
/// 转写本身（解码、切段、调引擎）不在状态机里，状态机只管三件事：能不能开始、进度文字、结束后回到哪。
/// 本组的原则：转写中只有 <see cref="SessionController.EndTranscription"/> 能让状态离开
/// <see cref="SessionState.Transcribing"/>，其余每个入口各有一条测试钉住。
/// </para>
/// </summary>
public sealed class SessionControllerTranscriptionTests : IAsyncDisposable
{
    private readonly FakeHotkeyHook _hook = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeAsrEngine _engine = new();
    private readonly FakeTextInjector _injector = new();
    private readonly FakeWindowProbe _probe = new();
    private readonly FakeSessionMonitor _monitor = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionController _controller;

    private readonly Lock _eventsGate = new();
    private readonly List<(SessionState State, string? Message)> _events = [];

    /// <summary>与默认配置的失败提示时长独立取值：这里写死 3 秒，被测代码改了默认值这条会先红。</summary>
    private static readonly TimeSpan FailedDisplayDuration = TimeSpan.FromSeconds(3);

    public SessionControllerTranscriptionTests()
    {
        // 引擎先加载好：本组测的是稳态下的互斥，未就绪那条拒绝路单独建一台控制器测。
        _engine.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        _controller = new SessionController(
            _hook, _capture, _engine, _injector, _probe, _monitor,
            SessionOptions.CreateDefault(), _time, NullLogger<SessionController>.Instance);

        _controller.StatusChanged += (_, e) =>
        {
            lock (_eventsGate)
            {
                _events.Add((e.State, e.Message));
            }
        };

        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));
    }

    public async ValueTask DisposeAsync() => await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));

    /// <summary>StatusChanged 的快照。事件可能在识别工作线程上引发，读写都过同一把锁。</summary>
    private List<(SessionState State, string? Message)> Events()
    {
        lock (_eventsGate)
        {
            return [.. _events];
        }
    }

    /// <summary>开始一次转写并断言已被接受，供「转写中按某个入口」类用例做前置。</summary>
    private void BeginTranscription()
    {
        Assert.True(_controller.TryBeginTranscription(out var refusal), refusal);
        Assert.Equal(SessionState.Transcribing, _controller.State);
    }

    /// <summary>轮询等待状态机抵达指定状态。识别在后台任务上跑，无法同步断言。</summary>
    private async Task WaitForStateAsync(SessionState expected, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_controller.State == expected)
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"等待状态 {expected} 超时，当前为 {_controller.State}");
    }

    /// <summary>按看门狗节拍逐跳推进假时钟。一次跨多个周期时补跳回调读到的是终点时刻，见倒数那组的说明。</summary>
    private void AdvanceWatchdogTicks(int ticks)
    {
        var interval = SessionOptions.CreateDefault().WatchdogInterval;

        for (var i = 0; i < ticks; i++)
        {
            _time.Advance(interval);
        }
    }

    // ───────────── 开始：每条拒绝路径各一条，拒绝后状态与事件都不许动 ─────────────

    [Fact]
    public async Task TryBegin_AfterShutdown_RefusesAndTouchesNothing()
    {
        await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));
        var before = Events().Count;

        var accepted = _controller.TryBeginTranscription(out var refusal);

        Assert.False(accepted);
        Assert.Equal("程序正在退出", refusal);
        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal(before, Events().Count);
    }

    [Fact]
    public async Task TryBegin_WhileTheEngineIsNotLoaded_RefusesAndTouchesNothing()
    {
        // 引擎故意不加载，故另起一台控制器，假件也各用各的，免得与本组那台抢事件。
        var engine = new FakeAsrEngine();
        var controller = new SessionController(
            new FakeHotkeyHook(), new FakeAudioCapture(), engine, new FakeTextInjector(),
            new FakeWindowProbe(), new FakeSessionMonitor(),
            SessionOptions.CreateDefault(), _time, NullLogger<SessionController>.Instance);

        var events = 0;
        controller.StatusChanged += (_, _) => Interlocked.Increment(ref events);
        controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));

        try
        {
            var accepted = controller.TryBeginTranscription(out var refusal);

            Assert.False(accepted);
            Assert.Equal("识别引擎未就绪", refusal);
            Assert.Equal(SessionState.Idle, controller.State);
            Assert.Equal(0, Volatile.Read(ref events));
        }
        finally
        {
            await controller.ShutdownAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void TryBegin_WhileRecording_RefusesAndLeavesTheRecordingRunning()
    {
        _hook.RaisePressed();
        Assert.Equal(SessionState.Recording, _controller.State);
        var before = Events().Count;

        var accepted = _controller.TryBeginTranscription(out var refusal);

        Assert.False(accepted);
        Assert.Equal("正在听写，请稍后再试", refusal);
        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.True(_capture.IsCapturing);
        Assert.Equal(before, Events().Count);
    }

    [Fact]
    public async Task TryBegin_WhileRecognizing_RefusesAndLetsTheRecognitionLand()
    {
        _engine.Gate = new TaskCompletionSource();
        _engine.NextResult = "说完的一句";

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Recognizing);
        var before = Events().Count;

        var accepted = _controller.TryBeginTranscription(out var refusal);

        Assert.False(accepted);
        Assert.Equal("正在听写，请稍后再试", refusal);
        Assert.Equal(SessionState.Recognizing, _controller.State);
        Assert.Equal(before, Events().Count);

        // 被拒的那次不许留下痕迹：识别照常上屏、照常回到 Idle。
        _engine.Gate.SetResult();
        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal("说完的一句", _injector.LastText);
    }

    [Fact]
    public void TryBegin_WhileAlreadyTranscribing_RefusesAndKeepsTheRunningOne()
    {
        BeginTranscription();
        _controller.ReportTranscriptionProgress(42);
        var before = Events().Count;

        var accepted = _controller.TryBeginTranscription(out var refusal);

        Assert.False(accepted);
        Assert.Equal("已有转写在进行", refusal);
        Assert.Equal(SessionState.Transcribing, _controller.State);

        // 进度不许被打回 0%：那等于第二次开始被半接受了。
        Assert.Equal("转写中 42%", _controller.LastMessage);
        Assert.Equal(before, Events().Count);
    }

    // ───────────── 开始：接受 ─────────────

    [Fact]
    public void TryBegin_FromIdle_EntersTranscribingAtZeroPercent()
    {
        var accepted = _controller.TryBeginTranscription(out var refusal);

        Assert.True(accepted);
        Assert.Null(refusal);
        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal([(SessionState.Transcribing, "转写中 0%")], Events());
    }

    [Fact]
    public async Task TryBegin_FromFailed_IsNotKickedOutByTheFailedTimer()
    {
        // 先造一个 Failed：按住够久但几乎没出声，落「录音过短」。
        _hook.RaisePressed();
        _capture.PushSeconds(0.2);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Failed);

        BeginTranscription();

        // 失败提示计时器到点后不许把转写打回静止态。
        _time.Advance(FailedDisplayDuration + TimeSpan.FromSeconds(1));

        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal("转写中 0%", _controller.LastMessage);
    }

    [Fact]
    public void TryBegin_FromPaused_LandsBackInPausedAndKeepsTheKeysPassingThrough()
    {
        _controller.Pause();
        Assert.Equal(SessionState.Paused, _controller.State);

        BeginTranscription();

        // 暂停期间转写，键照旧还给系统。
        Assert.True(_hook.PassThrough);

        _controller.EndTranscription(failure: null);

        Assert.Equal(SessionState.Paused, _controller.State);
        Assert.True(_hook.PassThrough);
    }

    // ───────────── 进度 ─────────────

    [Fact]
    public void Progress_IsCarriedAsTheStatusMessage_InOrder()
    {
        BeginTranscription();

        _controller.ReportTranscriptionProgress(35);
        _controller.ReportTranscriptionProgress(100);

        Assert.Equal(
            [
                (SessionState.Transcribing, "转写中 0%"),
                (SessionState.Transcribing, "转写中 35%"),
                (SessionState.Transcribing, "转写中 100%"),
            ],
            Events());
    }

    [Fact]
    public void Progress_WhenNotTranscribing_IsIgnored()
    {
        // 录音中迟到的一条进度若被受理，悬浮条会从「录音中」跳成「转写中」。
        _hook.RaisePressed();
        var before = Events().Count;

        _controller.ReportTranscriptionProgress(50);

        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.Equal(before, Events().Count);
    }

    // ───────────── 结束 ─────────────

    [Fact]
    public void End_WithoutFailure_ReturnsToIdle()
    {
        BeginTranscription();

        _controller.EndTranscription(failure: null);

        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal(SessionState.Idle, Events()[^1].State);
    }

    [Fact]
    public async Task End_WithFailure_ShowsItThenReturnsToIdleWhenTheTimerFires()
    {
        BeginTranscription();

        _controller.EndTranscription("无法解码该文件");

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.Equal("无法解码该文件", _controller.LastMessage);

        _time.Advance(FailedDisplayDuration);

        await WaitForStateAsync(SessionState.Idle);
    }

    [Fact]
    public async Task End_WithFailure_StartedFromPaused_ReturnsToPausedWhenTheTimerFires()
    {
        _controller.Pause();
        BeginTranscription();

        _controller.EndTranscription("无法解码该文件");

        Assert.Equal(SessionState.Failed, _controller.State);

        _time.Advance(FailedDisplayDuration);

        await WaitForStateAsync(SessionState.Paused);
    }

    [Fact]
    public void End_WhenNotTranscribing_DoesNotPaintOverALiveRecording()
    {
        // 一次已被取消的转写迟到的结局，不许把正在进行的录音踩成红条。
        _hook.RaisePressed();
        var before = Events().Count;

        _controller.EndTranscription("迟到的失败");

        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.True(_capture.IsCapturing);
        Assert.Equal(before, Events().Count);
    }

    // ───────────── 转写中，其余每个入口 ─────────────

    [Fact]
    public void Pressed_WhileTranscribing_DoesNotStartCapture()
    {
        BeginTranscription();
        var before = Events().Count;

        _hook.RaisePressed();

        Assert.Equal(0, _capture.StartCount);
        Assert.False(_capture.IsCapturing);
        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal(before, Events().Count);
    }

    [Fact]
    public async Task Released_WhileTranscribing_RecognizesNothing()
    {
        BeginTranscription();
        var before = Events().Count;

        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.Equal(0, _capture.StopCount);
        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal(before, Events().Count);
    }

    [Fact]
    public void AudioArriving_WhileTranscribing_IsNotMetered()
    {
        float? volume = null;
        _controller.VolumeChanged += (_, v) => volume = v;
        BeginTranscription();

        _capture.PushSamples(Enumerable.Repeat(0.5f, 1600).ToArray());

        Assert.Null(volume);
    }

    [Fact]
    public void Pause_WhileTranscribing_HandsTheKeysBackAtOnce_AndLandsInPausedAfterwards()
    {
        BeginTranscription();

        _controller.Pause();

        // 键当场还给系统，状态却不能当场变：转写还在跑。
        Assert.True(_hook.PassThrough);
        Assert.Equal(SessionState.Transcribing, _controller.State);

        _controller.EndTranscription(failure: null);

        Assert.Equal(SessionState.Paused, _controller.State);
    }

    [Fact]
    public void Resume_AfterPausingWhileTranscribing_TakesTheKeysBack_AndLandsInIdle()
    {
        BeginTranscription();
        _controller.Pause();
        var before = Events().Count;

        _controller.Resume();

        Assert.False(_hook.PassThrough);
        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal(before, Events().Count);

        _controller.EndTranscription(failure: null);

        Assert.Equal(SessionState.Idle, _controller.State);
    }

    [Fact]
    public void Resume_WhileATranscriptionStartedFromPausedRuns_LandsInIdle()
    {
        _controller.Pause();
        BeginTranscription();
        var before = Events().Count;

        _controller.Resume();

        Assert.False(_hook.PassThrough);
        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal(before, Events().Count);

        _controller.EndTranscription(failure: null);

        Assert.Equal(SessionState.Idle, _controller.State);
    }

    /// <summary>转写中暂停又继续：热键其实没停，「已暂停热键」不许留到转写落地后的 Idle 上。</summary>
    [Fact]
    public void PauseThenResume_WhileTranscribing_SuccessLandsInIdleWithoutThePauseNotice()
    {
        BeginTranscription();
        _controller.Pause();
        _controller.Resume();

        _controller.EndTranscription(failure: null);

        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Null(_controller.LastMessage);
    }

    /// <summary>同上，转写失败那一路：红条只说失败原因，到点回到的 Idle 上也不许有暂停告知。</summary>
    [Fact]
    public async Task PauseThenResume_WhileTranscribing_FailureShowsOnlyTheReason_AndIdleCarriesNoPauseNotice()
    {
        BeginTranscription();
        _controller.Pause();
        _controller.Resume();

        _controller.EndTranscription("无法解码该文件");

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.Equal("无法解码该文件", _controller.LastMessage);

        _time.Advance(FailedDisplayDuration);

        await WaitForStateAsync(SessionState.Idle);
        Assert.Null(_controller.LastMessage);
    }

    /// <summary>从暂停中开始的转写没挂暂停告知，期间点「继续」不许把更早一次暂停记下的旧告知翻出来。</summary>
    [Fact]
    public async Task Resume_DuringATranscriptionStartedFromPaused_DoesNotBringBackAnOlderNotice()
    {
        // 先让一次带告知的识别在途时被暂停并落地，留下一句暂停前的旧告知。
        _engine.Gate = new TaskCompletionSource();
        _hook.RaisePressed();
        _capture.PushSeconds(1.5);
        _time.Advance(TimeSpan.FromMilliseconds(800));
        _capture.RaiseDefaultDeviceChanged("新耳机");
        Assert.Equal("录音设备已变更", _controller.LastMessage);

        _controller.Pause();
        _engine.Gate.SetResult();
        await WaitForStateAsync(SessionState.Paused);

        BeginTranscription();
        _controller.Resume();

        _controller.EndTranscription(failure: null);

        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Null(_controller.LastMessage);
    }

    [Fact]
    public void SessionLockedOrSuspending_WhileTranscribing_DoesNotInterrupt()
    {
        // 设计 §4.8：锁屏、休眠不处理，醒来后继续。
        BeginTranscription();
        var before = Events().Count;

        _monitor.RaiseSessionLocked();
        _monitor.RaiseSystemSuspending();

        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal(0, _capture.StopCount);
        Assert.Equal(before, Events().Count);
    }

    [Fact]
    public async Task DefaultDeviceChanged_WhileTranscribing_DoesNothing()
    {
        BeginTranscription();
        var before = Events().Count;

        _capture.RaiseDefaultDeviceChanged("新耳机");

        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.Equal(before, Events().Count);
    }

    [Fact]
    public void Watchdog_WhileTranscribing_NeitherFinishesAnythingNorCountsDown()
    {
        var countdown = new List<int>();
        _controller.RecordingCountdown += (_, s) => countdown.Add(s);
        BeginTranscription();
        var before = Events().Count;

        // 主键松着、时钟走过整整一个录音上限还多：录音中这会触发 KEYUP 丢失收尾、倒数与到点收尾。
        _hook.SimulateLostKeyUp();
        AdvanceWatchdogTicks(320);

        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Empty(countdown);
        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.Equal(before, Events().Count);
    }

    [Fact]
    public void HookLost_WhileTranscribing_KeepsTranscribing_AndSaysSoWhenItEnds()
    {
        BeginTranscription();
        var before = Events().Count;

        _hook.RaiseHookLost();

        Assert.Equal(SessionState.Transcribing, _controller.State);
        Assert.Equal(before, Events().Count);

        // 进度照常走。
        _controller.ReportTranscriptionProgress(40);
        Assert.Equal("转写中 40%", _controller.LastMessage);

        // 转写成功收尾，钩子失效这件事也不能就此吞掉：「钩子」只出现在那一条提示里。
        _controller.EndTranscription(failure: null);

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.Contains("钩子", _controller.LastMessage ?? string.Empty);
    }

    [Fact]
    public void HookLost_WhileTranscribing_ThenTheTranscriptionFails_ReportsBoth()
    {
        BeginTranscription();
        _hook.RaiseHookLost();

        _controller.EndTranscription("无法解码该文件");

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.Contains("无法解码该文件", _controller.LastMessage ?? string.Empty);
        Assert.Contains("钩子", _controller.LastMessage ?? string.Empty);
    }

    [Theory]
    [InlineData(InjectResult.Ok)]
    [InlineData(InjectResult.Failed)]
    public async Task StaleRecognitionLanding_WhileTranscribing_DoesNotKickItOut(InjectResult injectResult)
    {
        // 识别在途时再按一次短按，状态回到 Idle 而上一路识别仍挂着；此刻开始转写是合法的。
        // 上一路落地时走的是「成功回静止态」或「失败转红条」两条路之一，都不许踩掉转写。
        _engine.Gate = new TaskCompletionSource();
        _engine.NextResult = "上一句";
        _injector.NextResult = injectResult;

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Recognizing);

        _hook.RaisePressed();
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(100));
        Assert.Equal(SessionState.Idle, _controller.State);

        BeginTranscription();
        var before = Events().Count;

        _engine.Gate.SetResult();

        // 等上一路真的走到上屏那一步，再在一段窗口里反复确认转写没被踩。
        var deadline = Environment.TickCount64 + 2000;
        while (_injector.Injections.Count == 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Single(_injector.Injections);

        var settle = Environment.TickCount64 + 200;
        while (Environment.TickCount64 < settle)
        {
            Assert.Equal(SessionState.Transcribing, _controller.State);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(before, Events().Count);
    }

    [Fact]
    public async Task Shutdown_WhileTranscribing_CompletesAndLaterCallsAreInert()
    {
        // 组合根保证关停前先取消转写并等它结束；这里测的是它没做到时控制器自身不崩、不卡。
        BeginTranscription();

        await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));

        Assert.True(_engine.IsDisposed);
        var before = Events().Count;

        var exception = Record.Exception(() =>
        {
            _controller.ReportTranscriptionProgress(50);
            _controller.EndTranscription("关停后才到的失败");
            _controller.EndTranscription(failure: null);
        });

        Assert.Null(exception);
        Assert.Equal(before, Events().Count);
    }
}
