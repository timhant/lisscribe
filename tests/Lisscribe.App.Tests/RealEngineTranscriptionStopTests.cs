using System.Diagnostics;
using System.Text.RegularExpressions;
using Lisscribe.App.Composition;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Lisscribe.Core.Transcription;
using Lisscribe.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 真引擎转写到一半被取消、被退出时会怎样。取消与关停的其余测试都用假引擎，这里换成真 SenseVoice、
/// 真 Media Foundation 解码器、真 <see cref="FileTranscriber"/>、真 <see cref="SessionController"/> 与真协调器。
/// 选文件、打开结果、通知、托盘是测试给的委托，状态机的其余平台接口用 Core.Tests 的假件。
///
/// <para>
/// 音频现场合成：zh.wav 重复 <see cref="Repeats"/> 遍、中间各夹 1 秒静音，约 5.5 分钟，SenseVoice 本机约 10 秒转完，
/// 取消才落得在中途。仓库里不放任何音频文件。装配、合成与找模型的做法取自
/// Platform.Tests 的 FileTranscriptionEndToEndTests（本项目引用不到它，复制了用到的部分）。
/// </para>
/// <para>
/// 下手的时机：先等状态机报出 0 与 100 之间的进度，再等下一次进入识别，再过 <see cref="IntoDecode"/> 才取消或退出。
/// 只等进度的话，取消落在读下一段音频的时候，引擎进闸门后一看令牌就退了，原生解码一次也没被打断，
/// 「释放引擎先等原生闸门」那条路根本走不到。进入识别的信号来自 <see cref="RecognitionEntrySignal"/>，
/// 它原样转给真引擎，不替代任何执行。
/// </para>
/// <para>
/// 阳性对照那一条不取消、跑完，生成了 txt、也打开了它。没有它，另外两条的「没有 txt」证明不了任何事。
/// </para>
/// </summary>
[Trait("Category", "RequiresModel")]
[Trait("Category", "RequiresMediaFoundation")]
public sealed partial class RealEngineTranscriptionStopTests : IAsyncDisposable
{
    /// <summary>参考句重复的次数：50 × 约 5.6s + 49 × 1s 静音 ≈ 5.5 分钟，本机实测切成 25 段。</summary>
    private const int Repeats = 50;

    /// <summary>每两句之间的静音秒数。</summary>
    private const int GapSeconds = 1;

    /// <summary>MF 的 AAC 编码器只收 44.1k / 48k。</summary>
    private const int EncodeRate = 48000;

    /// <summary>参考句里确定会出现的词，手写，取自 SenseVoice 测试对 zh.wav 的期望「开放时间早上9点至下午5点」。</summary>
    private const string ExpectedWord = "开放时间";

    /// <summary>转写结果与半截文件的后缀，手写，不取自被测代码。</summary>
    private const string TranscriptSuffix = ".txt";

    private const string PartSuffix = ".part";

    /// <summary>等进度走到中途、等短文件转完的上限。</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>整段长音频转完的上限。本机约 10 秒，留足余量。</summary>
    private static readonly TimeSpan FullRunLimit = TimeSpan.FromMinutes(3);

    /// <summary>退出的时限：后继进程只等本进程退出 30 秒（Program.RestartWaitTimeout），超了重启就失败。</summary>
    private static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(30);

    /// <summary>进入识别之后再等这么久才下手：闸门内解码前的几步不到 1ms，而一段识别约 300ms，此刻原生解码正在进行。</summary>
    private static readonly TimeSpan IntoDecode = TimeSpan.FromMilliseconds(50);

    private readonly ITestOutputHelper _output;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lisscribe-real-engine-stop-" + Guid.NewGuid().ToString("N"));

    private readonly string _sourceDir;

    private readonly string _outputDir;

    private readonly Lock _gate = new();

    private readonly List<string> _opened = [];

    private readonly List<string> _notices = [];

    private readonly List<bool> _tray = [];

    private readonly List<int> _progress = [];

    /// <summary>状态机第一次报出 0 与 100 之间的进度时完成，值为那个百分比。</summary>
    private readonly TaskCompletionSource<int> _midway = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly RecordingLogger<TranscriptionCoordinator> _coordinatorLog = new();

    private readonly RecordingLogger<FileTranscriber> _transcriberLog = new();

    private readonly RecordingLogger<SessionController> _controllerLog = new();

    private readonly RecordingLogger<ShutdownSequence> _shutdownLog = new();

