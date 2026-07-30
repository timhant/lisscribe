namespace Lisscribe.Core.Abstractions;

public interface IWindowProbe
{
    nint GetForegroundWindow();

    /// <summary>比对完整性级别。目标高于自身时返回 false(UIPI 会让注入静默失败)。</summary>
    bool CanInjectInto(nint window);
}
