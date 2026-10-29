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

    private readonly TrayIconHost _tray;

    private readonly AppWiring _wiring;

    public AppWiringTests()
    {
        // 引擎先加载好，否则按下热键会被「识别引擎未就绪」当场拒收，本组每条都会红在
        // 一个与接线毫无关系的地方。
        _engine.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        _controller = new SessionController(
            _hook, _capture, _engine, _injector, _probe, _monitor,
            SessionOptions.CreateDefault(), _time, NullLogger<SessionController>.Instance);

        _tray = new TrayIconHost(
            pause: () => { },
            resume: () => { },
            openSettings: () => { },
            toggleTranscription: () => { },
            openModelFolder: () => { },
            openLogFolder: () => { },
            requestExit: () => { },
            NullLogger<TrayIconHost>.Instance);

        // 通知时长与状态机那条失败提示同源，用同一个假时钟推。
        _wiring = new AppWiring(
            _controller, _overlay, _tray, _poster.Post,
            _time, SessionOptions.CreateDefault().FailedDisplayDuration);

        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));
    }

    /// <summary>转写中途最可能弹出的那类通知：关配置窗时热键重装失败。</summary>
    private const string HotkeyReinstallFailed = "热键重装失败：Ctrl+Alt+Space 已被别的程序占用";

    /// <summary>通知与状态机的失败一字不差时用的文案。文案不同的话，比文案那一步独自就分得开两者，测不出通知的记录清没清。</summary>
    private const string SameWords = "转写失败：磁盘已满";

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
    /// 收尾之后才来的通知一次也不许投递，也不许起计时器。
    /// 判据是投递次数而不只是悬浮条：闭包里那道拦截也能让悬浮条不变，只看悬浮条的话开头那道检查删了照样绿。
    /// </summary>
    [Fact]
    public void ANoticeArrivingAfterDispose_PostsNothing_AndArmsNoTimer()
    {
        _wiring.Dispose();

        _wiring.ShowNotice("识别引擎加载失败：模型文件缺失");

        Assert.Equal(0, _poster.PostCount);
        Assert.Equal(0, _poster.Drain());

        _time.Advance(NoticeDuration * 2);

        Assert.Equal(0, _poster.PostCount);
        Assert.Equal(SessionState.Idle, _overlay.State);
        Assert.False(_overlay.IsVisible);
    }

    /// <summary>通知已投递、UI 线程还没跑到它时收了尾：闭包跑起来时不许再改悬浮条。</summary>
    [Fact]
    public void ANoticePostedJustBeforeDispose_LeavesTheOverlayAlone_WhenTheUiThreadGetsToItAfterwards()
    {
        _wiring.ShowNotice("识别引擎加载失败：模型文件缺失");

        _wiring.Dispose();

        // 阳性对照：闭包确实跑了，是它自己没动手，不是压根没排上队。
        Assert.Equal(1, _poster.Drain());

        Assert.Equal(SessionState.Idle, _overlay.State);
        Assert.False(_overlay.IsVisible);
    }

    /// <summary>
    /// 计时器已到点、收场闭包已投递，UI 线程还没跑到它时收了尾：闭包跑起来时同样不许再改悬浮条。
    /// 上面那条只管计时器还没到点的情形，掐计时器管不到已经投出去的闭包。
    /// </summary>
    [Fact]
    public void ANoticeTimerThatFiredJustBeforeDispose_LeavesTheOverlayAlone_WhenTheUiThreadGetsToItAfterwards()
    {
        _wiring.ShowNotice("识别引擎加载失败：模型文件缺失");
        _poster.Drain();

        _time.Advance(NoticeDuration);

        _wiring.Dispose();

        // 阳性对照：收场闭包确实跑了。
        Assert.Equal(1, _poster.Drain());

        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal("识别引擎加载失败：模型文件缺失", _overlay.Message);
        Assert.True(_overlay.IsVisible);
    }

    /// <summary>
    /// 退出时有转写在跑：取消转写让状态机报出 Idle，这条已投递、UI 线程还没跑到它时收了尾。
    /// 闭包跑起来时不许再动悬浮条，否则它在 Avalonia 拆平台层之后同步驱动 <c>Window.Hide()</c>。
    /// 退订管不到已经排上队的闭包。
    /// </summary>
    [Fact]
    public void AStatusChangePostedJustBeforeDispose_LeavesTheOverlayAlone_WhenTheUiThreadGetsToItAfterwards()
    {
        QueueTheIdleThatEndsATranscription();

        _wiring.Dispose();

        // 闭包确实跑了，是它自己没动手。
        Assert.Equal(1, _poster.Drain());

        Assert.Equal(SessionState.Transcribing, _overlay.State);
        Assert.Equal("转写中 0%", _overlay.Message);
        Assert.True(_overlay.IsVisible);
    }

    /// <summary>阳性对照：同样的步骤不收尾，那条 Idle 确实改了悬浮条。少了它，上一条的「没变」可能只是事件没投进来。</summary>
    [Fact]
    public void AStatusChangePostedWithoutDispose_HidesTheOverlay_WhenTheUiThreadGetsToIt()
    {
        QueueTheIdleThatEndsATranscription();

        Assert.Equal(1, _poster.Drain());

        Assert.Equal(SessionState.Idle, _overlay.State);
        Assert.Equal(string.Empty, _overlay.Message);
        Assert.False(_overlay.IsVisible);
    }

    /// <summary>录音中报来的一格音量已投递、UI 线程还没跑到它时收了尾：闭包跑起来时不许再写音量。</summary>
    [Fact]
    public void AVolumeReadingPostedJustBeforeDispose_LeavesTheOverlayAlone_WhenTheUiThreadGetsToItAfterwards()
    {
        QueueAVolumeReading();

        _wiring.Dispose();

        // 闭包确实跑了，是它自己没动手。
        Assert.Equal(1, _poster.Drain());

        Assert.Equal(0d, _overlay.Volume);
    }

    /// <summary>阳性对照：同样的步骤不收尾，那格音量确实上了悬浮条。</summary>
    [Fact]
    public void AVolumeReadingPostedWithoutDispose_MovesTheVolume_WhenTheUiThreadGetsToIt()
    {
        QueueAVolumeReading();

        Assert.Equal(1, _poster.Drain());

        Assert.Equal(0.5d, _overlay.Volume, tolerance: 0.001d);
    }

    /// <summary>倒数的一声已投递、UI 线程还没跑到它时收了尾：闭包跑起来时不许再写剩余秒数。</summary>
    [Fact]
    public void ACountdownTickPostedJustBeforeDispose_LeavesTheOverlayAlone_WhenTheUiThreadGetsToItAfterwards()
    {
        QueueACountdownTick();

        _wiring.Dispose();

        // 闭包确实跑了，是它自己没动手。
        Assert.Equal(1, _poster.Drain());

        Assert.Null(_overlay.RemainingSeconds);
    }

    /// <summary>阳性对照：同样的步骤不收尾，那一声倒数确实上了悬浮条。</summary>
    [Fact]
    public void ACountdownTickPostedWithoutDispose_ShowsTheSeconds_WhenTheUiThreadGetsToIt()
    {
        QueueACountdownTick();

        Assert.Equal(1, _poster.Drain());

        Assert.Equal(5, _overlay.RemainingSeconds);
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

    /// <summary>
    /// **到点收场的通知不再算「正在显示」。** 通知隐去之后，状态机报出一条一字不差的失败，用户趁红条还亮着开始新一轮转写：
    /// 进度必须当场上屏。旧通知的文案若一直留着，状态机的红条就被认成那条通知，每一条进度都被压住，
    /// 又没有通知计时器来放行，一直看不见到转写结束。
    /// </summary>
    [Fact]
    public void ANoticeThatHasTimedOut_DoesNotHoldOffTheProgress_BehindTheControllersRedBarInTheSameWords()
    {
        _wiring.Connect();

        _wiring.ShowNotice(SameWords);
        _poster.Drain();

        _time.Advance(NoticeDuration);
        _poster.Drain();

        // 阳性对照：通知确实已经到点收场。
        Assert.False(_overlay.IsVisible);

        Assert.True(_controller.TryBeginTranscription(out _));
        _controller.EndTranscription(SameWords);
        _poster.Drain();

        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal(SameWords, _overlay.Message);

        Assert.True(_controller.TryBeginTranscription(out _));
        _controller.ReportTranscriptionProgress(5);
        _poster.Drain();

        Assert.Equal(SessionState.Transcribing, _overlay.State);
        Assert.Equal("转写中 5%", _overlay.Message);
    }

    /// <summary>
    /// **被状态迁移顶掉的通知同样不再算「正在显示」。** 通知还在屏上时状态机报出一字不差的失败，把它顶掉。
    /// 用户接着开始新一轮转写，进度必须当场上屏，不许等那条已被顶掉的通知的计时器来放行。
    /// </summary>
    [Fact]
    public void ANoticeDisplacedByTheControllersRedBarInTheSameWords_DoesNotHoldOffTheNextProgress()
    {
        _wiring.Connect();

        _wiring.ShowNotice(SameWords);
        _poster.Drain();

        Assert.True(_controller.TryBeginTranscription(out _));
        _controller.EndTranscription(SameWords);
        _poster.Drain();

        Assert.True(_controller.TryBeginTranscription(out _));
        _controller.ReportTranscriptionProgress(5);
        _poster.Drain();

        Assert.Equal(SessionState.Transcribing, _overlay.State);
        Assert.Equal("转写中 5%", _overlay.Message);
    }

    /// <summary>
    /// 通知盖在状态机一字不差的红条上，又先于状态机的失败计时器到点：收场回到的仍是那条红条，
    /// 而通知的记录必须随收场清掉，否则接下来那轮转写的进度又被当成「通知期间」压住。
    ///
    /// <para>
    /// <b>通知时长须短于状态机的失败提示。</b>生产两者同长，状态机的计时器先到点、先把红条换成静止态，
    /// 收场那一处清理轮不到出场。时长短一半，它才是唯一的防线。
    /// </para>
    /// </summary>
    [Fact]
    public void ANoticeHandingBackToTheControllersRedBarInTheSameWords_NoLongerHoldsOffTheNextProgress()
    {
        using var wiring = new AppWiring(
            _controller, _overlay, _tray, _poster.Post, _time, NoticeDuration / 2);
        wiring.Connect();

        Assert.True(_controller.TryBeginTranscription(out _));
        _controller.EndTranscription(SameWords);
        _poster.Drain();

        wiring.ShowNotice(SameWords);
        _poster.Drain();

        _time.Advance(NoticeDuration / 2);
        _poster.Drain();

        // 状态机的红条还没到点，收场回到的正是它。
        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal(SameWords, _overlay.Message);

        Assert.True(_controller.TryBeginTranscription(out _));
        _controller.ReportTranscriptionProgress(5);
        _poster.Drain();

        Assert.Equal(SessionState.Transcribing, _overlay.State);
        Assert.Equal("转写中 5%", _overlay.Message);
    }

    /// <summary>
    /// 上一条通知的计时器恰在新通知投递之后、被掐之前到点（计时器线程与调用方并发）：它的收场闭包排在新通知之后跑，
    /// 此刻屏上已是新通知，它既不许收场，也不许抹掉新通知的记录，否则下一条进度当场把新通知顶掉。
    ///
    /// <para>
    /// <b>两条文案必须不同</b>：相同的话，迟到的闭包认得出屏上那条「是自己」，照规矩就该收场，要验的东西就没了。
    /// </para>
    /// </summary>
    [Fact]
    public void ALateTimerOfThePreviousNotice_DoesNotForgetTheNoticeNowOnScreen()
    {
        _wiring.Connect();

        Assert.True(_controller.TryBeginTranscription(out _));
        _poster.Drain();

        _wiring.ShowNotice("识别引擎加载失败：模型文件缺失");
        _poster.Drain();

        _time.Advance(NoticeDuration - TimeSpan.FromMilliseconds(1));

        // 新通知一投递，上一条的计时器就到点。
        _poster.AfterNextPost = () => _time.Advance(TimeSpan.FromMilliseconds(1));

        _wiring.ShowNotice(HotkeyReinstallFailed);

        // 阳性对照：新通知与上一条的收场闭包都排上了队，新通知在前。
        Assert.Equal(2, _poster.Drain());
        Assert.Equal(HotkeyReinstallFailed, _overlay.Message);

        _controller.ReportTranscriptionProgress(35);
        _poster.Drain();

        Assert.Equal(SessionState.Failed, _overlay.State);
        Assert.Equal(HotkeyReinstallFailed, _overlay.Message);
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

    /// <summary>转写进行中、悬浮条正显示着它，再让转写正常结束：那条 Idle 已投递，尚未执行。</summary>
    private void QueueTheIdleThatEndsATranscription()
    {
        _wiring.Connect();

        Assert.True(_controller.TryBeginTranscription(out _));
        _poster.Drain();

        // 起点与 Idle 那条三项全不同，哪一项被改都看得出。
        Assert.Equal(SessionState.Transcribing, _overlay.State);
        Assert.Equal("转写中 0%", _overlay.Message);
        Assert.True(_overlay.IsVisible);

        var postsBefore = _poster.PostCount;

        _controller.EndTranscription(failure: null);

        // 状态机确实报了 Idle，闭包排上了队。
        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal(postsBefore + 1, _poster.PostCount);
    }

    /// <summary>录音中喂一包全 0.5 的样本：那格音量已投递，尚未执行。</summary>
    private void QueueAVolumeReading()
    {
        _wiring.Connect();

        _hook.RaisePressed();
        _poster.Drain();

        var postsBefore = _poster.PostCount;

        _capture.PushSamples([.. Enumerable.Repeat(0.5f, 1600)]);

        Assert.Equal(postsBefore + 1, _poster.PostCount);
        Assert.Equal(0d, _overlay.Volume);
    }

    /// <summary>录音推到「还剩 5 秒」：那一声倒数已投递，尚未执行。逐跳推的理由见 <see cref="ACountdownTick_ReachesTheOverlay_ButOnlyAfterThePosterRunsIt"/>。</summary>
    private void QueueACountdownTick()
    {
        _wiring.Connect();

        _hook.RaisePressed();
        _poster.Drain();

        var postsBefore = _poster.PostCount;

        for (var i = 0; i < 275; i++)
        {
            _time.Advance(SessionOptions.CreateDefault().WatchdogInterval);
        }

        Assert.Equal(postsBefore + 1, _poster.PostCount);
        Assert.Null(_overlay.RemainingSeconds);
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

        /// <summary>下一次收下活儿之后顺手跑一次的动作，用来把别的线程恰在此刻发生的事（例如计时器到点）摆进同一条时间线。</summary>
        public Action? AfterNextPost { get; set; }

        public void Post(Action action)
        {
            Action? afterPost;

            lock (_gate)
            {
                _pending.Add(action);
                _postCount++;

                afterPost = AfterNextPost;
                AfterNextPost = null;
            }

            // 出了锁再跑：它多半会再投递一次。
            afterPost?.Invoke();
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
