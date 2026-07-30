using System.Runtime.InteropServices;

namespace Lisscribe.Platform.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL;
    public ushort wParamH;
}

/// <summary>
/// INPUT 的联合体。本项目一个鼠标/硬件事件也不发，但 <see cref="MOUSEINPUT"/> 不能删：
/// x64 上三个成员分别是 32 / 24 / 8 字节，最大者 MOUSEINPUT 决定联合体大小，删掉它 INPUT 便从
/// 40 字节缩到 32，SendInput 拿到对不上的 cbSize 后既不报错也不注入，只返回 0。
/// <see cref="HARDWAREINPUT"/> 删不删都是 40，留着只为忠实于原生原型。
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public uint type;
    public InputUnion union;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public uint vkCode;
    public uint scanCode;

    /// <summary>
    /// 别拿 LLKHF_EXTENDED(0x01) 分左右：右 Shift 在低层钩子里恒带该位（即便 SendInput 根本
    /// 没置它），而同一次事件在 Raw Input 层却不带 E0。两层对同一个键的说法不一致，靠这一位
    /// 判左右，右 Shift 必错。要分左右就看 <see cref="scanCode"/>，或直接用 vkCode 的左右分离码。
    /// </summary>
    public uint flags;
    public uint time;

    /// <summary>
    /// 原生同为 ULONG_PTR，此处是 <c>nuint</c> 而 <see cref="KEYBDINPUT.dwExtraInfo"/> 是 <c>nint</c>。
    /// 与 <see cref="User32.LisscribeInjectedTag"/> 比较时须显式转换（正数常量，转换无损）；
    /// 类型不同会当场编译报错，不会静默比错。
    /// </summary>
    public nuint dwExtraInfo;
}

/// <summary>
/// user32.dll 的 P/Invoke 声明，全部采用 <c>[LibraryImport]</c>——包括收委托的
/// <see cref="SetWindowsHookEx"/> 与 <see cref="SetWinEventHook"/>。
/// </summary>
/// <remarks>
/// 「源生成互操作不支持委托封送、会报 SYSLIB1051」这个通行说法与实测相反：两条收委托的声明
/// 0 Warning 0 Error 展开成功，SYSLIB1051 管的是运行时封送（例如 <c>[Out] char[]</c> 这类参数）。
/// 生成的封送代码末尾有一句 <c>GC.KeepAlive(lpfn)</c>，但它只覆盖这一次调用：钩子安装成功后
/// 委托即失去根，之后 GC 回收 thunk，按键立刻 AccessViolation（catch 不到）。换 DllImport 也一样
/// ——安装方必须用静态字段 root 住委托。
/// </remarks>
internal static partial class User32
{
    public const int WH_KEYBOARD_LL = 13;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_USER = 0x0400;

    /// <summary>应用自定义消息的起点。本项目拿它给消息泵线程派活（见 MessagePumpThread）。</summary>
    public const uint WM_APP = 0x8000;

    public const uint PM_NOREMOVE = 0x0000;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// 点击穿透的另一半。<see cref="WS_EX_TRANSPARENT"/> 单独一位换不来穿透——命中测试的那层
    /// 语义属于分层窗口，见 <c>OverlayWindowStyler</c>。
    /// </summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary><c>SetLayeredWindowAttributes</c> 的 <c>dwFlags</c>：按 bAlpha 做整窗透明度。</summary>
    public const uint LWA_ALPHA = 0x00000002;

    /// <summary>
    /// 本程序注入的事件都带这个标记，供钩子回调识别并放行——否则会捕获自己发出的按键。
    /// VK 形态与 KEYEVENTF_UNICODE 形态的注入事件在钩子回调里都读得到它，既没被系统清零，
    /// 也没被 LLKHF_INJECTED 取代（两者同时置位）。
    /// 值本身不必改：AutoHotkey 的 KEY_IGNORE 与之不冲突，且撞车后果良性（顶多几下热键不响应，
    /// 不崩、不误上屏）。也不必加「LLKHF_INJECTED 且标记相符」的与判定——同魔数的注入方事件
    /// 同样带 INJECTED，挡不住。
    /// </summary>
    public const nint LisscribeInjectedTag = 0x4C_49_53_53;

    public delegate nint HookProc(int nCode, nint wParam, nint lParam);

