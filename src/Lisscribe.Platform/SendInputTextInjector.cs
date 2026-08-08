using Lisscribe.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Platform;

/// <summary>
/// SendInput 逐字符注入（KEYEVENTF_UNICODE）。INPUT 数组的构造全在 <see cref="InputBuilder"/>，
/// 本类只管调用时序，落地三条不变量：
/// <list type="number">
/// <item>注入前释放所有物理按下的修饰键（<see cref="InputSender.TryReleasePhysicalModifiers"/>）；</item>
/// <item>代理对拆成两个 INPUT 项（<see cref="InputBuilder.BuildText"/>）；</item>
/// <item>注入前做 UIPI 检查——SendInput 在提权窗口上返回成功但一个字符也进不去。</item>
/// </list>
/// </summary>
public sealed class SendInputTextInjector : ITextInjector
{
    private readonly IWindowProbe _windowProbe;

    /// <summary>构造时已过 <see cref="SafeLoggerExtensions.Safe"/>：本类由 SessionController
    /// 的队列 worker 调用，那里一次逃逸的异常就把 worker 永久 fault 掉。</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// 不收键盘钩子：让钩子放行自己注入的按键靠 <c>dwExtraInfo</c> 上的
    /// <see cref="Native.User32.LisscribeInjectedTag"/> 标记，与钩子对象无关。
    /// </summary>
    public SendInputTextInjector(IWindowProbe windowProbe, ILogger<SendInputTextInjector> logger)
    {
        _windowProbe = windowProbe;
        _logger = logger.Safe();
    }

    public Task<InjectResult> InjectAsync(
        string text, nint targetWindow, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(InjectResult.Ok);
        }

        // 取消只在这里认一次，之后一路走到底：一旦开始发 INPUT 就在改全局状态（修饰键已被释放、
        // 部分字符已经上屏），半途撂下比走完更糟。语义与 ClipboardTextInjector 一致。
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(InjectResult.Failed);
        }

        // 不变量 3。SessionController 入队前也查过一次，这里仍查：契约要求的是「实现方须保证」，
        // 而本类可能被别的调用方直接使用。
        if (!_windowProbe.CanInjectInto(targetWindow))
        {
            _logger.LogWarning("目标窗口 0x{Window:X} 完整性级别高于自身，放弃注入", targetWindow);

            return Task.FromResult(InjectResult.IntegrityLevelTooLow);
        }

        // 这一查是 InjectResult.FocusChanged 唯一的产地：竞态消不掉（查完到 SendInput 之间仍可
        // 切走），它买的是把「打错窗口」从「靠上游记得查」变成本层自己的保证，代价约 0.2us。
        // 判错的方向也在安全那一侧：判成变了而其实没变，上层退剪贴板，用户至少拿得到文字。
        var foreground = _windowProbe.GetForegroundWindow();

        if (foreground != targetWindow)
        {
            _logger.LogInformation(
                "临发送前焦点已从 0x{Target:X} 切至 0x{Current:X}，放弃注入", targetWindow, foreground);

            return Task.FromResult(InjectResult.FocusChanged);
        }

        // 不变量 1。放在焦点检查**之后**：若这次注入本来就不该发生，就没有理由把用户
        // 正按着的修饰键替他松开。
        if (!InputSender.TryReleasePhysicalModifiers(_logger))
        {
            return Task.FromResult(InjectResult.Failed);
        }

        // 修饰键释放完再查菜单模式：上面那一串补发的 Alt↑ 本身就可能把目标推进菜单模式，
        // 查在前面等于查了个过期的状态。
        InputSender.TryLeaveMenuMode(_windowProbe, targetWindow, _logger);

        // 不变量 2 在 BuildText 内部（逐 UTF-16 码元发送，代理对天然占两个索引）。
        var ok = InputSender.TrySend(InputBuilder.BuildText(text), _logger, "注入文本");

        return Task.FromResult(ok ? InjectResult.Ok : InjectResult.Failed);
    }
}
