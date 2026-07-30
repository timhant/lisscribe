using Lisscribe.Platform.Native;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 悬浮条的点击穿透样式：建一个真窗口、调一次、把 GWL_EXSTYLE 读回来看。
/// </summary>
[Collection(InjectedInputCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class OverlayWindowStylerTests
{
    /// <summary>四位，不是三位：<c>WS_EX_LAYERED</c> 少不得，理由见下面那条穿透测试。</summary>
    private const nint AllFour =
        User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT |
        User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;

    /// <summary>
    /// WS_EX_ACCEPTFILES | WS_EX_APPWINDOW。挑这两位只因为**系统对它们不作任何自动维护**，
    /// 且与被测的四位互不重叠——它们在这里只是「窗口原本带着的东西」的替身。
    /// </summary>
    private const uint Marker = 0x00000010 | 0x00040000;

    [Fact]
    public void MakeClickThrough_SetsAllFourExtendedStyleBits()
    {
        using var window = new TestWindow("overlay-bits");

        // 阴性对照：动手之前这四位一个也没有。没有它，「四位都在」也可能是窗口天生就带着，
        // 于是一个什么都不做的实现照样绿。
        var before = User32.GetWindowLongPtr(window.Handle, User32.GWL_EXSTYLE);
        Assert.Equal(0, before & AllFour);

        new OverlayWindowStyler().MakeClickThrough(window.Handle);

        var after = User32.GetWindowLongPtr(window.Handle, User32.GWL_EXSTYLE);
        Assert.Equal(User32.WS_EX_LAYERED, after & User32.WS_EX_LAYERED);
        Assert.Equal(User32.WS_EX_TRANSPARENT, after & User32.WS_EX_TRANSPARENT);
        Assert.Equal(User32.WS_EX_NOACTIVATE, after & User32.WS_EX_NOACTIVATE);
        Assert.Equal(User32.WS_EX_TOOLWINDOW, after & User32.WS_EX_TOOLWINDOW);
    }

    [Fact]
    public void MakeClickThrough_KeepsTheExtendedStylesTheWindowAlreadyHad()
    {
        // Avalonia 建好的窗口自带一串扩展样式（置顶、分层窗口等）。直接赋值而不是或上去，
        // 会把它们统统抹掉——悬浮条从此不再置顶，而那四位的断言照样全绿。
        //
        // **记号必须挑系统绝不插手的位。** 拿普通窗口动手前后的整串样式相比是空测：
        // 普通窗口那时只有 WS_EX_WINDOWEDGE(0x100)，而它由系统按 WS_THICKFRAME 自动维护
        // ——SetWindowLongPtr 抹掉之后系统又加回来，「赋值而非或」这个变异照样全绿。
        using var window = new TestWindow("overlay-preserve", extendedStyle: Marker);
        var before = User32.GetWindowLongPtr(window.Handle, User32.GWL_EXSTYLE);
        Assert.Equal(Marker, before & Marker);

        new OverlayWindowStyler().MakeClickThrough(window.Handle);

        var after = User32.GetWindowLongPtr(window.Handle, User32.GWL_EXSTYLE);
        Assert.Equal(Marker, after & Marker);
    }

    /// <summary>
    /// <b>WS_EX_TRANSPARENT 一个位换不来点击穿透</b>。两个真窗口上下叠放、各自数
    /// <c>PointerMoved</c> 逐一实测：只置 TRANSPARENT（含事后补一次
    /// <c>SetWindowPos(SWP_FRAMECHANGED)</c>）、只置 LAYERED，鼠标都仍被上方窗口吃掉；
    /// 唯 LAYERED + TRANSPARENT 才穿透。
    ///
    /// <para>
    /// 道理在 MSDN 的 Layered Windows 一节：命中测试按分层窗口的形状与透明度算，
    /// 而 WS_EX_TRANSPARENT 的作用是让分层窗口的形状被忽略、鼠标落到下面去——它是分层语义的
    /// 一部分。窗口不分层，这一位就只剩「画的次序」那层含义。
    /// </para>
    /// <para>
    /// 加 LAYERED <b>不会</b>让 Avalonia 的窗口消失，<c>ActualTransparencyLevel</c> 也仍是
    /// AcrylicBlur：那条「分层窗口在调 SetLayeredWindowAttributes 之前根本不显示」的老规矩在这里
    /// 不适用，因为 Avalonia 的窗口带 WS_EX_NOREDIRECTIONBITMAP，走的是 DirectComposition。
    /// </para>
    /// </summary>
    [Fact]
    public void MakeClickThrough_AlsoMakesTheWindowLayered_BecauseTransparentAloneDoesNotPassMouseThrough()
    {
        using var window = new TestWindow("overlay-layered");

        // 阴性对照：普通窗口天生不分层。
        var before = User32.GetWindowLongPtr(window.Handle, User32.GWL_EXSTYLE);
        Assert.Equal(0, before & User32.WS_EX_LAYERED);

        new OverlayWindowStyler().MakeClickThrough(window.Handle);

        var after = User32.GetWindowLongPtr(window.Handle, User32.GWL_EXSTYLE);
        Assert.Equal(User32.WS_EX_LAYERED, after & User32.WS_EX_LAYERED);
    }

    /// <summary>
    /// 分层属性也得真的设上。
    ///
    /// <b>它不是「让穿透生效」的那一步</b>——只置 LAYERED 位、一次
    /// <c>SetLayeredWindowAttributes</c> 也不调，穿透已经成了。留着它防的是另一件事：
    /// 若日后渲染后端换成不带 WS_EX_NOREDIRECTIONBITMAP 的形态，「分层窗口在设过属性之前不显示」
    /// 那条老规矩就会回来，表现是<b>悬浮条整个看不见</b>。<b>这一条属于推理，未实测</b>。
    /// </summary>
    [Fact]
    public void MakeClickThrough_SetsTheLayeredAttributesSoTheWindowIsStillDrawn()
    {
        using var window = new TestWindow("overlay-layered-attrs");

        // 阴性对照：没设过属性时这个函数返回 FALSE。
        Assert.False(TestNative.GetLayeredWindowAttributes(window.Handle, out _, out _, out _));

        new OverlayWindowStyler().MakeClickThrough(window.Handle);

        Assert.True(
            TestNative.GetLayeredWindowAttributes(window.Handle, out _, out var alpha, out var flags),
            "分层属性没设上");
        Assert.Equal(byte.MaxValue, alpha);
        Assert.Equal(TestNative.LWA_ALPHA, flags);
    }

    [Fact]
    public void MakeClickThrough_ForZeroHandle_Throws()
    {
        Assert.Throws<ArgumentException>(() => new OverlayWindowStyler().MakeClickThrough(nint.Zero));
    }

    [Fact]
    public void MakeClickThrough_WhenTheSystemRefuses_Throws()
    {
        // 「返回值不许丢」的钉子。样式没设上的后果是悬浮条挡住托盘图标、抢走焦点使上屏打到
        // 自己身上，而程序端毫无察觉。SetWindowLongPtr 成功时也可能返回 0，只能靠「调用前清零
        // last error」区分成败——LibraryImport 生成的桩恰好在调用前执行
        // Marshal.SetLastSystemError(0)，这条测试同时钉住了那个前提。
        //
        // **它钉的是「两处判错至少还剩一处」，不是各自独立**：只删读那侧或只删写那侧，
        // 本条都仍绿（另一侧接着抛），两侧都删才转红。要各自独立地杀，得造出「读得到、写不进」
        // 的现场，那需要别的进程的窗口——本进程内没有这种句柄，拿桌面窗口去试是在用用户的桌面
        // 做实验，故不做。两处判错都留着：从一次已知失败的读里算出 updated 再写下去，比不写更坏。
        //
        // SetLayeredWindowAttributes 那一步不在这笔账里，它**故意不抛**（调用点在 Opened 上，
        // 一条抛出路径会把异常送回状态机的线程）。「那一步失败了会怎样」没有测试杀得掉，
        // 只靠上面那条读回属性的测试保证正常路径上它真被调过。
        var window = new TestWindow("overlay-invalid");
        var handle = window.Handle;
        window.Dispose();
        Assert.False(window.StillExists(), "窗口没被真的销毁，本条断言无效");

        Assert.Throws<InvalidOperationException>(
            () => new OverlayWindowStyler().MakeClickThrough(handle));
    }
}
