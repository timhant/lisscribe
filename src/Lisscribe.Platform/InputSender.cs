using System.Runtime.InteropServices;
using Lisscribe.Core.Hotkey;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Platform;

/// <summary>
/// 两个注入器共用的两件事：读物理修饰键状态、把 INPUT 数组真的发出去。
/// </summary>
/// <remarks>
/// 共用而不是各写一遍：「注入前释放所有物理按下的修饰键」是
/// <see cref="Core.Abstractions.ITextInjector"/> 的接口层要求，只在 SendInput 那侧做而剪贴板那侧
/// 直发 Ctrl+V 的后果是——用户按住 Ctrl+Alt 说话，松开走剪贴板路径时发出去的是 Ctrl+Alt+V，
/// 按住 Alt 时它一个字符也粘不进去，而注入器还返回 Ok：文本没上屏，用户也没有任何提示。
/// </remarks>
internal static class InputSender
{
    /// <summary>
    /// 释放所有当前物理按下的修饰键。没有按住的就什么也不发。
    /// </summary>
    /// <remarks>
    /// 用 <c>GetAsyncKeyState</c> 而非 <see cref="ModifierKeyTracker"/>：被钩子吞掉的键固然不更新
    /// win32k 的异步键状态表，但本项目的钩子只吞主键、放行修饰键（否则 Ctrl 的 KEYUP 被吞就会跨
    /// 进程永久卡住），故修饰键那张表始终是准的；而这里不在钩子回调内，物理真实状态比钩子自己记的
    /// 账更接近「目标窗口此刻看到的修饰键状态」——后者才是要清掉的东西。
    /// <para>
    /// Win 那一支不是简单发一个 KEYUP：用户只按住 Win 时，补发的 <c>Win↑</c> 在 shell 眼里就是一次
    /// 完整的 Win 按放，弹出开始菜单并抢走前台，随后识别文本逐字进它的搜索框。处理在
    /// <see cref="InputBuilder.BuildModifierRelease"/>——Win 之前先插一个未分配码的按放当中间键，
    /// 见 <see cref="VirtualKey.VK_NEUTRAL"/>。
    /// </para>
    /// </remarks>
    public static bool TryReleasePhysicalModifiers(ILogger logger)
    {
        // 拿到手的 ILogger 一律过一道 Safe()：本类静态、每次现收 logger，不能指望调用方都包过。
        // Safe() 幂等，零成本。
        var safe = logger.Safe();

        var held = ReadPhysicalModifiers();

        if (held == ModifierKeys.None)
        {
            return true;
        }

        // **Information 而不是 Debug。** 这一步一旦没做成，症状是「注入返回 Ok、目标窗口里一个字
        // 也没有」——按住 Alt 时 Ctrl+V 一个字符都粘不进去，而注入器无从察觉。默认日志级别是 Info，
        // 记在 Debug 上等于现场发生时什么也留不下。
        safe.LogInformation("注入前释放修饰键 {Modifiers}", held);

        if (!TrySend(InputBuilder.BuildModifierRelease(held), safe, "释放修饰键"))
        {
            return false;
        }

        // 发出去不等于生效。**复核一次**：这两件事之间隔着整个输入栈，而没生效时下游的
        // Ctrl+V 会变成 Ctrl+Alt+V（一个字符也粘不进去）、SendInput 的字符会被当成快捷键，
        // 两种都返回 Ok，除了这一行没有任何痕迹。
        var stillHeld = ReadPhysicalModifiers();

        if (stillHeld != ModifierKeys.None)
        {
            safe.LogWarning("释放修饰键之后 {Modifiers} 仍处于按下态，本次注入很可能被目标应用当成快捷键", stillHeld);
        }

        return true;
    }

    public static bool TrySend(INPUT[] inputs, ILogger logger, string what)
    {
        if (inputs.Length == 0)
        {
            return true;
        }

        var sent = User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        if (sent == inputs.Length)
        {
            return true;
        }

        // UIPI 导致的失败不会走到这里：那种情况 SendInput 返回成功、lastError 为 0，而一个字符也进
        // 不去，只能靠事前的完整性级别比对拦。这条日志出现时是句柄耗尽、BlockInput 之类真正的失败。
        logger.Safe().LogError(
            "{What} 时 SendInput 只发出 {Sent}/{Total} 项，Win32 错误码 {Error}",
            what, sent, inputs.Length, Marshal.GetLastWin32Error());

        return false;
    }

    private static ModifierKeys ReadPhysicalModifiers()
    {
        var state = ModifierKeys.None;

        foreach (var modifier in (ReadOnlySpan<ModifierKeys>)[ModifierKeys.Ctrl, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Win])
        {
            foreach (var code in VirtualKey.GetBothSides(modifier))
            {
                if ((User32.GetAsyncKeyState(code) & 0x8000) != 0)
                {
                    state |= modifier;

                    break;
                }
            }
        }

        return state;
    }
}
