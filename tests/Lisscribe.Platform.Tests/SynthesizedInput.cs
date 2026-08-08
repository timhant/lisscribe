using System.Runtime.InteropServices;
using Lisscribe.Platform.Native;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 用 SendInput 合成按键。合成的按键走完整条 WH_KEYBOARD_LL 链，钩子分不出它与真实按键
/// （除非在 dwExtraInfo 上做记号，而那正是要验的一条行为）。
///
/// **选键须避开会产生可见输入的键。** 被吞掉的组合不会外泄，但任何被放行的键都会真的敲进
/// 当前前台窗口。F13–F24 在标准键盘上不存在，绝大多数应用也不绑定，故拿它们当测试键。
///
/// <para>修饰键的取舍，三条都是实测：</para>
/// <list type="bullet">
/// <item><b>Ctrl</b>：随便用，单独按下再松开无副作用。</item>
/// <item><b>Alt</b>：可以用，<b>但只许夹在别的按键中间</b>——真按一放 Alt 会激活菜单栏，
/// 而 <c>Alt↓ → Ctrl+V → Alt↑</c> 之后前台仍是自己的窗口、修饰键状态全部归零。
/// 之所以非用它不可：验「粘贴前有没有释放修饰键」需要一个<b>真能挡住 Ctrl+V 的</b>修饰键，
/// 而按住 Shift 时 Ctrl+V 照常粘贴，按住 Alt 时一个字符也不粘。</item>
/// <item><b>Win</b>：<b>绝对不许合成</b>。<c>Win↓ → Ctrl+V → Win↑</c> 会拉起剪贴板历史
/// （ShellExperienceHost 抢走前台且不自行退去），此后本进程每一次抢前台都失败，整套注入型测试
/// 连环转红，而失败讯息只说「系统拒绝了前台抢占」。</item>
/// </list>
/// </summary>
internal static class SynthesizedInput
{
    // 不存在于标准键盘的功能键。VK_F13 = VK_F1 + 12。
    public const ushort VK_F13 = 0x7C;
    public const ushort VK_F14 = 0x7D;
    public const ushort VK_F15 = 0x7E;

    public const ushort VK_V = 0x56;

