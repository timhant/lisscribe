using Avalonia;
using Lisscribe.App;
using Lisscribe.App.Views;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// **这一组才是 <see cref="OverlayPlacement"/> 的真判据。**
///
/// <para>
/// <see cref="OverlayPlacementTests"/> 里的算术断言全部建立在「<c>Position</c> / <c>WorkingArea</c>
/// 是物理像素，<c>Width</c> / <c>Height</c> 是 DIP」这条前提上；前提若反了，那一组会照着错的
/// 前提一起绿。这里的预言机是 <c>GetWindowRect</c> 与 <c>GetMonitorInfo</c>——两者都不经过
/// Avalonia，也不经过被测代码。
/// </para>
///
/// <para>
/// <b>本组刻意不依赖「本机现在是多少缩放」</b>：所有断言都拿 <c>GetMonitorInfo</c> /
/// <c>GetDpiForWindow</c> 现算，没有一处写死屏幕尺寸。这不是洁癖 ——
/// 用户随时会改显示设置，**分辨率与缩放一改，写死在注释与断言里的数就一夜全废**。
/// </para>
/// <para>
/// 最后两条仍把窗口的 DPI 推到 144 再量，理由也变了：不再是「本机造不出非 1 的缩放」，
/// 而是**把结论从「这台机器现在恰好是 150%」解耦成「窗口认定 1.5 时的行为」**。
/// 手法与其代价见 <see cref="NativeProbe.TellTheWindowItsDpiChanged"/>。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class OverlayPlacementOnRealScreenTests
{
    private const int MarginDip = OverlayWindow.MarginDip;

    [Fact]
    public void AvaloniaWindowPosition_LandsAtExactlyThoseWin32ScreenCoordinates()
    {
        using var scenario = OverlayScenario.Create();

        // 取一个不对称、不是 0 也不是屏幕中点的位置：X 与 Y 写反了也看得出来。
        scenario.MoveOverlayTo(new PixelPoint(311, 217));

        var rect = scenario.OverlayRectWhen(r => r.Left == 311 && r.Top == 217);
        Assert.Equal(311, rect.Left);
        Assert.Equal(217, rect.Top);
    }

    [Fact]
    public void AvaloniaWorkingArea_IsTheMonitorsPhysicalWorkAreaVerbatim()
    {
        using var scenario = OverlayScenario.Create();
        var monitor = NativeProbe.MonitorInfoFor(scenario.OverlayHandle);

        var (workingArea, bounds) = AvaloniaTestHost.Run(() =>
        {
            var screen = scenario.Overlay.Screens.Primary!;
            return (screen.WorkingArea, screen.Bounds);
        });

        Assert.Equal(monitor.Work.Left, workingArea.X);
        Assert.Equal(monitor.Work.Top, workingArea.Y);
        Assert.Equal(monitor.Work.Width, workingArea.Width);
        Assert.Equal(monitor.Work.Height, workingArea.Height);

        // 阴性对照：工作区必须真的小于整块屏，否则这条测试是在一块没有任务栏的屏上做的，
        // 「取 WorkingArea 而不是 Bounds」那句规格就没被验到。
        // 本机任务栏在**左侧**，2026-07-27 实测 rcWork = [84,0,2560,1440]（同日更早是
        // [56,0,1920,1080]，用户中途改了显示设置——84 = 56 DIP × 1.5，是同一条任务栏）。
        Assert.True(
            workingArea.Width < bounds.Width || workingArea.Height < bounds.Height,
            $"工作区与整屏一样大（work={workingArea} bounds={bounds}），本条无法区分 WorkingArea 与 Bounds");
    }

    [Fact]
    public void ScreenScaling_IsAMultiplierOfNinetySixDpi_NotAPercentage()
    {
        using var scenario = OverlayScenario.Create();
        var dpi = NativeProbe.GetDpiForWindow(scenario.OverlayHandle);

        var scaling = AvaloniaTestHost.Run(() => scenario.Overlay.Screens.Primary!.Scaling);

        // 若它是「百分数」，150% 屏上会读到 150 而不是 1.5。
        // （这条判据在 100% 屏上曾经很弱：1 与 100 的区别虽在，但 1.0 这个值本身没什么信息量。
        //   现在本机是 150%，它读到 1.5，语义「相对 96 DPI 的倍数」是在非 1 的值上验的。）
        Assert.Equal(dpi / 96.0, scaling, 6);
    }

    [Fact]
    public void Reposition_LeavesExactlyTheSpecifiedMarginToTheBottomRightOfTheWorkArea()
    {
        using var scenario = OverlayScenario.Create();
        var monitor = NativeProbe.MonitorInfoFor(scenario.OverlayHandle);
        var scaling = AvaloniaTestHost.Run(() => scenario.Overlay.Screens.Primary!.Scaling);
        var marginPx = (int)Math.Round(MarginDip * scaling, MidpointRounding.AwayFromZero);

        AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.Reposition();
            return 0;
        });

        var rect = scenario.OverlayRectWhen(r => r.Right == monitor.Work.Right - marginPx);
        Assert.Equal(monitor.Work.Right - marginPx, rect.Right);
        Assert.Equal(monitor.Work.Bottom - marginPx, rect.Bottom);
        Assert.True(rect.Left >= monitor.Work.Left, $"左缘跑出工作区：{rect} vs {monitor.Work}");
        Assert.True(rect.Top >= monitor.Work.Top, $"上缘跑出工作区：{rect} vs {monitor.Work}");
    }

    [Fact]
    public void WhenTheWindowIsAt150Percent_ItsDipSizeBecomesOneAndAHalfTimesAsManyPixels()
    {
        using var scenario = OverlayScenario.Create();

        var renderScaling = ForceOneAndAHalfTimesDpi(scenario);

        Assert.Equal(1.5, renderScaling, 6);

        var rect = scenario.OverlayRectWhen(r => r.Width == 300);
        Assert.Equal(300, rect.Width);
        Assert.Equal(72, rect.Height);
    }

    [Fact]
    public void WhenTheWindowIsAt150Percent_ThePlacementStillKeepsItInsideTheWorkArea()
    {
        // **这一条是整个 DPI 换算的判据。** 换算方向若写反（除而不是乘），
        // 窗口实占 300 物理像素，而除法会按 133 算、不换算会按 200 算，
        // 落点都会往右挪出工作区一大截。两种错法这条都转红（首轮在 100% 屏上实测各红 5 条）。
        using var scenario = OverlayScenario.Create();
        var monitor = NativeProbe.MonitorInfoFor(scenario.OverlayHandle);

        var scaling = ForceOneAndAHalfTimesDpi(scenario);
        var marginPx = (int)Math.Round(MarginDip * scaling, MidpointRounding.AwayFromZero);
        scenario.OverlayRectWhen(r => r.Width == 300);

        AvaloniaTestHost.Run(() =>
        {
            var screen = scenario.Overlay.Screens.Primary!;

            // 喂给 Compute 的 scaling 是窗口**真实的** RenderScaling（1.5），
            // 尺寸是 XAML 里声明的 DIP 值。落点算完就写进 Position，随后拿 Win32 量。
            scenario.Overlay.Position = OverlayPlacement.Compute(
                screen.WorkingArea,
                scenario.Overlay.Width,
                scenario.Overlay.Height,
                scaling,
                MarginDip);
            return 0;
        });

        var rect = scenario.OverlayRectWhen(r => r.Right == monitor.Work.Right - marginPx);
        Assert.Equal(300, rect.Width);
        Assert.Equal(monitor.Work.Right - marginPx, rect.Right);
        Assert.Equal(monitor.Work.Bottom - marginPx, rect.Bottom);
        Assert.True(
            rect.Left >= monitor.Work.Left && rect.Right <= monitor.Work.Right,
            $"150% 下悬浮条横向跑出工作区：{rect} vs {monitor.Work}");
    }

    /// <summary>
    /// 把窗口推到 150%，返回它自己报的 <c>RenderScaling</c>。
    ///
    /// <para>
    /// **建议矩形故意给错的**（500×200 而不是 300×72）：不这么做就分不清
    /// 「Avalonia 按 DIP×scaling 算出了 300×72」和「它只是把我建议的数字原样照抄」。
    /// 实测收到错的建议时它照单全收，并把 <c>Width</c> 反算成 500÷1.5 = 333.33——
    /// 这本身就是「Width 是 DIP、物理像素 = Width × scaling」的一次正面确认。
    /// 随后把 <c>Width</c>/<c>Height</c> 显式设回 200/48，物理尺寸才变成 300×72。
    /// </para>
    /// </summary>
    private static double ForceOneAndAHalfTimesDpi(OverlayScenario scenario)
    {
        AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.Position = new PixelPoint(400, 300);
            NativeProbe.TellTheWindowItsDpiChanged(
                scenario.OverlayHandle,
                144,
                new NativeProbe.RECT { Left = 400, Top = 300, Right = 900, Bottom = 500 });
            return 0;
        });

        var reboundWidth = AvaloniaTestHost.Run(() => scenario.Overlay.Width);
        Assert.Equal(500 / 1.5, reboundWidth, 6);

        return AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.Width = 200;
            scenario.Overlay.Height = 48;
            return scenario.Overlay.RenderScaling;
        });
    }
}
