using Lisscribe.Core.Abstractions;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Lisscribe.Platform;

/// <summary>
/// WASAPI 共享模式录音，产出恒为 16kHz 单声道。
/// </summary>
/// <remarks>
/// 共享模式是硬性选择：独占模式能直接向设备要 16kHz、省掉重采样，但它会抢占设备，
/// 对一个常驻工具不可接受。代价是拿到设备混音格式后必须自己重采样，那条链在
/// <see cref="CaptureSamplePipeline"/> 里。
/// <para>
/// 收尾次序是本类唯一真正难的地方。<c>StopRecording()</c> 只置一个标志就返回，采集线程此刻
/// 可能正卡在一次 <c>DataAvailable</c> 里；<c>Dispose()</c> 会 Join 采集线程，但只在采集线程
/// 还活着时（自停路径上一次 Join 也不做）。故次序是
/// <c>StopRecording()</c> → <c>Dispose()</c> → 才 <c>Flush()</c>；
/// 而救回收尾那一包的不是 <c>Flush()</c>，是「管道归 <see cref="CaptureSession"/> 自己持有、
/// 不被 <c>Stop</c> 置空」。
/// </para>
/// <para>
/// 「不死锁」压在两条上，第二条守在 Core 里：一、本类收尾时持锁 Join，而
/// <c>_gate</c> 只有生命周期调用方（Start / Stop / Dispose）会拿，采集线程与 COM 通知线程
/// 一次也不拿；二、<c>SessionController.OnDataAvailable</c> 绝不能拿它自己的 <c>_gate</c>
/// ——那是一条死锁豁免，改动那一处之前请连着这里一起读。
/// </para>
/// <para>
/// 连带的禁令按最宽的记：订阅者在 <see cref="DataAvailable"/> / <see cref="DefaultDeviceChanged"/>
/// 的处理器里，不得阻塞等待任何收尾线程可能持有的东西。
/// </para>
/// </remarks>
public sealed class WasapiAudioCapture : IAudioCapture, IMMNotificationClient
{
    /// <summary>拿不到默认录音设备时 <see cref="CurrentDeviceName"/> 给出的占位串。</summary>
    public const string NoDeviceName = "（无可用录音设备）";

    private readonly MMDeviceEnumerator _enumerator = new();

    /// <summary>
    /// 构造时已过 <see cref="SafeLoggerExtensions.Safe"/>。本类的日志分布在采集线程、
    /// COM 通知线程和收尾路径上，任何一条记日志失败都不许把那些线程带走。
    /// </summary>
    private readonly ILogger _logger;

    private readonly Lock _gate = new();

    private CaptureSession? _session;
    private bool _notificationsRegistered;

    /// <summary>写在 <see cref="_gate"/> 内，但线程池上的派发要在锁外读它，故 volatile。</summary>
    private volatile bool _disposed;

    /// <summary>已推给线程池、尚未跑完的设备变更通知条数。</summary>
    private int _notificationsInFlight;

    public WasapiAudioCapture(ILogger<WasapiAudioCapture> logger) => _logger = logger.Safe();

    public event EventHandler<ReadOnlyMemory<float>>? DataAvailable;

    /// <summary>
    /// 系统默认录音设备变更。
    /// </summary>
    /// <remarks>
    /// 它可能在多条线程池线程上并发引发，订阅者必须自己串行化（UI 订阅者要自己加锁或投递到
    /// UI 线程，且投递要用不等待的那种）。次序同样不保证，且刻意不保证：事件不带任何载荷，
    /// 为它引入一条自建派发线程反而会把「订阅者卡住 ⇒ 关停卡住」装回来。
    /// </remarks>
    public event EventHandler? DefaultDeviceChanged;

    /// <summary>
    /// 只替换「打开默认录音设备」这一步——那是真 COM + 真硬件，测试里既造不出想要的时序，
    /// 也没法让它交出一个我们指定的格式。缝落在数据上，不落在控制流上：它之后的每一行
    /// （格式校验、管道构造、订阅、收尾次序、事件转发、异常兜底）都是生产代码照常执行。
    /// </summary>
    internal Func<(IWaveIn Capture, MMDevice? Device, string Name)>? OpenCaptureForTest { get; init; }

