using System.Runtime.InteropServices;
using Lisscribe.Platform.Native;

namespace Lisscribe.Platform;

/// <summary>主屏工作区，物理像素。<c>Width</c> / <c>Height</c> 而非右下角坐标，与 Avalonia 的形状一致。</summary>
public readonly record struct ScreenWorkArea(int X, int Y, int Width, int Height);

/// <summary>
/// 主屏工作区的**实时**取值。
/// </summary>
/// <remarks>
/// 存在的理由只有一条：Avalonia 11.3.18 的 <c>Screens</c> 把 <c>WorkingArea</c> 缓存在
/// <c>WinScreen</c> 里，只在它自己认得的那几个消息上 <c>Refresh()</c>；而任务栏出现、工作区变化
/// 走的是 <c>WM_SETTINGCHANGE</c> / <c>SPI_SETWORKAREA</c>，Avalonia 的 <c>WndProc</c> 收到它
/// 只处理主题色。开机自启的进程若比 explorer 先起来，那份缓存就停在「还没有任务栏」的全屏值上。
/// <c>GetMonitorInfo</c> 每次调用现查，不吃这一手。
/// </remarks>
public static class PrimaryWorkArea
{
    /// <summary>取不到返回 <c>null</c>：调用方该退回自己原来的数据源，不该拿一个编出来的矩形去算坐标。</summary>
    public static ScreenWorkArea? Current()
    {
        var monitor = User32.MonitorFromPoint(default, User32.MONITOR_DEFAULTTOPRIMARY);

        if (monitor == nint.Zero)
        {
            return null;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (!User32.GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var work = info.rcWork;

        return new ScreenWorkArea(
            work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }
}
