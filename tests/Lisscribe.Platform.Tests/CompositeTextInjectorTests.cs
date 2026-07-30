using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 选路器。纯逻辑，不碰输入队列也不碰剪贴板，故不进
/// <see cref="InjectedInputCollection"/>，可与别的测试并行。
///
/// 之所以能这样测：本类的两路依赖声明成 <see cref="ITextInjector"/> 而不是计划写的
/// 两个具体类。计划那个签名让选路无法在不真的注入的前提下验证——而选路恰恰是这三个类里
/// 唯一一段纯逻辑，把它绑死在真注入上是白扔掉的覆盖。
/// </summary>
public sealed class CompositeTextInjectorTests
{
    private static OutputSection Output(string mode) =>
        new(mode, RestoreClipboard: true, TrimTrailingPunctuation: true, TrimPunctuationMaxChars: 8);

    [Fact]
    public async Task InSendInputMode_WithARealTarget_GoesToSendInput()
    {
        var sendInput = new RecordingInjector(InjectResult.Ok);
        var clipboard = new RecordingInjector(InjectResult.Ok);
        var composite = new CompositeTextInjector(Output("SendInput"), sendInput, clipboard);

        await composite.InjectAsync("文本", 0x1234, CancellationToken.None);

        Assert.Equal([("文本", 0x1234)], sendInput.Calls);
        Assert.Empty(clipboard.Calls);
    }

    [Fact]
    public async Task InClipboardMode_WithARealTarget_GoesToClipboard()
    {
        var sendInput = new RecordingInjector(InjectResult.Ok);
        var clipboard = new RecordingInjector(InjectResult.Ok);
        var composite = new CompositeTextInjector(Output("Clipboard"), sendInput, clipboard);

        await composite.InjectAsync("文本", 0x1234, CancellationToken.None);

        Assert.Equal([("文本", 0x1234)], clipboard.Calls);
        Assert.Empty(sendInput.Calls);
    }

    [Fact]
    public async Task WithZeroTarget_GoesToClipboardEvenInSendInputMode()
    {
        // targetWindow == 0 是状态机的约定信号：「焦点已切换 / 目标不可注入 / 结果过期，
        // 只复制给用户」。此时无论配置如何都必须走剪贴板，否则那三种情形一律无声丢字。
        var sendInput = new RecordingInjector(InjectResult.Ok);
        var clipboard = new RecordingInjector(InjectResult.Ok);
        var composite = new CompositeTextInjector(Output("SendInput"), sendInput, clipboard);

        await composite.InjectAsync("文本", nint.Zero, CancellationToken.None);

        Assert.Equal([("文本", nint.Zero)], clipboard.Calls);
        Assert.Empty(sendInput.Calls);
    }

    [Fact]
    public async Task ForAnUnknownMode_FallsBackToSendInput()
    {
        // ConfigValidator 只放行 "SendInput" / "Clipboard"，但选路器不该指望上游一定拦住。
        // 回落到哪一侧要有个明确答案，否则日后加第三种模式时是静默走剪贴板还是静默走注入
        // 全凭读代码的人猜。这里把答案钉成 SendInput（默认配置就是它）。
        var sendInput = new RecordingInjector(InjectResult.Ok);
        var clipboard = new RecordingInjector(InjectResult.Ok);
        var composite = new CompositeTextInjector(Output("八字还没一撇"), sendInput, clipboard);

        await composite.InjectAsync("文本", 0x1234, CancellationToken.None);

        Assert.Equal([("文本", 0x1234)], sendInput.Calls);
        Assert.Empty(clipboard.Calls);
    }

    [Fact]
    public async Task ReturnsWhateverTheChosenInjectorReturned()
    {
        // 把下层的结果吞掉、一律返回 Ok，是本层最容易复发的毛病：上屏没成功而会话记成成功。
        //
        // 这里刻意只用 Failed 与 Ok 两个**不触发兜底**的码：FocusChanged /
        // IntegrityLevelTooLow 现在会被本层接管并改投剪贴板，拿它们验「原样返回」
        // 就是在验另一件事了（那件事由 WhenSendInputReportsFocusChanged_… 等四条负责）。
        var sendInput = new RecordingInjector(InjectResult.Failed);
        var clipboard = new RecordingInjector(InjectResult.Ok);

        Assert.Equal(
            InjectResult.Failed,
            await new CompositeTextInjector(Output("SendInput"), sendInput, clipboard)
                .InjectAsync("文本", 0x1234, CancellationToken.None));

        Assert.Equal(
            InjectResult.Ok,
            await new CompositeTextInjector(Output("Clipboard"), sendInput, clipboard)
                .InjectAsync("文本", 0x1234, CancellationToken.None));
    }

