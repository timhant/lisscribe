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
    public async Task FakeAsrEngine_ScriptsByCallNumber_AndRecordsCopiesOfTheSamples()
    {
        var engine = new FakeAsrEngine { RecordSamples = true, Script = n => $"第{n}次" };
        var buffer = new float[] { 1, 2, 3 };

        var first = await engine.RecognizeAsync(buffer, CancellationToken.None);

        // 调用方复用缓冲，记下的必须是当时的副本。
        buffer[1] = 9;

        var second = await engine.RecognizeAsync(buffer.AsMemory(1, 2), CancellationToken.None);

        Assert.Equal("第1次", first);
        Assert.Equal("第2次", second);
        Assert.Equal([1f, 2f, 3f], engine.ReceivedSamples[0]);
        Assert.Equal([9f, 3f], engine.ReceivedSamples[1]);
    }

    [Fact]
    public async Task FakeAsrEngine_IgnoreCancellation_ReturnsDespiteACancelledToken()
    {
        var cancelled = new CancellationToken(canceled: true);

        var honouring = new FakeAsrEngine();
        var ignoring = new FakeAsrEngine { IgnoreCancellation = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => honouring.RecognizeAsync(new float[1], cancelled));
        Assert.Equal("识别结果", await ignoring.RecognizeAsync(new float[1], cancelled));
    }

    [Fact]
    public async Task FakeAsrEngine_IgnoreCancellation_KeepsTheGateShutWhenTheTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var engine = new FakeAsrEngine { IgnoreCancellation = true, Gate = new TaskCompletionSource() };

        var pending = engine.RecognizeAsync(new float[1], cts.Token);
        await engine.Parked.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await cts.CancelAsync();

        // 「关停后才落地」那组测试全靠这一点：取消打不开闸门，放行之后照常交出结果。
        Assert.False(pending.IsCompleted);
        Assert.False(engine.WasCancelledMidFlight);

        engine.Gate.SetResult();

        Assert.Equal("识别结果", await pending);
    }

    [Fact]
    public async Task FakeAsrEngine_CompleteOnNewThread_FinishesOnAnotherThread()
    {
        var engine = new FakeAsrEngine { CompleteOnNewThread = true };
        var caller = Environment.CurrentManagedThreadId;

        var text = await engine.RecognizeAsync(new float[1], CancellationToken.None);

        // 调用方线程此刻仍活着，新线程的编号不可能与它相同。
        Assert.Equal("识别结果", text);
        Assert.NotEqual(caller, Assert.Single(engine.CompletionThreadIds));
    }

    [Fact]
    public void FakeAudioFileDecoder_ServesCappedBlocks_ThenZeroForever()
    {
        var decoder = new FakeAudioFileDecoder([0f, 1, 2, 3, 4, 5, 6, 7, 8, 9]) { MaxSamplesPerRead = 4 };
        using var audio = decoder.Open("会议.m4a");
        var buffer = new float[16];

        int[] reads = [audio.Read(buffer), audio.Read(buffer), audio.Read(buffer), audio.Read(buffer), audio.Read(buffer)];

        Assert.Equal([4, 4, 2, 0, 0], reads);
        Assert.Equal([8f, 9f], buffer[..2]);
        Assert.Equal(TimeSpan.FromMilliseconds(10 / 16.0), audio.Duration);
        Assert.Equal("会议.m4a", decoder.OpenedPath);
        Assert.Throws<ArgumentException>(() => audio.Read(Span<float>.Empty));
    }

    [Fact]
    public void FakeAudioFileDecoder_ThrowsOnTheConfiguredRead_AndRecordsEveryCallsThread()
    {
        var boom = new InvalidOperationException("boom");
        var decoder = new FakeAudioFileDecoder(new float[100])
        {
            MaxSamplesPerRead = 10, ThrowOnReadNumber = 2, ReadException = boom,
        };

        var audio = decoder.Open("x.m4a");
        audio.Read(new float[50]);
        var thrown = Assert.Throws<InvalidOperationException>(() => audio.Read(new float[50]));
        audio.Dispose();

        Assert.Same(boom, thrown);
        Assert.Equal(1, decoder.DisposeCount);
        Assert.Equal(
            [Environment.CurrentManagedThreadId, Environment.CurrentManagedThreadId,
             Environment.CurrentManagedThreadId, Environment.CurrentManagedThreadId],
            decoder.ThreadIds);
    }

    [Fact]
    public void FakeAudioFileDecoder_OpenException_IsThrownAfterOnOpenRuns()
    {
        var boom = new IOException("打不开");
        var sawOpen = false;
        var decoder = new FakeAudioFileDecoder([]) { OpenException = boom, OnOpen = () => sawOpen = true };

        Assert.Same(boom, Assert.Throws<IOException>(() => decoder.Open("x.m4a")));
        Assert.True(sawOpen);
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
