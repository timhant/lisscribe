using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lisscribe.Core.Tests.Session;

public sealed class SessionControllerBasicTests : IAsyncDisposable
{
    private readonly FakeHotkeyHook _hook = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeAsrEngine _engine = new();
    private readonly FakeTextInjector _injector = new();
    private readonly FakeWindowProbe _probe = new();
    private readonly FakeSessionMonitor _monitor = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionController _controller;

    public SessionControllerBasicTests()
    {
        // 引擎先加载好，本组测的才是「稳态下的会话」。不加载的话按下热键会被「引擎未就绪」
        // 当场拒收，本组每一条都会以指不到真因的方式红。
        _engine.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        _controller = new SessionController(
            _hook, _capture, _engine, _injector, _probe, _monitor,
            SessionOptions.CreateDefault(), _time, NullLogger<SessionController>.Instance);
        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));
    }

    public async ValueTask DisposeAsync() => await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));

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

    /// <summary>在给定窗口内轮询确认状态从未变为 Failed 且录音未被打断，用于验证「不得被踩」类不变量。</summary>
    private async Task AssertRecordingNeverStompedAsync(TimeSpan window)
    {
        var deadline = Environment.TickCount64 + (long)window.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Assert.NotEqual(SessionState.Failed, _controller.State);
            Assert.True(_capture.IsCapturing);
            await Task.Delay(5);
        }
    }

    /// <summary>制造一个已经处于 Faulted 状态的 Task，用于测试 ShutdownAsync 对故障工作线程的容错。</summary>
    private static async Task<Task> CreateFaultedTaskAsync()
    {
        var faultedTask = Task.Run(() => throw new InvalidOperationException("工作线程模拟故障"));
        try
        {
            await faultedTask;
        }
        catch (InvalidOperationException)
        {
            // 让它先真正进入 Faulted 状态，再交给调用方接管。
        }

        return faultedTask;
    }

    [Fact]
    public void Start_LeavesControllerIdleAndStartsHookAndMonitor()
    {
        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.True(_hook.IsStarted);
        Assert.True(_monitor.IsStarted);
        Assert.False(_capture.IsCapturing);
    }

    [Fact]
    public void Pressed_StartsRecordingImmediately()
    {
        // 按下瞬间即开始录音，不等阈值：等满阈值再开录会吞掉说话的头 0.3 秒
        // （「今天天气」录成「天天气」）。
        _hook.RaisePressed();

        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.True(_capture.IsCapturing);
    }

    [Fact]
    public async Task ReleasedBelowThreshold_DiscardsAudioAndReturnsToIdle()
    {
        _hook.RaisePressed();
        _capture.PushSeconds(0.1);

        _hook.RaiseReleased(TimeSpan.FromMilliseconds(120));

        await WaitForStateAsync(SessionState.Idle);
        Assert.False(_capture.IsCapturing);
        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.Empty(_injector.Injections);
    }

    [Fact]
    public async Task ReleasedAboveThreshold_RecognizesAndInjects()
    {
        _engine.NextResult = "打开设置";
        _probe.ForegroundWindow = 777;

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(1, _engine.RecognizeCallCount);
        Assert.Single(_injector.Injections);
        Assert.Equal("打开设置", _injector.Injections[0].Text);
        Assert.Equal(777, _injector.Injections[0].Window);
    }

    [Fact]
    public async Task Recognize_ReceivesEverySampleThatWasCaptured()
    {
        _hook.RaisePressed();
        _capture.PushSeconds(0.5);
        _capture.PushSeconds(0.5);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(1000));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal([16000], _engine.ReceivedSampleCounts);
    }

    [Fact]
    public async Task Injection_AppliesTrailingPunctuationTrimming()
    {
        _engine.NextResult = "打开设置。";

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal("打开设置", _injector.LastText);
    }

    [Fact]
    public async Task AudioShorterThanMinimum_FailsWithoutCallingEngine()
    {
        // 按住够久但几乎没出声（麦克风没开、说话太快），不该白跑一次识别。
        _hook.RaisePressed();
        _capture.PushSeconds(0.2);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Failed);
        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.Contains("过短", _controller.LastMessage);
    }

    [Fact]
    public async Task Failed_ReturnsToIdleAfterDisplayDuration()
    {
        _hook.RaisePressed();
        _capture.PushSeconds(0.2);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Failed);

        _time.Advance(TimeSpan.FromSeconds(3));

        await WaitForStateAsync(SessionState.Idle);
    }

    [Fact]
    public async Task PressedDuringFailedDisplay_InterruptsAndStartsNewRecording()
    {
        // Failed 展示期间按下热键，打断计时立即开始新录音，红条即刻消失。
        _hook.RaisePressed();
        _capture.PushSeconds(0.2);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Failed);

        _hook.RaisePressed();

        Assert.Equal(SessionState.Recording, _controller.State);
    }

    [Fact]
    public async Task EngineThrowing_TransitionsToFailedNotCrash()
    {
        _engine.NextException = new InvalidOperationException("模型炸了");

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Failed);
        Assert.Contains("识别失败", _controller.LastMessage);
        Assert.Empty(_injector.Injections);
    }

    [Fact]
    public async Task SecondPressDuringRecognition_StartsNewRecordingAndQueuesSerially()
    {
        // 识别期间再按热键，允许开新录音、任务串行排队。拒绝反而让用户以为程序卡死。
        _engine.Gate = new TaskCompletionSource();
        _engine.NextResult = "第一句";

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Recognizing);

        _hook.RaisePressed();
        Assert.Equal(SessionState.Recording, _controller.State);
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        _engine.Gate.SetResult();
        await WaitForStateAsync(SessionState.Idle);

        Assert.Equal(2, _engine.RecognizeCallCount);
        Assert.Equal(2, _injector.Injections.Count);
    }

    [Fact]
    public void VolumeChanged_FiresWhileRecording()
    {
        float? lastVolume = null;
        _controller.VolumeChanged += (_, v) => lastVolume = v;

        _hook.RaisePressed();
        _capture.PushSamples(Enumerable.Repeat(0.5f, 1600).ToArray());

        Assert.NotNull(lastVolume);
        Assert.Equal(0.5f, lastVolume.Value, tolerance: 0.01f);
    }

    [Fact]
    public void StatusChanged_FiresOnEveryTransition()
    {
        var states = new List<SessionState>();
        _controller.StatusChanged += (_, e) => states.Add(e.State);

        _hook.RaisePressed();

        Assert.Equal([SessionState.Recording], states);
    }

    [Fact]
    public async Task FirstRecognitionFailsWhileSecondRecordingIsActive_DoesNotStompActiveRecording()
    {
        // 第一路识别的失败结局（EnterFailed）不得无条件把 State 踩成 Failed——此刻用户
        // 可能正按着第二次录音。State 一旦被错踩成 Failed，OnReleased 的
        // `if (State != Recording) return;` 守卫就会把第二次的 KEYUP 直接吞掉：
        // 麦克风不再停止，缓冲永久积压，会话卡死。
        _engine.Gate = new TaskCompletionSource();
        _engine.NextException = new InvalidOperationException("模型炸了");

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Recognizing);

        // 第一路仍卡在 Gate 上时，用户已经开始第二次录音。
        _hook.RaisePressed();
        Assert.Equal(SessionState.Recording, _controller.State);

        // 放行第一路，使其抛出 NextException。
        _engine.Gate.SetResult();

        // 在一段窗口内反复确认：第二次录音未被第一路的失败打断，麦克风仍在录。
        await AssertRecordingNeverStompedAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal(SessionState.Recording, _controller.State);

        // 第二路应正常识别成功（准备好不再抛异常的引擎响应）。
        _engine.NextException = null;
        _engine.NextResult = "第二句";

        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(2, _engine.RecognizeCallCount);
        Assert.Single(_injector.Injections);
        Assert.Equal("第二句", _injector.Injections[0].Text);
    }

    [Fact]
    public async Task ProbeThrowsDuringHandling_WorkerSurvivesAndProcessesNextRequest()
    {
        // GetForegroundWindow / CanInjectInto / InjectAsync 在生产环境都是原生 P/Invoke 的包装
        // （焦点查询、UIPI 完整性级别比对、SendInput、剪贴板访问），均可能抛异常。只在
        // RecognizeAsync 外面兜一层 try/catch 不够——异常会逃出 ProcessQueueAsync 的
        // await foreach，把工作线程整个弄死：State 永远卡在 Recognizing，之后按热键照样排队，
        // 但队列从此无人消费，且没有任何日志能定位现场。
        //
        // 探针在 OnPressed（按下瞬间同步捕获目标窗口）与 HandleRequestAsync（工作线程里比对
        // 焦点是否已切换）各被调用一次。本条要打的是后一个调用点，故等 RaisePressed 已成功
        // 捕获目标窗口之后才让探针开始抛。
        _engine.NextResult = "第一句";

        _hook.RaisePressed();
        _probe.ThrowOnGetForegroundWindow = new InvalidOperationException("探针炸了");
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Failed);
        Assert.Empty(_injector.Injections);
        Assert.Contains("出错", _controller.LastMessage);

        // 工作线程必须还活着：清掉故障根因后，下一次录音必须能被正常处理到底，
        // 而不是永远卡在排队里没人处理。
        _probe.ThrowOnGetForegroundWindow = null;
        _engine.NextResult = "第二句";

        _hook.RaisePressed(); // 打断 Failed 展示，立即开始新录音
        Assert.Equal(SessionState.Recording, _controller.State);
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(2, _engine.RecognizeCallCount);
        Assert.Single(_injector.Injections);
        Assert.Equal("第二句", _injector.Injections[0].Text);
    }

    [Fact]
    public void ProbeThrowsDuringPress_DoesNotEscapeHandlerAndStillStartsRecording()
    {
        // 另一个调用点：OnPressed 在按下瞬间同步调 _probe.GetForegroundWindow() 锁定目标窗口。
        // 这里比工作线程那处更严重——Pressed 在生产环境是从 WH_KEYBOARD_LL 钩子的消息泵线程
        // 同步触发的，托管异常从钩子回调里逃出去后果未知且 catch 不到。
        //
        // 降级策略：探针失败时退化成「目标窗口未知」（nint.Zero），录音正常开始，识别结果转入
        // 剪贴板兜底，而不是注入到一个其实并不确定的窗口。
        _probe.ThrowOnGetForegroundWindow = new InvalidOperationException("探针炸了");

        var exception = Record.Exception(() => _hook.RaisePressed());

        Assert.Null(exception);
        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.True(_capture.IsCapturing);
    }

    [Fact]
    public async Task ShutdownAsync_WhenWorkerTaskIsFaulted_StillDisposesEngine()
    {
        // ShutdownAsync 须对「工作线程已经以任何原因故障退出」保持防御：`_worker.WaitAsync`
        // 若只兜底 TimeoutException，工作线程是 Faulted 时异常会直接从 ShutdownAsync 里逃出去，
        // 跳过安全关键的最后一步——Dispose 引擎，于是原生 handle 的释放次序被绕过。
        //
        // 请求级异常都已被就地捕获，正常路径再也造不出 Faulted 的工作线程，故这里用反射直接
        // 替换私有的 _worker 字段：验的是这条防御路径本身，不依赖某个触发路径还存在。
        var workerField = typeof(SessionController).GetField(
            "_worker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(workerField);

        var faultedWorker = await CreateFaultedTaskAsync();
        workerField!.SetValue(_controller, faultedWorker);

        await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));

        Assert.True(_engine.IsDisposed);
    }

    [Fact]
    public async Task FocusChangedAndClipboardCopyFails_ReportsClipboardFailureNotFakeSuccess()
    {
        // 焦点切换后走剪贴板兜底，但不许无视 InjectAsync 的返回值一律报「已复制到剪贴板」。
        // 剪贴板写入本身是会失败的（别的进程占着剪贴板是常见场景，注入器为此专门做了重试循环）；
        // 真失败时若仍报成功，用户以为文本安全，实际上音频丢了、文本也没进剪贴板。
        _engine.NextResult = "打开设置";
        _injector.NextResult = InjectResult.Failed;

        _hook.RaisePressed();
        _probe.ForegroundWindow = 42; // 按下瞬间捕获的目标窗口是默认值 1000，这里制造焦点切换
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Failed);
        Assert.Contains("剪贴板", _controller.LastMessage);
        Assert.DoesNotContain("已复制到剪贴板", _controller.LastMessage);
    }

    [Fact]
    public async Task ElevatedTargetAndClipboardCopyFails_ReportsClipboardFailureNotFakeSuccess()
    {
        // UIPI 分支上的同一处问题。
        _engine.NextResult = "打开设置";
        _injector.NextResult = InjectResult.Failed;
        _probe.ElevatedWindows.Add(1000); // 按下瞬间捕获的目标窗口（Fake 默认 1000）判定为需要提权

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Failed);
        Assert.Contains("剪贴板", _controller.LastMessage);
        Assert.DoesNotContain("已复制到剪贴板", _controller.LastMessage);
    }

    [Fact]
    public async Task DuplicatePressedWithoutRelease_DoesNotClearAlreadyCapturedAudio()
    {
        // OnPressed 要有与 OnReleased 那句 `if (State != Recording) return;` 对称的守卫：
        // 生产环境里 HotkeyMatcher 会滤掉 OS 自动重复，但状态机不该依赖那层上游保证——
        // 第二次 Pressed（中间没有 Released）会把已说出口的音频 `_buffer.Clear()` 掉，等于丢字。
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);

        _hook.RaisePressed(); // 重复按下，理论上不该发生，但这里没有上游保证

        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Idle);
        // 若重复按下清空了缓冲，这里只会收到后半段 16000 个样本，而不是两段共 32000 个。
        Assert.Equal([32000], _engine.ReceivedSampleCounts);
    }

    [Fact]
    public async Task AudioAtExactlyMinimumDuration_ReachesEngine()
    {
        // 边界：MinAudioDuration = 0.5s 且判定用严格 `<`，故恰好 0.5s 应放行而非判过短。
        _hook.RaisePressed();
        _capture.PushSeconds(0.5);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Idle);
        Assert.Equal(1, _engine.RecognizeCallCount);
    }

    [Fact]
    public async Task ResultBecomesEmptyOnlyAfterTrimming_FailsWithoutInjecting()
    {
        // 识别器返回「。」：trim 前非空、trim 后成空串，须按「识别结果为空」处理，
        // 而不是把空字符串当合法文本注入进去。
        _engine.NextResult = "。";

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await WaitForStateAsync(SessionState.Failed);
        Assert.Contains("为空", _controller.LastMessage);
        Assert.Empty(_injector.Injections);
    }
}
