using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Config;

namespace Lisscribe.Platform;

/// <summary>
/// 按配置在两种注入方式间选路，**并在本层把「注入没发生」这件事兜住**。
///
/// <c>targetWindow == nint.Zero</c> 是状态机的约定信号：「不要注入，只复制到剪贴板」——
/// 用于焦点已切换、目标窗口提权、结果过期三种情形。此时无论配置如何都走剪贴板。
/// </summary>
/// <param name="sendInput">
/// 调用点务必用具名实参：两个参数同型，传反了编译器一声不吭，而症状是「配了 SendInput 却总在污染
/// 剪贴板」这种没人会往构造函数上想的怪事。声明成接口而非具体类，是为了让选路与兜底这段纯逻辑不必
/// 真往桌面注入一次才验得了。
/// </param>
public sealed class CompositeTextInjector(
    OutputSection output, ITextInjector sendInput, ITextInjector clipboard) : ITextInjector
{
    public async Task<InjectResult> InjectAsync(
        string text, nint targetWindow, CancellationToken cancellationToken)
    {
        if (targetWindow == nint.Zero)
        {
            return await clipboard.InjectAsync(text, nint.Zero, cancellationToken).ConfigureAwait(false);
        }

        // 认不出的模式回落到 SendInput（默认配置就是它）。ConfigValidator 只放行
        // "SendInput" / "Clipboard"，但选路器不该指望上游一定拦住——把回落方向写死在这里，
        // 好过日后加第三种模式时全凭读代码的人猜它会静默走哪一边。
        var chosen = output.Mode == "Clipboard" ? clipboard : sendInput;
        var result = await chosen.InjectAsync(text, targetWindow, cancellationToken).ConfigureAwait(false);

        if (result is not (InjectResult.FocusChanged or InjectResult.IntegrityLevelTooLow))
        {
            return result;
        }

        // 契约在这一层闭合：InjectResult.FocusChanged 的枚举注释写着「文本已转入剪贴板」，而两个实现
        // 返回它时都在写剪贴板之前就返回了，一个字也没放进去。不在这里兜住的话那句注释就是假的，且
        // 状态机对非 Ok 一律进 Failed，用户看到「上屏失败：FocusChanged」而识别结果彻底丢。
        // 兜底走 targetWindow = 0（只复制不粘贴）：注入本来就发不出去，再试一次没有意义。
        var fallback = await clipboard.InjectAsync(text, nint.Zero, cancellationToken).ConfigureAwait(false);

        // 兜底成功就把原因码原样返回——此时「文本已转入剪贴板」才是真的，上层也才说得清原因。
        // 兜底也失败则退回 Failed：文本确实哪儿都没到，不许再报一个听起来像「已保住」的码。
        return fallback == InjectResult.Ok ? result : InjectResult.Failed;
    }
}
