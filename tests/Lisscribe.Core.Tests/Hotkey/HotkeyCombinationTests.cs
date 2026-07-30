using Lisscribe.Core.Config;
using Lisscribe.Core.Hotkey;
using Xunit;

namespace Lisscribe.Core.Tests.Hotkey;

public sealed class HotkeyCombinationTests
{
    [Fact]
    public void TryParse_WithCtrlAltSpace_Succeeds()
    {
        var section = new HotkeySection(["Ctrl", "Alt"], "Space", 300);

        var ok = HotkeyCombination.TryParse(section, out var combo, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(ModifierKeys.Ctrl | ModifierKeys.Alt, combo.Modifiers);
        Assert.Equal("Space", combo.MainKey);
    }

    [Fact]
    public void TryParse_WithNoModifier_FailsBecauseT0RequiresCombination()
    {
        // 只支持「≥1 修饰键 + 恰好 1 主键」。单键热键要求把被吞掉的那一下按原样补发回系统，
        // 那是另一整套机制，故明确拒绝。
        var section = new HotkeySection([], "Space", 300);

        var ok = HotkeyCombination.TryParse(section, out _, out var error);

        Assert.False(ok);
        Assert.Contains("至少一个修饰键", error);
    }

    [Fact]
    public void TryParse_WithEmptyMainKey_Fails()
    {
        var section = new HotkeySection(["Ctrl"], "", 300);

        var ok = HotkeyCombination.TryParse(section, out _, out var error);

        Assert.False(ok);
        Assert.Contains("主键", error);
    }

    [Fact]
    public void TryParse_WhenMainKeyIsItselfAModifier_Fails()
    {
        // 纯修饰键组合（如 Ctrl+Alt）不构成合法热键——按住 Ctrl 时 Alt 一按下就会触发。
        var section = new HotkeySection(["Ctrl"], "Alt", 300);

        var ok = HotkeyCombination.TryParse(section, out _, out var error);

        Assert.False(ok);
        Assert.Contains("修饰键", error);
    }

    [Fact]
    public void TryParse_WithUnknownModifier_Fails()
    {
        var section = new HotkeySection(["Hyper"], "Space", 300);

        var ok = HotkeyCombination.TryParse(section, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Hyper", error);
    }

    [Fact]
    public void TryParse_WithNullModifiers_ReportsErrorInsteadOfThrowing()
    {
        // JSON 反序列化可绕过 NRT 产出 null 数组（ConfigStore/ConfigValidator 已因此吃过亏）。
        // out string? error 的契约是「报告失败」而非「抛异常」，故此处须用 null! 构造并断言不抛。
        var section = new HotkeySection(null!, "Space", 300);

        var ok = HotkeyCombination.TryParse(section, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_WithMixedCaseModifiers_MatchesCaseInsensitively()
    {
        var section = new HotkeySection(["cTrL", "aLt"], "Space", 300);

        var ok = HotkeyCombination.TryParse(section, out var combo, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(ModifierKeys.Ctrl | ModifierKeys.Alt, combo.Modifiers);
    }

    [Fact]
    public void TryParse_WhenMainKeyIsModifierInOddCasing_Fails()
    {
        var section = new HotkeySection(["Ctrl"], "aLt", 300);

        var ok = HotkeyCombination.TryParse(section, out _, out var error);

        Assert.False(ok);
        Assert.Contains("修饰键", error);
    }

    [Fact]
    public void ToDisplayString_OrdersModifiersCanonically()
    {
        var combo = new HotkeyCombination(
            ModifierKeys.Shift | ModifierKeys.Ctrl | ModifierKeys.Alt, "F2");

        Assert.Equal("Ctrl + Alt + Shift + F2", combo.ToDisplayString());
    }

    [Fact]
    public void ToDisplayString_WithSingleModifier_HasNoStrayPlus()
    {
        var combo = new HotkeyCombination(ModifierKeys.Win, "Q");

        Assert.Equal("Win + Q", combo.ToDisplayString());
    }
}
