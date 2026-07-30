using System.Buffers.Binary;
using NAudio.Wave;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 「原生字节 + 设备格式 → 16kHz 单声道 float」这条链。
///
/// 录音里最容易错、错了又最没有现场线索的三件事——字节怎么解释、两个声道怎么合、重采样比例
/// 对不对——全是纯数据变换，故整条从 COM 管道里剥出来用已知波形精确验；剩下的
/// <see cref="Lisscribe.Platform.WasapiAudioCapture"/> 只是开设备、收尾次序与事件转发。
/// </summary>
public sealed class CaptureSamplePipelineTests
{
    /// <summary>本机实测的采集混音格式：IeeeFloat / 32bit / 48000Hz / 2ch。</summary>
    private static WaveFormat MeasuredDeviceFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    [Fact]
    public void Constructor_WithTheFormatThisMachineActuallyGives_IsAccepted()
    {
        var pipeline = new CaptureSamplePipeline(MeasuredDeviceFormat);

        Assert.Equal(48000, pipeline.SourceSampleRate);
        Assert.Equal(2, pipeline.SourceChannels);
    }

    /// <summary>
    /// 不认得的格式必须**当场明确失败**，绝不能按 float32 硬解。
    /// 硬解的后果是静默产出垃圾波形：不崩、不报错，识别结果全是乱码或空，现场毫无线索。
    /// </summary>
    [Theory]
    [InlineData(48000, 24, 2)] // 24bit PCM
    [InlineData(48000, 8, 1)] // 8bit PCM
    [InlineData(48000, 32, 2)] // 32bit 整数 PCM（不是 float）
    public void Constructor_WithAFormatWeCannotDecode_ThrowsInsteadOfGuessing(
        int sampleRate, int bits, int channels)
    {
        var format = new WaveFormat(sampleRate, bits, channels);

        var error = Assert.Throws<NotSupportedException>(() => new CaptureSamplePipeline(format));

        // 消息里必须带上到底是什么格式，否则报障时不知道该加哪一支解码。
        // 断言挑「位深 + bit」而非裸数字：8bit 那行里裸的 "8" 在 "48000" 里也出现，那种断言是空的。
        Assert.Contains($"{bits}bit", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「float 但位宽不是 32」这一支必须单独有覆盖：拒绝理由若全是 <c>Pcm</c> 格式，
    /// 去掉构造函数里的 <c>BitsPerSample: 32</c>（任意位宽 float 照收）不会有任何测试转红。
    ///
    /// 两个样本都真实可能出现：64bit IEEE float 是 WAVE 规范里的格式；<c>Extensible</c> 则是
    /// NAudio <c>ToStandardWaveFormat()</c> 在 SubFormat 既非 IEEE_FLOAT-32 也非 PCM 时原样返回
    /// 的那一支，即「这台机器给了个没见过的 SubFormat」时的真实形状。
    /// </summary>
    [Fact]
    public void Constructor_WithSixtyFourBitFloat_ThrowsInsteadOfTreatingItAsFloat32()
    {
        var format = WaveFormat.CreateCustomFormat(
            WaveFormatEncoding.IeeeFloat, 48000, 2, 48000 * 16, blockAlign: 16, bitsPerSample: 64);

        var error = Assert.Throws<NotSupportedException>(() => new CaptureSamplePipeline(format));

        Assert.Contains("64bit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithAnUnrecognisedExtensibleSubFormat_Throws()
    {
        var format = new WaveFormatExtensible(48000, 32, 2);

        Assert.Equal(WaveFormatEncoding.Extensible, format.Encoding);
        Assert.Throws<NotSupportedException>(() => new CaptureSamplePipeline(format));
    }

    /// <summary>
    /// 16kHz 单声道进 = 直通（<see cref="Lisscribe.Core.Audio.AudioResampler"/> 此时不做任何加工），
    /// 于是这条测试量到的就是**字节解释本身**，没有滤波器掺和。
    /// </summary>
    [Fact]
    public void Process_Float32Bytes_DecodesEachFourBytesAsOneSample()
    {
        var pipeline = new CaptureSamplePipeline(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        float[] expected = [0f, 1f, -1f, 0.25f, -0.75f];
        var bytes = new byte[expected.Length * 4];
        for (var i = 0; i < expected.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), expected[i]);
        }

        var actual = pipeline.Process(bytes);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// PCM16 这一支没在真设备上取到过样本（本机引擎恒给 float32），故正确性全靠这条逐位对拍。
    /// 取样点挑四个边界：零、正负半程、正负满程。
    /// </summary>
    [Fact]
    public void Process_Pcm16Bytes_ScalesEachShortByOneOver32768()
    {
        var pipeline = new CaptureSamplePipeline(new WaveFormat(16000, 16, 1));
        short[] source = [0, 16384, -16384, short.MaxValue, short.MinValue];
        float[] expected = [0f, 0.5f, -0.5f, 32767f / 32768f, -1f];
        var bytes = new byte[source.Length * 2];
        for (var i = 0; i < source.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), source[i]);
        }

        var actual = pipeline.Process(bytes);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 契约本身：48kHz 立体声进，16kHz 单声道出。一秒输入必须换来一秒输出，误差 1% 以内
    /// ——判据放宽到几千的量级就把「比例算错 30%」放过去了，而比例错的后果是识别端拿到变调音频。
    ///
    /// 这条同时是「声道有没有被合并」的判据：把交错流当单声道喂进去，样本数会翻倍到 32000。
    /// </summary>
    [Fact]
    public void Process_From48kStereo_YieldsExactlyOneThirdAsManyMonoSamples()
    {
        var pipeline = new CaptureSamplePipeline(MeasuredDeviceFormat);
        const int sourceFrames = 48000; // 1 秒
        var bytes = BuildFloat32Stereo(sourceFrames, _ => 0.1f, _ => 0.1f);

        var produced = pipeline.Process(bytes).Length + pipeline.Flush().Length;

        Assert.InRange(produced, (int)(16000 * 0.99), (int)(16000 * 1.01));
    }

    [Fact]
    public void Process_From44100Stereo_YieldsTheRatioThatSampleRateImplies()
    {
        var pipeline = new CaptureSamplePipeline(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
        const int sourceFrames = 44100; // 1 秒
        var bytes = BuildFloat32Stereo(sourceFrames, _ => 0.1f, _ => 0.1f);

        var produced = pipeline.Process(bytes).Length + pipeline.Flush().Length;

        Assert.InRange(produced, (int)(16000 * 0.99), (int)(16000 * 1.01));
    }

    /// <summary>
    /// 左右不同值，输出必须是两者的平均：只取左声道得 0.8，只取右声道得 -0.4，都被这条挡住。
    ///
    /// 它单独挡不住「根本没合并声道」——那种写法经抗混叠低通后同样收敛到 0.2，
    /// 挡住那种的是上面那条样本数测试。两条合起来才是完整判据。
    /// </summary>
    [Fact]
    public void Process_From48kStereo_AveragesTheTwoChannelsRatherThanPickingOne()
    {
        var pipeline = new CaptureSamplePipeline(MeasuredDeviceFormat);
        var bytes = BuildFloat32Stereo(48000, _ => 0.8f, _ => -0.4f);

        var output = pipeline.Process(bytes);

        // 跳过首尾各 2000 个样本：重采样滤波器有起振与收尾过渡，稳态才是判据。
        var steady = output.AsSpan(2000, output.Length - 4000);
        foreach (var sample in steady)
        {
            Assert.InRange(sample, 0.19f, 0.21f);
        }
    }

    /// <summary>
    /// 波形判据：喂一段 200Hz 正弦，输出必须还是 200Hz、幅度不变。
    ///
    /// 预言机是过零点计数与 RMS，两者都由本测试按信号学自己算出，不碰被测代码的任何中间量。
    /// 比例错了频率必偏（48k 当 24k 解就成 400Hz），字节解错了波形就是噪声，两种错法都拦得住，
    /// 且不依赖重采样器的群延迟。
    /// </summary>
    [Fact]
    public void Process_A200HzSine_ComesOutStillAt200HzWithTheSameAmplitude()
    {
        const int sourceRate = 48000;
        const double frequency = 200.0;
        const float amplitude = 0.5f;
        var pipeline = new CaptureSamplePipeline(WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 2));
        var bytes = BuildFloat32Stereo(
            sourceRate,
            i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sourceRate)),
            i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sourceRate)));