    /// <summary>
    /// 一次 SendInput 发一批。同一次调用内的多个事件不会被别的线程的输入插进来，
    /// 故「先 Ctrl 按下再主键按下」的次序是有保证的——钩子据此更新修饰键状态表。
    /// </summary>
    public static void Send(nint extraInfo, params (ushort Vk, bool KeyUp)[] keys)
    {
        var inputs = new INPUT[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = new INPUT
            {
                type = User32.INPUT_KEYBOARD,
                union = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = keys[i].Vk,
                        wScan = 0,
                        dwFlags = keys[i].KeyUp ? User32.KEYEVENTF_KEYUP : 0,
                        time = 0,

                        // 生产代码里这一栏恒为 LisscribeInjectedTag（见 InputBuilder），
                        // 而模拟真实按键恰恰需要它是 0，故本类不复用 InputBuilder。
                        dwExtraInfo = extraInfo,
                    },
                },
            };
        }

        var sent = User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput 只发出 {sent}/{inputs.Length} 个事件，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// 逐 UTF-16 码元发一串 KEYEVENTF_UNICODE 事件。
    ///
    /// **刻意不复用 <c>InputBuilder.BuildText</c>**：用它的地方是「不释放修饰键直接发文本」
    /// 这个负对照，而负对照若和被测方共用构造代码，被测方那边写错时两边一起错，对照就失效了。
    /// 这几行是照 MSDN 直接写的，与生产代码无共用环节。
    /// </summary>
    public static void SendUnicode(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = UnicodeInput(text[i], keyUp: false);
            inputs[(i * 2) + 1] = UnicodeInput(text[i], keyUp: true);
        }

        var sent = User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput 只发出 {sent}/{inputs.Length} 个事件，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>左 Alt 的扫描码。硬件键盘发出来的 Alt 带着它，而 <see cref="Send"/> 合成的带的是 0。</summary>
    public const ushort ScanCodeLeftAlt = 0x38;

    /// <summary>
    /// 发一批**带真实扫描码**的按键事件。
    /// </summary>
    /// <remarks>
    /// <see cref="Send"/> 一律填 <c>wScan = 0</c>，而真键盘发出来的每一个事件都带扫描码。
    /// 这个差别不是细枝末节：Windows 判「某个修饰键按下到抬起之间有没有夹别的键」时若参考扫描码，
    /// 那么用 <see cref="Send"/> 合成的 Alt 与真手按下的 Alt 走的就不是同一条判定，
    /// 而整套「按 Alt 不弹菜单」的用例会变成空转。故对照必须能把扫描码那一栏填真。
    /// </remarks>
    public static void SendPhysical(params (ushort Vk, ushort Scan, bool KeyUp)[] keys)
    {
        var inputs = new INPUT[keys.Length];

        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = new INPUT
            {
                type = User32.INPUT_KEYBOARD,
                union = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = keys[i].Vk,
                        wScan = keys[i].Scan,
                        dwFlags = keys[i].KeyUp ? User32.KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = 0,
                    },
                },
            };
        }

        var sent = User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput 只发出 {sent}/{inputs.Length} 个事件，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>不带任何修饰键释放的裸 Ctrl+V，供剪贴板路径的负对照使用。</summary>
    public static void SendRawCtrlV() => Send(
        0,
        (VirtualKey.VK_LCONTROL, false),
        (VK_V, false),
        (VK_V, true),
        (VirtualKey.VK_LCONTROL, true));

    /// <summary>
    /// 把测试用到的键统一按一遍「松开」。
    ///
    /// 为什么必须做：低层钩子吞掉 KEYUP 时 win32k 不更新异步键状态表，该键就此
    /// <b>卡在系统级的「按住」态，且跨进程存活</b>——下一个进程读到的仍是 0x8000，
    /// 直到补发一次被放行的 KEYUP 才复位。测试跑完把用户的键盘留成那样是不可接受的。
    /// </summary>
    public static void ReleaseEverythingUsedByTests()
    {
        // **中间键排在最前面，而且必须成对。** 后面那一串里有 Win：孤立的 `Win↑` 若前面没有
        // 别的按键，shell 会当成一次完整的 Win 按放并弹出开始菜单。先按放一次未分配码
        // （见 VirtualKey.VK_NEUTRAL）把这条堵死，与生产的 BuildModifierRelease 同一手法。
        Send(0, (VirtualKey.VK_NEUTRAL, false), (VirtualKey.VK_NEUTRAL, true));

        foreach (var vk in (ushort[])[
            VK_F13, VK_F14, VK_F15,
            VirtualKey.VK_LCONTROL, VirtualKey.VK_RCONTROL,
            VirtualKey.VK_LSHIFT, VirtualKey.VK_RSHIFT,
            VirtualKey.VK_LMENU, VirtualKey.VK_RMENU,
            // 生产会发这两个（用户可以把 Win 配成热键修饰键）：链上某个钩子只吃掉那一次 KEYUP 时，
            // 那个键就跨进程卡在按下态，而没人来收。
            VirtualKey.VK_LWIN, VirtualKey.VK_RWIN,
            // 同理：中间键也是 SendInput 发出去的真按键。
            VirtualKey.VK_NEUTRAL])
        {
            Send(0, (vk, true));
        }
    }

    /// <summary>某个虚拟键此刻在 win32k 的异步键状态表里是不是按住的。</summary>
    public static bool IsHeld(ushort virtualKey) =>
        (User32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static INPUT UnicodeInput(char codeUnit, bool keyUp) => new()
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
                dwExtraInfo = 0,
            },
        },
    };
}
