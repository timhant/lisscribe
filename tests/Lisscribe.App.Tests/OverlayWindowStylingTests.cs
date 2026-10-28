using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Threading;
using Lisscribe.App.ViewModels;
using Lisscribe.App.Views;
using Lisscribe.Core.Session;
using Lisscribe.Platform;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 悬浮条在真桌面上的行为：点击穿透、不抢焦点、样式在 Show 之后还在不在。纯单测在这里不够用——它读的
/// 和写的是同一份窗口数据，验不出样式是否真生效。本组会短暂移动用户的真光标（点击穿透没有别的判据：
/// <c>WindowFromPoint</c> 不认 <c>WS_EX_TRANSPARENT</c>），用完必还原，见 <see cref="CursorParked"/>。
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class OverlayWindowStylingTests
{
    private const nint AllFour =
        NativeProbe.WS_EX_TRANSPARENT | NativeProbe.WS_EX_LAYERED |
        NativeProbe.WS_EX_NOACTIVATE | NativeProbe.WS_EX_TOOLWINDOW;

    /// <summary>两个窗口叠放的地方，挑屏幕中间偏上，避开任务栏与右下角的托盘。</summary>
    private const int BelowX = 500;
    private const int BelowY = 400;
    private const int OverlapX = 560;
    private const int OverlapY = 440;

    [Fact]
    public void TheOverlay_CarriesAllFourClickThroughStylesOnceItIsShown()
    {
        using var scenario = OverlayScenario.Create(show: false);

        // 阴性对照：Avalonia 建好的窗口一个也不带（实测天生只有
        // WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP = 0x200008）。
        Assert.Equal(0, scenario.ExtendedStyle() & AllFour);

        AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.Show();
            return 0;
        });

        var style = scenario.ExtendedStyle();
        Assert.Equal(NativeProbe.WS_EX_TRANSPARENT, style & NativeProbe.WS_EX_TRANSPARENT);
        Assert.Equal(NativeProbe.WS_EX_LAYERED, style & NativeProbe.WS_EX_LAYERED);
        Assert.Equal(NativeProbe.WS_EX_NOACTIVATE, style & NativeProbe.WS_EX_NOACTIVATE);
        Assert.Equal(NativeProbe.WS_EX_TOOLWINDOW, style & NativeProbe.WS_EX_TOOLWINDOW);
    }

    [Fact]
    public void EveryShow_WipesTheStyles_SoTheyMustBeReAppliedEveryTime()
    {
        // Avalonia 的 Show() 会把 GWL_EXSTYLE 整个写回它自己那一份，四个位全没，故样式必须设在 Opened
        // 里、每次 Show 都重设——「稳妥起见在 Show() 之前调」是错的。
        //
        // 阳性对照放在同一条用例里：一个裸窗口在 Show() 之前设好样式，Show() 之后一位不剩——没有它，
        // 下半段的「还在」也可能只是「Show 根本不动样式」。
        var wiped = AvaloniaTestHost.Run(() =>
        {
            var bare = new Window
            {
                Width = 200,
                Height = 48,
                SystemDecorations = SystemDecorations.None,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            var handle = bare.TryGetPlatformHandle()!.Handle;
            new OverlayWindowStyler().MakeClickThrough(handle);
            var beforeShow = NativeProbe.GetWindowLongPtr(handle, NativeProbe.GWL_EXSTYLE);
            bare.Show();
            var afterShow = NativeProbe.GetWindowLongPtr(handle, NativeProbe.GWL_EXSTYLE);
            bare.Close();
            return (BeforeShow: beforeShow, AfterShow: afterShow);
        });

        Assert.Equal(AllFour, wiped.BeforeShow & AllFour);
        Assert.Equal(0, wiped.AfterShow & AllFour);

        // 真的悬浮条：连着 Show / Hide / Show 三轮，四个位每次都还在。
        using var scenario = OverlayScenario.Create();
        Assert.Equal(AllFour, scenario.ExtendedStyle() & AllFour);

        AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.Hide();
            scenario.Overlay.Show();
            return 0;
        });

        Assert.Equal(AllFour, scenario.ExtendedStyle() & AllFour);
    }

    [Fact]
    public void TheMouseGoesThroughTheOverlay_ToWhateverIsUnderneath()
    {
        using var parked = new CursorParked();
        using var scenario = OverlayScenario
            .Create()
            .WithWindowUnderneath(new PixelPoint(BelowX, BelowY), new PixelSize(400, 200));
        scenario.MoveOverlayTo(new PixelPoint(OverlapX - 40, OverlapY - 20));
        scenario.OverlayRectWhen(r => r.Left == OverlapX - 40);

        var through = scenario.SweepCursorOver(OverlapX, OverlapY);

        Assert.True(through.Below > 0, $"下方窗口一次鼠标移动也没收到（below={through.Below} overlay={through.Overlay}）");
        Assert.Equal(0, through.Overlay);
    }

    [Fact]
    public void WithoutTheClickThroughStyles_TheOverlayWouldSwallowTheMouse()
    {
        // 上一条的阳性对照，单独立一条：**没有它，「下方收到了」也可能只是因为悬浮条压根没盖住**。
        // 这里用一个裸 Avalonia 窗口冒充悬浮条，几何完全相同，只是不设样式。
        using var parked = new CursorParked();
        using var scenario = OverlayScenario
            .Create(show: false)
            .WithWindowUnderneath(new PixelPoint(BelowX, BelowY), new PixelSize(400, 200));

        var fake = AvaloniaTestHost.Run(() =>
        {
            var window = new Window
            {
                Width = 200,
                Height = 48,
                SystemDecorations = SystemDecorations.None,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.PointerMoved += (_, _) => Interlocked.Increment(ref scenario.OverlayPointerMoves);
            window.Show();
            window.Position = new PixelPoint(OverlapX - 40, OverlapY - 20);
            return window;
        });

        try
        {
            Thread.Sleep(300);
            var blocked = scenario.SweepCursorOver(OverlapX, OverlapY);

            Assert.True(blocked.Overlay > 0, "冒充的悬浮条一次鼠标移动也没收到，说明它根本没盖住下方窗口，本组测试无效");
            Assert.Equal(0, blocked.Below);
        }
        finally
        {
            AvaloniaTestHost.Run(() =>
            {
                fake.Close();
                return 0;
            });
        }
    }

    [Fact]
    public void TheLayeredStyle_DoesNotMakeTheOverlayInvisible_AndTheRestOfTheWindowReallyIsTransparent()
    {
        // 加 WS_EX_LAYERED 有一条老规矩：分层窗口在调过 SetLayeredWindowAttributes 之前不显示。这条问
        // 的是屏幕本身——胶囊盖住的那一点，颜色必须不是下方那个纯蓝窗口的颜色。
        //
        // 第二问：窗口是块 320×48 的透明画布，只有右下角那颗 30 DIP 高的胶囊有颜色（Windows 不许顶层
        // 窗口矮于 SM_CYMIN=39，胶囊只能长在更大的窗口里）。若透明度提示落不下来、退回不透明，用户看到
        // 的就是一个更大的深色方块，而只取胶囊那一点照样能过。故取两点：胶囊里一点、上方透明带里一点。
        using var scenario = OverlayScenario
            .Create()
            .WithWindowUnderneath(new PixelPoint(BelowX, BelowY), new PixelSize(400, 200));
        scenario.MoveOverlayTo(new PixelPoint(OverlapX - 40, OverlapY - 20));

        // 撑满胶囊：文字短的时候胶囊只有几十 DIP 宽，取样点会落到窗口的透明区去。
        AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(
                SessionState.Failed, "识别引擎加载失败：模型目录里少了 tokens.txt，请补齐后重试");
            return 0;
        });

        var rect = scenario.OverlayRectWhen(r => r.Left == OverlapX - 40);
        Thread.Sleep(400);

        // 取样点按窗口的**真实矩形**算，不写死像素：屏幕缩放一改，写死的偏移就落到别处去，
        // 而那种失败看起来像「样式没生效」。890 是下方蓝窗口的右缘（500+400）往里让一点。
        var probeX = Math.Min(rect.Right - 40, 890);
        var insidePill = OverlayScenario.ScreenPixelAt(probeX, rect.Bottom - 8);
        var aboveThePill = OverlayScenario.ScreenPixelAt(probeX, rect.Top + 4);
        var onlyBelow = OverlayScenario.ScreenPixelAt(BelowX + 20, BelowY + 150);

        // 阴性对照：只有下方窗口的地方必须真的是纯蓝（COLORREF 低位是蓝）。
        // 不成立就说明这几点根本没落在预期的窗口上，下面两句断言也就没意义。
        Assert.Equal(0xFF0000, onlyBelow);

        Assert.NotEqual(onlyBelow, insidePill);
        Assert.Equal(onlyBelow, aboveThePill);
    }

    [Fact]
    public void ShowingTheOverlay_DoesNotStealTheForegroundWindow()
    {
        // 说准这一条钉住什么：它验的是「显示悬浮条不会换走前台窗口」这个行为。删掉 `| WS_EX_NOACTIVATE`
        // 本条照绿——`ShowActivated=false` 已令 Avalonia 走 SW_SHOWNOACTIVATE，独力满足了断言。故那一位
        // 本身与 `WS_EX_TOOLWINDOW` 同弱，只验到「位上了」。
        using var foreground = new ForegroundParked();

        // 阳性对照：先证明这个测试进程**抢得动**前台——抢不动的话，「悬浮条没抢走」
        // 就只是因为谁都抢不走，整条是空断言。
        var normal = AvaloniaTestHost.Run(() =>
        {
            var window = new Window { Width = 300, Height = 120, ShowActivated = true };
            window.Show();
            window.Activate();
            return window;
        });

        try
        {
            var normalHandle = AvaloniaTestHost.Run(() => normal.TryGetPlatformHandle()!.Handle);

            // `Show()` + `Activate()` 不够，会时灵时不灵：桌面上有别的进程的窗口拿着前台时，前台锁定
            // 规则不准我们抢，`Activate()` 一声不响地不生效（摆局复现过，3/3 红在下面这句阳性对照上）。
            // 抢法一律走 NativeProbe.BringToForeground。
            Assert.True(
                NativeProbe.BringToForeground(normalHandle),
                "本进程抢不到前台，本条无法区分「悬浮条没抢」与「谁都抢不到」");

            using var scenario = OverlayScenario.Create();
            Thread.Sleep(300);

            Assert.Equal(normalHandle, NativeProbe.GetForegroundWindow());
        }
        finally
        {
            AvaloniaTestHost.Run(() =>
            {
                normal.Close();
                return 0;
            });
        }
    }

    [Fact]
    public void TheOverlayFollowsTheViewModelsVisibility()
    {
        // IsVisible 只挂在 ViewModel 上是不够的——得有东西真的显示/隐藏窗口。这条钉的就是它。
        using var scenario = OverlayScenario.Create(show: false);

        Assert.False(NativeProbe.IsWindowVisible(scenario.OverlayHandle));

        AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(SessionState.Recording, null);
            return 0;
        });
        Assert.True(NativeProbe.IsWindowVisible(scenario.OverlayHandle), "进入录音态之后悬浮条没显示出来");

        AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(SessionState.Idle, null);
            return 0;
        });
        Assert.False(NativeProbe.IsWindowVisible(scenario.OverlayHandle), "回到空闲态之后悬浮条没藏起来");
    }

    [Fact]
    public void TheOverlayKeepsItsDeclaredDipSize()
    {
        // Reposition 把 Width/Height 当 DIP 尺寸喂给 Compute。XAML 里丢了这两个值，Width 会变成 NaN，
        // 而 .NET 的浮点转整数是饱和的（(int)NaN == 0），于是宽高算成 0、边距仍是 16，落点变成工作区
        // 右下角顶点减一个边距，只剩 16×16 那么一小块还在屏内——不抛，只是「指示器几乎看不见了」。
        //
        // 断言写死数值，不写「是个有限正数」：后者把 Width 改成 20 也照样绿。
        //
        // 窗口的尺寸不是用户看得见的尺寸：窗口是块透明画布，看得见的是里面那颗右下对齐的胶囊。窗口尺寸
        // 也不能照胶囊来写——Windows 的最小窗口尺寸是 SM_CXMIN=136 / SM_CYMIN=39，矮于它就被系统夹上去，
        // Avalonia 再把夹后的真尺寸写回 Width/Height（按 30 DIP 声明的实测读回 39.33）。
        using var scenario = OverlayScenario.Create();

        var (width, height, pill) = AvaloniaTestHost.Run(() => (
            scenario.Overlay.Width,
            scenario.Overlay.Height,
            scenario.Overlay.FindControl<Border>("Pill")!.Height));

        Assert.Equal(320d, width);
        Assert.Equal(48d, height);
        Assert.Equal(30d, pill);

        // XAML 里的字面量与 C# 常量必须是同一个数——重排算的是窗口，而注释讲的是胶囊，
        // 两处各写一份就一定有一天只改一处。
        Assert.Equal(OverlayWindow.WidthDip, width);
        Assert.Equal(OverlayWindow.HeightDip, height);
        Assert.Equal(OverlayWindow.PillHeightDip, pill);

        // 旧版胶囊是 200×48 且下半 24 DIP 恒空，实机反馈是「太大了」。
        Assert.True(pill < 48d, "胶囊没比旧的矮");
    }

    /// <summary>
    /// 录音中与识别中的胶囊必须一模一样大。原始差距实测 117 对 293 DIP，大头不是文案，是 Fluent 主题给
    /// <c>ProgressBar</c> 的 <c>MinWidth</c>（200）压过了 XAML 里写的 <c>Width</c>。
    /// <para>
    /// 判据取渲染后的实际尺寸，不是 XAML 里那几个数：后者要靠人把「主题有没有 MinWidth」和「三个汉字
    /// 有多宽」算对，而汉字并不等宽——「录音中」与「识别中」同为三字实测差 2 DIP。故宽度由常量定死，
    /// 不指望两边内容碰巧一样宽。
    /// </para>
    /// </summary>
    [Fact]
    public void TheRecognizingPill_IsExactlyTheSameSizeAsTheRecordingOne()
    {
        using var scenario = OverlayScenario.Create();

        var recording = PillSizeFor(scenario, SessionState.Recording);
        var recognizing = PillSizeFor(scenario, SessionState.Recognizing);

        // 阳性对照：真量到了东西。两态都排不出版时尺寸同为 0，下面那句就是空断言。
        Assert.True(recording.Width > 0 && recording.Height > 0, $"没量到胶囊：{recording}");

        Assert.Equal(recording, recognizing);

        // 顺带钉住「定死的那个数真的落到了屏幕上」——只比两态相等的话，
        // 把 Width 绑丢了、两态又碰巧一样宽时它照样绿。
        Assert.Equal(OverlayViewModel.NormalPillWidthDip, recording.Width);
    }

    [Fact]
    public void TheFailureNotice_IsAllowedToGrow_SoTheReasonIsReadable()
    {
        // 定死宽度只管录音、识别、转写这三态。失败态那句是唯一带排障线索的，
        // 按 132 DIP 画就只剩一串省略号，等于没报错。
        using var scenario = OverlayScenario.Create();

        var normal = PillSizeFor(scenario, SessionState.Recording);

        var notice = AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(
                SessionState.Failed, "识别引擎加载失败：模型目录里少了 tokens.txt，请补齐后重试");
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

            return scenario.Overlay.FindControl<Border>("Pill")!.Bounds.Size;
        });

        Assert.True(notice.Width > normal.Width, $"失败态没有变宽：{notice} vs {normal}");

        // 但不许长出窗口——窗口是块 320 DIP 宽的画布，超出去的部分画不出来。
        Assert.True(notice.Width <= OverlayWindow.WidthDip, $"胶囊 {notice.Width} 撑出了窗口 {OverlayWindow.WidthDip}");
    }

    /// <summary>
    /// 转写态右侧也挂那条不确定进度：百分比要识别完一段才跳一格，可能十来秒不动，看着像卡住。
    /// <para>
    /// 挂上之后文字列窄了 33 DIP，最长那句「转写中 100%」不许被截成省略号。判据独立于被测代码：
    /// 照 <c>MessageText</c> 的字体与字号另排一份不设宽度上限的文字量出它本该多宽，不拿生产常量去算。
    /// </para>
    /// </summary>
    [Fact]
    public void TheTranscribingPill_ShowsTheIndeterminateBar_AndStillFitsTheLongestProgress()
    {
        const string Longest = "转写中 100%";

        using var scenario = OverlayScenario.Create();

        var shortest = PillSizeFor(scenario, SessionState.Transcribing, "转写中 0%");

        var longest = AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(SessionState.Transcribing, Longest);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

            var bar = scenario.Overlay.FindControl<ProgressBar>("IndeterminateBar")!;
            var text = scenario.Overlay.FindControl<TextBlock>("MessageText")!;

            // 独立量尺：字体与字号是输入，取自 MessageText；宽度上限不设，量出这句话本该占多宽。
            var ruler = new TextLayout(
                Longest,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize,
                null);

            return new
            {
                Pill = scenario.Overlay.FindControl<Border>("Pill")!.Bounds.Size,
                BarVisible = bar.IsEffectivelyVisible,
                Bar = bar.Bounds.Size,
                Shown = text.Text,
                Needed = ruler.WidthIncludingTrailingWhitespace,
                Slot = text.Bounds.Width,
                Collapsed = text.TextLayout.TextLines.Any(line => line.HasCollapsed),
            };
        });

        // 阳性对照：那条进度真的画出来了，且排的确实是最长那句。
        Assert.True(longest.BarVisible, "转写态没有显示不确定进度条");
        Assert.True(longest.Bar.Width > 0 && longest.Bar.Height > 0, $"不确定进度条没排出尺寸：{longest.Bar}");
        Assert.Equal(Longest, longest.Shown);

        // 文字没被截断：文字列放得下这句话的实际宽度，且排版没有折叠出省略号。
        Assert.True(
            longest.Slot >= longest.Needed,
            $"「{Longest}」需 {longest.Needed:F2} DIP，文字列只有 {longest.Slot:F2} DIP，胶囊 {longest.Pill}，折叠 {longest.Collapsed}");
        Assert.False(longest.Collapsed, $"「{Longest}」被截成了省略号（需 {longest.Needed:F2}，列宽 {longest.Slot:F2}）");

        // 进度每跳一格整颗胶囊不许抖。
        Assert.Equal(shortest, longest.Pill);

        // 窗口是块 320 DIP 宽的画布，超出去的部分画不出来。
        Assert.True(longest.Pill.Width <= OverlayWindow.WidthDip, $"胶囊 {longest.Pill.Width} 撑出了窗口 {OverlayWindow.WidthDip}");
    }

    /// <summary>把悬浮条切到某一态，排完版，量胶囊的实际尺寸。</summary>
    private static Size PillSizeFor(OverlayScenario scenario, SessionState state, string? message = null) =>
        AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(state, message);

            // 布局排在 dispatcher 队列上跑，不抽干就会量到上一态的尺寸。
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

            return scenario.Overlay.FindControl<Border>("Pill")!.Bounds.Size;
        });

    [Fact]
    public void ClosingTheOverlay_UnsubscribesFromTheScreenChangedEvent()
    {
        // 在 Opened 里订阅 Screens.Changed 而从不退订是一条无上界的重复订阅：Opened 每次 Show 都会引发
        // （Show / Hide+Show / IsVisible=true 各来一次），而悬浮条每次会话都要 Show 一轮。故构造时订一次、
        // Closed 时退掉。
        //
        // 判据取自 Avalonia 自己那个事件字段的调用列表长度，不是被测代码记的账——让它自报「我订了几次」
        // 就是拿被测方自己的账当判据。
        using var scenario = OverlayScenario.Create();

        var afterThreeShows = AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.Hide();
            scenario.Overlay.Show();
            scenario.Overlay.Hide();
            scenario.Overlay.Show();
            return ScreensEventProbe.SubscriberCount(scenario.Overlay.Screens);
        });

        Assert.Equal(1, afterThreeShows);

        var afterClose = AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.Close();
            return ScreensEventProbe.SubscriberCount(scenario.Overlay.Screens);
        });

        Assert.Equal(0, afterClose);
    }

    /// <summary>跑完把光标放回原处。测试劫持用户的光标是可以的，不还回去就不行了。</summary>
    private sealed class CursorParked : IDisposable
    {
        private readonly NativeProbe.POINT _saved;

        public CursorParked() => NativeProbe.GetCursorPos(out _saved);

        public void Dispose() => NativeProbe.SetCursorPos(_saved.X, _saved.Y);
    }

    /// <summary>
    /// 同理，抢了前台也得还。尽力而为：Windows 的前台锁定规则本就不保证还得回去，而前台落在别人手里会
    /// 让整组注入型测试报出指不到真因的失败，故失败不抛，但一定要试。
    /// </summary>
    private sealed class ForegroundParked : IDisposable
    {
        private readonly nint _saved = NativeProbe.GetForegroundWindow();

        public void Dispose()
        {
            if (_saved != nint.Zero && NativeProbe.IsWindowVisible(_saved))
            {
                NativeProbe.SetForegroundWindow(_saved);
            }
        }
    }
}

