using System.Runtime.InteropServices;

namespace Lisscribe.App.Tests;

/// <summary>
/// 桌面级测试的取样口：**一律直接问 Win32，不经过 Avalonia**。
///
/// <para>
/// 要验的恰恰是「Avalonia 的 <c>Position</c> / <c>WorkingArea</c> 到底是什么单位」，
/// 拿 Avalonia 自己的另一个属性去核对只是在让它自证；必须有一把它管不着的尺。
/// </para>
///
/// <para>
/// 刻意不复用 <c>Lisscribe.Platform.Native.User32</c>：那是被测代码所在的程序集，
/// 它若把某个声明写错（比如常量抄错），预言机会跟着一起错。同一条理由下
/// <c>Lisscribe.Platform.Tests.TestNative</c> 也是自带一份。
/// </para>
/// </summary>
internal static partial class NativeProbe
{
    internal const int GWL_EXSTYLE = -20;

    internal const nint WS_EX_TRANSPARENT = 0x00000020;
    internal const nint WS_EX_TOOLWINDOW = 0x00000080;
    internal const nint WS_EX_NOACTIVATE = 0x08000000;
    internal const nint WS_EX_TOPMOST = 0x00000008;
    internal const nint WS_EX_LAYERED = 0x00080000;

    internal const uint WM_DPICHANGED = 0x02E0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;

