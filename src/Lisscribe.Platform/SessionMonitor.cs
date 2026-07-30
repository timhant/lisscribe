using System.Runtime.InteropServices;
using Lisscribe.Core.Abstractions;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Lisscribe.Platform;

/// <summary>
/// 锁屏、休眠、前台窗口变化的通知源。锁屏 / 休眠走 .NET 的 <see cref="SystemEvents"/>，
/// 前台窗口变化走 SetWinEventHook。
/// </summary>
/// <remarks>
/// 前者内部已经维护着一个隐藏的顶级窗口并注册了会话通知，自己再造一个只是把同一件事用最容易
/// 崩的方式重做一遍；SystemEvents 没有前台窗口变化的对应事件，那一件才必须自己来。代价是多一个
/// NuGet 包（<c>Microsoft.Win32.SystemEvents</c>）——net10.0-windows 的共享框架里没有它。
/// <para>
/// 本类自带一条消息泵线程，不在调用方的线程上装 WinEvent 钩子：WINEVENT_OUTOFCONTEXT 的回调是
/// 投到安装线程的消息队列上取的，安装线程没有消息泵就一次也不触发，而 SetWinEventHook 照样返回
/// 有效句柄、lastError = 0。故 <see cref="Start"/> 可以在任何线程上调，不必是 UI 线程。
/// </para>
/// <para>
/// 不用 WINEVENT_SKIPOWNPROCESS：加上它之后本进程自己的窗口抢前台一次事件都不产生。
/// 不加的代价是自家配置窗开关时多几次幂等的 <c>ResyncModifiers</c>；加上的代价是用户解锁后
/// 焦点若落回自家窗口，那一次该做的重同步就被跳过了，且这个事件从此在进程内无法被自动化验证。
/// </para>
/// <para>
/// 三个事件没有一个在 UI 线程上：<see cref="ForegroundWindowChanged"/> 在本类自己的泵线程，
/// <see cref="SessionLocked"/> 与 <see cref="SystemSuspending"/> 在 <c>SystemEvents</c> 自己的
/// 线程。订阅者若要动 Avalonia 的任何东西，必须自己 <c>Dispatcher.UIThread.Post</c>。
/// </para>
/// <para>
/// <see cref="Dispose"/> 的分量比接口看上去重，组合根必须真的调它：漏一次的代价是一条泵线程 +
/// 一个系统级 WinEvent 钩子 + SystemEvents 那条后台线程一起留下。
/// 别在事件处理器里回调本类的 <see cref="Start"/> 或 <see cref="Dispose"/>——前台变化的处理器
/// 跑在泵线程上，而 <c>Dispose</c> 要 <c>Join</c> 那条线程，从它自己身上调过来必然互等到超时。
/// </para>
/// </remarks>
public sealed class SessionMonitor : ISessionMonitor
{
    /// <summary>
    /// 委托必须被静态字段 root 住：<c>[LibraryImport]</c> 生成的 <c>GC.KeepAlive</c> 只覆盖
    /// SetWinEventHook 那一次调用，之后委托即失根，thunk 被回收后系统下一次回调就是一个
    /// catch 不到的 AccessViolation。
    /// 用集合而不是单个静态实例字段：root 委托与「一个进程只能有一个监听器」是两件事，
    /// 后者会让 Start 中途失败留下脏静态，也让「多条测试各建各的实例」直接做不到。
    /// </summary>
    private static readonly List<User32.WinEventProc> RootedCallbacks = [];
    private static readonly Lock RootGate = new();

    /// <summary>
    /// 构造时已过 <see cref="SafeLoggerExtensions.Safe"/>。本类的日志分布在自己的泵线程、
    /// <c>SystemEvents</c> 的线程和系统反向调进来的回调上——任何一条记日志失败都不许把那些线程
    /// 带走，更不许把它挡在 <c>?.Invoke</c> 前面吃掉一次事件。
    /// </summary>
    private readonly ILogger _logger;
    private readonly Lock _lifecycle = new();

    private MessagePumpThread? _pump;
    private User32.WinEventProc? _callback;

    private bool _started;
    private bool _disposed;

    /// <summary>
    /// 装与拆都在泵线程上，但读的人不止那条线程（<see cref="CurrentWinEventHook"/> 是从测试线程
    /// 读它的），故读写一律走 <see cref="Volatile"/>。
    /// </summary>
    private nint _winEventHook;

    public SessionMonitor(ILogger<SessionMonitor> logger) => _logger = logger.Safe();

