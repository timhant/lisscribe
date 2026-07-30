using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Hotkey;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Platform;

/// <summary>
/// WH_KEYBOARD_LL 低层键盘钩子。判定逻辑在 <see cref="ModifierKeyTracker"/> 与
/// <see cref="HotkeyMatcher"/> 里，本类只做接线。
/// </summary>
/// <remarks>
/// 三条硬约束：一、装钩子的线程必须有消息泵（见 <see cref="MessagePumpThread"/>），
/// 重装也必须回到那条线程上做，线程池线程上重装会得到一个永不触发的钩子。
/// 二、HOOKPROC 委托必须被静态字段 GC root 住，thunk 被回收后按键即 AccessViolation（catch 不到）。
/// 三、回调跑太久系统会静默摘除钩子（注册表 LowLevelHooksTimeout 管，默认 300ms），故有存活自检；
/// 另有一条阈值约 1000ms 量级，超了系统只丢这一次按键、不摘钩子，程序端拿不到任何证据。
/// </remarks>
public sealed class KeyboardHook : IHotkeyHook
{
    /// <summary>自检轮询间隔。无事可做时它只读一个 bool，故可以查得勤些把恢复延迟压下来。</summary>
    private static readonly TimeSpan DefaultHealthCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 回调耗时超过它就认为「系统可能已经把钩子摘了」，安排一次重装。
    /// 默认 LowLevelHooksTimeout 是 300ms，取 200ms 留余量；真实回调耗时不足 2ms，不会误触发。
    /// </summary>
    private static readonly TimeSpan DefaultCallbackOverrunThreshold = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan DispatcherStopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>连续这么多次重装失败才报 HookLost。一次偶发失败不该让自检自己成为单点故障。</summary>
    private const int MaxConsecutiveReinstallFailures = 3;

    /// <summary>
    /// 构造时已过 <see cref="SafeLoggerExtensions.Safe"/>。本类的日志分布在钩子回调、消息泵线程
    /// 与 <c>Timer</c> 回调上，三处逃逸的异常都是进程终结。
    /// </summary>
    private readonly ILogger logger;
    private readonly TimeSpan _healthCheckInterval;
    private readonly TimeSpan _callbackOverrunThreshold;
    private readonly TimeSpan? _pumpStartTimeout;

    public KeyboardHook(
        ILogger<KeyboardHook> logger,
        TimeSpan? healthCheckInterval = null,
        TimeSpan? callbackOverrunThreshold = null)
        : this(logger, healthCheckInterval, callbackOverrunThreshold, pumpStartTimeout: null)
    {
    }

    /// <summary>
    /// <paramref name="pumpStartTimeout"/> 只为测试而开：「泵没停干净就不许撤 GC root」那一支
    /// 只在 onStart 超时时才走得到，而生产超时是 5 秒，测试造不出那个现场。
    /// </summary>
    internal KeyboardHook(
        ILogger<KeyboardHook> logger,
        TimeSpan? healthCheckInterval,
        TimeSpan? callbackOverrunThreshold,
        TimeSpan? pumpStartTimeout)
    {
        this.logger = logger.Safe();

        _healthCheckInterval = healthCheckInterval ?? DefaultHealthCheckInterval;
        _callbackOverrunThreshold = callbackOverrunThreshold ?? DefaultCallbackOverrunThreshold;
        _pumpStartTimeout = pumpStartTimeout;
    }

    /// <summary>
    /// 约束 2 的落实处：委托实例由静态字段持有。用集合而不是单个静态字段，
    /// 免得 Start 中途失败留下脏静态、也免得「多条测试各建各的实例」做不到。
    /// </summary>
    private static readonly List<User32.HookProc> RootedCallbacks = [];
    private static readonly Lock RootGate = new();

    private readonly ModifierKeyTracker _modifiers = new();
    private readonly Lock _lifecycle = new();

