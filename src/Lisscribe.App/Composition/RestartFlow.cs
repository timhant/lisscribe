using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging;

namespace Lisscribe.App.Composition;

/// <summary>重启一次。成功返回 true，失败返回 false 并给出可直接示人的原因。生产是 <see cref="RestartSequence.TryRestart"/>。</summary>
public delegate bool TryRestartHandler(out string? error);

/// <summary>
/// 用户点了「立即重启」之后那一段：先取消在途转写并等它收尾，再投回 UI 线程拉起后继进程。另给出确认窗的文案。
/// 从 <see cref="AppServices"/> 里抽出来，是因为组合根立不起测试。
/// </summary>
/// <remarks>
/// 重启走的是退出那条路，退出时也会先等转写，但那时托盘已摘、悬浮条也快没了。在这里先停掉，转写收尾的状态变更还有地方显示。
/// </remarks>
public sealed class RestartFlow
{
    private readonly Func<bool> _isTranscribing;

    private readonly Func<TimeSpan, Task<bool>> _stopTranscription;

    private readonly TryRestartHandler _tryRestart;

    private readonly Action<Action> _postToUi;

    private readonly Action<string> _showNotice;

    private readonly ILogger<RestartFlow> _logger;

    /// <param name="isTranscribing">此刻有没有转写在跑，生产是 <see cref="TranscriptionCoordinator.IsTranscribing"/>。</param>
    /// <param name="stopTranscription">取消在途转写并等它收尾，到点没收尾返回 false。生产是 <see cref="TranscriptionCoordinator.CancelAndWaitAsync"/>。</param>
    /// <param name="tryRestart">拉起后继进程并请求本进程退出。</param>
    /// <param name="postToUi">把一段活儿投到 UI 线程上，不等它跑完。生产是 <c>Dispatcher.UIThread.Post</c>。</param>
    /// <param name="showNotice">悬浮条红条通知，生产是 <see cref="AppWiring.ShowNotice"/>。</param>
    /// <param name="logger">记重启失败。</param>
    public RestartFlow(
        Func<bool> isTranscribing,
        Func<TimeSpan, Task<bool>> stopTranscription,
        TryRestartHandler tryRestart,
        Action<Action> postToUi,
        Action<string> showNotice,
        ILogger<RestartFlow> logger)
    {
        _isTranscribing = isTranscribing;
        _stopTranscription = stopTranscription;
        _tryRestart = tryRestart;
        _postToUi = postToUi;
        _showNotice = showNotice;
        _logger = logger;
    }

    /// <summary>重启确认窗的文案，每次现读。有转写在跑就说明它会被取消（设计 §4.6）。</summary>
    public string ConfirmMessage => RestartPrompt.ConfirmMessageFor(_isTranscribing());

    /// <summary>用户点了「立即重启」。永不抛出：调用方是关窗回调，不 await 它。</summary>
    public async Task RunAsync()
    {
        try
        {
            // 等不到也照样重启：这一段在拉起后继进程之前，不占后继进程那 30 秒。上限与退出时同一个。
            await _stopTranscription(ShutdownSequence.TranscriptionStopTimeout).ConfigureAwait(false);

            // 投回 UI 线程再重启：请求退出要在 UI 线程上发。
            _postToUi(() =>
            {
                if (!_tryRestart(out var error))
                {
                    // 拉不起来时本进程原样活着，故这里还能报得出话来。
                    _logger.LogError("重启 lisscribe 失败：{Error}", error);
                    _showNotice($"重启失败：{error}");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重启前停止转写失败。");
            _showNotice($"重启失败：{ex.Message}");
        }
    }
}