    /// <summary>
    /// 装 WH_KEYBOARD_LL 时 <paramref name="hMod"/> 传 0 即可：流传甚广的
    /// ERROR_HOOK_NEEDS_HMOD(1428) 不复现，x64/x86 均返回有效句柄且 lastError=0。
    /// 这是单机结论，换机器若真撞上 1428，退路是补一条 GetModuleHandleW。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    public static partial nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    // Get/SetWindowLongPtr 成功时也可能返回 0，只能靠「调用前清零 last error」区分成败。
    // LibraryImport 生成的代码在调用前会执行 Marshal.SetLastSystemError(0)，恰好满足这个前提，
    // 故 SetLastError = true 在这两条上不是摆设。
    //
    // 另：这两个入口点只有 64 位 user32.dll 才导出（32 位下它们是映射到 *LongW 的宏），
    // 在 x86 上抛 EntryPointNotFoundException。本项目锁死 win-x64，故不做条件分派。
    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    /// <summary>
    /// 只有对已置 <see cref="WS_EX_LAYERED"/> 的窗口才成功；否则 <c>ERROR_INVALID_PARAMETER</c>。
    /// 这里返回的是货真价实的 BOOL，不像上面两个还要靠 last error 分辨成败。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(
        nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// 原生原型是 BOOL，此处故意声明成 int。GetMessage 有三种返回：&gt;0 收到消息、
    /// 0 收到 WM_QUIT、-1 出错。声明成 bool 时 -1 被封送成 <c>true</c>，于是消息泵的
    /// <c>while (GetMessage(...))</c> 在出错后永远转下去——线程 100% 占一个核且不退出，
    /// 而这条线程正是钩子的宿主。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>
    /// 只用来把线程的消息队列逼出来，不真的取消息。
    /// MSDN：投给尚未创建消息队列的线程的消息会丢失，而队列是线程首次调用某个 user32 函数时
    /// 才惰性创建的，给的解法就是先 PeekMessage 一次。
    /// 两点要说准：一、丢失不是静默的，PostThreadMessage 会返回 FALSE 且 GetLastError = 1444；
    /// 二、<see cref="MessagePumpThread"/> 走 STA，COM 的 STA 初始化已把队列建好，
    /// 本调用在那里是防日后 STA 被改成 MTA 的保险，不是当下的刚需。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(
        out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    public const uint CF_UNICODETEXT = 13;

    /// <summary>
    /// 传 0 与 MSDN 明文相抵，故本项目尽量不传 0，见 <see cref="ClipboardTextInjector"/> 的
    /// owner 参数。
    /// </summary>
    /// <remarks>
    /// 文档在 <c>EmptyClipboard</c> 与 <c>SetClipboardData</c> 两处写着同一句：
    /// 打开剪贴板时传 NULL 窗口句柄，<c>EmptyClipboard</c> 会成功但把 owner 置空，
    /// 而这会导致 <c>SetClipboardData</c> 失败。
    /// 实测与这句话相反（传 0 时 <c>SetClipboardData</c> 照样成功、读回内容正确），
    /// 但正因为相反才更不能赌。
    /// <para>
    /// 另有一条文档没写的：独占只在传真 HWND 时才成立——持有方传 0 时，另一进程不但
    /// <c>OpenClipboard</c> 得成，连 <c>EmptyClipboard</c> + <c>SetClipboardData</c> 都成，
    /// 读回来是闯入者写的内容；持有方传真 HWND 时，另一进程立刻拿到 err 5（ACCESS_DENIED）。
    /// 由此两条推论：<c>ClipboardTextInjector</c> 那圈重试不是多余的（浏览器、Office 这些真会
    /// 短暂持有剪贴板的应用都是拿窗口开的，撞上就是 err 5）；而传真 HWND 同时买到合乎契约与
    /// 真独占两样。
    /// </para>
    /// </remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    /// <summary>
    /// 成功后句柄的所有权归系统，调用方不得再 GlobalFree；失败（返回 0）时所有权还在调用方
    /// 手上，必须自己释放，否则每失败一次泄漏一块。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint memory);

    /// <summary>
    /// 返回的句柄归剪贴板所有，不许保存也不许释放，内容要当场拷走。
    /// 本机上它返回的一律是固定内存（<c>handle == GlobalLock(handle)</c>），故直接
    /// <c>Marshal.PtrToStringUni</c> 今天也读得对；仍照 MSDN 走一遍 GlobalLock，因为那个相等
    /// 是这一版 Windows 的实现细节、不是契约，而赌错的代价是读一段野内存。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetClipboardData(uint format);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>
    /// 回调不注入目标进程，而是投到安装线程的消息队列上取。代价是：安装线程没有消息泵，
    /// 回调一次也不触发，而 SetWinEventHook 照样返回有效句柄、lastError = 0。
    /// 故 <c>SessionMonitor</c> 自带一条 <see cref="MessagePumpThread"/>，不赌调用方的线程。
    /// </summary>
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public delegate void WinEventProc(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    /// <summary>
    /// <paramref name="lpfnWinEventProc"/> 与 <see cref="SetWindowsHookEx"/> 的委托同理：
    /// 生成的 <c>GC.KeepAlive</c> 只覆盖这一次调用，安装方必须用静态字段 root 住它。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hWinEventHook);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;

    // 原生此处是一个 POINT 结构体。摊平成两个 int 与逐字段布局完全等价，
    // 且省掉一个只为它存在的类型。
    public int ptX;
    public int ptY;
}
