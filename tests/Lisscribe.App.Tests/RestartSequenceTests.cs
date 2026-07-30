using Lisscribe.App.Composition;
using Lisscribe.Core.Diagnostics;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 用户点下「立即重启」之后那串动作的**顺序**。
///
/// <para>
/// 顺序是这里唯一要紧的东西，也是组合根里立不起测试的那一段：先拉起后继进程，再请求本进程退出。
/// 反过来做的话，第 3 步之后就没有可靠的执行点了——<c>ShutdownRequested</c> 处理器同步等收尾跑完，
/// 跑完 Avalonia 就拆平台层，进程随时可能没，那次 <c>Process.Start</c> 也就随时可能不发生。
/// </para>
/// </summary>
public sealed class RestartSequenceTests
{
    /// <summary>拉起动作与退出动作往同一本账上记，顺序才验得出来。</summary>
    private sealed class RecordingRestarter(List<string> log, Exception? failure) : IAppRestarter
    {
        public void LaunchSuccessor()
        {
            log.Add("launch");

            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    [Fact]
    public void TryRestart_LaunchesTheSuccessor_BeforeAskingThisProcessToExit()
    {
        var log = new List<string>();
        var sequence = new RestartSequence(new RecordingRestarter(log, null), () => log.Add("exit"));

        Assert.True(sequence.TryRestart(out var error), error);
        Assert.Null(error);
        Assert.Equal(["launch", "exit"], log);
    }

    /// <summary>
    /// <b>后继进程拉不起来时，绝不能退出。</b> 退了的结果是用户点一下「立即重启」，
    /// lisscribe 凭空消失、什么也不再出现——比不做这个功能坏得多。
    /// </summary>
    [Fact]
    public void TryRestart_WhenTheSuccessorCannotBeLaunched_KeepsThisProcessAlive()
    {
        var log = new List<string>();
        var sequence = new RestartSequence(
            new RecordingRestarter(log, new InvalidOperationException("拉不起来")),
            () => log.Add("exit"));

        Assert.False(sequence.TryRestart(out var error));

        Assert.Contains("拉不起来", error);
        Assert.Equal(["launch"], log);
    }
}

/// <summary>
/// 拼给后继进程的那条命令行。
///
/// <para>
/// 真去 <c>Process.Start</c> 的那半由端到端测试盯着；这里只验参数——而参数正是最容易悄悄写错的
/// 一处：pid 漏了、选项名打错了，程序照样起得来，只是新进程不等旧的，于是撞上单实例检查，
/// 症状是「点了重启，程序没了」。
/// </para>
/// </summary>
public sealed class ProcessAppRestarterTests
{
    [Fact]
    public void BuildSuccessorStartInfo_PointsAtTheGivenExecutable_AndDoesNotGoThroughTheShell()
    {
        var info = ProcessAppRestarter.BuildSuccessorStartInfo(@"C:\x\Lisscribe.App.exe", 4321);

        Assert.Equal(@"C:\x\Lisscribe.App.exe", info.FileName);

        // UseShellExecute=true 会走 ShellExecuteEx，那条路不接受 ArgumentList，
        // 参数会被整个丢掉——而丢掉之后一切照常，只是不等旧进程了。
        Assert.False(info.UseShellExecute);
    }

    /// <summary>
    /// <b>拼出去的和解析回来的必须是同一个 pid。</b> 这一条把 <see cref="ProcessAppRestarter"/> 与
    /// <see cref="RestartArguments"/> 两头钉在一起：任一头改了选项名或参数顺序，本条当场红。
    /// 分开各测各的话，两边各自「正确」而合不上，是这类协议最典型的坏法。
    /// </summary>
    [Fact]
    public void TheSuccessorCommandLine_ParsesBackIntoTheSamePid()
    {
        var info = ProcessAppRestarter.BuildSuccessorStartInfo(@"C:\x\Lisscribe.App.exe", 4321);

        Assert.True(RestartArguments.TryParseWaitForPid([.. info.ArgumentList], out var pid));
        Assert.Equal(4321, pid);
    }
}
