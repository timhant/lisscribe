using System.Buffers.Binary;
using Lisscribe.Core.Abstractions;
using NAudio.Wave;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 真调 Media Foundation 的解码测试：现场合成信号、编成文件、再解回来。仓库里不放任何音频文件。
/// </summary>
/// <remarks>
/// 每个声道放一个频率与幅度都不同的正弦，解码后逐个频率量幅度：等权平均应得「原幅度 ÷ 声道数」，
/// 只取第 0 声道、只除以 2、直接求和都会落到别的数上。期望值全部由本类的常量算出，不取自解码器。
/// </remarks>
[Trait("Category", "RequiresMediaFoundation")]
public sealed class MediaFoundationAudioFileDecoderTests : IDisposable
{
    /// <summary>合成信号的时长。</summary>
    private const double SignalSeconds = 3.0;

    /// <summary>识别引擎要的采样率，独立写在这里，不取被测代码的常量。</summary>
    private const int ExpectedRate = 16000;

    /// <summary>时长误差上限（计划任务 9 定的 100ms）。</summary>
    private const double DurationToleranceSeconds = 0.1;

    /// <summary>单个频率幅度的相对误差上限，实测各格式均在 2% 以内。</summary>
    private const double AmplitudeRelativeTolerance = 0.05;

    /// <summary>第 c 个声道用第 c 个音：频率互不相同且都是整赫兹，1 秒窗内各自恰好整周期。</summary>
    private static readonly (int Hz, double Amplitude)[] ChannelTones =
    [
        (440, 0.6),
        (1000, 0.2),
        (700, 0.4),
        (1300, 0.8),
    ];

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lisscribe-decoder-tests-" + Guid.NewGuid().ToString("N"));

