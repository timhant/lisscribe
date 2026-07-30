using System.Reflection;
using Avalonia.Controls;

namespace Lisscribe.App.Tests;

/// <summary>
/// 直接读 Avalonia <see cref="Screens"/> 上 <c>Changed</c> 事件的委托字段。
///
/// <para>
/// **为什么走反射而不是让被测代码自报**：要验的是「订了几次、退干净没有」，
/// 而被测代码自己记的那本账，正是它可能记错的东西。本项目已经栽过一次
/// 「用被测代码自己记的账当判据」。这里的取样口是 Avalonia 的事件字段本身，
/// 与 <c>OverlayWindow</c> 写了什么无关。
/// </para>
/// <para>
/// 反射拿不到字段就**当场抛**，绝不静默返回 0——那会让「退订了」和「测试坏了」
/// 长成一模一样。
/// </para>
/// <para>
/// **<see cref="RaiseChanged"/> 的边界说准**：它 <c>DynamicInvoke</c> 那个委托字段本身，
/// **绕过了 Avalonia 自己的 raise 入口**。故经它验到的是「注册走的是生产路径、处理器跑得起来」，
/// **不是**「Avalonia 会在显示器变化时发这条通知」——后者本机无从验证（造不出热插外屏）。
/// </para>
/// </summary>
internal static class ScreensEventProbe
{
    /// <summary>
    /// <c>Screens.Changed</c> 不是字段式事件（它有显式的 add/remove），委托存在这个私有字段里。
    /// 名字是实测出来的（Avalonia 11.3.18）：<c>_iScreenImpl</c> 与 <c>_changedHandlers</c> 两个字段。
    /// </summary>
    private const string EventName = "_changedHandlers";

    public static int SubscriberCount(Screens screens) =>
        Handler(screens)?.GetInvocationList().Length ?? 0;

    public static void RaiseChanged(Screens screens) =>
        Handler(screens)?.DynamicInvoke(screens, EventArgs.Empty);

    private static Delegate? Handler(Screens screens)
    {
        var field =
            typeof(Screens).GetField(EventName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Avalonia 的 Screens 上找不到名为 {EventName} 的事件委托字段。可选字段有：" +
                string.Join(", ", typeof(Screens)
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Select(f => f.Name + ":" + f.FieldType.Name)));

        return (Delegate?)field.GetValue(screens);
    }
}
