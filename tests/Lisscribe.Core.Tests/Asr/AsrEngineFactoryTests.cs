using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 按配置选档。**不许静默回落**：引擎名对不上时若悄悄给个默认档，
/// 用户会以为自己在跑高精度档，而唯一的线索是「识别质量好像没变好」。
/// </summary>
public sealed class AsrEngineFactoryTests
{
    private static AsrSection Section(string engine) => new(engine, "./models/whatever", "cpu");

    [Fact]
    public async Task SenseVoiceConfig_ProducesTheSenseVoiceEngine()
    {
        await using var engine = AsrEngineFactory.Create(
            Section(AsrEngines.SenseVoice), NullLoggerFactory.Instance);

        Assert.IsType<SenseVoiceEngine>(engine);
        Assert.Equal("SenseVoice", engine.EngineName);
    }

    [Fact]
    public async Task Qwen3Config_ProducesTheQwen3Engine()
    {
        await using var engine = AsrEngineFactory.Create(
            Section(AsrEngines.Qwen3Asr), NullLoggerFactory.Instance);

        // 断言具体类型，不只是 EngineName：名字是引擎自己报的，
        // 两档都报同一个字符串时它照样绿——那正是「工厂选错档」的样子。
        Assert.IsType<Qwen3AsrEngine>(engine);
        Assert.Equal("Qwen3Asr", engine.EngineName);
    }

    [Fact]
    public async Task TheNewEngine_StartsOutPointingAtTheConfiguredDirectory()
    {
        await using var engine = AsrEngineFactory.Create(
            new AsrSection(AsrEngines.Qwen3Asr, "D:/models/q3", "cpu"), NullLoggerFactory.Instance);

        Assert.Equal("D:/models/q3", engine.ModelDir);
        Assert.False(engine.IsLoaded);
    }

    [Theory]
    [InlineData("sensevoice")]
    [InlineData("Whisper")]
    [InlineData("")]
    public void AnUnknownEngine_Throws_RatherThanFallingBackToTheDefaultTier(string engine)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AsrEngineFactory.Create(Section(engine), NullLoggerFactory.Instance));
    }
}
