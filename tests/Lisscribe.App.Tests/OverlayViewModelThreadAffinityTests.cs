using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Lisscribe.App.ViewModels;
using Lisscribe.Core.Audio;
using Lisscribe.Core.Session;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 从非 UI 线程写 <see cref="OverlayViewModel"/> 的绑定属性会怎样。
/// <c>SessionController</c> 正是在非 UI 线程上引发 <c>VolumeChanged</c>（WASAPI 采集线程）
/// 与 <c>StatusChanged</c>（工作线程 / 热键线程 / 看门狗 / 会话监听 / 设备变更线程池线程）。
///
/// <para>
/// 分界不是「哪个方法」也不是「有没有改变可见性」，而是<b>这一次调用有没有越过它自己的提前返回
/// 哨兵</b>，哨兵盯的是窗口内部的已显示状态。故 <c>Show()</c> 与 <c>Hide()</c> 都可能抛，
/// 而「已显示的窗口再 Show」「未显示的窗口再 Hide」都不抛；抛过一次之后哨兵与实际可见性脱钩，
/// 「什么都没改」的调用照样抛。
/// </para>
/// <para>
/// 抛完之后 ViewModel 与窗口的可见性**不一致**，<c>ApplyState</c> 末尾那句 <c>Volume = 0</c>
/// 也一行没跑到。症状是「指示器从来不出现」或「一直亮着不消失」，都不像线程用错了。
/// 故订阅方必须 <c>Dispatcher.UIThread.Post</c>——<b>不许 <c>Invoke</c></b>，那是同步等 UI 线程，
/// 而收尾路径要 Join 采集线程，等于死锁。
/// </para>
/// <para>
/// <b>这一组没能测到什么</b>：不翻转可见性那一半造成的真实损害（布局队列被并发改坏、
/// 渲染读到半个值）一次也没复现出来，打 500 次也没事——单发竞争一次运行只有一次撞上的机会。
/// 那一半的价值只在于钉住「它不会告诉你」，而 <c>VolumeChanged</c> 恰恰只落在这一半。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class OverlayViewModelThreadAffinityTests
{
    [Fact]
    public void WritingABoundProperty_FromANonUiThread_DoesNotThrow()
    {
        using var scenario = OverlayScenario.Create();
        AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(SessionState.Recording, null);
            return 0;
        });

        // 前提：绑定真的建起来了，否则下面写什么都不经过 Avalonia，整条测试在跟自己对话。
        // 先在 UI 线程上写一次并读回控件，这是阳性对照。
        //
        // 读电平表最右那根柱子（最新一格）。柱高走对数刻度，故期望值过 VolumeScale 换一道
        // ——不能拿写进去的那个数当期望，那样等于让被测代码自己出题。
        var bar = AvaloniaTestHost.Run(() => scenario.Overlay.FindControl<Rectangle>("VolumeBar")!);
        AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.Volume = 0.25;
            return 0;
        });
        Assert.Equal(HeightFor(0.25), AvaloniaTestHost.Run(() => bar.Height), 6);

        var exception = Record.Exception(() => RunOffTheUiThread(() => scenario.ViewModel.Volume = 0.7));

        Assert.Null(exception);
        Assert.Equal(HeightFor(0.7), AvaloniaTestHost.Run(() => bar.Height), 6);
    }

    /// <summary>某个 RMS 该把最新那格柱子画多高（DIP）。与 ViewModel 同源但独立算一遍。</summary>
    private static double HeightFor(double rms) =>
        OverlayViewModel.BarMinHeightDip
        + (VolumeScale.ToBarLevel(rms) * (OverlayViewModel.BarMaxHeightDip - OverlayViewModel.BarMinHeightDip));

    [Fact]
    public void WritingTheMessage_FromANonUiThread_DoesNotThrow_AsLongAsTheVisibilityDoesNotFlip()
    {
        // 本条只主张「不翻转可见性时不抛」，且那个前提必须写进断言：Recognizing→Failed 的
        // IsVisible 恒为 true，setter 因值未变而短路、Show/Hide 一次也调不到。换成会翻转的
        // 目标态（如 Idle）当场就抛——不钉住前提，这条就是一句名不副实的空测。
        using var scenario = OverlayScenario.Create();
        AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(SessionState.Recognizing, "识别中");
            return 0;
        });

        var label = AvaloniaTestHost.Run(() => scenario.Overlay.FindControl<TextBlock>("MessageText")!);
        Assert.Equal("识别中", AvaloniaTestHost.Run(() => label.Text));
        Assert.True(scenario.ViewModel.IsVisible);

        // 改文字会连带触发重新测量与排版，比改一个 double 走得深得多。
        var exception = Record.Exception(
            () => RunOffTheUiThread(() => scenario.ViewModel.ApplyState(SessionState.Failed, "没有可用的录音设备")));

        Assert.Null(exception);
        Assert.True(scenario.ViewModel.IsVisible, "本条的前提是可见性没翻转；翻转了就该走另一条测试");
        Assert.Equal("没有可用的录音设备", AvaloniaTestHost.Run(() => label.Text));
    }

    [Fact]
    public void HidingTheOverlay_FromANonUiThread_Throws_AndLeavesViewModelAndWindowDisagreeing()
    {
        // Recognizing→Idle 是每一次会话收尾都会走的那一步。
        using var scenario = OverlayScenario.Create();
        AvaloniaTestHost.Run(() =>
        {
            scenario.ViewModel.ApplyState(SessionState.Recording, null);
            scenario.ViewModel.Volume = 0.5;
            return 0;
        });
        Assert.True(NativeProbe.IsWindowVisible(scenario.OverlayHandle));

        var exception = Record.Exception(
            () => RunOffTheUiThread(() => scenario.ViewModel.ApplyState(SessionState.Idle, null)));

        AssertWrongThread(exception);

        // 抛出后的现场三条都要钉：ViewModel 说该藏了、窗口还亮着（两边不一致），
        // 而 ApplyState 末尾那句 Volume = 0 一行也没跑到（异常在 IsVisible 的 setter 处抛出）。
        Assert.False(scenario.ViewModel.IsVisible);
        Assert.True(NativeProbe.IsWindowVisible(scenario.OverlayHandle), "抛完之后窗口居然藏起来了");
        Assert.Equal(0.5, scenario.ViewModel.Volume, 6);
    }

    [Fact]
    public void ShowingTheOverlay_FromANonUiThread_ThrowsToo_WhenItActuallyHasToShowSomething()
    {
        // 「Show 不查线程」是错的：那种读法出自在已显示的窗口上取的样本。
        // 让 Show 真有活要干（窗口从未显示过），它照样抛。
        using var scenario = OverlayScenario.Create(show: false);
        Assert.False(NativeProbe.IsWindowVisible(scenario.OverlayHandle));

        var exception = Record.Exception(
            () => RunOffTheUiThread(() => scenario.ViewModel.ApplyState(SessionState.Recording, null)));

        AssertWrongThread(exception);

        // 同样是两边不一致，只是方向反过来：ViewModel 说该显示了，窗口还是黑的。
        Assert.True(scenario.ViewModel.IsVisible);
        Assert.False(NativeProbe.IsWindowVisible(scenario.OverlayHandle), "抛完之后窗口居然显示出来了");
    }

    [Fact]
    public void AVisibilityChangeThatChangesNothing_DoesNotThrow_WhichIsHowThisGotMisreadThreeTimes()
    {
        // 陷阱格：窗口已显示着而 ViewModel 的 IsVisible 还是 false，此时跨线程 ApplyState
        // 翻转 IsVisible、进而调 Show()，但那一次 Show() 提前返回、什么也没做，于是不抛。
        //
        // 留着它是为标出「不抛」的条件有多窄——窄到只覆盖「这次调用本来就没事可做」。
        // 而「没事可做」判的是窗口内部的已显示状态、不是可见性本身，反例见
        // AfterOneThrow_TheRuleStopsHolding_*。
        using var scenario = OverlayScenario.Create();
        Assert.True(NativeProbe.IsWindowVisible(scenario.OverlayHandle));
        Assert.False(scenario.ViewModel.IsVisible);

        var exception = Record.Exception(
            () => RunOffTheUiThread(() => scenario.ViewModel.ApplyState(SessionState.Recording, null)));

        Assert.Null(exception);
        Assert.True(scenario.ViewModel.IsVisible);
        Assert.True(NativeProbe.IsWindowVisible(scenario.OverlayHandle));
    }

    [Fact]
    public void TheThreadCheck_FiresWhenTheCallGetsPastItsEarlyReturn_NotOnOneParticularMethod()
    {
        // 把分界线钉住，四格一起：Show / Hide × 有事可做 / 无事可做。
        //
        // 每一格各建各的窗口，绝不复用：抛完之后对象状态是不确定的，拿它继续量下一格会得到
        // 测试自己制造的假红（共用窗口时第四格「藏一个本来就没显示的窗口」会跟着转红）。
        AssertWrongThread(OnAFreshOverlay(shown: true, o => o.Hide()));
        AssertWrongThread(OnAFreshOverlay(shown: false, o => o.Show()));
        Assert.Null(OnAFreshOverlay(shown: true, o => o.Show()));
        Assert.Null(OnAFreshOverlay(shown: false, o => o.Hide()));

        // 顺带记两个不查线程的入口，免得把结论过度推广成「Avalonia 处处查线程」。
        AssertWrongThread(Record.Exception(() => RunOffTheUiThread(Dispatcher.UIThread.VerifyAccess)));
        Assert.Null(OnAFreshOverlay(shown: true, o => o.Position = new PixelPoint(300, 300)));
    }

    [Fact]
    public void AfterOneThrow_TheRuleStopsHolding_TheNextCallThrowsWithoutChangingAnything()
    {
        // 「改变了可见性才抛」的反例：窗口从未 Show 过，第一次跨线程 ApplyState 半途抛出，
        // 此后窗口内部的已显示状态与实际可见性脱钩——下一次 ApplyState(Idle) 什么可见性都没改，
        // 却照样抛（栈顶 WindowBase.set_Owner）。
        using var scenario = OverlayScenario.Create(show: false);

        var first = Record.Exception(
            () => RunOffTheUiThread(() => scenario.ViewModel.ApplyState(SessionState.Recording, null)));
        AssertWrongThread(first);

        // 前提：那一抛之后窗口**没有**显示出来，两侧可见性都是 false。
        var avaloniaBefore = AvaloniaTestHost.Run(() => scenario.Overlay.IsVisible);
        Assert.False(avaloniaBefore);
        Assert.False(NativeProbe.IsWindowVisible(scenario.OverlayHandle));

        var second = Record.Exception(
            () => RunOffTheUiThread(() => scenario.ViewModel.ApplyState(SessionState.Idle, null)));

        AssertWrongThread(second);

        // 判据：这一次调用**什么都没改**，两侧可见性前后都是 false，它还是抛了。
        Assert.False(AvaloniaTestHost.Run(() => scenario.Overlay.IsVisible));
        Assert.False(NativeProbe.IsWindowVisible(scenario.OverlayHandle));
    }

    /// <summary>拿一个**全新的**悬浮条跑一次，返回它抛了什么（没抛则 null）。</summary>
    private static Exception? OnAFreshOverlay(bool shown, Action<Views.OverlayWindow> action)
    {
        using var scenario = OverlayScenario.Create(shown);
        return Record.Exception(() => RunOffTheUiThread(() => action(scenario.Overlay)));
    }

    private static void AssertWrongThread(Exception? exception)
    {
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("invalid thread", invalid.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在线程池线程上跑，并把那边的异常原样抛回来（不裹 <c>AggregateException</c>）。
    /// 顺带自证「这条线程真的不是 UI 线程」——不然整组测试只是在 UI 线程上自娱自乐。
    /// </summary>
    private static void RunOffTheUiThread(Action action) =>
        Task.Run(() =>
        {
            Assert.False(AvaloniaTestHost.IsUiThread, "这条线程居然就是 UI 线程，本组测试无效");
            action();
        }).GetAwaiter().GetResult();
}
