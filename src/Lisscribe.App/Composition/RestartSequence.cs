namespace Lisscribe.App.Composition;

/// <summary>
/// 用户点下「立即重启」之后那串动作。
///
/// <para>
/// <b>顺序不可调换：先拉起后继进程，再请求本进程退出。</b> 请求退出之后就没有可靠的执行点了——
/// <c>ShutdownRequested</c> 处理器同步等 <c>AppServices.DisposeAsync</c> 跑完，跑完 Avalonia
/// 就开始拆平台层，进程随时可能没，那次 <c>Process.Start</c> 也就随时可能不发生。
/// 而先拉起是安全的：后继进程进门第一件事就是等本进程死透。
/// </para>
///
/// <para>
/// 单独成类只为一件事——这条顺序在组合根里立不起测试。见 <c>RestartSequenceTests</c>。
/// </para>
/// </summary>
public sealed class RestartSequence(IAppRestarter restarter, Action requestExit)
{
    /// <returns>
    /// 后继进程已拉起、退出也已请求时为 true；拉不起来时为 false，
    /// 此时**本进程原样活着**，<paramref name="error"/> 是可以直接给用户看的一句话。
    /// </returns>
    public bool TryRestart(out string? error)
    {
        try
        {
            restarter.LaunchSuccessor();
        }
        catch (Exception ex)
        {
            // 拉不起来还退出的话，用户点一下「立即重启」，lisscribe 凭空消失、什么也不再出现。
            error = ex.Message;

            return false;
        }

        requestExit();

        error = null;

        return true;
    }
}
