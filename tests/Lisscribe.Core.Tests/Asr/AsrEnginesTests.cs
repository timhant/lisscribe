using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 两档引擎的名字与默认模型目录。
///
/// <para>
/// 这里断言的是**字面量**，不是「和生产代码算出来的一样」——拿被测代码自己的输出当预言机，
/// 改错了两边一起错，测试照样绿。本项目已在这上头栽过两次。
/// </para>
/// </summary>
public sealed class AsrEnginesTests
{
    [Fact]
    public void TheTwoEngineNames_AreExactlyWhatTheConfigFileWrites()
    {
        Assert.Equal("SenseVoice", AsrEngines.SenseVoice);
        Assert.Equal("Qwen3Asr", AsrEngines.Qwen3Asr);
    }

    [Fact]
    public void EachEngineHasItsOwnDefaultDirectory_BecauseTwoModelsCannotShareOne()
    {
        Assert.Equal("./models/sense-voice", AsrEngines.DefaultModelDir(AsrEngines.SenseVoice));
        Assert.Equal("./models/qwen3-asr", AsrEngines.DefaultModelDir(AsrEngines.Qwen3Asr));
    }

    [Fact]
    public void TheShippedDefaultConfig_StillPointsAtSenseVoice()
    {
        // 默认档不因为多了一档就改：Qwen3 那 982MB 权重是用户自己去下的，
        // 默认指过去只会让所有人开箱即见「模型缺失」。
        var config = LisscribeConfig.CreateDefault();

        Assert.Equal(AsrEngines.SenseVoice, config.Asr.Engine);
        Assert.Equal(AsrEngines.DefaultModelDir(AsrEngines.SenseVoice), config.Asr.ModelDir);
    }

    /// <summary>
    /// 两档的录音上限**不一样，而且差得很远**。这两个数都是实测出来的，改动前请先重测。
    ///
    /// <para>
    /// SenseVoice 60s：本机实测 61.5s 音频不截断，11 段内容一段不少（只掉个别字）。
    /// </para>
    ///
    /// <para>
    /// Qwen3 25s：**远低于那个 512 token 的硬上限（约 38.2s）**。拿官方 test_wavs 里的真实密集
    /// 中文语音逐段实测，输出字数是 10s→59、15s→125、20s→141、22s→154、<b>25s→157</b>、
    /// 27s→151、30s→129、33s→77、35s→53、36s→29、38s→1，50.96s 整条则是**空串**。
    /// 也就是说它在撞到 512 之前很久就开始丢内容了，25s 是最后一个还在涨的点。
    /// </para>
    /// </summary>
    [Fact]
    public void TheRecordingCeilings_AreTheMeasuredOnes_NotRoundNumbersSomebodyLiked()
    {
        Assert.Equal(60, AsrEngines.MaxRecordingSeconds(AsrEngines.SenseVoice));
        Assert.Equal(25, AsrEngines.MaxRecordingSeconds(AsrEngines.Qwen3Asr));
    }

    [Fact]
    public void TheHighAccuracyTier_RecordsForLess_NotMore()
    {
        // 反直觉，故写成断言：高精度档的上限**更短**。谁按「更强的模型应该更能扛」去改这个数，
        // 换来的是用户说满 40 秒、上屏一个「黑」字。
        Assert.True(
            AsrEngines.MaxRecordingSeconds(AsrEngines.Qwen3Asr)
            < AsrEngines.MaxRecordingSeconds(AsrEngines.SenseVoice));
    }

    [Fact]
    public void AskingForAnUnknownEnginesCeiling_Throws_RatherThanGuessing() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AsrEngines.MaxRecordingSeconds("Whisper"));

    [Theory]
    [InlineData("SenseVoice")]
    [InlineData("Qwen3Asr")]
    public void BothEnginesAreSupported(string engine) =>
        Assert.True(AsrEngines.IsSupported(engine));

    [Theory]
    [InlineData("sensevoice")]   // 大小写不算数：配置里写什么就得是什么，免得两处比较用不同规则。
    [InlineData("Qwen3")]
    [InlineData("Paraformer")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsNot(string? engine) =>
        Assert.False(AsrEngines.IsSupported(engine));

    [Fact]
    public void AskingForAnUnknownEnginesDefaultDirectory_Throws_RatherThanGuessing()
    {
        // 静默回落到 SenseVoice 的目录，会让「引擎名拼错」表现成「模型目录莫名其妙对了一半」。
        Assert.Throws<ArgumentOutOfRangeException>(() => AsrEngines.DefaultModelDir("Whisper"));
    }
}
