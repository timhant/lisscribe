using System.Diagnostics;
using System.Runtime.InteropServices;
using Lisscribe.Core.Hotkey;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 真钩子 + 真 SendInput 的接线验证。判定逻辑（<c>ModifierKeyTracker</c> / <c>HotkeyMatcher</c>）
/// 已在 Lisscribe.Core.Tests 里单测，本类不重验，只验「那套逻辑真的被接到了 WH_KEYBOARD_LL 上」
/// 与几处只在真机上现形的缺陷不再复现。注入型测试共享整个桌面的输入队列、彼此不能并行，故用
/// Collection 把它与日后其它注入型测试类绑在一起。
/// </summary>
[Collection(InjectedInputCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class KeyboardHookTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>与 KeyboardHook.MaxConsecutiveReinstallFailures 对应。改那边要改这里。</summary>
    private const int MaxReinstallFailuresBeforeHookLost = 3;

    /// <summary>Ctrl 而非 Ctrl+Alt：真按一下 Alt 会激活目标应用的菜单栏。</summary>
    private static readonly HotkeyCombination Combination = new(ModifierKeys.Ctrl, "F13");

    public void Dispose() => SynthesizedInput.ReleaseEverythingUsedByTests();

    [Fact]
    public void Start_WithAMainKeyNobodyCanMap_Throws()
    {
        using var hook = NewHook();

        var error = Assert.Throws<ArgumentException>(
            () => hook.Start(new HotkeyCombination(ModifierKeys.Ctrl, "Nonexistent")));

        Assert.Contains("Nonexistent", error.Message);
    }

    [Fact]
    public void PressingTheCombination_RaisesPressedThenReleased()
    {
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        PressHotkey();

        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");
        Assert.Equal(["Pressed", "Released"], log.Names);
    }

    [Fact]
    public void Events_KeepTheirOrder_EvenWhenTheFirstHandlerIsSlow()
    {
        // 两个事件若各自 ThreadPool.UnsafeQueueUserWorkItem 出去，次序就没有任何保证：实测 2000 对
        // 「慢的先入队」里 1989 对颠倒。状态机按 Pressed -> Released 写的，收到颠倒的一对会把刚结束
        // 的会话重新拉回录音态。
        using var hook = NewHook();
        var log = new EventLog(hook, onPressed: () => Thread.Sleep(150));
        hook.Start(Combination);

        PressHotkey();

        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");
        Assert.Equal(["Pressed", "Released"], log.Names);
    }

    [Fact]
    public void ASubscriberThatThrows_NeitherKillsTheProcessNorStopsLaterEvents()
    {
        // 抛异常的订阅者会把异常传回引发者，而引发者有时就是钩子回调线程——那里逃逸的托管异常令进程
        // 以 STATUS_FATAL_USER_CALLBACK_EXCEPTION (0xC000041D) 当场终结。故本条转红时多半是整个测试
        // 宿主一起没了，不只是这一条失败。
        using var hook = NewHook();
        var log = new EventLog(hook, onPressed: () => throw new InvalidOperationException("订阅者炸了"));
        hook.Start(Combination);

        PressHotkey();

        Assert.True(WaitFor(() => log.Names.Contains("Released"), Patience),
            $"Pressed 的订阅者抛异常后 Released 没能送达，只收到 {log.Describe()}");
    }

    [Fact]
    public void TheMainKey_IsSwallowed_SoTheRestOfTheHookChainNeverSeesIt()
    {
        using var observer = new HookChainObserver();
        observer.Start(); // 先装 -> 在链上排在 KeyboardHook 之后

        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        PressHotkey();
        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");

        // 阳性对照：F14 从来不是热键，必须一路穿到观察者那里。没有它，
        // 「观察者没看见 F13」既可能是被吞了，也可能是观察者压根没在工作。
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F14, false), (SynthesizedInput.VK_F14, true));
        Assert.True(WaitFor(() => observer.Saw(SynthesizedInput.VK_F14), Patience),
            "阳性对照失败：链上观察者连放行的 F14 都没看到，本条断言无效");

        Assert.False(observer.Saw(SynthesizedInput.VK_F13), "主键没被吞，链上后面的钩子看到了它");
    }

    [Fact]
    public void ModifierKeys_ArePassedThroughEvenWhileTheHotkeyMatches()
    {
        // 吞掉修饰键会让用户按 Ctrl 时所有应用失灵。
        using var observer = new HookChainObserver();
        observer.Start();

        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        PressHotkey();
        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");

        Assert.True(WaitFor(() => observer.CountOf(VirtualKey.VK_LCONTROL) >= 2, Patience),
            $"Ctrl 的按下与松开没有都被放行，观察者只见到 {observer.CountOf(VirtualKey.VK_LCONTROL)} 次");
    }

    [Fact]
    public void KeysCarryingOurOwnInjectionTag_AreIgnoredAndPassedThrough()
    {
        // 上屏时我们自己会用 SendInput 注入按键，不认这个记号就会捕获自己发出的按键。
        // 注入事件的 dwExtraInfo 在低层钩子里带得住，故这个记号读得到。
        using var observer = new HookChainObserver();
        observer.Start();

        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        SynthesizedInput.Send(
            User32.LisscribeInjectedTag,
            (VirtualKey.VK_LCONTROL, false),
            (SynthesizedInput.VK_F13, false),
            (SynthesizedInput.VK_F13, true),
            (VirtualKey.VK_LCONTROL, true));

        Assert.True(WaitFor(() => observer.Saw(SynthesizedInput.VK_F13), Patience),
            "带我们自己记号的按键没有被放行");
        Assert.Empty(log.Names);
    }

    /// <summary>
    /// 放行态：钩子还装着，但热键原样交给钩子链的下游。判定逻辑已在 <c>HotkeyMatcher</c> 上单测，
    /// 本条只问那个开关有没有真接到钩子回调上——差这一段的后果是配置窗录新热键时按当前热键毫无反应，
    /// 而所有纯逻辑测试全绿。
    /// </summary>
    [Fact]
    public void PassThrough_HandsTheHotkeyItselfToTheRestOfTheChain()
    {
        using var observer = new HookChainObserver();
        observer.Start();

        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        // 阳性对照：武装态下主键必须被吞掉，否则下面「放行态看得见」不说明任何问题。
        PressHotkey();
        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");
        Assert.False(observer.Saw(SynthesizedInput.VK_F13), "武装态下主键本该被吞掉");

        hook.PassThrough = true;

        PressHotkey();

        Assert.True(WaitFor(() => observer.Saw(SynthesizedInput.VK_F13), Patience),
            "放行态下主键没有交给钩子链的下游——这正是「按了没反应」那条故障");

        // 放行的按键不该再产出热键事件，否则状态机会在暂停期间照常开录。
        Assert.Equal(2, log.Names.Count);
    }

    [Fact]
    public void AfterStop_SynthesizedKeysRaiseNoEvents()
    {
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        PressHotkey();
        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");

        hook.Stop();
        log.Clear();
        PressHotkey();
        Thread.Sleep(300);

        Assert.Empty(log.Names);
    }

    [Fact]
    public void HoldingTheMainKeyDown_RaisesPressedOnlyOnce()
    {
        // 系统的自动重复会一直投 KEYDOWN。重复触发 Pressed 会把用户已经说出口的音频清空。
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false));
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, false));
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, false));
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, false));
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true));
        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, true));

        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");
        Thread.Sleep(200);
        Assert.Equal(["Pressed", "Released"], log.Names);
    }

    [Fact]
    public void Released_CarriesRoughlyHowLongTheMainKeyWasHeld()
    {
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        PressHotkey(TimeSpan.FromMilliseconds(400));

        Assert.True(WaitFor(() => log.HeldFor is not null, Patience), $"只收到 {log.Describe()}");

        // 下界卡在 350ms：SessionController 拿它与 HoldThreshold(300ms) 比，量成 0 就是
        // 每次录音都被当成误触丢掉。上界只是拦住「读的是别的时钟」这类离谱值。
        Assert.InRange(log.HeldFor!.Value, TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void TheHealthCheck_ReinstallsTheHookWithoutKillingIt()
    {
        // 存活自检最危险的一处：钩子回调跑在装钩子那条线程上，所以在别的线程（如 Timer 的线程池
        // 线程）上重装，SetWindowsHookEx 照样返回有效句柄、lastError = 0，而回调再也不触发——
        // 等于每 30 秒把一个健康的钩子换成一个永不触发的钩子，还汇报「一切正常」。
        //
        // 触发走真路径：把「回调耗时超阈值」设成 0，第一次按键就构成「系统可能已把钩子摘了」的证据。
        //
        // 预言机是句柄，不是 CompletedHealthChecks：后者是被测代码自己记的账，把自检的身子整个掏空
        // 只留那一行自增，测试照样全绿。句柄由系统分配、重装必换号，这份证词不由我们决定。
        using var hook = NewHook(TimeSpan.FromMilliseconds(100), TimeSpan.Zero);
        var log = new EventLog(hook);
        hook.Start(Combination);

        var handleBefore = hook.CurrentHookHandle;
        Assert.NotEqual(nint.Zero, handleBefore);

        PressHotkey();

        Assert.True(WaitFor(() => hook.CurrentHookHandle != handleBefore, Patience),
            $"自检没有真的重装：句柄仍是 0x{handleBefore:X}，已跑 {hook.CompletedHealthChecks} 次自检");

        // 阈值为 0 时自检每 100ms 拆装一次，一次按键的 KEYUP 有可能正落在 Unhook 与
        // SetWindowsHookEx 之间那一瞬。允许重试几次——要验的是「钩子还活着」，
        // 不是「每一次按键都不丢」；钩子若真死了，五次全都收不到。
        var delivered = false;
        for (var attempt = 0; attempt < 5 && !delivered; attempt++)
        {
            log.Clear();
            PressHotkey();
            delivered = WaitFor(() => log.Names.Count >= 2, TimeSpan.FromSeconds(1));
        }

        Assert.True(delivered, $"存活自检重装之后钩子哑了，最后一次只收到 {log.Describe()}");
        Assert.Equal(0, log.HookLostCount);
    }

    [Fact]
    public void AHealthyHook_IsNotTornDownWithoutEvidenceOfTrouble()
    {
        // 无条件定期 Unhook + SetWindowsHookEx 的话，那两次系统调用之间热键完全不设防——默认热键
        // Ctrl+Alt+Space 若正好撞上，会往用户文档里漏一个空格。只有「我们自己的回调超时了」才构成
        // 动手的理由。
        //
        // 超时阈值显式设成一小时，把「没有证据」这个前提坐实：用生产默认值（200ms）时本条 6/6 转红，
        // 因为整套测试并发跑时一次阻塞式 GC 就能把某次回调拖过 200ms，那种情况下重装是对的反应，
        // 错的是这条测试没控住它不打算测的变量。
        using var hook = NewHook(TimeSpan.FromMilliseconds(100), TimeSpan.FromHours(1));
        var log = new EventLog(hook);
        hook.Start(Combination);

        var handleBefore = hook.CurrentHookHandle;
        PressHotkey();
        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");

        // 六个轮询间隔。无条件重装的版本在这段时间里会换好几次号。
        Thread.Sleep(600);

        Assert.Equal(handleBefore, hook.CurrentHookHandle);
        Assert.Equal(0, hook.CompletedHealthChecks);
    }

    [Fact]
    public void Stop_ActuallyRemovesTheHookFromTheSystem()
    {
        // AfterStop_SynthesizedKeysRaiseNoEvents 名字读着像在验这件事，其实不验：光把订阅清空就够它
        // 通过，钩子摘没摘它不关心。这里的预言机换成操作系统——对一个已摘除的句柄再 Unhook 会失败并
        // 给 ERROR_INVALID_HOOK_HANDLE(1404)，钩子还在则这次调用会成功。
        //
        // 说清它拦不住什么：删掉 UninstallHook 里那次 UnhookWindowsHookEx，本条仍绿——不是测试弱，
        // 而是那件事在外部不可观测：装钩子的线程一退出，系统就替我们摘了钩子，此时对该句柄 Unhook
        // 同样返回 false、err 1404。显式那次 Unhook 是防御性的（万一日后泵线程不再随 Stop 退出）。
        // 本条真正拦得住的是「Stop 没把泵停掉」：拿掉 StopAndJoin，本条即红。
        using var hook = NewHook();
        hook.Start(Combination);
        var handle = hook.CurrentHookHandle;
        Assert.NotEqual(nint.Zero, handle);

        hook.Stop();

        var unhookedAgain = User32.UnhookWindowsHookEx(handle);
        var error = Marshal.GetLastWin32Error();

        Assert.False(unhookedAgain, "Stop 之后这个句柄仍能被摘除，说明 Stop 根本没摘它");
        Assert.Equal(1404, error);
    }

    [Fact]
    public void WhenThePumpCannotBeStopped_TheDelegateRootIsDeliberatelyLeaked()
    {
        // 泵没停干净 => UninstallHook 没跑完 => 钩子仍挂在系统里指着那个委托。此刻撤 GC root，
        // 下一次按键就是 catch 不到的 AccessViolation。
        var baseline = KeyboardHook.RootedCallbackCount;

        // 阳性对照：泵停得干净时 root 必须被撤掉。没有它，「count 没变」也可能只是因为
        // 这个类从来就不撤 root，本条断言便什么也没验。
        using (var clean = NewHook())
        {
            clean.Start(Combination);
            Assert.Equal(baseline + 1, KeyboardHook.RootedCallbackCount);
            clean.Stop();
            Assert.Equal(baseline, KeyboardHook.RootedCallbackCount);
        }

        using var release = new ManualResetEventSlim();
        using var stuck = NewHook();
        stuck.Start(Combination);
        Assert.Equal(baseline + 1, KeyboardHook.RootedCallbackCount);

        // 把泵线程堵住，让 StopAndJoin 的 Join 必然超时。
        //
        // 用可释放的信号而不是固定时长的 Sleep：Sleep 版本里 Stop 返回后泵线程还要再睡约两秒，
        // 带着一个仍然安装着的钩子活到后面几条测试里去，是跨测试串味的现成来源。
        Assert.True(stuck.PumpForTest!.Post(
            () => release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None)));
        Thread.Sleep(200);

        stuck.Stop();

        Assert.Equal(baseline + 1, KeyboardHook.RootedCallbackCount);

        // 放行并等它真的收完尾：UninstallHook 跑完才会把句柄清零。
        // 这既是收尾，本身也是一条断言——泵最终必须退得掉。
        release.Set();
        Assert.True(WaitFor(() => stuck.CurrentHookHandle == nint.Zero, Patience),
            "泵线程最终没能收尾，钩子被漏给了后面的测试");
    }

    [Fact]
    public void AfterALostKeyUp_TheNextHotkeyPressStillWorks()
    {
        // KEYUP 丢失（锁屏 / UAC secure desktop / 钩子被摘除）之后，判定器与钩子各自的「主键还按着」
        // 双双卡在 true。不复位，用户下一次按热键就被当成重复 KEYDOWN 吞掉：无 Pressed、无日志、
        // 界面毫无反应，要按到第三次才恢复。
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (SynthesizedInput.VK_F13, false));
        Assert.True(WaitFor(() => log.Names.Contains("Pressed"), Patience), $"只收到 {log.Describe()}");

        // 主键的 KEYUP 永不到达，只有修饰键松开了。
        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, true));

        // 会话中断（锁屏 / 休眠 / 会话切换）时由会话监听调它，这是复位唯一的出口。
        // 不是 ResyncModifiers——那个只重同步修饰键，理由见下面两条测试。
        hook.ForgetHeldMainKey();

        log.Clear();
        PressHotkey();

        Assert.True(WaitFor(() => log.Names.Contains("Pressed"), Patience),
            $"丢一次 KEYUP 之后下一次热键被静默吞掉，只收到 {log.Describe()}");
    }

    [Fact]
    public void ResyncModifiers_WhileTheHotkeyIsHeld_DoesNotCutTheSessionShort()
    {
        // 「复位主键按住态」绝不能塞进 ResyncModifiers：那样按住热键期间调它，只收到 Pressed、
        // Released 根本不来，IsMainKeyPhysicallyDown 立刻翻假，看门狗下一跳就强行收尾。而
        // ResyncModifiers 挂的是前台窗口变化——悬浮条、托盘气泡、通知、IME 候选窗任何一个在用户
        // 说话期间抢一下焦点都会截断本次录音，用户端毫无感知。
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (SynthesizedInput.VK_F13, false));
        Assert.True(WaitFor(() => log.Names.Contains("Pressed"), Patience), $"只收到 {log.Describe()}");

        hook.ResyncModifiers();

        Assert.True(hook.IsMainKeyPhysicallyDown, "重同步修饰键把主键按住态也抹掉了");

        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true), (VirtualKey.VK_LCONTROL, true));

        Assert.True(WaitFor(() => log.Names.Contains("Released"), Patience),
            $"重同步修饰键之后 Released 不来了，只收到 {log.Describe()}");
        Assert.Equal(["Pressed", "Released"], log.Names);
    }

    [Fact]
    public void ForgetHeldMainKey_DoesResetTheHeldState()
    {
        // 上一条的对照：复位这件事本身必须有个出口，只是不该挂在 ResyncModifiers 上。没有这条，
        // 上一条测试可以靠「谁也不复位」通过，而那就把「丢一次 KEYUP 便永久吞键」放回来了。
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (SynthesizedInput.VK_F13, false));
        Assert.True(WaitFor(() => log.Names.Contains("Pressed"), Patience), $"只收到 {log.Describe()}");
        Assert.True(hook.IsMainKeyPhysicallyDown);

        hook.ForgetHeldMainKey();

        Assert.False(hook.IsMainKeyPhysicallyDown, "ForgetHeldMainKey 没有复位主键按住态");

        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true), (VirtualKey.VK_LCONTROL, true));
    }

    [Fact]
    public void ThreeConsecutiveReinstallFailures_ReportHookLost()
    {
        // 「连续 N 次失败才上报」这条容易写成不可达：失败分支若把 _hookHandle 写成 0，而方法第一句正是
        // 「句柄为 0 就返回」，计数便永停在 1——程序此后彻底沉默，热键永久死掉，用户只拿到一条 Warning。
        using var hook = NewHook(TimeSpan.FromMilliseconds(80), TimeSpan.Zero);
        var log = new EventLog(hook);
        hook.Start(Combination);

        hook.PendingForcedInstallFailures = MaxReinstallFailuresBeforeHookLost;

        // 回调超时阈值是 0，故随便按一次就构成「该重装了」的证据。
        PressHotkey();

        Assert.True(WaitFor(() => log.HookLostCount >= 1, Patience),
            $"连续 {MaxReinstallFailuresBeforeHookLost} 次重装失败没有上报 HookLost，"
            + $"自检只跑了 {hook.CompletedHealthChecks} 次");
    }

    [Fact]
    public void RestartingTheHook_DoesNotCarryOverTheReinstallFailureCount()
    {
        // 重装失败计数跨 Stop→Start 泄漏的后果：重启后第一次偶发失败就凑够上限报 HookLost、热键宣告
        // 死亡——恰是那个常量要防的事。配置窗改热键必然走 Stop→Start。
        //
        // 自检间隔取 400ms，好让「已失败一次、下一次自检尚未到来」这段窗口宽到能稳稳停住。
        using var hook = NewHook(TimeSpan.FromMilliseconds(400), TimeSpan.Zero);
        var log = new EventLog(hook);

        hook.Start(Combination);
        hook.PendingForcedInstallFailures = 1;
        PressHotkey();

        // 只用来定序，不作预言机——预言机是下面的 HookLostCount。
        Assert.True(WaitFor(() => hook.ConsecutiveReinstallFailures == 1, Patience),
            "没能制造出一次重装失败");
        hook.Stop();

        log.Clear();
        hook.Start(Combination);
        hook.PendingForcedInstallFailures = MaxReinstallFailuresBeforeHookLost - 1;
        PressHotkey();

        Assert.True(
            WaitFor(() => hook.ConsecutiveReinstallFailures >= MaxReinstallFailuresBeforeHookLost - 1, Patience),
            "第二段没能制造出两次重装失败");
        Thread.Sleep(600);

        // 计数若跨重启泄漏，1 + 2 就凑够 3，HookLost 会在这里冒出来。
        Assert.Equal(0, log.HookLostCount);
    }

    [Fact]
    public void RestartingTheHook_DoesNotCarryOverAPendingReinstall()
    {
        // 待重装标志同样会跨 Stop→Start 泄漏，后果是重启后第一次自检无条件拆装一次健康钩子——
        // 即「两次系统调用之间热键完全不设防」那段窗口。
        using var hook = NewHook(TimeSpan.FromMilliseconds(80), TimeSpan.Zero);
        hook.Start(Combination);

        // 制造证据只需要「回调跑过一次」。用 F14 这种从来不是热键的单键，而不是完整的热键
        // 按放：超时阈值为 0 时自检每 80ms 就拆装一次钩子，热键的 KEYUP 有可能正落在
        // Unhook 与 SetWindowsHookEx 之间那一瞬而丢掉 Released——那既是本测试的易碎点，
        // 也正是「无条件重装」本身的危害。
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F14, false));
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F14, true));
        Assert.True(WaitFor(() => hook.CompletedHealthChecks >= 1, Patience), "自检没有因超时证据而动手");
        hook.Stop();

        hook.Start(Combination);
        var handleAfterRestart = hook.CurrentHookHandle;
        Assert.False(hook.ReinstallPending, "重启之后仍带着上一轮的待重装标志");

        // 重启之后一个键也不按，就没有任何新证据。等足若干个自检周期。
        Thread.Sleep(500);

        Assert.Equal(handleAfterRestart, hook.CurrentHookHandle);
    }

    [Fact]
    public void ResyncModifiers_PicksUpAModifierThatWasAlreadyDownBeforeTheHookStarted()
    {
        // ResyncModifiers 的本职：上面两条只断言它「不该做什么」，把方法体改成无条件 return 也不红。
        // 现场是钩子起来之前 Ctrl 就按住了，那次 KEYDOWN 钩子根本没看见，修饰键表里是空的。
        using var hook = NewHook();
        var log = new EventLog(hook);

        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false));
        try
        {
            hook.Start(Combination);

            // 阴性对照：还没重同步，钩子以为没按修饰键，这次 F13 不构成热键。
            SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, false));
            SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true));
            Thread.Sleep(250);
            Assert.Empty(log.Names);

            hook.ResyncModifiers();

            SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, false));
            SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true));

            Assert.True(WaitFor(() => log.Names.Count >= 2, Patience),
                $"重同步之后钩子仍不知道 Ctrl 按着，只收到 {log.Describe()}");
            Assert.Equal(["Pressed", "Released"], log.Names);
        }
        finally
        {
            SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, true));
        }
    }

    [Fact]
    public void WhenInstallingTheHookTimesOut_TheDelegateRootIsKept()
    {
        // 「泵没停干净就不许撤 GC root」在装钩子失败的回滚路径上也必须成立，而那一支只有 onStart
        // 超时才走得到——生产超时 5 秒，故把超时做成可注入的，否则这条路径无从覆盖。
        var baseline = KeyboardHook.RootedCallbackCount;

        using var gate = new ManualResetEventSlim();
        using var hook = new KeyboardHook(
            NullLogger<KeyboardHook>.Instance,
            healthCheckInterval: TimeSpan.FromHours(1),
            callbackOverrunThreshold: TimeSpan.FromHours(1),
            pumpStartTimeout: TimeSpan.FromMilliseconds(200))
        {
            InstallGateForTest = gate,
        };

        Assert.Throws<TimeoutException>(() => hook.Start(Combination));

        // 泵线程还卡在 onStart 里，收不到 WM_QUIT，Join 必然超时 => root 不许撤。
        Assert.Equal(baseline + 1, KeyboardHook.RootedCallbackCount);

        gate.Set();
    }

    [Fact]
    public void ADisposedHook_RefusesToStartAgain()
    {
        using var hook = NewHook();
        hook.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hook.Start(Combination));
    }

    [Fact]
    public void TheHookKeepsWorkingAcrossAggressiveGarbageCollection()
    {
        // 手工清单里那条长时留驻用例的加速版。说清它拦不住什么：把静态 root 改成实例字段，本条仍绿——
        // 泵线程的回调捕获了 this，泵活着就 root 住了钩子实例连带其实例字段。也就是说静态 root 是双
        // 保险中的一道，另一道只在泵线程活着时成立，恰恰不覆盖它要防的那个窗口。
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        GC.Collect();

        PressHotkey();

        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience),
            $"三轮 GC 之后钩子不再响应，只收到 {log.Describe()}");
    }

    [Fact]
    public void IsMainKeyPhysicallyDown_IsTrueWhileTheSwallowedMainKeyIsHeld()
    {
        // 看门狗每 200ms 查一次这个属性，判 false 即认定「KEYUP 丢失」并强行收尾；
        // 那时 held ≈ 200ms < HoldThreshold(300ms)，整段录音按短按丢弃。
        // 若这个属性在按住期间返回 false，产品就是一个字也出不来。
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (SynthesizedInput.VK_F13, false));
        Assert.True(WaitFor(() => log.Names.Contains("Pressed"), Patience), $"只收到 {log.Describe()}");

        Assert.True(hook.IsMainKeyPhysicallyDown, "主键正按着，它却说没按");

        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true), (VirtualKey.VK_LCONTROL, true));
        Assert.True(WaitFor(() => log.Names.Contains("Released"), Patience), $"只收到 {log.Describe()}");

        Assert.False(hook.IsMainKeyPhysicallyDown, "主键已松开，它却说还按着");
    }

    [Fact]
    public void TheOperatingSystemItselfLosesTrackOfASwallowedKey()
    {
        // 上一条为什么不能用 GetAsyncKeyState 实现，理由钉在这里：被钩子吞掉的 KEYDOWN 不更新 win32k
        // 的异步键状态表。这条把那个前提做成活的——哪天 Windows 改了这个行为它会转红，而不是让下一个
        // 人凭直觉把实现改回 GetAsyncKeyState。
        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (SynthesizedInput.VK_F13, false));
        Assert.True(WaitFor(() => log.Names.Contains("Pressed"), Patience), $"只收到 {log.Describe()}");

        var swallowed = User32.GetAsyncKeyState(SynthesizedInput.VK_F13);

        // 对照：Ctrl 是被放行的，同一时刻它在同一张表里必须是按住的。没有这个对照，
        // 「读到 0」也可能只是因为这台机器上 GetAsyncKeyState 整个不工作。
        var passedThrough = User32.GetAsyncKeyState(VirtualKey.VK_LCONTROL);

        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true), (VirtualKey.VK_LCONTROL, true));

        Assert.True((passedThrough & 0x8000) != 0, "对照失败：被放行的 Ctrl 在异步键状态表里也没落位");
        Assert.True((swallowed & 0x8000) == 0,
            "被吞掉的主键居然在异步键状态表里落位了——若属实，IsMainKeyPhysicallyDown 可以改回 GetAsyncKeyState");
    }

    [Fact]
    public void TwoHooksInTheSameProcess_BothWork()
    {
        // 把实例本身也做成静态单例的话，一个进程只能有一个钩子，且 Start 中途失败会留下脏静态。
        // root 住委托与「实例单例」是两件事。
        using var first = NewHook();
        using var second = NewHook();
        var firstLog = new EventLog(first);
        var secondLog = new EventLog(second);

        first.Start(new HotkeyCombination(ModifierKeys.Ctrl, "F13"));
        second.Start(new HotkeyCombination(ModifierKeys.Ctrl, "F14"));

        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (SynthesizedInput.VK_F13, false));
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true), (VirtualKey.VK_LCONTROL, true));
        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (SynthesizedInput.VK_F14, false));
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F14, true), (VirtualKey.VK_LCONTROL, true));

        Assert.True(WaitFor(() => firstLog.Names.Count >= 2, Patience), $"第一个钩子只收到 {firstLog.Describe()}");
        Assert.True(WaitFor(() => secondLog.Names.Count >= 2, Patience), $"第二个钩子只收到 {secondLog.Describe()}");
        Assert.Equal(["Pressed", "Released"], firstLog.Names);
        Assert.Equal(["Pressed", "Released"], secondLog.Names);
    }

    [Fact]
    public void WhenLoggingItsOwnStartupThrows_StartStillSucceeds()
    {
        // 钉住构造函数里那道 `.Safe()`：拿掉它，全套测试一条也不红。
        //
        // 钉的是 Start 成功路径上那句 LogInformation——它不在任何手写 try/catch 里，抛出即从 Start
        // 逃出去，而那时钩子已经装上、泵已经跑起来，调用方却收到一个异常，于是既不会用它也不会
        // Dispose 它：一个装着全局键盘钩子的孤儿。sink 只在那一句上抛，别的日志照常。
        //
        // 自检那句「派发钩子存活自检失败」不能拿来钉：它在 catch 里，而要进那个 catch 得 Post 先抛，
        // 可 Post 现在返回 false 不抛——实测把泵线程弄死、让自检连踩 60 多次，收到的全是投递失败的
        // Warning，一次 Error 都没有。那个 catch 仍留着（Post 仍可能因 OOM 抛），但不是可钉的行为。
        var sink = new RecordingLogger<KeyboardHook> { ThrowOnMessageContaining = "键盘钩子已启动" };
        using var hook = new KeyboardHook(sink, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        hook.Start(Combination);

        // 阳性对照：确认真的走到了那句日志上，否则本条是空测。
        Assert.True(sink.CountOf(LogLevel.Information) >= 1, $"没走到启动日志上：{sink.Describe()}");

        // 而且是真的起来了，不是「因为什么都没做所以没抛」。
        Assert.NotEqual(nint.Zero, hook.CurrentHookHandle);
    }

    /// <summary>
    /// 吞掉主键的那一刻必须往链上补一对中间键。
    ///
    /// <para>
    /// 判据是链上观察者真看见了 <c>0xE8</c>，而不是「菜单没弹出来」——后者由
    /// <see cref="AltOnlyHotkeyMenuTests"/> 判，那条依赖真菜单窗口与前台，贵且脆。
    /// 这一条只问「补了没有」，与为什么要补解耦。
    /// </para>
    /// </summary>
    [Fact]
    public void SwallowingTheMainKey_PutsANeutralKeyOnTheWire_SoTheModifierIsNotLeftLookingLikeATap()
    {
        using var observer = new HookChainObserver();
        observer.Start();

        using var hook = NewHook();
        var log = new EventLog(hook);
        hook.Start(Combination);

        // 阴性对照要在按热键**之前**取：链上此刻还不该有中间键，
        // 否则「按完看见了」既可能是我们补的，也可能是别处早就有的。
        Assert.False(observer.Saw(VirtualKey.VK_NEUTRAL), "还没按热键，链上就已经有中间键了，本条断言无效");

        PressHotkey();

        Assert.True(WaitFor(() => log.Names.Count >= 2, Patience), $"只收到 {log.Describe()}");
        Assert.True(
            WaitFor(() => observer.Saw(VirtualKey.VK_NEUTRAL), Patience),
            "主键被吞掉了，却没往链上补中间键——修饰键在系统眼里就是一次孤立按放");

        // 阳性对照：观察者当时确实活着。少了它，上面那条若因观察者压根没工作而红，
        // 失败信息会指向补中间键这件事，而真因与它无关。
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F14, false), (SynthesizedInput.VK_F14, true));
        Assert.True(WaitFor(() => observer.Saw(SynthesizedInput.VK_F14), Patience),
            "链上观察者连放行的 F14 都没看到，本条测试无效");
    }

    private static KeyboardHook NewHook(
        TimeSpan? healthCheckInterval = null, TimeSpan? callbackOverrunThreshold = null) =>
        new(NullLogger<KeyboardHook>.Instance, healthCheckInterval, callbackOverrunThreshold);

    private static void PressHotkey(TimeSpan? holdFor = null)
    {
        SynthesizedInput.Send(0, (VirtualKey.VK_LCONTROL, false), (SynthesizedInput.VK_F13, false));
        Thread.Sleep(holdFor ?? TimeSpan.FromMilliseconds(80));
        SynthesizedInput.Send(0, (SynthesizedInput.VK_F13, true), (VirtualKey.VK_LCONTROL, true));
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private sealed class EventLog
    {
        private readonly List<string> _names = [];
        private readonly Lock _gate = new();

        public EventLog(KeyboardHook hook, Action? onPressed = null)
        {
            hook.Pressed += (_, _) =>
            {
                // 慢活儿必须发生在记录之前。反过来写（先记名字再慢），记下的次序永远等于投递次序，
                // 并发投递造成的颠倒根本落不进这份记录——那条次序测试就成了空测。实测过：把派发换成
                // 两次线程池入队之后，先记后慢的版本照样全绿。
                onPressed?.Invoke();

                lock (_gate)
                {
                    _names.Add("Pressed");
                }
            };

            hook.Released += (_, held) =>
            {
                lock (_gate)
                {
                    _names.Add("Released");
                    HeldFor = held;
                }
            };

            hook.HookLost += (_, _) => Interlocked.Increment(ref _hookLost);
        }

        private int _hookLost;

        public TimeSpan? HeldFor { get; private set; }

        public int HookLostCount => Volatile.Read(ref _hookLost);

        public IReadOnlyList<string> Names
        {
            get
            {
                lock (_gate)
                {
                    return [.. _names];
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _names.Clear();
                HeldFor = null;
            }

            Interlocked.Exchange(ref _hookLost, 0);
        }

        public string Describe() => $"[{string.Join(", ", Names)}]";
    }
}

/// <summary>
/// <see cref="ClipboardGuard"/> 也挂在这里：剪贴板测试会写全机共享的剪贴板，而集合级 fixture 保证
/// 整轮跑完必定还原一次，不依赖每条用例自己记得收尾。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InjectedInputCollection
    : ICollectionFixture<ForegroundSanityCheck>, ICollectionFixture<ClipboardGuard>
{
    public const string Name = "注入型输入（共享整个桌面的输入队列，不可并行）";
}
