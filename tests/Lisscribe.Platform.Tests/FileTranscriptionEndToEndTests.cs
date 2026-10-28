using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Lisscribe.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 整条转写链路的端到端测试：真模型 + 真 Media Foundation 解码器 + 真写入器，音频现场合成、编成 m4a。
/// 仓库里不放任何音频文件。两档引擎在同一个类里，xunit 同类串行，Qwen3 的 1GB 内存不会与 SenseVoice 叠在一起。
/// </summary>
[Trait("Category", "RequiresModel")]
[Trait("Category", "RequiresMediaFoundation")]
public sealed partial class FileTranscriptionEndToEndTests : IDisposable
{
    /// <summary>参考句重复的次数：11 × 约 5.6s + 10 × 1s 静音 ≈ 71.6s，远超 20 秒上限，至少切成 4 段。</summary>
    private const int Repeats = 11;

    /// <summary>每两句之间的静音秒数。</summary>
    private const int GapSeconds = 1;

    /// <summary>MF 的 AAC 编码器只收 44.1k / 48k。</summary>
    private const int EncodeRate = 48000;

    /// <summary>参考句里确定会出现的词，手写，取自 SenseVoice 测试对 zh.wav 的期望「开放时间早上9点至下午5点」。</summary>
    private const string ExpectedWord = "开放时间";

    /// <summary>每行的格式：「[hh:mm:ss] 文字」。</summary>
    [GeneratedRegex(@"^\[(\d{2}):(\d{2}):(\d{2})\] .+$")]
    private static partial Regex LinePattern();

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lisscribe-e2e-tests-" + Guid.NewGuid().ToString("N"));

    public FileTranscriptionEndToEndTests(ITestOutputHelper output)
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

    [Fact]
    public async Task SenseVoice_TranscribesASyntheticM4a_ToAWellFormedTranscript()
    {
        var modelDir = ModelDir("sense-voice");
        var paths = AsrModelPaths.Resolve(modelDir);

        Assert.SkipUnless(paths.IsComplete && File.Exists(ReferenceWav(modelDir)), $"SenseVoice 模型不可用：{modelDir}");

        var config = new AsrSection(AsrEngines.SenseVoice, modelDir, "cpu");

        await using var engine = new SenseVoiceEngine(config, NullLogger<SenseVoiceEngine>.Instance);

        var (lines, progress) = await TranscribeAsync(engine, modelDir, AsrEngines.SenseVoice);

        // 期望词手写，不取自这次运行的输出。
        Assert.Contains(lines, line => line.Contains(ExpectedWord, StringComparison.Ordinal));

        // 进度单调不减，最后一个是 100。
        Assert.NotEmpty(progress);
        Assert.Equal(progress.OrderBy(p => p), progress);
        Assert.Equal(100, progress[^1]);
    }

    [Fact]
    public async Task Qwen3_TranscribesASyntheticM4a_ToAWellFormedTranscript()
    {
        var modelDir = ModelDir("qwen3-asr");

        Assert.SkipUnless(
            Qwen3ModelPaths.Resolve(modelDir).IsComplete && File.Exists(ReferenceWav(ModelDir("sense-voice"))),
            $"Qwen3 模型或参考音频不可用：{modelDir}");

        var config = new AsrSection(AsrEngines.Qwen3Asr, modelDir, "cpu");

        await using var engine = new Qwen3AsrEngine(config, NullLogger<Qwen3AsrEngine>.Instance);

        // Qwen3 的措辞与 SenseVoice 不同，只验结构，不验具体文字。
        await TranscribeAsync(engine, ModelDir("sense-voice"), AsrEngines.Qwen3Asr);
    }

    // ---------- 工具 ----------

