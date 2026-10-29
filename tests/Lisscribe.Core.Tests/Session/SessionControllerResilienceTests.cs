using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lisscribe.Core.Tests.Session;

/// <summary>
/// 韧性用例：Windows 用各种方式把键盘从状态机手里夺走时，会话必须仍能自行落地。
/// 核心是看门狗——Win+L / UAC / Ctrl+Alt+Del 切到 secure desktop 后钩子永远收不到
/// KEYUP，没有看门狗就永久卡死在 Recording：麦克风一直录、内存一直涨，且再按热键会被
/// 当成「已在录音中」而无法恢复。
/// </summary>
public sealed class SessionControllerResilienceTests : IAsyncDisposable
{
    private readonly FakeHotkeyHook _hook = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeAsrEngine _engine = new();
    private readonly FakeTextInjector _injector = new();
    private readonly FakeWindowProbe _probe = new();
    private readonly FakeSessionMonitor _monitor = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionController _controller;

    private readonly Lock _eventsGate = new();
    private readonly List<(SessionState State, string? Message)> _events = [];

    /// <summary>与默认配置的失败提示时长独立取值：这里写死 3 秒，被测代码改了默认值这条会先红。</summary>
    private static readonly TimeSpan FailedDisplayDuration = TimeSpan.FromSeconds(3);

    public SessionControllerResilienceTests()
    {
        // 引擎未加载时热键会被当场拒收，那是另一组的题目，不该让本组每条都栽在它上面。
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

    /// <summary>
    /// 让 <see cref="FakeTimeProvider"/> 的时钟前进，但保证主键仍处于「物理按下」状态——
    /// 看门狗每 200ms 巡检一次，此时它只会确认「键还按着、缓冲没满」然后原样返回。
    /// 需要这一步是因为按住时长由 now - _recordingStartedAt 算出：不推进时钟，任何
    /// 收尾路径拿到的 held 都是 0，会被短按规则（&lt; 300ms）当作误触整段丢弃。
    /// </summary>
    private void HoldFor(TimeSpan duration)
    {
        Assert.True(_hook.IsMainKeyPhysicallyDown, "调用方必须先 RaisePressed，否则看门狗会抢先结束录音");
        _time.Advance(duration);
    }

    /// <summary>
    /// 开始等下一次迁入 <paramref name="expected"/>，须在触发迁移之前调用。
    /// 只轮询 State 不够：识别线程上 LastMessage 紧随 State 写入，轮询看到新状态时文案可能还是旧的。
    /// </summary>
    private Task ExpectTransitionTo(SessionState expected)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _controller.StatusChanged += (_, e) =>
        {
            if (e.State == expected)
            {
                reached.TrySetResult();
            }
        };

        return reached.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 一跳一跳推进假时钟，直到红条到点退场。失败计时器由识别线程在报出 Failed 之后才建，
    /// 测试线程一次推满可能推在它建好之前，那它就再也等不到点。
    /// </summary>
    private void AdvanceUntilFailedDisplayEnds()
    {
        var step = SessionOptions.CreateDefault().WatchdogInterval;
        var budget = FailedDisplayDuration * 2;

        for (var spent = TimeSpan.Zero; _controller.State == SessionState.Failed && spent < budget; spent += step)
        {
            _time.Advance(step);
        }
    }

    /// <summary>录一段正常长度的话送去识别，并让识别挂在返回的闸门上，供「识别在途时暂停」类用例做前置。</summary>
    private async Task<TaskCompletionSource> StartParkedRecognitionAsync()
    {
        var gate = _engine.Gate = new TaskCompletionSource();
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Recognizing);

        return gate;
    }

    /// <summary>同上，但闸门不随取消打开，模拟原生解码中途打断不了。返回时识别已真的挂在闸门上。</summary>
    private async Task<TaskCompletionSource> StartUninterruptibleRecognitionAsync()
    {
        _engine.IgnoreCancellation = true;
        var gate = await StartParkedRecognitionAsync();

        // Recognizing 在入队时就置上了，须等工作线程真把请求取出来，否则关停会让它根本不被识别。
        await _engine.Parked.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        return gate;
    }

