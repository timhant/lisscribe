using NAudio.Wave;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 多声道等权平均成单声道，纯数据变换，不碰 Media Foundation，故不打 trait，没有 MF 的机器上也是一道闸门。
/// </summary>
public sealed class ChannelAveragerTests
{
    [Fact]
    public void Read_FourChannels_GivesTheEqualWeightAverageOfEachFrame()
    {
        // 期望值手算：(1+2+3+6)/4=3，(-1+1+0.5+0.5)/4=0.25，(0.8+0+0+0)/4=0.2。
        float[] interleaved =
        [
            1f, 2f, 3f, 6f,
            -1f, 1f, 0.5f, 0.5f,
            0.8f, 0f, 0f, 0f,
        ];

        var averager = new MediaFoundationAudioFileDecoder.ChannelAverager(new ArraySource(interleaved, channels: 4));
        var output = new float[8];

        var read = averager.Read(output, 0, output.Length);

        Assert.Equal(3, read);
        Assert.Equal([3f, 0.25f, 0.2f], output[..3]);
        Assert.Equal(48000, averager.WaveFormat.SampleRate);
        Assert.Equal(1, averager.WaveFormat.Channels);
    }

    /// <summary>上游一次只给零碎几个样本、还切在帧中间时，照样凑满整帧再平均，不错位。</summary>
    [Fact]
    public void Read_WhenTheSourceHandsOverPartialFrames_StillAveragesWholeFrames()
    {
        float[] interleaved =
        [
            1f, 3f,
            -2f, 0f,
            0.5f, 0.25f,
        ];

        var averager = new MediaFoundationAudioFileDecoder.ChannelAverager(
            new ArraySource(interleaved, channels: 2, maxPerRead: 3));
        var output = new float[3];

        var read = averager.Read(output, 0, output.Length);

        Assert.Equal(3, read);
        Assert.Equal([2f, -1f, 0.375f], output);
        Assert.Equal(0, averager.Read(output, 0, output.Length));
    }

    /// <summary>按交错样本数组吐数据的假上游，每次最多给 <c>maxPerRead</c> 个。</summary>
    private sealed class ArraySource(float[] samples, int channels, int maxPerRead = int.MaxValue) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, maxPerRead), samples.Length - _position);

            Array.Copy(samples, _position, buffer, offset, n);

            _position += n;

            return n;
        }
    }
}
