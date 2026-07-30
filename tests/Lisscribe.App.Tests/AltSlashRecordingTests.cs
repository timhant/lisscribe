using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Lisscribe.App.Views;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 「Alt + / 能不能配成热键」——Avalonia 在 Alt 按住时把 <c>/</c> 报成哪个 <see cref="Key"/>。
///
/// <para>
/// <b>为什么非得动真格。</b> <see cref="HotkeyRecorderControlTests"/> 那一组是自己 new 一个
/// <see cref="KeyEventArgs"/> 再 <c>RaiseEvent</c>，于是「Avalonia 会给什么 Key」这件事
/// 被测试自己假定掉了。而这正是风险所在：WPF 在修饰键含 Alt 时把 <c>e.Key</c> 报成
/// <c>Key.System</c>、真键塞进 <c>e.SystemKey</c>；Avalonia 没有 <c>SystemKey</c> 这个属性，
/// 若它照搬了前半段，<see cref="HotkeyRecording.IsModifierKey"/> 会把 <c>Key.System</c>
/// 当成「还在按修饰键，等下一下」而**静默**返回 false ——
/// 用户按 <c>Alt + /</c> 的观感就是「按了没反应」，一句提示也没有。
/// </para>
///
/// <para>
/// 本组因此走完整条真链路：真 Avalonia 窗口、抢到前台、<c>SendInput</c> 合成按键、
/// 读控件真收到的事件。取样口 <see cref="NativeProbe"/> 不经 Avalonia，
/// 与被测对象无共用环节。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class AltSlashRecordingTests
{
    /// <summary>
    /// 本组的核心事实：按住 Alt 敲 <c>/</c> 时，Avalonia 交给控件的是哪个 <see cref="Key"/>。
    /// 断言写成「不是 Key.System」而不是「必须等于 OemQuestion」，
    /// 是因为**真正致命的只有 Key.System 那一种**（它会被当成修饰键静默吃掉）；
    /// 若哪天 Avalonia 换成别的具名键，下面那条录制断言自会说话。
    /// </summary>
    [Fact]
    public void HoldingAlt_DoesNotTurnTheSlashKeyInto_KeySystem()
    {
        var seen = CaptureRealKeyPress(withAlt: true);

        Assert.NotNull(seen);
        Assert.NotEqual(Key.System, seen.Value.Key);
        Assert.True(seen.Value.Modifiers.HasFlag(KeyModifiers.Alt),
            $"Alt 没被算进修饰键里，实收 {seen.Value.Modifiers}");
    }

    /// <summary>
    /// 用户真正关心的那一格：**这一下能不能被录成一组热键**。
    /// 上一条只是把病因隔离出来，这一条才是「Alt + / 配得成吗」的答案。
    /// </summary>
    [Fact]
    public void AltSlash_IsActuallyRecordable_AsAHotkey()
    {
        var seen = CaptureRealKeyPress(withAlt: true);

        Assert.NotNull(seen);

        var captured = HotkeyRecording.TryCapture(
            seen.Value.Modifiers, seen.Value.Key, out var modifiers, out var mainKey);

        Assert.True(captured, $"录不下来：Avalonia 给的是 Key.{seen.Value.Key}（修饰键 {seen.Value.Modifiers}）");
        Assert.Equal(["Alt"], modifiers);
        Assert.Equal("/", mainKey);
    }

    /// <summary>
    /// 阳性对照：**同一套注入手法**换成 Ctrl 也要走通。
    ///
    /// <para>
    /// 少了这条，上面两条若因为「窗口没抢到前台」「SendInput 被吞」而收不到任何按键，
    /// 失败信息会指向 Alt，而真因与 Alt 毫无关系。有了它，两边一起红就说明是台架坏了。
    /// </para>
    /// </summary>
    [Fact]
    public void TheSameHarness_RecordsCtrlSlash_SoAFailureAboveReallyIsAboutAlt()
    {
        var seen = CaptureRealKeyPress(withAlt: false);

        Assert.NotNull(seen);

        var captured = HotkeyRecording.TryCapture(
            seen.Value.Modifiers, seen.Value.Key, out var modifiers, out var mainKey);

        Assert.True(captured, $"连 Ctrl + / 都录不下来，台架有问题：Key.{seen.Value.Key}");
        Assert.Equal(["Ctrl"], modifiers);
        Assert.Equal("/", mainKey);
    }

    /// <summary>
    /// 建一个真窗口、抢到前台、合成一次「修饰键 + <c>/</c>」，返回控件真收到的那一下。
    /// 返回 <c>null</c> 表示等到超时也没收到非修饰键的 KeyDown。
    /// </summary>
    private static (Key Key, KeyModifiers Modifiers)? CaptureRealKeyPress(bool withAlt)
    {
        (Key, KeyModifiers)? seen = null;
        var arrived = new ManualResetEventSlim();

        var window = AvaloniaTestHost.Run(() =>
        {
            // 放一个能拿焦点的控件：键盘输入只到有焦点的地方，空窗口收不到 KeyDown。
            var box = new TextBox { Width = 200 };
            var w = new Window { Width = 320, Height = 120, Content = box, ShowActivated = true };

            // **隧道**而不是冒泡：TextBox 会把普通字符键当成输入吃掉。
            w.AddHandler(
                InputElement.KeyDownEvent,
                (object? _, KeyEventArgs e) =>
                {
                    if (HotkeyRecording.IsModifierKey(e.Key))
                    {
                        return;
                    }

                    seen = (e.Key, e.KeyModifiers);
                    e.Handled = true;
                    arrived.Set();
                },
                RoutingStrategies.Tunnel);

            w.Show();
            box.Focus();

            return w;
        });

        try
        {
            var handle = AvaloniaTestHost.Run(() => window.TryGetPlatformHandle()!.Handle);

            // 抢前台必须从测试线程调，不能从 UI 线程：AttachThreadInput 挂的是调用线程的
            // 输入队列，而 UI 线程此刻正跑着自己的消息循环（见 NativeProbe.BringToForeground）。
            Assert.True(NativeProbe.BringToForeground(handle),
                "系统拒绝了前台抢占——这是前提不成立，不是被测行为失败");

            var modifier = withAlt ? NativeProbe.VK_LMENU : NativeProbe.VK_LCONTROL;

            NativeProbe.SendKeys(
                (modifier, false),
                (NativeProbe.VK_OEM_2, false),
                (NativeProbe.VK_OEM_2, true),
                (modifier, true));

            arrived.Wait(TimeSpan.FromSeconds(3));

            // 事件是在 UI 线程上派发的；跑一轮 Dispatcher 保证在途的那一下已经落地，
            // 免得偶发地在信号量刚置位、字段还没写完时就读。
            AvaloniaTestHost.Run(() => Dispatcher.UIThread.RunJobs(DispatcherPriority.Input));

            return seen;
        }
        finally
        {
            // 顺序有讲究：先把键松掉再关窗。被吞掉 KEYUP 的键会跨进程卡在按住态，
            // 而关窗那一步可能自己就抛。
            NativeProbe.ReleaseKeysUsedHere();

            AvaloniaTestHost.Run(window.Close);
            arrived.Dispose();
        }
    }
}
