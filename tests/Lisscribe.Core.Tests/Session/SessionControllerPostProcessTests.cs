using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lisscribe.Core.Tests.Session;

/// <summary>
/// 上屏前那两道后处理（中文数字转换、句末标点清理）在状态机里的落点与先后。
/// </summary>
public sealed class SessionControllerPostProcessTests : IAsyncDisposable
{
    private readonly FakeHotkeyHook _hook = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeAsrEngine _engine = new();
    private readonly FakeTextInjector _injector = new();
    private readonly FakeWindowProbe _probe = new();
    private readonly FakeSessionMonitor _monitor = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));

    private SessionController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null)
        {
            await _controller.ShutdownAsync(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>跑一整轮「按下—说话—松开」，返回真正递给注入器的那一串。</summary>
    private async Task<string?> InjectAsync(string recognized, bool applyItn)
    {
        _engine.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        _engine.NextResult = recognized;

        _controller = new SessionController(
            _hook, _capture, _engine, _injector, _probe, _monitor,
            SessionOptions.CreateDefault() with { ApplyChineseNumberItn = applyItn },
            _time, NullLogger<SessionController>.Instance);
        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));

        _hook.RaisePressed();
        _capture.PushSeconds(1.0);
        _hook.RaiseReleased(TimeSpan.FromMilliseconds(800));

        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline && _controller.State != SessionState.Idle)
        {
            await Task.Delay(5);
        }

        return _injector.LastText;
    }

    /// <summary>
    /// **中文数字转换必须跑在句末标点清理之前。**
    ///
    /// <para>
    /// 这一句是唯一能钉住先后的形状，随便挑个短句两种顺序结果都一样：
    /// 转换先跑，正文「1234块钱」6 字没过 8 字的阈值，句号被清掉；
    /// 清理先跑，正文「一千二百三十四块钱」9 字超了阈值，句号留下，转换再来也带着它。
    /// 阈值该按最终要上屏的那串算，否则同一句话转不转的行为不一致。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Injection_RunsItnBeforeTrimming()
    {
        Assert.Equal("1234块钱", await InjectAsync("一千二百三十四块钱。", applyItn: true));
    }

    [Fact]
    public async Task Injection_AppliesItnWhenEnabled()
    {
        Assert.Equal("早上9点半", await InjectAsync("早上九点半", applyItn: true));
    }

    [Fact]
    public async Task Injection_LeavesTextAloneWhenItnDisabled()
    {
        Assert.Equal("早上九点半", await InjectAsync("早上九点半", applyItn: false));
    }

    /// <summary>关掉转换时，那一句「1234块钱」的对照：句号按原文 9 字算，留得下来。</summary>
    [Fact]
    public async Task Injection_WithoutItn_KeepsPunctuationOnLongSentence()
    {
        Assert.Equal("一千二百三十四块钱。", await InjectAsync("一千二百三十四块钱。", applyItn: false));
    }
}
