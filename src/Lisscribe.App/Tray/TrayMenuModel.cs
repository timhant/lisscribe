namespace Lisscribe.App.Tray;

/// <summary>托盘菜单上的一条命令。壳层按它决定点下去做什么。</summary>
public enum TrayCommand
{
    OpenSettings,
    TogglePause,
    OpenModelFolder,
    OpenLogFolder,
    About,
    Exit,
}

/// <summary>
/// 菜单上的一项。<see cref="Header"/> 为 <c>null</c> 即分隔符。
/// </summary>
/// <param name="Header">菜单文案；分隔符没有文案。</param>
/// <param name="Command">点下去执行什么；分隔符没有命令。</param>
public readonly record struct TrayMenuEntry(string? Header, TrayCommand? Command)
{
    public bool IsSeparator => Header is null;
}

/// <summary>托盘菜单的纯逻辑：项序、文案、提示文字。不碰 Avalonia，不碰 Win32。</summary>
/// <remarks>
/// 状态由文案自己携带，不用勾选标记：<c>NativeMenuItem.IsChecked</c> 在 Windows 上不渲染
/// （avalonia#8751），照它写出来的菜单看上去就是「点了没反应」，故「暂停热键」↔「恢复热键」是
/// 文本切换。每次状态变化都要整个重建菜单：<c>NativeMenu.Opening</c> / <c>NeedsUpdate</c> 从不
/// 触发（avalonia#8076），指望「弹出前回调里刷新」等于永远显示第一次那份；本类无状态、每次现算，
/// 就是为了让重建廉价到没有理由不做。
/// </remarks>
public static class TrayMenuModel
{
    private const string PauseHeader = "暂停热键";
    private const string ResumeHeader = "恢复热键";

    /// <summary>
    /// 按当前暂停状态现算一份菜单。「打开模型目录」常驻、不只在模型未加载时出现：让托盘随引擎状态
    /// 增减菜单项要多一条跨层接线与一个重建时机，而这一项任何时候点开都不碍事。
    /// </summary>
    public static IReadOnlyList<TrayMenuEntry> Build(bool isPaused) =>
    [
        new("打开配置", TrayCommand.OpenSettings),
        new(isPaused ? ResumeHeader : PauseHeader, TrayCommand.TogglePause),
        new(null, null),
        new("打开模型目录", TrayCommand.OpenModelFolder),
        new("打开日志目录", TrayCommand.OpenLogFolder),
        new("关于 lisscribe", TrayCommand.About),
        new(null, null),
        new("退出", TrayCommand.Exit),
    ];

    /// <summary>
    /// 托盘提示文字。托盘没有气泡通知 API，「热键现在是停的」除了菜单文案只剩鼠标悬停这一处能说；
    /// 报警一律走悬浮条，不走这里。
    /// </summary>
    public static string TooltipFor(bool isPaused) => isPaused ? "lisscribe（热键已暂停）" : "lisscribe";
}
