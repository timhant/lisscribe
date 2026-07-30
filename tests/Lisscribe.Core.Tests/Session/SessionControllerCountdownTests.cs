using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lisscribe.Core.Tests.Session;

/// <summary>
/// 录音临近上限时的倒数。
///
/// <para>
/// 上限本身早有兜底（到点自动收尾并提示「已达最长录音时长」），**但那是事后**。
/// Qwen3 档只能录 25 秒，而用户在界面上无从得知这件事——真撞上去，看到的是
/// 「话说了一半没了」，本项目已知最难自诊的症状之一。倒数是那 25 秒里唯一的事前提醒。
/// </para>
///
/// <para>
/// 状态机只报**剩几秒**这个事实，不报文案：界面文案归 <c>OverlayViewModel.DisplayText</c>，
/// 理由写在那里——Core 不该认识「用户看到什么」。
/// </para>
/// </summary>
public sealed class SessionControllerCountdownTests : IAsyncDisposable
{
    private readonly FakeHotkeyHook _hook = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeAsrEngine _engine = new();
    private readonly FakeTextInjector _injector = new();
    private readonly FakeWindowProbe _probe = new();
    private readonly FakeSessionMonitor _monitor = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionController _controller;

    private readonly List<int> _seen = [];

    public SessionControllerCountdownTests()
    {
        _engine.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        // 默认档：上限 60s、临界 5s、看门狗 200ms 一跳。
        _controller = new SessionController(
            _hook, _capture, _engine, _injector, _probe, _monitor,
            SessionOptions.CreateDefault(), _time, NullLogger<SessionController>.Instance);

        _controller.RecordingCountdown += (_, seconds) => _seen.Add(seconds);
        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));
    }

    public async ValueTask DisposeAsync() => await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));

    [Fact]
    public void Countdown_TicksOncePerSecond_ThroughTheLastFiveSeconds()
    {
        _hook.RaisePressed();

        // 推到还剩 0.2s：正好走完整个临界区，又不触发到点收尾（那是另一条路）。
        // 这段里看门狗巡检了 299 次——**断言恰好五个数**，
        // 「每跳都发一次」会得到二十多个，界面就成了每秒闪五下。
        AdvanceWatchdogTicks(299);

        Assert.Equal([5, 4, 3, 2, 1], _seen);
    }

    [Fact]
    public void Countdown_SaysNothingWhileThereIsStillPlentyOfTime()
    {
        // 阴性对照。还剩 5.2s，差一跳进临界区——没有这条，上一条测的可能只是「它一直在发」。
        _hook.RaisePressed();

        AdvanceWatchdogTicks(274);

        Assert.Empty(_seen);
    }

    [Fact]
    public void Countdown_SaysNothingWhenNobodyIsRecording()
    {
        // 没按热键，看门狗照样在跳。这一条钉住「巡检不等于录音」。
        AdvanceWatchdogTicks(299);

        Assert.Empty(_seen);
    }

    [Fact]
    public async Task Countdown_StartsFromTheTopOnEachRecording()
    {
        // 上一轮停在「还剩 5 秒」，下一轮就该再从 5 说起。跨会话记着上次那个数的话，
        // 第二轮的 5 会被当成「跟上次一样」咽掉——**恰好咽掉最要紧的那一次提示**。
        _hook.RaisePressed();
        _capture.PushSeconds(1);
        AdvanceWatchdogTicks(275);

        Assert.Equal([5], _seen);

        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));
        await WaitForStateAsync(SessionState.Idle);

        _hook.RaisePressed();
        _capture.PushSeconds(1);
        AdvanceWatchdogTicks(275);

        Assert.Equal([5, 5], _seen);
    }

    /// <summary>
    /// 按看门狗的节拍逐跳推进假时钟。
    ///
    /// <para>
    /// **不能写成一次 <c>Advance(59.9s)</c>。** 实测（<c>Microsoft.Extensions.TimeProvider.Testing</c>）：
    /// 一次跨过 299 个周期时，补跳的回调里读到的时间是**推进后的终点**，不是各自的到期时刻，
    /// 于是整段倒数只得到一声「还剩 1 秒」。生产里时间是真在走的，这纯是假时钟的取样问题，
    /// 但照那样写出来的测试会把「每秒一声」测成「只有一声」。
    /// </para>
    /// </summary>
    private void AdvanceWatchdogTicks(int ticks)
    {
        var interval = SessionOptions.CreateDefault().WatchdogInterval;

        for (var i = 0; i < ticks; i++)
        {
            _time.Advance(interval);
        }
    }

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
}
