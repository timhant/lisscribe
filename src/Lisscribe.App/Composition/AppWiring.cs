using Lisscribe.App.Tray;
using Lisscribe.App.ViewModels;
using Lisscribe.Core.Session;

namespace Lisscribe.App.Composition;

/// <summary>
/// 把状态机、悬浮条、托盘三者接起来。只有接线，没有构造——真正 new 出平台对象的是
/// <see cref="AppServices"/>。分开是为了让这一层能在没有麦克风、没有模型、没有钩子的情况下
/// 用假件整条跑一遍。
/// </summary>
/// <remarks>
/// 本类存在的唯一理由是那条线程规矩：<see cref="SessionController.StatusChanged"/> 与
/// <see cref="SessionController.VolumeChanged"/> 在多条非 UI 线程上引发（识别工作线程、热键线程、
/// 看门狗计时器线程、会话监听线程、采集线程与设备变更的线程池线程），而
/// <see cref="OverlayViewModel"/> 一被写就会同步驱动窗口 <c>Show()</c> / <c>Hide()</c>。直接订阅的
/// 后果是每一轮会话的收尾都抛 <c>Call from invalid thread</c>，而用户看见的症状是「指示器从来
/// 不出现」或「一直亮着不消失」——都不像线程用错了。
/// <para>
/// 投递必须是 Post，不许是 Invoke：<c>Invoke</c> 同步等 UI 线程，而收尾路径要 Join 采集线程；
/// UI 线程若正等着状态机，两边互等就是挂死。故这里收的是一个「把活儿丢过去就返回」的投递器。
/// </para>
/// </remarks>
public sealed class AppWiring : IDisposable
{
    private readonly SessionController _controller;

    private readonly OverlayViewModel _overlay;

    private readonly Action<Action> _postToUi;

    private readonly TrayIconHost _tray;

    private readonly TimeProvider _time;

    private readonly TimeSpan _noticeDuration;

    /// <summary>
    /// 只护住 <see cref="_noticeTimer"/> 与 <see cref="_disposed"/> 两个字段，UI 线程上的闭包读 <see cref="_disposed"/> 也进它。
    /// </summary>
    private readonly Lock _noticeGate = new();

    private ITimer? _noticeTimer;

    private bool _disposed;

    /// <summary>
    /// 状态机最近一次报来的状态，被通知压住的那条也记。通知到点后回到它。
    /// 只在 UI 线程上读写，与所有写悬浮条的活儿同一条线程，故不进 <see cref="_noticeGate"/>。
    /// </summary>
    private (SessionState State, string? Message) _lastStatus = (SessionState.Idle, null);

    /// <summary>
    /// 本类亲手上屏、此刻仍在屏上的那条通知的文案，被状态顶掉或到点收场即清空。只在 UI 线程上读写，理由同上。
    /// </summary>
    private string? _lastNotice;

    /// <param name="postToUi">
    /// 把一段活儿投到 UI 线程上，不等它跑完。生产是 <c>Dispatcher.UIThread.Post</c>。
    /// </param>
    /// <param name="time">通知自动隐藏用的时钟。测试传 <c>FakeTimeProvider</c>，生产传系统时钟。</param>
    /// <param name="noticeDuration">
    /// 一条通知在屏上待多久。与状态机自己那条失败提示同源，两条路的观感才一致。
    /// </param>
    public AppWiring(
        SessionController controller,
        OverlayViewModel overlay,
        TrayIconHost tray,
        Action<Action> postToUi,
        TimeProvider time,
        TimeSpan noticeDuration)
    {
        _controller = controller;
        _overlay = overlay;
        _tray = tray;
        _postToUi = postToUi;
        _time = time;
        _noticeDuration = noticeDuration;
    }

    public TrayIconHost Tray => _tray;

    /// <summary>
    /// 挂上事件。幂等：重复调用不会造成重复订阅——先退再订。这不是洁癖，「每次 Show 都重订一次」
    /// 那条无上界的重复订阅就是这么来的。
    /// </summary>
    public void Connect()
    {
        _controller.StatusChanged -= OnStatusChanged;
        _controller.VolumeChanged -= OnVolumeChanged;
        _controller.RecordingCountdown -= OnCountdown;

        _controller.StatusChanged += OnStatusChanged;
        _controller.VolumeChanged += OnVolumeChanged;
        _controller.RecordingCountdown += OnCountdown;
    }

    public void Disconnect()
    {
        _controller.StatusChanged -= OnStatusChanged;
        _controller.VolumeChanged -= OnVolumeChanged;
        _controller.RecordingCountdown -= OnCountdown;
    }

    /// <summary>
    /// 拆事件，掐掉在途的通知计时器，并立下 <see cref="_disposed"/>：之后的通知不再投递，已排上队的闭包跑起来也不再写悬浮条。
    /// 后两件少不得：收尾之后 Avalonia 就要拆平台层，写悬浮条会同步驱动 <c>Window.Show()</c> / <c>Hide()</c>，而退订管不到已投出去的闭包。
    /// </summary>
    public void Dispose()
    {
        Disconnect();

        lock (_noticeGate)
        {
            _disposed = true;

            CancelNoticeTimerLocked();
        }
    }

    /// <summary>调用方须已持有 <see cref="_noticeGate"/>。</summary>
    private void CancelNoticeTimerLocked()
    {
        _noticeTimer?.Dispose();
        _noticeTimer = null;
    }