    public MediaFoundationAudioFileDecoderTests(ITestOutputHelper output)
    {
        _output = output;

        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不该盖住测试本身的结论。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// m4a 一行对应三星手机录音（AAC 48kHz 双声道）。mp3 编码器若在本机缺失，<c>EncodeToMp3</c> 自己会抛
    /// 「No suitable MP3 encoders available」，这一行明确变红，不跳过。
    /// </summary>
    [Theory]
    [InlineData("m4a", 48000, 2)]
    [InlineData("mp3", 44100, 2)]
    [InlineData("wav", 48000, 2)]
    [InlineData("wav", 48000, 4)]
    [InlineData("wav", 16000, 1)]
    public void Open_DecodesToMono16kHz_AveragingAllChannelsEqually(string extension, int sampleRate, int channels)
    {
        var path = WriteSignal(_dir, extension, sampleRate, channels);

        using var audio = new MediaFoundationAudioFileDecoder().Open(path);

        var samples = ReadAll(audio, batch: 4000);

        _output.WriteLine(
            $"{extension} {sampleRate}Hz {channels}ch：Duration={audio.Duration.TotalSeconds:F4}s，"
            + $"解出 {samples.Length / (double)ExpectedRate:F4}s，"
            + $"Duration − 解出 = {(audio.Duration.TotalSeconds - samples.Length / (double)ExpectedRate) * 1000:F1}ms");

        Assert.InRange(audio.Duration.TotalSeconds, SignalSeconds - DurationToleranceSeconds, SignalSeconds + DurationToleranceSeconds);

        AssertIsTheAveragedSignal(samples, channels);
    }

    /// <summary>
    /// 返回 0 即文件结束，故读得再碎也不许提前给 0：一次读 1 个与一次读 4096 个，必须逐样本相同。
    /// </summary>
    [Fact]
    public void Read_OneSampleAtATime_GivesExactlyTheSamplesOfLargeBatches()
    {
        var path = WriteSignal(_dir, "m4a", 48000, 2);

        float[] large;
        using (var audio = new MediaFoundationAudioFileDecoder().Open(path))
        {
            large = ReadAll(audio, batch: 4096);
        }

        float[] single;
        using (var audio = new MediaFoundationAudioFileDecoder().Open(path))
        {
            single = ReadAll(audio, batch: 1);
        }

        // 阳性对照：确实读到了整段，否则两边都空也相等。
        Assert.InRange(large.Length / (double)ExpectedRate, SignalSeconds - DurationToleranceSeconds, SignalSeconds + DurationToleranceSeconds);

        Assert.Equal(large, single);
    }

    /// <summary>空缓冲读回的 0 会被调用方当成文件结束，故当场拒绝。</summary>
    [Fact]
    public void Read_WithAnEmptyBuffer_Throws()
    {
        var path = WriteSignal(_dir, "wav", 48000, 2);

        using var audio = new MediaFoundationAudioFileDecoder().Open(path);

        Assert.Throws<ArgumentException>(() => audio.Read(Span<float>.Empty));
    }

    [Fact]
    public void Open_ATextFileRenamedToM4a_ThrowsAMessageForTheUser()
    {
        var path = Path.Combine(_dir, "不是音频.m4a");

        File.WriteAllText(path, "This is plain text, not audio. 这是一段文字。");

        var error = Assert.Throws<IOException>(() => new MediaFoundationAudioFileDecoder().Open(path));

        Assert.StartsWith("无法解码该文件：不是音频.m4a（", error.Message, StringComparison.Ordinal);
        Assert.Contains("不是系统能识别的音频格式", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("若是 Windows N 版本，请安装 Media Feature Pack。", error.Message, StringComparison.Ordinal);

        // 原始异常留给日志。
        Assert.NotNull(error.InnerException);
    }

    /// <summary>文件不在时直说找不到，不提 Media Feature Pack——那会把人引到错的方向上。</summary>
    [Fact]
    public void Open_AMissingFile_SaysItCannotBeFound()
    {
        var path = Path.Combine(_dir, "已删除的录音.m4a");

        var error = Assert.Throws<FileNotFoundException>(() => new MediaFoundationAudioFileDecoder().Open(path));

        Assert.Equal("找不到该文件：已删除的录音.m4a。", error.Message);
        Assert.Equal(path, error.FileName);
    }

    /// <summary>本项目在中文路径上栽过，这一条钉住目录与文件名都带中文和空格时照常解码。</summary>
    [Fact]
    public void Open_UnderAChinesePath_Decodes()
    {
        var dir = Path.Combine(_dir, "中文 录音目录");

        Directory.CreateDirectory(dir);

        var path = WriteSignal(dir, "m4a", 48000, 2);
        var renamed = Path.Combine(dir, "会议 录音一.m4a");

        File.Move(path, renamed);

        using var audio = new MediaFoundationAudioFileDecoder().Open(renamed);

        AssertIsTheAveragedSignal(ReadAll(audio, batch: 4000), channels: 2);
    }

    /// <summary>
    /// 组合根多半在 UI 线程（STA）上打开、在线程池（MTA）上读。打开的线程像 UI 线程一样一直活着，
    /// 但不泵消息：读的那一侧若还在用 STA 上建的 COM 对象，这里会失败或卡住。
    /// </summary>
    [Fact]
    public async Task OpenedOnAnStaThread_IsReadToTheEndOnAThreadPoolThread()
    {
        var path = WriteSignal(_dir, "m4a", 48000, 2);

        IDecodedAudio? audio = null;
        Exception? openError = null;
        var openApartment = ApartmentState.Unknown;

        using var opened = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();

        var uiLike = new Thread(() =>
        {
            openApartment = Thread.CurrentThread.GetApartmentState();

            try
            {
                audio = new MediaFoundationAudioFileDecoder().Open(path);
            }
            catch (Exception ex)
            {
                openError = ex;
            }

            opened.Set();

            finished.Wait(Patience);
        })
        {
            IsBackground = true,
        };

        uiLike.SetApartmentState(ApartmentState.STA);
        uiLike.Start();

        try
        {
            Assert.True(opened.Wait(Patience, TestContext.Current.CancellationToken), "打开文件没在期限内完成");
            Assert.Null(openError);

            // 阳性对照：两侧真在不同套间，否则这条测的就不是跨套间。
            Assert.Equal(ApartmentState.STA, openApartment);

            var (readApartment, samples) = await Task.Run(() =>
            {
                using (audio!)
                {
                    return (Thread.CurrentThread.GetApartmentState(), ReadAll(audio!, batch: 4000));
                }
            }).WaitAsync(Patience, TestContext.Current.CancellationToken);

            Assert.Equal(ApartmentState.MTA, readApartment);

            AssertIsTheAveragedSignal(samples, channels: 2);
        }
        finally
        {
            finished.Set();
        }
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow_AndReadAfterwardsThrows()
    {
        var path = WriteSignal(_dir, "m4a", 48000, 2);

        var audio = new MediaFoundationAudioFileDecoder().Open(path);
        var buffer = new float[4000];

        // 先读一次，让读线程上的那个 COM reader 真建出来，Dispose 才有东西可放。
        Assert.True(audio.Read(buffer) > 0);

        audio.Dispose();
        audio.Dispose();

        Assert.Throws<ObjectDisposedException>(() => audio.Read(buffer));
    }

    // ---------- 工具 ----------

    /// <summary>按 <see cref="ChannelTones"/> 合成 16 位 PCM 并写成指定格式的文件。</summary>
    private static string WriteSignal(string dir, string extension, int sampleRate, int channels)
    {
        var path = Path.Combine(dir, $"signal-{sampleRate}-{channels}ch.{extension}");
        var format = new WaveFormat(sampleRate, 16, channels);

        using var source = new RawSourceWaveStream(new MemoryStream(Synthesize(sampleRate, channels)), format);

        switch (extension)
        {
            case "m4a":
                MediaFoundationEncoder.EncodeToAac(source, path);
                break;

            case "mp3":
                MediaFoundationEncoder.EncodeToMp3(source, path);
                break;

            case "wav":
                WaveFileWriter.CreateWaveFile(path, source);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(extension), extension, "测试只合成 m4a / mp3 / wav");
        }

        return path;
    }

    private static byte[] Synthesize(int sampleRate, int channels)
    {
        var frames = (int)(sampleRate * SignalSeconds);
        var pcm = new byte[frames * channels * sizeof(short)];

        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                var (hz, amplitude) = ChannelTones[c];
                var value = amplitude * Math.Sin(2 * Math.PI * hz * i / sampleRate);

                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan((i * channels + c) * sizeof(short)), (short)Math.Round(value * short.MaxValue));
            }
        }