    private MessagePumpThread? _pump;
    private HotkeyMatcher? _matcher;
    private User32.HookProc? _callback;

    // Notify 在钩子回调线程上读它，Start/Stop 在别的线程上写它。
    private volatile BlockingCollection<Notification>? _notifications;

    private Thread? _dispatcher;
    private Timer? _healthCheck;
    private ushort _mainKeyCode;
    private long _pressedAtTimestamp;
    private int _healthCheckCount;
    private bool _disposed;

    /// <summary>
    /// 有证据表明钩子可能已被系统摘除（自己的回调跑得太久），需要重装一次。
    /// 置位与清位同在泵线程上——低层钩子的回调就跑在装钩子那条线程上。
    /// </summary>
    private volatile bool _reinstallPending;

    private volatile int _pendingForcedInstallFailures;

    // 以下三项只在泵线程上读写（InstallHook / UninstallHook / CheckHookAlive 全在那条线程），
    // 故不需要锁。别把它们挪到别的线程上碰。
    private nint _hookHandle;
    private bool _hookLostReported;
    private int _consecutiveReinstallFailures;

    /// <summary>
    /// 主键是否按住。由本钩子自己看到的按下/松开维护，不是问操作系统要的。
    /// 详见 <see cref="IsMainKeyPhysicallyDown"/>。
    /// </summary>
    private volatile bool _mainKeyHeld;

    /// <summary>见 <see cref="PassThrough"/>。跨线程读写，故 volatile。</summary>
    private volatile bool _passThrough;

    public event EventHandler? Pressed;
    public event EventHandler<TimeSpan>? Released;
    public event EventHandler? HookLost;

    /// <summary>
    /// 存活自检实际重装过的次数。供测试等待自检发生，生产代码不读它。
    /// 不可单独当预言机（这是被测代码自己记的账）——判「真的重装了」要看
    /// <see cref="CurrentHookHandle"/> 变没变。
    /// </summary>
    internal int CompletedHealthChecks => Volatile.Read(ref _healthCheckCount);

    /// <summary>当前钩子句柄。供测试判断重装是否真的发生过——句柄由系统分配，重装必换号。</summary>
    internal nint CurrentHookHandle => _hookHandle;

    /// <summary>静态 GC root 表里现存的委托数。供测试验证「泵没停干净时故意不撤 root」。</summary>
    internal static int RootedCallbackCount
    {
        get { lock (RootGate) { return RootedCallbacks.Count; } }
    }

    /// <summary>供测试往泵线程上塞一个堵住它的动作，制造「Join 超时」的现场。</summary>
    internal MessagePumpThread? PumpForTest => _pump;

    /// <summary>
    /// 泵线程的原生线程 id。供测试直接投 WM_QUIT 把它弄死，让自检定时器踩上「Post 必失败」那条路。
    /// </summary>
    internal uint PumpThreadIdForTest => _pump?.ThreadIdForTest ?? 0;

    /// <summary>
    /// 让接下来这么多次装钩子强制失败。只有测试会置它，生产恒为 0。
    /// 跨线程读写（测试线程置位、泵线程消费），故底下的字段是 volatile。
    /// </summary>
    internal int PendingForcedInstallFailures
    {
        get => _pendingForcedInstallFailures;
        set => _pendingForcedInstallFailures = value;
    }

    /// <summary>
    /// 装钩子前先等这个闸门。只有测试会置它，用来把 onStart 超时那条支路造出来。
    /// 用闸门而不是定长 Sleep：测试断言完即可放行，不会带着一条睡着的线程漏进后面的测试。
    /// </summary>
    internal ManualResetEventSlim? InstallGateForTest { get; set; }

    /// <summary>供测试给多段场景定序，不作预言机。</summary>
    internal int ConsecutiveReinstallFailures => _consecutiveReinstallFailures;

    /// <summary>同上。</summary>
    internal bool ReinstallPending => _reinstallPending;