    /// <summary>
    /// 锁屏 / 注销 / 远程断开。
    /// </summary>
    /// <remarks>
    /// 组合根必须把这个事件与 <see cref="SystemSuspending"/> 一起接到
    /// <c>KeyboardHook.ForgetHeldMainKey()</c> 上，一行也不能少。少了它的后果不是「少一个功能」：
    /// 锁屏时主键的 KEYUP 收不到，<c>_mainKeyHeld</c> 与 HotkeyMatcher 双双卡在 true，
    /// 用户解锁后下一次热键被静默吞掉——无 Pressed、无日志、界面毫无反应，要按到第三次才恢复。
    /// 没把 <c>ForgetHeldMainKey</c> 加到 <c>IHotkeyHook</c> 上是权衡的结果：它是低层钩子
    /// 「看不见被吞掉的 KEYUP」这个盲区的修补动作，不是热键契约的一部分，加上去等于要求每个实现
    /// 与每个测试假件都提供一个多半只能写成空实现的方法；且它与 <c>ResyncModifiers</c> 同层，
    /// 两个同类的状态修复入口分居两层比都放在一层更难懂。
    /// </remarks>
    public event EventHandler? SessionLocked;

    public event EventHandler? SystemSuspending;

    /// <summary>
    /// 前台窗口变化——供组合根触发 <c>KeyboardHook.ResyncModifiers()</c>。
    /// 只接 <c>ResyncModifiers</c>，绝不要接 <c>ForgetHeldMainKey</c>：按住热键期间复位主键按住态
    /// 会让 Released 根本不来，看门狗下一跳强行收尾。而前台窗口随时可能因为自家的悬浮条、
    /// 托盘气泡、通知、IME 候选窗而变化——接错就是用户说到一半被自己人截断，且毫无提示。
    /// </summary>
    public event EventHandler? ForegroundWindowChanged;

    /// <summary>静态 GC root 表里现存的委托数。供测试验证 Start 幂等与 Dispose 撤根。</summary>
    internal static int RootedCallbackCount
    {
        get { lock (RootGate) { return RootedCallbacks.Count; } }
    }

    /// <summary>供测试往泵线程上塞一个堵住它的动作，制造「Join 超时」的现场。</summary>
    internal MessagePumpThread? PumpForTest => _pump;

    /// <summary>
    /// 让 <see cref="Start"/> 在造出消息泵之前失败。只有测试会置它，生产恒为 false。
    /// 存在的理由是给 <c>StopCore</c> 里那个 <c>?? false</c> 立一根回归钉子——那个默认值只在
    /// 「<c>_started</c> 为真而 <c>_pump</c> 为 null」时才被读到，而生产里唯一能到达那个状态的
    /// 就是这条路：委托已经 root 上了、泵还没造出来。
    /// </summary>
    internal bool FailBeforeCreatingPumpForTest { get; set; }

    /// <summary>
    /// 在「泵已停、收尾尚未做」之间抛一个异常。只有测试会置它，生产恒为 null。
    /// 给 <c>StopCore</c> 的 <c>finally</c> 立回归钉子。刻意放在 <c>StopAndJoin</c> 之后——
    /// 放在之前的话泵就真的没停，会留下一条挂着 WinEvent 钩子的孤儿线程漏进后面的测试。
    /// </summary>
    internal Action? ThrowAfterStoppingThePumpForTest { get; set; }

    /// <summary>
    /// 让 <c>SetWinEventHook</c> 交出一个失败句柄（0）。只有测试会置它，生产恒为 false。
    /// 钉两件事：「装不上是功能降级不是启动失败」这条明写的决定，以及那条路上的日志不许把
    /// <see cref="Start"/> 变成失败。它只替换那一次系统调用的返回值，之后的每一行都是生产代码。
    /// </summary>
    internal bool FailWinEventHookInstallForTest { get; set; }