        public override readonly string ToString() => $"[{Left},{Top},{Right},{Bottom}]";
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint window, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(
        uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    internal static partial int GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLayeredWindowAttributes(
        nint window, out uint key, out byte alpha, out uint flags);

    // 合成按键。与 Lisscribe.Platform.Tests.SynthesizedInput 同一手法却各写一份：
    // 那边住在被测程序集的测试项目里，这边要验「Avalonia 收到了什么」，两条链不该共用构造代码。

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>`/` 与 `?` 那个键。US 布局下 VK_OEM_2。</summary>
    internal const ushort VK_OEM_2 = 0xBF;

    internal const ushort VK_LMENU = 0xA4;
    internal const ushort VK_RMENU = 0xA5;
    internal const ushort VK_LCONTROL = 0xA2;
    internal const ushort VK_RCONTROL = 0xA3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>
    /// 只声明键盘那一支，但**大小必须是整个联合的大小**，否则 SendInput 按 cbSize 校验时会拒收。
    /// x64 下 INPUT 是 4（type）+ 4（填充）+ 32（KEYBDINPUT，含 nint 对齐）= 40 字节，
    /// 而 KEYBDINPUT 自身 24 字节 —— 靠 <c>Marshal.SizeOf</c> 算，不手写数字。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint Type;
        public uint Padding;
        public KEYBDINPUT Key;
        public nint Tail;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint count, INPUT[] inputs, int size);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);

    /// <summary>一次 SendInput 发一批，批内次序有保证。</summary>
    internal static void SendKeys(params (ushort Vk, bool KeyUp)[] keys)
    {
        var inputs = new INPUT[keys.Length];

        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = new INPUT
            {
                Type = INPUT_KEYBOARD,
                Key = new KEYBDINPUT
                {
                    Vk = keys[i].Vk,
                    Flags = keys[i].KeyUp ? KEYEVENTF_KEYUP : 0,
                },
            };
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput 只发出 {sent}/{inputs.Length} 个事件，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// 把本组用到的键统一松一遍。少了它，<b>被吞掉 KEYUP 的键会跨进程永久卡在按住态</b>，
    /// 下一条测试乃至用户的键盘都跟着遭殃。
    /// </summary>
    internal static void ReleaseKeysUsedHere()
    {
        foreach (var vk in (ushort[])[VK_OEM_2, VK_LMENU, VK_RMENU, VK_LCONTROL, VK_RCONTROL])
        {
            SendKeys((vk, true));
        }
    }

    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint window, nint dc);

    /// <summary>
    /// 屏幕上那一点**真正**是什么颜色。这是唯一一把不问任何窗口、只问屏幕的尺，
    /// 用来分辨「窗口还画得出来吗」——加了 <c>WS_EX_LAYERED</c> 之后这不是白问的问题。
    /// </summary>
    [LibraryImport("gdi32.dll")]
    internal static partial int GetPixel(nint dc, int x, int y);

    /// <summary>本窗口所在显示器的工作区，物理像素。<c>MONITOR_DEFAULTTONEAREST</c>。</summary>
    internal static MONITORINFO MonitorInfoFor(nint window)
    {
        var monitor = MonitorFromWindow(window, 2);
        var info = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info) == 0)
        {
            throw new InvalidOperationException(
                $"GetMonitorInfo 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }

        return info;
    }

    /// <summary>
    /// 把窗口抢到前台并压到最上层。返回 <c>false</c> 表示系统拒绝了这次抢占，
    /// 调用方应当作**前提不成立**报出去，而不是当作被测对象的失败。
    ///
    /// <para>
    /// **光调 <see cref="SetForegroundWindow"/> 不够。** Windows 只准「当前就是前台进程」或
    /// 「收到了最后一次输入事件」的进程抢前台，测试宿主两条都不占，于是它返回 TRUE 而前台
    /// 纹丝不动。解法是先把本线程的输入队列 attach 到当前前台窗口所在的线程上借它的资格，
    /// 用完立刻 detach。
    /// </para>
    /// <para>
    /// <see cref="OverlayScenario.WithWindowUnderneath"/> 建的「下方窗口」必须走这条路抢前台：
    /// 少了它，桌面上只要有别的进程的窗口盖着取样区并拿着前台，三条测试就一起红
    /// （取样点不是纯蓝、下方窗口收不到鼠标、阳性对照抢不到前台）。摆好那种局时 3/3 确定性复现，
    /// 不摆时四轮里三轮绿——正是最容易被当成「偶发、重跑一次就好」而放过去的形状。
    /// </para>
    /// <para>
    /// 从**测试线程**调，不要从 Avalonia 的 UI 线程调：attach 的是调用线程的输入队列，
    /// 而 UI 线程此刻正跑着自己的消息循环，按住它会让下面那个轮询等不到任何变化。
    /// </para>
    /// </summary>
    internal static bool BringToForeground(nint window)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (GetForegroundWindow() == window)
            {
                return true;
            }

            var ours = GetCurrentThreadId();
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);

            var attached = foregroundThread != 0
                && foregroundThread != ours
                && AttachThreadInput(ours, foregroundThread, doAttach: true);

            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(ours, foregroundThread, doAttach: false);
                }
            }

            Thread.Sleep(50);
        }

        return GetForegroundWindow() == window;
    }

    /// <summary>
    /// 假装系统通知这个窗口「你现在在一块 <paramref name="dpi"/> DPI 的屏上」。
    ///
    /// <para>
    /// 用它是为把结论从「这台机器现在恰好是 150%」解耦成「窗口认定 1.5 时的行为」——
    /// 显示设置说变就变，缩放回到 100% 时 DIP 与物理像素同值，任何直接测量都分不出
    /// 「乘 scaling」和「什么都不做」。
    /// </para>
    /// <para>
    /// 发 <c>WM_DPICHANGED</c> 走的是**真实的生产代码路径**——把窗口拖到另一块不同缩放的屏上时
    /// 系统发的就是这条消息，Avalonia 的 Win32 后端也正是在这条消息里更新 scaling。
    /// 它假的地方只有一处：显示器本身没变。故结论限于「Avalonia 认定 scaling = 1.5 时，
    /// DIP 与物理像素的换算方向是乘」，不能推广成「在真 150% 屏上通过了验收」。
    /// </para>
    /// </summary>
    internal static void TellTheWindowItsDpiChanged(nint window, int dpi, RECT suggestedRect)
    {
        var wParam = (nint)((uint)dpi | ((uint)dpi << 16));
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(suggestedRect, buffer, fDeleteOld: false);
            SendMessage(window, WM_DPICHANGED, wParam, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
