using System.Runtime.InteropServices;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 主屏工作区的实时取值。
///
/// <para>
/// 预言机是 <c>SystemParametersInfo(SPI_GETWORKAREA)</c>——与被测的 <c>GetMonitorInfo</c> 是两条
/// 各自独立的系统 API，取样不经过被测代码的任何一步。
/// </para>
/// </summary>
public sealed partial class PrimaryWorkAreaTests
{
    private const uint SPI_GETWORKAREA = 0x0030;

    [Fact]
    public void Current_MatchesTheWorkAreaReportedBySystemParametersInfo()
    {
        var expected = default(ProbeRect);

        Assert.True(
            SystemParametersInfo(SPI_GETWORKAREA, 0, ref expected, 0),
            $"SPI_GETWORKAREA 失败（Win32 错误码 {Marshal.GetLastWin32Error()}），预言机取不到样，本条测试无从判定");

        var actual = PrimaryWorkArea.Current();

        Assert.NotNull(actual);

        Assert.Equal(
            (expected.Left, expected.Top, expected.Right - expected.Left, expected.Bottom - expected.Top),
            (actual!.Value.X, actual.Value.Y, actual.Value.Width, actual.Value.Height));
    }

    /// <summary>
    /// 宽高为零或负数不是「边界情形」，是「这次取回来的是垃圾」，而按它算出来的落点会静默地
    /// 把悬浮条摆到屏幕外。
    /// </summary>
    [Fact]
    public void Current_ReturnsAPositivelySizedArea()
    {
        var area = PrimaryWorkArea.Current();

        Assert.NotNull(area);
        Assert.True(area!.Value.Width > 0, $"工作区宽度是 {area.Value.Width}");
        Assert.True(area.Value.Height > 0, $"工作区高度是 {area.Value.Height}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(
        uint action, uint param, ref ProbeRect data, uint winIni);
}
