namespace Lisscribe.Core.Abstractions;

public enum InjectResult
{
    Ok,

    /// <summary>
    /// 目标窗口已不是按下热键时那个，**注入一个字符也没发生**。
    ///
    /// 谁负责兜底要说准：**单个注入器只负责不往错的窗口里打字，它不写剪贴板**
    /// （写了反而会在焦点竞态里平白顶掉用户的剪贴板）。把文本转进剪贴板的是
    /// <c>CompositeTextInjector</c>——它两条路都握着，收到本值时改投「只复制不粘贴」。
    /// 故调用方看到本值时，含义是「没上屏，但文本已在剪贴板里」；
    /// 若连兜底也失败，返回的是 <see cref="Failed"/> 而不是本值。
    /// </summary>
    FocusChanged,

    /// <summary>
    /// UIPI：目标进程完整性级别高于自身，SendInput 会静默失败（返回成功而一个字符也进不去）。
    /// 兜底责任与 <see cref="FocusChanged"/> 完全相同。
    /// </summary>
    IntegrityLevelTooLow,

    Failed,
}

public interface ITextInjector
{
    /// <summary>
    /// 实现方须保证三条不变量:
    /// 1. 注入前释放所有物理按下的修饰键(否则文本被解释为快捷键);
    /// 2. 代理对拆成两个 INPUT 项(emoji、扩展 CJK);
    /// 3. 注入前做 UIPI 检查(SendInput 在此失败时不返回错误码)。
    /// </summary>
    Task<InjectResult> InjectAsync(string text, nint targetWindow, CancellationToken cancellationToken);
}
