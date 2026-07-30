namespace Lisscribe.App.Composition;

/// <summary>拉起一份新的 lisscribe，让它接替本进程。</summary>
public interface IAppRestarter
{
    /// <summary>
    /// 起一个带 <c>--wait-for &lt;本进程 pid&gt;</c> 的新实例。
    ///
    /// <para>
    /// <b>拉不起来必须抛</b>，不许吞掉返回个 false 之外还照常往下走：
    /// 调用方要靠这个信号决定「本进程还退不退」，而退了又没有新的起来，程序就是凭空消失。
    /// </para>
    /// </summary>
    void LaunchSuccessor();
}
