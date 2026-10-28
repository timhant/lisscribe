using System.ComponentModel;
using Lisscribe.App.Composition;
using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Core.Tests.Fakes;
using Lisscribe.Core.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 托盘「转写音频文件…」那条链路：选文件 → 状态机准不准 → 定目录与文件名 → 转写 → 收尾。
/// 用真的 <see cref="SessionController"/> 加假件，选文件、转写、打开结果、通知、托盘全是测试给的委托。
///
/// <para>
/// <b>先后顺序的判据是同一本账</b>：状态机的每次迁移、每条通知、托盘的每次切换、每次打开都按发生顺序记进
/// <see cref="_events"/>。「回落通知必须在 EndTranscription 之后」这类断言只看这本账里的位置，不看被测类自己记的任何东西。
/// </para>
/// </summary>
public sealed class TranscriptionCoordinatorTests : IAsyncDisposable
{
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 10, 28, 13, 30, 45, TimeSpan.Zero);

    /// <summary>东五区：与 UTC、与本机常见的东八区都不同，三种取时写法各得出不同的文件名。</summary>
    private static readonly TimeZoneInfo TestZone =
        TimeZoneInfo.CreateCustomTimeZone("lisscribe-test+05", TimeSpan.FromHours(5), "UTC+05", "UTC+05");

    /// <summary>13:30:45Z 在东五区是 18:30:45，独立算出，不经被测代码。</summary>
    private const string StampInTestZone = "20261028183045";

    /// <summary>等异步那一步的上限。被测代码坏了时让测试红在这里，而不是整个测试进程挂死。</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHotkeyHook _hook = new();

    private readonly FakeAudioCapture _capture = new();

    private readonly FakeAsrEngine _engine = new();

    private readonly FakeTimeProvider _time = new(StartedAtUtc);

    private readonly SessionController _controller;

    private readonly RecordingLogger<TranscriptionCoordinator> _logger = new();

    private readonly string _base;

    private readonly string _source;

    private readonly Lock _gate = new();

    private readonly List<string> _events = [];

    private readonly List<string> _requestedOutputs = [];

    private int _pickCount;

    private int _transcribeCount;

    /// <summary>选文件对话框的假件。默认选中 <see cref="_source"/>。</summary>
    private Func<Task<string?>> _pick;

    /// <summary>转写的假件。默认照收到的输出路径报成功。</summary>
    private Func<string, string, IProgress<int>, CancellationToken, Task<TranscriptionOutcome>> _transcribe =
        (_, output, _, _) => Task.FromResult(TranscriptionOutcome.Success(output));

    /// <summary>配置里的保存位置。默认留空。</summary>
    private Func<string?> _readConfigured = () => string.Empty;

    private Action<string> _open;

    public TranscriptionCoordinatorTests()
    {
        _engine.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        _time.SetLocalTimeZone(TestZone);

        _controller = new SessionController(
            _hook, _capture, _engine, new FakeTextInjector(), new FakeWindowProbe(), new FakeSessionMonitor(),
            SessionOptions.CreateDefault(), _time, NullLogger<SessionController>.Instance);

        _controller.StatusChanged += (_, e) => Record($"state:{e.State}:{e.Message}");

        _controller.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space"));

        _base = Path.Combine(Path.GetTempPath(), "lisscribe-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);

        // 源文件不必真存在：转写是假件，协调器只取它的文件名。
        _source = Path.Combine(_base, "录音.m4a");

        _pick = () => Task.FromResult<string?>(_source);
        _open = path => Record($"open:{path}");
    }

    public async ValueTask DisposeAsync()
    {
        await _controller.ShutdownAsync(TimeSpan.FromSeconds(2));

        Directory.Delete(_base, recursive: true);
    }

    private string DefaultDirectory => Path.Combine(_base, "transcripts");

    private string[] Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    private string[] Notices => [.. Events.Where(e => e.StartsWith("notice:", StringComparison.Ordinal))];

    private string[] TrayEvents => [.. Events.Where(e => e.StartsWith("tray:", StringComparison.Ordinal))];

    private string[] RequestedOutputs
    {
        get
        {
            lock (_gate)
            {
                return [.. _requestedOutputs];
            }
        }
    }

    [Fact]
    public async Task CancellingThePicker_ChangesNothing_AndTheNextClickOpensItAgain()
    {
        _pick = () => Task.FromResult<string?>(null);
        var coordinator = NewCoordinator();

        await coordinator.ToggleAsync();

        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal(0, _transcribeCount);

        // 状态机一次也没被碰过，通知、托盘、打开也都没有。
        Assert.Empty(Events);

        await coordinator.ToggleAsync();

        Assert.Equal(2, _pickCount);
    }

    [Fact]
    public async Task WhenTheControllerRefuses_TheRefusalIsShown_AndNothingElseMoves()
    {
        // 正在听写时状态机拒绝转写，拒绝原因原样示人。
        _hook.RaisePressed();
        Assert.Equal(SessionState.Recording, _controller.State);

        var coordinator = NewCoordinator();

        await coordinator.ToggleAsync();

        Assert.Equal(["notice:正在听写，请稍后再试"], Notices);
        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.Equal(0, _transcribeCount);
        Assert.Empty(TrayEvents);
    }

    [Fact]
    public async Task ASuccessfulRun_OpensThePathTheTranscriberReports_AndReturnsToRest()
    {
        // 转写器改名落地时会避开重名，故要打开的是它报回来的路径，不是协调器交给它的那个。
        var landed = Path.Combine(DefaultDirectory, $"录音-{StampInTestZone}-1.txt");
        _transcribe = (_, _, _, _) => Task.FromResult(TranscriptionOutcome.Success(landed));

        await NewCoordinator().ToggleAsync();

        Assert.Equal([Path.Combine(DefaultDirectory, $"录音-{StampInTestZone}.txt")], RequestedOutputs);
        Assert.Equal([$"open:{landed}"], Events.Where(e => e.StartsWith("open:", StringComparison.Ordinal)));
        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal(["tray:True", "tray:False"], TrayEvents);
        Assert.Empty(Notices);
    }

    [Fact]
    public async Task Progress_ReachesTheController_SynchronouslyWithTheReport()
    {
        // 进度必须当场转给状态机，不经同步上下文：退出时 UI 线程被收尾占着，投过去的进度永远到不了。
        string? seenRightAfterReport = null;

        _transcribe = (_, output, progress, _) =>
        {
            progress.Report(35);
            seenRightAfterReport = _controller.LastMessage;

            return Task.FromResult(TranscriptionOutcome.Success(output));
        };

        await NewCoordinator().ToggleAsync();

        Assert.Equal("转写中 35%", seenRightAfterReport);
        Assert.Contains("state:Transcribing:转写中 35%", Events);
    }

    [Fact]
    public async Task TheFileName_CarriesTheInjectedClocksLocalTime()
    {
        await NewCoordinator().ToggleAsync();

        var name = Path.GetFileName(Assert.Single(RequestedOutputs));

        Assert.Equal($"录音-{StampInTestZone}.txt", name);

        // 阴性对照：用 UTC 取时会得到 13 点那个。
        Assert.DoesNotContain("20261028133045", name);
    }

    [Fact]
    public async Task AWritableConfiguredDirectory_IsCreatedAndUsed_WithoutAnyNotice()
    {
        var configured = Path.Combine(_base, "我的 转写");
        _readConfigured = () => configured;

        await NewCoordinator().ToggleAsync();

        Assert.True(Directory.Exists(configured));
        Assert.Equal([Path.Combine(configured, $"录音-{StampInTestZone}.txt")], RequestedOutputs);
        Assert.Empty(Notices);
    }

    [Fact]
    public async Task AnUnusableConfiguredDirectory_FallsBackToTheDefault_AndSaysSoAfterTheTranscriptionEnds()
    {
        // 设定的「目录」其实是个文件：建不了，也写不进。本机任何权限下都稳定复现。
        var blocker = Path.Combine(_base, "占位文件.txt");
        File.WriteAllText(blocker, string.Empty);
        _readConfigured = () => blocker;

        await NewCoordinator().ToggleAsync();

        Assert.True(Directory.Exists(DefaultDirectory));
        Assert.Equal([Path.Combine(DefaultDirectory, $"录音-{StampInTestZone}.txt")], RequestedOutputs);

        var notice = $"notice:设定的保存位置不可用，已改存到：{DefaultDirectory}";
        Assert.Equal([notice], Notices);

        // 先回静止态再说：说在前面的话会被紧随其后的状态迁移顶掉。
        var events = Events;
        var rest = Array.FindLastIndex(events, e => e.StartsWith("state:Idle", StringComparison.Ordinal));

        Assert.True(rest >= 0, "转写没有回到静止态");
        Assert.True(Array.IndexOf(events, notice) > rest, string.Join(" | ", events));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenNoDirectoryIsUsable_TheTranscriptionFailsWithoutStarting(bool configuredIsBroken)
    {
        // 默认目录的位置被一个同名文件占着。
        File.WriteAllText(DefaultDirectory, string.Empty);

        if (configuredIsBroken)
        {
            var blocker = Path.Combine(_base, "占位文件.txt");
            File.WriteAllText(blocker, string.Empty);
            _readConfigured = () => blocker;
        }

        await NewCoordinator().ToggleAsync();

        Assert.Equal(0, _transcribeCount);
        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.StartsWith("无法写入转写结果：", _controller.LastMessage);
        Assert.Empty(TrayEvents);

        // 没有改存到任何地方，「已改存到」那句不许说。
        Assert.Empty(Notices);
    }

    [Fact]
    public async Task AFailureOutcome_ReachesTheController_AndNothingIsOpened()
    {
        _transcribe = (_, _, _, _) => Task.FromResult(TranscriptionOutcome.Failure("无法解码该文件：格式不支持"));

        await NewCoordinator().ToggleAsync();

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.Equal("无法解码该文件：格式不支持", _controller.LastMessage);
        Assert.DoesNotContain(Events, e => e.StartsWith("open:", StringComparison.Ordinal));
        Assert.Equal(["tray:True", "tray:False"], TrayEvents);
    }

    [Fact]
    public async Task CancellingFromTheTray_ReturnsAtOnce_ThenTheRunEndsQuietly()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _transcribe = async (_, output, _, ct) =>
        {
            entered.SetResult();

            // 真转写器要等当前这一段识别完才看令牌。
            await release.Task;
            ct.ThrowIfCancellationRequested();

            return TranscriptionOutcome.Success(output);
        };

        var coordinator = NewCoordinator();
        var run = coordinator.ToggleAsync();
        await entered.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var cancelClick = coordinator.ToggleAsync();

        // 点「取消转写」不等转写停下：托盘回调跑在 UI 线程上，等就是卡住界面。
        Assert.True(cancelClick.IsCompleted);
        Assert.False(run.IsCompleted);

        release.SetResult();
        await run.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.DoesNotContain(Events, e => e.StartsWith("open:", StringComparison.Ordinal));
        Assert.DoesNotContain(Events, e => e.StartsWith("state:Failed", StringComparison.Ordinal));
        Assert.Equal(["tray:True", "tray:False"], TrayEvents);
    }

    [Fact]
    public async Task AnIOExceptionEscapingTheTranscriber_EndsTheRunAsAFailure_AndTheNextRunCanStart()
    {
        // 中途写盘失败时转写器直接抛 IOException（.part 它已删掉），兜不住的话状态机永远停在转写中。
        var diskFull = new IOException("磁盘空间不足");
        _transcribe = (_, _, _, _) => Task.FromException<TranscriptionOutcome>(diskFull);

        var coordinator = NewCoordinator();
        await coordinator.ToggleAsync();

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.Equal("转写失败：磁盘空间不足", _controller.LastMessage);
        Assert.Equal(1, _logger.TimesRecorded(diskFull));
        Assert.Equal(["tray:True", "tray:False"], TrayEvents);

        _transcribe = (_, output, _, _) => Task.FromResult(TranscriptionOutcome.Success(output));
        await coordinator.ToggleAsync();

        Assert.Equal(2, _transcribeCount);
        Assert.Equal(SessionState.Idle, _controller.State);
    }

    [Fact]
    public async Task ACancellationThatIsNotOurs_CountsAsAFailure()
    {
        // 只认自己的令牌：别处冒出来的取消若当成用户取消，失败就被悄悄吞成了「什么也没发生」。
        _transcribe = (_, _, _, _) =>
            Task.FromException<TranscriptionOutcome>(new OperationCanceledException("别处的取消"));

        await NewCoordinator().ToggleAsync();

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.Equal("转写失败：别处的取消", _controller.LastMessage);
    }

    [Fact]
    public async Task AnythingThrowingAfterTheControllerSaidYes_StillEndsTheTranscription()
    {
        // 读配置、建目录、定文件名都在 TryBeginTranscription 之后，任何一处抛了都必须落到 EndTranscription，
        // 否则热键从此不录音，直到重启。
        _readConfigured = () => throw new InvalidOperationException("配置读不出");

        var coordinator = NewCoordinator();
        await coordinator.ToggleAsync();

        Assert.Equal(SessionState.Failed, _controller.State);
        Assert.Equal("转写失败：配置读不出", _controller.LastMessage);
        Assert.Equal(0, _transcribeCount);

        _readConfigured = () => string.Empty;
        await coordinator.ToggleAsync();

        Assert.Equal(1, _transcribeCount);
    }

    [Fact]
    public async Task WhenTheOpenerThrows_TheUserIsTold_AndTheRunStillCountsAsASuccess()
    {
        _open = _ => throw new Win32Exception("没有与之关联的应用");

        await NewCoordinator().ToggleAsync();

        var landed = Path.Combine(DefaultDirectory, $"录音-{StampInTestZone}.txt");
        var notice = $"notice:转写完成，但无法打开结果：{landed}";

        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal([notice], Notices);

        var events = Events;
        var rest = Array.FindLastIndex(events, e => e.StartsWith("state:Idle", StringComparison.Ordinal));
        Assert.True(Array.IndexOf(events, notice) > rest, string.Join(" | ", events));
    }

    [Fact]
    public async Task ASecondClickWhileThePickerIsOpen_IsIgnored()
    {
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pick = () => picked.Task;

        var coordinator = NewCoordinator();

        var first = coordinator.ToggleAsync();
        var second = coordinator.ToggleAsync();

        Assert.True(second.IsCompleted);
        Assert.Equal(1, _pickCount);

        picked.SetResult(null);
        await first.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Empty(TrayEvents);

        // 复位之后再点，照常弹出。
        _pick = () => Task.FromResult<string?>(null);
        await coordinator.ToggleAsync();

        Assert.Equal(2, _pickCount);
    }

    [Fact]
    public async Task WhenThePickerThrows_TheUserIsTold_AndNothingStarts()
    {
        _pick = () => throw new InvalidOperationException("对话框起不来");

        var coordinator = NewCoordinator();
        await coordinator.ToggleAsync();

        Assert.Equal(["notice:无法打开选择文件对话框：对话框起不来"], Notices);
        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal(0, _transcribeCount);

        _pick = () => Task.FromResult<string?>(null);
        await coordinator.ToggleAsync();

        Assert.Equal(2, _pickCount);
    }

    [Fact]
    public void CancelAndWait_WithNothingInFlight_CompletesAtOnce()
    {
        Assert.True(NewCoordinator().CancelAndWaitAsync().IsCompletedSuccessfully);
    }

    /// <summary>
    /// 退出与重启都靠这一条：等到的必须是整段收尾（状态机回静止、托盘切回），不是「令牌已取消」。
    /// 转写器收到取消后还要一会儿才放手（当前这一段识别完），这段时间里引擎仍在用；之后才轮得到
    /// <see cref="SessionController.ShutdownAsync"/> 去释放引擎。
    /// </summary>
    [Fact]
    public async Task CancelAndWait_WaitsForTheWholeWindDown_SoTheEngineIsReleasedOnlyAfterTheTranscriberLetGo()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? engineDisposedWhileStillInUse = null;

        _transcribe = (_, output, _, ct) => Task.Run(() =>
        {
            entered.SetResult();
            ct.WaitHandle.WaitOne(Patience);

            // 收到取消后还在识别当前这一段。
            Thread.Sleep(100);
            engineDisposedWhileStillInUse = _engine.IsDisposed;

            ct.ThrowIfCancellationRequested();

            return TranscriptionOutcome.Success(output);
        });

        var coordinator = NewCoordinator();
        var run = coordinator.ToggleAsync();
        await entered.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        await coordinator.CancelAndWaitAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        // 收尾已全部做完。
        Assert.True(run.IsCompleted);
        Assert.Equal(SessionState.Idle, _controller.State);
        Assert.Equal(["tray:True", "tray:False"], TrayEvents);

        // 关停顺序与 AppServices.DisposeAsync 相同：先等转写，再停状态机。
        await _controller.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.True(_engine.IsDisposed);
        Assert.False(engineDisposedWhileStillInUse);
    }

    [Fact]
    public async Task CancelAndWait_WhileThePickerIsOpen_ReturnsAtOnce_AndTheLatePickStartsNothing()
    {
        // 退出时不等用户关对话框；对话框晚些关上，选中的文件也不再转写。
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pick = () => picked.Task;

        var coordinator = NewCoordinator();
        var run = coordinator.ToggleAsync();

        Assert.True(coordinator.CancelAndWaitAsync().IsCompleted);

        picked.SetResult(_source);
        await run.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(0, _transcribeCount);
        Assert.DoesNotContain(Events, e => e.StartsWith("state:Transcribing", StringComparison.Ordinal));
        Assert.Empty(Notices);
    }

    [Fact]
    public async Task TheLog_NamesTheSourceAndTheOutput_AndHowItEnded()
    {
        await NewCoordinator().ToggleAsync();

        var output = Path.Combine(DefaultDirectory, $"录音-{StampInTestZone}.txt");
        var messages = _logger.Entries.Where(e => e.Level >= LogLevel.Information).Select(e => e.Message).ToArray();

        Assert.Contains(messages, m => m.Contains("录音.m4a") && m.Contains(output));
        Assert.Contains(messages, m => m.Contains("完成"));
    }

    private TranscriptionCoordinator NewCoordinator() => new(
        _controller,
        pickSourceFile: () =>
        {
            Interlocked.Increment(ref _pickCount);

            return _pick();
        },
        transcribe: (source, output, progress, ct) =>
        {
            lock (_gate)
            {
                _requestedOutputs.Add(output);
            }

            Interlocked.Increment(ref _transcribeCount);

            return _transcribe(source, output, progress, ct);
        },
        readConfiguredOutputDir: () => _readConfigured(),
        baseDirectory: _base,
        openResult: path => _open(path),
        showNotice: message => Record($"notice:{message}"),
        setTrayTranscribing: on => Record($"tray:{on}"),
        _time,
        _logger);

    private void Record(string entry)
    {
        lock (_gate)
        {
            _events.Add(entry);
        }
    }
}
