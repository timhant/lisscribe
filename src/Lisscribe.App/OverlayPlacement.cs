using Avalonia;

namespace Lisscribe.App;

/// <summary>
/// 悬浮指示器的落点计算：主屏工作区右下角，距边 16px。
/// </summary>
/// <remarks>
/// 单独抽出来是因为它是 App 层唯一值得单测、也最容易搞错的一块：<c>Screen.WorkingArea</c> /
/// <c>Window.Position</c> 是物理像素，而 <c>Window.Width/Height</c> 是 DIP。混用在 100% 屏上
/// 「看起来对」，150% 屏上偏移约三分之一，200% 屏上跑出屏幕一半。
/// <para>
/// 这条前提在一块真的 150% 屏上直接坐实过：把 <c>Window.Position</c> 设成某个值之后绕开 Avalonia
/// 直接问 <c>GetWindowRect</c>，读回的 left/top 一字不差；<c>Screen.WorkingArea</c> 与
/// <c>GetMonitorInfo</c> 的 <c>rcWork</c> 逐字段相等（X 非零，不是退化样本），即这两个量确实活在
/// Win32 的屏幕坐标里。而 <c>Window.Width</c> 200 / <c>ClientSize</c> 200×48（DIP）对应
/// <c>GetWindowRect</c> 量到的 300×72——故 px = dip × scaling，乘不是除。
/// <c>Screen.Scaling</c> 读到的是 1.5 而不是 150，即「相对 96 DPI 的倍数」。
/// </para>
/// </remarks>
public static class OverlayPlacement
{
    public static PixelPoint Compute(
        PixelRect workingArea,
        double windowWidthDip,
        double windowHeightDip,
        double scaling,
        int marginDip)
    {
        // NaN / 0 / 负数不是「边界情形」，是「这次算出来的坐标全是垃圾」，而且一声不响。
        // .NET Core 起浮点转整数是饱和的，不抛也不溢出（NaN → 0、+∞ → int.MaxValue）。
        // 于是 scaling = NaN 时宽高边距三个一起变成 0，落点正好是工作区右下角那个顶点，窗体整个
        // 挂在屏幕外面——屏幕上什么也看不见，日志里什么也没有，用户只知道「指示器不见了」。
        // +∞ 更隐蔽：widthPx = marginPx = int.MaxValue，而下面那两句减法在 unchecked 下回绕，
        // 算出来的坐标看着还落在屏内。
        // 另一种失败形态与这里不同，别张冠李戴：宽高为 NaN（XAML 丢了 Width/Height）而 scaling
        // 正常时，只有宽高变 0、边距仍是 16，落点是「顶点减一个边距」，屏上还剩右下角一小块。
        if (!double.IsFinite(scaling) || scaling <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scaling), scaling, "屏幕缩放比例必须是正的有限数");
        }

        // DIP → 物理像素。边距也要乘，否则高 DPI 屏上视觉边距会缩水到 1/scaling。
        // AwayFromZero 不是随手写的：Math.Round 默认走 ToEven，252.5 会变成 252。尺寸往大里算，
        // 落点就往屏内挪；宁可少一像素边距，也不要多一像素溢出。
        var widthPx = (int)Math.Round(windowWidthDip * scaling, MidpointRounding.AwayFromZero);
        var heightPx = (int)Math.Round(windowHeightDip * scaling, MidpointRounding.AwayFromZero);
        var marginPx = (int)Math.Round(marginDip * scaling, MidpointRounding.AwayFromZero);

        var x = workingArea.X + workingArea.Width - widthPx - marginPx;
        var y = workingArea.Y + workingArea.Height - heightPx - marginPx;

        // 窗口比屏幕还大时（不该发生，但别让它跑到工作区外面去）。
        // 夹到 workingArea.X/Y 而不是 0：副屏在主屏右侧时工作区 X 是 1920，夹到 0 等于把悬浮条扔回
        // 主屏左上角；副屏在左侧时工作区 X 是负数，夹到 0 又把它推出了那块屏。
        // 两轴各夹各的——只有一轴溢出时另一轴不该跟着回原点。
        return new PixelPoint(
            Math.Max(workingArea.X, x),
            Math.Max(workingArea.Y, y));
    }
}
