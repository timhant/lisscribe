using System.Runtime.InteropServices;
using Lisscribe.Platform.Native;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 排在被测钩子**之后**的链上观察者：它看得见什么，就是「什么被放行了」的判据。
///
/// 低层钩子按「后装的先调用」排序，故本类必须在 <c>KeyboardHook.Start</c> **之前**装上。
/// 被测钩子返回 1 吞掉的事件，本类一条也收不到。
///
/// **本类刻意不用 <see cref="MessagePumpThread"/>，自己起线程、自己跑 GetMessage 循环。**
/// 预言机的取样范围必须独立于被测对象：若观察者也架在 MessagePumpThread 上，那条线程一旦
/// 出问题（比如回调根本不触发），观察者就会「什么也没看到」，而「什么也没看到」恰好是
/// 「主键被吞掉了」的通过条件——一处故障能同时喂饱被测方与预言机，测试便静默变成空测。
/// 用它的每条测试还须配一个阳性对照（放行一个从来不是热键的键并断言观察者看见了），
/// 那才能证明观察者当时确实活着。
/// </summary>
internal sealed class HookChainObserver : IDisposable
{
    // 委托必须被静态字段 root 住，理由与生产代码同：[LibraryImport] 生成的 GC.KeepAlive
    // 只覆盖 SetWindowsHookEx 这一次调用，返回即失根，此后按一次键就是 AccessViolation。
    private static readonly List<User32.HookProc> RootedProcs = [];
    private static readonly Lock RootGate = new();

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly List<(uint VkCode, int Message)> _seen = [];
    private readonly Lock _gate = new();

    private User32.HookProc? _proc;
    private nint _hook;
    private uint _threadId;

    public HookChainObserver()
    {
        _thread = new Thread(Run) { Name = "test-chain-observer", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("链上观察者未能在期限内装好钩子");
        }

        if (_hook == nint.Zero)
        {
            throw new InvalidOperationException(
                $"链上观察者装钩子失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    public bool Saw(ushort virtualKey)
    {
        lock (_gate)
        {
            return _seen.Any(e => e.VkCode == virtualKey);
        }
    }

    public int CountOf(ushort virtualKey)
    {
        lock (_gate)
        {
            return _seen.Count(e => e.VkCode == virtualKey);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _seen.Clear();
        }
    }

    /// <summary>
    /// 按到达顺序的完整快照。
    ///
    /// **只数个数验不出形状**：给 Ctrl+V 外面夹一层 Alt（<c>Alt↓ Ctrl↓ V↓ V↑ Ctrl↑ Alt↑</c>）
    /// 之后，「V 出现 2 次、Ctrl 至少 2 次」两条断言照样全绿——而那正是「注入前没释放修饰键」
    /// 的形状，目标窗口一个字符也粘不进去。要拦它就得看序列。
    /// </summary>
    public IReadOnlyList<(uint VkCode, int Message)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _seen];
        }
    }

    /// <summary>低层钩子里，按住 Alt 时同一个键报的是 WM_SYSKEYDOWN 而不是 WM_KEYDOWN，两者都算「按下」。</summary>
    public static bool IsKeyDown((uint VkCode, int Message) entry) =>
        entry.Message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;

    public static string Describe(IReadOnlyList<(uint VkCode, int Message)> entries) =>
        "[" + string.Join(", ", entries.Select(
            e => $"0x{e.VkCode:X2}{(IsKeyDown(e) ? "↓" : "↑")}")) + "]";

    private void Run()
    {
        User32.PeekMessage(out _, nint.Zero, User32.WM_USER, User32.WM_USER, User32.PM_NOREMOVE);
        _threadId = Kernel32.GetCurrentThreadId();

        _proc = Callback;
        lock (RootGate)
        {
            RootedProcs.Add(_proc);
        }

        _hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _proc, nint.Zero, 0);
        _ready.Set();

        while (User32.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            User32.TranslateMessage(in msg);
            User32.DispatchMessage(in msg);
        }

        if (_hook != nint.Zero)
        {
            User32.UnhookWindowsHookEx(_hook);
            _hook = nint.Zero;
        }
    }

    private nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            lock (_gate)
            {
                _seen.Add((data.vkCode, (int)wParam));
            }
        }

        // 观察者永远不吞：它只负责如实记录链上还剩下什么。
        return User32.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        // 与生产代码同一条规矩（KeyboardHook.StopCore）：**线程没退干净就不许撤 GC root**。
        // 观察者线程若还活着，它的钩子就还挂在系统里指着这个委托，撤了 root 之后 GC 回收
        // thunk，下一次按键是一个不可 catch 的 AccessViolation——而这里是测试宿主进程。
        // 宁可泄漏几十字节。
        var stoppedCleanly = true;
        if (_threadId != 0)
        {
            User32.PostThreadMessage(_threadId, User32.WM_QUIT, nint.Zero, nint.Zero);
            stoppedCleanly = _thread.Join(TimeSpan.FromSeconds(3));
        }

        if (_proc is not null && stoppedCleanly)
        {
            lock (RootGate)
            {
                RootedProcs.RemoveAll(p => ReferenceEquals(p, _proc));
            }
        }

        _ready.Dispose();
    }
}