        var output = pipeline.Process(bytes);

        var steady = output.AsSpan(800, output.Length - 1600).ToArray();
        var seconds = steady.Length / 16000.0;

        var crossings = 0;
        for (var i = 1; i < steady.Length; i++)
        {
            if ((steady[i - 1] < 0f && steady[i] >= 0f) || (steady[i - 1] >= 0f && steady[i] < 0f))
            {
                crossings++;
            }
        }

        var measuredHz = crossings / 2.0 / seconds;
        Assert.InRange(measuredHz, frequency * 0.98, frequency * 1.02);

        var rms = Math.Sqrt(steady.Sum(s => (double)s * s) / steady.Length);
        var expectedRms = amplitude / Math.Sqrt(2);
        Assert.InRange(rms, expectedRms * 0.95, expectedRms * 1.05);
    }

    /// <summary>
    /// PCM16 复用缓冲的长度必须按这一包算，不能按缓冲区自身的长度算：后一包比前一包短时，
    /// 会把上一包的尾巴当成真音频再放一遍。故必须连送两包、且第二包更短。
    ///
    /// 用 16kHz 单声道（直通，重采样器不加工），于是输出可以逐个精确对拍。
    /// </summary>
    [Fact]
    public void Process_TwoPcm16PacketsOfDifferentSizes_DoesNotReplayTheFirstPacketsTail()
    {
        var pipeline = new CaptureSamplePipeline(new WaveFormat(16000, 16, 1));

        var first = pipeline.Process(Pcm16Bytes([1000, 2000, 3000, 4000, 5000, 6000]));
        var second = pipeline.Process(Pcm16Bytes([-1000, -2000]));

        Assert.Equal(6, first.Length);
        Assert.Equal(2, second.Length);
        Assert.Equal([-1000f / 32768f, -2000f / 32768f], second);
    }

    /// <summary>
    /// 半个帧不许进重采样器。
    ///
    /// <para>
    /// <b>这条守卫在当前 NAudio 实现下没有可观测效果，任何测试都杀不死移除它的变异</b>：
    /// <c>MemoryMarshal.Cast</c> 本就丢掉不足一个样本的尾字节，真能漏进去的只有「整数个 sample
    /// 但非整数个 frame」，而 <c>StereoToMonoSampleProvider.Read</c> 返回
    /// <c>sourceSamplesRead / 2</c>、落单那个样本就地丢掉不跨包留存。它守的是
    /// <c>AudioResampler.Process</c> 的入参契约（交错排列的 float32 样本），
    /// 而契约由别人的实现兜住是运气不是设计。
    /// </para>
    /// <para>
    /// 故本条只钉当下这个可观测事实：残帧不会凭空多出样本。它不是那条守卫的回归钉子。
    /// </para>
    /// </summary>
    [Fact]
    public void Process_WithATrailingPartialFrame_DoesNotProduceAnExtraSample()
    {
        var pipeline = new CaptureSamplePipeline(WaveFormat.CreateIeeeFloatWaveFormat(16000, 2));
        // 3 个完整帧（每帧 8 字节）再加 5 个野字节。
        var bytes = new byte[3 * 8 + 5];
        for (var i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), 0.5f);
        }

        var output = pipeline.Process(bytes);

        // 3 帧立体声 → 3 个单声道样本（16kHz 进 16kHz 出，只做声道合并）。
        Assert.Equal(3, output.Length);
        Assert.All(output, s => Assert.Equal(0.5f, s, 0.0001f));
    }

    [Fact]
    public void Process_WithNoBytes_ReturnsNothing()
    {
        var pipeline = new CaptureSamplePipeline(MeasuredDeviceFormat);

        Assert.Empty(pipeline.Process(ReadOnlySpan<byte>.Empty));
    }

    /// <summary>
    /// <see cref="Lisscribe.Platform.CaptureSamplePipeline.Flush"/> 只许交出残留，
    /// 不许把交过的再交一遍——凭空多出的样本会被当成真实音频接在句尾。
    ///
    /// <b>强度说清楚：<c>Flush()</c> 在这条链上实测恒空</b>，故下面两个 flush 读数恒为 0、
    /// 本条实际只重复验了一遍总量区间。真需要它的一天是 <c>AudioResampler.Flush</c> 改成会吐
    /// 残留那天——契约允许非空，只是当前实现不会。
    /// </summary>
    [Fact]
    public void Flush_CalledTwice_DoesNotManufactureExtraSamples()
    {
        var pipeline = new CaptureSamplePipeline(MeasuredDeviceFormat);
        var bytes = BuildFloat32Stereo(48000, _ => 0.1f, _ => 0.1f);

        var streamed = pipeline.Process(bytes).Length;
        var firstFlush = pipeline.Flush().Length;
        var secondFlush = pipeline.Flush().Length;

        Assert.Empty(pipeline.Flush());
        Assert.Equal(0, secondFlush);
        Assert.InRange(streamed + firstFlush, (int)(16000 * 0.99), (int)(16000 * 1.01));
    }

    private static byte[] Pcm16Bytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        return bytes;
    }

    private static byte[] BuildFloat32Stereo(int frames, Func<int, float> left, Func<int, float> right)
    {
        var bytes = new byte[frames * 2 * sizeof(float)];
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8), left(i));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i * 8) + 4), right(i));
        }

        return bytes;
    }
}
