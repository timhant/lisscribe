using Avalonia.Input;
using Lisscribe.Platform.Native;

namespace Lisscribe.App.Views;

/// <summary>把一次 Avalonia 按键事件翻成配置文件里的热键写法。纯函数，不碰界面。</summary>
/// <remarks>
/// 失败方向是拒收：主键名最终要过 <see cref="VirtualKey.TryFromName"/> 那一关（钩子安装时用它翻
/// 虚拟键码，翻不出来就抛），故这里拿它当验收关——否则用户按下确定、关窗、一切正常，下次启动才
/// 发现热键装不上，而错误指向的是钩子不是那次录制。用 Avalonia 按键事件而非低层钩子，代价是录不
/// 到会被系统吃掉的组合（Win+L 之类），那类组合本来也当不了热键。
/// </remarks>
public static class HotkeyRecording
{
    /// <summary>
    /// 试着把这一次按键记成一组热键。返回 <c>false</c> 表示「这一下还不算数」——
    /// 只按了修饰键，或者主键是本项目认不出的键。
    /// </summary>
    public static bool TryCapture(
        KeyModifiers modifiers, Key key, out string[] recordedModifiers, out string mainKey)
    {
        recordedModifiers = [];
        mainKey = string.Empty;

        if (IsModifierKey(key))
        {
            // 用户还在按修饰键，主键没落下。这不是错误，是「等下一下」。
            return false;
        }

        var name = NameOf(key);

        if (name is null || !VirtualKey.TryFromName(name, out _))
        {
            return false;
        }

        var list = new List<string>(4);

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            list.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            list.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            list.Add("Shift");
        }

        // Avalonia 把 Win 键叫 Meta。配置文件里叫 Win，与 Windows 惯例和
        // ConfigValidator 的规范顺序一致。
        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            list.Add("Win");
        }

        recordedModifiers = [.. list];
        mainKey = name;

        // 修饰键为空也照样返回 true：够不够格当热键是 HotkeyCombination 的事，它给的拒绝理由更准。
        // 这一层只回答「这一下按的是什么」。
        return true;
    }

    /// <summary>
    /// 这一下按的是不是纯修饰键。公开是为了让控件分得清两种 <c>false</c>：「还在按修饰键，等下一下」
    /// 该沉默，「这个键当不了热键」必须出声，否则用户看到的一律是「按了没反应」。
    /// </summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System;

    /// <summary>
    /// Avalonia 的 <see cref="Key"/> → 本项目的主键名。认不出来的返回 <c>null</c>。
    /// </summary>
    private static string? NameOf(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),

        // 名字对不上的几个，逐个点名。剩下的一律拿 Key 的名字去问 VirtualKey，
        // 对不上就是不支持——这样新增支持只需动 VirtualKey 那张表。
        Key.Back => "Backspace",
        Key.Return => "Enter",
        Key.Escape => "Escape",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.Capital => "CapsLock",

        // 符号键：Avalonia 叫 OemSemicolon / OemQuestion / OemTilde…，而 VirtualKey 那张表里存的是
        // 符号本身。少了这一段，这 11 个键一个都录不进来，且 TryCapture 只返回 false，界面上连一句
        // 提示都没有——正是「按了没反应」那条故障。
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",

        _ => key.ToString(),
    };
}
