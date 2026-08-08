using Lisscribe.Core.Abstractions;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 只决定「能不能注入」这一个答案的假探测器。
///
/// **这是一条纯输入缝，不替代任何执行**：它替代的是
/// <see cref="WindowProbe.CanInjectInto"/> 那次完整性级别比对的**结果**，而注入器拿到
/// 这个结果之后该走哪条路、发不发 SendInput，全部照常跑真的代码。UIPI 现场（焦点落在
/// 提权窗口上）在测试进程里造不出来——真提权要么弹 UAC 把整轮测试挂住，要么依赖非默认
/// 的注册表设置；而「判为不可注入时一个字符也不许发」这条行为，只要给定那个判定就验得死。
///
/// <see cref="GetForegroundWindow"/> **不造假**，走真的 user32：焦点竞态那条分支要的是
/// 真实前台窗口，假一个就等于测试自己回答自己。
/// </summary>
internal sealed class FakeWindowProbe(bool canInject) : IWindowProbe
{
    public int CanInjectIntoCalls { get; private set; }

    public nint LastQueriedWindow { get; private set; }

    public nint GetForegroundWindow() => Native.User32.GetForegroundWindow();

    public bool CanInjectInto(nint window)
    {
        CanInjectIntoCalls++;
        LastQueriedWindow = window;
        return canInject;
    }

    /// <summary>纯日志用，走真的 user32，不造假。</summary>
    public string Describe(nint window) => Real.Describe(window);

    /// <summary>与前台窗口同理：菜单模式是真实的窗口状态，假一个就等于测试自己回答自己。</summary>
    public bool IsInMenuMode(nint window) => Real.IsInMenuMode(window);

    private static WindowProbe Real =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowProbe>.Instance);
}
