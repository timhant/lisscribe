namespace Lisscribe.Core.Abstractions;

public interface IOverlayWindowStyler
{
    /// <summary>
    /// 设 WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW。
    /// Avalonia 11.3.18 与 12.1.0 均无 OS 级点击穿透 API(已扫描程序集确认),只能 P/Invoke。
    ///
    /// <para>
    /// 点击穿透要 LAYERED 与 TRANSPARENT 两位齐备,少一位都不成:只 TRANSPARENT 时下方窗口一次鼠标
    /// 移动也收不到(补 SWP_FRAMECHANGED 也没用),只 LAYERED 同样不成。
    /// 缺 NOACTIVATE 则悬浮条抢焦点;缺 TOOLWINDOW 则它出现在 Alt+Tab 列表里。
    /// </para>
    /// <para>
    /// **调用时机也有硬要求**:Avalonia 的 <c>Window.Show()</c> 会把 GWL_EXSTYLE 整个写回它自己
    /// 那一份,四个位全被抹掉,故必须在 <c>Opened</c> 里设、且每次 <c>Show()</c> 都重设一遍。
    /// </para>
    /// </summary>
    void MakeClickThrough(nint window);
}