    [Fact]
    public async Task WhenSendInputReportsFocusChanged_TheTextGoesToTheClipboardInstead()
    {
        // **契约闭合。** InjectResult.FocusChanged 的枚举注释写着「文本已转入剪贴板」，
        // 而单个注入器返回它时都在写剪贴板之前就返回了，一个字也没放进去。
        // 不在这一层兜住，SessionController 会对非 Ok 一律 EnterFailedIfStillMine，
        // 用户看到「上屏失败：FocusChanged」而识别结果彻底丢。
        var sendInput = new RecordingInjector(InjectResult.FocusChanged);
        var clipboard = new RecordingInjector(InjectResult.Ok);
        var composite = new CompositeTextInjector(Output("SendInput"), sendInput, clipboard);

        var result = await composite.InjectAsync("文本", 0x1234, CancellationToken.None);

        // 兜底必须走 targetWindow = 0（只复制不粘贴）：注入本来就发不出去，再试一次没有意义。
        Assert.Equal([("文本", nint.Zero)], clipboard.Calls);

        // 兜底成功后原因码原样返回——此时那句「文本已转入剪贴板」才是真的。
        Assert.Equal(InjectResult.FocusChanged, result);
    }

    [Fact]
    public async Task WhenSendInputReportsIntegrityLevelTooLow_TheTextGoesToTheClipboardInstead()
    {
        var sendInput = new RecordingInjector(InjectResult.IntegrityLevelTooLow);
        var clipboard = new RecordingInjector(InjectResult.Ok);
        var composite = new CompositeTextInjector(Output("SendInput"), sendInput, clipboard);

        var result = await composite.InjectAsync("文本", 0x1234, CancellationToken.None);

        Assert.Equal([("文本", nint.Zero)], clipboard.Calls);
        Assert.Equal(InjectResult.IntegrityLevelTooLow, result);
    }

    [Fact]
    public async Task WhenTheClipboardRouteAlsoReportsFocusChanged_ItStillFallsBackToCopyOnly()
    {
        // 配成 Clipboard 模式时同样会撞上焦点竞态与 UIPI，兜底责任一模一样。
        // 注意兜底走的仍是剪贴板那一路，只是换成 targetWindow = 0。
        var sendInput = new RecordingInjector(InjectResult.Ok);
        var clipboard = new RecordingInjector(InjectResult.FocusChanged);
        var composite = new CompositeTextInjector(Output("Clipboard"), sendInput, clipboard);

        var result = await composite.InjectAsync("文本", 0x1234, CancellationToken.None);

        Assert.Equal([("文本", 0x1234), ("文本", nint.Zero)], clipboard.Calls);
        Assert.Empty(sendInput.Calls);

        // 兜底那一次返回的还是 FocusChanged（RecordingInjector 每次都返回同一个值），
        // 即兜底也没成 —— 此时不许再报一个听起来像「已保住」的码。
        Assert.Equal(InjectResult.Failed, result);
    }

    [Fact]
    public async Task WhenTheFallbackAlsoFails_TheResultBecomesFailed()
    {
        // 「文本确实哪儿都没到」必须报 Failed，不许把 FocusChanged 原样传上去——
        // 那个码的含义是「没上屏，但文本在剪贴板里」，此时后半句是假的。
        var sendInput = new RecordingInjector(InjectResult.FocusChanged);
        var clipboard = new RecordingInjector(InjectResult.Failed);
        var composite = new CompositeTextInjector(Output("SendInput"), sendInput, clipboard);

        Assert.Equal(
            InjectResult.Failed,
            await composite.InjectAsync("文本", 0x1234, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheChosenRouteJustFails_ThereIsNoClipboardFallback()
    {
        // 兜底只针对「注入没发生」那两个码。Failed 是「发了但没成」，此时文本可能已经
        // 上了一半屏，再往剪贴板抄一份既救不回来，也会平白顶掉用户的剪贴板。
        var sendInput = new RecordingInjector(InjectResult.Failed);
        var clipboard = new RecordingInjector(InjectResult.Ok);
        var composite = new CompositeTextInjector(Output("SendInput"), sendInput, clipboard);

        Assert.Equal(
            InjectResult.Failed,
            await composite.InjectAsync("文本", 0x1234, CancellationToken.None));

        Assert.Empty(clipboard.Calls);
    }

    [Fact]
    public async Task PassesTheCancellationTokenThrough()
    {
        // 传错（例如图省事传 CancellationToken.None）时，取消就传不到两个实现开头那道
        // 取消预检 —— 而那道预检正是「已取消则一个字符也不发、剪贴板也不动」的唯一入口。
        //
        // （别拿剪贴板路径里那段 150ms 的 await 当判据：它**刻意不响应取消**，传对传错都跑满。）
        var sendInput = new RecordingInjector(InjectResult.Ok);
        var clipboard = new RecordingInjector(InjectResult.Ok);
        using var source = new CancellationTokenSource();

        await new CompositeTextInjector(Output("SendInput"), sendInput, clipboard)
            .InjectAsync("文本", 0x1234, source.Token);

        Assert.Equal(source.Token, sendInput.LastToken);
    }
}

/// <summary>记下被调用的实参。选路器唯一的职责就是「谁被调、拿什么参数被调」。</summary>
internal sealed class RecordingInjector(InjectResult result) : ITextInjector
{
    public List<(string Text, nint TargetWindow)> Calls { get; } = [];

    public CancellationToken LastToken { get; private set; }

    public Task<InjectResult> InjectAsync(
        string text, nint targetWindow, CancellationToken cancellationToken)
    {
        Calls.Add((text, targetWindow));
        LastToken = cancellationToken;
        return Task.FromResult(result);
    }
}