    /// <summary>
    /// 当前 WinEvent 钩子句柄。供测试判断泵线程有没有真的收完尾——拆钩子是 onStop 干的活，
    /// 句柄归零才说明那条线程走完了 <c>Run()</c> 的 finally。
    /// </summary>
    internal nint CurrentWinEventHook => Volatile.Read(ref _winEventHook);

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_started)
            {
                return;
            }

            _started = true;

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _callback = HandleForegroundEvent;

            lock (RootGate)
            {
                RootedCallbacks.Add(_callback);
            }

            if (FailBeforeCreatingPumpForTest)
            {
                throw new InvalidOperationException("测试缝：假装消息泵还没造出来就失败了");
            }

            var pump = new MessagePumpThread(
                "lisscribe-session-monitor", InstallWinEventHook, UninstallWinEventHook, _logger);

            try
            {
                pump.Start();
            }
            catch
            {
                // 起不来的泵也必须收尾，否则就是一条挂着 WinEvent 钩子的孤儿线程。
                // 先认领再收尾：自己 Dispose 会丢掉 StopAndJoin 的返回值，
                // 而那个返回值正是「能不能撤 GC root」的唯一依据。
                _pump = pump;

                StopCore();

                throw;
            }

            _pump = pump;
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            StopCore();

            // 置位在 StopCore 之后：Dispose 自己不该被 ObjectDisposedException 挡住。
            _disposed = true;
        }
    }

    /// <summary>
    /// 调用方须已持有 <see cref="_lifecycle"/>。
    /// </summary>
    /// <remarks>
    /// 形状要点：默认落在「不撤 root」那一侧，且收尾一定跑完。把「泵停干净了没有」写成
    /// <c>?? true</c> 再紧接着 <c>_pump = null</c>，就有这么一条通往不可 catch 的 AccessViolation
    /// 的路：中途任何一步抛出 → 异常逃出 <see cref="Dispose"/>，此刻 <c>_pump</c> 已置 null 而
    /// <c>_started</c> 还是 true → 调用方再 Dispose 一次 → <c>?? true</c> 得出「停干净了」→
    /// 撤 root，而钩子当时正是因为泵没停干净才可能仍挂在系统里指着这个委托。
    /// 堵死它的是两件事，且各自单独就够：<c>?? false</c>（问不出结论就当没停干净）与收尾全部
    /// 搬进 <c>finally</c>（<c>_callback</c> 与 <c>_started</c> 不会半途留在中间态）。
    /// </remarks>
    private void StopCore()
    {
        if (!_started)
        {
            return;
        }

        var pumpStoppedCleanly = false;

        try
        {
            // 退订是必修不是清洁工作。SystemEvents 的事件是静态的：忘了退订，这个实例连同它的
            // 订阅者图就永久留在进程里——配置窗改一次热键重建一个监听器，旧的那个还在替一个
            // 早已不用的会话取消录音。
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            // StopAndJoin 返回 true 才意味着 UninstallWinEventHook 已经跑完——它在泵线程 Run() 的
            // finally 里，Join 超时只是放弃等待，线程照旧活着，钩子照旧挂着。
            // 取 false 而不是 true：_pump 为 null 只可能是「泵还没造出来就失败了」那条路，那时钩子
            // 根本没装、撤 root 其实安全，这里仍白白泄漏一个委托——因为这个默认值同时也是
            // 「问不出结论」时的答案，而那时它必须是 false。
            pumpStoppedCleanly = _pump?.StopAndJoin() ?? false;

            ThrowAfterStoppingThePumpForTest?.Invoke();
        }
        finally
        {
            _pump = null;

            ReleaseCallbackRoot(pumpStoppedCleanly);

            _started = false;
        }
    }

    private void ReleaseCallbackRoot(bool pumpStoppedCleanly)
    {
        if (_callback is null)
        {
            return;
        }

        if (pumpStoppedCleanly)
        {
            lock (RootGate)
            {
                RootedCallbacks.RemoveAll(p => ReferenceEquals(p, _callback));
            }
        }
        else
        {
            // 钩子可能仍挂在系统里指着这个委托，此刻撤 root 就是一个 catch 不到的
            // AccessViolation。宁可泄漏几十字节。这一句的安全来自构造函数里那道 Safe()。
            _logger.LogWarning(
                "会话监听的消息泵线程未干净退出，WinEvent 钩子可能仍挂在系统里；"
                + "故意保留回调委托的 GC root，宁可泄漏也不冒 AccessViolation 的险");
        }

        _callback = null;
    }

    // ---------- 泵线程上的两件事：装、拆 ----------

    private void InstallWinEventHook()
    {
        // 测试缝落在「装出来的句柄是什么」这个数据上，不落在控制流上：写成
        // `if (缝) { …; return; }` 的话真正的失败分支一次也走不到，而缝自己写的日志会让
        // 阳性对照照样绿——「装不上是降级不是启动失败」于是零覆盖。
        var installed = FailWinEventHookInstallForTest
            ? nint.Zero
            : User32.SetWinEventHook(
                User32.EVENT_SYSTEM_FOREGROUND,
                User32.EVENT_SYSTEM_FOREGROUND,
                nint.Zero,
                _callback!,
                idProcess: 0,
                idThread: 0,
                User32.WINEVENT_OUTOFCONTEXT);

        Volatile.Write(ref _winEventHook, installed);

        if (installed == nint.Zero)
        {
            // 不抛。前台变化只驱动一次幂等的修饰键重同步，装不上是功能降级不是启动失败；
            // 为它把整个程序拦在门外，代价远大于收益。锁屏 / 休眠两条链不受影响。
            _logger.LogWarning(
                "SetWinEventHook 失败，Win32 错误码 {Error}；前台窗口变化将无人通知，"
                + "修饰键状态表只能等下一次热键自然纠正",
                Marshal.GetLastWin32Error());
        }
    }

    private void UninstallWinEventHook()
    {
        var installed = Volatile.Read(ref _winEventHook);

        if (installed != nint.Zero)
        {
            User32.UnhookWinEvent(installed);
            Volatile.Write(ref _winEventHook, nint.Zero);
        }
    }

    // ---------- 三个回调，全部整段包 try/catch ----------

    /// <summary>
    /// 跑在本类自己的泵线程上，而且是系统反向调进来的，而 <c>ForegroundWindowChanged?.Invoke</c>
    /// 调的正是外部订阅者的代码。
    /// </summary>
    /// <remarks>
    /// 这个 try/catch 是承重的：让异常逃出这个回调，进程随即以 0xC000041D
    /// （STATUS_FATAL_USER_CALLBACK_EXCEPTION）结束。别被测试的表现骗了——同一个变异在 xunit 的
    /// 测试宿主里进程反而活得下来，也就是说这里一旦拆掉，测试宿主的表现比真实程序温和得多，
    /// 而用户拿到的是「切了个窗口，程序没了」。
    /// 订阅者在这里做慢活儿只会拖慢本类自己的泵线程，不会碰到键盘钩子那条泵线程——两条是分开的。
    /// </remarks>
    private void HandleForegroundEvent(
        nint hookHandle, uint eventType, nint window,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        try
        {
            ForegroundWindowChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogSwallowed(ex, "前台窗口变化的订阅者抛出异常，已忽略");
        }
    }

    /// <summary>
    /// 跑在 <see cref="SystemEvents"/> 自己的线程上。
    /// 这里的 try/catch 与前台回调那处不是同一回事：拿掉它进程不崩、后续事件照来，因为
    /// <c>Microsoft.Win32.SystemEvents</c> 自己的 <c>RaiseEvent</c> 与 <c>WindowProc</c> 各有一层
    /// <c>catch (Exception)</c>。它仍要留着，理由变成两条：那是别人实现里的细节不是契约，
    /// 随时可以变；而且没有它连日志都没有——订阅者悄悄炸了，我们一个字都不知道。
    /// </summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        try
        {
            // 只认「用户离开了这台机器」的那几种。解锁 / 远程接入也是 SessionSwitch，
            // 不过滤就会在用户回到机器前的那一刻取消他刚开口的这次录音。
            if (
                e.Reason is not (SessionSwitchReason.SessionLock
                or SessionSwitchReason.SessionLogoff
                or SessionSwitchReason.RemoteDisconnect
                or SessionSwitchReason.ConsoleDisconnect)
                )
            {
                return;
            }

            // 这一句挡在 Invoke 前面，它抛出就等于把事件静默吃掉：ForgetHeldMainKey 不跑 →
            // 锁屏后主键卡在 held → 用户解锁后下一次热键被静默吞掉。不靠「挪到 Invoke 之后」
            // 解决——那样订阅者一抛就丢日志，只是换了个洞；靠的是构造函数里那道 Safe()。
            _logger.LogInformation("会话中断：{Reason}", e.Reason);

            SessionLocked?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogSwallowed(ex, "处理会话切换事件时发生未预期异常，已忽略");
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            // Resume / StatusChange 不算——只有「即将睡下去」才意味着 KEYUP 再也不会来。
            if (e.Mode != PowerModes.Suspend)
            {
                return;
            }

            // 同上：挡在 Invoke 前面的日志不许抛，否则休眠事件被静默吃掉。
            _logger.LogInformation("系统即将休眠");

            SystemSuspending?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogSwallowed(ex, "处理电源状态变化事件时发生未预期异常，已忽略");
        }
    }

    private void LogSwallowed(Exception ex, string message) => _logger.LogError(ex, "{Message}", message);
}
