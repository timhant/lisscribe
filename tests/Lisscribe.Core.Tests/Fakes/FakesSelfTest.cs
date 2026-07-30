using Lisscribe.Core.Hotkey;
using Xunit;

namespace Lisscribe.Core.Tests.Fakes;

public sealed class FakesSelfTest
{
    [Fact]
    public void FakeHotkeyHook_RaisesPressedAndReleased()
    {
        var hook = new FakeHotkeyHook();
        var pressed = 0;
        TimeSpan? releasedWith = null;
        hook.Pressed += (_, _) => pressed++;
        hook.Released += (_, held) => releasedWith = held;

        hook.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));
        hook.RaisePressed();
        hook.RaiseReleased(TimeSpan.FromMilliseconds(500));

        Assert.True(hook.IsStarted);
        Assert.Equal(1, pressed);
        Assert.Equal(TimeSpan.FromMilliseconds(500), releasedWith);
        Assert.False(hook.IsMainKeyPhysicallyDown);
    }

    [Fact]
    public void FakeAudioCapture_PushSecondsProducesCorrectSampleCount()
    {
        var capture = new FakeAudioCapture();
        var received = 0;
        capture.DataAvailable += (_, data) => received += data.Length;

        capture.PushSeconds(1.5);

        Assert.Equal(24000, received);
    }

    [Fact]
    public async Task FakeAsrEngine_ReturnsConfiguredResult()
    {
        var engine = new FakeAsrEngine { NextResult = "你好" };
        await engine.LoadAsync(CancellationToken.None);

        var text = await engine.RecognizeAsync(new float[100], CancellationToken.None);

        Assert.True(engine.IsLoaded);
        Assert.Equal("你好", text);
        Assert.Equal(1, engine.RecognizeCallCount);
        Assert.Equal([100], engine.ReceivedSampleCounts);
    }

    [Fact]
    public async Task FakeAsrEngine_GateBlocksUntilReleased()
    {
        var engine = new FakeAsrEngine { Gate = new TaskCompletionSource() };

        var pending = engine.RecognizeAsync(new float[10], CancellationToken.None);
        Assert.False(pending.IsCompleted);

        engine.Gate.SetResult();

        Assert.Equal("识别结果", await pending);
    }

    [Fact]
    public void FakeWindowProbe_ReportsElevatedWindowsAsUninjectable()
    {
        var probe = new FakeWindowProbe { ForegroundWindow = 42 };
        probe.ElevatedWindows.Add(42);

        Assert.Equal(42, probe.GetForegroundWindow());
        Assert.False(probe.CanInjectInto(42));
        Assert.True(probe.CanInjectInto(43));
    }
}
