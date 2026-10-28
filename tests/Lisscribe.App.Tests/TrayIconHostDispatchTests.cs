using System.IO;
using Lisscribe.App.Tray;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 托盘命令的分派：点哪一项、调到谁身上。
///
/// <para>
/// 与 <see cref="TrayIconHostTests"/> 分作两组，是因为这一层**够不着 Avalonia**——
/// <c>Invoke</c> 除 <c>TogglePause</c>（要重建原生菜单）之外只是转调传进来的委托。
/// 放在这里跑，就不必为「点了会不会调错人」去抢一次真桌面。<c>TogglePause</c>
/// 仍留在那组里。
/// </para>
/// </summary>
public sealed class TrayIconHostDispatchTests
{
    [Theory]
    [InlineData(TrayCommand.OpenSettings, "settings")]
    [InlineData(TrayCommand.TranscribeFile, "transcribe")]
    [InlineData(TrayCommand.About, "settings")]
    [InlineData(TrayCommand.OpenModelFolder, "model")]
    [InlineData(TrayCommand.OpenLogFolder, "log")]
    [InlineData(TrayCommand.Exit, "exit")]
    public void Invoke_RoutesEachCommandToItsOwnAction(TrayCommand command, string expected)
    {
        // 「关于」当前也开配置窗——单开一个窗不值当。
        // 这条把那个现状钉住：哪天它改成别的去处，这里会红着提醒改文案。
        var calls = new List<string>();

        NewHost(calls).Invoke(command);

        // 断言整份调用记录而非「含某一项」：调对了人却顺手多调一个，同样是缺陷。
        Assert.Equal([expected], calls);
    }

    [Fact]
    public void Invoke_WhenOpeningTheModelFolderThrows_DoesNotTakeTheProcessDown()
    {
        // 模型目录不存在是这一项**最可能**的处境——它正是给「模型没放对地方」的人准备的。
        // 点一下菜单就把程序带走，是本项目在另外三处回调上各栽过一次的形状。
        var host = new TrayIconHost(
            pause: () => { },
            resume: () => { },
            openSettings: () => { },
            toggleTranscription: () => { },
            openModelFolder: () => throw new DirectoryNotFoundException("模型目录不在"),
            openLogFolder: () => { },
            requestExit: () => { },
            NullLogger<TrayIconHost>.Instance);

        var escaped = Record.Exception(() => host.Invoke(TrayCommand.OpenModelFolder));

        Assert.Null(escaped);
    }

    private static TrayIconHost NewHost(List<string> calls) =>
        new(
            pause: () => calls.Add("pause"),
            resume: () => calls.Add("resume"),
            openSettings: () => calls.Add("settings"),
            toggleTranscription: () => calls.Add("transcribe"),
            openModelFolder: () => calls.Add("model"),
            openLogFolder: () => calls.Add("log"),
            requestExit: () => calls.Add("exit"),
            NullLogger<TrayIconHost>.Instance);
}
