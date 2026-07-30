using Lisscribe.Core.Hotkey;
using Xunit;

namespace Lisscribe.Core.Tests.Hotkey;

public sealed class HotkeyMatcherTests
{
    private static readonly HotkeyCombination CtrlAltSpace =
        new(ModifierKeys.Ctrl | ModifierKeys.Alt, "Space");

    private static HotkeyMatcher Matcher => new(CtrlAltSpace);

    [Fact]
    public void Evaluate_MainKeyDownWithAllModifiers_SuppressesAndStartsPress()
    {
        var action = Matcher.Evaluate("Space", isKeyDown: true,
            activeModifiers: ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(KeyAction.SuppressAndPress, action);
    }

    [Fact]
    public void Evaluate_MainKeyDownWithMissingModifier_PassesThrough()
    {
        var action = Matcher.Evaluate("Space", isKeyDown: true, activeModifiers: ModifierKeys.Ctrl);

        Assert.Equal(KeyAction.PassThrough, action);
    }

    [Fact]
    public void Evaluate_MainKeyDownWithExtraModifier_PassesThrough()
    {
        // Ctrl+Alt+Shift+Space 不是 Ctrl+Alt+Space。多按了修饰键就不该触发，
        // 否则用户的其他快捷键会被无差别吞掉。
        var action = Matcher.Evaluate("Space", isKeyDown: true,
            activeModifiers: ModifierKeys.Ctrl | ModifierKeys.Alt | ModifierKeys.Shift);

        Assert.Equal(KeyAction.PassThrough, action);
    }

    [Fact]
    public void Evaluate_ModifierKeyItself_AlwaysPassesThrough()
    {
        // 拦截主键、放行修饰键。若连 Ctrl 一起吞，用户按 Ctrl 时所有应用都失灵。
        var matcher = Matcher;

        Assert.Equal(KeyAction.PassThrough,
            matcher.Evaluate("Ctrl", isKeyDown: true, ModifierKeys.None));
        Assert.Equal(KeyAction.PassThrough,
            matcher.Evaluate("Alt", isKeyDown: true, ModifierKeys.Ctrl));
    }

    [Fact]
    public void Evaluate_UnrelatedKey_PassesThrough()
    {
        var action = Matcher.Evaluate("A", isKeyDown: true,
            activeModifiers: ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(KeyAction.PassThrough, action);
    }

    [Fact]
    public void Evaluate_MainKeyUpAfterPress_SuppressesAndReleases()
    {
        var matcher = Matcher;
        matcher.Evaluate("Space", isKeyDown: true, ModifierKeys.Ctrl | ModifierKeys.Alt);

        var action = matcher.Evaluate("Space", isKeyDown: false, ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(KeyAction.SuppressAndRelease, action);
    }

    [Fact]
    public void Evaluate_MainKeyUp_ReleasesEvenIfModifiersWereAlreadyLetGo()
    {
        // Released 在主键松开时触发，与修饰键是否仍按下无关。
        // 用户先松 Ctrl 再松 Space 是极常见的手势，不得因此收不到 release。
        var matcher = Matcher;
        matcher.Evaluate("Space", isKeyDown: true, ModifierKeys.Ctrl | ModifierKeys.Alt);

        var action = matcher.Evaluate("Space", isKeyDown: false, ModifierKeys.None);

        Assert.Equal(KeyAction.SuppressAndRelease, action);
    }

    [Fact]
    public void Evaluate_MainKeyUpWithoutPriorPress_PassesThrough()
    {
        // 组合未成立时按下的 Space 已放行给系统，其 KEYUP 也必须放行，
        // 否则目标应用会收到一个没有配对 KEYUP 的 KEYDOWN。
        var action = Matcher.Evaluate("Space", isKeyDown: false, ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(KeyAction.PassThrough, action);
    }

    [Fact]
    public void Evaluate_RepeatedMainKeyDownWhileHeld_IsSuppressedButDoesNotRePress()
    {
        // 按住不放时 Windows 持续投递 KEYDOWN。必须继续吞（否则字符会漏进目标窗口），
        // 但不得重复触发 Pressed（否则录音会被反复重启，用户的话全丢）。
        var matcher = Matcher;
        matcher.Evaluate("Space", isKeyDown: true, ModifierKeys.Ctrl | ModifierKeys.Alt);

        var action = matcher.Evaluate("Space", isKeyDown: true, ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(KeyAction.Suppress, action);
    }

    [Fact]
    public void Evaluate_AfterRelease_CanPressAgain()
    {
        var matcher = Matcher;
        var mods = ModifierKeys.Ctrl | ModifierKeys.Alt;
        matcher.Evaluate("Space", true, mods);
        matcher.Evaluate("Space", false, mods);

        var action = matcher.Evaluate("Space", true, mods);

        Assert.Equal(KeyAction.SuppressAndPress, action);
    }

    [Fact]
    public void Reset_AfterSuppressedPress_SuppressesTheOutstandingRelease()
    {
        // Reset() 发生在「已吞下 KEYDOWN、尚未收到 KEYUP」之后（例如钩子存活期间的锁屏恢复）。
        // 那次 KEYDOWN 从未放行给系统，故其之后真正到来的 KEYUP 也必须被吞掉，
        // 否则目标应用会收到一个没有配对 KEYDOWN 的 KEYUP——与「从未匹配」分支要防止的
        // 是同一类缺陷，只是方向相反。
        var matcher = Matcher;
        matcher.Evaluate("Space", true, ModifierKeys.Ctrl | ModifierKeys.Alt);

        matcher.Reset();

        Assert.Equal(KeyAction.Suppress,
            matcher.Evaluate("Space", false, ModifierKeys.Ctrl | ModifierKeys.Alt));
    }

    [Fact]
    public void Reset_WithNothingHeld_LeavesSubsequentKeyUpPassingThrough()
    {
        // 干净状态下的 Reset()（例如钩子启动时的例行复位）不得凭空产生一个
        // 待吞的 release——「从未匹配」路径必须保持不变。
        var matcher = Matcher;

        matcher.Reset();

        Assert.Equal(KeyAction.PassThrough,
            matcher.Evaluate("Space", false, ModifierKeys.Ctrl | ModifierKeys.Alt));
    }

    [Fact]
    public void Evaluate_MainKeyNameDiffersOnlyByCase_StillMatches()
    {
        // 主键比较刻意用 OrdinalIgnoreCase：钩子供给的键名大小写不该由本纯逻辑层去保证。
        // 这条钉住该选择，防止日后有人把它「改回」序数比较而全套测试仍然全绿。
        var action = Matcher.Evaluate("space", isKeyDown: true,
            activeModifiers: ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(KeyAction.SuppressAndPress, action);
    }

    [Fact]
    public void Reset_CalledTwiceAfterSuppressedPress_StillSuppressesTheOutstandingRelease()
    {
        // Reset() 记录的是「待吞 release 仍未兑现」，只能被置位，不能被清空
        // ——否则连续两次 Reset()（中间没有任何按键事件）会把第一次记下的标志覆盖回
        // false。看门狗 tick、前台窗口变化、会话变化、电源事件四路都会调 Resync/Reset，
        // 背靠背触发（例如切前台窗口的同时系统也在发会话事件——正是锁屏这一幕）是常态，
        // 不是边角情况。
        var matcher = Matcher;
        matcher.Evaluate("Space", true, ModifierKeys.Ctrl | ModifierKeys.Alt);

        matcher.Reset();
        matcher.Reset();

        Assert.Equal(KeyAction.Suppress,
            matcher.Evaluate("Space", false, ModifierKeys.Ctrl | ModifierKeys.Alt));
    }

    [Fact]
    public void Reset_AfterSuppressedPress_ConsumesTheFlagExactlyOnce()
    {
        // 待吞标志被第一次 KEYUP 消费后即清除；紧随其后的第二次 KEYUP
        // （物理上不该出现，但防御性验证）必须走「从未匹配」分支放行。
        var matcher = Matcher;
        matcher.Evaluate("Space", true, ModifierKeys.Ctrl | ModifierKeys.Alt);
        matcher.Reset();
        matcher.Evaluate("Space", false, ModifierKeys.Ctrl | ModifierKeys.Alt);

        var action = matcher.Evaluate("Space", false, ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(KeyAction.PassThrough, action);
    }

    [Fact]
    public void Reset_AfterSuppressedPress_ThenFreshMatchedPress_ReleasesNormally()
    {
        // Reset() 之后若又按出一次全新且合法的组合，那次按下开启的是全新会话，
        // 不该被「reset 遗留的待吞 release」标志污染——其 KEYUP 必须正常触发 Released。
        var matcher = Matcher;
        var mods = ModifierKeys.Ctrl | ModifierKeys.Alt;
        matcher.Evaluate("Space", true, mods);
        matcher.Reset();
        matcher.Evaluate("Space", true, mods);

        var action = matcher.Evaluate("Space", false, mods);

        Assert.Equal(KeyAction.SuppressAndRelease, action);
    }

    [Fact]
    public void Reset_AfterAlreadyAccountedForRelease_LeavesSubsequentKeyUpPassingThrough()
    {
        // 按下已经正常松开（Released 已触发）之后再调 Reset()，不该凭空产生一个
        // 待吞的 release——那次按下已经被完整地处理过了。
        var matcher = Matcher;
        var mods = ModifierKeys.Ctrl | ModifierKeys.Alt;
        matcher.Evaluate("Space", true, mods);
        matcher.Evaluate("Space", false, mods);

        matcher.Reset();

        Assert.Equal(KeyAction.PassThrough, matcher.Evaluate("Space", false, mods));
    }

    [Fact]
    public void Reset_AfterUnmatchedPress_LeavesSubsequentKeyUpPassingThrough()
    {
        // 修饰键不对而从未匹配的按下（已放行）之后调 Reset()，不该产生待吞标志
        // ——它本就不属于「已吞下 KEYDOWN」的情形。
        var matcher = Matcher;
        matcher.Evaluate("Space", true, ModifierKeys.Ctrl); // 缺 Alt，未匹配，已放行

        matcher.Reset();

        Assert.Equal(KeyAction.PassThrough,
            matcher.Evaluate("Space", false, ModifierKeys.Ctrl | ModifierKeys.Alt));
    }

    /// <summary>
    /// 放行态的立身之本：暂停期间**热键本身**必须交还给别的窗口。
    ///
    /// <para>
    /// 少了这一条，配置窗录新热键时按下当前热键就是「按了没反应」——键在钩子里被吞掉，
    /// 一个事件都到不了任何进程。
    /// </para>
    /// </summary>
    [Fact]
    public void PassThrough_HandsTheHotkeyItselfBackToWhoeverHasFocus()
    {
        var matcher = Matcher;
        var mods = ModifierKeys.Ctrl | ModifierKeys.Alt;

        matcher.PassThrough = true;

        Assert.Equal(KeyAction.PassThrough, matcher.Evaluate("Space", true, mods));
        Assert.Equal(KeyAction.PassThrough, matcher.Evaluate("Space", false, mods));
    }

    [Fact]
    public void ClearingPassThrough_ArmsItAgain()
    {
        // 阳性对照兼回归：放行期间那次按下没有留下任何痕迹，收回开关后第一次按下就该成立。
        var matcher = Matcher;
        var mods = ModifierKeys.Ctrl | ModifierKeys.Alt;

        matcher.PassThrough = true;
        matcher.Evaluate("Space", true, mods);
        matcher.Evaluate("Space", false, mods);
        matcher.PassThrough = false;

        Assert.Equal(KeyAction.SuppressAndPress, matcher.Evaluate("Space", true, mods));
    }

    [Fact]
    public void PassThrough_StillSwallowsTheKeyUpItAlreadyOwes()
    {
        // 按住热键时被暂停（托盘点「暂停」就是这个现场）：那次 KEYDOWN 已经吞下去了，
        // 放行它的 KEYUP 就是给目标应用一个没有配对按下的松开。欠的这一次得还完。
        var matcher = Matcher;
        var mods = ModifierKeys.Ctrl | ModifierKeys.Alt;

        Assert.Equal(KeyAction.SuppressAndPress, matcher.Evaluate("Space", true, mods));

        matcher.PassThrough = true;

        Assert.Equal(KeyAction.Suppress, matcher.Evaluate("Space", false, mods));

        // 只还一次。下一轮按放照常放行，否则暂停期间主键就永远打不出来了。
        Assert.Equal(KeyAction.PassThrough, matcher.Evaluate("Space", true, mods));
        Assert.Equal(KeyAction.PassThrough, matcher.Evaluate("Space", false, mods));
    }

    [Fact]
    public void PassThrough_NeverTouchesKeysThatAreNotTheHotkey()
    {
        // 阴性对照：放行态只关主键那一条判定，别的键本来就一直是放行的。
        var matcher = Matcher;

        matcher.PassThrough = true;

        Assert.Equal(KeyAction.PassThrough, matcher.Evaluate("A", true, ModifierKeys.Ctrl));
        Assert.Equal(KeyAction.PassThrough, matcher.Evaluate("Ctrl", true, ModifierKeys.Ctrl));
    }
}
