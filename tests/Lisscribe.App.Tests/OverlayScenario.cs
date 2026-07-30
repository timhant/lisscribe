using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lisscribe.App.ViewModels;
using Lisscribe.App.Views;
using Lisscribe.Platform;

namespace Lisscribe.App.Tests;

/// <summary>
/// 桌面级测试的场景搭子：真的 <see cref="OverlayWindow"/>，加一个可选的「下方窗口」。
///
/// <para>
/// 每条测试各建各的、用完必关。悬浮条是置顶窗口，留在屏幕上就是脏状态；
/// 而 <see cref="AvaloniaTestHost"/> 的 UI 线程是进程级的，不会替谁收尾。
/// </para>
/// </summary>
internal sealed class OverlayScenario : IDisposable
{
    private OverlayScenario(OverlayViewModel viewModel, OverlayWindow overlay, nint overlayHandle)
    {
        ViewModel = viewModel;
        Overlay = overlay;
        OverlayHandle = overlayHandle;
    }

    public OverlayViewModel ViewModel { get; }

    public OverlayWindow Overlay { get; }

    public nint OverlayHandle { get; }

    public Window? Below { get; private set; }

    public nint BelowHandle { get; private set; }

    public int BelowPointerMoves;

    public int OverlayPointerMoves;

    /// <param name="show">
    /// 建好就显示。<c>false</c> 用于「还没 Show 时窗口是什么样」这类断言——
    /// <c>Show()</c> 会抹掉扩展样式，正是在这条路上量出来的。
    /// </param>
    public static OverlayScenario Create(bool show = true)
    {
        return AvaloniaTestHost.Run(() =>
        {
            var viewModel = new OverlayViewModel();
            var overlay = new OverlayWindow(viewModel, new OverlayWindowStyler());
            var handle = overlay.TryGetPlatformHandle()!.Handle;
            var scenario = new OverlayScenario(viewModel, overlay, handle);
            overlay.PointerMoved += (_, _) => Interlocked.Increment(ref scenario.OverlayPointerMoves);

            if (show)
            {
                overlay.Show();
            }

            return scenario;
        });
    }

    /// <summary>
    /// 在悬浮条下面垫一个已知纯色的窗口，用来分辨鼠标去了哪里、屏幕上画的是谁。
    ///
    /// <para>
    /// **建完必须真的把它抢到最上层，否则这一组测试全是假的。** 它是
    /// <c>ShowActivated = false</c> 的普通窗口（不置顶——置顶就和悬浮条同一层，
    /// 「悬浮条盖在它上面」这个前提就没了），桌面上任何一个拿着前台的窗口都压在它上面。
    /// 那时三条测试会各自以自己的方式红：取样点不是纯蓝、下方窗口一次鼠标移动也收不到、
    /// 阳性对照抢不到前台——**没有一条说得出真因是「下方窗口根本不在最上层」**。
    /// 抢法见 <see cref="NativeProbe.BringToForeground"/>。
    /// </para>
    /// <para>
    /// 抢不到就**当场把前提不成立喊出来**，不留给下游断言去误报。
    /// </para>
    /// </summary>
    public OverlayScenario WithWindowUnderneath(PixelPoint at, PixelSize size)
    {
        AvaloniaTestHost.Run(() =>
        {
            var below = new Window
            {
                Width = size.Width,
                Height = size.Height,
                SystemDecorations = SystemDecorations.None,
                Background = Brushes.Blue,
                ShowInTaskbar = false,
                ShowActivated = false,
                CanResize = false,
            };
            below.PointerMoved += (_, _) => Interlocked.Increment(ref BelowPointerMoves);
            below.Show();
            below.Position = at;
            Below = below;
            BelowHandle = below.TryGetPlatformHandle()!.Handle;
            return 0;
        });

        if (!NativeProbe.BringToForeground(BelowHandle))
        {
            throw new InvalidOperationException(
                $"""
                下方窗口没能抢到前台，本组测试的前提不成立（这不是产品缺陷）。

                当前前台窗口是 0x{NativeProbe.GetForegroundWindow():X}，不是我们刚建的那个。
                此时下方窗口被别人压着，「鼠标穿透到它」「那一点是纯蓝」都会以指不到真因的方式失败。

                恢复办法（把前台还给一个普通窗口即可）：
                    (New-Object -ComObject Shell.Application).MinimizeAll()
                    (New-Object -ComObject Shell.Application).UndoMinimizeALL()
                """);
        }

        return this;
    }

