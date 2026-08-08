namespace Lisscribe.Core.Abstractions;

public interface IWindowProbe
{
    nint GetForegroundWindow();

    /// <summary>比对完整性级别。目标高于自身时返回 false(UIPI 会让注入静默失败)。</summary>
    bool CanInjectInto(nint window);

    /// <summary>
    /// 把一个窗口说清楚，只为日志。格式「进程名/窗口类名」。
    /// </summary>
    /// <remarks>
    /// 日志里只有一个 HWND 时排障走不下去：事后没人认得出 <c>0x67972</c> 是记事本还是别的东西，
    /// 而「上屏返回 Ok、目标窗口里一个字也没有」这类故障的第一个岔路口恰恰是「究竟打给了谁」。
    /// <b>刻意不含窗口标题</b>：那一栏常带着用户的文件名与文档内容，而本程序连识别结果都不落盘。
    /// 实现方须保证不抛：排障用的东西自己把程序搞崩，比没有它更糟。
    /// </remarks>
    string Describe(nint window);

    /// <summary>
    /// 这个窗口所在的线程此刻是不是正处在菜单模式（菜单栏被键盘激活、系统菜单或弹出菜单开着）。
    /// </summary>
    /// <remarks>
    /// 菜单模式下所有按键都归菜单，注入的字符一个也进不去，而 <c>SendInput</c> 照样返回成功
    /// ——「上屏返回 Ok、目标窗口里一个字也没有」这个症状的既知成因之一。
    /// 判不出来时答 <c>false</c>：那是安全的那一侧，最坏是维持现状，而误报会让每次注入都多发
    /// 一对退出菜单的按键。
    /// </remarks>
    bool IsInMenuMode(nint window);
}
