using System.Text;
using Lisscribe.Core.Tests.Fakes;
using Lisscribe.Core.Transcription;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lisscribe.Core.Tests.Transcription;

/// <summary>
/// 文件转写编排。预言机都不读被测代码自己报的偏移：覆盖靠样本里编码的序号，切点、行首时间与进度靠手算。
/// </summary>
public sealed class FileTranscriberTests : IDisposable
{
    private const int Rate = 16000;

    private const string SourcePath = @"D:\录音\会议.m4a";

    private const string SourceName = "会议.m4a";

    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-transcriber").FullName;

    private readonly FakeAsrEngine _engine = new() { RecordSamples = true };

    private readonly RecordingLogger<FileTranscriber> _logger = new();

    private readonly ProgressRecorder _progress;

    public FileTranscriberTests()
    {
        _progress = new ProgressRecorder(() => File.Exists(Output));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Output => Path.Combine(_dir, "会议-20261028213045.txt");

    private Task<TranscriptionOutcome> RunAsync(
        FakeAudioFileDecoder decoder, bool chineseNumberItn = false, string? output = null) =>
        RunCancellableAsync(decoder, TestContext.Current.CancellationToken, chineseNumberItn, output);

    private Task<TranscriptionOutcome> RunCancellableAsync(
        FakeAudioFileDecoder decoder, CancellationToken cancellationToken, bool chineseNumberItn = false,
        string? output = null) =>
        new FileTranscriber(_engine, decoder, chineseNumberItn, _logger)
            .RunAsync(SourcePath, output ?? Output, _progress, cancellationToken);

    private string[] Lines() => File.ReadAllLines(Output, Encoding.UTF8);

    /// <summary>sample[i] = i。float 在 2^24 以内表示整数是精确的，够 17 分钟。</summary>
    private static float[] Ramp(int count)
    {
        var s = new float[count];
        for (var i = 0; i < count; i++)
        {
            s[i] = i;
        }

        return s;
    }

    /// <summary>
    /// 常数 0.5，在 13.0s 处放 300ms 的 0。
    ///
    /// <para>
    /// 手算切点：第一段在 [10s, 20s] 里找最安静的 200ms 窗口。整窗全零的起点落在 [208000, 209600]，
    /// 设计 §4.2 并列取最早，故切在 208000 + 1600 = 209600（13.1s），是静音起点后 100ms，不是静音中心 210400。
    /// 之后缓冲里只剩常数 0.5，每个窗口能量相同，取最早，故每段都是 10s + 100ms = 161600 个样本：
    /// 切点依次为 13.1s、23.2s、33.3s、43.4s、53.5s、63.6s……剩余不超过 20s 时整段收尾。
    /// </para>
    /// </summary>
    private static float[] ToneWithSilenceAt13s(int seconds)
    {
        var s = new float[seconds * Rate];
        Array.Fill(s, 0.5f);
        Array.Fill(s, 0f, 13 * Rate, 4800);

        return s;
    }

    private static string Loop(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));

    private void AssertNothingLeftOnDisk() => Assert.Empty(Directory.GetFileSystemEntries(_dir));

    // ───────────── 覆盖与切段 ─────────────

    [Theory]
    [InlineData(1000)]
    [InlineData(4096 * 50)]
    [InlineData(4096 * 100)]
    public async Task EverySampleIsSentExactlyOnce_InOrder(int samplesPerRead)
    {
        // 2 分 37 秒多一点，不与任何块大小对齐。
        var total = 157 * Rate + 4321;
        var decoder = new FakeAudioFileDecoder(Ramp(total)) { MaxSamplesPerRead = samplesPerRead };

        var outcome = await RunAsync(decoder);

        Assert.True(outcome.IsSuccess, outcome.FailureMessage);
        Assert.True(_engine.RecognizeCallCount >= 8);
        Assert.Equal(Ramp(total), _engine.ReceivedSamples.SelectMany(s => s).ToArray());
    }