    /// <summary>
    /// 拿悬浮条当报警口。托盘没有气泡通知 API，这是唯一的去处。报的是组合根自己的事
    /// （模型加载失败、配置损坏、热键装不上），与状态机无关。
    /// </summary>
    /// <remarks>
    /// 自动隐藏必须在这里自己做：这条路绕开状态机，也就绕开了 <c>EnterFailed</c> 里那个失败提示
    /// 计时器；少了这一段，启动时模型没下好就等于「开机以后右下角永远糊着一块红」，直到用户碰巧
    /// 按一次热键把它顶掉。
    /// 到点了才判该不该收场，且照那一刻的实际状态判：判据是「悬浮条还在显示我这条」——期间用户
    /// 按热键开了录音，或者又来了别的提示，都不该被这条迟到的计时器打回去。判断跑在投递之后的
    /// 闭包里，也就是跑在 UI 线程上，与所有其它写悬浮条的活儿同一条线程，故不需要再为读状态加锁。
    /// <para>
    /// 收场回到状态机最近一次报来的状态，不一律回 Idle：转写一跑几分钟，期间弹的通知（例如关配置窗时
    /// 热键重装失败）若到点回 Idle，悬浮条就此隐去，而转写还在跑，进度从此看不见。通知期间被压住的
    /// 进度也记着，故回到的是最新那条百分比。还没收到过任何状态时那一对是 (Idle, null)，与从前一样隐去。
    /// </para>
    /// </remarks>
    public void ShowNotice(string message)
    {
        lock (_noticeGate)
        {
            // 先查再投：收尾之后 Avalonia 要拆平台层，投过去的活儿会同步驱动窗口显隐。
            if (_disposed)
            {
                return;
            }

            _postToUi(() =>
            {
                // 收尾可能落在投递之后、执行之前。
                if (IsDisposed())
                {
                    return;
                }

                _lastNotice = message;

                _overlay.ApplyState(SessionState.Failed, message);
            });

            // 先掐上一条。别以为下面那道 Message 相等的判断顶得了这件事：两条文案不同时它确实顶得了，
            // 而文案相同时（同一句「识别引擎加载失败：…」重复出现是常事）第一条的计时器一到点就把第
            // 二条抹掉，第二条实际待多久取决于两条之间隔了多久，短则几十毫秒。
            CancelNoticeTimerLocked();

            _noticeTimer = _time.CreateTimer(
                _ => _postToUi(() =>
                {
                    // 到点之后、执行之前收了尾的话，掐计时器管不到这个已投出去的闭包。
                    if (IsDisposed())
                    {
                        return;
                    }

                    // 比的是本条的文案而不是 _lastNotice：已投递出去的上一条计时器若晚到，不许把新通知当成自己的收场。
                    if (IsShowingNotice(message))
                    {
                        // 清空也只在收场时做：晚到的旧计时器不许抹掉新通知的记录。
                        _lastNotice = null;

                        _overlay.ApplyState(_lastStatus.State, _lastStatus.Message);
                    }
                }),
                state: null,
                dueTime: _noticeDuration,
                period: Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>收过尾没有。投递出去的闭包在 UI 线程上读它，故进锁。</summary>
    private bool IsDisposed()
    {
        lock (_noticeGate)
        {
            return _disposed;
        }
    }

    /// <summary>
    /// 悬浮条此刻显示的是不是这条通知。只在 UI 线程上调。
    /// 文案那半少不得：只看 Failed 的话，状态机自己的红条（例如转写失败）也被当成通知，其后的进度就被压住。
    /// </summary>
    private bool IsShowingNotice(string notice) =>
        _overlay.State == SessionState.Failed && _overlay.Message == notice;

    /// <summary>
    /// 收尾之前，状态机的每一次迁移都先记下，再决定上不上屏。只压住「转写中」：进度每识别完一段就可能来一条
    /// （SenseVoice 一段不到半秒），照旧上屏的话通知一闪就没了。其余迁移照旧顶掉通知——开始录音、
    /// 状态机自己的失败、转写结束，都是用户此刻更该看见的事。
    /// </summary>
    private void OnStatusChanged(object? sender, SessionStatusEventArgs e) =>
        _postToUi(() =>
        {
            // 退订管不到已排上队的闭包：收尾若落在投递之后、执行之前，既不上屏也不必再记。
            if (IsDisposed())
            {
                return;
            }

            // 先记后判：被压住的那条也得记下，通知到点后回到的正是它。
            _lastStatus = (e.State, e.Message);

            if (e.State == SessionState.Transcribing && _lastNotice is not null && IsShowingNotice(_lastNotice))
            {
                return;
            }

            // 通知被顶掉了，记录随之作废：留着的话，状态机日后一条一字不差的红条会被认成它，其后的进度全被压住。
            _lastNotice = null;

            _overlay.ApplyState(e.State, e.Message);
        });

    private void OnVolumeChanged(object? sender, float volume) =>
        _postToUi(() =>
        {
            // 收尾可能落在投递之后、执行之前。
            if (IsDisposed())
            {
                return;
            }

            _overlay.Volume = volume;
        });

    /// <summary>
    /// 录音快到上限时的倒数。在看门狗计时器线程上引发，与另外两条同一条禁令。
    /// 清空由 <c>OverlayViewModel.ApplyState</c> 顺手做掉，这里不必发「结束」。
    /// </summary>
    private void OnCountdown(object? sender, int seconds) =>
        _postToUi(() =>
        {
            // 收尾可能落在投递之后、执行之前。
            if (IsDisposed())
            {
                return;
            }

            _overlay.RemainingSeconds = seconds;
        });
}
