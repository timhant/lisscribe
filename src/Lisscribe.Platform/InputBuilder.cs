using Lisscribe.Core.Hotkey;
using Lisscribe.Platform.Native;

namespace Lisscribe.Platform;

/// <summary>
/// 字符串与修饰键释放序列到 INPUT 数组的转换。纯函数，无副作用，故可完整单测——
/// SendInput 本身无法自动化验证，但它的输入可以。
/// 类型是 internal：每个成员都返回 <see cref="INPUT"/> 或其数组，而它是 internal，成员再怎样也
/// 公开不出去（改成 public 即报 CS0050），公开的类名只会谎报有 API 可用。
/// </summary>
internal static class InputBuilder
{
    /// <summary>
    /// 把文本转成 KEYEVENTF_UNICODE 的 INPUT 序列。
    /// 逐 UTF-16 码元发送：一个 INPUT 的 wScan 只有 16 位，BMP 外字符（emoji、扩展 CJK）是
    /// 代理对，必须拆成两个 INPUT 项，否则目标应用收到半个字符。
    /// 用 Unicode 模式而非 VK 码，是因为 VK 码依赖键盘布局，发不出中文。
    /// </summary>
    public static INPUT[] BuildText(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var inputs = new INPUT[text.Length * 2];

        for (var i = 0; i < text.Length; i++)
        {
            // text[i] 已经是单个 UTF-16 码元；代理对天然占两个索引，因此这个循环自动完成了拆分。
            // 按下与抬起必须成对紧挨——写成「先全部按下再全部抬起」时目标应用收到的是一串不成对
            // 的按键。
            inputs[i * 2] = CreateUnicodeInput(text[i], keyUp: false);
            inputs[(i * 2) + 1] = CreateUnicodeInput(text[i], keyUp: true);
        }

        return inputs;
    }

    /// <summary>
    /// 为指定修饰键生成 KEYUP 序列（左右两侧都发）。
    /// </summary>
    /// <remarks>
    /// 触发条件：用户松开热键后一秒内结果到达，目标窗口 <c>GetKeyState(VK_CONTROL)</c> 仍为按下。
    /// 即使用户不做二次录音，主键与修饰键的松开也非严格同时，20–80ms 的尾差足够撞上短文本注入。
    /// <para>
    /// 后果分三层，前两层实测、第三层是推理，别混着说。
    /// 一、已实测：纯 user32 EDIT 控件在按住 Ctrl+Alt 时照样收全每一个 KEYEVENTF_UNICODE 字符，
    /// 所以在这类靶子上「文本进去了」证明不了本不变量——自动化那侧的预言机因此改用 win32k 的
    /// 异步键状态表。
    /// 二、已实测且后果很硬：剪贴板路径按住 Alt 时 Ctrl+V 一个字符也粘不进去，而注入器还会返回
    /// Ok。这一层是这条不变量当下唯一看得见的实际损失。
    /// 三、未实测，推理：自己处理加速键的应用（Chromium 壳这类）预计会把注入序列当成快捷键——
    /// 折叠代码、切分屏、跳定义。靶子是别人的代码，只能人验，见
    /// <c>tests/Lisscribe.Platform.Tests/TextInjectorManualChecklist.md</c> A 组第一项。
    /// </para>
    /// <para>
    /// 补发的 Alt KEYUP 不会激活目标应用的菜单栏（实测正对照「真按一下 Alt」3/3 激活，
    /// 孤立释放、真实时序、按住后释放三种各 3 次合计 0/9 未激活）。
    /// 发出顺序（Ctrl→Alt→Shift→Win，各先左后右）实测无害，故不为它写测试。
    /// Win 那一支不能只发 KEYUP，见 <see cref="VirtualKey.VK_NEUTRAL"/>：用户只按住 Win 时，
    /// 补发的 <c>Win↑</c> 在 shell 眼里是一次完整的 Win 按放，实测 3/3 弹出开始菜单并抢走前台，
    /// 识别文本随后逐字进它的搜索框。故 Win 在集合里时，先插一个未分配码的按放当中间键。
    /// </para>
    /// </remarks>
    public static INPUT[] BuildModifierRelease(ModifierKeys modifiers)
    {
        var inputs = new List<INPUT>();

        foreach (var modifier in (ModifierKeys[])[ModifierKeys.Ctrl, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Win])
        {
            if (!modifiers.HasFlag(modifier))
            {
                continue;
            }

            if (modifier == ModifierKeys.Win)
            {
                // 无条件插，不判「前面是不是已经发过别的 KEYUP」：别的修饰键的 KEYUP 确实自己就够
                // 当中间键，但那要靠「Win 恒排在最后」这条顺序约定长期成立。多发两个不留痕迹的
                // 事件，换掉一处「改了顺序就悄悄复发」的耦合。
                inputs.Add(CreateVirtualKeyInput(VirtualKey.VK_NEUTRAL, keyUp: false));
                inputs.Add(CreateVirtualKeyInput(VirtualKey.VK_NEUTRAL, keyUp: true));
            }

            foreach (var code in VirtualKey.GetBothSides(modifier))
            {
                inputs.Add(CreateVirtualKeyInput(code, keyUp: true));
            }
        }

        return [.. inputs];
    }

