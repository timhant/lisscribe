using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lisscribe.Platform.Native;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 测试用的真窗口：自己注册窗口类、自己起带消息泵的 STA 线程、自己建 HWND。
/// </summary>
/// <remarks>
/// 必须自建：测试宿主是控制台进程，<c>Process.GetCurrentProcess().MainWindowHandle</c> 恒为 0，拿它
/// 取窗口的测试一次也跑不到。窗口探测、点击穿透样式、前台窗口变化三件事都要一个货真价实的 HWND。
/// <para>
/// 刻意不用 <see cref="MessagePumpThread"/> 而自己起线程：<c>SessionMonitor</c> 自己就架在它上面，
/// 测试窗口若也架在同一个设施上，那条线程一出问题就同时喂饱被测方与预言机——「没收到前台变化事件」
/// 既可能是监听器坏了也可能是窗口压根没起来，两者以同一种方式失败。预言机的取样范围必须独立于被测
/// 对象。窗口过程用函数指针而非托管委托，于是「委托失根 → thunk 被回收 → AccessViolation」在这里
/// 根本不存在。
/// </para>
/// </remarks>
internal sealed unsafe class TestWindow : IDisposable
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;

    /// <summary>
    /// 单行 EDIT **没有** ES_AUTOHSCROLL 时，文本一超出可见宽度就被控件拒收（只响一声铃），
    /// 于是「注入了 8 个字符只读回 5 个」会被误判成注入丢字。加上它，控件横向滚动，
    /// 内容一个不少。
    /// </summary>
    private const uint ES_AUTOHSCROLL = 0x0080;

    private const int SW_SHOW = 5;
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_SETTEXT = 0x000C;
    private const uint GW_CHILD = 5;

    /// <summary>菜单栏被键盘激活时会走到的三条消息。三条都数，任何一条到了都算激活。</summary>
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_INITMENU = 0x0116;
    private const uint WM_ENTERMENULOOP = 0x0211;
    private const nint SC_KEYMENU = 0xF100;

    private const uint MF_POPUP = 0x0010;
    private const uint MF_STRING = 0x0000;

    /// <summary>
    /// 菜单激活消息的计数。只能是静态的：窗口过程是函数指针，捕获不了实例字段。桌面测试都在各自的
    /// Collection 里串行跑，故进程级共享一个计数器不会串味；用它之前先 <see cref="ResetMenuActivations"/>。
    /// </summary>
    private static int _menuActivations;

    /// <summary>自上次归零以来，菜单栏被键盘激活了几次。</summary>
    public static int MenuActivations => Volatile.Read(ref _menuActivations);

    public static void ResetMenuActivations() => Volatile.Write(ref _menuActivations, 0);

    /// <summary>
    /// 窗口类只注册一次，进程存续期间不注销：注销要求先销毁全部实例，得不偿失。用 PublicationOnly 而
    /// 不是默认的 ExecutionAndPublication——默认模式会缓存异常，注册偶发失败一次之后，此后每一个
    /// TestWindow 都重抛同一个陈年异常，整套测试一起陪葬且现场早已不在。
    /// </summary>
    private static readonly Lazy<string> ClassName = new(RegisterClass, LazyThreadSafetyMode.PublicationOnly);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();

    private uint _threadId;
    private int _createError;
    private Exception? _threadFailure;

    /// <param name="withTextBox">
    /// 建一个占满客户区的 EDIT 子控件，并把键盘焦点交给它。上屏这件事因此可以自动化验：注入的文本进了
    /// 哪里、进去几个字符，都能从这个控件机械读回，不必靠人眼看记事本。
    /// </param>
    /// <param name="withMenuBar">
    /// 挂一条真的菜单栏。「按 Alt 会不会弹出目标应用的菜单」只有真 HMENU 答得了：没有菜单的窗口收不到
    /// <c>WM_INITMENU</c> / <c>WM_ENTERMENULOOP</c>，测出来的「没激活」是假绿。
    /// </param>
    public TestWindow(string title, uint extendedStyle = 0, bool withTextBox = false, bool withMenuBar = false)
    {
        _thread = new Thread(() => Run(title, extendedStyle, withTextBox, withMenuBar)) { Name = "test-window", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("测试窗口未在期限内建好");
        }

        // 窗口线程上的异常必须搬到调用方线程来抛。留在那边逃逸就是进程终结，
        // 而那时上面这个 5 秒超时**永远等不到**——测试宿主整个没掉，一条失败讯息都没有。
        if (_threadFailure is not null)
        {
            throw new InvalidOperationException("测试窗口线程建窗口失败", _threadFailure);
        }

        if (Handle == nint.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx 失败，Win32 错误码 {_createError}");
        }
    }

    /// <summary>窗口句柄。<see cref="Dispose"/> 之后这个值仍可读，用来构造「已失效的句柄」。</summary>
    public nint Handle { get; private set; }

    /// <summary>EDIT 子控件的句柄；构造时未要求文本框则为 0。</summary>
    public nint TextBox { get; private set; }

    /// <summary>
    /// 读回 EDIT 控件里的内容。
    ///
    /// 走 GetWindowTextW（同进程即一次 WM_GETTEXT），由窗口线程的消息泵应答——
    /// 与注入走的那条路（系统输入队列 → 窗口线程 → EDIT 控件）没有任何共用环节，
    /// 故它是独立于被测对象的取样口。
    /// </summary>
    public string ReadText()
    {
        if (TextBox == nint.Zero)
        {
            throw new InvalidOperationException("本窗口没有 EDIT 控件，构造时须传 withTextBox: true");
        }

        var length = TestNative.GetWindowTextLength(TextBox);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* pBuffer = buffer)
        {
            var copied = TestNative.GetWindowText(TextBox, pBuffer, buffer.Length);
            return new string(buffer, 0, Math.Max(copied, 0));
        }
    }

    /// <summary>把 EDIT 清空。注入型测试之间必须清，否则上一条的残留会被下一条读成自己的结果。</summary>
    public void ClearText() =>
        TestNative.SendMessage(TextBox, WM_SETTEXT, nint.Zero, string.Empty);

    /// <summary>
    /// 等到 EDIT 的内容满足条件为止，返回最后一次读到的内容。
    ///
    /// SendInput 是**异步**的：它把事件塞进系统输入队列就返回，目标线程什么时候取走不归我们管。
    /// 固定 Sleep 要么不够（偶发转红）要么太长（整套测试变慢），轮询两头都占。
    /// </summary>
    public string WaitForText(Func<string, bool> satisfied, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = ReadText();
        while (!satisfied(text) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            text = ReadText();
        }

        return text;
    }

    /// <summary>
    /// 给注入留出足够时间**后**再读，用于断言「什么也没进去」。
    ///
    /// 这类断言天生是空断言的高危区——读得太早，什么实现都能通过。用它的测试必须在同一条用例里
    /// 带一个阳性对照：同一个窗口、同一段等待，换成该进去的那一路，文本必须真的进去。
    /// </summary>
    public string ReadTextAfterSettling()
    {
        Thread.Sleep(SettleMilliseconds);
        return ReadText();
    }

    /// <summary>
    /// 注入到读回实测稳定在 300ms 以内，取两倍余量。这个值只影响「什么也没进去」那类断言的可信度，
    /// 取大不取小。
    /// </summary>
    internal const int SettleMilliseconds = 600;

    /// <summary>
    /// 把窗口抢到前台。返回 false 表示系统拒绝了这次抢占（前台锁定规则），调用方须当作前提不成立而不是
    /// 当作被测对象的失败。光调 SetForegroundWindow 会时灵时不灵：Windows 只准「当前就是前台进程」或
    /// 「收到了最后一次输入事件」的进程抢前台，测试宿主两条都不占，于是它返回 TRUE 而前台窗口纹丝不动。
    /// 故先把本线程的输入队列 attach 到当前前台窗口所在的线程上借它的资格，用完立刻 detach。
    /// </summary>
    public bool BringToForeground()
    {
        TestNative.ShowWindow(Handle, SW_SHOW);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (User32.GetForegroundWindow() == Handle)
            {
                return true;
            }

            var ours = Kernel32.GetCurrentThreadId();
            var foregroundThread = User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out _);

            var attached = foregroundThread != 0
                && foregroundThread != ours
                && TestNative.AttachThreadInput(ours, foregroundThread, attach: true);

            try
            {
                TestNative.BringWindowToTop(Handle);
                TestNative.SetForegroundWindow(Handle);
            }
            finally
            {
                if (attached)
                {
                    TestNative.AttachThreadInput(ours, foregroundThread, attach: false);
                }
            }

            Thread.Sleep(50);
        }

        return User32.GetForegroundWindow() == Handle;
    }

    public bool StillExists() => TestNative.IsWindow(Handle);

    public void Dispose()
    {
        if (_threadId != 0)
        {
            User32.PostThreadMessage(_threadId, User32.WM_QUIT, nint.Zero, nint.Zero);

            // 没退干净就别再动它：窗口只能由建它的线程销毁，那条线程还活着就说明窗口还在，
            // 此时把句柄当失效的用会让后面的测试拿到一个仍然有效的 HWND。
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _ready.Dispose();
    }

    /// <summary>
    /// 两半各自包住，一个异常也不许逃出这条线程：它是自己 new 出来的，其上逃逸的托管异常就是进程终结，
    /// 而最容易抛的恰恰是第一句（<c>ClassName.Value</c> 会触发注册窗口类）——那时构造函数还堵在
    /// <c>_ready.Wait(5s)</c> 上等一个永远不会到来的信号，而整个测试宿主已经没了。
    /// 前半把异常存进字段交给构造函数在调用方线程重抛；后半没有人再等它，只能就地咽下并记进同一个
    /// 字段，但窗口照样销毁，于是失败表现为「这个窗口不见了」而不是「测试宿主没了」。
    /// </summary>
    private void Run(string title, uint extendedStyle, bool withTextBox, bool withMenuBar)
    {
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();

            // 带文本框时把窗口开大些：EDIT 铺满客户区，太窄会让长文本一直横向滚动，
            // 排查失败时看不见现场。
            var width = withTextBox ? 520 : 220;
            var height = withTextBox ? 160 : 120;

            Handle = TestNative.CreateWindowEx(
                extendedStyle, ClassName.Value, title, WS_OVERLAPPEDWINDOW,
                60, 60, width, height, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
            _createError = Marshal.GetLastWin32Error();

            if (withMenuBar && Handle != nint.Zero)
            {
                // 一条顶层项 + 一个下拉子菜单。子菜单少不得：光有顶层项时按 Alt 只高亮，
                // 走不到 WM_ENTERMENULOOP，而那正是「菜单真被打开了」最硬的那条证据。
                var submenu = TestNative.CreatePopupMenu();
                TestNative.AppendMenu(submenu, MF_STRING, 1001, "打开(&O)");

                var bar = TestNative.CreateMenu();
                TestNative.AppendMenu(bar, MF_POPUP, submenu, "文件(&F)");

                if (!TestNative.SetMenu(Handle, bar))
                {
                    throw new InvalidOperationException(
                        $"SetMenu 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
                }

                TestNative.DrawMenuBar(Handle);
            }

            if (withTextBox && Handle != nint.Zero)
            {
                // 子控件必须由建父窗口的这条线程创建：它与父窗口同属一个线程的输入队列，
                // 焦点才转得过去。
                TextBox = TestNative.CreateWindowEx(
                    0, "EDIT", string.Empty, WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                    0, 0, width, height, Handle, nint.Zero, nint.Zero, nint.Zero);

                if (TextBox == nint.Zero)
                {
                    throw new InvalidOperationException(
                        $"建 EDIT 控件失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
                }
            }
        }
        catch (Exception ex)
        {
            // 交给构造函数在调用方线程上重抛。
            _threadFailure = ex;
            return;
        }
        finally
        {
            // 放在 finally：上面任何一条路都必须放行构造函数，否则它白等满 5 秒才报超时，
            // 报的还是错的原因。
            _ready.Set();
        }

        if (Handle == nint.Zero)
        {
            return;
        }

        try
        {
            while (User32.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                User32.TranslateMessage(in msg);
                User32.DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            // 到这一步构造函数早已返回，没人再等这个结果——但让它逃出去是测试宿主当场终结。
            _threadFailure = ex;
        }
        finally
        {
            TestNative.DestroyWindow(Handle);
        }
    }

    private static string RegisterClass()
    {
        // 类名带 GUID：同一个进程里跑两遍（例如测试重跑）不会撞上已注册的类名。
        var name = "LisscribeTestWindow_" + Guid.NewGuid().ToString("N");

        fixed (char* pName = name)
        {
            var description = new TestNative.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(TestNative.WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WindowProcedure,
                lpszClassName = (nint)pName,
            };

            if (TestNative.RegisterClassEx(in description) == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            }
        }

        return name;
    }

    /// <summary>
    /// 除了转交焦点之外一律交给 DefWindowProc。必须转交：键盘输入只到有焦点的窗口，而窗口激活时拿到
    /// 焦点的是父窗口不是 EDIT 子控件——不转交则注入的字符全被父窗口的 DefWindowProc 丢掉、读回永远
    /// 是空串，而那恰好是「注入失败」该有的样子，整类测试会一起假红。用「第一个子窗口」而不是记住某个
    /// 句柄，是因为函数指针捕获不了实例字段。
    /// </summary>
    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        // 数完照样交给 DefWindowProc：拦下来的话菜单根本不会打开，
        // 于是「注入之后菜单是不是真开着」这件事就再也验不了了。
        if (message == WM_INITMENU
            || message == WM_ENTERMENULOOP
            || (message == WM_SYSCOMMAND && (wParam & 0xFFF0) == SC_KEYMENU))
        {
            Interlocked.Increment(ref _menuActivations);
        }

        if (message == WM_SETFOCUS)
        {
            var child = TestNative.GetWindow(window, GW_CHILD);
            if (child != nint.Zero)
            {
                TestNative.SetFocus(child);
                return nint.Zero;
            }
        }

        return TestNative.DefWindowProc(window, message, wParam, lParam);
    }
}

/// <summary>
/// 只有测试才需要的 user32 声明。刻意不加进 <see cref="User32"/>：那里的规矩是
/// 「用到时再由用到的任务加」，而建窗口、抢前台这些事生产代码一次也不做。
/// </summary>
internal static unsafe partial class TestNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(in WNDCLASSEXW description);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(
        uint attachFrom, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    internal delegate int EnumWindowsProc(nint window, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    internal const uint LWA_ALPHA = 0x00000002;

    /// <summary>
    /// 分层属性的读回口。**只有真调过 <c>SetLayeredWindowAttributes</c> 才返回 TRUE**
    /// （光置上 WS_EX_LAYERED 位不算），故它既是断言也是阴性对照。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLayeredWindowAttributes(
        nint window, out uint key, out byte alpha, out uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CreateMenu();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenu(nint menu, uint flags, nint idNewItem, string newItem);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetMenu(nint window, nint menu);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawMenuBar(nint window);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll")]
    internal static partial nint SetFocus(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(nint window);

    /// <summary>
    /// 缓冲区声明成 <c>char*</c> 而不是 <c>[Out] char[]</c>：后者在 LibraryImport 下报 SYSLIB1051
    /// （源生成互操作不支持这种运行时封送），而 <c>TreatWarningsAsErrors</c> 下那就是编译失败。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    internal static partial int GetWindowText(nint window, char* buffer, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SendMessage(nint window, uint message, nint wParam, string lParam);

    /// <summary>
    /// 往本进程自己的每个顶级窗口投一条消息，用来驱动 <c>SystemEvents</c>：它内部维护着一个顶级窗口，
    /// 收到 WM_WTSSESSION_CHANGE / WM_POWERBROADCAST 就照常引发 SessionSwitch / PowerModeChanged，
    /// 于是锁屏与休眠这两条路径能走生产那条链自动化验证。
    /// <para>
    /// 只投给本进程：用 HWND_BROADCAST 会把「会话已锁定」告诉桌面上每一个程序，那是拿用户的整个桌面
    /// 做实验。也不按类名去找那个窗口——类名是 SystemEvents 的内部实现，改了名字测试就悄悄变成空测；
    /// 挨个投只依赖「它是本进程的一个顶级窗口」这个弱得多的前提。
    /// </para>
    /// </summary>
    internal static void PostToOwnTopLevelWindows(uint message, nint wParam, nint lParam)
    {
        var mine = (uint)Environment.ProcessId;

        EnumWindowsProc callback = (window, _) =>
        {
            User32.GetWindowThreadProcessId(window, out var processId);
            if (processId == mine)
            {
                PostMessage(window, message, wParam, lParam);
            }

            return 1;
        };

        EnumWindows(callback, nint.Zero);
        GC.KeepAlive(callback);
    }
}
