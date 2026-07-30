using Lisscribe.Core.Asr;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 换引擎时，模型目录跟不跟着换。
///
/// <para>
/// 两档模型在两个目录，而 <c>asr.modelDir</c> 只有一个。若切档时目录一动不动，
/// 用户切完必然撞一次「缺少模型文件」——而他什么也没做错。
/// </para>
///
/// <para>
/// <b>只在用户没自定义过时才跟随。</b>他要是把目录指到了 D:\my-models\sv，那是他自己安排的，
/// 程序无从知道 Qwen3 的权重该在哪；这时候擅自改写，等于把用户填的东西悄悄抹掉。
/// 那种情况就让「模型缺失」提示去引导他改，提示里有目录全路径。
/// </para>
/// </summary>
public sealed class ModelDirFollowPolicyTests
{
    [Fact]
    public void SwitchingEngines_MovesAnUntouchedDirectory_ToTheNewEnginesDefault()
    {
        Assert.Equal(
            "./models/qwen3-asr",
            ModelDirFollowPolicy.Follow(
                AsrEngines.SenseVoice, AsrEngines.Qwen3Asr, "./models/sense-voice"));

        // 反向同样成立，否则切回去的人得自己记着原来那个路径。
        Assert.Equal(
            "./models/sense-voice",
            ModelDirFollowPolicy.Follow(
                AsrEngines.Qwen3Asr, AsrEngines.SenseVoice, "./models/qwen3-asr"));
    }

    [Fact]
    public void TheDefaultIsRecognised_HoweverItWasWritten()
    {
        // "./models/sense-voice" 与 "models\sense-voice" 是同一处。按原样比字符串的话，
        // 用户只要少打两个字符，跟随就失效——而失效的样子和「他自定义过」一模一样，查无可查。
        Assert.Equal(
            "./models/qwen3-asr",
            ModelDirFollowPolicy.Follow(
                AsrEngines.SenseVoice, AsrEngines.Qwen3Asr, "models\\sense-voice"));
    }

    [Fact]
    public void SwitchingEngines_LeavesACustomDirectoryAlone()
    {
        Assert.Equal(
            "D:/my-models/sv",
            ModelDirFollowPolicy.Follow(
                AsrEngines.SenseVoice, AsrEngines.Qwen3Asr, "D:/my-models/sv"));
    }

    [Fact]
    public void NotSwitchingEngines_NeverTouchesTheDirectory()
    {
        // 连「目录恰好等于另一档的默认值」也不动：没换引擎就没有换目录的理由，
        // 而那种目录多半正是用户有意指过去的。
        Assert.Equal(
            "./models/qwen3-asr",
            ModelDirFollowPolicy.Follow(
                AsrEngines.SenseVoice, AsrEngines.SenseVoice, "./models/qwen3-asr"));
    }

    [Fact]
    public void AnUnknownEngineOnEitherSide_LeavesTheDirectoryAlone()
    {
        // 引擎名非法这件事归 ConfigValidator 报，这里别抢话，更别拿一个猜出来的目录去盖掉用户的值。
        Assert.Equal(
            "./models/sense-voice",
            ModelDirFollowPolicy.Follow("Whisper", AsrEngines.Qwen3Asr, "./models/sense-voice"));

        Assert.Equal(
            "./models/sense-voice",
            ModelDirFollowPolicy.Follow(AsrEngines.SenseVoice, "Whisper", "./models/sense-voice"));
    }
}