    /// <summary>
    /// 一对 Alt 的按放，用来把已经进了菜单模式的目标窗口拽出来。
    /// </summary>
    /// <remarks>
    /// 菜单开着时 Alt 就是「关掉菜单」，此外没有别的含义；Esc 在应用里五花八门（关对话框、
    /// 取消 IME 候选、退出全屏），替用户按下去是越权。只发左 Alt：两侧都发的话，退出之后那一次
    /// 多余的按放又可能把菜单重新打开。
    /// </remarks>
    public static INPUT[] BuildMenuModeExit() =>
    [
        CreateVirtualKeyInput(VirtualKey.VK_LMENU, keyUp: false),
        CreateVirtualKeyInput(VirtualKey.VK_LMENU, keyUp: true),
    ];

    /// <summary>
    /// 一对未分配码 <see cref="VirtualKey.VK_NEUTRAL"/> 的按放，当「中间键」用。
    /// </summary>
    /// <remarks>
    /// 它解的是同一个病，在两个地方犯。Windows 只在「某个修饰键按下到抬起之间一个别的键也没有」
    /// 时才把那次按放当成敲击：Win 单独按放弹开始菜单，Alt 单独按放激活目标应用的菜单栏。
    /// 第一处是补发修饰键 KEYUP 时（见 <see cref="BuildModifierRelease"/>）；第二处是热键把主键
    /// 吞掉时——被吞的键不到 win32k，于是热键配成 <c>Alt + X</c> 时系统看到的就是一次孤立的
    /// Alt 按放（实测 <c>Alt + /</c> 按住再松开产出 3 条菜单消息，而 <c>Ctrl+Alt + /</c> 为 0）。
    /// 按下与抬起必须成对：只按不抬同样能抑制，但会把一个键永久留在按下态。
    /// </remarks>
    public static INPUT[] BuildNeutralTap() =>
    [
        CreateVirtualKeyInput(VirtualKey.VK_NEUTRAL, keyUp: false),
        CreateVirtualKeyInput(VirtualKey.VK_NEUTRAL, keyUp: true),
    ];

    /// <summary>
    /// 一个 VK 形态的按键事件。wScan 与扩展位都要填，虽然只填 wVk 也能让系统状态复位。
    /// </summary>
    /// <remarks>
    /// 系统不会替 wVk 补扫描码，低层钩子看到的就是 0x0000。GetKeyState / GetAsyncKeyState 不受
    /// 影响（win32k 按 vk 索引状态表），受影响的是靠 (MakeCode, E0) 自行配对 make/break 的消费者
    /// ——Raw Input 游戏、DirectInput、转发扫描码的 RDP/VNC 客户端：它们收到的 break 与记下的
    /// make 对不上号，内部模型里修饰键就一直不释放。左右两侧同样中招。
    /// 填齐 scan + 扩展位之后，注入事件与硬件按键在钩子层与 Raw Input 层都同形。
    /// </remarks>
    public static INPUT CreateVirtualKeyInput(ushort virtualKey, bool keyUp)
    {
        // 表里没有的键（本项目只发那八个修饰键）退回 scan=0、不置扩展位。
        VirtualKey.TryGetScanCode(virtualKey, out var scanCode, out var isExtended);

        return new INPUT
        {
            type = User32.INPUT_KEYBOARD,

            union = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = scanCode,
                    // 这里绝不能带 KEYEVENTF_UNICODE：带上它 wVk 会被忽略、wScan 当成字符码，
                    // 于是「释放 Ctrl」变成上屏一个 U+0000，修饰键仍旧按着。
                    dwFlags = (keyUp ? User32.KEYEVENTF_KEYUP : 0)
                        | (isExtended ? User32.KEYEVENTF_EXTENDEDKEY : 0),
                    time = 0,
                    dwExtraInfo = User32.LisscribeInjectedTag,
                },
            },
        };
    }

    private static INPUT CreateUnicodeInput(char codeUnit, bool keyUp) => new()
    {
        type = User32.INPUT_KEYBOARD,
        union = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = codeUnit,
                dwFlags = User32.KEYEVENTF_UNICODE | (keyUp ? User32.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = User32.LisscribeInjectedTag,
            },
        },
    };
}