    /// <summary>
    /// 摆出「识别在途 → 关停开始 → 识别才落地」，等关停返回。
    /// 返回关停开始前的事件条数，此后的事件全由这次落地引起。
    /// </summary>
    private async Task<int> LandAfterShutdownBeginsAsync()
    {
        var gate = await StartUninterruptibleRecognitionAsync();
        var before = Events().Count;

        // 先不 await：ShutdownAsync 第一段同步代码就已停止受理，拿到任务时关停已经开始。
        var shutdown = _controller.ShutdownAsync(TimeSpan.FromSeconds(5));

        gate.SetResult();

        // 关停要等工作线程退出，故它返回时落地已经走完。
        await shutdown;

        // 阳性对照：引擎确实在关停开始之后交出了结果，否则「没有事件」可能只是落地根本没发生。
        Assert.Single(_engine.CompletionThreadIds);

        return before;
    }

    /// <summary>录音中换了设备、带着「录音设备已变更」进识别，并让识别挂在返回的闸门上。</summary>
    private TaskCompletionSource StartParkedRecognitionAfterDeviceChange()
    {
        var gate = _engine.Gate = new TaskCompletionSource();
        _hook.RaisePressed();
        _capture.PushSeconds(1.5);
        HoldFor(TimeSpan.FromMilliseconds(800));

        _capture.RaiseDefaultDeviceChanged("新耳机");

        // 阳性对照：会话上确实挂着这句告知，否则后面「它还在」的断言什么也没证明。
        Assert.Equal(SessionState.Recognizing, _controller.State);
        Assert.Equal("录音设备已变更", _controller.LastMessage);

        return gate;
    }

    [Fact]
    public void Pause_WhileIdle_EntersPausedAndIgnoresHotkey()
    {
        _controller.Pause();

        Assert.Equal(SessionState.Paused, _controller.State);

        _hook.RaisePressed();

        Assert.Equal(SessionState.Paused, _controller.State);
        Assert.False(_capture.IsCapturing);
    }

    /// <summary>
    /// <b>暂停必须把键还给系统，只改状态机是不够的。</b>
    ///
    /// <para>
    /// 钩子从 <see cref="SessionController.Start"/> 一路武装到 <c>Dispose</c>，若暂停不动它，
    /// <c>HotkeyMatcher.Evaluate</c> 照旧对旧组合返回 <c>SuppressAndPress</c>、钩子回调
    /// <c>return 1</c>，而被钩子吞掉的键不会到任何进程。症状是配置窗点了「录制组合键」
    /// 之后按热键**毫无反应**——不是录不对，是一个按键事件都收不到；托盘那个「暂停」同理，
    /// 暂停期间旧热键仍被全局吞掉，别的应用一样收不到。
    /// </para>
    ///
    /// <para>
    /// <see cref="Pause_WhileIdle_EntersPausedAndIgnoresHotkey"/> 抓不到它：那条拿
    /// <c>_hook.RaisePressed()</c> 当输入，而真机上那次 <c>Pressed</c> 恰恰是钩子吞掉按键之后
    /// 才发出的——假件把「键去哪了」整段替掉了，故它只看得见状态机的反应。
    /// </para>
    ///
    /// <para>
    /// 拨放行开关而不是摘钩子：<see cref="Pause"/> 持着 <c>_gate</c>，而 <c>KeyboardHook.Stop</c>
    /// 要 Join 事件派发线程，那条线程正回头抢同一把 <c>_gate</c>。「吞还是放」由
    /// <c>HotkeyMatcherTests</c> 直接测，本条只问状态机拨没拨这个开关。
    /// </para>
    /// </summary>
    [Fact]
    public void Pause_HandsTheKeysBack_OrTheOldHotkeyStaysStolenFromEveryOtherWindow()
    {
        Assert.False(_hook.PassThrough, "阳性对照：Start 之后钩子本该是武装的，否则下面那条断言不成立");

        _controller.Pause();

        Assert.Equal(SessionState.Paused, _controller.State);
        Assert.True(_hook.PassThrough);
    }

    /// <summary>
    /// 放了就得收回来 —— 只放不收的话，暂停一次热键就永久失灵，而状态显示一切正常。
    /// </summary>
    [Fact]
    public void Resume_TakesTheKeysBack()
    {
        _controller.Pause();

        _controller.Resume();

        Assert.False(_hook.PassThrough);
    }

