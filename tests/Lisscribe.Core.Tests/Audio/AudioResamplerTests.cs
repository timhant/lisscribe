using Lisscribe.Core.Audio;
using Xunit;

namespace Lisscribe.Core.Tests.Audio;

public sealed class AudioResamplerTests
{
    private const int TargetRate = 16000;

    /// <summary>单点 DFT（Goertzel 同构），返回指定频率处的归一化幅度。</summary>
    private static double MagnitudeAt(float[] samples, double frequency, int sampleRate)
    {
        double re = 0, im = 0;
        for (var n = 0; n < samples.Length; n++)
        {
            var angle = 2 * Math.PI * frequency * n / sampleRate;
            re += samples[n] * Math.Cos(angle);
            im -= samples[n] * Math.Sin(angle);
        }

        return Math.Sqrt(re * re + im * im) / samples.Length;
    }

    private static float[] GenerateSine(double frequency, int sampleRate, int channels, double seconds)
    {
        var frames = (int)(sampleRate * seconds);
        var buffer = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
            for (var c = 0; c < channels; c++)
            {
                buffer[(i * channels) + c] = value;
            }
        }

        return buffer;
    }

    [Fact]
    public void Process_From48kStereo_ProducesOneThirdTheFrames()
    {
        var resampler = new AudioResampler(sourceSampleRate: 48000, sourceChannels: 2);
        var input = GenerateSine(1000, 48000, channels: 2, seconds: 1.0);

        var output = resampler.Process(input).Concat(resampler.Flush()).ToArray();

        // 1 秒 48k 立体声（96000 float）→ 1 秒 16k 单声道（约 16000 float）。
        // 重采样器有内部延迟，容许 1% 偏差。
        Assert.InRange(output.Length, 15840, 16160);
    }

    [Fact]
    public void Process_PreservesInBandTone()
    {
        var resampler = new AudioResampler(48000, 2);
        var input = GenerateSine(1000, 48000, 2, 1.0);

        var output = resampler.Process(input).Concat(resampler.Flush()).ToArray();

        var atSignal = MagnitudeAt(output, 1000, TargetRate);
        var atOther = MagnitudeAt(output, 3000, TargetRate);

        Assert.True(atSignal > 0.2, $"1kHz 分量应保留，实测幅度 {atSignal:F4}");
        Assert.True(atSignal > atOther * 20, $"1kHz({atSignal:F4}) 应远强于 3kHz({atOther:F4})");
    }

    [Fact]
    public void Process_SuppressesAliasingFromAboveNyquist()
    {
        // 关键回归测试：10kHz 超过 16kHz 采样率的 Nyquist（8kHz）。
        // 无抗混叠滤波的朴素抽取会把它折叠到 16000-10000 = 6000Hz。
        // 带滤波的重采样器应把它压下去。
        var resampler = new AudioResampler(48000, 2);
        var input = GenerateSine(10000, 48000, 2, 1.0);

        var output = resampler.Process(input).Concat(resampler.Flush()).ToArray();

        var aliasMagnitude = MagnitudeAt(output, 6000, TargetRate);

        Assert.True(aliasMagnitude < 0.05,
            $"6kHz 处的混叠分量应被滤除，实测幅度 {aliasMagnitude:F4}");
    }

    [Fact]
    public void Process_MixesStereoToMonoByAveraging()
    {
        var resampler = new AudioResampler(16000, sourceChannels: 2);
        // 左声道 +1，右声道 -1，平均后应为 0。
        var input = new float[3200];
        for (var i = 0; i < input.Length; i += 2)
        {
            input[i] = 1f;
            input[i + 1] = -1f;
        }

        var output = resampler.Process(input).Concat(resampler.Flush()).ToArray();

        Assert.All(output, sample => Assert.InRange(sample, -0.01f, 0.01f));
    }

    [Fact]
    public void Process_WhenSourceIsAlready16kMono_PassesThroughUnchanged()
    {
        var resampler = new AudioResampler(16000, sourceChannels: 1);
        var input = GenerateSine(440, 16000, 1, 0.1);

        var output = resampler.Process(input).Concat(resampler.Flush()).ToArray();

        Assert.Equal(input.Length, output.Length);
        for (var i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i], output[i], tolerance: 1e-5f);
        }
    }

    [Fact]
    public void Process_AcrossMultipleCalls_ProducesSameResultAsSingleCall()
    {
        // WASAPI 是分块回调的，分块喂入必须与整段喂入等价。
        var input = GenerateSine(1000, 48000, 2, 0.5);

        var single = new AudioResampler(48000, 2);
        var whole = single.Process(input).Concat(single.Flush()).ToArray();

        var chunked = new AudioResampler(48000, 2);
        var parts = new List<float>();
        const int chunkFloats = 4800;
        for (var offset = 0; offset < input.Length; offset += chunkFloats)
        {
            var length = Math.Min(chunkFloats, input.Length - offset);
            parts.AddRange(chunked.Process(input.AsSpan(offset, length)));
        }

        parts.AddRange(chunked.Flush());

        Assert.Equal(whole.Length, parts.Count);
    }

    [Fact]
    public void Process_WithEmptyInput_ReturnsEmpty()
    {
        var resampler = new AudioResampler(48000, 2);

        Assert.Empty(resampler.Process([]));
    }
}