        return pcm;
    }

    private static float[] ReadAll(IDecodedAudio audio, int batch)
    {
        var all = new List<float>();
        var buffer = new float[batch];

        int read;
        while ((read = audio.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read));
        }

        return [.. all];
    }

    /// <summary>总长对得上原信号，且每个声道的音都以「原幅度 ÷ 声道数」出现在 16kHz 单声道输出里。</summary>
    private static void AssertIsTheAveragedSignal(float[] samples, int channels)
    {
        Assert.InRange(samples.Length / (double)ExpectedRate, SignalSeconds - DurationToleranceSeconds, SignalSeconds + DurationToleranceSeconds);

        for (var c = 0; c < channels; c++)
        {
            var (hz, amplitude) = ChannelTones[c];
            var expected = amplitude / channels;

            // 量 [1s, 2s) 这一秒，避开编码器在首尾加的延迟与淡入淡出。
            var measured = ToneAmplitude(samples.AsSpan(ExpectedRate, ExpectedRate), hz);

            Assert.True(
                Math.Abs(measured - expected) <= expected * AmplitudeRelativeTolerance,
                $"{hz}Hz 的幅度应为 {expected:F4}（声道 {c} 的 {amplitude} ÷ {channels}），实测 {measured:F4}");
        }
    }

    /// <summary>单频点 DFT：窗长 1 秒、频率为整赫兹时各音恰好整周期，互不泄漏。</summary>
    private static double ToneAmplitude(ReadOnlySpan<float> window, int hz)
    {
        double re = 0;
        double im = 0;

        for (var i = 0; i < window.Length; i++)
        {
            var phase = 2 * Math.PI * hz * i / ExpectedRate;

            re += window[i] * Math.Cos(phase);
            im += window[i] * Math.Sin(phase);
        }

        return 2 * Math.Sqrt(re * re + im * im) / window.Length;
    }
}