    /// <summary>
    /// 暂停请求尚未落地就撤销的那条路 —— <see cref="Resume"/> 会提前 return，
    /// 放行开关若没写在 return 之前，就永远停在放行态了。
    /// </summary>
    [Fact]
    public async Task Resume_BeforeThePauseLands_StillTakesTheKeysBack()
    {
        _engine.NextResult = "半句话";
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        HoldFor(TimeSpan.FromMilliseconds(800));

        _controller.Pause();
        _controller.Resume();

        Assert.False(_hook.PassThrough);

        // 撤销之后终态必须是 Idle 而不是 Paused，否则「放行开关已收回」只是个孤立的标志。
        await WaitForStateAsync(SessionState.Idle);
    }

    [Fact]
    public async Task Pause_WhileRecording_FinishesCurrentSessionFirst()
    {
        // 暂停时若正在 Recording，先按正常流程结束当前会话再进 Paused，不丢已录音频。
        // 注意终态必须是 Paused 而不是 Idle——识别是异步的，它落地时若忘了暂停请求，
        // 用户点了「暂停」却发现热键仍然生效。
        _engine.NextResult = "半句话";
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        HoldFor(TimeSpan.FromMilliseconds(800));

        _controller.Pause();

        await WaitForStateAsync(SessionState.Paused);
        Assert.Equal(1, _engine.RecognizeCallCount);
        Assert.Equal("半句话", _injector.LastText);
    }

    [Fact]
    public void Resume_ReturnsToIdleAndReenablesHotkey()
    {
        _controller.Pause();

        _controller.Resume();

        Assert.Equal(SessionState.Idle, _controller.State);

        _hook.RaisePressed();

        Assert.Equal(SessionState.Recording, _controller.State);
    }

    /// <summary>识别在途时暂停又继续：热键其实没停，「已暂停热键」不许留到落地后的 Idle 上。</summary>
    [Fact]
    public async Task PauseThenResume_WhileRecognizing_SuccessLandsInIdleWithoutThePauseNotice()
    {
        var gate = await StartParkedRecognitionAsync();

        _controller.Pause();
        _controller.Resume();

        var idle = ExpectTransitionTo(SessionState.Idle);
        gate.SetResult();
        await idle;

        Assert.Null(_controller.LastMessage);
    }

    /// <summary>同上，识别失败那一路：红条只说失败原因，到点回到的 Idle 上也不许有暂停告知。</summary>
    [Fact]
    public async Task PauseThenResume_WhileRecognizing_FailureShowsOnlyTheReason_AndIdleCarriesNoPauseNotice()
    {
        _engine.NextException = new InvalidOperationException("模型炸了");
        var gate = await StartParkedRecognitionAsync();

        _controller.Pause();
        _controller.Resume();

        var failed = ExpectTransitionTo(SessionState.Failed);
        gate.SetResult();
        await failed;

        Assert.Equal("识别失败，详见日志", _controller.LastMessage);

        var idle = ExpectTransitionTo(SessionState.Idle);
        AdvanceUntilFailedDisplayEnds();
        await idle;

        Assert.Null(_controller.LastMessage);
    }

    /// <summary>暂停之前会话上已挂着的告知（这里是录音设备已变更），不许因「暂停 → 继续」而丢掉。</summary>
    [Fact]
    public async Task PauseThenResume_WhileRecognizing_KeepsTheNoticeFromBeforeThePause()
    {
        var gate = StartParkedRecognitionAfterDeviceChange();

        _controller.Pause();
        _controller.Resume();

        var idle = ExpectTransitionTo(SessionState.Idle);
        gate.SetResult();
        await idle;

        Assert.Equal("录音设备已变更", _controller.LastMessage);
    }

    /// <summary>同上，识别失败那一路：红条照旧只说失败原因（不暂停时也是如此），到点回到的 Idle 上告知仍在。</summary>
    [Fact]
    public async Task PauseThenResume_WhileRecognizing_FailureStillLeavesTheEarlierNoticeOnIdle()
    {
        _engine.NextException = new InvalidOperationException("模型炸了");
        var gate = StartParkedRecognitionAfterDeviceChange();

        _controller.Pause();
        _controller.Resume();

        var failed = ExpectTransitionTo(SessionState.Failed);
        gate.SetResult();
        await failed;

        Assert.Equal("识别失败，详见日志", _controller.LastMessage);

        var idle = ExpectTransitionTo(SessionState.Idle);
        AdvanceUntilFailedDisplayEnds();
        await idle;

        Assert.Equal("录音设备已变更", _controller.LastMessage);
    }