    [Fact]
    public async Task EverySegmentIsTenToTwentySeconds_ExceptTheLast()
    {
        // 噪声里最安静处的位置随机，切点因而各不相同。
        var random = new Random(20261028);
        var samples = new float[180 * Rate];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(random.NextDouble() - 0.5);
        }

        var decoder = new FakeAudioFileDecoder(samples) { MaxSamplesPerRead = 7777 };

        await RunAsync(decoder);

        var lengths = _engine.ReceivedSampleCounts;

        Assert.True(lengths.Count >= 9);
        Assert.All(lengths[..^1], n => Assert.InRange(n, 10 * Rate, 20 * Rate));
        Assert.InRange(lengths[^1], 1, 20 * Rate);
        Assert.Equal(samples.Length, lengths.Sum());
    }

    [Fact]
    public async Task ExactlyTwentySeconds_IsOneSegment()
    {
        var decoder = new FakeAudioFileDecoder(Ramp(20 * Rate));

        await RunAsync(decoder);

        Assert.Equal([20 * Rate], _engine.ReceivedSampleCounts);
    }

    [Fact]
    public async Task TwentySecondsAndOneSample_IsCutIntoTwo()
    {
        var decoder = new FakeAudioFileDecoder(Ramp(20 * Rate + 1));

        await RunAsync(decoder);

        Assert.Equal(2, _engine.ReceivedSampleCounts.Count);
        Assert.InRange(_engine.ReceivedSampleCounts[0], 10 * Rate, 20 * Rate);
        Assert.Equal(20 * Rate + 1, _engine.ReceivedSampleCounts.Sum());
    }

    [Fact]
    public async Task TheFirstCut_LandsInTheSilence()
    {
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45)) { MaxSamplesPerRead = 4096 };

        await RunAsync(decoder);

        // 手算见 ToneWithSilenceAt13s：13.1s。
        Assert.InRange(_engine.ReceivedSampleCounts[0], 209600 - 160, 209600 + 160);
    }

    // ───────────── 写行 ─────────────

    [Fact]
    public async Task EachLine_StartsWithItsSegmentsStartTime()
    {
        _engine.Script = n => $"第{n}段";
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(75));

        var outcome = await RunAsync(decoder);

        Assert.True(outcome.IsSuccess, outcome.FailureMessage);
        Assert.Equal(
            [
                "[00:00:00] 第1段",
                "[00:00:13] 第2段",
                "[00:00:23] 第3段",
                "[00:00:33] 第4段",
                "[00:00:43] 第5段",
                "[00:00:53] 第6段",
                "[00:01:03] 第7段",
            ],
            Lines());
    }

    [Fact]
    public async Task ABlankResult_WritesNoLine_AndTheRestAreKept()
    {
        _engine.Script = n => n switch
        {
            2 => string.Empty,
            3 => "   ",
            _ => $"第{n}段",
        };
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45));

        await RunAsync(decoder);

        Assert.Equal(["[00:00:00] 第1段", "[00:00:33] 第4段"], Lines());
    }

    [Theory]
    [InlineData(true, "[00:00:00] 增长了50%")]
    [InlineData(false, "[00:00:00] 增长了百分之五十")]
    public async Task ChineseNumberItn_FollowsTheConstructorFlag(bool enabled, string expected)
    {
        _engine.NextResult = "增长了百分之五十";
        var decoder = new FakeAudioFileDecoder(new float[5 * Rate]);

        await RunAsync(decoder, chineseNumberItn: enabled);

        Assert.Equal([expected], Lines());
    }

    [Fact]
    public async Task TheReturnedPath_IsWhereTheFileActuallyLanded()
    {
        var decoder = new FakeAudioFileDecoder(new float[5 * Rate]);

        var outcome = await RunAsync(decoder);

        Assert.True(outcome.IsSuccess, outcome.FailureMessage);
        Assert.Equal(Output, outcome.OutputPath);
        Assert.Equal([Output], Directory.GetFileSystemEntries(_dir));
    }

    // ───────────── 复读循环 ─────────────

    [Fact]
    public async Task ALoopingSegment_IsSplitInTwo_AndTheHalvesReplaceIt()
    {
        _engine.Script = n => n switch
        {
            2 => Loop("没有，", 25),
            3 => "前半",
            4 => "后半",
            _ => $"第{n}段",
        };
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45));

        await RunAsync(decoder);

        var whole = _engine.ReceivedSamples[1];
        var first = _engine.ReceivedSamples[2];
        var second = _engine.ReceivedSamples[3];

        // 两半恰好拼回原段，切点落在 [1/3, 2/3]。
        Assert.Equal(whole, first.Concat(second).ToArray());
        Assert.InRange(first.Length, whole.Length / 3, whole.Length * 2 / 3);
        Assert.Equal(6, _engine.RecognizeCallCount);

        // 后半起点：13.1s 起的那段长 161600，[1/3, 2/3] 全是常数，取最早的窗口，切在 53866 + 1600 处，即 16.57s。
        Assert.Equal(
            ["[00:00:00] 第1段", "[00:00:13] 前半", "[00:00:16] 后半", "[00:00:23] 第5段", "[00:00:33] 第6段"],
            Lines());
        Assert.Equal(0, _logger.CountOf(LogLevel.Warning));
    }

    [Fact]
    public async Task WhenTheHalvesStillLoopTogether_TheyAreWrittenAsIs_WithAWarning_AndNotSplitAgain()
    {
        // 每半各重复 12 次，单看都不到阈值，拼起来 24 次才算循环：钉住「两半拼起来再判」。
        var half = Loop("好的，", 12);
        _engine.Script = n => n switch
        {
            2 => Loop("没有，", 25),
            3 or 4 => half,
            _ => $"第{n}段",
        };
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45));

        await RunAsync(decoder);

        Assert.Equal(6, _engine.RecognizeCallCount);
        Assert.Equal(
            ["[00:00:00] 第1段", $"[00:00:13] {half}", $"[00:00:16] {half}", "[00:00:23] 第5段", "[00:00:33] 第6段"],
            Lines());
        Assert.Equal(1, _logger.CountOf(LogLevel.Warning));
    }

    [Fact]
    public async Task ALoopingSegmentTooShortToSplit_IsWrittenAsIs_WithAWarning()
    {
        // 0.3s：[1/3, 2/3] 放不下一个 200ms 窗口，切不开。
        var looping = Loop("没有，", 25);
        _engine.NextResult = looping;
        var decoder = new FakeAudioFileDecoder(new float[5000]);

        var outcome = await RunAsync(decoder);

        Assert.True(outcome.IsSuccess, outcome.FailureMessage);
        Assert.Equal(1, _engine.RecognizeCallCount);
        Assert.Equal([$"[00:00:00] {looping}"], Lines());
        Assert.Equal(1, _logger.CountOf(LogLevel.Warning));
    }

    // ───────────── 进度 ─────────────

    [Fact]
    public async Task Progress_IsFlooredPercentOfDuration_ClampedTo100_ThenAFinal100AfterCommit()
    {
        // 解出的样本比 Duration 多 0.1s，同实测。Duration = 718400 个样本。
        // 段终点 209600、371200、532800、720000 → 29、51、74、100（100.2 夹到 100），再补报一次 100。
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45))
        {
            Duration = TimeSpan.FromTicks((45 * Rate - 1600) * 625L),
        };

        await RunAsync(decoder);

        Assert.Equal([29, 51, 74, 100, 100], _progress.Reports);

        // 最后那次 100 在正式文件落地之后报。
        Assert.True(_progress.FinalFileExistedAt[^1]);
    }

    [Fact]
    public async Task Progress_NeverExceeds100_WhenTheDecoderYieldsMoreThanTheHeaderSays()
    {
        // 5s 样本、文件头报 4.9s：不夹的话会报 102。
        var decoder = new FakeAudioFileDecoder(new float[5 * Rate])
        {
            Duration = TimeSpan.FromTicks((5 * Rate - 1600) * 625L),
        };

        await RunAsync(decoder);

        Assert.Equal([100, 100], _progress.Reports);
    }

    [Fact]
    public async Task Progress_WhenDurationIsZero_StaysAtZero_ThenEndsAt100()
    {
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45)) { Duration = TimeSpan.Zero };

        var outcome = await RunAsync(decoder);

        Assert.True(outcome.IsSuccess, outcome.FailureMessage);
        Assert.Equal([0, 100], _progress.Reports);
    }

    // ───────────── 取消 ─────────────

    [Fact]
    public async Task CancelDuringTheSecondSegment_WhenTheEngineCannotBeInterrupted_StopsBeforeTheThird()
    {
        using var cts = new CancellationTokenSource();
        _engine.IgnoreCancellation = true;
        _engine.Script = n =>
        {
            if (n == 2)
            {
                cts.Cancel();
            }

            return $"第{n}段";
        };
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunCancellableAsync(decoder, cts.Token));

        Assert.Equal(2, _engine.RecognizeCallCount);
        Assert.Equal(1, decoder.DisposeCount);
        AssertNothingLeftOnDisk();
    }

    [Fact]
    public async Task CancelSurfacedByTheEngine_IsACancellation_NotARecognitionFailure()
    {
        using var cts = new CancellationTokenSource();
        _engine.Script = n =>
        {
            if (n == 2)
            {
                cts.Cancel();
            }

            return $"第{n}段";
        };
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunCancellableAsync(decoder, cts.Token));

        Assert.Equal(2, _engine.RecognizeCallCount);
        Assert.Equal(1, decoder.DisposeCount);
        AssertNothingLeftOnDisk();
    }

    // ───────────── 失败路径 ─────────────

    [Fact]
    public async Task OpenFails_ReturnsTheDecodersMessage_AndCreatesNoFile()
    {
        const string message = "无法解码该文件：会议.m4a（不是系统能识别的音频格式）。若是 Windows N 版本，请安装 Media Feature Pack。";
        var entriesAtOpen = -1;
        var decoder = new FakeAudioFileDecoder(new float[5 * Rate])
        {
            OpenException = new IOException(message),
            OnOpen = () => entriesAtOpen = Directory.GetFileSystemEntries(_dir).Length,
        };

        var outcome = await RunAsync(decoder);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(message, outcome.FailureMessage);
        Assert.Equal(SourcePath, decoder.OpenedPath);

        // 打开解码器时还没建 .part。
        Assert.Equal(0, entriesAtOpen);
        Assert.Equal(0, _engine.RecognizeCallCount);
        AssertNothingLeftOnDisk();
    }

    [Theory]
    [InlineData(1)]     // 第一次读就坏
    [InlineData(6)]     // 第一段已写进 .part 之后才坏
    public async Task ReadFails_ReturnsAFailure_LogsTheOriginal_AndLeavesNothing(int failingRead)
    {
        var original = new InvalidOperationException("Unspecified error");
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45))
        {
            MaxSamplesPerRead = 100000, ThrowOnReadNumber = failingRead, ReadException = original,
        };

        var outcome = await RunAsync(decoder);

        Assert.False(outcome.IsSuccess);
        Assert.Equal($"读取音频时中断：{SourceName}", outcome.FailureMessage);
        Assert.Equal(1, _logger.TimesRecorded(original));
        Assert.Equal(1, decoder.DisposeCount);
        AssertNothingLeftOnDisk();
    }

    [Fact]
    public async Task ReadFailsAfterTheFirstSegment_TheFirstSegmentWasRecognized()
    {
        // 上一条的前提：第 6 次读确实发生在第一段识别之后，「.part 里已有内容」那一支真被走到。
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45))
        {
            MaxSamplesPerRead = 100000, ThrowOnReadNumber = 6,
        };

        await RunAsync(decoder);

        Assert.Equal(1, _engine.RecognizeCallCount);
    }

    [Fact]
    public async Task AFileWithNoAudio_ReturnsAFailure_AndCreatesNoFile()
    {
        var decoder = new FakeAudioFileDecoder([]);

        var outcome = await RunAsync(decoder);

        Assert.False(outcome.IsSuccess);
        Assert.Equal("文件里没有可识别的音频", outcome.FailureMessage);
        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.Equal(1, decoder.DisposeCount);
        AssertNothingLeftOnDisk();
    }

    [Fact]
    public async Task EngineFails_ReturnsAFailureWithTheReason_LogsTheOriginal_AndLeavesNothing()
    {
        var original = new InvalidOperationException("模型炸了");
        _engine.Script = n => n == 2 ? throw original : $"第{n}段";
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45));

        var outcome = await RunAsync(decoder);

        Assert.False(outcome.IsSuccess);
        Assert.Equal("识别失败：模型炸了", outcome.FailureMessage);
        Assert.Equal(1, _logger.TimesRecorded(original));
        Assert.Equal(2, _engine.RecognizeCallCount);
        Assert.Equal(1, decoder.DisposeCount);
        AssertNothingLeftOnDisk();
    }

    [Fact]
    public async Task ThePartFileCannotBeCreated_ReturnsAFailureNamingTheDirectory()
    {
        var missing = Path.Combine(_dir, "不存在的目录");
        var decoder = new FakeAudioFileDecoder(new float[5 * Rate]);

        var outcome = await RunAsync(decoder, output: Path.Combine(missing, "会议-20261028213045.txt"));

        Assert.False(outcome.IsSuccess);
        Assert.Contains(missing, outcome.FailureMessage);
        Assert.Equal(0, _engine.RecognizeCallCount);
        Assert.Equal(1, decoder.DisposeCount);
        AssertNothingLeftOnDisk();
    }

    // ───────────── 线程与日志 ─────────────

    [Fact]
    public async Task OpenEveryReadAndDispose_HappenOnOneThread()
    {
        // 引擎在新线程上完成：若实现靠 await 续体往下走，后面的 Read 与 Dispose 会落到那条线程上。
        _engine.CompleteOnNewThread = true;
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45)) { MaxSamplesPerRead = 50000 };

        var outcome = await RunAsync(decoder);

        Assert.True(outcome.IsSuccess, outcome.FailureMessage);
        Assert.Equal(1, decoder.OpenCount);
        Assert.Equal(1, decoder.DisposeCount);
        Assert.True(decoder.ReadCount > 4);
        Assert.Single(decoder.ThreadIds.Distinct());

        // 阳性对照：引擎确实每次都在别的线程上完成，上面那条断言才有区分力。
        Assert.Equal(4, _engine.CompletionThreadIds.Count);
        Assert.DoesNotContain(decoder.ThreadIds[0], _engine.CompletionThreadIds);
    }

    [Fact]
    public async Task TheLog_NeverContainsTheRecognizedText()
    {
        _engine.NextResult = "机密甲乙丙";
        var decoder = new FakeAudioFileDecoder(ToneWithSilenceAt13s(45));

        await RunAsync(decoder);

        // 阳性对照：每段一条 Debug，收尾一条 Information，日志确实记了。
        Assert.Equal(4, _logger.CountOf(LogLevel.Debug));
        Assert.Equal(1, _logger.CountOf(LogLevel.Information));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("机密甲乙丙", StringComparison.Ordinal));
    }

    /// <summary>同步记下每次报告。Progress&lt;T&gt; 异步投递，断言时可能还是空的。</summary>
    private sealed class ProgressRecorder(Func<bool> finalFileExists) : IProgress<int>
    {
        public List<int> Reports { get; } = [];

        /// <summary>每次报告时正式文件在不在。</summary>
        public List<bool> FinalFileExistedAt { get; } = [];

        public void Report(int value)
        {
            Reports.Add(value);
            FinalFileExistedAt.Add(finalFileExists());
        }
    }
}
