using Lisscribe.Platform.Native;

namespace Lisscribe.Platform;

/// <summary>窗口此刻的现状，物理像素。<see cref="LayeredAlpha"/> 只在 <see cref="LayeredAttributesSet"/> 为真时有意义。</summary>
public readonly record struct OverlayWindowState(
    nint ExtendedStyle,
    bool Visible,
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool LayeredAttributesSet,
    byte LayeredAlpha);

/// <summary>
/// 「窗口摆好了，可屏幕上没有」这类故障的取样口。
/// </summary>
/// <remarks>
/// 它只问系统、不听被测代码自述。最要紧的一项是分层属性：
/// <c>WS_EX_LAYERED</c> 置了位而 <c>SetLayeredWindowAttributes</c> 没设上时窗口整个不可见，
/// 而 <see cref="OverlayWindowStyler"/> 刻意丢掉了那次调用的返回值（抛出会把异常送回状态机的线程）。
/// </remarks>
public static class OverlayWindowProbe
{
    /// <summary>句柄已失效或读不出矩形时返回 <c>null</c>——宁可日志里缺一行，也不留一份看着正常的假现场。</summary>
    public static OverlayWindowState? Describe(nint window)
    {
        if (window == nint.Zero || !User32.IsWindow(window))
        {
            return null;
        }

        var rect = default(RECT);

        if (!User32.GetWindowRect(window, ref rect))
        {
            return null;
        }

        var layeredSet = User32.GetLayeredWindowAttributes(window, out _, out var alpha, out _);

        return new OverlayWindowState(
            User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE),
            User32.IsWindowVisible(window),
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom,
            layeredSet,
            layeredSet ? alpha : (byte)0);
    }
}
