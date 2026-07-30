using Avalonia;
using Lisscribe.App;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 悬浮条落点的纯函数。
///
/// <para>
/// **这一整组测试都建立在一条前提上，而前提本身单测验不了**：Avalonia 的
/// <c>Screen.WorkingArea</c> / <c>Window.Position</c> 是物理像素，<c>Window.Width/Height</c>
/// 是 DIP，故 <c>px = dip × scaling</c>。换算方向若是反的，下面每一条断言都会照着错的前提一起绿
/// ——它们只是在复述 <see cref="OverlayPlacement.Compute"/> 自己的算术。
/// 真正钉住方向的是 <see cref="OverlayPlacementOnRealScreenTests"/>：那里用 <c>GetWindowRect</c>
/// 与 <c>GetMonitorInfo</c>（两者都不经过 Avalonia）当预言机，且把窗口的 DPI 强推到 150%。
/// </para>
/// </summary>
public sealed class OverlayPlacementTests
{
    /// <summary>1920x1080 屏，任务栏占底部 40 物理像素。</summary>
    private static readonly PixelRect WorkingArea = new(0, 0, 1920, 1040);

    private const double WidthDip = 200;
    private const double HeightDip = 48;
    private const int MarginDip = 16;

    [Fact]
    public void Compute_At100Percent_PlacesAtBottomRightWithMargin()
    {
        var point = OverlayPlacement.Compute(WorkingArea, WidthDip, HeightDip, scaling: 1.0, MarginDip);

        Assert.Equal(1920 - 200 - 16, point.X);
        Assert.Equal(1040 - 48 - 16, point.Y);
    }

    [Fact]
    public void Compute_At150Percent_ScalesBothSizeAndMargin()
    {
        // 关键回归：不换算的实现会算出 1920-200-16 = 1704，而窗口在 150% 屏上实际占 300
        // 物理像素，于是右侧溢出 100px。边距也必须乘，否则视觉边距在高 DPI 屏上缩水到三分之二。
        var point = OverlayPlacement.Compute(WorkingArea, WidthDip, HeightDip, scaling: 1.5, MarginDip);

        Assert.Equal(1920 - 300 - 24, point.X);
        Assert.Equal(1040 - 72 - 24, point.Y);
    }

    [Fact]
    public void Compute_At200Percent_StaysOnScreen()
    {
        var point = OverlayPlacement.Compute(WorkingArea, WidthDip, HeightDip, scaling: 2.0, MarginDip);

        Assert.Equal(1920 - 400 - 32, point.X);
        Assert.Equal(1040 - 96 - 32, point.Y);
    }

    [Fact]
    public void Compute_OnSecondaryMonitorWithOffset_RespectsWorkingAreaOrigin()
    {
        // 外接显示器在主屏右侧：WorkingArea.X 非零。
        // 本机也是这个形状——任务栏在**左侧**，主屏工作区 X 非零（2026-07-27 实测 84，
        // 同日更早为 56；见 OverlayPlacementOnRealScreenTests），故这条不是假想场景。
        var secondary = new PixelRect(1920, 0, 2560, 1400);

        var point = OverlayPlacement.Compute(secondary, WidthDip, HeightDip, 1.0, MarginDip);

        Assert.Equal(1920 + 2560 - 200 - 16, point.X);
        Assert.Equal(1400 - 48 - 16, point.Y);
    }

    [Fact]
    public void Compute_WhenWindowLargerThanScreenInBothAxes_ClampsToWorkingAreaOrigin()
    {
        // **屏幕两边都得比窗口小**才验得着「两轴都夹住」：拿 100×100 的屏幕，
        // 100 - 48 - 16 = 36，Y 根本不溢出，那条测试其实只验了 X。
        var tiny = new PixelRect(0, 0, 100, 40);

        var point = OverlayPlacement.Compute(tiny, WidthDip, HeightDip, 1.0, MarginDip);

        Assert.Equal(0, point.X);
        Assert.Equal(0, point.Y);
    }

    [Fact]
    public void Compute_WhenOnlyOneAxisOverflows_LeavesTheOtherAlone()
    {
        // 杀「一边溢出就把两边一起夹到原点」这个改法。
        var narrow = new PixelRect(0, 0, 100, 1040);

        var point = OverlayPlacement.Compute(narrow, WidthDip, HeightDip, 1.0, MarginDip);

        Assert.Equal(0, point.X);
        Assert.Equal(1040 - 48 - 16, point.Y);
    }

    [Fact]
    public void Compute_WhenClampingOnAScreenThatDoesNotStartAtZero_ClampsToThatScreen()
    {
        // 杀 Math.Max(0, x)：副屏在主屏右侧时，夹到 0 等于把悬浮条扔回主屏左上角。
        var secondaryTiny = new PixelRect(1920, 300, 100, 40);

        var point = OverlayPlacement.Compute(secondaryTiny, WidthDip, HeightDip, 1.0, MarginDip);

        Assert.Equal(1920, point.X);
        Assert.Equal(300, point.Y);
    }

    [Fact]
    public void Compute_WithFractionalScaling_RoundsToWholePixels()
    {
        // **缩放比例得挑真会产生小数的**：取 1.25 的话 200×1.25=250、16×1.25=20 都是整数，
        // 一次舍入也没发生，把 Math.Round 整个删掉照样绿。
        // 1.1 才有：200×1.1 = 220.00000000000003 → 220，16×1.1 = 17.6 → 18。
        var point = OverlayPlacement.Compute(WorkingArea, WidthDip, HeightDip, scaling: 1.1, MarginDip);

        Assert.Equal(1920 - 220 - 18, point.X);
        Assert.Equal(1040 - 53 - 18, point.Y);
    }

    [Fact]
    public void Compute_AtAHalfPixel_RoundsAwayFromZeroSoTheWindowStaysInside()
    {
        // 钉住舍入模式本身。202×1.25 = 252.5 恰在中点：默认的 Math.Round 走 ToEven 给 252，
        // 本实现要的是 AwayFromZero 给 253 —— 尺寸往大里算，落点就往屏内挪，宁可少一像素边距
        // 也不要多一像素溢出。
        var point = OverlayPlacement.Compute(WorkingArea, windowWidthDip: 202, HeightDip, scaling: 1.25, MarginDip);

        Assert.Equal(1920 - 253 - 20, point.X);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1.5d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compute_WithANonsenseScaling_Throws(double scaling)
    {
        // 不是防御性洁癖。.NET 的浮点转整数是**饱和**的，不抛（实测 (int)Math.Round(NaN) == 0，
        // +∞ == int.MaxValue）：NaN 会让宽高边距一起变成 0，落点正好是工作区右下角那个顶点，
        // 整个窗体挂在屏幕外面；+∞ 则让减法整数溢出回绕。两种都是「屏幕上什么也看不见、
        // 日志里什么也没有」——本项目最不想要的那种失败形态。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayPlacement.Compute(WorkingArea, WidthDip, HeightDip, scaling, MarginDip));
    }
}
