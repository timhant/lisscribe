using System.Runtime.InteropServices;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 窗口现状探针。它只有一个职责：把 Win32 那几个「窗口现在是什么样」的值原样搬出来，
/// 供日志留证——「悬浮条摆好了却看不见」这类故障，缺的正是这份现场。
///
/// <para>
/// 分层属性那条是本组的重头：<c>WS_EX_LAYERED</c> 置了位而
/// <c>SetLayeredWindowAttributes</c> 没设上时，窗口**整个不可见**，而生产代码
/// （<see cref="OverlayWindowStyler"/>）刻意丢掉了那个返回值。探针从系统侧读回来，
/// 不听被测代码自己怎么说。
/// </para>
/// </summary>
public sealed partial class OverlayWindowProbeTests
{
    [Fact]
    public void Describe_CopiesTheWindowRectVerbatim()
    {
        using var window = new TestWindow("probe-rect");

        var expected = default(ProbeRect);
        Assert.True(
            GetWindowRect(window.Handle, ref expected),
            $"GetWindowRect 失败（Win32 错误码 {Marshal.GetLastWin32Error()}），预言机取不到样");

        var actual = OverlayWindowProbe.Describe(window.Handle);

        Assert.NotNull(actual);
        Assert.Equal(
            (expected.Left, expected.Top, expected.Right, expected.Bottom),
            (actual!.Value.Left, actual.Value.Top, actual.Value.Right, actual.Value.Bottom));
    }

    /// <summary>
    /// 一个普通窗口没有分层属性；跑过 <see cref="OverlayWindowStyler.MakeClickThrough"/> 之后有。
    /// 两头都断，才排除掉「探针恒返回某一个值」。
    /// </summary>
    [Fact]
    public void Describe_TellsWhetherLayeredAttributesAreSet()
    {
        using var window = new TestWindow("probe-layered");

        var before = OverlayWindowProbe.Describe(window.Handle);

        Assert.NotNull(before);
        Assert.False(before!.Value.LayeredAttributesSet, "还没设过分层属性，探针却说设了");

        new OverlayWindowStyler().MakeClickThrough(window.Handle);

        var after = OverlayWindowProbe.Describe(window.Handle);

        Assert.NotNull(after);
        Assert.True(after!.Value.LayeredAttributesSet, "设过分层属性了，探针却说没有");
        Assert.Equal(byte.MaxValue, after.Value.LayeredAlpha);
    }

    /// <summary>句柄已经失效时不能编一个矩形出来——那会让日志里出现一份看着正常的假现场。</summary>
    [Fact]
    public void Describe_ReturnsNullForADeadHandle()
    {
        var window = new TestWindow("probe-dead");
        var handle = window.Handle;

        window.Dispose();

        Assert.Null(OverlayWindowProbe.Describe(handle));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, ref ProbeRect rect);
}
