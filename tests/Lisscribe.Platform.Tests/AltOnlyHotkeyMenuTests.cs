using Lisscribe.Core.Hotkey;
using Lisscribe.Platform;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// Alt 单独作修饰键时，一次热键按放会不会激活目标应用的菜单栏。
/// 靶子是自建的带真 HMENU 的窗口，判据是它收到的菜单消息。
///
/// <para>
/// <b>为什么非验不可。</b> <see cref="HotkeyMatcher"/> 只吞主键，修饰键一律放行
/// （吞掉修饰键会让用户按 Ctrl 时所有应用一起失灵）。于是热键配成 <c>Alt + X</c> 时，
/// win32k 看到的序列是 <c>Alt↓</c> …（主键被吞，它一个字节也看不见）… <c>Alt↑</c>
/// ——与「真按一下 Alt」不可区分，而真按一下 Alt 是会激活菜单栏的。
/// <see cref="KeyboardHookTests"/> 的热键因此刻意选了 <c>Ctrl+F13</c> 而不是带 Alt 的组合。
/// </para>
///
/// <para>
/// 与「孤立的 <c>Win↑</c> 弹出开始菜单」同一形状：<b>某个修饰键单独按放就有副作用，
/// 而中间那个键被吞掉之后它就是单独按放。</b> <c>Ctrl+Alt+Space</c> 那种组合里
/// Ctrl 的 KEYUP 自己就当了中间键，故它安全**证明不了** Alt 单独作修饰键时也安全。
/// </para>
/// </summary>
[Collection(InjectedInputCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class AltOnlyHotkeyMenuTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>`/` 与 `?` 那个键。US 布局下 VK_OEM_2。</summary>
    private const ushort VK_OEM_2 = 0xBF;

    private const ushort VK_ESCAPE = 0x1B;

    public void Dispose()
    {
        // 菜单若真被打开了，它会吃掉后续输入直到 Esc；不关掉就把桌面留在半开的菜单上，
        // 下一条注入型测试会莫名其妙地什么也收不到。
        CloseAnyOpenMenu();

        SynthesizedInput.ReleaseEverythingUsedByTests();
    }

    /// <summary>
    /// 阳性对照，**必须排在前面读**：这台机器上「真按一下 Alt」确实会激活菜单栏。
    /// 没有它，下面那条若报 0 次，既可能是真没激活，也可能是这个窗口压根没菜单、
    /// 或者计数器没接上——两者都以同一种方式「通过」。
    /// </summary>
    [Fact]
    public void APlainAltTap_DoesActivateTheMenuBar_OnThisMachine()
    {
        using var target = NewMenuWindow();

        TestWindow.ResetMenuActivations();
        SynthesizedInput.Send(0, (VirtualKey.VK_LMENU, false), (VirtualKey.VK_LMENU, true));

        Assert.True(
            WaitFor(() => TestWindow.MenuActivations > 0),
            "阳性对照失败：真按一下 Alt 都没让菜单栏动静，本组其余断言全部无效");
    }

    /// <summary>
    /// 本组的正题：装上 <c>Alt + /</c> 这个热键，合成一次完整的按住—松开，
    /// 看目标窗口的菜单栏动不动。
    ///
    /// <para>
    /// 若菜单真被激活，<c>Alt + 单键</c> 这类组合就不该开放给用户，
    /// 或须补一个中间键（手法见 <see cref="VirtualKey.VK_NEUTRAL"/>）。
    /// </para>
    /// </summary>
    [Fact]
    public void HoldingAltSlash_AsAHotkey_DoesNotPopTheTargetsMenuBar()
    {
        using var target = NewMenuWindow();
        using var hook = new KeyboardHook(NullLogger<KeyboardHook>.Instance);

        var pressed = 0;
        var released = 0;
        hook.Pressed += (_, _) => Interlocked.Increment(ref pressed);
        hook.Released += (_, _) => Interlocked.Increment(ref released);

        hook.Start(new HotkeyCombination(ModifierKeys.Alt, "/"));

        TestWindow.ResetMenuActivations();

        // 真人按住热键说话再松开的形状：Alt 先下，主键随后，松开时主键先起、Alt 后起。
        SynthesizedInput.Send(0, (VirtualKey.VK_LMENU, false), (VK_OEM_2, false));
        Thread.Sleep(120);
        SynthesizedInput.Send(0, (VK_OEM_2, true), (VirtualKey.VK_LMENU, true));

        // 前提得成立：热键真的被识别了。没识别的话主键根本没被吞，
        // 这条测试量的就不是它声称要量的那件事。
        Assert.True(
            WaitFor(() => Volatile.Read(ref pressed) > 0 && Volatile.Read(ref released) > 0),
            $"热键没被识别（Pressed={pressed} Released={released}），本条断言无效");

        // 给菜单消息留够时间再判——「什么也没发生」这类断言读得太早，什么实现都能通过。
        Thread.Sleep(TestWindow.SettleMilliseconds);

        Assert.Equal(0, TestWindow.MenuActivations);
    }

    /// <summary>
    /// 与上一条同形，只把修饰键换成 <c>Ctrl+Alt</c>，好让两种结局区分得开：
    /// 两条都红说明是台架或本机状态，只有 Alt 那条红才说明问题出在「Alt 单独作修饰键」。
    /// </summary>
    [Fact]
    public void HoldingCtrlAltSlash_DoesNotPopTheMenuBarEither()
    {
        using var target = NewMenuWindow();
        using var hook = new KeyboardHook(NullLogger<KeyboardHook>.Instance);

        var released = 0;
        hook.Released += (_, _) => Interlocked.Increment(ref released);

        hook.Start(new HotkeyCombination(ModifierKeys.Ctrl | ModifierKeys.Alt, "/"));

        TestWindow.ResetMenuActivations();

        SynthesizedInput.Send(
            0, (VirtualKey.VK_LCONTROL, false), (VirtualKey.VK_LMENU, false), (VK_OEM_2, false));
        Thread.Sleep(120);
        SynthesizedInput.Send(
            0, (VK_OEM_2, true), (VirtualKey.VK_LMENU, true), (VirtualKey.VK_LCONTROL, true));

        Assert.True(
            WaitFor(() => Volatile.Read(ref released) > 0),
            $"热键没被识别（Released={released}），本条断言无效");

        Thread.Sleep(TestWindow.SettleMilliseconds);

        Assert.Equal(0, TestWindow.MenuActivations);
    }

    private static TestWindow NewMenuWindow()
    {
        var window = new TestWindow("lisscribe 菜单靶子", withMenuBar: true);

        Assert.True(window.BringToForeground(),
            "系统拒绝了前台抢占——这是前提不成立，不是被测行为失败");

        return window;
    }

    private static void CloseAnyOpenMenu()
    {
        if (TestWindow.MenuActivations == 0)
        {
            return;
        }

        SynthesizedInput.Send(0, (VK_ESCAPE, false), (VK_ESCAPE, true));
        SynthesizedInput.Send(0, (VK_ESCAPE, false), (VK_ESCAPE, true));

        Thread.Sleep(100);
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }
}
