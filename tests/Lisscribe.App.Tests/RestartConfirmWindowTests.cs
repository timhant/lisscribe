using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Lisscribe.App.Views;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 「现在重启吗」那个窗口的真控件行为。
///
/// <para>
/// 这窗口只有一件事要做对：**把用户的选择如实报出来**。报反了的后果不对称——报成「要重启」
/// 会打断用户手头的一切，报成「稍后」只是没生效。故默认那一侧必须是「稍后」。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class RestartConfirmWindowTests
{
    private const string Message = "识别引擎已从 SenseVoice 改为 Qwen3Asr，重启 lisscribe 之后才会生效。";

    [Fact]
    public void TheWindow_ShowsTheMessageItWasGiven()
    {
        WithWindow(window =>
        {
            var text = window.FindControl<TextBlock>("MessageText");

            Assert.NotNull(text);
            Assert.Equal(Message, text!.Text);
        });
    }

    [Fact]
    public void ClickingRestartNow_ReportsConfirmed_AndClosesTheWindow()
    {
        WithWindow(window =>
        {
            // 阳性对照：不先确认初值是 false，下面那条断言分不清「按钮起了作用」
            // 和「这个属性本来就是 true」。
            Assert.False(window.Confirmed);

            Click(window, "RestartButton");

            Assert.True(window.Confirmed);
            Assert.False(window.IsVisible);
        });
    }

    /// <summary>
    /// 「稍后重启」得真把窗口关掉。**只断言 <c>Confirmed</c> 仍为 false 是条空断言**——
    /// 它初值就是 false，把整个 Click 处理器删掉也照样绿。关窗才是这个按钮做的事。
    /// </summary>
    [Fact]
    public void ClickingLater_ReportsNotConfirmed_AndClosesTheWindow()
    {
        WithWindow(window =>
        {
            Click(window, "LaterButton");

            Assert.False(window.Confirmed);
            Assert.False(window.IsVisible);
        });
    }

    /// <summary>
    /// <b>直接关掉窗口（点 X、按 Esc）必须等于「稍后」。</b>
    /// 用户随手关掉一个没看清的窗口，程序不该就此自己重启。
    /// </summary>
    [Fact]
    public void ClosingTheWindowOutright_CountsAsLater()
    {
        WithWindow(window =>
        {
            window.Close();

            Assert.False(window.Confirmed);
        });
    }

    private static void Click(Window window, string buttonName)
    {
        var button = window.FindControl<Button>(buttonName);

        Assert.NotNull(button);

        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void WithWindow(Action<RestartConfirmWindow> assert) => AvaloniaTestHost.Run(() =>
    {
        // ShowActivated=false：这一组会与别的桌面测试排在同一条 UI 线程上，
        // 抢前台只会让它们互相看到对方的窗口。
        var window = new RestartConfirmWindow(Message)
        {
            ShowActivated = false,
            Topmost = false,
        };

        try
        {
            window.Show();

            // 绑定与模板是排队生效的，不抽干队列就会量到「还没长出控件」的中间态。
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

            assert(window);
        }
        finally
        {
            window.Close();
        }
    });
}
