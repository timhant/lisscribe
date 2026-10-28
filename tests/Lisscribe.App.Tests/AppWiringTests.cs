using Lisscribe.App.Composition;
using Lisscribe.App.Tray;
using Lisscribe.App.ViewModels;
using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 组合根接线层：把状态机的事件经投递器转给悬浮条。用真的 <c>SessionController</c> 加假件跑整条
/// 链路，不需要麦克风、模型、键盘钩子。本套钉住的是<b>转达必须经投递器，不许自己直接调</b>。
///
/// <para>
/// <b>投递器故意不立刻执行</b>（<see cref="DeferredPoster"/> 收下就返回，要显式 <c>Drain</c> 才跑）：
/// 若它同步执行，把 <c>_postToUi(() =&gt; _overlay.ApplyState(...))</c> 改写成
/// <c>_overlay.ApplyState(...)</c> 之后每条断言照样绿，测试就成了空的。有了延迟投递，
/// 「没 Drain 之前悬浮条纹丝不动」这一格才有区分力，才真钉住 <c>Dispatcher.UIThread.Post</c>。
/// </para>
///
/// <para>
/// 这里不建 Avalonia 应用：<see cref="OverlayViewModel"/> 是纯 ObservableObject，
/// 没人订它的 PropertyChanged 时不碰任何窗口；<see cref="TrayIconHost"/> 的构造也只存字段，
/// 碰 Avalonia 的是它的 <c>Attach</c>，而接线层一次也不调。
/// </para>
/// </summary>
public sealed class AppWiringTests : IAsyncDisposable
{
    private readonly FakeHotkeyHook _hook = new();

    private readonly FakeAudioCapture _capture = new();

    private readonly FakeAsrEngine _engine = new();

    private readonly FakeTextInjector _injector = new();

    private readonly FakeWindowProbe _probe = new();

