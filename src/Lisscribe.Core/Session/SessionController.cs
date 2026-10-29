using System.Threading.Channels;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Audio;
using Lisscribe.Core.Hotkey;
using Lisscribe.Core.PostProcess;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Core.Session;

/// <summary>
/// 会话状态机：录音 → 识别 → 上屏。全部依赖经接口注入，故可在无麦克风、无模型、
/// 无窗口的环境中完整单测。
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    /// <summary>钩子失效的提示。当场报与转写落地后补报共用这一句，两处各写一份迟早漂移。</summary>
    private const string HookLostMessage = "热键钩子已失效，请重启程序";

    /// <summary>暂停请求挂在会话上、随它带到落地时的告知。撤销暂停时要凭它认出这句，故只写这一份。</summary>
    private const string PausedHotkeyNotice = "已暂停热键";

    private readonly IHotkeyHook _hook;
    private readonly IAudioCapture _capture;
    private readonly IAsrEngine _engine;
    private readonly ITextInjector _injector;
    private readonly IWindowProbe _probe;
    private readonly ISessionMonitor _monitor;
    private readonly SessionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionController> _logger;

    private readonly AudioBuffer _buffer;

    private readonly Channel<RecognitionRequest> _queue =
        Channel.CreateUnbounded<RecognitionRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    private Task? _worker;
    private ITimer? _failedTimer;
    private ITimer? _watchdog;
    private nint _targetWindow;
    private bool _acceptingWork = true;
    private DateTimeOffset _recordingStartedAt;

    /// <summary>
    /// 上一次报出去的剩余秒数，用来把「每 200ms 巡检一次」压成「每秒说一句」。
    /// 每次开录都要清空：上一轮若停在「还剩 5 秒」，不清空的话下一轮那个 5 会被当成
    /// 「跟上次一样」咽掉——恰好咽掉最要紧的第一声。
    /// </summary>
    private int? _lastCountdownSecond;

    /// <summary>
    /// 暂停已被请求，但当前会话尚未落地（还在 Recording / Recognizing / Transcribing / Failed 展示中）。
    /// 落地时静止态就是 Paused 而不是 Idle——见 <see cref="EnterRest"/>。识别是异步的，
    /// 若不把这个意图带过去，用户点了「暂停」，几百毫秒后热键又活了过来。
    /// </summary>
    private bool _pauseRequested;

    /// <summary>
    /// 本次会话的附带告知（如「录音设备已变更」）。它必须跨状态迁移存活：
    /// <see cref="SetState"/> 会无条件覆盖 <see cref="LastMessage"/>，所以在收尾时直接写
    /// LastMessage 再送去识别，消息会被紧随其后的 Recognizing 迁移当场抹掉。
    /// 故告知只能挂在会话上、由每次迁移在自己无话可说时沿用。
    /// </summary>
    private string? _sessionNotice;

    /// <summary>暂停告知顶掉的那句会话告知，暂停未落地就被撤销时由 <see cref="Resume"/> 还原。</summary>
    private string? _noticeBeforePause;

    /// <summary>转写中钩子被系统摘除。状态不能离开 Transcribing，提示留到转写落地时再报。</summary>
    private bool _hookLostWhileTranscribing;

    public SessionController(
        IHotkeyHook hook,
        IAudioCapture capture,
        IAsrEngine engine,
        ITextInjector injector,
        IWindowProbe probe,
        ISessionMonitor monitor,
        SessionOptions options,
        TimeProvider time,
        ILogger<SessionController> logger)
    {
        _hook = hook;
        _capture = capture;
        _engine = engine;
        _injector = injector;
        _probe = probe;
        _monitor = monitor;
        _options = options;
        _time = time;
        _logger = logger;
        _buffer = new AudioBuffer(options.MaxRecordingSeconds);
    }

    public SessionState State { get; private set; } = SessionState.Idle;

    public string? LastMessage { get; private set; }

    /// <summary>
    /// 本事件在多条非 UI 线程上引发（识别工作线程、热键线程、看门狗计时器线程、
    /// 会话监听线程、设备变更的线程池线程、转写报进度的线程），而 UI 侧订阅者必须 <c>Dispatcher.UIThread.Post</c>。
    /// </summary>
    /// <remarks>
    /// 这条不是「最好这样」，是「不这样每一轮会话的收尾都会抛」：
    /// <c>OverlayViewModel.ApplyState</c> 会翻转 <c>IsVisible</c>，<c>OverlayWindow</c> 随即在
    /// 引发本事件的那条线程上同步调 <c>Window.Show()</c> / <c>Hide()</c>，两个方向都会抛
    /// <c>InvalidOperationException: Call from invalid thread</c>。用户看见的症状是
    /// 「指示器从来不出现」或「一直亮着不消失」，都不像「线程用错了」。
    /// 不许用 <c>Dispatcher.UIThread.Invoke</c>：它同步等 UI 线程，而收尾路径要 Join 采集线程，
    /// 那就是死锁。同一条禁令覆盖 <see cref="VolumeChanged"/>。
    /// </remarks>
    public event EventHandler<SessionStatusEventArgs>? StatusChanged;

    public event EventHandler<float>? VolumeChanged;

    /// <summary>
    /// 录音离上限只剩 <see cref="SessionOptions.CountdownThreshold"/> 之内时，每秒报一次
    /// 还剩几秒（向上取整，故最后一声是 1 而不是 0）。
    /// 报的是秒数这个事实，不是文案——界面上写成什么字归 <c>OverlayViewModel.DisplayText</c>。
    /// 与 <see cref="StatusChanged"/> 同一条禁令：在看门狗计时器线程上引发，订阅者不许 Invoke。
    /// </summary>
    public event EventHandler<int>? RecordingCountdown;

    public void Start(HotkeyCombination combination)
    {
        _hook.Pressed += OnPressed;
        _hook.Released += OnReleased;
        _hook.HookLost += OnHookLost;
        _capture.DataAvailable += OnDataAvailable;
        _capture.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _monitor.SessionLocked += OnSessionInterrupted;
        _monitor.SystemSuspending += OnSessionInterrupted;

        _worker = Task.Run(() => ProcessQueueAsync(_shutdown.Token));

        // 看门狗是唯一能把「KEYUP 永不到达」的会话捞回来的机制。
        _watchdog = _time.CreateTimer(
            _ => OnWatchdogTick(),
            state: null,
            dueTime: _options.WatchdogInterval,
            period: _options.WatchdogInterval);

        _monitor.Start();

        _hook.Start(combination);
    }

    /// <summary>
    /// 暂停热键。若正在录音，先按正常流程结束当前会话再进 Paused——不丢已录音频；
    /// 若识别在途，等它上屏完再进 Paused，绝不半路掐断一次已经说出口的话。转写在途同理。
    /// 暂停期间模型保持常驻，不卸载。
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_acceptingWork || State == SessionState.Paused || _pauseRequested)
            {
                return;
            }

            // 先立起意图，再收尾：收尾路径的终态由 EnterRest 依据它决定。
            _pauseRequested = true;

            // 键要当场还给系统，不能等暂停落地。只让本状态机不理会 Pressed 是不够的：
            // 钩子吞掉的键不到任何进程，于是配置窗录新热键时按下当前热键，界面一个事件都收不到。
            // 置在这里而不是 EnterRest：Recognizing 那条路上暂停要等识别落地才生效，
            // 而用户此刻已经在配置窗里等着按键了。
            // 拨的是钩子里一个标志，无锁无 Join——摘钩子则要 Join 事件派发线程，
            // 而那条线程正回过头来抢本方法持着的 _gate。
            _hook.PassThrough = true;

            // 录音中与在途时这两条路会用暂停告知顶掉现有告知，先记下原来那句，暂停未落地就被撤销时还原。
            _noticeBeforePause = _sessionNotice;

            if (State == SessionState.Recording)
            {
                FinishRecording(_time.GetUtcNow() - _recordingStartedAt, PausedHotkeyNotice);

                return;
            }

            if (State is SessionState.Recognizing or SessionState.Transcribing)
            {
                // 在途识别或转写自己会走到终态，那时 EnterRest 把它落到 Paused。
                _sessionNotice = PausedHotkeyNotice;

                return;
            }

            CancelFailedTimer();

            EnterRest("热键已暂停");
        }
    }

    /// <summary>恢复热键。暂停请求尚未落地时调用则等同于撤销该请求。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            // 撤放行态必须排在最前，两条返回路都要走到。漏在下面那条「撤销未落地的暂停」上，
            // 钩子从此永远放行——热键再也开不出录音，而状态显示一切正常。
            _hook.PassThrough = false;

            if (State != SessionState.Paused)
            {
                // 在途会话还没落地就又点了「继续」：撤销意图即可，状态本就不该动
                // （此刻可能正在识别，改它就是踩掉别人的进度）。
                _pauseRequested = false;

                // 热键并没有停，暂停告知不许留到落地时的提示里。只认它本身：已被别的事换掉的告知不动。
                if (_sessionNotice == PausedHotkeyNotice)
                {
                    _sessionNotice = _noticeBeforePause;
                }

                return;
            }

            _pauseRequested = false;
            _sessionNotice = null;

            SetState(SessionState.Idle, null);
        }
    }

    /// <summary>
    /// 申请开始一次音频文件转写。转写与热键听写互斥，由本状态机裁决，界面层不各自判断。
    /// 拒绝时 <paramref name="refusal"/> 是给用户看的原因，状态不动。
    /// </summary>
    public bool TryBeginTranscription(out string? refusal)
    {
        lock (_gate)
        {
            refusal = RefuseTranscription();

            if (refusal is not null)
            {
                _logger.LogInformation("不开始转写：{Reason}", refusal);

                return false;
            }

            // Failed 展示中开始：掐掉失败提示计时器，与按热键打断红条同一做法。
            CancelFailedTimer();

            // 暂停中开始：转完回到 Paused，沿用暂停请求跨异步落地的那套机制。
            if (State == SessionState.Paused)
            {
                _pauseRequested = true;
            }

            // 转写是新的一段占用，上一轮会话的告知不许带到它的终态上。
            _sessionNotice = null;

            SetState(SessionState.Transcribing, "转写中 0%");

            return true;
        }
    }

    /// <summary>调用方须已持有 _gate。不能开始转写的原因，能开始时返回 null。</summary>
    private string? RefuseTranscription()
    {
        if (!_acceptingWork)
        {
            return "程序正在退出";
        }

        if (!_engine.IsLoaded)
        {
            return "识别引擎未就绪";
        }

        if (State is SessionState.Recording or SessionState.Recognizing)
        {
            return "正在听写，请稍后再试";
        }

        if (State == SessionState.Transcribing)
        {
            return "已有转写在进行";
        }

        return null;
    }

    /// <summary>报告转写进度。只在转写中生效，迟到的一条不许把别的状态改成「转写中」。</summary>
    public void ReportTranscriptionProgress(int percent)
    {
        lock (_gate)
        {
            if (!_acceptingWork || State != SessionState.Transcribing)
            {
                return;
            }

            SetState(SessionState.Transcribing, $"转写中 {percent}%");
        }
    }

    /// <summary>
    /// 转写落地。<paramref name="failure"/> 为 null 即成功，回静止态；否则亮红条，到点再回静止态。
    /// 只在转写中生效：已取消的那次迟到的结局不许踩掉此刻的录音或红条。
    /// </summary>
    public void EndTranscription(string? failure)
    {
        lock (_gate)
        {
            // 关停后也不受理：那时再起的失败计时器已无人掐。
            if (!_acceptingWork || State != SessionState.Transcribing)
            {
                return;
            }

            // 转写中被摘除的钩子在这里补报，与转写自己的失败并列，谁也不吞掉谁。
            if (_hookLostWhileTranscribing)
            {
                _hookLostWhileTranscribing = false;

                failure = failure is null ? HookLostMessage : $"{failure}，{HookLostMessage}";
            }

            if (failure is not null)
            {
                EnterFailed(failure);

                return;
            }

            EnterRest(null);
        }
    }

    private void OnPressed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            // _pauseRequested 一并拦截：暂停请求发出后、在途会话落地前的这段时间里 State
            // 还不是 Paused，只看 State 会让用户在「已点暂停」之后仍能开出一次新录音。
            if (!_acceptingWork || State == SessionState.Paused || _pauseRequested)
            {
                return;
            }

            if (State == SessionState.Recording)
            {
                // 与 OnReleased 那道 `if (State != Recording) return;` 对称的守卫。生产环境里
                // HotkeyMatcher 会滤掉 OS 自动重复，这里没有那层上游保证——没有中间 Released
                // 的第二次 Pressed 会把已经说出口的音频通过下面的 _buffer.Clear() 直接清空。
                return;
            }

            // 转写与热键听写互斥：不开录，也不改状态，悬浮条照旧显示进度。
            // 排在引擎检查之前，免得任何一条路把 Transcribing 改成 Failed。
            if (State == SessionState.Transcribing)
            {
                _logger.LogInformation("正在转写音频文件，本次热键不开始录音");

                return;
            }

            // 引擎没加载就别开录。它可能还在后台加载（Qwen3 那四秒多里真会有人按热键），
            // 也可能加载时就失败了（模型目录不对是头号原因）。这两种情况下开录的结果一样：
            // 录一整段注定扔掉的音频，再从识别那头回一句「识别失败，详见日志」——
            // 而真正说得清的那句话（缺哪个文件、去哪找）在启动那一瞬间就闪过去了。
            if (!_engine.IsLoaded)
            {
                _logger.LogWarning("识别引擎尚未就绪，本次热键不开始录音");

                EnterFailed("识别引擎未就绪，详见日志");

                return;
            }

            // Failed 显示期间按下：打断计时，立即开录。
            CancelFailedTimer();

            // 目标窗口必须在按下瞬间锁定。若等到上屏时才取，用户在识别的 1 秒里切了窗口，
            // 文本就落进错误的应用。
            // GetForegroundWindow 是原生 P/Invoke 的包装，可能抛异常；这里是热键回调线程，
            // 托管异常逃出去是进程级后果。降级策略：记成 nint.Zero——HandleRequestAsync 那边
            // 「焦点未变」的校验会自然判定不匹配，结果转入剪贴板兜底。
            try
            {
                _targetWindow = _probe.GetForegroundWindow();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取前台窗口失败，识别结果将转入剪贴板兜底");
                _targetWindow = nint.Zero;
            }

            // **目标窗口是谁必须当场记下来。** 事后光有一个 HWND 认不出它是记事本还是别的东西，
            // 而「上屏返回 Ok、目标窗口里一个字也没有」这类故障的第一个岔路口恰恰是「打给了谁」
            // ——2026-08-08 那次真机故障就卡在这上头。不含窗口标题，见 IWindowProbe.Describe。
            _logger.LogInformation(
                "开始录音，目标窗口 0x{Window:X}（{Description}）",
                _targetWindow, DescribeSafely(_targetWindow));

            // 看门狗按 now - _recordingStartedAt 判定按住时长与最长录音时长，故起始时刻
            // 必须与 _capture.Start() 同刻记下。
            _recordingStartedAt = _time.GetUtcNow();

            // 倒数从头说起，理由见字段注释。
            _lastCountdownSecond = null;

            _sessionNotice = null;

            _buffer.Clear();

            _capture.Start();

            SetState(SessionState.Recording, null);
        }
    }

    /// <summary>
    /// 描述一个窗口，供日志用。探针的实现契约里写着「不许抛」，这里仍再兜一层——
    /// 这几个调用点全在日志语句里，为一行排障信息把状态机搞崩是本末倒置。
    /// </summary>
    private string DescribeSafely(nint window)
    {
        try
        {
            return _probe.Describe(window);
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private void OnReleased(object? sender, TimeSpan heldFor)
    {
        lock (_gate)
        {
            if (State != SessionState.Recording)
            {
                return;
            }

            // 用户正常松键，与看门狗抢救、设备变更、暂停收尾共用同一条收尾路径。
            // 短按丢弃这类规则一旦存在两份实现必然漂移，而漂移的代价是「有时丢字、有时不丢」。
            FinishRecording(heldFor, notice: null);
        }
    }

    /// <summary>
    /// 调用方须已持有 _gate。录音收尾的唯一入口：按 <paramref name="heldFor"/> 判定
    /// 短按丢弃还是送去识别。<paramref name="notice"/> 是随本次会话一路带到终态的附带告知。
    /// </summary>
    private void FinishRecording(TimeSpan heldFor, string? notice)
    {
        if (heldFor >= _options.HoldThreshold)
        {
            SubmitRecording(notice);

            return;
        }

        // 短按：整段丢弃，且不向系统补发按键。
        StopCaptureSafely();

        var samples = _buffer.Take();

        _logger.LogDebug("短按 {Held}ms，丢弃 {Count} 个样本", heldFor.TotalMilliseconds, samples.Length);

        _sessionNotice = notice;

        EnterRest(null);
    }

    /// <summary>
    /// 调用方须已持有 _gate。无条件把已录音频送去识别，不做短按判定。
    /// 供「缓冲已满」这条路径使用：已经录满上限，是不是短按根本无从谈起，而按住时长由挂钟
    /// 算出——一次系统时钟回拨就足以让整整一分钟的话被当成误触静默丢掉。
    /// </summary>
    private void SubmitRecording(string? notice)
    {
        StopCaptureSafely();

        _sessionNotice = notice;

        EnqueueOrFail(_buffer.Take());
    }

    /// <summary>调用方须已持有 _gate。</summary>
    private void EnqueueOrFail(float[] samples)
    {
        var duration = TimeSpan.FromSeconds(samples.Length / (double)AudioResampler.TargetSampleRate);

        if (duration < _options.MinAudioDuration)
        {
            // 刚开口就拔了耳机时缓冲确实不足 0.5s，但红条只写「录音过短」等于什么也没解释，
            // 故须把设备变更这条告知并进失败提示一起说。
            EnterFailed(WithNotice("录音过短"));

            return;
        }

        var request = new RecognitionRequest(samples, _targetWindow, _time.GetUtcNow());

        if (!_queue.Writer.TryWrite(request))
        {
            EnterFailed(WithNotice("识别队列已关闭"));

            return;
        }

        SetState(SessionState.Recognizing, null);
    }

    /// <summary>调用方须已持有 _gate。把会话告知拼在失败原因之前，无告知时原样返回。</summary>
    private string WithNotice(string reason) =>
        _sessionNotice is null ? reason : $"{_sessionNotice}，{reason}";

    /// <summary>
    /// 这里读 <see cref="State"/> 不加 <c>_gate</c> 是一条死锁豁免，不是疏忽。别给它加锁。
    /// </summary>
    /// <remarks>
    /// 生产环境这个回调跑在 WASAPI 的采集线程上，而收尾这条链是：热键线程持 <c>_gate</c> →
    /// <see cref="FinishRecording"/> → <see cref="StopCaptureSafely"/> → <c>_capture.Stop()</c>
    /// → <c>WasapiCapture.Dispose()</c>，最后那一步会 Join 采集线程。采集线程若在这里去抢
    /// <c>_gate</c>，两边互等——用户每次松开热键都可能挂死。
    /// 不加锁也没有窄窗口：<c>IAudioCapture.Stop()</c> 返回时采集线程已经交不出新样本了，
    /// 而收尾的最后一段音频是在本线程、<c>_gate</c> 内、<see cref="AudioBuffer.Take"/> 之前
    /// 同步送进来的，落进的正是本次会话的缓冲。
    /// <see cref="VolumeChanged"/> 同样受这条约束覆盖：它也在采集线程上同步调外部代码，
    /// UI 若在处理器里 <c>Dispatcher.Invoke</c> 就是同一形状的阻塞等待。
    /// </remarks>
    private void OnDataAvailable(object? sender, ReadOnlyMemory<float> data)
    {
        if (State != SessionState.Recording)
        {
            return;
        }

        _buffer.Append(data.Span);

        VolumeChanged?.Invoke(this, RmsMeter.Compute(data.Span));
    }

    /// <summary>
    /// 每 <see cref="SessionOptions.WatchdogInterval"/> 巡检一次。这是「录音永远结束不了」
    /// 的唯一兜底：切到 secure desktop 后钩子在原桌面上根本收不到 KEYUP，钩子回调超时还会被
    /// 系统静默摘除。任一情形下没有看门狗，状态机就永久卡死在 Recording：麦克风一直录、
    /// 悬浮条一直绿、内存一直涨，而且用户再按热键会被当成「已在录音中」而无法自救。
    /// </summary>
    private void OnWatchdogTick()
    {
        // 生产环境这里跑在线程池的计时器线程上：异常逃出去不是「这一次巡检失败」，
        // 而是整个进程崩掉。
        try
        {
            lock (_gate)
            {
                if (!_acceptingWork || State != SessionState.Recording)
                {
                    return;
                }

                var held = _time.GetUtcNow() - _recordingStartedAt;

                // 到达最长录音时长：自动结束并正常识别（不算失败）。
                if (_buffer.IsFull || held >= TimeSpan.FromSeconds(_options.MaxRecordingSeconds))
                {
                    _logger.LogInformation("达到最长录音时长 {Max}s，自动结束", _options.MaxRecordingSeconds);

                    SubmitRecording("已达最长录音时长");

                    return;
                }

                RaiseCountdownIfNear(held);

                // KEYUP 丢失：物理键已松开，但 Released 事件永不到达。
                if (!_hook.IsMainKeyPhysicallyDown)
                {
                    _logger.LogWarning("检出 KEYUP 丢失（物理键已松开），强制结束录音");

                    FinishRecording(held, notice: null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "看门狗巡检发生未预期异常");
        }
    }

    /// <summary>
    /// 快到上限了就报一声还剩几秒。调用方须已持有 <see cref="_gate"/>、状态确为 Recording，
    /// 且这一跳已经排除过「到点收尾」那条路——故这里的剩余量必然为正。
    /// 向上取整：最后一声因此落在 1 而不是 0，屏上那个数也永远不小于真实剩余量。
    /// 同一秒内看门狗会跳五次，靠 <see cref="_lastCountdownSecond"/> 压成一声。
    /// </summary>
    private void RaiseCountdownIfNear(TimeSpan held)
    {
        var remaining = TimeSpan.FromSeconds(_options.MaxRecordingSeconds) - held;

        if (remaining > _options.CountdownThreshold)
        {
            return;
        }

        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);

        if (seconds == _lastCountdownSecond)
        {
            return;
        }

        _lastCountdownSecond = seconds;

        RecordingCountdown?.Invoke(this, seconds);
    }

    private void OnSessionInterrupted(object? sender, EventArgs e)
    {
        // 生产环境由 SystemEvents 的专用线程同步触发，异常逃出去同样是进程级后果。
        try
        {
            lock (_gate)
            {
                if (!_acceptingWork || State != SessionState.Recording)
                {
                    return;
                }

                // 锁屏 / 休眠：取消当前会话，不识别。用户此刻已不在这台机器前，
                // 解锁后往一个陌生的前台窗口注入文本是危险的。
                _logger.LogInformation("会话中断（锁屏或休眠），取消当前录音");

                StopCaptureSafely();

                _buffer.Clear();

                _sessionNotice = null;

                EnterRest(null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理会话中断事件时发生未预期异常");
        }
    }

    /// <summary>
    /// 生产环境跑在线程池线程上，且可能并发引发，故 <c>_gate</c> 同时也是并发的兜底。
    /// 连带一条已知降级：投递到执行之间没有上界，线程池饥饿时可到秒级，期间用户若已开始
    /// 新一次录音，这条陈旧通知会把新录音立刻收尾。事件不带载荷、没有代次可判，
    /// 故只能靠 <c>State != Recording</c> 这道守卫兜住大部分。
    /// </summary>
    private void OnDefaultDeviceChanged(object? sender, EventArgs e)
    {
        try
        {
            lock (_gate)
            {
                if (!_acceptingWork || State != SessionState.Recording)
                {
                    return;
                }

                // 用已有缓冲直接收尾，不丢弃：设备一换 DataAvailable 就停了，若还傻等 KEYUP，
                // 松开时缓冲只剩零点几秒，前半句被「录音过短」静默吞掉。
                _logger.LogInformation("默认录音设备已变更，用已有缓冲结束本次录音");

                FinishRecording(_time.GetUtcNow() - _recordingStartedAt, "录音设备已变更");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理录音设备变更事件时发生未预期异常");
        }
    }

    private void OnHookLost(object? sender, EventArgs e)
    {
        try
        {
            lock (_gate)
            {
                if (!_acceptingWork)
                {
                    return;
                }

                _logger.LogError("键盘钩子已被系统摘除（回调超时），热键失效");

                // 转写只认 EndTranscription 收尾：这里改状态会让转写还在跑却显示静止，还能再开一次。
                // 提示不能丢，记下来等转写落地时补报。
                if (State == SessionState.Transcribing)
                {
                    _hookLostWhileTranscribing = true;

                    return;
                }

                if (State == SessionState.Recording)
                {
                    StopCaptureSafely();

                    _buffer.Clear();
                }

                // 这里用 EnterFailed 而非 EnterFailedIfStillMine：钩子失效是此刻正在发生的
                // 全局故障，不是某一路陈旧识别的结局，此后热键彻底不工作，必须无条件告诉用户。
                _sessionNotice = null;

                EnterFailed(HookLostMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理钩子失效事件时发生未预期异常");
        }
    }

    /// <summary>
    /// IAudioCapture.Stop 在生产环境是 WASAPI 的原生包装，会抛。采集器停不下来不该连带
    /// 把状态机卡死在 Recording——那正是本类要根除的故障模式，故这里只记日志不外抛。
    /// </summary>
    private void StopCaptureSafely()
    {
        try
        {
            _capture.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止录音采集失败，继续收尾流程");
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                await HandleRequestAsync(request, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 关停时的正常退出路径。
        }
    }

    private async Task HandleRequestAsync(RecognitionRequest request, CancellationToken cancellationToken)
    {
        string text;

        try
        {
            text = await _engine.RecognizeAsync(request.Samples, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "识别失败");

            lock (_gate) { EnterFailedIfStillMine("识别失败，详见日志"); }

            return;
        }

        try
        {
            // 单独兜一层：下面那个 catch 的语义是「放弃上屏」，数字没转成而已却把整句话丢掉，
            // 这交换不成立。转换失败按原文上屏，但要留日志——纯字符串函数抛异常本身是 bug。
            try
            {
                text = ChineseNumberItn.Convert(text, _options.ApplyChineseNumberItn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "中文数字转换失败，本次按原文上屏");
            }

            // 顺序要紧：清理阈值按「最终要上屏的那串」的字数算，故必须排在数字转换之后。
            text = TrailingPunctuationTrimmer.Trim(
                text, _options.TrimTrailingPunctuation, _options.TrimPunctuationMaxChars);

            if (string.IsNullOrWhiteSpace(text))
            {
                // 「录到了但识别为空」恰好是最常见的一种「按了没反应」，不记日志的话排障的人
                // 无从判断究竟是钩子没触发、采集没起来，还是识别真的返回了空。
                _logger.LogInformation("识别结果为空，本次不上屏（{Samples} 个样本）", request.Samples.Length);

                lock (_gate) { EnterFailedIfStillMine("识别结果为空"); }

                return;
            }

            var elapsed = _time.GetUtcNow() - request.CapturedAt;
            var currentWindow = _probe.GetForegroundWindow();

            if (currentWindow != request.TargetWindow || elapsed > _options.StaleResultThreshold)
            {
                _logger.LogInformation(
                    "焦点已从 0x{Original:X}（{OriginalName}）切至 0x{Current:X}（{CurrentName}）"
                    + "，耗时 {Elapsed}，结果转剪贴板",
                    request.TargetWindow, DescribeSafely(request.TargetWindow),
                    currentWindow, DescribeSafely(currentWindow), elapsed);

                var clipboardResult = await _injector.InjectAsync(text, nint.Zero, cancellationToken);

                // 剪贴板兜底本身也会失败（常见场景是另一个进程正占着剪贴板）。无视返回值一律报
                // 「已复制到剪贴板」的话，用户会被告知文本安全，实际上音频和文本双双丢失。
                var message = clipboardResult == InjectResult.Ok
                    ? "焦点已切换，结果已复制到剪贴板"
                    : $"焦点已切换，且复制到剪贴板也失败（{clipboardResult}），结果已丢失";

                lock (_gate) { EnterFailedIfStillMine(message); }

                return;
            }

            if (!_probe.CanInjectInto(request.TargetWindow))
            {
                // UIPI：SendInput 在此场景下返回成功但一个字符也进不去，且 GetLastError 不指示原因。
                var clipboardResult = await _injector.InjectAsync(text, nint.Zero, cancellationToken);
                var message = clipboardResult == InjectResult.Ok
                    ? "目标窗口需管理员权限，已复制到剪贴板"
                    : $"目标窗口需管理员权限，且复制到剪贴板也失败（{clipboardResult}），结果已丢失";

                lock (_gate) { EnterFailedIfStillMine(message); }

                return;
            }

            var result = await _injector.InjectAsync(text, request.TargetWindow, cancellationToken);

            lock (_gate)
            {
                // 以下两个分支共享同一条不变量：只有当 State 仍是「我这次识别启动时留下的
                // Recognizing」，才允许把它改成终态。识别期间允许再按热键排队下一轮，那一轮可能
                // 早已进入 Recording 甚至再次变回 Recognizing，改它就是踩掉别人的进度。
                // 踩成 Idle 只是显示错误；踩成 Failed 更严重——会让 OnReleased 的守卫把新一轮
                // 真实的 KEYUP 直接吞掉，麦克风从此不再停止，会话永久卡死。
                if (result == InjectResult.Ok)
                {
                    if (IsRecognitionStillMine())
                    {
                        EnterRest(null);
                    }
                }
                else
                {
                    EnterFailedIfStillMine($"上屏失败：{result}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // GetForegroundWindow / CanInjectInto / InjectAsync 在生产环境都是原生 P/Invoke 的
            // 包装，任一个都可能抛异常。没有这层兜底，异常会逃出 ProcessQueueAsync 的
            // await foreach，把工作线程整个弄死——State 永远卡在 Recognizing，此后每次按热键
            // 都能正常排队，但队列从此无人消费。
            _logger.LogError(ex, "处理识别结果时发生未预期异常");

            lock (_gate) { EnterFailedIfStillMine("处理识别结果时出错，详见日志"); }
        }
    }

    /// <summary>
    /// 调用方须已持有 _gate。仅当本次识别的落地仍归它管时才转入 Failed，见 <see cref="IsRecognitionStillMine"/>。
    /// 异常本身已经在调用处记过日志，这里静默放弃展示失败提示不会丢信息。
    /// </summary>
    private void EnterFailedIfStillMine(string message)
    {
        if (IsRecognitionStillMine())
        {
            EnterFailed(message);
        }
    }

    /// <summary>
    /// 调用方须已持有 _gate。识别落地能否改状态，七处落地都经这里，守卫只写这一份。
    /// 关停已开始则不能：原生解码打断不了，落地可能晚于关停掐掉失败计时器，那时再起的计时器已无人掐。
    /// 否则仅当 State 仍是本次识别自己留下的 Recognizing 时才能，不是则说明用户已开始更新一代的录音或识别，不得覆盖它。
    /// </summary>
    private bool IsRecognitionStillMine()
    {
        if (!_acceptingWork)
        {
            _logger.LogInformation("关停已开始，丢弃本次识别的落地");

            return false;
        }

        return State == SessionState.Recognizing;
    }

    /// <summary>
    /// 调用方须已持有 _gate。回到「静止态」。静止态通常是 Idle，但若期间用户请求过暂停，
    /// 则是 Paused——暂停请求必须能穿过异步识别活到会话终点。
    /// </summary>
    private void EnterRest(string? message)
    {
        if (_pauseRequested)
        {
            _pauseRequested = false;

            SetState(SessionState.Paused, message);

            return;
        }

        SetState(SessionState.Idle, message);
    }

    /// <summary>调用方须已持有 _gate。</summary>
    private void EnterFailed(string message)
    {
        SetState(SessionState.Failed, message);

        CancelFailedTimer();

        _failedTimer = _time.CreateTimer(
            _ =>
            {
                lock (_gate)
                {
                    if (State == SessionState.Failed)
                    {
                        EnterRest(null);
                    }
                }
            },
            state: null,
            dueTime: _options.FailedDisplayDuration,
            period: Timeout.InfiniteTimeSpan);
    }

    private void CancelFailedTimer()
    {
        _failedTimer?.Dispose();
        _failedTimer = null;
    }

    /// <summary>
    /// 调用方须已持有 _gate。<paramref name="message"/> 为 null 表示「本次迁移自己没话要说」，
    /// 此时沿用会话告知 <see cref="_sessionNotice"/>，不再无条件把 LastMessage 清空。
    /// 这条规则正是「录音设备已变更」能活到终态的原因：设备变更发生在 Recording，而它之后
    /// 必然还要经过 Recognizing 与 Idle 两次无话可说的迁移。
    /// </summary>
    private void SetState(SessionState state, string? message)
    {
        State = state;
        LastMessage = message ?? _sessionNotice;
        StatusChanged?.Invoke(this, new SessionStatusEventArgs(state, LastMessage));
    }

    /// <summary>
    /// 关停序列。顺序不可颠倒：先停受理、再取消在途、等工作线程退出、最后才 Dispose 引擎。
    /// 反过来会在原生 handle 已释放而工作线程仍在 Decode 时触发进程级 AccessViolation
    /// ——try/catch 抓不到。
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (!_acceptingWork)
            {
                return;
            }

            _acceptingWork = false;

            CancelFailedTimer();
        }

        _hook.Pressed -= OnPressed;
        _hook.Released -= OnReleased;
        _hook.HookLost -= OnHookLost;

        _capture.DataAvailable -= OnDataAvailable;
        _capture.DefaultDeviceChanged -= OnDefaultDeviceChanged;

        _monitor.SessionLocked -= OnSessionInterrupted;
        _monitor.SystemSuspending -= OnSessionInterrupted;

        // 停看门狗要在解绑之后：留着它反而能兜住「解绑瞬间正好在途的一次巡检」，
        // 而巡检本身已由 _acceptingWork 守卫拦住，不会再往队列里塞新活。
        _watchdog?.Dispose();

        _watchdog = null;

        _hook.Stop();

        // 采集器停不下来时不能让异常从这里逃出去，否则会跳过下面 Dispose 引擎那一步。
        StopCaptureSafely();

        _queue.Writer.TryComplete();

        await _shutdown.CancelAsync();

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("识别工作线程未在 {Timeout} 内退出，强制继续关停", timeout);
            }
            catch (OperationCanceledException)
            {
                // 工作线程按预期因取消退出，非故障，无需特殊处理。
            }
            catch (Exception ex)
            {
                // 工作线程即便以任何原因故障退出，也不能让异常从这里逃出 ShutdownAsync，
                // 跳过下面 Dispose 引擎这一步——那一步存在的意义正是避免原生 handle 在推理
                // 仍可能进行时被释放。
                _logger.LogError(ex, "识别工作线程异常退出，仍继续关停流程");
            }
        }

        await _engine.DisposeAsync();

        _shutdown.Dispose();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync(TimeSpan.FromSeconds(3));
}
