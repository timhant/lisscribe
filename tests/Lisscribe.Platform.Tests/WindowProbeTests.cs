using System.Reflection;
using System.Security.Principal;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 前台窗口获取与 UIPI 完整性级别比对。
///
/// 与注入型测试同一个 Collection：本类会把测试窗口抢到前台，而
/// <see cref="KeyboardHookTests"/> 正往整个桌面发合成按键，两者并行跑会互相串味。
/// </summary>
[Collection(InjectedInputCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class WindowProbeTests
{
    private static WindowProbe Probe => new(NullLogger<WindowProbe>.Instance);

    [Fact]
    public void CanInjectInto_ForZeroHandle_IsFalse()
    {
        Assert.False(Probe.CanInjectInto(nint.Zero));
    }

    [Fact]
    public void Describe_NamesTheProcessAndTheWindowClass()
    {
        // 日志里只有一个 HWND 时排障根本走不下去：0x67972 是记事本还是别的东西，事后无从追认
        // ——2026-08-08 那次真机故障就卡在这儿。进程名 + 窗口类名两样合起来才认得出目标。
        //
        // **刻意不记窗口标题**：那一栏常带着用户的文件名与文档内容，而本程序连识别结果都不落盘。
        using var window = new TestWindow("probe-describe");

        var description = Probe.Describe(window.Handle);

        Assert.Contains(TestWindow.RegisteredClassName, description);
        Assert.Contains(
            System.Diagnostics.Process.GetCurrentProcess().ProcessName, description);
    }

    [Fact]
    public void Describe_ForAnInvalidHandle_SaysSoAndDoesNotThrow()
    {
        // 与 CanInjectInto 同一条纪律：排障用的东西自己把程序搞崩，比没有它更糟。
        var description = Probe.Describe(0x7FFFFFFF);

        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Fact]
    public void Describe_ForZeroHandle_SaysNone()
    {
        Assert.Equal("无", Probe.Describe(nint.Zero));
    }

    [Fact]
    public void CanInjectInto_ForAWindowOfOurOwnProcess_IsTrue()
    {
        // **别用 Process.GetCurrentProcess().MainWindowHandle 取句柄**：测试宿主是控制台进程，
        // 那个属性恒为 0，这条测试会以「跳过」的形式一次也没真跑过。
        // 自己建一个真窗口才有内容：同一进程的完整性级别必然相同，必可注入。
        using var window = new TestWindow("probe-own-process");

        Assert.True(Probe.CanInjectInto(window.Handle));
    }

    [Fact]
    public void CanInjectInto_ForAnInvalidHandle_IsFalseAndDoesNotThrow()
    {
        // 注入路径上抛异常会让整条链路挂掉：凡 P/Invoke 包装都当它会抛。
        Assert.False(Probe.CanInjectInto(0x7FFFFFFF));
    }

    [Fact]
    public void CanInjectInto_ForAWindowThatHasBeenDestroyed_IsFalse()
    {
        // 真实时序，不是编造的句柄：用户在我们识别的这一秒里关掉了目标窗口。
        var window = new TestWindow("probe-destroyed");
        var handle = window.Handle;

        // 阳性对照：销毁之前它必须是可注入的，否则下面那条断言换成任何实现都成立。
        Assert.True(Probe.CanInjectInto(handle));

        window.Dispose();
        Assert.False(window.StillExists(), "窗口没被真的销毁，本条断言无效");

        Assert.False(Probe.CanInjectInto(handle));
    }

    [Fact]
    public void GetForegroundWindow_ReturnsTheWindowThatIsActuallyInFront()
    {
        // 「不抛异常」验不出任何东西：返回值恒为 0 的实现照样通过。预言机换成
        // 「我们刚刚亲手抢到前台的那个 HWND」——那个值由测试自己制造，与被测代码无关。
        using var window = new TestWindow("probe-foreground");
        Assert.True(window.BringToForeground(), "系统拒绝了前台抢占，本条断言无效");

        Assert.Equal(window.Handle, Probe.GetForegroundWindow());
    }

    [Fact]
    public void OurOwnIntegrityLevel_MatchesWhetherWeAreElevated()
    {
        // **没有这条，完整性级别的解析代码是完全无覆盖的**：CanInjectInto 对同进程窗口做的是
        // `x <= x`，无论 GetIntegrityLevel 返回什么垃圾值都成立。真正难写对的是 SID 里
        // 「取最后一个子颁发机构」那几步（GetSidSubAuthorityCount 拿到的是指针，要先读一个
        // 字节；再按 count-1 取 RID），错了也不会有任何症状。
        //
        // 预言机是 WindowsPrincipal.IsInRole——另一套完全不同的机制（token 组判定），
        // 不经过我们这段 SID 解析。提权进程为 High(0x3000)，未提权为 Medium(0x2000)。
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        var level = WindowProbe.GetIntegrityLevel(Kernel32.GetCurrentProcess());

        Assert.NotNull(level);
        Assert.Equal(
            elevated ? Advapi32.SECURITY_MANDATORY_HIGH_RID : Advapi32.SECURITY_MANDATORY_MEDIUM_RID,
            level!.Value);
    }

    [Fact]
    public void CanInjectInto_WhenTheTargetSitsAboveUs_IsFalse()
    {
        // **这一条与下一条钉的是本类存在的全部理由：比较方向。** 别的 CanInjectInto 测试
        // 要么走早退分支、要么是同进程的 `x <= x`，把 `targetLevel <= ownLevel` 改成
        // `>=`、`==` 乃至 `return true`，它们一条也不会红。
        //
        // 现场是这样造的：目标那侧全是真的（本进程的真窗口、真的 SID 解析结果 = Medium），
        // 只把「我们自己是什么级别」给定成 Low。UIPI 判据要求「低的不许向高的发」，
        // 故 Low 的我们看 Medium 的目标必须答 false。
        //
        // 不真起一个提权进程的理由：那要么弹 UAC 框把整轮测试挂住（默认策略的机器都会），
        // 要么依赖 ConsentPromptBehaviorAdmin = 0 这个非默认设置。真实 UIPI 行为归手工清单。
        using var window = new TestWindow("probe-below-target");

        var lowIntegrityUs = new WindowProbe(
            NullLogger<WindowProbe>.Instance, Advapi32.SECURITY_MANDATORY_LOW_RID);

        Assert.False(lowIntegrityUs.CanInjectInto(window.Handle));
    }

    [Fact]
    public void CanInjectInto_WhenTheTargetSitsBelowUs_IsTrue()
    {
        // 上一条的另一侧。只有这一条能杀掉把判据写成 `==` 的变异——同级与「目标更低」
        // 都得放行，UIPI 只挡「目标更高」。
        using var window = new TestWindow("probe-above-target");

        var systemIntegrityUs = new WindowProbe(
            NullLogger<WindowProbe>.Instance, Advapi32.SECURITY_MANDATORY_SYSTEM_RID);

        Assert.True(systemIntegrityUs.CanInjectInto(window.Handle));
    }

    [Fact]
    public void TheLoggerIsWrappedSoThatLoggingCannotFaultTheCallingWorker()
    {
        // 钉住构造函数里那道 `.Safe()`：拿掉它，别处一条测试也不会红。
        //
        // 这里用反射断言而不是「造一个会抛的 sink 再看行为」，是因为本类**唯一**的日志点
        // 在 CanInjectInto 的 catch 里，而要进那个 catch 得先让 CanInjectIntoCore 抛——
        // 那条路本身就没有可达的触发点（几个 P/Invoke 都不抛）。断言「它被包过」比
        // 造一个到不了的现场诚实，也比什么都不断言强。
        //
        // 代价说清楚：这条钉的是**结构**不是行为，改坏包装方式（比如换个别的包法）它会误报。
        // 但它拦得住真正要拦的那件事——有人把 `.Safe()` 删掉。
        var probe = new WindowProbe(NullLogger<WindowProbe>.Instance);

        var field = typeof(WindowProbe)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        Assert.Equal("SafeLogger", field!.GetValue(probe)?.GetType().Name);
    }

    [Fact]
    public void GetIntegrityLevel_ForAProcessHandleThatIsNotOne_IsNullNotAThrow()
    {
        // OpenProcessToken 会失败，此后每一步都拿着垃圾指针走——漏一个判空就是
        // 不可 catch 的 AccessViolation，而这条路径在生产里由「目标进程刚退出」触发。
        Assert.Null(WindowProbe.GetIntegrityLevel(0x7FFFFFFF));
    }
}