    private readonly FakeSessionMonitor _monitor = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero));

    private readonly SessionController _controller;

    private readonly OverlayViewModel _overlay = new();

    private readonly DeferredPoster _poster = new();

    private readonly AppWiring _wiring;

    public AppWiringTests()
    {
        // 引擎先加载好，否则按下热键会被「识别引擎未就绪」当场拒收，本组每条都会红在
        // 一个与接线毫无关系的地方。
        _engine.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        _controller = new SessionController(
            _hook, _capture, _engine, _injector, _probe, _monitor,
            SessionOptions.CreateDefault(), _time, NullLogger<SessionController>.Instance);

        var tray = new TrayIconHost(
            pause: () => { },
            resume: () => { },
            openSettings: () => { },
            openModelFolder: () => { },
            openLogFolder: () => { },
            requestExit: () => { },
            NullLogger<TrayIconHost>.Instance);

        // 通知时长与状态机那条失败提示同源，用同一个假时钟推。
        _wiring = new AppWiring(
            _controller, _overlay, tray, _poster.Post,
            _time, SessionOptions.CreateDefault().FailedDisplayDuration);

        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));
    }

    /// <summary>转写中途最可能弹出的那类通知：关配置窗时热键重装失败。</summary>
    private const string HotkeyReinstallFailed = "热键重装失败：Ctrl+Alt+Space 已被别的程序占用";

    private static TimeSpan NoticeDuration => SessionOptions.CreateDefault().FailedDisplayDuration;

    public async ValueTask DisposeAsync() => await _controller.ShutdownAsync(TimeSpan.FromSeconds(2));

    [Fact]
    public void AStatusChange_ReachesTheOverlay_ButOnlyAfterThePosterRunsIt()
    {
        _wiring.Connect();

        _hook.RaisePressed();

        // 状态机确实动了——事件的确发了出来。
        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.Equal(1, _poster.PostCount);

        // 而悬浮条还没动。这一格就是「不许自己直接调」的判据：
        // 把 AppWiring 里的 _postToUi(...) 换成直接调用，这里立刻红。
        Assert.Equal(SessionState.Idle, _overlay.State);
        Assert.False(_overlay.IsVisible);

        _poster.Drain();

        Assert.Equal(SessionState.Recording, _overlay.State);
        Assert.True(_overlay.IsVisible);
    }

    [Fact]
    public void AVolumeChange_ReachesTheOverlay_ButOnlyAfterThePosterRunsIt()
    {
        _wiring.Connect();

        _hook.RaisePressed();
        _poster.Drain();

        // 全 0.5 的样本，RMS 恰好也是 0.5——与初值 0 区分得开。
        // 推静音在这里是无效样本：那样 RMS 是 0，与「压根没转达」一模一样。
        _capture.PushSamples([.. Enumerable.Repeat(0.5f, 1600)]);

        Assert.Equal(0f, _overlay.Volume);

        _poster.Drain();

        Assert.Equal(0.5f, _overlay.Volume, tolerance: 0.001f);
    }

    [Fact]
    public void ACountdownTick_ReachesTheOverlay_ButOnlyAfterThePosterRunsIt()
    {
        // 倒数在**看门狗计时器线程**上报出来，与状态、音量同一条禁令：只许 Post，不许 Invoke。
        _wiring.Connect();

        _hook.RaisePressed();
        _poster.Drain();

        // 默认档 60s 上限、5s 临界、看门狗 200ms 一跳：275 跳正好落在「还剩 5 秒」。
        // 必须逐跳推：假时钟一次跨过多个周期时，补跳的回调读到的是推进后的终点而非各自的
        // 到期时刻，整段倒数会塌成一声。
        for (var i = 0; i < 275; i++)
        {
            _time.Advance(SessionOptions.CreateDefault().WatchdogInterval);
        }

        Assert.Null(_overlay.RemainingSeconds);

        _poster.Drain();

        Assert.Equal(5, _overlay.RemainingSeconds);
    }

    [Fact]
    public void Connect_CalledTwice_StillPostsOnce()
    {
        _wiring.Connect();
        _wiring.Connect();

        _hook.RaisePressed();

        // 重复订阅的症状是同一次状态变更被投递两遍，在 UI 上完全看不出来——只有计数看得见。
        Assert.Equal(1, _poster.PostCount);
    }

    [Fact]
    public void Disconnect_StopsBothFlows()
    {
        _wiring.Connect();
        _wiring.Disconnect();

        _hook.RaisePressed();
        _capture.PushSamples([.. Enumerable.Repeat(0.5f, 1600)]);

        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.Equal(0, _poster.PostCount);
    }

    /// <summary>
    /// 整条链路走一遍：按下 → 录一秒 → 松开 → 识别 → 上屏 → 回 Idle，悬浮条每一格都要跟上。
    /// 少了这条，「钩子装上了、可谁也没订阅」这类缺陷要到实机才暴露。
    /// </summary>
    [Fact]
    public async Task AWholeSession_DrivesTheOverlayFromRecordingBackToHidden()
    {
        _engine.NextResult = "你好世界";
        _wiring.Connect();

        _hook.RaisePressed();
        _poster.Drain();
        Assert.Equal(SessionState.Recording, _overlay.State);

        // MinAudioDuration 是 0.5s，喂足 1s 才不会被判为「太短」。
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromSeconds(1));

        await WaitForControllerAsync(SessionState.Idle);
        _poster.Drain();

        Assert.Equal("你好世界", _injector.LastText);
        Assert.Equal(SessionState.Idle, _overlay.State);
        Assert.False(_overlay.IsVisible);

        // 收尾时音量必须归零，否则最后一格音量会冻在条上，看着像还在录。
        Assert.Equal(0f, _overlay.Volume);
    }

    /// <summary>
    /// 组合根自己的报警（模型加载失败、配置损坏、热键装不上）走 <see cref="AppWiring.ShowNotice"/>，
    /// <b>不经状态机</b>，故状态机那条 <c>_failedTimer</c> 帮不上忙：只 Post 一次 <c>Failed</c> 就完事的话，
    /// 红提示会一直挂在屏幕右下角，直到用户碰巧按一次热键把它顶掉。
    /// </summary>
    [Fact]
    public void ANotice_HidesItself_InsteadOfStayingOnScreenForever()
    {
        _wiring.ShowNotice("识别引擎加载失败：模型文件缺失");
        _poster.Drain();

        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.True(_overlay.IsVisible);

        _time.Advance(NoticeDuration);
        _poster.Drain();

        Assert.Equal(SessionState.Idle, _overlay.State);
        Assert.False(_overlay.IsVisible);
        Assert.Equal(string.Empty, _overlay.Message);
    }

    /// <summary>
    /// 反过来的一格：**时间没到就不许消失**。少了这条，把隐藏写成「Post 完立刻隐藏」
    /// 也能让上面那条绿——用户则连一眼都来不及看。
    /// </summary>
    [Fact]
    public void ANotice_StaysPut_UntilItsTimeIsActuallyUp()
    {
        _wiring.ShowNotice("配置文件损坏，已回落默认值");
        _poster.Drain();

        _time.Advance(NoticeDuration - TimeSpan.FromMilliseconds(1));
        _poster.Drain();

        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal("配置文件损坏，已回落默认值", _overlay.Message);
        Assert.True(_overlay.IsVisible);
    }

    /// <summary>
    /// 通知还在屏上时用户按了热键：录音态绝不能被那条迟到的隐藏打回去。
    /// 这一格钉的是「到点才判、且照当时的状态判」，不是「起timer时就把结局定死」。
    /// </summary>
    [Fact]
    public void ASessionStartedDuringTheNotice_IsNotWipedOut_WhenTheTimerFires()
    {
        _wiring.Connect();
        _wiring.ShowNotice("识别引擎加载失败：模型文件缺失");
        _poster.Drain();

        _hook.RaisePressed();
        _poster.Drain();
        Assert.Equal(SessionState.Recording, _overlay.State);

        _time.Advance(NoticeDuration);
        _poster.Drain();

        // 录音照旧。被打回 Idle 的话，用户看到的是「说着说着指示器自己没了」。
        Assert.Equal(SessionState.Recording, _overlay.State);
        Assert.True(_overlay.IsVisible);
    }

    /// <summary>
    /// 连来两条通知：第二条要拿满整段时长，而不是接着第一条的余额。
    /// 写成「不取消上一个计时器」时，第二条会在第一条到点的那一刻被抹掉——实际显示时间取决于
    /// 两条之间隔了多久，短则几十毫秒。
    ///
    /// <para>
    /// <b>两条通知的文案必须一样</b>，否则这条测试是空的：被测代码里那道「隐藏前先确认屏上还是
    /// 我这条」的判断，在文案不同时自己就挡下了迟到的第一条计时器，变异掉
    /// <c>CancelNoticeTimerLocked</c> 照样全绿；只有文案相同时它才是唯一的防线。
    /// 重复文案也是实况——「识别引擎加载失败：…」连开两次配置窗就会一字不差地再来一遍。
    /// </para>
    /// </summary>
    [Fact]
    public void ASecondNotice_GetsTheFullDuration_NotTheRemainderOfTheFirst()
    {
        const string SameMessage = "识别引擎加载失败：模型文件缺失";

        _wiring.ShowNotice(SameMessage);
        _poster.Drain();

        _time.Advance(NoticeDuration - TimeSpan.FromMilliseconds(10));

        _wiring.ShowNotice(SameMessage);
        _poster.Drain();

        // 第一条的计时器若还活着，这一步就到点了，第二条当场被抹掉。
        _time.Advance(TimeSpan.FromMilliseconds(20));
        _poster.Drain();

        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal(SameMessage, _overlay.Message);

        // 而它自己的那份时长照样管用。
        _time.Advance(NoticeDuration);
        _poster.Drain();

        Assert.Equal(SessionState.Idle, _overlay.State);
    }

    /// <summary>
    /// 收尾之后那个在途的计时器不许再动悬浮条：那一刻 Avalonia 正在拆平台层，
    /// 而 <c>Dispatcher.UIThread.Post</c> 之后同步驱动的是 <c>Window.Hide()</c>。
    /// </summary>
    [Fact]
    public void AfterDispose_ThePendingNoticeTimer_DoesNotTouchTheOverlayAnyMore()
    {
        _wiring.ShowNotice("识别引擎加载失败：模型文件缺失");
        _poster.Drain();

        _wiring.Dispose();

        var postsBefore = _poster.PostCount;
        _time.Advance(NoticeDuration * 2);

        Assert.Equal(postsBefore, _poster.PostCount);
        Assert.Equal(SessionState.Failed, _overlay.State);
    }

    /// <summary>
    /// **转写中弹的通知，到点后要回到转写进度，不能回到空闲。** 转写一跑几分钟，期间任何一条通知
    /// （例如关配置窗时热键重装失败）过几秒就把悬浮条整个藏掉的话，转写还在跑，进度从此看不见。
    ///
    /// <para>
    /// <b>两条进度必须是不同的百分比</b>，且断言的是最后那条：只记「应用过的」状态时，被通知压住的两条
    /// 都没记下，到点回放的是通知之前那条「0%」；只来一条进度的话，「记第一条」与「记最后一条」分不开。
    /// </para>
    /// </summary>
    [Fact]
    public void ANoticeDuringTranscription_HoldsOffTheProgress_ThenHandsBackTheLatestOne()
    {
        _wiring.Connect();

        Assert.True(_controller.TryBeginTranscription(out _));
        _poster.Drain();

        // 阳性对照：通知弹出之前，悬浮条真在显示转写。
        Assert.Equal(SessionState.Transcribing, _overlay.State);

        _wiring.ShowNotice(HotkeyReinstallFailed);
        _poster.Drain();

        _controller.ReportTranscriptionProgress(35);
        _controller.ReportTranscriptionProgress(70);
        _poster.Drain();

        // 进度每识别完一段就可能来一条，照旧上屏的话，通知一闪就没了。
        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal(HotkeyReinstallFailed, _overlay.Message);

        _time.Advance(NoticeDuration);
        _poster.Drain();

        Assert.Equal(SessionState.Transcribing, _overlay.State);
        Assert.Equal("转写中 70%", _overlay.Message);
        Assert.True(_overlay.IsVisible);
    }

    /// <summary>
    /// 压住的只有进度。状态机自己的失败（例如转写失败）是用户此刻更该看见的事，照旧当场顶掉通知。
    /// 到此为止、不推时间：状态机自己那个失败计时器与通知同长，一推它也回静止态，要验的东西就没了。
    /// </summary>
    [Fact]
    public void AFailureFromTheController_DuringTranscription_DisplacesTheNoticeAtOnce()
    {
        _wiring.Connect();

        Assert.True(_controller.TryBeginTranscription(out _));
        _poster.Drain();

        _wiring.ShowNotice(HotkeyReinstallFailed);
        _poster.Drain();

        _controller.EndTranscription("识别失败：引擎返回错误");
        _poster.Drain();

        // 两条都是 Failed，分得开它们的只有文案。
        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal("识别失败：引擎返回错误", _overlay.Message);
    }

    /// <summary>
    /// 转写在通知显示期间正常结束：Idle 当场生效，悬浮条隐去；通知那个计时器到点后不许再把任何东西翻出来。
    /// </summary>
    [Fact]
    public void TranscriptionEndingDuringTheNotice_HidesTheOverlayAtOnce_AndTheTimerLeavesItAlone()
    {
        _wiring.Connect();

        Assert.True(_controller.TryBeginTranscription(out _));
        _poster.Drain();

        _wiring.ShowNotice(HotkeyReinstallFailed);
        _poster.Drain();

        _controller.EndTranscription(failure: null);
        _poster.Drain();

        Assert.Equal(SessionState.Idle, _overlay.State);
        Assert.False(_overlay.IsVisible);

        _time.Advance(NoticeDuration);
        _poster.Drain();

        Assert.Equal(SessionState.Idle, _overlay.State);
        Assert.False(_overlay.IsVisible);
        Assert.Equal(string.Empty, _overlay.Message);
    }

    /// <summary>
    /// **状态机自己的红条不是通知。** 转写失败、红条还亮着，用户马上又挑了一个文件：新一轮的进度必须当场上屏。
    /// 「通知正在显示」的判据若只看 Failed、不比文案，这里的进度就被当成「通知期间」压住，而状态机的失败
    /// 计时器已被新一轮转写掐掉、也没有通知计时器来放行，红条一直挂到转写结束。
    ///
    /// <para>
    /// 先弹过一条通知是有意的：它让「最后一次通知的文案」有值，「有过通知就压」那种写法也一并在这里红。
    /// </para>
    /// </summary>
    [Fact]
    public void TheControllersOwnRedBar_IsNotMistakenForANotice_WhenTheNextTranscriptionStarts()
    {
        _wiring.Connect();

        Assert.True(_controller.TryBeginTranscription(out _));
        _poster.Drain();

        _wiring.ShowNotice(HotkeyReinstallFailed);
        _poster.Drain();

        _controller.EndTranscription("识别失败：引擎返回错误");
        _poster.Drain();

        // 阳性对照：此刻屏上是状态机的红条，不是那条通知。
        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal("识别失败：引擎返回错误", _overlay.Message);

        Assert.True(_controller.TryBeginTranscription(out _));
        _poster.Drain();

        Assert.Equal(SessionState.Transcribing, _overlay.State);
        Assert.Equal("转写中 0%", _overlay.Message);
    }

    private async Task WaitForControllerAsync(SessionState expected, int timeoutMs = 3000)
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

        Assert.Fail($"等待状态机抵达 {expected} 超时，当前为 {_controller.State}");
    }

    /// <summary>
    /// 收下活儿就返回，攒着不跑——生产里那个 <c>Dispatcher.UIThread.Post</c> 的行为形状。
    /// 加锁是必需的：状态机在工作线程、热键线程、采集线程上都会引发事件。
    /// </summary>
    private sealed class DeferredPoster
    {
        private readonly List<Action> _pending = [];

        private readonly Lock _gate = new();

        public int PostCount
        {
            get
            {
                lock (_gate)
                {
                    return _postCount;
                }
            }
        }

        private int _postCount;

        public void Post(Action action)
        {
            lock (_gate)
            {
                _pending.Add(action);
                _postCount++;
            }
        }

        /// <summary>把攒下的活儿依次跑掉，返回跑了几条。</summary>
        public int Drain()
        {
            Action[] batch;

            lock (_gate)
            {
                batch = [.. _pending];
                _pending.Clear();
            }

            foreach (var action in batch)
            {
                action();
            }

            return batch.Length;
        }
    }
}