    /// <summary>
    /// 主键是否仍被按住。
    /// </summary>
    /// <remarks>
    /// 不能用 GetAsyncKeyState 实现：win32k 的异步键状态表只对**被放行**的事件更新，而热键成立时
    /// 主键的 KEYDOWN 正是本钩子吞掉的，故它在整个按住期间恒报「没按」；Raw Input 与
    /// 「先 CallNextHookEx 再返回 1」两条退路同样收不到。
    /// 代价：内部标志只在自己看见 KEYUP 时才清位，而 KEYUP 真丢了的场景恰恰是看不见它，
    /// 兜底只剩 <c>SessionOptions.MaxRecordingSeconds</c>，锁屏 / 休眠另由 ISessionMonitor 覆盖。
    /// 仍与 GetAsyncKeyState 取或：主键的 KEYDOWN 被放行过（组合不成立）时那张表是准的。
    /// </remarks>
    public bool IsMainKeyPhysicallyDown =>
        _mainKeyHeld
        || (_mainKeyCode != 0 && (User32.GetAsyncKeyState(_mainKeyCode) & 0x8000) != 0);

    /// <summary>
    /// 放行态。真正的判定在 <see cref="HotkeyMatcher.PassThrough"/> 上，本类只做转发 + 记住当前值
    /// ——<see cref="Start"/> 会新建 matcher，不记就会在暂停期间改热键之后悄悄变回武装态。
    /// </summary>
    public bool PassThrough
    {
        get => _passThrough;

        set
        {
            _passThrough = value;

            // matcher 为 null（尚未 Start / 已 Stop）时只留值，Start 时一并交过去。
            var matcher = _matcher;

            if (matcher is not null)
            {
                matcher.PassThrough = value;
            }
        }
    }

    public void Start(HotkeyCombination combination)
    {
        if (!VirtualKey.TryFromName(combination.MainKey, out var mainKeyCode))
        {
            throw new ArgumentException($"无法识别的主键 \"{combination.MainKey}\"", nameof(combination));
        }

        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pump is not null)
            {
                throw new InvalidOperationException("键盘钩子已经启动");
            }

            _mainKeyCode = mainKeyCode;
            _matcher = new HotkeyMatcher(combination) { PassThrough = _passThrough };
            _modifiers.Reset();
            _mainKeyHeld = false;

            // 三个自检状态必须一起清，否则跨 Stop→Start 泄漏：重装失败计数带着旧值进入新一轮，
            // 重启后第一次偶发失败就凑够 MaxConsecutiveReinstallFailures 报 HookLost；
            // _reinstallPending 泄漏则让第一次自检无条件拆装一次健康钩子。配置窗改热键必走这条路。
            _hookLostReported = false;
            _reinstallPending = false;
            _consecutiveReinstallFailures = 0;

            _callback = HandleCallback;

            lock (RootGate)
            {
                RootedCallbacks.Add(_callback);
            }

            StartDispatcher();

            var pump = new MessagePumpThread(
                "lisscribe-keyboard-hook", InstallHook, UninstallHook, logger, _pumpStartTimeout);

            try
            {
                pump.Start();
            }
            catch
            {
                // 起不来的泵也必须收尾：onStart 超时那条路上线程还活着，不收尾就是一条挂着钩子的
                // 孤儿线程。先认领再收尾，别在这里自己 Dispose——那样会丢掉 StopAndJoin 的返回值，
                // 随后 StopCore 看到 _pump 为 null 便当作「干净停止」把 GC root 撤掉。
                _pump = pump;

                StopCore();

                throw;
            }

            _pump = pump;

            _healthCheck = new Timer(
                _ => RequestHealthCheck(),
                state: null,
                dueTime: _healthCheckInterval,
                period: _healthCheckInterval);

            logger.LogInformation("键盘钩子已启动，热键 {Hotkey}", combination.ToDisplayString());
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            StopCore();
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            StopCore();

