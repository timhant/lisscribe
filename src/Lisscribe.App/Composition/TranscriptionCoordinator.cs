using Lisscribe.Core.Diagnostics;
using Lisscribe.Core.Session;
using Lisscribe.Core.Transcription;
using Microsoft.Extensions.Logging;

namespace Lisscribe.App.Composition;

/// <summary>
/// 托盘「转写音频文件…」那条链路：选文件 → 向状态机申请 → 定目录与文件名 → 转写 → 收尾。
/// 设计见 docs/plans/2026-10-28-file-transcription-design.md §4.4–§4.6。
/// </summary>
/// <remarks>
/// <para>
/// 依赖全部从构造参数进来，选文件、转写、打开结果、通知、托盘都是委托：组合根立不起测试，规矩只能住在这里。
/// </para>
/// <para>
/// <b>收尾不许依赖 UI 线程。</b>退出时 <c>App</c> 的 <c>ShutdownRequested</c> 处理器在 UI 线程上同步等
/// <see cref="CancelAndWaitAsync"/>，收尾若有一步要回 UI 线程，退出就永远卡住。故选文件之后的每个 await 都
/// <c>ConfigureAwait(false)</c>，进度直转状态机而不经 <see cref="Progress{T}"/>，生产传进来的通知与托盘回调
/// 也只投递不等待。
/// </para>
/// </remarks>
public sealed class TranscriptionCoordinator
{
    private readonly SessionController _controller;

    private readonly Func<Task<string?>> _pickSourceFile;

    private readonly Func<string, string, IProgress<int>, CancellationToken, Task<TranscriptionOutcome>> _transcribe;

    private readonly Func<string?> _readConfiguredOutputDir;

    private readonly string _baseDirectory;

    private readonly Action<string> _openResult;

    private readonly Action<string> _showNotice;

    private readonly Action<bool> _setTrayTranscribing;

    private readonly TimeProvider _time;

    private readonly ILogger<TranscriptionCoordinator> _logger;

    /// <summary>护住 <see cref="_run"/> 以及它身上的两个标志。点击来自 UI 线程，收尾跑在线程池上。</summary>
    private readonly Lock _gate = new();

    /// <summary>当前这一次，从点下去到收尾完毕；没有时为 null。</summary>
    private Run? _run;

