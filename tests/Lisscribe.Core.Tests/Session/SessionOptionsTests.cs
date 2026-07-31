using Lisscribe.Core.Asr;
using Lisscribe.Core.Session;
using Xunit;

namespace Lisscribe.Core.Tests.Session;

public sealed class SessionOptionsTests
{
    [Fact]
    public void CreateFor_GivesEachEngineItsOwnRecordingCeiling()
    {
        Assert.Equal(60, SessionOptions.CreateFor(AsrEngines.SenseVoice).MaxRecordingSeconds);
        Assert.Equal(25, SessionOptions.CreateFor(AsrEngines.Qwen3Asr).MaxRecordingSeconds);
    }

    /// <summary>
    /// 中文数字转换只补 Qwen3 档。SenseVoice 那档由 sherpa-onnx 自带的 ITN 负责，再叠一层
    /// 只会互相打架。这个判断留在这里而不是组合根里，与录音上限同理。
    /// </summary>
    [Fact]
    public void CreateFor_TurnsChineseNumberItnOnForQwen3Only()
    {
        Assert.False(SessionOptions.CreateFor(AsrEngines.SenseVoice).ApplyChineseNumberItn);
        Assert.True(SessionOptions.CreateFor(AsrEngines.Qwen3Asr).ApplyChineseNumberItn);
    }

    /// <summary>
    /// 档位与用户开关取合取。四种组合各来一条——只测「用户关掉就不转」的话，实现里
    /// 直接采信配置值也能绿，而那样默认档会在模型自带的 ITN 上再叠一层。
    /// </summary>
    [Theory]
    [InlineData(AsrEngines.Qwen3Asr, true, true)]
    [InlineData(AsrEngines.Qwen3Asr, false, false)]
    [InlineData(AsrEngines.SenseVoice, true, false)]
    [InlineData(AsrEngines.SenseVoice, false, false)]
    public void CreateFor_TakesConjunctionOfEngineAndUserSwitch(
        string engine, bool userEnabled, bool expected)
    {
        Assert.Equal(expected, SessionOptions.CreateFor(engine, userEnabled).ApplyChineseNumberItn);
    }

    /// <summary>
    /// **只有录音上限与中文数字转换随档位变，别的一个都不许动。**
    ///
    /// <para>
    /// 拿「整条记录相等」当判据，而不是逐个字段点名：日后 <see cref="SessionOptions"/> 新增字段时，
    /// 逐个点名的写法查不到新字段，而这一条会立刻翻红。
    /// </para>
    /// </summary>
    [Fact]
    public void CreateFor_ChangesNothingElse()
    {
        var qwen3 = SessionOptions.CreateFor(AsrEngines.Qwen3Asr);

        Assert.Equal(
            SessionOptions.CreateDefault() with { MaxRecordingSeconds = 25, ApplyChineseNumberItn = true },
            qwen3);
    }

    [Fact]
    public void CreateDefault_IsStillTheSenseVoiceTier()
    {
        // 默认档没变，这条守的是「加了 CreateFor 之后 CreateDefault 被顺手改掉」。
        Assert.Equal(60, SessionOptions.CreateDefault().MaxRecordingSeconds);
    }
}