            // 置位要在 StopCore 之后：Dispose 期间 StopCore 自己不该被 ObjectDisposedException 挡住。
            _disposed = true;
        }
    }

    /// <summary>
    /// 修饰键状态表重同步。只做名字说的事，不碰主键按住态——要复位那个请调
    /// <see cref="ForgetHeldMainKey"/>，两者该挂的事件不同。
    /// </summary>
    /// <remarks>
    /// 由前台窗口变化驱动。在钩子回调之外调 GetAsyncKeyState 是合法的，且修饰键一律被放行，
    /// 故它们在那张表里是准的。
    /// </remarks>
    public void ResyncModifiers()
    {
        var state = ModifierKeys.None;

        foreach (var modifier in (ModifierKeys[])
                 [ModifierKeys.Ctrl, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Win])
        {
            if (VirtualKey.GetBothSides(modifier)
                .Any(code => (User32.GetAsyncKeyState(code) & 0x8000) != 0))
            {
                state |= modifier;
            }
        }

        _modifiers.Resync(state);
    }

    /// <summary>
    /// 忘掉「主键还按着」这件事，两处标志一起清。挂会话中断（锁屏 / 休眠 / 会话切换），
    /// 不要挂前台窗口变化。
    /// </summary>
    /// <remarks>
    /// 必须成对清：KEYUP 丢失后 <see cref="HotkeyMatcher"/> 与本类的 <see cref="_mainKeyHeld"/>
    /// 双双卡在 true，用户下一次按热键会走进 <c>Suppress</c> 分支——键被吞、无 Pressed、无日志。
    /// 不能跟 <see cref="ResyncModifiers"/> 合并：前台窗口变化包括本程序自己的悬浮条、托盘气泡、
    /// IME 候选窗，任何一个在用户说话期间抢一下焦点都会截断录音。
    /// 「会话被打断」蕴含「这次按住已经结束」，「前台窗口变了」不蕴含。
    /// </remarks>
    public void ForgetHeldMainKey()
    {
        _mainKeyHeld = false;

        _matcher?.Reset();
    }

    /// <summary>调用方须已持有 _lifecycle。</summary>
    private void StopCore()
    {
        _healthCheck?.Dispose();

        _healthCheck = null;

        // 先摘钩子再停事件分发：反过来的话，摘除之前到达的最后一次按键会往一个已关闭的队列里投递。
        // StopAndJoin 返回 true 才意味着 UninstallHook 已经跑完——它在泵线程 Run() 的 finally 里，
        // 而 Join 超时只是放弃等待。
        var pumpStoppedCleanly = _pump?.StopAndJoin() ?? true;

        _pump = null;

        StopDispatcher();

        _matcher?.Reset();

        _matcher = null;

        _modifiers.Reset();

        // 主键码一并清掉：留着它，IsMainKeyPhysicallyDown 在停用之后还会去问一次 GetAsyncKeyState，
        // 于是一个已经停用的钩子报告「主键按住中」。
        _mainKeyHeld = false;
        _mainKeyCode = 0;

        if (_callback is not null)
        {
            if (pumpStoppedCleanly)
            {
                lock (RootGate)
                {
                    // 按引用移除而不是按值。委托的相等是「同 target 同 method」，眼下每实例只有一个
                    // 委托故按值也无害，但那是当下的形状不是不变量。
                    RootedCallbacks.RemoveAll(p => ReferenceEquals(p, _callback));
                }
            }
            else
            {
                // 泵没停干净 => UninstallHook 没跑完 => 钩子可能仍挂在系统里指着这个委托。
                // 此刻撤 root，GC 回收 thunk 之后下一次按键就是一个 catch 不到的 AccessViolation。
                // 故意泄漏这一个委托（几十字节），换掉一次不可 catch 的崩溃。
                logger.LogWarning(
                    "消息泵线程未干净退出，钩子可能仍挂在系统里；"
                    + "故意保留 HOOKPROC 委托的 GC root，宁可泄漏也不冒 AccessViolation 的险");
            }

            _callback = null;
        }
    }

    // ---------- 泵线程上的三件事：装、拆、自检 ----------

    /// <summary>
    /// 装一次钩子并返回句柄，0 表示失败。
    /// 单独抽出来只为 <see cref="PendingForcedInstallFailures"/> 这个测试缝——SetWindowsHookEx
    /// 的失败无法从外部制造，而「连续失败到第 N 次才上报 HookLost」需要钉住。
    /// </summary>
    private nint InstallHookHandle(out int error)
    {
        if (_pendingForcedInstallFailures > 0)
        {
            _pendingForcedInstallFailures--;

            // 强制失败绕过了 SetWindowsHookEx，此刻 GetLastWin32Error 是别处留下的陈旧值。
            error = 0;

            return nint.Zero;
        }

        InstallGateForTest?.Wait(TimeSpan.FromSeconds(30));

        // hMod 传 0 可行，流传甚广的 ERROR_HOOK_NEEDS_HMOD(1428) 不复现。
        var handle = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _callback!, nint.Zero, 0);

        error = handle == nint.Zero ? Marshal.GetLastWin32Error() : 0;

        return handle;
    }

    private void InstallHook()
    {
        _hookHandle = InstallHookHandle(out var error);

        if (_hookHandle == nint.Zero)
        {
            logger.LogError("SetWindowsHookEx 失败，Win32 错误码 {Error}", error);

            // 抛给 MessagePumpThread.Start()，由它重抛到调用方手上。在这条线程上让它逃逸出去
            // 是进程崩，不是启动失败。
            throw new InvalidOperationException($"安装键盘钩子失败，Win32 错误码 {error}");
        }
    }

    private void UninstallHook()
    {
        if (_hookHandle != nint.Zero)
        {
            User32.UnhookWindowsHookEx(_hookHandle);

            _hookHandle = nint.Zero;
        }
    }

    /// <summary>
    /// 计时器回调跑在线程池线程上，不能在这里直接重装钩子——那样换来的钩子句柄有效、
    /// lastError = 0，回调却再也不触发。这里只负责把活儿派回泵线程。
    /// </summary>
    private void RequestHealthCheck()
    {
        try
        {
            _pump?.Post(CheckHookAlive);
        }
        catch (Exception ex)
        {
            // 计时器回调里逃逸的异常是进程级后果。
            logger.LogError(ex, "派发钩子存活自检失败");
        }
    }

    /// <summary>
    /// 只在泵线程上跑。有证据才动手，没证据一个系统调用都不发——无条件定期重装的代价是
    /// 那两次系统调用之间热键完全不设防，且每次都把本进程的钩子挪到链首、扰动别的程序。
    /// </summary>
    /// <remarks>
    /// 这套证据有两个盲区。一、计时起点是托管回调的第一句，反向 P/Invoke 之前的排队与线程调度
    /// 全在窗口之外，低估量可达 800ms 量级。二、回调排队超过约 1000ms 时系统丢掉这一次按键
    /// 而不摘钩子，事后句柄没变、下一次热键正常，程序端零证据。
    /// 谁往泵线程上加活，谁就在制造这类丢键——而本方法自己就跑在这条线程上。
    /// </remarks>
    private void CheckHookAlive()
    {
        if (_hookHandle == nint.Zero || _hookLostReported || !_reinstallPending)
        {
            return;
        }

        _reinstallPending = false;

        // 系统若已把钩子静默摘除，Unhook 会失败（err 1404 ERROR_INVALID_HOOK_HANDLE），
        // 这正是我们要的信号。
        var stillValid = User32.UnhookWindowsHookEx(_hookHandle);

        // 新句柄先落局部变量，成功才写回字段。直接写 _hookHandle 时一次失败就把它写成 0，
        // 而本方法第一句就从守卫返回，_consecutiveReinstallFailures 永停在 1、HookLost 不可达，
        // 热键永久死掉而只留一条 Warning。
        var reinstalled = InstallHookHandle(out var installError);

        Interlocked.Increment(ref _healthCheckCount);

        if (reinstalled == nint.Zero)
        {
            _consecutiveReinstallFailures++;

            // 留着待办，下一轮再试。一次失败就永不重试会让自检自己成为单点故障。
            _reinstallPending = true;

            if (_consecutiveReinstallFailures < MaxConsecutiveReinstallFailures)
            {
                logger.LogWarning(
                    "钩子重装失败（第 {Count} 次），Win32 错误码 {Error}，将重试",
                    _consecutiveReinstallFailures, installError);

                return;
            }

            _hookLostReported = true;

            logger.LogError(
                "钩子连续 {Count} 次重装失败，Win32 错误码 {Error}，热键已失效",
                _consecutiveReinstallFailures, installError);

            Notify(new Notification(NotificationKind.HookLost, TimeSpan.Zero));

            return;
        }

        _hookHandle = reinstalled;
        _consecutiveReinstallFailures = 0;

        if (!stillValid)
        {
            logger.LogWarning("检出钩子曾被系统摘除（回调超时所致），已重新安装");

            // 摘除期间发生的 KEYUP 一条也没看见，「主键还按着」这个信念已经不可信。
            // 不复位它，用户的下一次热键会被静默吞掉。
            ForgetHeldMainKey();
        }
    }

    /// <summary>
    /// 在钩子回调线程上记录本次回调耗时。超阈值即认为系统可能已经把钩子摘了，挂起一次重装。
    /// 只置标志、不直接 Post：Post 要抢 MessagePumpThread 的生命周期锁，而 StopAndJoin 持着那把锁
    /// 去 Join 本线程，两者会互等到超时。
    /// </summary>
    private void NoteCallbackDuration(long startedAtTimestamp)
    {
        // 整段包住。本方法由 HandleCallback 的 finally 调用，落在那里所有 catch 之外——
        // 此处逃逸的异常一样是进程当场终结。
        try
        {
            if (_reinstallPending)
            {
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAtTimestamp);

            if (elapsed < _callbackOverrunThreshold)
            {
                return;
            }

            _reinstallPending = true;

            logger.LogWarning(
                "钩子回调耗时 {Elapsed}ms，已达重装阈值，系统可能已把钩子摘除，已安排重装",
                elapsed.TotalMilliseconds);
        }
        catch
        {
            // 已是最后一道防线（记日志本身也会抛）。代价只是这一次证据没记上，下一次回调还会再量。
        }
    }

    // ---------- 钩子回调 ----------

    private nint HandleCallback(int nCode, nint wParam, nint lParam)
    {
        // 这里跑在钩子回调线程上。逃出去的托管异常不是「这一次处理失败」，而是进程当场终结
        // （0xC000041D STATUS_FATAL_USER_CALLBACK_EXCEPTION），故整段包住。
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            return Evaluate(nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            try
            {
                logger.LogError(ex, "键盘钩子回调发生未预期异常，本次按键放行");
            }
            catch
            {
                // 记日志本身也可能抛。这里已是最后一道防线，除了咽下去没有别的选择。
            }

            // 出错时放行而不是吞掉：吞掉意味着用户的按键凭空消失。
            return User32.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }
        finally
        {
            NoteCallbackDuration(startedAt);
        }
    }

    private unsafe nint Evaluate(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0 || lParam == nint.Zero)
        {
            return User32.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }

        var matcher = _matcher;

        if (matcher is null)
        {
            return User32.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }

        // Unsafe.Read 而非 Marshal.PtrToStructure：后者每次调用分配 40 字节，而这是回调里唯一
        // 自己制造 GC 压力的地方，一次阻塞式 gen2 GC 就能吃掉超时预算。
        // 用 ReadUnaligned 而非 Read：lParam 必然 8 对齐是观察不是契约，而它在 x64 上同码。
        var data = Unsafe.ReadUnaligned<KBDLLHOOKSTRUCT>((void*)lParam);

        // 放行自己注入的事件，否则会捕获自己发出的按键。这个判定必须排在按 vkCode 分派之前：
        // KEYEVENTF_UNICODE 形态的注入在钩子里 vkCode = VK_PACKET(0xE7)，按码分派认不出它。
        if (data.dwExtraInfo == (nuint)User32.LisscribeInjectedTag)
        {
            return User32.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var isKeyDown = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        var isKeyUp = message is User32.WM_KEYUP or User32.WM_SYSKEYUP;

        if (!isKeyDown && !isKeyUp)
        {
            return User32.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }

        var code = (ushort)data.vkCode;
        var modifier = VirtualKey.ToModifier(code);

        if (modifier is not null)
        {
            // 修饰键：更新状态表后一律放行。吞掉它们会让用户按 Ctrl 时所有应用失灵。
            if (isKeyDown)
            {
                _modifiers.OnKeyDown(modifier.Value);
            }
            else
            {
                _modifiers.OnKeyUp(modifier.Value);
            }

            return User32.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }

        switch (matcher.Evaluate(VirtualKey.GetName(code), isKeyDown, _modifiers.Current))
        {
            case KeyAction.SuppressAndPress:
                // 先补中间键，再干别的：这一下把主键吞掉之后，系统看到的就是一次孤立的修饰键按放。
                SendNeutralTap();

                // 单调时钟。用挂钟时一次 NTP 校时就能让按住时长变成负数，
                // 而 SessionController 拿它跟 HoldThreshold 比。
                _pressedAtTimestamp = Stopwatch.GetTimestamp();
                _mainKeyHeld = true;
                Notify(new Notification(NotificationKind.Pressed, TimeSpan.Zero));
                return 1;

            case KeyAction.SuppressAndRelease:
                _mainKeyHeld = false;
                Notify(new Notification(
                    NotificationKind.Released, Stopwatch.GetElapsedTime(_pressedAtTimestamp)));
                return 1;

            case KeyAction.Suppress:
                return 1;

            default:
                return User32.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }
    }

    /// <summary>
    /// 主键被吞下的那一刻，往系统里补一对未分配码（0xE8）的按放当「中间键」。
    /// </summary>
    /// <remarks>
    /// 被吞的键一个字节也不到 win32k，而修饰键一律放行，于是热键配成 <c>Alt + X</c> 时系统看到的
    /// 序列是 <c>Alt↓ …（空）… Alt↑</c>，与「真按一下 Alt」不可区分，会激活目标应用的菜单栏；
    /// <c>Win + X</c> 同理会弹开始菜单。
    /// 无条件补，不判「这次的修饰键里有没有 Alt 或 Win」：<c>Ctrl+Alt</c> 确实不中招，但那要靠
    /// 「用户配的组合里恰好还有别的修饰键」长期成立，改了默认热键就悄悄复发。
    /// 在回调里同步发是安全的：两个事件的 SendInput 是微秒级，且带
    /// <see cref="User32.LisscribeInjectedTag"/>，回到本回调时会被注入标记检查放行，不会递归。
    /// 失败只记日志：补不上顶多弹一下菜单栏，而在这条路径上抛异常是进程当场终结。
    /// </remarks>
    private void SendNeutralTap()
    {
        try
        {
            var inputs = InputBuilder.BuildNeutralTap();
            var sent = User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

            if (sent != inputs.Length)
            {
                logger.LogWarning(
                    "补中间键失败，只发出 {Sent}/{Total} 个事件，Win32 错误码 {Error}",
                    sent, inputs.Length, Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "补中间键时出错，本次热键可能会激活目标应用的菜单栏。");
        }
    }

    // ---------- 事件派发：单消费者，严格保序 ----------

    private enum NotificationKind
    {
        Pressed,
        Released,
        HookLost,
    }

    private readonly record struct Notification(NotificationKind Kind, TimeSpan HeldFor);

    /// <summary>
    /// 回调线程上只做入队。两个理由：一、回调有硬期限，而订阅者要开麦克风、取前台窗口，慢得多。
    /// 二、次序——线程池两次入队之间没有任何顺序保证，实测 2000 对里 99.45% 次序颠倒，
    /// 而 SessionController 是按 OnPressed → OnReleased 的次序写的。
    /// </summary>
    private void Notify(Notification notification)
    {
        var queue = _notifications;

        if (queue is null)
        {
            return;
        }

        try
        {
            queue.Add(notification);
        }
        catch (InvalidOperationException)
        {
            // 已在关停：CompleteAdding 之后 Add 抛 InvalidOperationException，队列已 Dispose 则抛
            // ObjectDisposedException——后者是前者的子类，一条捕获即可（分开写会撞 CS0160）。
            // 两种情形下丢弃都是对的：此刻没人还在听。
        }
    }

    private void StartDispatcher()
    {
        var queue = new BlockingCollection<Notification>();

        _notifications = queue;

        _dispatcher = new Thread(() => DispatchLoop(queue))
        {
            Name = "lisscribe-hotkey-events",
            IsBackground = true,
        };

        _dispatcher.Start();
    }

    private void DispatchLoop(BlockingCollection<Notification> queue)
    {
        // 单线程串行消费 => 投递次序即执行次序。
        try
        {
            foreach (var notification in queue.GetConsumingEnumerable())
            {
                try
                {
                    switch (notification.Kind)
                    {
                        case NotificationKind.Pressed:
                            Pressed?.Invoke(this, EventArgs.Empty);
                            break;
                        case NotificationKind.Released:
                            Released?.Invoke(this, notification.HeldFor);
                            break;
                        case NotificationKind.HookLost:
                            HookLost?.Invoke(this, EventArgs.Empty);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // 订阅者抛异常不该连累后续事件，更不该崩进程。
                    // 记日志本身也会抛，而它就在 catch 体内——不再包一层，它一抛就逃到外层 catch，
                    // 派发线程当场退出：无人重启、不报 HookLost，而 Notify 继续往一个无人消费的
                    // 无界队列里堆。
                    try
                    {
                        logger.LogError(ex, "热键事件的订阅者抛出异常，已忽略并继续派发");
                    }
                    catch
                    {
                        // 连日志都写不出去了，只能咽下。派发线程活着比记下这一条重要。
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 这条线程上逃逸的异常同样是进程崩。走到这里说明连上面那层嵌套 try 都没兜住，
            // 派发就此停摆——记成 Error 而不是 Warning，它是个不该发生的终局。
            try
            {
                logger.LogError(ex, "热键事件派发线程异常退出，此后不再有热键事件");
            }
            catch
            {
                // 同上。
            }
        }
    }

    private void StopDispatcher()
    {
        var queue = _notifications;
        var dispatcher = _dispatcher;

        _notifications = null;
        _dispatcher = null;

        queue?.CompleteAdding();

        var joined = false;

        if (dispatcher is not null && dispatcher != Thread.CurrentThread)
        {
            joined = dispatcher.Join(DispatcherStopTimeout);

            if (!joined)
            {
                logger.LogWarning("热键事件派发线程未在 {Timeout} 内退出", DispatcherStopTimeout);
            }
        }

        // 只有确认它已经退出才 Dispose：还卡在某个订阅者里时 Dispose 掉队列，
        // 那条线程下一轮取值就会吃到 ObjectDisposedException——在它上面逃逸即进程崩。
        if (joined)
        {
            queue?.Dispose();
        }
    }
}
