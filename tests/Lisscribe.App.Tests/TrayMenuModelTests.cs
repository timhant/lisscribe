using Lisscribe.App.Tray;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 托盘菜单的**纯逻辑**：项序、文案、提示文字，全部与 Avalonia 无关。
///
/// <para>
/// 单分一层，是为了把「容易错且验得起来的部分」从「必须有真桌面才跑得动的壳」里剥出来。
/// </para>
/// </summary>
public sealed class TrayMenuModelTests
{
    [Fact]
    public void Build_WhenRunning_ListsTheSevenCommandsInTheOrderTheSpecGives()
    {
        // 项序写死：打开配置、转写音频文件、暂停/恢复热键、模型目录、日志目录、关于、退出。
        // 转写那一项紧跟「打开配置」，见设计 §4.5。
        var entries = TrayMenuModel.Build(isPaused: false, isTranscribing: false);

        Assert.Equal(
            [
                TrayCommand.OpenSettings,
                TrayCommand.TranscribeFile,
                TrayCommand.TogglePause,
                TrayCommand.OpenModelFolder,
                TrayCommand.OpenLogFolder,
                TrayCommand.About,
                TrayCommand.Exit,
            ],
            entries.Where(e => !e.IsSeparator).Select(e => e.Command).ToArray());
    }

    [Fact]
    public void Build_PutsSeparatorsAroundTheDiagnosticGroup()
    {
        var entries = TrayMenuModel.Build(isPaused: false, isTranscribing: false);

        // 位置写死：分隔符挪了位置就是菜单版式变了，那属于要人重新看一眼的改动。
        Assert.Equal([3, 7], entries.Index().Where(p => p.Item.IsSeparator).Select(p => p.Index).ToArray());
    }

    [Fact]
    public void Build_NamesTheModelFolderItem_SoTheHeaderItselfSaysWhereModelsGo()
    {
        // 模型未加载时用户要靠这条菜单找到该往哪儿放模型，故文案本身得说出来——
        // 写成「打开目录」也能点开同一个地方，但用户看不出那是干什么用的。
        var entries = TrayMenuModel.Build(isPaused: false, isTranscribing: false);

        Assert.Contains("模型", entries.Single(e => e.Command == TrayCommand.OpenModelFolder).Header);

        // 阴性对照：全菜单只此一处提「模型」。否则版式一改，上一句会挑中别人照样绿。
        Assert.Single(entries, e => e.Header?.Contains("模型") == true);
    }

    [Fact]
    public void Build_WhenPaused_ChangesTheTextOfExactlyOneItem()
    {
        // **这一条钉的是 Avalonia #8751 的绕行**：IsChecked 在 Windows 上不渲染，
        // 故状态必须由文案自己携带。断言写成「恰好一项变了」而不是「暂停项变了」——
        // 后者对「顺手把别的文案也改成带状态的」毫无抵抗力，而那正是绕行失效的方式。
        var running = TrayMenuModel.Build(isPaused: false, isTranscribing: false);
        var paused = TrayMenuModel.Build(isPaused: true, isTranscribing: false);

        var differing = running.Zip(paused)
            .Where(pair => pair.First.Header != pair.Second.Header)
            .ToArray();

        Assert.Single(differing);
        Assert.Equal(TrayCommand.TogglePause, differing[0].First.Command);
        Assert.Equal("暂停热键", differing[0].First.Header);
        Assert.Equal("恢复热键", differing[0].Second.Header);
    }

    [Fact]
    public void Build_WhileTranscribing_ChangesTheTextOfExactlyOneItem()
    {
        // 与暂停那条同理（avalonia#8751）：转写中的状态由那一项的文案自己携带，别的项一字不动。
        // 命令不变，点下去由组合根按当时是否在转写决定是开始还是取消。
        var idle = TrayMenuModel.Build(isPaused: false, isTranscribing: false);
        var transcribing = TrayMenuModel.Build(isPaused: false, isTranscribing: true);

        var differing = idle.Zip(transcribing)
            .Where(pair => pair.First.Header != pair.Second.Header)
            .ToArray();

        Assert.Single(differing);
        Assert.Equal(TrayCommand.TranscribeFile, differing[0].First.Command);
        Assert.Equal(TrayCommand.TranscribeFile, differing[0].Second.Command);
        Assert.Equal("转写音频文件…", differing[0].First.Header);
        Assert.Equal("取消转写", differing[0].Second.Header);
    }

    [Fact]
    public void TooltipFor_SaysSoWhenTheHotkeyIsPaused()
    {
        // Avalonia 的托盘没有气泡通知 API，提示文字是这里唯一能说话的地方。
        Assert.Equal("lisscribe", TrayMenuModel.TooltipFor(isPaused: false));
        Assert.Contains("暂停", TrayMenuModel.TooltipFor(isPaused: true));

        // 阴性对照：别让「暂停」二字在未暂停那条里也出现，否则上一句是空断言。
        Assert.DoesNotContain("暂停", TrayMenuModel.TooltipFor(isPaused: false));
    }

    [Fact]
    public void EverySeparator_CarriesNoCommand_AndEveryCommandItemCarriesNoNullHeader()
    {
        // 结构自洽：壳层按 IsSeparator 分派，两者混一起会让菜单少一项或多一条死项。
        foreach (var entry in TrayMenuModel.Build(isPaused: true, isTranscribing: true))
        {
            if (entry.IsSeparator)
            {
                Assert.Null(entry.Command);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Header));
                Assert.NotNull(entry.Command);
            }
        }
    }
}