    public void MoveOverlayTo(PixelPoint position) => AvaloniaTestHost.Run(() =>
    {
        Overlay.Position = position;
        return 0;
    });

    public nint ExtendedStyle() => NativeProbe.GetWindowLongPtr(OverlayHandle, NativeProbe.GWL_EXSTYLE);

    public NativeProbe.RECT OverlayRect()
    {
        NativeProbe.GetWindowRect(OverlayHandle, out var rect);
        return rect;
    }

    /// <summary>
    /// 等到窗口的物理矩形满足条件为止，返回最后一次读到的值。
    ///
    /// <para>
    /// Avalonia 的布局是排在 dispatcher 队列上跑的，改完 <c>Width</c> 不等于当场就改了 HWND。
    /// 固定 <c>Sleep</c> 要么不够（偶发转红）要么太长（整套测试变慢）。
    /// **在测试线程上轮询**——UI 线程此刻正跑着它自己的循环，别去按住它。
    /// 超时不抛：让调用方的断言去报「实际是多少」，那比一句 TimeoutException 有用。
    /// </para>
    /// </summary>
    public NativeProbe.RECT OverlayRectWhen(Func<NativeProbe.RECT, bool> satisfied)
    {
        var deadline = Environment.TickCount64 + 3000;
        var rect = OverlayRect();
        while (!satisfied(rect) && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(25);
            rect = OverlayRect();
        }

        return rect;
    }

    /// <summary>
    /// 把真光标在某点上来回扫 12 次，返回这期间两个窗口各自收到多少次 <c>PointerMoved</c>。
    ///
    /// <para>
    /// **只能用真光标。** <c>WindowFromPoint</c> 实测**不认** <c>WS_EX_TRANSPARENT</c>
    /// （置位之后仍返回悬浮条），拿它当判据会得出反的结论。
    /// </para>
    /// <para>
    /// 扫的动作必须在**测试线程**上做，不能在 UI 线程上做：光标移动是靠 Win32 消息送到窗口的，
    /// 而 UI 线程正是取消息的那条线程——在它上面 <c>Thread.Sleep</c> 就是把消息泵按住，
    /// 于是两个窗口一次也收不到，量出来的结论是「谁都没收到」。
    /// </para>
    /// </summary>
    public (int Below, int Overlay) SweepCursorOver(int x, int y)
    {
        Interlocked.Exchange(ref BelowPointerMoves, 0);
        Interlocked.Exchange(ref OverlayPointerMoves, 0);

        for (var i = 0; i < 12; i++)
        {
            NativeProbe.SetCursorPos(x + (i % 5), y + (i % 3));
            Thread.Sleep(30);
        }

        Thread.Sleep(250);
        return (Volatile.Read(ref BelowPointerMoves), Volatile.Read(ref OverlayPointerMoves));
    }

    /// <summary>屏幕上那一点真正的颜色（COLORREF，低位是蓝）。不问任何窗口，只问屏幕。</summary>
    public static int ScreenPixelAt(int x, int y)
    {
        var screen = NativeProbe.GetDC(0);
        try
        {
            return NativeProbe.GetPixel(screen, x, y);
        }
        finally
        {
            NativeProbe.ReleaseDC(0, screen);
        }
    }

    public void Dispose() => AvaloniaTestHost.Run(() =>
    {
        Overlay.Close();
        Below?.Close();
        return 0;
    });
}
