using Avalonia.Input;
using Lisscribe.App.Views;
using Lisscribe.Core.Config;
using Lisscribe.Core.Hotkey;
using Lisscribe.Platform.Native;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 热键录制的翻译层：一次 Avalonia 按键 → 配置文件里的写法。纯函数，不需要窗口。
/// </summary>
public sealed class HotkeyRecordingTests
{
    [Fact]
    public void TryCapture_TranslatesTheDefaultHotkey()
    {
        Assert.True(HotkeyRecording.TryCapture(
            KeyModifiers.Control | KeyModifiers.Alt, Key.Space, out var modifiers, out var mainKey));

        Assert.Equal(["Ctrl", "Alt"], modifiers);
        Assert.Equal("Space", mainKey);
    }

    [Fact]
    public void TryCapture_CallsTheWindowsKeyWin_NotMeta()
    {
        // Avalonia 管它叫 Meta，配置文件与 ConfigValidator 的规范顺序里叫 Win。
        // 翻错的症状是校验器报「未知的修饰键 "Meta"」——而用户明明按的是 Win 键。
        Assert.True(HotkeyRecording.TryCapture(
            KeyModifiers.Meta, Key.F8, out var modifiers, out _));

        Assert.Equal(["Win"], modifiers);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightCtrl)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.RWin)]
    public void TryCapture_WhileOnlyModifiersAreDown_SaysNotYet(Key key)
    {
        // 按下 Ctrl 那一瞬间就把「Ctrl」记成主键，是这类控件最常见的坏法。
        Assert.False(HotkeyRecording.TryCapture(KeyModifiers.Control, key, out _, out _));
    }

    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.Z, "Z")]
    [InlineData(Key.D0, "0")]
    [InlineData(Key.D7, "7")]
    [InlineData(Key.F1, "F1")]
    [InlineData(Key.F12, "F12")]
    [InlineData(Key.Back, "Backspace")]
    [InlineData(Key.Return, "Enter")]
    [InlineData(Key.Prior, "PageUp")]
    [InlineData(Key.Next, "PageDown")]
    [InlineData(Key.Capital, "CapsLock")]
    [InlineData(Key.Left, "Left")]
    [InlineData(Key.Delete, "Delete")]
    public void TryCapture_NamesTheMainKeyTheWayTheConfigFileDoes(Key key, string expected)
    {
        Assert.True(HotkeyRecording.TryCapture(KeyModifiers.Control, key, out _, out var mainKey));
        Assert.Equal(expected, mainKey);
    }

    [Fact]
    public void EveryKeyItAccepts_CanActuallyBeInstalledAsAHotkey()
    {
        // **这一条才是本类存在的理由。** 主键名最终要过 VirtualKey.TryFromName 那一关
        // （KeyboardHook 装钩子时用它翻回虚拟键码，翻不出来就抛）。录制这头若肯收一个
        // 它不认识的名字，用户当场看不出问题，**下次启动才发现热键装不上**。
        //
        // 预言机是 VirtualKey 本身——它就是那份契约的另一端，不是被测代码的复读。
        var accepted = new List<string>();

        foreach (Key key in Enum.GetValues<Key>())
        {
            if (HotkeyRecording.TryCapture(KeyModifiers.Control, key, out _, out var mainKey))
            {
                accepted.Add(mainKey);
                Assert.True(
                    VirtualKey.TryFromName(mainKey, out _),
                    $"录制收下了 {key}，翻成 \"{mainKey}\"，而 VirtualKey 不认识它——装钩子时会抛");
            }
        }

        // 阴性对照：上面那个循环若一个键都没收下，断言全是空转。
        Assert.Contains("Space", accepted);
        Assert.Contains("F1", accepted);
        Assert.True(accepted.Count >= 40, $"只收下了 {accepted.Count} 个键，少得可疑");
    }

    /// <summary>
    /// **上一条只查了一半。** 它问「收下的都装得上吗」，问不到反方向那一半：
    /// 键盘上按得出、<see cref="VirtualKey"/> 也认的键，录制这头能不能翻出名字。
    ///
    /// <para>
    /// 翻不出来时 <c>TryCapture</c> 返回 <c>false</c>，而控件把 <c>false</c> 一律当「继续等」，
    /// 界面上零反馈 —— 用户看到的就是「按了没反应」。
    /// </para>
    ///
    /// <para>
    /// 症结在 <c>HotkeyRecording.NameOf</c> 的兜底 <c>key.ToString()</c>：Avalonia 把这些键
    /// 叫 <c>OemSemicolon</c> / <c>OemQuestion</c> / <c>OemTilde</c>…，而 <see cref="VirtualKey"/>
    /// 那张表里存的是符号本身（<c>";"</c> / <c>"/"</c> / <c>"`"</c>）。两头对不上，
    /// 于是 11 个符号键**一个都录不进来**，尽管钩子完全装得上它们。
    /// </para>
    ///
    /// <para>
    /// 预言机是 <see cref="VirtualKey"/>（契约的另一端），下面第一条断言即阳性对照：
    /// 这些名字若连 <c>VirtualKey</c> 都不认，本条测试就无从成立。
    /// </para>
    /// </summary>
    [Fact]
    public void EveryKeyTheHookCanInstall_CanAlsoBeRecorded()
    {
        string[] symbolKeys = ["`", "-", "=", "[", "]", "\\", ";", "'", ",", ".", "/"];

        foreach (var name in symbolKeys)
        {
            Assert.True(
                VirtualKey.TryFromName(name, out _),
                $"阳性对照不成立：VirtualKey 并不认 \"{name}\"，这条测试的前提已经没了");
        }

        var recordable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Key key in Enum.GetValues<Key>())
        {
            if (HotkeyRecording.TryCapture(KeyModifiers.Control, key, out _, out var mainKey))
            {
                recordable.Add(mainKey);
            }
        }

        Assert.Equal([], symbolKeys.Where(name => !recordable.Contains(name)).ToArray());
    }

    [Fact]
    public void WhatItCaptures_GoesStraightIntoAUsableHotkeyCombination()
    {
        // 端到端一小段：录制 → HotkeySection → HotkeyCombination，中间不许有人再翻一次。
        Assert.True(HotkeyRecording.TryCapture(
            KeyModifiers.Control | KeyModifiers.Shift, Key.F2, out var modifiers, out var mainKey));

        Assert.True(HotkeyCombination.TryParse(
            new HotkeySection(modifiers, mainKey, 300), out var combo, out var error), error);

        Assert.Equal("Ctrl + Shift + F2", combo.ToDisplayString());
    }
}
