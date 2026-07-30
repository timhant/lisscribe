using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Platform;

/// <summary>
/// 带消息泵的专用线程，并能把任意动作派回自己身上执行。
/// </summary>
/// <remarks>
/// 为何需要它：MSDN 载明低层键盘钩子「is called in the context of the thread that installed it…
/// the thread that installed the hook must have a message loop」。在普通线程或线程池线程上装
/// WH_KEYBOARD_LL 而不跑 GetMessage 循环，回调一次也不会触发，且没有任何报错。
/// 为何还要 <see cref="Post"/>：重装钩子这件事本身也必须发生在有消息泵的那条线程上。实测在
/// 线程池线程上 Unhook + SetWindowsHookEx，句柄有效、lastError = 0，而回调 0 次；把同样的重装
/// 挪回泵线程即恢复。也就是说把存活自检写在 Timer 回调里（线程池线程）会让自检本身成为杀死
/// 钩子的凶手。
/// </remarks>
internal sealed class MessagePumpThread : IDisposable
{
    /// <summary>派活消息。用 WM_APP 段，与系统消息和窗口类自定义消息都不冲突。</summary>
    private const uint WM_RUN_ACTION = User32.WM_APP;

    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Action _onStart;
    private readonly Action _onStop;
    private readonly ILogger _logger;
    private readonly TimeSpan _startTimeout;
    private readonly ConcurrentQueue<Action> _pending = new();

    // Post 与 Dispose 互斥：没有这层互斥，Post 可能在「线程已死、id 已被系统回收再分配」之后
    // 才真正调用 PostThreadMessage，把消息塞进一个不相干线程的队列。
    private readonly Lock _lifetime = new();

    private uint _threadId;
    private bool _started;
    private bool _disposed;
    private bool _stoppedCleanly;

    public MessagePumpThread(
        string name,
        Action onStart,
        Action onStop,
        ILogger logger,
        TimeSpan? startTimeout = null)
    {
        _onStart = onStart;
        _onStop = onStop;

        // 幂等：宿主传进来的通常已经包过了。这里再包一道，是为了让直接 new 出来的实例同样安全，
        // 而不必靠调用方记得。
        _logger = logger.Safe();
        _startTimeout = startTimeout ?? DefaultStartTimeout;

        _thread = new Thread(Run)
        {
            Name = name,

            // 后台线程。判据是「进程该不该因为这条线程还活着而不退出」：钩子是个服务，不是进程
            // 存在的理由；WM_QUIT 万一没送到，也不该把整个程序钉在内存里下不了班。
            IsBackground = true,
        };

        // 单线程单元模型是带消息循环的线程的惯例选择。本类不创建任何 COM 对象，但 STA 并非可有
        // 可无：COM 的 STA 初始化会把线程的消息队列一并建好，Run() 里那次 PeekMessage 正因此才成了
        // 纯保险。改成 MTA 之前先读那两处注释。
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>
    /// 起线程并等它就绪。<c>onStart</c> 抛出时原样重抛给调用方；超时抛
    /// <see cref="TimeoutException"/>。两者都不会让异常在泵线程上逃逸——那是进程级后果：
    /// 泵线程上未捕获的托管异常打印一行 <c>Unhandled exception … at Thread.StartCallback()</c>
    /// 之后直接终结进程，调用方连一个返回都等不到。
    /// </summary>
    public void Start()
    {
        _started = true;

        _thread.Start();

        try
        {
            _ready.Task.WaitAsync(_startTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"消息泵线程 \"{_thread.Name}\" 未在 {_startTimeout} 内就绪");
        }
    }

    /// <summary>
    /// 泵线程的原生线程 id，0 表示还没就绪。供测试直接把 WM_QUIT 投给它，制造「线程已安静死掉、
    /// 而本类并不知情」那个现场。
    /// </summary>
    internal uint ThreadIdForTest => Volatile.Read(ref _threadId);

    /// <summary>
    /// 把一个动作派到泵线程上执行。返回 false 表示这条线程已经不在了，动作不会被执行。
    /// 动作在泵线程上抛出的异常只落日志——泵线程死了钩子也就跟着没了。
    /// </summary>
    public bool Post(Action action)
    {
        lock (_lifetime)
        {
            if (_disposed || _threadId == 0)
            {
                return false;
            }

            // 先入队再发信号：反过来的话，泵线程可能在动作入队之前就被唤醒并空转一轮，
            // 那个动作要等到下一条消息到来才会被执行。
            _pending.Enqueue(action);

            if (User32.PostThreadMessage(_threadId, WM_RUN_ACTION, nint.Zero, nint.Zero))
            {
                return true;
            }

            // 这一句抛出会让 Post 抛而不是返回 false，而那是一条通往进程终结的接力的第一棒：
            // 泵线程若已安静死掉（见 PumpUntilQuit 里 GetMessage == -1 那一支），PostThreadMessage
            // 必失败（err 1444）→ 走到这里 → 裸日志抛 → 异常逃出 Post → 落到
            // KeyboardHook.RequestHealthCheck 的 catch → 那里的日志若也抛，就逃出 Timer 的回调，
            // 进程当场终结。保障来自构造函数里那道 Safe()。
            _logger.LogWarning(
                "向消息泵线程 \"{Name}\" 投递动作失败，Win32 错误码 {Error}",
                _thread.Name, Marshal.GetLastWin32Error());

            _pending.TryDequeue(out _);

            return false;
        }
    }

    private void Run()
    {
        // 顺序要紧：先把消息队列逼出来，再把线程 id 交出去。
        // 老实说：这一行在本类当前的形态下走不到——构造函数里 SetApartmentState(STA)，COM 的 STA
        // 初始化已经把消息队列建好了（实测删掉本行测试全绿）。留着它是防日后有人把 STA 改成
        // MTA：那时队列确实缺席，实测 MTA 下 PostThreadMessage 全部失败。
        User32.PeekMessage(out _, nint.Zero, User32.WM_USER, User32.WM_USER, User32.PM_NOREMOVE);
        Volatile.Write(ref _threadId, Kernel32.GetCurrentThreadId());

        try
        {
            _onStart();
        }
        catch (Exception ex)
        {
            // 交给 Start() 重抛。这里绝不能让它逃出去——那是进程崩，不是启动失败。
            // onStop 不跑：onStart 没成功，没有任何东西需要释放，跑它只会对着没申请到的资源
            // 做释放。
            _ready.TrySetException(ex);

            return;
        }

        _ready.TrySetResult();

        try
        {
            PumpUntilQuit();
        }
        finally
        {
            try
            {
                _onStop();
            }
            catch (Exception ex)
            {
                // 这一句在 Run() 的 finally 里，是本线程最外层的最后一句，它自己抛出就是异常逃出
                // 线程入口 = 进程终结。保障来自构造函数里那道 Safe()。
                _logger.LogError(ex, "消息泵线程 \"{Name}\" 的收尾动作抛出异常", _thread.Name);
            }
        }
    }

    private void PumpUntilQuit()
    {
        while (true)
        {
            // GetMessage 三种返回都要分开处理，故它被声明成 int 而非 BOOL：声明成 bool 时出错的
            // -1 被封送成 true，于是 `while (GetMessage(...))` 在出错后永远转下去——线程 100%
            // 占一个核且不退出，而这条线程正是钩子的宿主。
            var result = User32.GetMessage(out var msg, nint.Zero, 0, 0);

            if (result == 0)
            {
                return;
            }

            if (result == -1)
            {
                // 走到这里泵线程就安静地结束了，而 _disposed 仍为 false、_threadId 仍非零——
                // 此后每一次 Post 都会走 PostThreadMessage 失败那一支（err 1444）。KeyboardHook 的
                // 自检定时器 5 秒一跳，随后就会踩上去，故那条链上的每一句日志都不许抛。
                _logger.LogError(
                    "消息泵线程 \"{Name}\" 的 GetMessage 出错，Win32 错误码 {Error}，停止泵送",
                    _thread.Name, Marshal.GetLastWin32Error());

                return;
            }

            if (msg.message == WM_RUN_ACTION)
            {
                DrainPending();

                continue;
            }

            User32.TranslateMessage(in msg);
            User32.DispatchMessage(in msg);
        }
    }

    private void DrainPending()
    {
        // 一条消息可能对应多个已入队的动作（两次 Post 之间泵线程没被调度到），故这里排空而不是
        // 只取一个；多出来的消息会碰上空队列，无害。
        while (_pending.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 存活自检就是靠 Post 跑的。这一句抛出会把泵线程连同它宿主的钩子一起赔进去，
                // 而记日志失败（磁盘满、sink 自己抛）本不该有这个后果。
                // 保障来自构造函数里那道 Safe()。
                _logger.LogError(ex, "消息泵线程 \"{Name}\" 上的投递动作抛出异常", _thread.Name);
            }
        }
    }

