using System.Runtime.InteropServices;
using Lisscribe.Core.Hotkey;
using Lisscribe.Platform.Native;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 只供测试使用：拿系统自己的映射复核 <see cref="VirtualKey"/> 里那张写死的扫描码表。
/// 生产代码有意不调它——见 <c>VirtualKey.TryGetScanCode</c> 的注释。
/// </summary>
internal static partial class LayoutProbe
{
    /// <summary>
    /// 必须是 _EX：实测 MAPVK_VK_TO_VSC(0) 会把 E0 前缀丢掉，
    /// 右 Ctrl 在它眼里是 0x001D，与左 Ctrl 无从分辨；_EX(4) 才给 0xE01D。
    /// </summary>
    public const uint MAPVK_VK_TO_VSC_EX = 4;

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKey(uint uCode, uint uMapType);
}

public sealed class VirtualKeyTests
{
    [Theory]
    [InlineData("Space", 0x20)]
    [InlineData("Enter", 0x0D)]
    [InlineData("Tab", 0x09)]
    [InlineData("Escape", 0x1B)]
    [InlineData("Backspace", 0x08)]
    [InlineData("PageUp", 0x21)]
    [InlineData("PageDown", 0x22)]
    [InlineData("CapsLock", 0x14)]
    [InlineData("A", 0x41)]
    [InlineData("Z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("9", 0x39)]
    [InlineData("F1", 0x70)]
    [InlineData("F12", 0x7B)]
    [InlineData("F24", 0x87)]
    [InlineData("`", 0xC0)]
    [InlineData("/", 0xBF)]
    [InlineData("\\", 0xDC)]
    public void TryFromName_MapsKnownKeys(string name, int expected)
    {
        Assert.True(VirtualKey.TryFromName(name, out var vk));
        Assert.Equal((ushort)expected, vk);
    }

    [Fact]
    public void TryFromName_IsCaseInsensitive()
    {
        Assert.True(VirtualKey.TryFromName("space", out var lower));
        Assert.True(VirtualKey.TryFromName("SPACE", out var upper));
        Assert.Equal(lower, upper);
        Assert.Equal((ushort)0x20, lower);
    }

    [Fact]
    public void TryFromName_ForUnknownName_ReturnsFalse()
    {
        Assert.False(VirtualKey.TryFromName("Nonexistent", out _));
    }

    [Theory]
    [InlineData(0xA2, nameof(ModifierKeys.Ctrl))]   // VK_LCONTROL
    [InlineData(0xA3, nameof(ModifierKeys.Ctrl))]   // VK_RCONTROL
    [InlineData(0xA4, nameof(ModifierKeys.Alt))]    // VK_LMENU
    [InlineData(0xA5, nameof(ModifierKeys.Alt))]    // VK_RMENU
    [InlineData(0xA0, nameof(ModifierKeys.Shift))]  // VK_LSHIFT
    [InlineData(0xA1, nameof(ModifierKeys.Shift))]  // VK_RSHIFT
    [InlineData(0x5B, nameof(ModifierKeys.Win))]    // VK_LWIN
    [InlineData(0x5C, nameof(ModifierKeys.Win))]    // VK_RWIN
    public void ToModifier_MapsBothLeftAndRightVariants(int vk, string expectedName)
    {
        var modifier = VirtualKey.ToModifier((ushort)vk);

        Assert.NotNull(modifier);
        Assert.Equal(Enum.Parse<ModifierKeys>(expectedName), modifier.Value);
    }

    [Fact]
    public void ToModifier_ForNonModifierKey_ReturnsNull()
    {
        Assert.Null(VirtualKey.ToModifier(0x41)); // 'A'
    }

    [Theory]
    [InlineData(0x11, nameof(ModifierKeys.Ctrl))]   // VK_CONTROL
    [InlineData(0x12, nameof(ModifierKeys.Alt))]    // VK_MENU
    [InlineData(0x10, nameof(ModifierKeys.Shift))]  // VK_SHIFT
    public void ToModifier_ForAggregateCodes_MapsToTheSameModifier(int vk, string expectedName)
    {
        // 0x10/0x11/0x12 是「不分左右」的聚合码。低层钩子通常给左右分离码，
        // 但 GetAsyncKeyState 重同步时会用到聚合码，两者都要认。
        // Win 键没有聚合码（0x5B/0x5C 之外无 VK_WIN），故此处只有三条。
        Assert.Equal(Enum.Parse<ModifierKeys>(expectedName), VirtualKey.ToModifier((ushort)vk));
    }

    [Theory]
    [InlineData("F5")]
    [InlineData("A")]
    [InlineData("Z")]
    [InlineData("7")]
    [InlineData("F1")]
    [InlineData("F12")]
    [InlineData("F24")]
    [InlineData("Space")]
    [InlineData("PageDown")]
    [InlineData("`")]
    [InlineData("/")]
    [InlineData("'")]
    public void GetName_RoundTripsWithTryFromName(string name)
    {
        Assert.True(VirtualKey.TryFromName(name, out var vk));
        Assert.Equal(name, VirtualKey.GetName(vk));
    }

    [Fact]
    public void GetName_ForEveryNamedCode_RoundTripsBackToTheSameCode()
    {
        // 正反两张表若有一处漏改（例如只往正向表补了新键），这条会把它逮住。
        // 遍历整个 ushort 空间只要几毫秒，比逐键列举可靠得多——列举本身就会漏。
        var named = 0;
        for (var code = 0; code <= ushort.MaxValue; code++)
        {
            var name = VirtualKey.GetName((ushort)code);
            if (name.StartsWith("0x", StringComparison.Ordinal))
            {
                continue; // 无名码回落成十六进制，不参与往返
            }

            named++;
            Assert.True(VirtualKey.TryFromName(name, out var back), $"反向表有 {name} 而正向表没有");
            Assert.Equal((ushort)code, back);
        }

        // 字母 26 + 数字 10 + F 键 24 + 具名键 16 + 标点 11 = 87。
        // 写死这个数是为了让「反向表整个塌成空表」也算失败——否则上面的循环一条不跑照样绿。
        //
        // **合法新增一个按键就会红成 Expected 87 / Actual 88，届时改这个数即可。**
        // 别改成 KeyTable.Length：两边同源就削掉了它挡「反向表整个塌陷」的那颗牙
        // （表塌成空表时正是这条报 Expected 87 / Actual 0）。
        Assert.Equal(87, named);
    }

    [Fact]
    public void GetName_ForUnknownCode_FallsBackToHex()
    {
        // 0x07 未分配。日志与界面宁可显示 "0x07" 也不能抛。
        Assert.Equal("0x07", VirtualKey.GetName(0x07));
    }

    [Theory]
    [InlineData(nameof(ModifierKeys.Ctrl), 0xA2, 0xA3)]
    [InlineData(nameof(ModifierKeys.Alt), 0xA4, 0xA5)]
    [InlineData(nameof(ModifierKeys.Shift), 0xA0, 0xA1)]
    [InlineData(nameof(ModifierKeys.Win), 0x5B, 0x5C)]
    public void GetBothSides_ReturnsLeftAndRightCodes(string modifierName, int left, int right)
    {
        var codes = VirtualKey.GetBothSides(Enum.Parse<ModifierKeys>(modifierName));

        Assert.Equal([(ushort)left, (ushort)right], codes);
    }

    [Fact]
    public void GetBothSides_ForNone_ReturnsEmpty()
    {
        Assert.Empty(VirtualKey.GetBothSides(ModifierKeys.None));
    }

    [Theory]
    [InlineData(0xA0, 0x2A, false)] // VK_LSHIFT
    [InlineData(0xA1, 0x36, false)] // VK_RSHIFT——看着与左 Ctrl/Alt 对称就顺手加扩展位，正是栽在这里
    [InlineData(0xA2, 0x1D, false)] // VK_LCONTROL
    [InlineData(0xA3, 0x1D, true)]  // VK_RCONTROL
    [InlineData(0xA4, 0x38, false)] // VK_LMENU
    [InlineData(0xA5, 0x38, true)]  // VK_RMENU
    [InlineData(0x5B, 0x5B, true)]  // VK_LWIN
    [InlineData(0x5C, 0x5C, true)]  // VK_RWIN
    public void TryGetScanCode_ReturnsSet1MakeCodeAndExtendedBit(int vk, int expectedScan, bool expectedExtended)
    {
        Assert.True(VirtualKey.TryGetScanCode((ushort)vk, out var scanCode, out var isExtended));
        Assert.Equal((ushort)expectedScan, scanCode);
        Assert.Equal(expectedExtended, isExtended);
    }

    [Fact]
    public void TryGetScanCode_ForNonModifierKey_ReturnsFalse()
    {
        // 表只覆盖八个修饰键——BuildModifierRelease 只发这八个，别的键没实测过就不该写进来。
        Assert.False(VirtualKey.TryGetScanCode(0x41, out var scanCode, out var isExtended)); // 'A'
        Assert.Equal(0, scanCode);
        Assert.False(isExtended);
    }

    [Fact]
    public void ScanCodeTable_AgreesWithWhatTheOperatingSystemItselfReports()
    {
        // 写死表的代价是「万一某布局的修饰键扫描码不同便失真」。这条把代价挪到看得见的地方：
        // 跑测试的这台机器上，表若与系统自己的映射不符就转红。
        // 生产代码不调 MapVirtualKeyW，是为了让 InputBuilder 保持纯函数；复核没有这个约束。
        // 受检键集刻意不走 GetBothSides：那是生产代码，拿它划定复核范围，它自己漏掉一侧时
        // 这条测试就静默少查一项而照样绿——实测过，表里右 Shift 写成 0x99 也能 Passed: 1。
        // 预言机的取样范围必须独立于被测对象。
        ushort[] allModifierKeys =
        [
            VirtualKey.VK_LSHIFT, VirtualKey.VK_RSHIFT,
            VirtualKey.VK_LCONTROL, VirtualKey.VK_RCONTROL,
            VirtualKey.VK_LMENU, VirtualKey.VK_RMENU,
            VirtualKey.VK_LWIN, VirtualKey.VK_RWIN,
        ];

        foreach (var vk in allModifierKeys)
        {
            var fromOs = LayoutProbe.MapVirtualKey(vk, LayoutProbe.MAPVK_VK_TO_VSC_EX);

            Assert.True(VirtualKey.TryGetScanCode(vk, out var scanCode, out var isExtended));
            Assert.Equal((ushort)(fromOs & 0xFF), scanCode);
            Assert.Equal((fromOs >> 8) == 0xE0, isExtended);
        }

        // 上面那张写死的键集与 GetBothSides 是两份独立来源，此处对账：任一侧漏了都在此现形。
        ushort[] fromProduction =
        [
            .. VirtualKey.GetBothSides(ModifierKeys.Ctrl),
            .. VirtualKey.GetBothSides(ModifierKeys.Alt),
            .. VirtualKey.GetBothSides(ModifierKeys.Shift),
            .. VirtualKey.GetBothSides(ModifierKeys.Win),
        ];

        Assert.Equal(allModifierKeys.Order(), fromProduction.Order());
    }
}