    private SenseVoiceEngine? _engine;

    /// <summary>包着 <see cref="_engine"/> 的那一层，状态机与转写器拿的都是它。</summary>
    private RecognitionEntrySignal? _recognition;

    private SessionController? _controller;

    /// <summary>假的选文件对话框下一次返回的路径。</summary>
    private string? _nextSource;

    public RealEngineTranscriptionStopTests(ITestOutputHelper output)
    {
        _output = output;

        _sourceDir = Path.Combine(_dir, "source");
        _outputDir = Path.Combine(_dir, "transcripts");

        Directory.CreateDirectory(_sourceDir);
    }

    public async ValueTask DisposeAsync()
    {
        // 关停状态机会释放引擎，两样都是重复调用无害。
        if (_controller is not null)
        {
            await _controller.ShutdownAsync(TimeSpan.FromSeconds(10));
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

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

    [GeneratedRegex(@"^转写中 (\d+)%$")]
    private static partial Regex ProgressPattern();

    [Fact]
    public async Task PositiveControl_AnUninterruptedRun_LandsATranscript_AndOpensIt()
    {
        var modelDir = RequireModel();
        var source = WriteSyntheticM4a(Path.Combine(_sourceDir, "合成长录音.m4a"), ReferenceWav(modelDir));
        var coordinator = await StartAsync(modelDir);

        try
        {
            _nextSource = source;

            var stopwatch = Stopwatch.StartNew();
            await coordinator.ToggleAsync().WaitAsync(FullRunLimit, TestContext.Current.CancellationToken);
            stopwatch.Stop();

            _output.WriteLine($"整段转写耗时 {stopwatch.Elapsed.TotalSeconds:F1}s，进度 {string.Join(" ", Snapshot(_progress))}");

            var landed = Assert.Single(OutputFiles(TranscriptSuffix));

            Assert.Equal([landed], Snapshot(_opened));
            Assert.Empty(OutputFiles(PartSuffix));
            Assert.Contains(ExpectedWord, File.ReadAllText(landed), StringComparison.Ordinal);

            Assert.Empty(Snapshot(_notices));
            Assert.Equal(SessionState.Idle, _controller!.State);
            Assert.Equal([true, false], Snapshot(_tray));

            // 另两条靠「进度走到中途」这个时机下手，这里先证明这个时机真的会来。
            Assert.True(_midway.Task.IsCompletedSuccessfully, "整段转写期间没有报出过 0 与 100 之间的进度");
        }
        finally
        {
            DumpLogs();
        }
    }

    [Fact]
    public async Task CancellingMidway_LeavesNoFile_ReturnsToIdle_AndTheSameEngineTranscribesAgain()
    {
        var modelDir = RequireModel();
        var source = WriteSyntheticM4a(Path.Combine(_sourceDir, "合成长录音.m4a"), ReferenceWav(modelDir));
        var coordinator = await StartAsync(modelDir);

        try
        {
            _nextSource = source;

            var run = coordinator.ToggleAsync();
            var percent = await WaitUntilDecodingMidwayAsync();

            // 托盘「取消转写」：只发令牌不等。收尾由 CancelAndWaitAsync 等，上限与退出、重启同一个。
            var stopwatch = Stopwatch.StartNew();
            await coordinator.ToggleAsync();
            var woundDown = await coordinator.CancelAndWaitAsync(ShutdownSequence.TranscriptionStopTimeout);
            stopwatch.Stop();

            _output.WriteLine($"取消时进度 {percent}%，从点下取消到收尾 {stopwatch.ElapsedMilliseconds} ms");

            Assert.True(woundDown, "转写没有在上限内收尾");
            Assert.True(run.IsCompleted);

            Assert.Empty(OutputFiles(TranscriptSuffix));
            Assert.Empty(OutputFiles(PartSuffix));
            Assert.Empty(Snapshot(_opened));
            Assert.Empty(Snapshot(_notices));
            Assert.Equal(SessionState.Idle, _controller!.State);
            Assert.False(Snapshot(_tray)[^1]);

            // 同一个引擎再转一个短文件：引擎没坏，状态机也重新受理。
            _nextSource = ReferenceWav(modelDir);

            await coordinator.ToggleAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

            var landed = Assert.Single(OutputFiles(TranscriptSuffix));

            Assert.Equal([landed], Snapshot(_opened));
            Assert.Contains(ExpectedWord, File.ReadAllText(landed), StringComparison.Ordinal);
            Assert.Equal(SessionState.Idle, _controller.State);
            Assert.Empty(Snapshot(_notices));
        }
        finally
        {
            DumpLogs();
        }
    }

    [Fact]
    public async Task ExitingMidway_ThroughTheProductionShutdownSequence_EndsInTime_LeavesNoFile_AndReleasesTheEngine()
    {
        var modelDir = RequireModel();
        var source = WriteSyntheticM4a(Path.Combine(_sourceDir, "合成长录音.m4a"), ReferenceWav(modelDir));
        var coordinator = await StartAsync(modelDir);

        try
        {
            _nextSource = source;

            var run = coordinator.ToggleAsync();
            var percent = await WaitUntilDecodingMidwayAsync();

            // 生产的退出顺序：停转写接真协调器，停状态机接真状态机（它会释放引擎），其余步骤只记账。
            var steps = new List<string>();
            var shutdown = new ShutdownSequence(
                detachTray: () => Record(steps, "tray"),
                stopTranscription: coordinator.CancelAndWaitAsync,
                shutdownController: _controller!.ShutdownAsync,
                releaseInOrder:
                [
                    new NamedPart("wiring", name => Record(steps, name)),
                    new NamedPart("hook", name => Record(steps, name)),
                    new NamedPart("monitor", name => Record(steps, name)),
                    new NamedPart("capture", name => Record(steps, name)),
                    new NamedPart("loggerFactory", name => Record(steps, name)),
                ],
                _shutdownLog);

            var stopwatch = Stopwatch.StartNew();
            await shutdown.RunAsync().WaitAsync(ExitLimit, TestContext.Current.CancellationToken);
            stopwatch.Stop();

            _output.WriteLine(
                $"退出时进度 {percent}%，退出耗时 {stopwatch.ElapsedMilliseconds} ms，此刻转写已收尾：{run.IsCompleted}");

            Assert.Equal(["tray", "wiring", "hook", "monitor", "capture", "loggerFactory"], Snapshot(steps));

            // 状态机关停没有抛：抛了会记进这本日志。
            Assert.Empty(_shutdownLog.Entries);

            Assert.True(run.IsCompleted, "退出返回时转写还没收尾");
            Assert.Empty(OutputFiles(TranscriptSuffix));
            Assert.Empty(OutputFiles(PartSuffix));
            Assert.Empty(Snapshot(_opened));
            Assert.Empty(Snapshot(_notices));

            // SessionController.ShutdownAsync 的最后一步是 Dispose 引擎。
            Assert.False(_engine!.IsLoaded);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => _engine.RecognizeAsync(new float[16000], CancellationToken.None));
        }
        finally
        {
            DumpLogs();
        }
    }

    /// <summary>装好真引擎、真状态机、真转写器与真协调器。引擎在这里加载，加载失败就让测试红在这里。</summary>
    private async Task<TranscriptionCoordinator> StartAsync(string modelDir)
    {
        _engine = new SenseVoiceEngine(
            new AsrSection(AsrEngines.SenseVoice, modelDir, "cpu"), NullLogger<SenseVoiceEngine>.Instance);

        await _engine.LoadAsync(CancellationToken.None);

        _recognition = new RecognitionEntrySignal(_engine);

        // 与生产同一口径：中文数字转换取运行中那份选项。
        var options = SessionOptions.CreateFor(AsrEngines.SenseVoice);

        _controller = new SessionController(
            new FakeHotkeyHook(), new FakeAudioCapture(), _recognition, new FakeTextInjector(), new FakeWindowProbe(),
            new FakeSessionMonitor(), options, TimeProvider.System, _controllerLog);

        _controller.StatusChanged += OnStatusChanged;
        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));

