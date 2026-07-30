using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lisscribe.Core.Tests.Session;

/// <summary>
/// 引擎还没加载时按热键会怎样。
///
/// <para>
/// <b>这是真机上踩出来的。</b>模型目录不对 → 启动时 <c>LoadAsync</c> 抛
/// <c>FileNotFoundException</c>（那句话带着「缺哪个文件、去哪找」）→ 组合根照样把钩子装上 →
/// 用户按住热键说完一整句 → 只等来一句「识别失败，详见日志」。真正说得清的那句话在启动那
/// 一瞬间闪过就没了，而每一次按键都在录一段注定扔掉的音频。
/// </para>
///
/// <para>
/// 本组不要求组合根「别装钩子」——引擎是后台异步加载的（规格：托盘图标不许等模型），
/// 那段窗口天然存在。要求的是：<b>那段窗口里按下热键，当场说清楚，且一个样本都不要录。</b>
/// </para>
/// </summary>
public sealed class SessionControllerEngineReadinessTests : IAsyncDisposable
{
    private readonly FakeHotkeyHook _hook = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeAsrEngine _engine = new();
    private readonly FakeTextInjector _injector = new();
    private readonly FakeWindowProbe _probe = new();
    private readonly FakeSessionMonitor _monitor = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionController _controller;

    /// <summary>引擎**故意不加载**——这正是被测的那个状态。</summary>
    public SessionControllerEngineReadinessTests()
    {
        _controller = new SessionController(
            _hook, _capture, _engine, _injector, _probe, _monitor,
            SessionOptions.CreateDefault(), _time, NullLogger<SessionController>.Instance);
        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));
    }

    public async ValueTask DisposeAsync() => await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));

    [Fact]
    public void Pressed_WhileTheEngineIsNotLoaded_RefusesToRecordAndSaysWhy()
    {
        _hook.RaisePressed();

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.False(_capture.IsCapturing);

        // 「引擎」这两个字在本状态机别处的任何提示里都不出现，故这条断言不是空的。
        Assert.Contains("识别引擎", _controller.LastMessage ?? string.Empty);
    }

    [Fact]
    public async Task ReleasedAfterARefusedPress_DoesNotRecogniseAnything()
    {
        // 拒收得拒得干净：既然一个样本都没录，松键就不该再触发一次识别，
        // 也不该把那条说明覆盖成「录音过短」。
        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.Empty(_injector.Injections);
        Assert.Contains("识别引擎", _controller.LastMessage ?? string.Empty);
    }

    [Fact]
    public async Task OnceTheEngineFinishesLoading_TheVeryNextPressRecordsNormally()
    {
        // 阴性对照。少了这一条，「按下永远拒收」这种实现照样把上面两条跑绿——
        // 而那是个比原缺陷更糟的实现。
        await _engine.LoadAsync(CancellationToken.None);

        _hook.RaisePressed();

        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.True(_capture.IsCapturing);
    }
}