    /// <param name="controller">裁决能不能开始，并显示进度与结局。</param>
    /// <param name="pickSourceFile">弹选择文件对话框，返回选中的本地路径，取消返回 null。</param>
    /// <param name="transcribe">执行转写，生产是 <see cref="FileTranscriber.RunAsync"/>。</param>
    /// <param name="readConfiguredOutputDir">现读配置里的保存位置：改了立即生效，不用启动时那份（设计 §4.7）。</param>
    /// <param name="baseDirectory">程序目录，默认保存位置与相对路径都以它为准。</param>
    /// <param name="openResult">用系统默认程序打开转写结果。</param>
    /// <param name="showNotice">悬浮条红条通知，生产是 <see cref="AppWiring.ShowNotice"/>。</param>
    /// <param name="setTrayTranscribing">切换托盘那一项的文案，生产投到 UI 线程再调。</param>
    /// <param name="time">取本地时间作文件名时间戳，并计耗时。</param>
    public TranscriptionCoordinator(
        SessionController controller,
        Func<Task<string?>> pickSourceFile,
        Func<string, string, IProgress<int>, CancellationToken, Task<TranscriptionOutcome>> transcribe,
        Func<string?> readConfiguredOutputDir,
        string baseDirectory,
        Action<string> openResult,
        Action<string> showNotice,
        Action<bool> setTrayTranscribing,
        TimeProvider time,
        ILogger<TranscriptionCoordinator> logger)
    {
        _controller = controller;
        _pickSourceFile = pickSourceFile;
        _transcribe = transcribe;
        _readConfiguredOutputDir = readConfiguredOutputDir;
        _baseDirectory = baseDirectory;
        _openResult = openResult;
        _showNotice = showNotice;
        _setTrayTranscribing = setTrayTranscribing;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// 托盘那一项被点了。空闲时开始一次，选文件期间忽略（免得连点弹出两个对话框），转写中则取消且不等它停下。
    /// 返回的任务在这一次整段收尾后完成，永不抛出；忽略与取消时立即完成。
    /// </summary>
    public Task ToggleAsync()
    {
        Run run;
        var startNew = false;

        lock (_gate)
        {
            if (_run is null)
            {
                _run = new Run();
                startNew = true;
            }
            else if (!_run.Begun)
            {
                _logger.LogDebug("选择文件对话框还开着，忽略这次点击");

                return Task.CompletedTask;
            }

            run = _run;
        }

        if (startNew)
        {
            return ExecuteAsync(run);
        }

        // 只发令牌不等待：本方法跑在 UI 线程上，转写要识别完当前这一段才停，结局由 ExecuteAsync 收。
        _logger.LogInformation("用户取消转写");
        run.Cancellation.Cancel();

        return Task.CompletedTask;
    }

    /// <summary>
    /// 取消在途转写并等它整段收尾，供退出与重启用：之后才轮得到释放引擎。没有在途转写时立即完成。
    /// 选文件对话框还开着时不等用户关它，立即完成，对话框晚些关上也不再开始转写。
    /// </summary>
    public Task CancelAndWaitAsync()
    {
        Run? run;
        bool begun;

        lock (_gate)
        {
            run = _run;

            if (run is null)
            {
                return Task.CompletedTask;
            }

            begun = run.Begun;

            // 与 ExecuteAsync 里置 Begun 的那一步同在锁内：两者谁先谁后都只有一种结局。
            if (!begun)
            {
                run.Abandoned = true;
            }
        }

        run.Cancellation.Cancel();

        if (!begun)
        {
            _logger.LogInformation("退出时选择文件对话框还开着，选中的文件不再转写");

            return Task.CompletedTask;
        }

        _logger.LogInformation("取消在途转写，等它收尾");

        return run.Finished.Task;
    }

    /// <summary>一次点击的整段：选文件、申请、转写。任何异常都在这里收住，托盘回调与退出路径都不许被它带走。</summary>
    private async Task ExecuteAsync(Run run)
    {
        try
        {
            string? sourcePath;

            try
            {
                // 选文件那一步留在 UI 线程上，此后的续体一律到线程池：不回 UI 线程见类注释，
                // 不在 UI 线程上接着跑是因为下面建目录、探测可写都是磁盘 IO，设到断开的网络盘上会卡住界面。
                sourcePath = await _pickSourceFile().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "选择文件对话框打不开");
                _showNotice($"无法打开选择文件对话框：{ex.Message}");

                return;
            }

            if (sourcePath is null)
            {
                _logger.LogDebug("用户在选择文件对话框里取消");

                return;
            }

            lock (_gate)
            {
                if (run.Abandoned)
                {
                    return;
                }

                // 先于 TryBeginTranscription 置上：一旦状态机准了，退出就必须等这一次收尾。
                run.Begun = true;
            }

            if (!_controller.TryBeginTranscription(out var refusal))
            {
                _showNotice(refusal ?? "现在不能开始转写");

                return;
            }

            await TranscribeAsync(run, sourcePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 走到这里的只会是通知或托盘回调自己抛了：转写那段的异常 TranscribeAsync 已收住。
            _logger.LogError(ex, "转写流程意外中断");
        }
        finally
        {
            lock (_gate)
            {
                _run = null;
            }

            run.Finished.TrySetResult();
        }
    }

    /// <summary>
    /// 状态机已准之后的那段。每条路都必须落到 <see cref="SessionController.EndTranscription"/>，
    /// 漏一条，状态就停在转写中，热键从此不录音，直到重启。
    /// </summary>
    private async Task TranscribeAsync(Run run, string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var startedAt = _time.GetTimestamp();
        var trayShowsCancel = false;

        try
        {
            var target = PrepareOutputDirectory();

            if (target.Failure is not null)
            {
                _logger.LogWarning("不开始转写 {File}：{Reason}", fileName, target.Failure);
                _controller.EndTranscription(target.Failure);

                return;
            }

            var outputPath = TranscriptFileNamer.ChoosePath(
                target.Directory!, sourcePath, _time.GetLocalNow().DateTime, File.Exists);

            _logger.LogInformation("开始转写 {File}，输出到 {Output}", fileName, outputPath);

            _setTrayTranscribing(true);
            trayShowsCancel = true;

            TranscriptionOutcome outcome;

            try
            {
                outcome = await _transcribe(
                        sourcePath, outputPath, new DirectProgress(_controller.ReportTranscriptionProgress),
                        run.Cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
            {
                // 只认自己的令牌：别处冒出来的取消落到下面的 catch，按失败报。
                _logger.LogInformation("转写 {File} 已取消，耗时 {Elapsed}", fileName, _time.GetElapsedTime(startedAt));
                _controller.EndTranscription(null);

                return;
            }

            if (!outcome.IsSuccess)
            {
                _logger.LogWarning(
                    "转写 {File} 失败：{Reason}，耗时 {Elapsed}",
                    fileName, outcome.FailureMessage, _time.GetElapsedTime(startedAt));
                _controller.EndTranscription(outcome.FailureMessage);

                return;
            }

            _logger.LogInformation(
                "转写 {File} 完成：{Output}，耗时 {Elapsed}",
                fileName, outcome.OutputPath, _time.GetElapsedTime(startedAt));

            // 先回静止态再开文件、再说话：通知若在前，会被紧随其后的状态迁移顶掉。
            _controller.EndTranscription(null);

            Deliver(outcome.OutputPath!, target.FallbackNotice);
        }
        catch (Exception ex)
        {
            // 例如中途写盘失败：转写器已删掉 .part，IOException 直接抛到这里。
            _logger.LogError(ex, "转写 {File} 中断，耗时 {Elapsed}", fileName, _time.GetElapsedTime(startedAt));
            _controller.EndTranscription($"转写失败：{ex.Message}");
        }
        finally
        {
            if (trayShowsCancel)
            {
                _setTrayTranscribing(false);
            }
        }
    }

    /// <summary>打开转写结果；打不开不改结局，只告诉用户文件在哪。两句话都有时并成一条，接连两条后一条会当场顶掉前一条。</summary>
    private void Deliver(string outputPath, string? fallbackNotice)
    {
        string? openFailure = null;

        try
        {
            _openResult(outputPath);
        }
        catch (Exception ex)
        {
            // 最常见的是没有程序关联 .txt。
            _logger.LogError(ex, "转写结果打不开：{Output}", outputPath);
            openFailure = $"转写完成，但无法打开结果：{outputPath}";
        }

        var notice = string.Join("。", new[] { fallbackNotice, openFailure }.OfType<string>());

        if (notice.Length > 0)
        {
            _showNotice(notice);
        }
    }

    /// <summary>
    /// 定保存目录（设计 §4.4）：设定的目录建不了或写不进就回落到默认的 transcripts，并记下要告诉用户的话；
    /// 默认目录也不行就不开始。设定值本来就空时没有回落这回事。
    /// </summary>
    private OutputTarget PrepareOutputDirectory()
    {
        var configured = _readConfiguredOutputDir();
        var defaultDirectory = TranscriptFileNamer.ResolveDirectory(null, _baseDirectory);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredReport = PrepareConfiguredDirectory(configured);

            if (configuredReport.IsWritable)
            {
                return new OutputTarget(configuredReport.Directory, null, null);
            }

            _logger.LogWarning(
                "设定的保存位置不可用：{Directory}（{Reason}），改存到默认目录",
                configuredReport.Directory, configuredReport.FailureReason);
        }

        var defaultReport = PrepareDirectory(defaultDirectory);

        if (!defaultReport.IsWritable)
        {
            return new OutputTarget(null, null, $"无法写入转写结果：{defaultReport.FailureReason}");
        }

        var fallbackNotice = string.IsNullOrWhiteSpace(configured)
            ? null
            : $"设定的保存位置不可用，已改存到：{defaultDirectory}";

        return new OutputTarget(defaultDirectory, fallbackNotice, null);
    }

    /// <summary>设定值按程序目录展开再建、再探测；展开本身失败（路径非法）同样算不可用。</summary>
    private DirectoryWritabilityReport PrepareConfiguredDirectory(string configured)
    {
        string directory;

        try
        {
            directory = TranscriptFileNamer.ResolveDirectory(configured, _baseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DirectoryWritabilityReport(configured, $"路径无效：{ex.Message}");
        }

        return PrepareDirectory(directory);
    }

    /// <summary>先建目录再探测可写：探测器对不存在的目录直接判失败。建不了（例如同名的是个文件）即不可写。</summary>
    private static DirectoryWritabilityReport PrepareDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return new DirectoryWritabilityReport(directory, $"无法创建目录：{ex.Message}");
        }

        return DirectoryWritabilityProbe.Probe(directory);
    }

    /// <summary>定好的保存目录、回落时要说的话、或不能开始的原因，三者只有前两个或最后一个有值。</summary>
    private sealed record OutputTarget(string? Directory, string? FallbackNotice, string? Failure);

    /// <summary>
    /// 进度当场转给状态机。不用 <see cref="Progress{T}"/>：它把每条投到捕获的同步上下文，退出时 UI 线程被收尾占着，投过去的永远到不了。
    /// </summary>
    private sealed class DirectProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>一次点击从开始到收尾的全部状态。两个标志只在 <see cref="_gate"/> 内读写。</summary>
    private sealed class Run
    {
        /// <summary>不 Dispose：取消可能与收尾并发，释放后再 Cancel 会抛；不带计时器的令牌源不握非托管资源。</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>整段收尾完毕时完成。续体异步跑，等的人不会接着在收尾线程上往下执行。</summary>
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>已向状态机申请开始：此后点击即取消，退出要等这一次收尾。</summary>
        public bool Begun { get; set; }

        /// <summary>选文件期间退出或重启已开始：对话框晚些关上，选中的文件也不再转写。</summary>
        public bool Abandoned { get; set; }
    }
}