    /// <summary>
    /// 已推给线程池、尚未跑完的设备变更通知条数。只给测试用来「等派发落地」，生产代码不读它。
    /// 拿它来等是无害的：等错了只会超时，不会把红的说成绿的。
    /// </summary>
    internal int NotificationDispatchesInFlight => Volatile.Read(ref _notificationsInFlight);

    /// <summary>
    /// 只替换 <see cref="CurrentDeviceName"/> 里查默认端点那一次调用。
    /// 存在的理由是「查不到设备时给占位串」这条路在有麦克风的机器上永远走不到。
    /// </summary>
    internal Func<MMDevice>? OpenDefaultDeviceForTest { get; init; }

    /// <summary>
    /// 当前默认录音设备名。
    /// </summary>
    /// <remarks>
    /// 不缓存：单次查询约 1.58ms，UI 按需读一次完全够用，而缓存要背上三处失效时机
    /// （默认设备变更、设备改名、从 Unplugged 回到 Active），漏一处就是「界面上写着一个
    /// 早就不用的麦克风」。
    /// </remarks>
    public string CurrentDeviceName
    {
        get
        {
            try
            {
                // 缝只决定「查询交出什么」，之后的每一行——读名字、失败降级、日志——都是生产代码。
                using var device = (OpenDefaultDeviceForTest ?? DefaultCaptureEndpoint)();

                return device.FriendlyName;
            }
            catch (Exception ex)
            {
                // UI 直接读这个属性（托盘提示、配置窗），从这里抛出去等于「拔了麦克风就打不开设置」。
                // 这层不是过度防御：MMDevice.FriendlyName 在 NotPresent 端点上真的会抛
                // （COMException 0xE000020B），故设备列表 UI 逐个读名字时每一个都得单独包。
                _logger.LogWarning(ex, "读取默认录音设备名失败");

                return NoDeviceName;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_session is not null)
            {
                return;
            }

            EnsureNotificationsRegistered();

            var (capture, device, name) = (OpenCaptureForTest ?? OpenDefaultCapture)();

            CaptureSession session;
            try
            {
                // 格式在这里校验，认不出就抛，而上一行已经 Activate 出了一个 IAudioClient——
                // 必须当场释放掉，否则漏一个 COM 对象。（此刻麦克风还没开：AudioClient.Initialize
                // 要到 StartRecording() 里才调，何况共享模式本就不独占。）
                session = new CaptureSession(
                    capture, device, new CaptureSamplePipeline(capture.WaveFormat), _logger, RaiseDataAvailable);
            }
            catch
            {
                DisposeQuietly(capture, device);

                throw;
            }

            _logger.LogInformation(
                "录音设备 {Device}，采集格式 {Format}", name, CaptureSamplePipeline.Describe(capture.WaveFormat));

            try
            {
                session.Start();
            }
            catch
            {
                session.Stop(deliverRemaining: false);

                throw;
            }

            _session = session;
        }
    }

    public void Stop()
    {
        // 持锁走完整个收尾是刻意的：不然 Stop 放开锁之后、尾巴发出去之前，另一条线程的 Start()
        // 就能开出新一轮录音，于是上一轮的尾巴接到了下一轮的缓冲上。
        // 持锁期间会 Join 采集线程，而采集线程从不拿这把锁，故不会死锁。
        lock (_gate)
        {
            var session = _session;

            _session = null;

            session?.Stop(deliverRemaining: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            var session = _session;

            _session = null;

            // 关停时不再往订阅者身上发音频：那段尾巴属于一次没人会再消费的会话，而此刻订阅者
            // 可能已经半关停。这一条是为「接线被改」留的兜底，不是主路径。
            session?.Stop(deliverRemaining: false);

            if (_notificationsRegistered)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(this);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "注销录音设备变更通知失败");
                }

                _notificationsRegistered = false;
            }

