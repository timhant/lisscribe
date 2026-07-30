using Lisscribe.Core.Hotkey;
using Lisscribe.Platform;
using Lisscribe.Platform.Native;
using Xunit;

namespace Lisscribe.Platform.Tests;

public sealed class InputBuilderTests
{
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint InputKeyboard = 1;

    [Fact]
    public void BuildText_ForAsciiString_ProducesKeyDownAndKeyUpPerChar()
    {
        var inputs = InputBuilder.BuildText("ab");

        Assert.Equal(4, inputs.Length);
        Assert.Equal('a', (char)inputs[0].union.ki.wScan);
        Assert.Equal(KeyEventUnicode, inputs[0].union.ki.dwFlags);
        Assert.Equal('a', (char)inputs[1].union.ki.wScan);
        Assert.Equal(KeyEventUnicode | KeyEventKeyUp, inputs[1].union.ki.dwFlags);
        Assert.Equal('b', (char)inputs[2].union.ki.wScan);
        Assert.Equal(KeyEventUnicode, inputs[2].union.ki.dwFlags);
        Assert.Equal('b', (char)inputs[3].union.ki.wScan);
        Assert.Equal(KeyEventUnicode | KeyEventKeyUp, inputs[3].union.ki.dwFlags);
    }

    [Fact]
    public void BuildText_MarksEveryInputAsKeyboard()
    {
        // type 默认值 0 是 INPUT_MOUSE。忘了赋值不会编译报错，只会让 SendInput
        // 把整串文本当成鼠标事件——光标乱跳，一个字也不上屏。
        var inputs = InputBuilder.BuildText("ab");

        Assert.All(inputs, i => Assert.Equal(InputKeyboard, i.type));
    }

    [Fact]
    public void BuildText_TagsEveryInputSoTheHookCanLetItThrough()
    {
        // 钩子靠 dwExtraInfo 认出「这是我自己发的」。漏了标记，
        // 注入的每个字符都会被自己的钩子重新捕获。
        var inputs = InputBuilder.BuildText("ab");

        Assert.All(inputs, i => Assert.Equal(User32.LisscribeInjectedTag, i.union.ki.dwExtraInfo));
    }

    [Fact]
    public void BuildText_UsesUnicodeModeNotVirtualKeys()
    {
        // KEYEVENTF_UNICODE 让我们不依赖用户的键盘布局。用 VK 码发中文是不可能的。
        var inputs = InputBuilder.BuildText("中");

        Assert.All(inputs, i => Assert.Equal(0, i.union.ki.wVk));
        Assert.All(inputs, i => Assert.True((i.union.ki.dwFlags & KeyEventUnicode) != 0));
    }

    [Fact]
    public void BuildText_ForChineseCharacters_ProducesOneCodeUnitEach()
    {
        var inputs = InputBuilder.BuildText("打开设置");

        Assert.Equal(8, inputs.Length);
        Assert.Equal('打', (char)inputs[0].union.ki.wScan);
        Assert.Equal('开', (char)inputs[2].union.ki.wScan);
        Assert.Equal('设', (char)inputs[4].union.ki.wScan);
        Assert.Equal('置', (char)inputs[6].union.ki.wScan);
    }

    [Fact]
    public void BuildText_ForSurrogatePair_EmitsBothCodeUnitsSeparately()
    {
        // 关键回归测试：一个 INPUT 只能带一个 WORD 的 wScan。
        // BMP 外字符（emoji、扩展 CJK）是 UTF-16 代理对，必须拆成两个 INPUT 项，
        // 否则目标应用收到半个字符，显示成方块或直接丢弃。
        const string emoji = "😀"; // 😀 U+1F600

        var inputs = InputBuilder.BuildText(emoji);

        Assert.Equal(4, inputs.Length);
        Assert.Equal(0xD83D, inputs[0].union.ki.wScan);
        Assert.Equal(0xD83D, inputs[1].union.ki.wScan);
        Assert.Equal(0xDE00, inputs[2].union.ki.wScan);
        Assert.Equal(0xDE00, inputs[3].union.ki.wScan);

        // 顺序必须是 高位按下/抬起/低位按下/抬起。若写成「两个按下再两个抬起」，
        // 上面四条断言照样全过，但目标应用收到的是两个不成对的按键。
        Assert.Equal(KeyEventUnicode, inputs[0].union.ki.dwFlags);
        Assert.Equal(KeyEventUnicode | KeyEventKeyUp, inputs[1].union.ki.dwFlags);
        Assert.Equal(KeyEventUnicode, inputs[2].union.ki.dwFlags);
        Assert.Equal(KeyEventUnicode | KeyEventKeyUp, inputs[3].union.ki.dwFlags);
    }

