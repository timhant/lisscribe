using System.ComponentModel;
using System.Diagnostics;

namespace Lisscribe.Core.Diagnostics;

/// <summary>等待旧实例退出的结果。</summary>
public enum PreviousInstanceWaitOutcome
{
    /// <summary>
    /// 旧进程已经不在了——等到它退出、或进去时它就已经没了。两者合成一项是有意的：调用方对这两种
    /// 情形的处置一模一样（继续启动）。
    /// </summary>
    Gone,

    /// <summary>给的时限内它还没退出。此时**不能硬闯**，见 <see cref="PreviousInstanceWaiter"/>。</summary>
    TimedOut,
}

/// <summary>换档重启时，新进程在抢单实例互斥体之前先等旧进程死透。</summary>
/// <remarks>
/// 必须在 <see cref="StartupPreflight.Run"/> 之前调用：反过来的话新进程一进门就撞上旧进程还攥着的
/// 互斥体，被判「已有实例在跑」当场退出，用户看到的是「点了重启，程序直接没了」。超时也不强杀——
/// 强杀会跳过摘钩子、停采集、Dispose 引擎那一串，还可能杀掉一次正在落盘的识别，而真等满时限只有
/// 一种可能：旧进程卡住了，那正是不该硬闯的时候。
/// </remarks>
public static class PreviousInstanceWaiter
{
    /// <summary>
    /// 等 pid 为 <paramref name="pid"/> 的进程退出。**不抛异常**：这段代码跑在 <c>Main</c> 里、
    /// 在任何日志装配起来之前，漏出去的异常等于双击程序一闪即没。
    /// </summary>
    public static PreviousInstanceWaitOutcome Wait(int pid, TimeSpan timeout)
    {
        try
        {
            using var previous = Process.GetProcessById(pid);

            return previous.WaitForExit((int)timeout.TotalMilliseconds)
                ? PreviousInstanceWaitOutcome.Gone
                : PreviousInstanceWaitOutcome.TimedOut;
        }
        catch (ArgumentException)
        {
            // 没有这个 pid 的进程。旧的已经先走一步了，正是我们要等的结果。
            return PreviousInstanceWaitOutcome.Gone;
        }
        catch (InvalidOperationException)
        {
            // 进程在 GetProcessById 与 WaitForExit 之间退出并被回收，同上。
            return PreviousInstanceWaitOutcome.Gone;
        }
        catch (Win32Exception)
        {
            // 打不开那个进程（受保护进程、别的用户的进程、或提权跑着的那一份；pid 0 也走这里——
            // System Idle Process 找得到，炸在 WaitForExit 内部的 OpenProcess）。打不开就无从等它，
            // 只能往下走：真要是旧实例还活着，接着的单实例检查会说一句真话「已在运行」。
            return PreviousInstanceWaitOutcome.Gone;
        }
    }
}
