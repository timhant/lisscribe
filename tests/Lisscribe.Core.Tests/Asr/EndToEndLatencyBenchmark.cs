using System.Diagnostics;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Asr;
using Lisscribe.Core.Audio;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 识别延迟的取数用具。**它量的是识别这一段**——从样本进引擎到文本出来，
/// 不含录音、不含注入。定延迟口径要用的数由它产出。
///
/// <para>
/// **两个 Trait 都要**：它既要真模型，又要跑两分钟。常规运行须一并排除
/// <c>Category=Benchmark</c>，否则每次跑测试都白等一遍。
/// </para>
///
/// <para>
/// 样本全部是**真实语音**，不拼接：低熵重复音频的 RTF 偏乐观，照它定出来的数是假的
/// （Qwen3 的长度上限也栽过这一手，见 <see cref="Qwen3ModelFixture"/>）。
/// </para>
/// </summary>
[Trait("Category", "RequiresModel")]
[Trait("Category", "Benchmark")]
[Collection(ProcessWorkingDirectoryCollection.Name)]
public sealed class EndToEndLatencyBenchmark(ITestOutputHelper output)
{
    /// <summary>定口径那一档（5 秒语音）的采样次数。P95 取第 19 个数，样本再少就没意义了。</summary>
    private const int CoreSamples = 20;

    /// <summary>其余长度只为看趋势（RTF 随时长涨），取 5 次的最大值，**不称其为 P95**。</summary>
    private const int TrendSamples = 5;

    [Fact]
    public async Task SenseVoice_HowLongRecognitionActuallyTakes()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, "更长的真实样本取自 Qwen3 包的 test_wavs");

        await using var engine = new SenseVoiceEngine(
            new AsrSection(AsrEngines.SenseVoice, ModelFixture.ModelDir, "cpu"),
            NullLogger<SenseVoiceEngine>.Instance);

        await engine.LoadAsync(TestContext.Current.CancellationToken);

        var core = await MeasureAsync(engine, "SenseVoice", ModelFixture.TestWavPath, CoreSamples);

        // 这一档上限 60s，故一路量到接近上限处。
        await MeasureAsync(engine, "SenseVoice", Qwen3WavPath("cantonese.wav"), TrendSamples);
        await MeasureAsync(engine, "SenseVoice", Qwen3WavPath("qiqiu1.wav"), TrendSamples);

        // 护栏而非验收：只拦「跑成了单线程」「误用了 fp32」这类量级退化。
        Assert.True(core.P95 < 1500, $"5.6s 语音 P95 {core.P95:F0}ms，与实测基线差了一个量级");
    }

    [Fact]
    public async Task Qwen3_HowLongRecognitionActuallyTakes()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);
        Assert.SkipUnless(ModelFixture.IsAvailable, "5 秒那条样本取自 SenseVoice 包的 test_wavs");

        await using var engine = new Qwen3AsrEngine(
            new AsrSection(AsrEngines.Qwen3Asr, Qwen3ModelFixture.ModelDir, "cpu"),
            NullLogger<Qwen3AsrEngine>.Instance);

        await engine.LoadAsync(TestContext.Current.CancellationToken);

        var core = await MeasureAsync(engine, "Qwen3Asr", ModelFixture.TestWavPath, CoreSamples);

        // **只量到 16.45s 为止。** 这一档上限 25s，再长的样本本身就会丢字，
        // 拿一条输出残缺的识别去算延迟没有意义。
        await MeasureAsync(engine, "Qwen3Asr", Qwen3WavPath("cantonese.wav"), TrendSamples);

        Assert.True(core.P95 < 6000, $"5.6s 语音 P95 {core.P95:F0}ms，与实测基线差了一个量级");
    }

    /// <summary>
    /// 同一条 16.45s 样本，**单独跑**——前面不垫那 20 遍 5.6s。
    ///
    /// <para>
    /// 与 <see cref="Qwen3_HowLongRecognitionActuallyTakes"/> 里那次只差一个条件：那次轮到它时
    /// CPU 已经连续满载约一分钟。**这条是用来分辨「差异是不是热降频造成的」的**，
    /// 须用 <c>--filter-method</c> 单独跑，跟别的一起跑就失去意义了。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Qwen3_TheLongClipWithNothingRunBeforeIt()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        await using var engine = new Qwen3AsrEngine(
            new AsrSection(AsrEngines.Qwen3Asr, Qwen3ModelFixture.ModelDir, "cpu"),
            NullLogger<Qwen3AsrEngine>.Instance);

        await engine.LoadAsync(TestContext.Current.CancellationToken);

        await MeasureAsync(engine, "Qwen3Asr(单跑)", Qwen3WavPath("cantonese.wav"), TrendSamples);
    }

    /// <summary>
    /// 跑 <paramref name="samples"/> 次并把分位数打到测试输出（须带 <c>--output Detailed</c> 才看得见）。
    /// </summary>
    private async Task<(double P50, double P95, double Max)> MeasureAsync(
        IAsrEngine engine, string tier, string wavPath, int samples)
    {
        var (audio, rate) = ModelFixture.ReadPcm16Mono(wavPath);

        // **必须校验采样率。** 引擎按 16kHz 解释递进来的样本，不看 wav 头；喂一条 44.1kHz 的
        // 进去不会报错，只是内容被当成慢 2.76 倍的音频——耗时看着合理，标注的时长却是错的。
        // 官方包里 raokouling.wav 就是 44.1kHz，第一版这里没校验，它被记成「20.76s / RTF 0.315」，
        // 而真相是 57.28s 等效数据量、RTF 0.114。
        Assert.Equal(AudioResampler.TargetSampleRate, rate);

        var seconds = audio.Length / (double)rate;

        // 预热一次且不计入：第一次推理要建 ORT 的内部缓存，明显慢于其后每一次，
        // 而用户按第一次热键时模型早已加载并（在启动自检里）跑过一遍。
        await engine.RecognizeAsync(audio, TestContext.Current.CancellationToken);

        var times = new List<double>(samples);

        for (var i = 0; i < samples; i++)
        {
            var clock = Stopwatch.StartNew();
            await engine.RecognizeAsync(audio, TestContext.Current.CancellationToken);
            clock.Stop();

            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        times.Sort();

        var p50 = times[times.Count / 2];
        var p95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
        var max = times[^1];

        output.WriteLine(
            $"{tier,-12} {Path.GetFileName(wavPath),-16} {seconds,6:F2}s  n={samples,-3} " +
            $"min={times[0],7:F0}ms  P50={p50,7:F0}ms  P95={p95,7:F0}ms  max={max,7:F0}ms  " +
            $"RTF(P50)={p50 / 1000 / seconds:F3}");

        return (p50, p95, max);
    }

    private static string Qwen3WavPath(string name) =>
        Path.Combine(Path.GetFullPath(Qwen3ModelFixture.ModelDir, AppContext.BaseDirectory), "test_wavs", name);
}