    [Fact]
    public void BuildText_ForExtendedCjk_EmitsBothCodeUnits()
    {
        const string extendedCjk = "𠀋"; // U+2000B，扩展 B 区汉字

        var inputs = InputBuilder.BuildText(extendedCjk);

        Assert.Equal(4, inputs.Length);
        Assert.Equal(0xD840, inputs[0].union.ki.wScan);
        Assert.Equal(0xDC0B, inputs[2].union.ki.wScan);
    }

    [Fact]
    public void BuildText_ForEmptyString_ReturnsEmpty()
    {
        Assert.Empty(InputBuilder.BuildText(string.Empty));
    }

    [Fact]
    public void BuildModifierRelease_ForCtrlAlt_EmitsKeyUpForBothSides()
    {
        // 注入前必须释放物理按下的修饰键，
        // 否则「打开设置」会被 VS Code 当成一串 Ctrl+Alt+快捷键。
        // 左右两侧都要发 —— 用户按的可能是右 Alt。
        var inputs = InputBuilder.BuildModifierRelease(ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(4, inputs.Length);
        Assert.All(inputs, i => Assert.True((i.union.ki.dwFlags & KeyEventKeyUp) != 0));

        var codes = inputs.Select(i => i.union.ki.wVk).ToArray();
        Assert.Contains((ushort)0xA2, codes); // VK_LCONTROL
        Assert.Contains((ushort)0xA3, codes); // VK_RCONTROL
        Assert.Contains((ushort)0xA4, codes); // VK_LMENU
        Assert.Contains((ushort)0xA5, codes); // VK_RMENU
    }

    [Fact]
    public void BuildModifierRelease_SendsVirtualKeysNotUnicode()
    {
        // 带上 KEYEVENTF_UNICODE 的话 wVk 会被忽略、wScan 当成字符码，
        // 于是「释放 Ctrl」变成「上屏一个 U+0000」——修饰键仍旧按着。
        var inputs = InputBuilder.BuildModifierRelease(ModifierKeys.Ctrl);

        Assert.All(inputs, i => Assert.Equal(0u, i.union.ki.dwFlags & KeyEventUnicode));
        Assert.All(inputs, i => Assert.Equal(InputKeyboard, i.type));
        Assert.All(inputs, i => Assert.Equal(User32.LisscribeInjectedTag, i.union.ki.dwExtraInfo));
    }

    [Theory]
    [InlineData(nameof(ModifierKeys.Shift), 0xA0, 0x2A, false)] // VK_LSHIFT
    [InlineData(nameof(ModifierKeys.Shift), 0xA1, 0x36, false)] // VK_RSHIFT——右 Shift 不是扩展键
    [InlineData(nameof(ModifierKeys.Ctrl), 0xA2, 0x1D, false)]  // VK_LCONTROL
    [InlineData(nameof(ModifierKeys.Ctrl), 0xA3, 0x1D, true)]   // VK_RCONTROL
    [InlineData(nameof(ModifierKeys.Alt), 0xA4, 0x38, false)]   // VK_LMENU
    [InlineData(nameof(ModifierKeys.Alt), 0xA5, 0x38, true)]    // VK_RMENU
    [InlineData(nameof(ModifierKeys.Win), 0x5B, 0x5B, true)]    // VK_LWIN
    [InlineData(nameof(ModifierKeys.Win), 0x5C, 0x5C, true)]    // VK_RWIN
    public void BuildModifierRelease_CarriesTheSameScanCodeShapeAsRealHardware(
        string modifierName, int vk, int expectedScan, bool expectedExtended)
    {
        // 只填 wVk 时 GetKeyState/GetAsyncKeyState 已能正确复位（win32k 按 vk 索引状态表），
        // 但事件本身与硬件不同形：实测硬件按下右 Ctrl 给 scan=0x1D、extendedBit=1，
        // 而只填 wVk 的注入给 scan=0x00、extendedBit=0——**系统不会替 wVk 补扫描码**。
        // 靠 (MakeCode, E0) 配对 make/break 的消费者（Raw Input 游戏、DirectInput、
        // 转发扫描码的 RDP/VNC 客户端）收到的 break 与其记下的 make 对不上号，
        // 其内部模型里修饰键就一直不释放。左侧同样中招：左 Ctrl 的 break 也是 MakeCode=0x00。
        var inputs = InputBuilder.BuildModifierRelease(Enum.Parse<ModifierKeys>(modifierName));

        var input = inputs.Single(i => i.union.ki.wVk == (ushort)vk);
        Assert.Equal((ushort)expectedScan, input.union.ki.wScan);
        Assert.Equal(expectedExtended, (input.union.ki.dwFlags & KeyEventExtendedKey) != 0);
    }

    [Fact]
    public void BuildModifierRelease_ForNone_ReturnsEmpty()
    {
        Assert.Empty(InputBuilder.BuildModifierRelease(ModifierKeys.None));
    }

    /// <summary>
    /// 中间键那一对本身的形状。热键把主键吞掉时会发它——被吞的键不到 win32k，
    /// 于是 <c>Alt + X</c> 这类组合在系统眼里就是一次孤立的 Alt 按放，
    /// 会激活目标应用的菜单栏（见 <c>AltOnlyHotkeyMenuTests</c>）。
    /// </summary>
    [Fact]
    public void BuildNeutralTap_IsAPressAndARelease_OfTheUnassignedCode()
    {
        var inputs = InputBuilder.BuildNeutralTap();

        Assert.Equal(2, inputs.Length);

        Assert.Equal(VirtualKey.VK_NEUTRAL, inputs[0].union.ki.wVk);
        Assert.Equal(0u, inputs[0].union.ki.dwFlags & KeyEventKeyUp);

        // 只按不抬同样能抑制，但会把这个键永久留在按下态，且跨进程存活。
        Assert.Equal(VirtualKey.VK_NEUTRAL, inputs[1].union.ki.wVk);
        Assert.True((inputs[1].union.ki.dwFlags & KeyEventKeyUp) != 0, "中间键只按不抬会把它永久留在按下态");

        // 必须带注入标记，否则我们自己的钩子会把它当成用户真按的键再处理一遍。
        Assert.All(inputs, input =>
            Assert.Equal(User32.LisscribeInjectedTag, input.union.ki.dwExtraInfo));
    }

    [Fact]
    public void BuildModifierRelease_ForAllModifiers_EmitsEightKeyUpsPlusTheNeutralPair()
    {
        var all = ModifierKeys.Ctrl | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Win;

        var inputs = InputBuilder.BuildModifierRelease(all);

        // 八个修饰键各一次 KEYUP，外加 Win 之前那对中间键的按下与抬起。
        Assert.Equal(10, inputs.Length);
        Assert.Equal(
            [(ushort)0x5B, (ushort)0x5C, (ushort)0xA0, (ushort)0xA1,
             (ushort)0xA2, (ushort)0xA3, (ushort)0xA4, (ushort)0xA5],
            [.. inputs.Where(i => i.union.ki.wVk != VirtualKey.VK_NEUTRAL)
                      .Select(i => i.union.ki.wVk).Order()]);
    }

    [Fact]
    public void BuildModifierRelease_WhenWinIsHeld_PutsANeutralKeyPressBeforeTheWinKeyUps()
    {
        // 用户只按住 Win 说话时，补发的 Win↑ 在 shell 眼里是一次完整的 Win 按放——
        // 弹出开始菜单、抢走前台，识别文本随后逐字进它的搜索框。插一个中间键才能堵死。
        var inputs = InputBuilder.BuildModifierRelease(ModifierKeys.Win);

        Assert.Equal(4, inputs.Length);

        // 顺序是承重的：中间键必须在两个 Win KEYUP **之前**，插在后面等于没插。
        Assert.Equal(VirtualKey.VK_NEUTRAL, inputs[0].union.ki.wVk);
        Assert.Equal(0u, inputs[0].union.ki.dwFlags & KeyEventKeyUp);
        Assert.Equal(VirtualKey.VK_NEUTRAL, inputs[1].union.ki.wVk);
        Assert.True((inputs[1].union.ki.dwFlags & KeyEventKeyUp) != 0, "中间键只按不抬会把它永久留在按下态");

        Assert.Equal((ushort)0x5B, inputs[2].union.ki.wVk); // VK_LWIN
        Assert.Equal((ushort)0x5C, inputs[3].union.ki.wVk); // VK_RWIN
    }

    [Fact]
    public void BuildModifierRelease_WhenWinIsNotHeld_EmitsNoNeutralKey()
    {
        // 另一侧。没有它，「无条件插一对中间键」这种实现也能通过上一条，
        // 而那会在每一次注入前给目标应用平白多发两个按键事件。
        var inputs = InputBuilder.BuildModifierRelease(
            ModifierKeys.Ctrl | ModifierKeys.Alt | ModifierKeys.Shift);

        Assert.Equal(6, inputs.Length);
        Assert.DoesNotContain(inputs, i => i.union.ki.wVk == VirtualKey.VK_NEUTRAL);
    }
}