/// <summary>
/// 落点这一路：悬浮条停在主屏工作区右下角，且要把「主屏切换 / 热插外屏」的行为说清楚。
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class OverlayRepositionTests
{
    [Fact]
    public void Reposition_UsesThePrimaryScreen_NotWhicheverScreenTheWindowHappensToBeOn()
    {
        // 本机只有一块屏，验不了「多屏时选的是主屏」（那半留在手工清单里）。能验的是：先把悬浮条挪到一个
        // 明显不对的位置，Reposition 之后它必须回到主屏工作区的右下角。
        //
        // 断言必须是精确落点，不能只写「没超出工作区」——那样把 Reposition() 整个短路掉它照样绿：窗口停在
        // (0,0)，「没超出」自然成立。
        using var scenario = OverlayScenario.Create();
        var monitor = NativeProbe.MonitorInfoFor(scenario.OverlayHandle);
        var scaling = AvaloniaTestHost.Run(() => scenario.Overlay.Screens.Primary!.Scaling);
        var marginPx = (int)Math.Round(OverlayWindow.MarginDip * scaling, MidpointRounding.AwayFromZero);

        scenario.MoveOverlayTo(new PixelPoint(0, 0));
        scenario.OverlayRectWhen(r => r.Left == 0);

        AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.Reposition();
            return 0;
        });

        var rect = scenario.OverlayRectWhen(r => r.Right == monitor.Work.Right - marginPx);
        Assert.Equal(monitor.Work.Right - marginPx, rect.Right);
        Assert.Equal(monitor.Work.Bottom - marginPx, rect.Bottom);
    }

    [Fact]
    public void Reposition_SurvivesAScreensChangedNotification()
    {
        // 订阅了 Screens.Changed 却在处理器里抛异常，等于每次热插外屏都崩一次。本机没法真的热插一块屏，
        // 故走 Avalonia 自己那个事件字段把它引发一次。说准了：注册走生产那条路，派发不是——探针
        // DynamicInvoke 的是那个委托字段本身，绕过了 Avalonia 自己的 raise 入口。被短路的因此有两件：
        // 「显示器真的变了」与「Avalonia 决定要不要发这条通知」。
        using var scenario = OverlayScenario.Create();
        scenario.MoveOverlayTo(new PixelPoint(0, 0));
        scenario.OverlayRectWhen(r => r.Left == 0);

        AvaloniaTestHost.Run(() =>
        {
            ScreensEventProbe.RaiseChanged(scenario.Overlay.Screens);
            return 0;
        });

        // 处理器真跑了：位置从 (0,0) 回到了右下角。
        Assert.NotEqual(0, scenario.OverlayRectWhen(r => r.Left != 0).Left);

        var monitor = NativeProbe.MonitorInfoFor(scenario.OverlayHandle);
        var rect = scenario.OverlayRect();
        Assert.True(rect.Right <= monitor.Work.Right, $"{rect} 超出工作区 {monitor.Work}");
    }

    [Fact]
    public void RepositionOn_TakesTheScalingAndWorkAreaFromTheScreenItIsGiven()
    {
        // 本条盖 `screen.Scaling → Compute` 那一步，而它是本组唯一与屏幕当前 DPI 无关的一条：把
        // `screen.Scaling` 硬写成 1.0 这个变异，在 100% 单屏上全组绿，改成 150% 之后同一个变异红 4 条
        // ——那几条既有测试只在非 100% 屏上盖得住这一步。
        //
        // 故本条让测试决定输入（传一块自造的 Screen），执行仍走生产那条路；工作区也取一个跟真显示器毫无
        // 关系的数值，顺带钉住「用的是给它的那块屏，不是 Screens.Primary」。
        //
        // 期望值是手算的，不是再调一次 Compute（那就成了自证）：
        //   x = 100 + 800 − round(320×1.5) − round(16×1.5) = 396
        //   y =  50 + 600 − round( 48×1.5) − round(16×1.5) = 554
        // 若 scaling 被硬写成 1.0，则是 (464, 586)。
        using var scenario = OverlayScenario.Create();
        var fabricated = new Screen(
            scaling: 1.5,
            bounds: new PixelRect(100, 50, 800, 600),
            workingArea: new PixelRect(100, 50, 800, 600),
            isPrimary: false);

        AvaloniaTestHost.Run(() =>
        {
            scenario.Overlay.RepositionOn(fabricated);
            return 0;
        });

        var rect = scenario.OverlayRectWhen(r => r.Left == 396);
        Assert.Equal(396, rect.Left);
        Assert.Equal(554, rect.Top);
    }
}
