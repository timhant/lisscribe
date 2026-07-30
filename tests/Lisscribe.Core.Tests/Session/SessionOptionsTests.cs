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
    /// **只有录音上限随档位变，别的一个都不许动。**
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
            SessionOptions.CreateDefault() with { MaxRecordingSeconds = 25 },
            qwen3);
    }

    [Fact]
    public void CreateDefault_IsStillTheSenseVoiceTier()
    {
        // 默认档没变，这条守的是「加了 CreateFor 之后 CreateDefault 被顺手改掉」。
        Assert.Equal(60, SessionOptions.CreateDefault().MaxRecordingSeconds);
    }
}
