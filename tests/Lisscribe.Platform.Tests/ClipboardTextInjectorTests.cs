using System.Diagnostics;
using Lisscribe.Core.Abstractions;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 剪贴板 + Ctrl+V 注入。取样一律走 <see cref="TestClipboard"/>，它与被测注入器零共用——拿注入器
/// 自己的读函数验它自己写的东西，两边一起错时测试照样绿。剪贴板是全机共享资源，整轮测试前后由
/// <see cref="ClipboardGuard"/> 存取一次。
/// </summary>
[Collection(InjectedInputCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class ClipboardTextInjectorTests
{
    private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 一次粘贴最多试几遍。重试的是那一次 Ctrl+V，不是断言。
    /// </summary>
    /// <remarks>
    /// 约 1% 的 Ctrl+V 会「按键到了、EDIT 也发了 WM_PASTE，可一个字符都没粘进来」：失败现场里剪贴板
    /// 内容完好、焦点与前台都对、修饰键全为 0，隔 200ms 补发一次立刻成功——是目标控件自己在 WM_PASTE
    /// 里调的 OpenClipboard 撞上了别人（剪贴板历史服务每次变化都要抓一次去快照）。这不是被测代码的
    /// 缺陷，而是「剪贴板 + Ctrl+V」固有的；1% 乘上一轮里六处依赖粘贴的断言，就是每十几轮一条假红。
    /// <para>
    /// 这层放过什么，说准：必然失败的缺陷、改返回值的缺陷、把内容弄坏的缺陷都照样转红（后者靠
    /// <see cref="MayRetry"/> 只准一种故障态重试）；而单次失败率 p 的间歇缺陷在这层转红的概率是 p⁴，
    /// p=0.5 只有 6%——「每隔一次静默不发且谎报成功」「每隔一次在外面夹一层 Alt」两个真缺陷本层都
    /// 三连跑全绿。补法不是继续拧这层的判据，而是换一个不受那 1% 影响的预言机，见
    /// <see cref="InjectAsync_ActuallyPutsExactlyOneCtrlVOnTheInputQueue"/>；那一条是单发的，对随机型
    /// 间歇缺陷转红概率是 p 而非 p⁴，比这层好得多但仍不是保证——真正随机且只影响「目标粘没粘上」的
    /// 缺陷，两层都拦不住，只有手工清单能暴露。
    /// </para>
    /// </remarks>
    private const int PasteAttempts = 4;

    /// <summary>
    /// 只有那一个已知故障态才准重试：读回为空，且剪贴板停在「这次注入本该留下的内容」上。别的读回值
    /// 一律不许重试——粘成别的内容、粘重了、代理对被拆坏都是内容缺陷，再试一遍只会把它洗掉。
    /// <paramref name="expectedClipboardAfter"/> 必须由调用方给准，不能一律拿注入的文本充数：
    /// <c>restoreClipboard = true</c> 那条路跑完剪贴板上是用户的旧内容。
    /// </summary>
    private static bool MayRetry(string readBack, string expectedClipboardAfter) =>
        readBack.Length == 0 && TestClipboard.ReadText() == expectedClipboardAfter;

    private static ClipboardTextInjector Create(
        bool restoreClipboard, IWindowProbe? probe = null, nint owner = default) => new(
        probe ?? new WindowProbe(NullLogger<WindowProbe>.Instance),
        restoreClipboard,
        () => owner,
        NullLogger<ClipboardTextInjector>.Instance);

    /// <summary>
    /// 布置前提 → 注入 → 等文本到达，整段最多重试 <see cref="PasteAttempts"/> 遍。
    ///
    /// <paramref name="arrange"/> 每一遍都重跑：像「先往剪贴板里放旧内容」这类前提，
    /// 上一遍已经把它改掉了，不重新布置的话第二遍验的就不是同一件事。
    ///
    /// 注入器**自己说不行**（返回值不是 Ok）时立即返回，不重试也不掩盖——
    /// 那不是「粘丢了」，是被测代码给出的判断，该原样交给调用方断言。
    /// </summary>
    private static async Task<InjectResult> InjectUntilThePasteLands(
        TestWindow window,
        string expected,
        Action arrange,
        Func<Task<InjectResult>> inject,
        string? expectedClipboardAfter = null)
    {
        // 默认「注入完剪贴板上就是刚写进去的那段」——restoreClipboard = false 的形态。
        var clipboardAfter = expectedClipboardAfter ?? expected;
        var result = InjectResult.Failed;

        for (var attempt = 1; attempt <= PasteAttempts; attempt++)
        {
            arrange();
            window.ClearText();

            result = await inject();
            if (result != InjectResult.Ok)
            {
                return result;
            }

            var readBack = window.WaitForText(t => t == expected, ArrivalTimeout);
            if (readBack == expected)
            {
                return result;
            }

            if (!MayRetry(readBack, clipboardAfter))
            {
                Assert.Fail(
                    $"第 {attempt} 次注入后读回 <{readBack}>，既不是期望的 <{expected}>，"
                    + $"也不是那个已知的「空 + 剪贴板为 {clipboardAfter}」故障态"
                    + $"（剪贴板实为 <{TestClipboard.ReadText()}>）——这一类不许重试。");
            }
        }

        Assert.Fail(
            $"连发 {PasteAttempts} 次 Ctrl+V 都没粘进去，最后读回 <{window.ReadText()}>，"
            + $"剪贴板 <{TestClipboard.ReadText()}>");
        return result;
    }

    /// <summary>裸 Ctrl+V 版的同一件事，供阳性对照使用。判据同样收窄。</summary>
    private static void RawPasteUntilItLands(TestWindow window, string expected)
    {
        for (var attempt = 1; attempt <= PasteAttempts; attempt++)
        {
            SynthesizedInput.SendRawCtrlV();

            var readBack = window.WaitForText(t => t == expected, ArrivalTimeout);
            if (readBack == expected)
            {
                return;
            }

            if (!MayRetry(readBack, expected))
            {
                Assert.Fail($"第 {attempt} 次裸 Ctrl+V 后读回 <{readBack}>，不是已知故障态，不许重试。");
            }
        }

        Assert.Fail($"连发 {PasteAttempts} 次裸 Ctrl+V 都没粘进去，最后读回 <{window.ReadText()}>");
    }

    [Fact]
    public async Task InjectAsync_WithATarget_ActuallyPastesIntoIt()
    {
        using var window = new TestWindow("clip-paste", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "整段粘贴的文本\U0001F600";

        var result = await InjectUntilThePasteLands(
            window, text, arrange: () => { },
            () => Create(restoreClipboard: false).InjectAsync(text, window.Handle, CancellationToken.None));

        Assert.Equal(InjectResult.Ok, result);
    }

    [Fact]
    public async Task InjectAsync_WithZeroTarget_CopiesWithoutPasting()
    {
        // targetWindow == 0 是状态机的约定信号：「只复制给用户，别注入」。
        using var window = new TestWindow("clip-copy-only", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "只复制不粘贴";

        var result = await Create(restoreClipboard: true)
            .InjectAsync(text, nint.Zero, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(text, TestClipboard.ReadText());

        // 一个字符也不许粘出去。
        Assert.Equal(string.Empty, window.ReadTextAfterSettling());

        // 阳性对照：同一个窗口、同一段等待，给上真的目标句柄，文本必须真的粘进去——
        // 否则上面那句「空串」只说明这个靶子根本收不到东西。
        var control = await InjectUntilThePasteLands(
            window, text, arrange: () => { },
            () => Create(restoreClipboard: false).InjectAsync(text, window.Handle, CancellationToken.None));

        Assert.Equal(InjectResult.Ok, control);
    }

    [Fact]
    public async Task InjectAsync_ReleasesTheModifiersTheUserIsStillHolding()
    {
        // 不释放物理按下的修饰键就直接发 Ctrl+V 的后果：用户按住 Ctrl+Alt 说话，松开后走剪贴板路径，
        // 发出去的是 Ctrl+Alt+V，而按住 Alt 时 Ctrl+V 一个字符也粘不进去。
        // 负对照见 HoldingAltBlocksARawCtrlV。
        using var window = new TestWindow("clip-modifier-release", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "按住修饰键也要粘进去";

        try
        {
            var result = await InjectUntilThePasteLands(
                window,
                text,
                // 每一遍都要重新按住：注入器会把它们松开，第二遍不重按就等于没有前提。
                arrange: () =>
                {
                    SynthesizedInput.Send(
                        0, (VirtualKey.VK_LCONTROL, false), (VirtualKey.VK_LMENU, false));

                    Assert.True(SynthesizedInput.IsHeld(VirtualKey.VK_LCONTROL), "Ctrl 没按下去，本条前提不成立");
                    Assert.True(SynthesizedInput.IsHeld(VirtualKey.VK_LMENU), "Alt 没按下去，本条前提不成立");
                },
                () => Create(restoreClipboard: false)
                    .InjectAsync(text, window.Handle, CancellationToken.None));

            Assert.Equal(InjectResult.Ok, result);
        }
        finally
        {
            SynthesizedInput.ReleaseEverythingUsedByTests();
        }
    }

    [Fact]
    public void HoldingAltBlocksARawCtrlV()
    {
        // 上一条的负对照。没有它，「粘进去了」可能只是「按住 Alt 本来也不影响粘贴」，那样把释放修饰键
        // 那段整个删掉测试照样绿。
        //
        // 用 Alt 而不是 Ctrl 或 Shift：按住 Shift 时 Ctrl+V 照常粘贴，按住 Ctrl 时发出的还是 Ctrl+V，
        // 只有 Alt 真能挡住。Win 键绝对不能用——Win+Ctrl+V 会拉起剪贴板历史并永久抢走前台。
        using var window = new TestWindow("clip-modifier-control", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string text = "负对照样本";
        TestClipboard.WriteText(text);

        try
        {
            SynthesizedInput.Send(0, (VirtualKey.VK_LMENU, false));
            SynthesizedInput.SendRawCtrlV();

            Assert.Equal(string.Empty, window.ReadTextAfterSettling());
        }
        finally
        {
            SynthesizedInput.ReleaseEverythingUsedByTests();
        }

        // 阳性对照：松开 Alt 之后，同一个窗口、同一份剪贴板内容、同一串按键，必须粘得进去。
        RawPasteUntilItLands(window, text);
    }

    [Fact]
    public async Task InjectAsync_WhenRestoreIsOn_PutsTheOldContentBack()
    {
        using var window = new TestWindow("clip-restore-on", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string previous = "用户原本复制的东西";
        const string injected = "识别结果";

        var result = await InjectUntilThePasteLands(
            window, injected,
            // 上一遍已经把剪贴板改掉了，每遍都要重新摆好「用户原本复制的东西」。
            arrange: () => TestClipboard.WriteText(previous),
            () => Create(restoreClipboard: true)
                .InjectAsync(injected, window.Handle, CancellationToken.None),
            // 这条路跑完剪贴板上是用户的旧内容，不是注入的文本。
            expectedClipboardAfter: previous);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(previous, TestClipboard.ReadText());
    }

    [Fact]
    public async Task InjectAsync_WhenRestoreIsOff_LeavesOurTextOnTheClipboard()
    {
        // 上一条的另一侧：只有这一条拦得住「无论配置如何都恢复」。
        using var window = new TestWindow("clip-restore-off", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string previous = "用户原本复制的东西";
        const string injected = "识别结果";

        var result = await InjectUntilThePasteLands(
            window, injected,
            arrange: () => TestClipboard.WriteText(previous),
            () => Create(restoreClipboard: false)
                .InjectAsync(injected, window.Handle, CancellationToken.None));

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(injected, TestClipboard.ReadText());
    }

    [Fact]
    public async Task InjectAsync_WithZeroTarget_DoesNotRestoreEvenWhenRestoreIsOn()
    {
        // 「只复制不粘贴」时用户要的就是新内容留在剪贴板里，恢复旧内容等于把结果扔了。
        const string previous = "用户原本复制的东西";
        const string injected = "识别结果";
        TestClipboard.WriteText(previous);

        var result = await Create(restoreClipboard: true)
            .InjectAsync(injected, nint.Zero, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(injected, TestClipboard.ReadText());
    }

    [Fact]
    public async Task InjectAsync_WhenAnotherProcessHoldsTheClipboard_ReportsFailureInsteadOfOk()
    {
        // 兜底路径若丢弃返回值、无条件告诉用户「结果已复制到剪贴板」，写失败时文本就没了而用户以为还在。
        //
        // 现场只能由另一个进程制造：同进程另一条线程握着剪贴板时，本线程 OpenClipboard 照样成功。
        using var window = new TestWindow("clip-busy", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string previous = "用户原本复制的东西";
        TestClipboard.WriteText(previous);

        var stopwatch = Stopwatch.StartNew();
        InjectResult result;
        using (new ClipboardHolderProcess(TimeSpan.FromSeconds(5)))
        {
            result = await Create(restoreClipboard: true)
                .InjectAsync("拿不到剪贴板的时候", window.Handle, CancellationToken.None);
            stopwatch.Stop();
        }

        Assert.Equal(InjectResult.Failed, result);

        // 首次 OpenClipboard 失败是常态（浏览器、Office 都会短暂持有），一次就放弃等于
        // 把常见的瞬时冲突变成用户可见的失败。这条断言钉住「它真的重试过」。
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 100,
            $"只花了 {stopwatch.ElapsedMilliseconds}ms 就放弃，说明没有重试");

        // 写失败不许连坐用户的旧内容：按 Open → Empty → Alloc → Set 那个顺序写，任何一步失败时剪贴板
        // 都已经被清空——我们的文本没进去，用户的也没了。
        Assert.Equal(previous, TestClipboard.ReadText());

        Assert.Equal(string.Empty, window.ReadTextAfterSettling());

        // 阳性对照：占用进程已经退出，同一个窗口、同一段等待，注入必须真的粘进去——
        // 否则上面那句「空串」只说明这个靶子从头到尾就收不到东西。
        const string control = "占用解除之后";
        var landed = await InjectUntilThePasteLands(
            window, control, arrange: () => { },
            () => Create(restoreClipboard: false).InjectAsync(control, window.Handle, CancellationToken.None));
        Assert.Equal(InjectResult.Ok, landed);
    }

    [Fact]
    public async Task InjectAsync_WhenTheTargetSitsAboveUs_LeavesTheClipboardAloneAndSaysSo()
    {
        // Ctrl+V 同样是 SendInput，一样会被 UIPI 静默丢掉。不查这件事的后果是：配成 Mode=Clipboard 时
        // 对着提权窗口注入，剪贴板被顶掉、粘贴没发生、却返回 Ok。
        using var window = new TestWindow("clip-uipi", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string previous = "不许被顶掉的内容";
        TestClipboard.WriteText(previous);

        var blocked = new FakeWindowProbe(canInject: false);
        var result = await Create(restoreClipboard: false, blocked)
            .InjectAsync("识别结果", window.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.IntegrityLevelTooLow, result);
        Assert.Equal(1, blocked.CanInjectIntoCalls);
        Assert.Equal(previous, TestClipboard.ReadText());
        Assert.Equal(string.Empty, window.ReadTextAfterSettling());

        // 阳性对照（ReadTextAfterSettling 的注释明令要有）：同一个窗口、同一段等待，
        // 换成可注入的判定，文本必须真的粘进去。
        const string control = "换成可注入之后";
        var landed = await InjectUntilThePasteLands(
            window, control, arrange: () => { },
            () => Create(restoreClipboard: false, new FakeWindowProbe(canInject: true))
                .InjectAsync(control, window.Handle, CancellationToken.None));
        Assert.Equal(InjectResult.Ok, landed);
    }

    [Fact]
    public async Task InjectAsync_WhenTheFocusHasMovedElsewhere_LeavesTheClipboardAloneAndSaysSo()
    {
        using var target = new TestWindow("clip-focus-target", withTextBox: true);
        using var thief = new TestWindow("clip-focus-thief", withTextBox: true);
        Assert.True(thief.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string previous = "不许被顶掉的内容";
        TestClipboard.WriteText(previous);

        var result = await Create(restoreClipboard: false)
            .InjectAsync("识别结果", target.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.FocusChanged, result);
        Assert.Equal(previous, TestClipboard.ReadText());
        Assert.Equal(string.Empty, thief.ReadTextAfterSettling());

        // 阳性对照：目标改成真正的前台窗口，同一个注入器必须把文本粘进去。
        const string control = "目标改对之后";
        var landed = await InjectUntilThePasteLands(
            thief, control, arrange: () => { },
            () => Create(restoreClipboard: false).InjectAsync(control, thief.Handle, CancellationToken.None));
        Assert.Equal(InjectResult.Ok, landed);
    }

    [Fact]
    public async Task InjectAsync_ActuallyPutsExactlyOneCtrlVOnTheInputQueue()
    {
        // 专门补那层重试补不了的洞：重试的判据是「文本有没有粘进去」，而那件事天生带 1% 的环境噪声，
        // 「每隔一次静默不发且谎报成功」这类缺陷会被 p⁴ 稀释掉。
        //
        // 换一个完全不同层面的预言机：装一个链上低层键盘钩子，直接数输入队列里到底有没有那次 Ctrl+V。
        // 它取样的是 SendInput 的产物，与「目标控件粘没粘上」无关，因此不受那 1% 影响。
        using var observer = new HookChainObserver();
        observer.Start();

        using var window = new TestWindow("clip-ctrlv-observed", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        observer.Clear();

        var result = await Create(restoreClipboard: false)
            .InjectAsync("被观察的一次粘贴", window.Handle, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);

        // 钩子回调跑在观察者自己的线程上，事件到达要一点时间。
        var deadline = DateTime.UtcNow + ArrivalTimeout;
        while (observer.CountOf(SynthesizedInput.VK_V) < 2 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        var seen = observer.Snapshot();
        var shape = HookChainObserver.Describe(seen);

        // 阳性对照：先证明观察者当时确实活着，否则下面每一条「没有多余的东西」都是空话。
        Assert.True(
            observer.CountOf(VirtualKey.VK_LCONTROL) >= 2,
            $"观察者只看到 {observer.CountOf(VirtualKey.VK_LCONTROL)} 次左 Ctrl，它可能压根没装上：{shape}");

        // 一次按下 + 一次抬起，不多不少。少了就是根本没发（谎报成功），
        // 多了就是发重了（用户会看到粘贴两遍）。
        Assert.Equal(2, observer.CountOf(SynthesizedInput.VK_V));

        // 只数个数不看形状是不够的：给 Ctrl+V 外面夹一层 Alt，个数一个不差而目标窗口一个字符也粘不
        // 进去，那正是「注入前释放修饰键」间歇失效的形状，故连「有没有别的键被按下」一起钉死。
        var strayDown = seen
            .Where(HookChainObserver.IsKeyDown)
            .Where(e => e.VkCode != VirtualKey.VK_LCONTROL && e.VkCode != SynthesizedInput.VK_V)
            .ToArray();

        Assert.True(
            strayDown.Length == 0,
            $"除 Ctrl 与 V 之外还有别的键被按下（{HookChainObserver.Describe(strayDown)}），"
            + $"完整序列 {shape}");

        // 再钉住 Ctrl 按下与抬起之间**恰好**是 V↓ V↑：这一条拦的是顺序被打乱、
        // 或中间夹进别的东西。
        var ctrlDown = seen.ToList().FindIndex(e => e.VkCode == VirtualKey.VK_LCONTROL && HookChainObserver.IsKeyDown(e));
        Assert.True(ctrlDown >= 0, $"没看到左 Ctrl 按下：{shape}");

        var ctrlUp = seen.ToList().FindIndex(
            ctrlDown + 1, e => e.VkCode == VirtualKey.VK_LCONTROL && !HookChainObserver.IsKeyDown(e));
        Assert.True(ctrlUp > ctrlDown, $"没看到左 Ctrl 抬起：{shape}");

        var between = seen.Skip(ctrlDown + 1).Take(ctrlUp - ctrlDown - 1).ToArray();
        Assert.True(
            between.Length == 2
            && between[0].VkCode == SynthesizedInput.VK_V && HookChainObserver.IsKeyDown(between[0])
            && between[1].VkCode == SynthesizedInput.VK_V && !HookChainObserver.IsKeyDown(between[1]),
            $"Ctrl 按下与抬起之间不是恰好一次 V 的按放：{HookChainObserver.Describe(between)}，完整序列 {shape}");
    }

    [Fact]
    public async Task InjectAsync_WithAnAlreadyCancelledToken_FailsWithoutTouchingTheClipboard()
    {
        // 取消语义此前无覆盖：把两个注入器的取消预检同时改成恒不触发，全套测试一条也不红。
        using var window = new TestWindow("clip-cancelled", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string previous = "不许被顶掉的内容";
        TestClipboard.WriteText(previous);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await Create(restoreClipboard: false)
            .InjectAsync("识别结果", window.Handle, cancelled.Token);

        Assert.Equal(InjectResult.Failed, result);

        // 已取消就不许动全局状态：剪贴板与目标窗口都得原封不动。
        Assert.Equal(previous, TestClipboard.ReadText());
        Assert.Equal(string.Empty, window.ReadTextAfterSettling());

        // 阳性对照：换一个没取消的 token，同一个窗口必须粘得进去。
        const string control = "没取消的那次";
        var landed = await InjectUntilThePasteLands(
            window, control, arrange: () => { },
            () => Create(restoreClipboard: false).InjectAsync(control, window.Handle, CancellationToken.None));
        Assert.Equal(InjectResult.Ok, landed);
    }

    [Fact]
    public async Task InjectAsync_WhenGivenAnOwnerWindow_MakesItTheClipboardOwner()
    {
        // MSDN 两处明写「owner 为 NULL 会导致 SetClipboardData 失败」，实测本机不失败，但传真 HWND 才
        // 合乎契约，且顺带买到写入期间的真独占（别的进程 OpenClipboard 拿 err 5）。这条钉住「owner
        // 真的被传下去了」，预言机是 GetClipboardOwner()——系统自己记的账，与注入器零共用。
        using var window = new TestWindow("clip-owner", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        var result = await Create(restoreClipboard: false, owner: window.Handle)
            .InjectAsync("带 owner 写入", nint.Zero, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(window.Handle, TestClipboard.GetOwner());
    }

    [Fact]
    public async Task InjectAsync_AsksForTheOwnerWindowEveryTimeRatherThanCachingIt()
    {
        // `Func<nint>` 的存在理由：把它在构造函数里缓存成字段，别的 owner 测试用的都是恒定委托，
        // 缓存与不缓存无从分辨。而「每次求值」承重——窗口可能比注入器晚建，悬浮窗更是随会话建/毁。
        // 造法：委托第一次答「还没有窗口」，第二次才答真句柄，缓存实现在第二次仍会传 0。
        using var window = new TestWindow("clip-owner-refresh", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        var calls = 0;
        var injector = new ClipboardTextInjector(
            new WindowProbe(NullLogger<WindowProbe>.Instance),
            restoreClipboard: false,
            () => ++calls == 1 ? nint.Zero : window.Handle,
            NullLogger<ClipboardTextInjector>.Instance);

        // 第一次：委托答 0，剪贴板应当无主。
        Assert.Equal(InjectResult.Ok, await injector.InjectAsync("第一次", nint.Zero, CancellationToken.None));
        Assert.Equal(nint.Zero, TestClipboard.GetOwner());

        // 第二次：委托改口给真句柄。缓存实现会在这里仍然答 0。
        Assert.Equal(InjectResult.Ok, await injector.InjectAsync("第二次", nint.Zero, CancellationToken.None));
        Assert.Equal(window.Handle, TestClipboard.GetOwner());

        Assert.True(calls >= 2, $"owner 委托只被求值了 {calls} 次，说明它被缓存了");
    }

    [Fact]
    public async Task InjectAsync_WhenTheOwnerDelegateThrows_FallsBackToNullOwnerInsteadOfBlowingUp()
    {
        // 委托是组合根交进来的外部代码。让它一路抛出注入器的后果：用户只看到「处理识别结果时出错，
        // 详见日志」而真因不过是取不到一个窗口句柄；更要紧的是那时 GlobalAlloc 已经成功，那块 HGLOBAL
        // 无人释放。
        using var window = new TestWindow("clip-owner-throws", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        var injector = new ClipboardTextInjector(
            new WindowProbe(NullLogger<WindowProbe>.Instance),
            restoreClipboard: false,
            () => throw new InvalidOperationException("owner 窗口已销毁"),
            NullLogger<ClipboardTextInjector>.Instance);

        // 不抛，且照常完成——回落到 NULL owner 是本来就支持的形态。
        var result = await injector.InjectAsync("委托抛了也要写进去", nint.Zero, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal("委托抛了也要写进去", TestClipboard.ReadText());
        Assert.Equal(nint.Zero, TestClipboard.GetOwner());
    }

    [Fact]
    public async Task InjectAsync_WhenTheOwnerHandleIsStale_DoesNotBurnTheWholeRetryBudget()
    {
        // 已销毁的 HWND 让 OpenClipboard 得 err 1400（ERROR_INVALID_WINDOW_HANDLE）——一个重试一万次
        // 也不会变的错。把它当「被占用」去重试，读一圈写一圈各吃满 10×20ms，单次注入白扔 618ms，而
        // 端到端延迟预算总共 800ms。owner 由 Func 每次求值正是因为窗口会被重建，故「拿到旧句柄」不是
        // 理论路径。
        var dead = new TestWindow("clip-owner-stale");
        var staleHandle = dead.Handle;
        dead.Dispose();
        Assert.False(dead.StillExists(), "窗口没被真的销毁，本条前提不成立");

        var stopwatch = Stopwatch.StartNew();
        var result = await Create(restoreClipboard: false, owner: staleHandle)
            .InjectAsync("句柄已失效", nint.Zero, CancellationToken.None);
        stopwatch.Stop();

        // 当场改用 NULL owner 继续，所以这一次照样成功。
        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal("句柄已失效", TestClipboard.ReadText());

        // 判据取「远小于一整圈重试」。一整圈是 10×20ms = 200ms，这里应当一次都不睡。
        Assert.True(
            stopwatch.ElapsedMilliseconds < 100,
            $"花了 {stopwatch.ElapsedMilliseconds}ms，说明 err 1400 仍在走重试圈空等");
    }

    [Fact]
    public async Task AnUnterminatedClipboardBlockComesBackTruncated_WhichIsWhyTheSizeBoundHasNoMutationTest()
    {
        // 这条是特征测试，钉的是系统行为、不是我们的代码，理由要写明。
        //
        // 「写一块不带终止符的 CF_UNICODETEXT，再断言读回恰好 N 个字符」这个做法做不到：Windows 自己
        // 不允许这种块存在——写 10 个字符 / 20 字节、一个 U+0000 也没有，读回的块 GlobalSize 仍是 20
        // 字节，而最后那个字符已被系统改写成 U+0000，内容变成 9 个字符。
        //
        // 于是两条推论：① GlobalSize 那个上限杀不掉——去掉它退回 PtrToStringUni 也会停在系统写的那个
        // U+0000 上，两种实现结果一模一样；② 上限仍然留着——它防的是「块里没有终止符」，而那个前提
        // 今天由系统保证、不在契约里，代价只是一次 GlobalSize。
        //
        // 本条的价值：哪天 Windows 不再改写最后一个码元，它会转红，那时那个上限就从防御性变成承重。
        using var window = new TestWindow("clip-unterminated", withTextBox: true);
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        const string unterminated = "没有终止符的一块内容";
        var truncatedBySystem = unterminated[..^1];

        var result = await InjectUntilThePasteLands(
            window, "识别结果",
            arrange: () => TestClipboard.WriteTextWithoutTerminator(unterminated),
            () => Create(restoreClipboard: true)
                .InjectAsync("识别结果", window.Handle, CancellationToken.None),
            expectedClipboardAfter: truncatedBySystem);

        Assert.Equal(InjectResult.Ok, result);

        // 恢复回来的是系统截断之后的那一份，不多一个字符。多出来就说明读的时候越过了分配边界，
        // 那时上限才是唯一拦得住 AccessViolation 的东西。
        Assert.Equal(truncatedBySystem, TestClipboard.ReadText());
    }

    [Fact]
    public async Task InjectAsync_WithoutAnOwnerWindow_LeavesTheClipboardUnowned()
    {
        // 另一侧，把回落形态也钉住：没有 owner 时照样写得进去（与 MSDN 相反），且 owner 为 0。
        // 没有这一条，「无论传什么都用 0」也能通过上一条。
        var result = await Create(restoreClipboard: false, owner: nint.Zero)
            .InjectAsync("不带 owner 写入", nint.Zero, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(nint.Zero, TestClipboard.GetOwner());
        Assert.Equal("不带 owner 写入", TestClipboard.ReadText());
    }

    [Fact]
    public async Task InjectAsync_ForEmptyText_IsOkAndTouchesNothing()
    {
        const string previous = "不许被顶掉的内容";
        TestClipboard.WriteText(previous);

        var probe = new FakeWindowProbe(canInject: true);
        var result = await Create(restoreClipboard: false, probe)
            .InjectAsync("", nint.Zero, CancellationToken.None);

        Assert.Equal(InjectResult.Ok, result);
        Assert.Equal(previous, TestClipboard.ReadText());
    }
}
