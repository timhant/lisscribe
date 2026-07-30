using System.Diagnostics;
using System.Globalization;
using Lisscribe.App;
using Lisscribe.Core.Diagnostics;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 换档重启的交接：**拿真的 <c>Lisscribe.App.exe</c> 验，不是拿假件**。
///
/// <para>
/// 这是整个功能的核心风险。旧进程退出前一直攥着单实例互斥体，新进程若在等它之前就去做
/// 单实例检查，会被判「已有实例在跑」当场退出——用户看到的是「点了立即重启，程序直接没了」。
/// 那条顺序只存在于 <c>Program.RunStartup</c> 里，任何单元测试都够不着。
/// </para>
///
/// <para>
/// <b>手法：让被测进程一步也走不进真正的启动。</b> 测试自己先占住真的单实例名字，于是
/// 被测 exe 无论如何都过不了单实例检查——它既装不上全局键盘钩子，也弹不出托盘图标，
/// 更不会去加载模型。两条用例靠**退出码与耗时**区分它到底先做了哪一件：
/// </para>
/// <list type="bullet">
///   <item>先等（正确）：上一个进程赖着不走 → 等满 30 秒 → 退出码 5</item>
///   <item>先检查（错误）：互斥体被占 → 立刻退出码 1</item>
/// </list>
///
/// <para>
/// 「上一个进程」直接用**测试宿主自己**：它必然活到测试跑完，省掉一整套子进程机关。
/// </para>
///
/// <para>
/// 归 <see cref="DesktopCollection"/> 与 <c>RequiresDesktop</c> 不是因为要画窗口，
/// 而是因为它**霸占真的单实例名字**：跑的时候本机不能开着 lisscribe，与桌面那些测试的
/// 实际约束是同一条。占不到就跳过，并把原因说清楚。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class RestartHandoffTests
{
    /// <summary>
    /// 真 exe 就在测试输出目录里（App.Tests 引用了 App，apphost 一并复制过来）。
    /// </summary>
    private static string AppExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, "Lisscribe.App.exe");

    /// <summary>
    /// 大到不可能存在的 pid。Windows 的 pid 实际远小于此，而「刚退出的那个进程的 pid」
    /// 随时可能被系统分配给别人，拿它当「不存在」会偶发地测到另一个真进程头上。
    /// </summary>
    private const int NoSuchPid = 0x7FFFFFFC;

    /// <summary>
    /// <b>先等，后查单实例。</b> 上一个进程（测试宿主自己）绝不会在 30 秒内退出，
    /// 故正确的实现必然等满时限、以 5 退出；顺序写反的话互斥体当场就是占着的，
    /// 会立刻以 1 退出——那正是本条要拦的。
    /// </summary>
    [Fact]
    public void TheRealExecutable_WaitsForThePreviousInstance_BeforeCheckingSingleInstance()
    {
        using var occupied = OccupyTheSingleInstanceNameOrSkip();

        var clock = Stopwatch.StartNew();
        var exitCode = RunAppWaitingFor(Environment.ProcessId, TimeSpan.FromSeconds(90));
        clock.Stop();

        Assert.Equal(5, exitCode);

        // 时限是 30 秒。低于 25 秒就说明它根本没等满——最可能的成因正是顺序写反了，
        // 而那种情况下退出码会是 1，上面已先拦一道；这一条兜的是「等了但没等够」。
        Assert.True(
            clock.Elapsed >= TimeSpan.FromSeconds(25),
            $"只等了 {clock.Elapsed.TotalSeconds:0.0}s，不像等满了 30 秒的时限");
    }

    /// <summary>
    /// 等待结束之后必须**接着往下走**，而不是就此收工。
    ///
    /// <para>
    /// 上一个进程压根不存在，等待应当立刻放行，于是被测进程走到单实例检查那关，
    /// 撞上测试占着的名字，以 1（已有实例在跑）退出。**退出码 1 在这里是好消息**——
    /// 它证明那段等待没有把启动流程截断。
    /// </para>
    /// </summary>
    [Fact]
    public void TheRealExecutable_WhenThereIsNothingToWaitFor_FallsThroughToTheNormalStartup()
    {
        using var occupied = OccupyTheSingleInstanceNameOrSkip();

        var clock = Stopwatch.StartNew();
        var exitCode = RunAppWaitingFor(NoSuchPid, TimeSpan.FromSeconds(60));
        clock.Stop();

        Assert.Equal(1, exitCode);

        // 阴性对照：它不该在这里等任何东西。等满 30 秒说明「进程不存在」被当成了「还活着」。
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(20),
            $"没有要等的进程，却等了 {clock.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>
    /// 占住真的单实例名字。**用生产代码里那个常量，不另抄一份**：抄一份的话日后改名两边悄悄
    /// 对不上，而对不上的后果不是测试变红，是这两条测试转而把真程序整个启起来
    /// （装全局键盘钩子、出托盘图标、加载模型）——测试跑一次就抢走用户的热键。
    /// </summary>
    private static IDisposable OccupyTheSingleInstanceNameOrSkip()
    {
        var acquired = SingleInstanceGuard.TryAcquire(Program.InstanceName, out var guard);

        Assert.SkipUnless(
            acquired,
            $"本机此刻有 lisscribe 在运行（单实例名 \"{Program.InstanceName}\" 已被占用），" +
            "本组测试要独占这个名字才能把被测进程挡在真正的启动之外。请先退出 lisscribe。");

        return guard!;
    }

    /// <summary>拿真 exe 跑一次 <c>--wait-for &lt;pid&gt;</c>，返回退出码。</summary>
    private static int RunAppWaitingFor(int previousPid, TimeSpan patience)
    {
        var info = ProcessAppRestarterStartInfo(previousPid);

        using var app = Process.Start(info)
            ?? throw new InvalidOperationException($"拉不起被测进程：{AppExecutablePath}");

        // 等不到就杀掉，别把一个装着全局钩子的进程留在系统里。
        if (!app.WaitForExit((int)patience.TotalMilliseconds))
        {
            app.Kill(entireProcessTree: true);

            throw new TimeoutException(
                $"被测进程没有在 {patience.TotalSeconds:0} 秒内退出，已强杀。");
        }

        return app.ExitCode;
    }

    private static ProcessStartInfo ProcessAppRestarterStartInfo(int previousPid)
    {
        var info = new ProcessStartInfo
        {
            FileName = AppExecutablePath,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        info.ArgumentList.Add(RestartArguments.WaitForOption);
        info.ArgumentList.Add(previousPid.ToString(CultureInfo.InvariantCulture));

        // 没有这一句，被测进程会弹一个 MessageBox 并一直等人点确定——测试挂到超时为止，
        // 而且屏幕上凭空多出一个对话框。
        info.Environment[Program.HeadlessVariable] = "1";

        return info;
    }
}
