using System.Runtime.InteropServices;
using Lisscribe.Core.Abstractions;
using Lisscribe.Platform.Native;

namespace Lisscribe.Platform;

/// <summary>
/// 给悬浮指示器加上点击穿透 / 不激活 / 不进 Alt+Tab 这几个扩展样式。
/// </summary>
/// <remarks>
/// 必须在 <c>Opened</c> 里调，且每次 <c>Show()</c> 都要重调一遍——<c>Show()</c> 会把
/// <c>GWL_EXSTYLE</c> 整个写回 Avalonia 自己那一份，四个位全被抹掉。
/// </remarks>
public sealed class OverlayWindowStyler : IOverlayWindowStyler
{
    public void MakeClickThrough(nint window)
    {
        if (window == nint.Zero)
        {
            throw new ArgumentException("窗口句柄为 0，还没建出窗口就来设样式了", nameof(window));
        }

        // 这两个函数成功时也返回 0（窗口原本没有扩展样式），只能靠 last error 区分成败。
        var current = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
        var readError = Marshal.GetLastWin32Error();

        if (current == 0 && readError != 0)
        {
            throw new InvalidOperationException(
                $"读取窗口 0x{window:X} 的扩展样式失败，Win32 错误码 {readError}");
        }

        // 点击穿透要 LAYERED 与 TRANSPARENT 两位齐备，缺一不成。
        // 是「或上去」不是「写进去」：Avalonia 建好的窗口自带一串扩展样式，直接赋值会抹掉它们。
        var updated = current
            | User32.WS_EX_LAYERED
            | User32.WS_EX_TRANSPARENT
            | User32.WS_EX_NOACTIVATE
            | User32.WS_EX_TOOLWINDOW;

        var previous = User32.SetWindowLongPtr(window, User32.GWL_EXSTYLE, updated);
        var writeError = Marshal.GetLastWin32Error();

        if (previous == 0 && writeError != 0)
        {
            throw new InvalidOperationException(
                $"设置窗口 0x{window:X} 的扩展样式失败，Win32 错误码 {writeError}");
        }

        // 整窗不透明，把它登记成一个设过属性的分层窗口。
        // 失败刻意不抛：调用点在 OnOpened 上，抛出会把异常送回引发状态变更的那条线程。
        _ = User32.SetLayeredWindowAttributes(window, 0, byte.MaxValue, User32.LWA_ALPHA);
    }
}