    /// <summary>
    /// 停泵并等它退出。返回 false 表示线程没在期限内退出，此时 <c>onStop</c> 尚未跑完——
    /// 它在 <see cref="Run"/> 的 finally 里，而这里超时只是放弃等待，线程照旧活着。
    /// 调用方必须据此决定还能不能释放「泵线程仍可能在用」的东西。对键盘钩子而言，那就是
    /// HOOKPROC 委托的 GC root：钩子的卸载正是 <c>onStop</c> 干的活，没跑完就意味着钩子仍挂在
    /// 系统里指着那个委托，此时撤 root 的下一次按键就是一个 catch 不到的 AccessViolation。
    /// 宁可泄漏几十字节。
    /// </summary>
    public bool StopAndJoin()
    {
        lock (_lifetime)
        {
            if (_disposed)
            {
                return _stoppedCleanly;
            }

            _disposed = true;

            if (_threadId != 0
                && !User32.PostThreadMessage(_threadId, User32.WM_QUIT, nint.Zero, nint.Zero))
            {
                // 这里是本类唯一真会「静默」的地方：投不进去，泵就永远等不到退出信号，
                // 只能靠下面的 Join 超时兜住。
                _logger.LogWarning(
                    "向消息泵线程 \"{Name}\" 投递 WM_QUIT 失败，Win32 错误码 {Error}",
                    _thread.Name, Marshal.GetLastWin32Error());
            }

            if (!_started)
            {
                // 从未 Start 过的 Thread 一 Join 就抛 ThreadStateException。没起来过也就没有任何
                // 东西需要它收尾，算干净停止。
                _stoppedCleanly = true;

                return true;
            }

            _stoppedCleanly = _thread.Join(StopTimeout);
            if (!_stoppedCleanly)
            {
                _logger.LogWarning(
                    "消息泵线程 \"{Name}\" 未在 {Timeout} 内退出，其收尾动作尚未跑完",
                    _thread.Name, StopTimeout);
            }

            return _stoppedCleanly;
        }
    }

    public void Dispose() => StopAndJoin();
}