            // 刻意不 Dispose _enumerator：它的 Dispose 就是一次 ReleaseComObject，而
            // CurrentDeviceName 不持锁、随时可能正在用同一个 RCW。不释放也不漏——
            // MMDeviceEnumerator 自己没有终结器，底下的 __ComObject 由 CLR 的 RCW 终结器回收，
            // 而这个对象本来就是进程生命期的。
        }
    }

    // ---------- IMMNotificationClient ----------
    // 用户中途插拔耳机时，录音流会指向已拔出的设备并静默失败。
    // 状态机收到此通知后用已有缓冲直接 finish。

    /// <summary>
    /// 这个方法跑在 COM 通知线程上，必须立即返回，一次也不许阻塞。
    /// </summary>
    /// <remarks>
    /// MSDN 明令这些方法 must be nonblocking、never wait on a synchronization object、
    /// never release the final reference on an MMDevice API object。同步调订阅者会一路走到
    /// <c>WasapiCapture.Dispose()</c>（Join 采集线程）与 <c>MMDevice.Dispose()</c>，
    /// 实测整条链挂死 15 秒未返回，连音频服务一起卡住，只能杀进程。
    /// 故这条线程只做两件事：过滤，和把余下的活整体推到线程池。过滤留在这里是有意的——
    /// 切一次默认设备会来多条通知（实测 7 次），为无关的那些去排线程池活儿没有意义。
    /// 用线程池而不是自建派发线程：这里的事件不带载荷、次序无意义，而自建线程要在 Dispose 里
    /// Join，那就把「订阅者卡住 ⇒ 关停卡住」又装了回来。
    /// </remarks>
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Capture || role != Role.Console)
        {
            return;
        }

        Interlocked.Increment(ref _notificationsInFlight);

        try
        {
            // UnsafeQueueUserWorkItem：不流转 ExecutionContext——不想把 COM 通知线程的执行上下文
            // 捎带到订阅者身上，也省掉一次捕获。
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Owner.DeliverDefaultDeviceChanged(state.DeviceId),
                (Owner: this, DeviceId: defaultDeviceId),
                preferLocal: false);
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _notificationsInFlight);

            // 排不进线程池（内存耗尽之类）。仍然不许抛：异常逃回 COM 只会变成一个没人看的
            // 失败 HRESULT，而我们连一条记录都没有。
            _logger.LogError(ex, "派发默认录音设备变更通知失败，本次设备变更将无人知晓");
        }
    }

    // 余下四个方法本项目一件事也不做，故留空——空方法体抛不出东西，不必包。
    // 真要加逻辑，记得连 try/catch 一起加。
    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
    }

    public void OnDeviceRemoved(string deviceId)
    {
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    // ---------- 内部 ----------

    private MMDevice DefaultCaptureEndpoint() =>
        _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

    private static void DisposeQuietly(IWaveIn capture, MMDevice? device)
    {
        try
        {
            capture.Dispose();
        }
        catch
        {
            // 开设备失败路径上的清理，抛出去只会盖掉真正的原因。
        }

        try
        {
            device?.Dispose();
        }
        catch
        {
        }
    }

    private void RaiseDataAvailable(float[] samples) => DataAvailable?.Invoke(this, samples);

    /// <summary>
    /// 跑在线程池线程上，不再是 COM 通知线程——订阅者在这里想阻塞多久都行。
    /// <c>_disposed</c> 那道检查是尽力而为：加锁就等于让线程池线程去抢 <c>_gate</c>，
    /// 而 <c>Dispose</c> 持着它做 Join，又是同一族的等待关系。
    /// </summary>
    private void DeliverDefaultDeviceChanged(string deviceId)
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            // 这一句挡在 Invoke 前面，它抛出就等于把事件静默吃掉，而设备变更被吃掉的后果是
            // 用户拔了耳机、状态机毫不知情，傻等 KEYUP。安全性来自构造函数里的 Safe()。
            _logger.LogInformation("默认录音设备已变更为 {DeviceId}", deviceId);

            DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 异常逃出 IMMNotificationClient 的方法不会终结进程（CCW 翻成失败 HRESULT 交还给
            // 音频服务，后续通知照常到达），所以没有它的后果不是崩，而是一个字都不知道。
            // 但派发已挪到线程池，逃逸的后果反而更重——线程池线程上未处理的异常是进程终结。
            try
            {
                _logger.LogError(ex, "处理默认录音设备变更事件时发生未预期异常，已忽略");
            }
            catch
            {
                // 与 CaptureSession.OnStopped 同一形状、同一代价：这一句在线程池线程上，
                // 逃逸即进程终结。
            }
        }
        finally
        {
            Interlocked.Decrement(ref _notificationsInFlight);
        }
    }

    /// <summary>
    /// 调用方须已持有 <see cref="_gate"/>。
    /// 只从 <see cref="Start"/> 调是有意的：状态机唯一在乎设备变更的时刻是 Recording 中，
    /// 在那之前注册只是让一个空跑的通知客户端挂在音频服务上。
    /// 代价：程序启动后、第一次录音之前 <see cref="DefaultDeviceChanged"/> 恒不触发，
    /// 想靠它刷新托盘 / 配置窗上的设备名拿不到，那时只能自己轮询 <see cref="CurrentDeviceName"/>。
    /// </summary>
    private void EnsureNotificationsRegistered()
    {
        if (_notificationsRegistered)
        {
            return;
        }

        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);

            _notificationsRegistered = true;
        }
        catch (Exception ex)
        {
            // 不抛：通知装不上是功能降级（拔耳机时状态机只能等用户松键），不是「这次录音开不了」。
            _logger.LogWarning(ex, "注册录音设备变更通知失败，插拔设备时状态机将收不到通知");
        }
    }

    private (IWaveIn Capture, MMDevice? Device, string Name) OpenDefaultCapture()
    {
        // device 一路带到收尾再 Dispose，实际几乎是个空动作——MMDevice 不持有 AudioClient
        // （dev.AudioClient 每次新建），WasapiCapture 只持有构造时 Activate 出来的那一个，
        // 提前释放 device 也照录不误。带着它是因为 MMDevice 自带终结器，显式释放让它按确定的
        // 时机走完；别据此相信一条「不能提前释放」的约束去设计。
        // 与 CurrentDeviceName 走同一个查询入口：端点选择规则（Capture + Console）只写一处。
        var device = DefaultCaptureEndpoint();
        try
        {
            var name = device.FriendlyName;

            // WasapiCapture 构造时默认就是 Shared，这里显式写出来是因为「共享还是独占」是本类
            // 最要紧的一条设计决定，不该只存在于别人的默认值里。
            // 另注：WasapiCapture 在构造时捕获 SynchronizationContext.Current，用来决定
            // RecordingStopped 在哪条线程上引发。生产里 Start() 跑在热键派发线程上（无
            // SynchronizationContext），故那个事件落在采集线程上；别默认它是哪一条。
            var capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };

            return (capture, device, name);
        }
        catch
        {
            device.Dispose();

            throw;
        }
    }

    /// <summary>
    /// 一次录音会话所拥有的全部东西：采集器、设备、重采样管道。
    /// 它存在的理由就是那笔锁的账：管道是本对象的 <c>readonly</c> 字段、采集回调是本对象的
    /// 实例方法，于是回调不经过任何会被 <c>Stop()</c> 改写的字段，一把锁也不用加。
    /// </summary>
    private sealed class CaptureSession(
        IWaveIn capture,
        MMDevice? device,
        CaptureSamplePipeline pipeline,
        ILogger logger,
        Action<float[]> emit)
    {
        /// <summary>
        /// 关停途中不再往外发样本。采集线程读、收尾线程写，故 <c>volatile</c>。
        /// 它是尽力而为：已经进到回调里的那一包拦不住——但那一包本来就是要发的。
        /// </summary>
        private volatile bool _muted;

        public void Start()
        {
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
        }

        /// <summary>
        /// 收尾。次序不可颠倒：<c>StopRecording()</c>（只置标志）→ <c>Dispose()</c>
        /// （Join 采集线程）→ 才 <c>Flush()</c> 并发尾巴。
        /// </summary>
        /// <remarks>
        /// 「Join 之后采集线程已经死了」这句只在他停路径上成立。NAudio 的次序是
        /// <c>DoRecording</c> 返回 → <c>captureThread = null</c> → <c>RaiseRecordingStopped</c>，
        /// 故自停路径（订阅者抛异常、拔设备、驱动出错）上 <c>Dispose()</c> 读到的
        /// <c>captureThread</c> 已是 null，一次 Join 也不做就返回。
        /// 实际成立且够用的保证是：<c>Dispose()</c> 返回之后不会再有 <c>DataAvailable</c>，
        /// 故 <c>Flush()</c> 与 <c>Process()</c> 不可能并发。但 <c>RecordingStopped</c> 的处理器
        /// 可能仍在采集线程上跑，所以 <see cref="OnStopped"/> 里不得碰任何收尾线程也会碰的状态。
        /// </remarks>
        public void Stop(bool deliverRemaining)
        {
            _muted = !deliverRemaining;

            try
            {
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "停止录音失败，继续收尾");
            }

            try
            {
                capture.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "释放采集器失败，继续收尾");
            }

            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;

            try
            {
                device?.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "释放录音设备失败");
            }

            if (!deliverRemaining)
            {
                return;
            }

            try
            {
                // 实测这一支是死代码：AudioResampler.Flush() 在这条链上恒空，救回收尾那一包的
                // 从来不是它，而是「管道归本对象持有、不被 Stop 置空」。留着是因为 Flush 的契约
                // 允许非空，删掉就意味着实现哪天真吐出残留时会被静默丢弃。
                var tail = pipeline.Flush();

                if (tail.Length > 0)
                {
                    emit(tail);
                }
            }
            catch (Exception ex)
            {
                // 订阅者在尾巴上抛异常，不该把 Stop() 变成失败——状态机正靠它收尾。
                logger.LogError(ex, "送出录音尾巴时发生未预期异常，已忽略");
            }
        }

        /// <summary>
        /// 跑在 NAudio 的采集线程上，且 <c>emit</c> 调的是外部订阅者的代码。
        /// </summary>
        /// <remarks>
        /// 这里逃逸的异常不会崩进程（NAudio 自己包了 try/catch），但后果比崩更隐蔽：
        /// 录音当场静默停止，异常被塞进 <c>RecordingStopped</c>，而用户看到的是悬浮条还绿着、
        /// 说完却什么也没上屏。故这层 catch 是承重的。
        /// 采集线程没有键盘钩子那种系统级时限，唯一的预算是 WASAPI 采集缓冲被灌满
        /// （实测包长自 60ms 涨到 100ms 就封顶），故在这条线程上做整包重采样安全，
        /// 但订阅者若长期比实时慢，丢样本是迟早的事。
        /// </remarks>
        private void OnData(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (_muted || e.BytesRecorded <= 0)
                {
                    return;
                }

                var samples = pipeline.Process(e.Buffer.AsSpan(0, e.BytesRecorded));

                if (samples.Length > 0)
                {
                    emit(samples);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "处理录音采集回调时发生未预期异常，已忽略");
            }
        }

        /// <summary>
        /// 与 <see cref="OnData"/> 不同：这里逃逸的异常会终结进程（退出码 0xE0434352），
        /// 因为 NAudio 是在 <c>CaptureThread</c> 那层 try/catch 之外引发它的。
        /// </summary>
        /// <remarks>
        /// 死得并不快——从抛出到进程真的结束约 3–4 秒，长到足够骗人：主线程若在那个窗口里
        /// 自己跑完，同一个崩溃会被记成「还活着、退出码 0」。
        /// 带异常回来意味着采集中途死了。T0 没有「录音已死」这条通路可走：看门狗会在最长录音
        /// 时长处收尾，用户松键也会收尾，已录到的部分照常送去识别，故这里只记账。
        /// 另注：本处理器可能与 <see cref="Stop"/> 的其余部分同时在跑（自停路径上 Dispose 不
        /// Join），故这里除了记日志什么也不许碰。
        /// </remarks>
        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            try
            {
                if (e.Exception is not null)
                {
                    logger.LogError(e.Exception, "录音异常终止");
                }
            }
            catch
            {
                // logger 已过 Safe()，走到这里说明连它都失效了。这条线程逃出去就是进程终结，
                // 少记一条日志换进程活着，是唯一划算的交换。
                // 这层裸 catch 与构造函数里那道 .Safe() 各自单独就够，回归钉子钉的是两者的合取
                // ——别据此以为随手删一个是安全的。
            }
        }
    }
}