    /// <summary>录音中暂停：告知随收尾换成「已暂停热键」再送去识别。识别落地前点「继续」，Idle 上同样不许留着它。</summary>
    [Fact]
    public async Task PauseThenResume_WhileRecording_LandsInIdleWithoutThePauseNotice()
    {
        var gate = _engine.Gate = new TaskCompletionSource();
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        HoldFor(TimeSpan.FromMilliseconds(800));

        _controller.Pause();

        // 阳性对照：暂停已把会话收尾送去识别，告知也已挂上。
        Assert.Equal(SessionState.Recognizing, _controller.State);
        Assert.Equal("已暂停热键", _controller.LastMessage);

        _controller.Resume();

        var idle = ExpectTransitionTo(SessionState.Idle);
        gate.SetResult();
        await idle;

        Assert.Null(_controller.LastMessage);
    }

    /// <summary>对照：识别中暂停而不继续，落地后进 Paused，告知照旧是「已暂停热键」。</summary>
    [Fact]
    public async Task Pause_WhileRecognizing_WithoutResume_LandsInPausedSayingSo()
    {
        var gate = await StartParkedRecognitionAsync();

        _controller.Pause();

        var paused = ExpectTransitionTo(SessionState.Paused);
        gate.SetResult();
        await paused;

        Assert.Equal("已暂停热键", _controller.LastMessage);
    }

    /// <summary>对照，识别失败那一路：红条只说失败原因，到点落到 Paused，告知照旧是「已暂停热键」。</summary>
    [Fact]
    public async Task Pause_WhileRecognizing_WithoutResume_FailureShowsTheReason_ThenPausedSayingSo()
    {
        _engine.NextException = new InvalidOperationException("模型炸了");
        var gate = await StartParkedRecognitionAsync();

        _controller.Pause();

        var failed = ExpectTransitionTo(SessionState.Failed);
        gate.SetResult();
        await failed;

        Assert.Equal("识别失败，详见日志", _controller.LastMessage);

        var paused = ExpectTransitionTo(SessionState.Paused);
        AdvanceUntilFailedDisplayEnds();
        await paused;

        Assert.Equal("已暂停热键", _controller.LastMessage);
    }

    [Fact]
    public async Task Watchdog_WhenKeyUpIsLost_ForcesFinish()
    {
        // 核心回归测试：Win+L / UAC / Ctrl+Alt+Del 切 secure desktop 时 KEYUP 永不到达。
        // 没有看门狗，状态机永久卡死在 Recording。
        _engine.NextResult = "被抢救回来的一句";
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);

