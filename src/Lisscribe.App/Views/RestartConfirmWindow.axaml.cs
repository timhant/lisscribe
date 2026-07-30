using Avalonia.Controls;
using Avalonia.Interactivity;
using Lisscribe.Core.Config;

namespace Lisscribe.App.Views;

/// <summary>
/// 改了「重启才生效」的配置之后问一句「现在重启吗」。
///
/// <para>
/// <b>这不是技术意义上的模态窗，只能是 <c>Show()</c>。</b> Avalonia 的 <c>ShowDialog</c> 必须有
/// owner window，而本窗口是在配置窗的 <c>Closed</c> 回调里弹的——那一刻配置窗已经关了，
/// 组合根里的引用也已置 null；本程序又是托盘型，没有主窗口可当 owner。
/// 靠 <c>Topmost</c> 保证看得见。
/// </para>
///
/// <para>
/// 回答走 <see cref="Confirmed"/> 属性而不是 <c>Task&lt;bool&gt;</c>：调用方本就要挂
/// <c>Closed</c>（<c>Show()</c> 不阻塞），在那里读一个 bool 比等一个 Task 简单，
/// 也不会把 async 引进 UI 线程上的收尾路径。
/// </para>
/// </summary>
public sealed partial class RestartConfirmWindow : Window
{
    /// <summary>XAML 预览器要一个无参构造。文案带「预览」二字，免得日后有人以为它是真提示。</summary>
    public RestartConfirmWindow()
        : this($"（预览）{RestartPrompt.Message}")
    {
    }

    public RestartConfirmWindow(string message)
    {
        InitializeComponent();

        MessageText.Text = message;
    }

    /// <summary>
    /// 用户是不是选了「立即重启」。
    ///
    /// <para>
    /// <b>默认必为 false</b>：点 X、按 Esc、被别的东西关掉都算「稍后」。反过来的话，
    /// 用户随手关掉一个没看清的窗口，程序就自己重启了——而重启会打断他手头的一切。
    /// </para>
    /// </summary>
    public bool Confirmed { get; private set; }

    private void OnRestartClicked(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;

        Close();
    }

    private void OnLaterClicked(object? sender, RoutedEventArgs e) => Close();
}
