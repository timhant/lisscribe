using Lisscribe.Core.Abstractions;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// SendInput 逐字符注入的端到端验证：拿 <see cref="TestWindow"/> 当靶，把注入的文本原样读回来
/// 逐字比对，靶子、焦点、取样口全在测试自己手里。
///
/// 只有两件事留给手工清单：**真实第三方应用**（微信、VS Code）与**真提权窗口**——
/// 前者是别人的代码，后者会弹 UAC 把整轮测试挂住。
/// </summary>
[Collection(InjectedInputCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class SendInputTextInjectorTests
{
    private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromSeconds(5);

    /// <summary>走真的令牌解析。只有 UIPI 那条用例换成假的，别处一律用它。</summary>
    private static SendInputTextInjector WithRealProbe() => new(
        new WindowProbe(NullLogger<WindowProbe>.Instance),
        NullLogger<SendInputTextInjector>.Instance);

    [Fact]
    public async Task InjectAsync_PutsTheExactTextIntoTheFocusedControl()
    {
        using var window = new TestWindow("inject-text", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "打开设置";
        var injector = WithRealProbe();

        var result = await injector.InjectAsync(text, window.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(text, window.WaitForText(t => t == text, ArrivalTimeout));
    }

    [Fact]
    public async Task InjectAsync_KeepsSurrogatePairsIntact()
    {
        // InputBuilderTests 已经钉住「代理对拆成两个 INPUT 项」这件事的**构造**；
        // 这一条是它的端到端对照：拆错了目标应用收到的是两个方块或半个字符，
        // 而那只有真的走一遍系统输入队列才看得见。
        using var window = new TestWindow("inject-surrogates", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "a\U0001F600b\U0002000Bc";
        Assert.Equal(7, text.Length); // 3 个 BMP 字符 + 两对代理对 = 7 个 UTF-16 码元

        var injector = WithRealProbe();

        var result = await injector.InjectAsync(text, window.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(text, window.WaitForText(t => t == text, ArrivalTimeout));
    }

    [Fact]
    public async Task InjectAsync_ReleasesTheModifiersTheUserIsStillHolding()
    {
        // 「注入前必须释放用户还按着的修饰键」的端到端验证。
        //
        // **预言机是 win32k 的异步键状态表，不是 EDIT 的内容**，且与直觉相反：按住 Ctrl+Alt
        // 直接发 KEYEVENTF_UNICODE，纯 EDIT 控件照样收全每一个字符——「文本进去了」在这条上
        // 什么也证明不了，会折叠代码的只有自己处理加速键的应用。
        // 真正能机械判定的是：注入结束后那两个修饰键还按不按着。
        //
        // 这条断言不是空的，由 ModifierKeysStayHeldWhenNobodyReleasesThem 做负对照：
        // 同样按住 Ctrl+Alt、同样发一串 Unicode 文本，不释放就依然是按住的。
        using var window = new TestWindow("inject-modifier-release", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "打开设置";
        var injector = WithRealProbe();

        try
        {
            SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (VirtualKey.VK_LMENU, false));

            Assert.True(SynthesizedInput.IsHeld(VirtualKey.VK_LCONTROL), "Ctrl 没按下去，本条前提不成立");
            Assert.True(SynthesizedInput.IsHeld(VirtualKey.VK_LMENU), "Alt 没按下去，本条前提不成立");

            var result = await injector.InjectAsync(text, window.Handle, CancellationToken.None);

            Assert.Equal(InjectResult.Ok, result);
            Assert.Equal(text, window.WaitForText(t => t == text, ArrivalTimeout));

            Assert.False(SynthesizedInput.IsHeld(VirtualKey.VK_LCONTROL), "注入后 Ctrl 仍按着");
            Assert.False(SynthesizedInput.IsHeld(VirtualKey.VK_LMENU), "注入后 Alt 仍按着");
        }
        finally
        {
            SynthesizedInput.ReleaseEverythingUsedByTests();
        }
    }

    [Fact]
    public void ModifierKeysStayHeldWhenNobodyReleasesThem()
    {
        // 上一条的负对照。没有它，「注入后修饰键为松开」可能只是「发任何输入都会清掉修饰键」，
        // 那样把 BuildModifierRelease 整段删掉测试照样绿。
        //
        // 这里发的 Unicode 序列由 SynthesizedInput 自己照 MSDN 构造，不经 InputBuilder——
        // 负对照与被测方共用构造代码，被测方错时两边一起错。
        using var window = new TestWindow("modifier-control", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "打开设置";

        try
        {
            SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (VirtualKey.VK_LMENU, false));
            SynthesizedInput.SendUnicode(text);

            // 阳性对照：这一路确实把文本送到了（否则「修饰键仍按着」也可能只是因为什么都没发生）。
            Assert.Equal(text, window.WaitForText(t => t == text, ArrivalTimeout));

            Assert.True(SynthesizedInput.IsHeld(VirtualKey.VK_LCONTROL), "Ctrl 竟自己松开了");
            Assert.True(SynthesizedInput.IsHeld(VirtualKey.VK_LMENU), "Alt 竟自己松开了");
        }
        finally
        {
            SynthesizedInput.ReleaseEverythingUsedByTests();
        }
    }

    [Fact]
    public async Task InjectAsync_WhenTheTargetSitsAboveUs_SendsNotOneCharacter()
    {
        // UIPI 的现场（前台落在提权窗口上）在测试进程里造不出来，但 IWindowProbe 是接口——
        // 把「不可注入」这个判定给定，剩下的行为全验得死。
        using var window = new TestWindow("inject-uipi", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        var blocked = new FakeWindowProbe(canInject: false);
        var result = await new SendInputTextInjector(blocked, NullLogger<SendInputTextInjector>.Instance)
            .InjectAsync("打开设置", window.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.IntegrityLevelTooLow, result);
        Assert.Equal(1, blocked.CanInjectIntoCalls);
        Assert.Equal(window.Handle, blocked.LastQueriedWindow);
        Assert.Equal(string.Empty, window.ReadTextAfterSettling());

        // 阳性对照：同一个窗口、同一段等待，换成可注入的判定，文本必须真的进去——
        // 否则上面那句「空串」只说明这个靶子根本收不到东西。
        var allowed = new FakeWindowProbe(canInject: true);
        var control = await new SendInputTextInjector(allowed, NullLogger<SendInputTextInjector>.Instance)
            .InjectAsync("打开设置", window.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, control);
        Assert.Equal("打开设置", window.WaitForText(t => t == "打开设置", ArrivalTimeout));
    }

    [Fact]
    public async Task InjectAsync_WhenTheFocusHasMovedElsewhere_SendsNotOneCharacter()
    {
        // InjectResult.FocusChanged 必须真有人返回，不能只是个摆着的枚举值。
        // 状态机在入队前查过一次前台，但那次检查与真正 SendInput 之间隔着一次
        // CanInjectInto（要 OpenProcess + 查令牌）和一次 await，焦点完全来得及变。
        // 注入器在发送前**贴着** SendInput 再查一次，把这个窗口从几十毫秒缩到几微秒。
        using var target = new TestWindow("inject-focus-target", withTextBox: true);
        using var thief = new TestWindow("inject-focus-thief", withTextBox: true);

        Assert.True(thief.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        var injector = WithRealProbe();

        // 目标是 target，可前台是 thief——这正是竞态落地时的样子。
        var result = await injector.InjectAsync("打开设置", target.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.FocusChanged, result);
        Assert.Equal(string.Empty, thief.ReadTextAfterSettling());
        Assert.Equal(string.Empty, target.ReadText());

        // 阳性对照：目标改成真正的前台窗口，同一个注入器必须把文本送进去。
        var control = await injector.InjectAsync("打开设置", thief.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, control);
        Assert.Equal("打开设置", thief.WaitForText(t => t == "打开设置", ArrivalTimeout));
    }

    [Fact]
    public async Task InjectAsync_ReleasesTheRightHandSideModifiersToo()
    {
        // 右侧必须单独有覆盖：只测左侧的话，把 ReadPhysicalModifiers 改成
        // GetBothSides(m)[..1] 全套测试照样绿。
        //
        // 右侧不是对称的凑数项：`Ctrl+Alt` 与 AltGr 布局等价，而 AltGr 就是**右** Alt——
        // 用户真按到的很可能只有右侧那一个。
        using var window = new TestWindow("inject-right-modifiers", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "右侧修饰键";
        var injector = WithRealProbe();

        try
        {
            SynthesizedInput.Send(0, (VirtualKey.VK_RCONTROL, false), (VirtualKey.VK_RMENU, false));

            Assert.True(SynthesizedInput.IsHeld(VirtualKey.VK_RCONTROL), "右 Ctrl 没按下去，本条前提不成立");
            Assert.True(SynthesizedInput.IsHeld(VirtualKey.VK_RMENU), "右 Alt 没按下去，本条前提不成立");

            var result = await injector.InjectAsync(text, window.Handle, CancellationToken.None);

            Assert.Equal(InjectResult.Ok, result);
            Assert.Equal(text, window.WaitForText(t => t == text, ArrivalTimeout));

            Assert.False(SynthesizedInput.IsHeld(VirtualKey.VK_RCONTROL), "注入后右 Ctrl 仍按着");
            Assert.False(SynthesizedInput.IsHeld(VirtualKey.VK_RMENU), "注入后右 Alt 仍按着");
        }
        finally
        {
            SynthesizedInput.ReleaseEverythingUsedByTests();
        }
    }

    [Fact]
    public async Task InjectAsync_WithAnAlreadyCancelledToken_FailsWithoutSendingAnything()
    {
        // 取消预检要有专门的钉子：少了它，把两个注入器的取消预检同时改成恒不触发，
        // 全套测试照样绿。
        using var window = new TestWindow("inject-cancelled", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        var probe = new FakeWindowProbe(canInject: true);
        var injector = new SendInputTextInjector(probe, NullLogger<SendInputTextInjector>.Instance);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await injector.InjectAsync("打开设置", window.Handle, cancelled.Token);

        Assert.Equal(InjectResult.Failed, result);

        // 取消要在动手之前认掉：连 UIPI 都不该去查，更不许发出任何按键。
        Assert.Equal(0, probe.CanInjectIntoCalls);
        Assert.Equal(string.Empty, window.ReadTextAfterSettling());

        // 阳性对照：换一个没取消的 token，同一个窗口必须收到文本。
        var control = await injector.InjectAsync("打开设置", window.Handle, CancellationToken.None);
        Assert.Equal(InjectResult.Ok, control);
        Assert.Equal("打开设置", window.WaitForText(t => t == "打开设置", ArrivalTimeout));
    }

    [Fact]
    public async Task InjectAsync_ForEmptyText_IsOkAndSendsNothing()
    {
        using var window = new TestWindow("inject-empty", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        var probe = new FakeWindowProbe(canInject: true);
        var injector = new SendInputTextInjector(probe, NullLogger<SendInputTextInjector>.Instance);

        Assert.Equal(InjectResult.Ok, await injector.InjectAsync("", window.Handle, CancellationToken.None));

        // 空文本连 UIPI 都不该去查——没有任何要送的东西，查了也只是白花一次 OpenProcess。
        Assert.Equal(0, probe.CanInjectIntoCalls);
        Assert.Equal(string.Empty, window.ReadTextAfterSettling());
    }
}
