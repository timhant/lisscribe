using Microsoft.Extensions.Logging;

namespace Lisscribe.App.Composition;

/// <summary>
/// 退出时的收尾顺序：摘托盘 → 取消在途转写并等它收尾 → 停状态机 → 依次释放其余部件。
/// 从 <see cref="AppServices"/> 里抽出来，是因为组合根立不起测试，而这条顺序错了的后果是引擎被释放时转写还在用它。
/// </summary>
/// <remarks>
/// <para>
/// 反过来做会让收尾过程中的状态变更打到已经关掉的窗口上：托盘先摘，接线与日志最后才拆。
/// 状态机自己会按定好的次序停采集、等在途识别、最后 Dispose 引擎，故转写必须在它之前停下。
/// </para>
/// <para>
/// <b>每个 await 都 <c>ConfigureAwait(false)</c>。</b>App 的 <c>ShutdownRequested</c> 处理器在 UI 线程上同步等本类跑完，
/// 续体若投回 UI 线程就永远等不到。
/// </para>
/// </remarks>
public sealed class ShutdownSequence
{
    /// <summary>
    /// 等转写收尾的上限。取消最多等当前一段识别完，Qwen3 一段 20 秒音频在本机约 4 秒，留一倍余量。
    /// 退出总共至多 10 秒等转写加 10 秒等状态机，仍在后继进程等本进程退出的 30 秒之内（Program.RestartWaitTimeout）。
    /// </summary>
    public static readonly TimeSpan TranscriptionStopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>等状态机的识别工作线程退出的上限。</summary>
    public static readonly TimeSpan ControllerShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly Action _detachTray;

    private readonly Func<TimeSpan, Task<bool>> _stopTranscription;

    private readonly Func<TimeSpan, Task> _shutdownController;

    private readonly IReadOnlyList<IDisposable> _releaseInOrder;

    private readonly ILogger<ShutdownSequence> _logger;

    /// <summary>0 = 还没开始，1 = 已开始。托盘「退出」与窗口关闭都可能走到收尾，只许跑一次。</summary>
    private int _started;

    /// <param name="detachTray">摘托盘，生产是 <c>TrayIconHost.Detach</c>。</param>
    /// <param name="stopTranscription">取消在途转写并等它收尾，到点没收尾返回 false。生产是 <see cref="TranscriptionCoordinator.CancelAndWaitAsync"/>。</param>
    /// <param name="shutdownController">停状态机，它会释放引擎。生产是 <c>SessionController.ShutdownAsync</c>。</param>
    /// <param name="releaseInOrder">其余部件，按给出的顺序逐个释放。</param>
    /// <param name="logger">记状态机关停失败。</param>
    public ShutdownSequence(
        Action detachTray,
        Func<TimeSpan, Task<bool>> stopTranscription,
        Func<TimeSpan, Task> shutdownController,
        IReadOnlyList<IDisposable> releaseInOrder,
        ILogger<ShutdownSequence> logger)
    {
        _detachTray = detachTray;
        _stopTranscription = stopTranscription;
        _shutdownController = shutdownController;
        _releaseInOrder = releaseInOrder;
        _logger = logger;
    }

    /// <summary>收尾。第二次起调用当场返回，即使第一次还没跑完。</summary>
    public async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _detachTray();

        // 转写必须先停：状态机关停会释放引擎，而转写正用着它。等不到也照常往下关，为什么安全见 CancelAndWaitAsync 的注释。
        await _stopTranscription(TranscriptionStopTimeout).ConfigureAwait(false);

        try
        {
            await _shutdownController(ControllerShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 状态机关不掉也要拆其余部件：钩子不摘，全局键盘钩子就一直挂到进程退出。
            _logger.LogError(ex, "状态机收尾失败，继续拆其余部件。");
        }

        foreach (var part in _releaseInOrder)
        {
            part.Dispose();
        }
    }
}