        var transcriber = new FileTranscriber(
            _recognition, new MediaFoundationAudioFileDecoder(), options.ApplyChineseNumberItn, _transcriberLog);

        return new TranscriptionCoordinator(
            _controller,
            pickSourceFile: () => Task.FromResult(_nextSource),
            transcribe: transcriber.RunAsync,
            readConfiguredOutputDir: () => _outputDir,
            baseDirectory: _dir,
            openResult: path => Record(_opened, path),
            showNotice: message => Record(_notices, message),
            setTrayTranscribing: on => Record(_tray, on),
            TimeProvider.System,
            _coordinatorLog);
    }

    /// <summary>
    /// 等到转写走到中途、且原生解码正在进行，返回那时的百分比。进入识别若抢在布防之前，等到的是再下一段，同样在中途。
    /// </summary>
    private async Task<int> WaitUntilDecodingMidwayAsync()
    {
        var percent = await _midway.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        await _recognition!.NextEntry().WaitAsync(Patience, TestContext.Current.CancellationToken);
        await Task.Delay(IntoDecode, TestContext.Current.CancellationToken);

        return percent;
    }

    /// <summary>记下状态机报出的每个百分比，第一次落在 0 与 100 之间时放行等着下手的测试。</summary>
    private void OnStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.State != SessionState.Transcribing || e.Message is null)
        {
            return;
        }

        var match = ProgressPattern().Match(e.Message);

        if (!match.Success)
        {
            return;
        }

        var percent = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        Record(_progress, percent);

        if (percent is > 0 and < 100)
        {
            _midway.TrySetResult(percent);
        }
    }

    /// <summary>模型与参考音频都在才跑，否则跳过。</summary>
    private static string RequireModel()
    {
        var modelDir = ModelDir();

        Assert.SkipUnless(
            AsrModelPaths.Resolve(modelDir).IsComplete && File.Exists(ReferenceWav(modelDir)),
            $"SenseVoice 模型或参考音频不可用：{modelDir}");

        return modelDir;
    }

    /// <summary>
    /// 模型目录：环境变量优先，与 Core.Tests 的 ModelFixture 同一口径，git worktree 里没有 models 时靠它指过去。
    /// 没设就取仓库根（往上找 lisscribe.sln）下的 models\sense-voice。
    /// </summary>
    private static string ModelDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable(AsrModelPaths.ModelDirEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "lisscribe.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "models", "sense-voice");
    }

    private static string ReferenceWav(string senseVoiceDir) => Path.Combine(senseVoiceDir, "test_wavs", "zh.wav");

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

    /// <summary>输出目录里以某后缀结尾的文件。按后缀逐个比，不依赖通配符的匹配规则。</summary>
    private string[] OutputFiles(string suffix) =>
        Directory.Exists(_outputDir)
            ? [.. Directory.GetFiles(_outputDir).Where(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))]
            : [];

    private void Record<T>(List<T> list, T item)
    {
        lock (_gate)
        {
            list.Add(item);
        }
    }

    private T[] Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }

    /// <summary>把协调器、转写器、状态机、退出顺序的日志写进测试输出，排障与写报告用。</summary>
    private void DumpLogs()
    {
        DumpLog("协调器", _coordinatorLog.Entries);
        DumpLog("转写器", _transcriberLog.Entries);
        DumpLog("状态机", _controllerLog.Entries);
        DumpLog("退出顺序", _shutdownLog.Entries);

        var files = Directory.Exists(_outputDir) ? Directory.GetFiles(_outputDir) : [];

        _output.WriteLine($"输出目录现状：{string.Join(" | ", files.Select(Path.GetFileName))}");
    }

    private void DumpLog(string name, IReadOnlyList<(LogLevel Level, string Message, Exception? Error)> entries)
    {
        foreach (var (level, message, error) in entries)
        {
            _output.WriteLine($"[{name}] {level}: {message}{(error is null ? string.Empty : $" <{error.GetType().Name}: {error.Message}>")}");
        }
    }

    /// <summary>退出顺序里只记账的那几个部件：被释放时记下自己的名字。</summary>
    private sealed class NamedPart(string name, Action<string> record) : IDisposable
    {
        public void Dispose() => record(name);
    }

    /// <summary>
    /// 真引擎外面包一层，每个成员原样转过去，只多做一件事：进入识别时放行一个布了防的等待者。
    /// 识别不经 async 再包一层，原样返回真引擎的任务，不多出一次续体跳转。
    /// </summary>
    private sealed class RecognitionEntrySignal(IAsrEngine inner) : IAsrEngine
    {
        private TaskCompletionSource? _nextEntry;

        public bool IsLoaded => inner.IsLoaded;

        public string EngineName => inner.EngineName;

        public string ModelDir => inner.ModelDir;

        /// <summary>布防：下一次进入识别时完成。</summary>
        public Task NextEntry()
        {
            var entry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Volatile.Write(ref _nextEntry, entry);

            return entry.Task;
        }

        public void RepointModelDir(string modelDir) => inner.RepointModelDir(modelDir);

        public Task LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);

        public Task<string> RecognizeAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _nextEntry, null)?.TrySetResult();

            return inner.RecognizeAsync(samples, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