        // 先按满 800ms（此间看门狗只会看到「键还按着」），再让 KEYUP 凭空消失：
        // 下一次巡检算出的 held 已越过 300ms 短按阈值，故必须识别而不是丢弃。
        HoldFor(TimeSpan.FromMilliseconds(800));
        _hook.SimulateLostKeyUp();
        _time.Advance(TimeSpan.FromMilliseconds(400));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(1, _engine.RecognizeCallCount);
        Assert.Equal("被抢救回来的一句", _injector.LastText);
    }

    [Fact]
    public async Task Watchdog_WhenKeyUpLostBeforeThreshold_DiscardsInsteadOfRecognizing()
    {
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);

        // 只过了 200ms（< 300ms 阈值）就丢了 KEYUP：按短按处置。
        _hook.SimulateLostKeyUp();
        _time.Advance(TimeSpan.FromMilliseconds(200));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(0, _engine.RecognizeCallCount);
    }

    [Fact]
    public async Task MaxRecordingDuration_AutoFinishesAndRecognizes()
    {
        // 键盘被压住时无上限缓冲会一路涨到 GB 级，喂进 encoder 即 OOM。
        // 缓冲已满等于已录满上限，是不是短按无从谈起——这条路径必须绕开短按判定，
        // 否则一次系统时钟回拨就足以让整整一分钟的话被当成误触静默丢掉。
        _engine.NextResult = "说了很久";
        _hook.RaisePressed();
        _capture.PushSeconds(60);

        _time.Advance(TimeSpan.FromMilliseconds(400));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(1, _engine.RecognizeCallCount);
        Assert.Equal(960000, _engine.ReceivedSampleCounts[0]);
    }

    [Fact]
    public async Task SessionLocked_WhileRecording_CancelsWithoutRecognizing()
    {
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);

        _monitor.RaiseSessionLocked();

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.False(_capture.IsCapturing);
    }

    [Fact]
    public async Task SystemSuspending_WhileRecording_CancelsWithoutRecognizing()
    {
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);

        _monitor.RaiseSystemSuspending();

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(0, _engine.RecognizeCallCount);
    }

    [Fact]
    public async Task DefaultDeviceChanged_WhileRecording_FinishesWithWhatWasCaptured()
    {
        // 中途拔耳机不得丢弃已录部分：直接丢的话，说到一半拔耳机后 DataAvailable 停止而状态
        // 仍 Recording，松开时缓冲不足半秒、落入「音频过短」红条，前半句被静默丢掉。
        _engine.NextResult = "拔耳机前说的话";
        _hook.RaisePressed();
        _capture.PushSeconds(1.5);
        HoldFor(TimeSpan.FromMilliseconds(800));

        _capture.RaiseDefaultDeviceChanged("新耳机");

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(1, _engine.RecognizeCallCount);
        Assert.Equal("拔耳机前说的话", _injector.LastText);

        // 告知必须活到会话终态。它若只在进 Recognizing 的瞬间存在，用户根本来不及看见，
        // 之后每次说话都莫名其妙地录进了另一个麦克风。
        Assert.Contains("设备", _controller.LastMessage ?? string.Empty);
    }

    [Fact]
    public async Task DefaultDeviceChanged_WithTooLittleAudio_ExplainsDeviceChangeAlongsideFailure()
    {
        // 刚开口就拔耳机，缓冲确实不足 0.5s：红条只写「录音过短」等于什么也没解释。
        _hook.RaisePressed();
        _capture.PushSeconds(0.2);
        HoldFor(TimeSpan.FromMilliseconds(800));

        _capture.RaiseDefaultDeviceChanged("新耳机");

        await WaitForStateAsync(SessionState.Failed);
        Assert.Contains("设备", _controller.LastMessage ?? string.Empty);
        Assert.Contains("过短", _controller.LastMessage ?? string.Empty);
    }

    [Fact]
    public async Task DefaultDeviceChanged_WhileIdle_DoesNothing()
    {
        _capture.RaiseDefaultDeviceChanged("新耳机");

        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal(0, _engine.RecognizeCallCount);
    }

    [Fact]
    public async Task HookLost_WhileRecording_FinishesAndReportsFailure()
    {
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);

        _hook.RaiseHookLost();

        await WaitForStateAsync(SessionState.Failed);
        Assert.Contains("热键", _controller.LastMessage ?? string.Empty);
    }

    [Fact]
    public async Task WatchdogTickWhenCaptureStopThrows_StillFinishesAndDoesNotEscapeTimerCallback()
    {
        // 看门狗回调跑在线程池计时器线程上，锁屏／设备变更／钩子失效三个处理器则从系统回调线程
        // 同步触发——异常从这些地方逃出去不是「这一次失败」，是整个进程崩掉。
        // _capture.Stop() 包着 WASAPI 原生调用，正是会抛的那个；停不下来的采集器更不该连带
        // 把状态机卡死在 Recording。
        _engine.NextResult = "采集器抛异常也要保住的一句";
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        HoldFor(TimeSpan.FromMilliseconds(800));

        _capture.ThrowOnStop = new InvalidOperationException("WASAPI 炸了");
        _hook.SimulateLostKeyUp();

        var exception = Record.Exception(() => _time.Advance(TimeSpan.FromMilliseconds(400)));

        Assert.Null(exception);

        // 否则测试收尾的 ShutdownAsync 也会被同一个异常绊住。
        _capture.ThrowOnStop = null;

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal("采集器抛异常也要保住的一句", _injector.LastText);
    }

    [Fact]
    public async Task Shutdown_CancelsInFlightRecognitionBeforeDisposingEngine()
    {
        // 关停序列。顺序错了会在原生 handle 已释放而工作线程仍在 Decode 时触发进程级
        // AccessViolation——try/catch 抓不到，用户看到「程序已停止工作」。
        _engine.Gate = new TaskCompletionSource();
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Recognizing);

        // 等到识别真的挂在闸门上再关停。Recognizing 是**入队**那一刻就置上的，工作线程此时
        // 可能还没把请求取出来；那种相位下取消令牌打在一个还没注册取消回调的地方，
        // WasCancelledMidFlight 恒为 false，用例偶发红（整条 2ms 就失败，与超时无关）。
        await _engine.Parked.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await _controller.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.True(_engine.WasCancelledMidFlight);
        Assert.True(_engine.IsDisposed);
    }

    [Fact]
    public async Task Shutdown_DoesNotInjectPendingResults()
    {
        // 关停时丢弃未上屏结果并落日志——不要在关停过程中往随机窗口注入文本。
        _engine.Gate = new TaskCompletionSource();
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Recognizing);

        // 同上。这条更阴：识别还没真开始就关停的话，「没有注入」这个结论对任何实现都成立，
        // 用例绿得毫无意义。
        await _engine.Parked.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await _controller.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(_injector.Injections);
    }

    [Fact]
    public async Task Shutdown_IsIdempotent()
    {
        await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));
        await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));

        Assert.True(_engine.IsDisposed);
    }

    [Fact]
    public async Task Shutdown_WhileRecording_StopsCaptureAndDiscards()
    {
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);

        await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));

        Assert.False(_capture.IsCapturing);
        Assert.Equal(0, _engine.RecognizeCallCount);
    }

    /// <summary>
    /// 原生解码打断不了，识别可能在关停掐掉失败计时器之后才失败。那时不许再进 Failed，
    /// 更不许起一个没人再掐的计时器，到点又报一次 Idle，那时组合根已在拆。
    /// </summary>
    [Fact]
    public async Task Shutdown_ThenAnUninterruptibleRecognitionFails_RaisesNothingAndLeavesNoFailedTimer()
    {
        _engine.NextException = new InvalidOperationException("模型炸了");

        var before = await LandAfterShutdownBeginsAsync();

        Assert.Empty(Events()[before..]);

        _time.Advance(FailedDisplayDuration * 2);

        Assert.Empty(Events()[before..]);
    }

    /// <summary>同上，识别结果为空那一路。</summary>
    [Fact]
    public async Task Shutdown_ThenAnUninterruptibleRecognitionComesBackEmpty_RaisesNothingAndLeavesNoFailedTimer()
    {
        _engine.NextResult = string.Empty;

        var before = await LandAfterShutdownBeginsAsync();

        Assert.Empty(Events()[before..]);

        _time.Advance(FailedDisplayDuration * 2);

        Assert.Empty(Events()[before..]);
    }

    /// <summary>同上，上屏成功那一路：不许再迁回静止态。上屏本身发不发生不归本条管。</summary>
    [Fact]
    public async Task Shutdown_ThenAnUninterruptibleRecognitionInjects_RaisesNothing()
    {
        _engine.NextResult = "关停前说完的一句";

        var before = await LandAfterShutdownBeginsAsync();

        Assert.Empty(Events()[before..]);
    }

    /// <summary>上面几条的阳性对照：同样的引擎配置不关停，失败落地照常进 Failed、到点回 Idle。</summary>
    [Fact]
    public async Task WithoutShutdown_AnUninterruptibleRecognitionFails_ShowsFailedThenReturnsToIdle()
    {
        _engine.NextException = new InvalidOperationException("模型炸了");
        var gate = await StartUninterruptibleRecognitionAsync();
        var before = Events().Count;

        var failed = ExpectTransitionTo(SessionState.Failed);
        gate.SetResult();
        await failed;

        var idle = ExpectTransitionTo(SessionState.Idle);
        AdvanceUntilFailedDisplayEnds();
        await idle;

        Assert.Equal([SessionState.Failed, SessionState.Idle], Events()[before..].Select(e => e.State));
    }
}
