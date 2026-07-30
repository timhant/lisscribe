using Lisscribe.Core.Audio;
using Xunit;

namespace Lisscribe.Core.Tests.Audio;

public sealed class AudioBufferTests
{
    private const int Rate = AudioResampler.TargetSampleRate;

    [Fact]
    public void Duration_Initially_IsZero()
    {
        Assert.Equal(TimeSpan.Zero, new AudioBuffer(maxSeconds: 60).Duration);
    }

    [Fact]
    public void Append_AccumulatesDuration()
    {
        var buffer = new AudioBuffer(60);

        buffer.Append(new float[Rate]);          // 1 秒
        buffer.Append(new float[Rate / 2]);      // 0.5 秒

        Assert.Equal(1.5, buffer.Duration.TotalSeconds, tolerance: 1e-6);
    }

    [Fact]
    public void Take_ReturnsAllSamplesAndClearsBuffer()
    {
        var buffer = new AudioBuffer(60);
        buffer.Append([0.1f, 0.2f, 0.3f]);

        var taken = buffer.Take();

        Assert.Equal([0.1f, 0.2f, 0.3f], taken);
        Assert.Equal(TimeSpan.Zero, buffer.Duration);
    }

    [Fact]
    public void IsFull_BecomesTrueAtLimit()
    {
        var buffer = new AudioBuffer(maxSeconds: 2);

        buffer.Append(new float[Rate * 2]);

        Assert.True(buffer.IsFull);
    }

    [Fact]
    public void Append_BeyondLimit_TruncatesInsteadOfGrowing()
    {
        // 关键防线：即便调用方忽略了 IsFull，缓冲也绝不无限增长。
        var buffer = new AudioBuffer(maxSeconds: 2);

        buffer.Append(new float[Rate * 10]);

        Assert.Equal(2.0, buffer.Duration.TotalSeconds, tolerance: 1e-6);
        Assert.Equal(Rate * 2, buffer.Take().Length);
    }

    [Fact]
    public void Clear_DiscardsEverything()
    {
        var buffer = new AudioBuffer(60);
        buffer.Append(new float[Rate]);

        buffer.Clear();

        Assert.Equal(TimeSpan.Zero, buffer.Duration);
        Assert.Empty(buffer.Take());
    }

    [Fact]
    public void Take_OnEmptyBuffer_ReturnsEmptyArray()
    {
        Assert.Empty(new AudioBuffer(60).Take());
    }

    [Fact]
    public void Constructor_RejectsNonPositiveLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBuffer(0));
    }
}
