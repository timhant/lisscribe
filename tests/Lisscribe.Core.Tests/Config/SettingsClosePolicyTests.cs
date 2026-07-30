using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.Core.Tests.Config;

/// <summary>
/// 配置窗关掉之后到底做哪几件事——把「引擎那一路」与「其余字段那一路」合成一个决定。
///
/// <para>
/// 合流规矩住在这里而不是组合根里，理由同 <c>EngineReloadPolicy</c>：组合根刻意不含判断逻辑。
/// </para>
/// </summary>
public sealed class SettingsClosePolicyTests
{
    private const string SenseVoiceDir = "./models/sense-voice";

    private static readonly ConfigChange LogLevelChanged = new("日志级别", "Info", "Debug");

    /// <summary>
    /// <b>须重启时不做 Reload。</b>两条理由：重启本来就会按新配置把引擎重建一遍，
    /// 先加载一次是白加载（Qwen3 档 982MB / 4 秒）；而且那次加载会跟用户点「立即重启」
    /// 触发的收尾撞在一起——<c>LoadAsync</c> 不在状态机的在途任务账里，<c>ShutdownAsync</c> 等不到它。
    /// </summary>
    [Fact]
    public void WhenSomethingElseNeedsARestart_ItDoesNotReloadTheEngine()
    {
        var action = Decide(EngineReloadAction.Reload, [LogLevelChanged]);

        Assert.True(action.AskRestart);
        Assert.False(action.ReloadEngine);
    }

    [Fact]
    public void WhenOnlyTheEngineNeedsReloading_ItReloadsAndDoesNotAsk()
    {
        // 这是那条自救路径：模型缺失 → 加载失败 → 用户把权重下下来 → 保存 → 重载。
        // 此刻弹一句「须重启」是在把一条本来能就地走完的路堵上。
        var action = Decide(EngineReloadAction.Reload, []);

        Assert.True(action.ReloadEngine);
        Assert.False(action.AskRestart);
        Assert.Empty(action.Changes);
    }

    [Fact]
    public void WhenNothingChangedAtAll_ItDoesNeitherThing()
    {
        var action = Decide(EngineReloadAction.Nothing, []);

        Assert.False(action.ReloadEngine);
        Assert.False(action.AskRestart);
    }

    [Fact]
    public void WhenOnlyOtherFieldsChanged_ItAsksWithoutTouchingTheEngine()
    {
        var action = Decide(EngineReloadAction.Nothing, [LogLevelChanged]);

        Assert.True(action.AskRestart);
        Assert.False(action.ReloadEngine);
        Assert.Equal(LogLevelChanged, Assert.Single(action.Changes));
    }

    // ---- 引擎那一路也要进 Changes，否则日志里说不清 --------------------------

    /// <summary>
    /// 弹窗那句话是笼统的（「配置已修改，重启之后才会全部生效」），**分辨力全靠日志**。
    /// 引擎换档若不进这张单子，日志里就只剩下「配置已修改」——而换档恰是这里面
    /// 影响最大的一项（换的是整个识别质量与录音上限）。
    /// </summary>
    [Fact]
    public void ChangingTheEngine_ShowsUpInTheChangeList_NamingBothEngines()
    {
        var action = SettingsClosePolicy.Decide(
            EngineReloadAction.RestartRequired,
            AsrEngines.SenseVoice, SenseVoiceDir,
            AsrEngines.Qwen3Asr, "./models/qwen3-asr",
            []);

        var change = Assert.Single(action.Changes);

        Assert.Equal(AsrEngines.SenseVoice, change.Before);
        Assert.Equal(AsrEngines.Qwen3Asr, change.After);
        Assert.True(action.AskRestart);
    }

    /// <summary>
    /// 换目录与换档必须是**两条不同的记录**。原先这个分辨力在弹窗文案上（用户改的是引擎却
    /// 只看到「模型目录已改」，会回头去把目录再改一遍）；文案统一之后它整个搬到了日志上，
    /// 这里就是它现在唯一的落脚处。
    /// </summary>
    [Fact]
    public void ChangingOnlyTheModelDirectory_ShowsUpAsADifferentKindOfChange()
    {
        var engineChanged = SettingsClosePolicy.Decide(
            EngineReloadAction.RestartRequired,
            AsrEngines.SenseVoice, SenseVoiceDir,
            AsrEngines.Qwen3Asr, "./models/qwen3-asr",
            []);

        var dirChanged = SettingsClosePolicy.Decide(
            EngineReloadAction.RestartRequired,
            AsrEngines.SenseVoice, SenseVoiceDir,
            AsrEngines.SenseVoice, "D:/models/elsewhere",
            []);

        var dir = Assert.Single(dirChanged.Changes);

        Assert.Equal(SenseVoiceDir, dir.Before);
        Assert.Equal("D:/models/elsewhere", dir.After);

        // 两条真的不一样。少了这一句，实现里两个分支返回同一条记录也照样绿。
        Assert.NotEqual(engineChanged.Changes[0], dir);
    }

    [Fact]
    public void TheEngineChange_ComesAlongsideTheOtherChanges_NotInsteadOfThem()
    {
        // 一次既换档又调日志级别是完全可能的。只留一条的话，日志里会缺掉另一条，
        // 而用户重启后发现「怎么日志级别也变了」无从对证。
        var action = SettingsClosePolicy.Decide(
            EngineReloadAction.RestartRequired,
            AsrEngines.SenseVoice, SenseVoiceDir,
            AsrEngines.Qwen3Asr, "./models/qwen3-asr",
            [LogLevelChanged]);

        Assert.Equal(2, action.Changes.Count);
        Assert.Contains(LogLevelChanged, action.Changes);
    }

    /// <summary>引擎没动时不许凭空造一条引擎改动出来。</summary>
    [Fact]
    public void WhenTheEngineNeedsNothing_NoEngineChangeIsInvented()
    {
        var action = Decide(EngineReloadAction.Nothing, [LogLevelChanged]);

        Assert.Equal(LogLevelChanged, Assert.Single(action.Changes));
    }

    private static SettingsCloseAction Decide(
        EngineReloadAction engineAction, IReadOnlyList<ConfigChange> otherChanges) =>
        SettingsClosePolicy.Decide(
            engineAction,
            AsrEngines.SenseVoice, SenseVoiceDir,
            AsrEngines.SenseVoice, SenseVoiceDir,
            otherChanges);
}
