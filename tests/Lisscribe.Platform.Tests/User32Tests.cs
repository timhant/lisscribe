using System.Runtime.InteropServices;
using Lisscribe.Platform.Native;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// P/Invoke 声明本身的可验证部分：结构体尺寸与返回类型是纯粹的编译期/常量事实，
/// 错了属于「只在真机上现形、且现形时无从诊断」那一类故障，能钉死就钉死。
/// </summary>
public sealed class User32Tests
{
    [Fact]
    public void Input_HasTheSizeSendInputExpects()
    {
        // SendInput 的第三参数是 cbSize；对不上号时它不报错，只返回 0 一个字也不注入。
        // x64 上 INPUT 是 40 字节（type 4 + 填充 4 + 联合体 32）。
        // 任何人往 KEYBDINPUT/MOUSEINPUT 里加字段、或把 nint 写成 int，这条立刻转红。
        //
        // 只写 x64 一个数、不给 x86 分支：x86 下 sizeof(INPUT) 确为 28 不假，但同一层的
        // Get/SetWindowLongPtrW 在 32 位 user32.dll 里根本没有导出（32 位下它们是映射到
        // *LongW 的宏），实测抛 EntryPointNotFoundException。写个 28 出来只是张空头支票。
        // Lisscribe.App.csproj 已锁死 win-x64。
        Assert.Equal(40, Marshal.SizeOf<INPUT>());
    }

    [Fact]
    public void KbdllHookStruct_HasTheSizeTheHookCallbackExpects()
    {
        // 钩子回调拿 lParam 直接 Marshal.PtrToStructure<KBDLLHOOKSTRUCT>。
        // 布局错了不会抛，只会读到错位的 vkCode——热键随机失灵，且无从复现。
        Assert.Equal(24, Marshal.SizeOf<KBDLLHOOKSTRUCT>());
    }

    [Fact]
    public void GetMessage_ForInvalidWindowHandle_ReturnsMinusOne()
    {
        // GetMessage 的原型是 BOOL，但它有三种返回：>0 收到消息、0 收到 WM_QUIT、
        // -1 出错。声明成 bool 会把 -1 封送成 true，于是消息泵那句
        // `while (GetMessage(...))` 在出错时永远转下去——线程 100% 占核且不退出。
        //
        // 用户句柄在 Win64 上是 32 位值符号扩展而来，故 0x0000_7FFF_FFFF_0001 必然不是窗口句柄，
        // 传它立即返回 -1（lastError=1400 ERROR_INVALID_WINDOW_HANDLE）。传 0 才会阻塞。
        // 不能写成 const：C# 的 nint 常量取值被限制在 int 范围内（CS0133）。
        var neverAWindow = unchecked((nint)0x0000_7FFF_FFFF_0001L);

        var result = User32.GetMessage(out _, neverAWindow, 0, 0);

        Assert.Equal(-1, result);
    }
}