    /// <summary>合成 m4a、转写、逐项验结构，返回各行与进度记录。参考音频固定取 SenseVoice 包里的 zh.wav。</summary>
    private async Task<(string[] Lines, List<int> Progress)> TranscribeAsync(
        IAsrEngine engine, string referenceModelDir, string engineName)
    {
        var source = WriteSyntheticM4a(Path.Combine(_dir, "合成会议.m4a"), ReferenceWav(referenceModelDir));

        await engine.LoadAsync(CancellationToken.None);

        var output = TranscriptFileNamer.ChoosePath(_dir, source, DateTime.Now, File.Exists);
        var progress = new RecordingProgress();

        var transcriber = new FileTranscriber(
            engine, new MediaFoundationAudioFileDecoder(),
            AsrEngines.NeedsChineseNumberItn(engineName), NullLogger<FileTranscriber>.Instance);

        var stopwatch = Stopwatch.StartNew();
        var outcome = await transcriber.RunAsync(source, output, progress, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(outcome.IsSuccess, outcome.FailureMessage);
        Assert.Equal(output, outcome.OutputPath);
        Assert.True(File.Exists(outcome.OutputPath));

        // 落地后不留 .part。
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));

        Assert.Matches(@"^合成会议-\d{14}\.txt$", Path.GetFileName(outcome.OutputPath));

        var bytes = File.ReadAllBytes(outcome.OutputPath!);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);

        // 去掉 CRLF 后不剩任何换行字符，即换行全是 CRLF。
        Assert.DoesNotContain('\n', text.Replace("\r\n", string.Empty));
        Assert.DoesNotContain('\r', text.Replace("\r\n", string.Empty));

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        _output.WriteLine($"{engineName}：{lines.Length} 行，转写耗时 {stopwatch.Elapsed.TotalSeconds:F1}s");

        Assert.True(lines.Length >= 4, $"行数应不少于 4，实为 {lines.Length}");

        var starts = new List<TimeSpan>();

        foreach (var line in lines)
        {
            var match = LinePattern().Match(line);

            Assert.True(match.Success, $"行格式不对：{line}");

            starts.Add(new TimeSpan(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)));
        }

        Assert.Equal(TimeSpan.Zero, starts[0]);

        for (var i = 1; i < starts.Count; i++)
        {
            Assert.True(starts[i] > starts[i - 1], $"第 {i + 1} 行的时间 {starts[i]} 没有晚于上一行 {starts[i - 1]}");
        }

        return (lines, progress.Values);
    }

    /// <summary>zh.wav 重复 <see cref="Repeats"/> 遍、中间各夹静音，重采样到 48kHz 后编成 m4a。</summary>
    private static string WriteSyntheticM4a(string path, string referenceWav)
    {
        byte[] pcm;
        WaveFormat format;

        using (var reader = new WaveFileReader(referenceWav))
        {
            format = reader.WaveFormat;

            Assert.Equal(16, format.BitsPerSample);
            Assert.Equal(1, format.Channels);

            using var buffer = new MemoryStream();

            reader.CopyTo(buffer);

            pcm = buffer.ToArray();
        }

        var silence = new byte[format.AverageBytesPerSecond * GapSeconds];

        using var joined = new MemoryStream();

        for (var i = 0; i < Repeats; i++)
        {
            if (i > 0)
            {
                joined.Write(silence);
            }

            joined.Write(pcm);
        }

        joined.Position = 0;

        using var source = new RawSourceWaveStream(joined, format);

        var resampled = new WdlResamplingSampleProvider(source.ToSampleProvider(), EncodeRate).ToWaveProvider16();

        MediaFoundationEncoder.EncodeToAac(resampled, path);

        return path;
    }

    private static string ReferenceWav(string senseVoiceDir) => Path.Combine(senseVoiceDir, "test_wavs", "zh.wav");

    /// <summary>模型目录：仓库根（往上找 lisscribe.sln）下的 models\&lt;name&gt;，与 Core.Tests 的探测一致。</summary>
    private static string ModelDir(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "lisscribe.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "models", name);
    }

    /// <summary>同步记录的进度：<see cref="Progress{T}"/> 会把回调丢到线程池，顺序不可信。</summary>
    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }
}
