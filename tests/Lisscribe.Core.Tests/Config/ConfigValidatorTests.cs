using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.Core.Tests.Config;

public sealed class ConfigValidatorTests
{
    private static LisscribeConfig Default => LisscribeConfig.CreateDefault();

    [Fact]
    public void Validate_WhenConfigIsDefault_Passes()
    {
        var result = ConfigValidator.Validate(Default);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WhenEngineIsQwen3_NowPasses()
    {
        // 高精度档已落地（T1 首项）。这条从前断言的是「T0 拒绝 Qwen3Asr」，
        // 随实现一起翻面——把它删掉的话，日后谁把校验改回只认一档就没人拦得住。
        var config = Default with
        {
            Asr = Default.Asr with
            {
                Engine = "Qwen3Asr",
                ModelDir = "./models/qwen3-asr",
            },
        };

        var result = ConfigValidator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("sensevoice")]   // 大小写不算数：配置里写什么就得是什么。
    [InlineData("Whisper")]
    [InlineData("")]
    public void Validate_WhenEngineIsUnknown_Rejects_AndListsWhatIsOnOffer(string engine)
    {
        var config = Default with { Asr = Default.Asr with { Engine = engine } };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);

        // 只说「不支持」没用，用户下一步就要问「那支持什么」。两个名字都得报出来。
        Assert.Contains(result.Errors, e => e.Contains("SenseVoice") && e.Contains("Qwen3Asr"));
    }

    [Fact]
    public void Validate_WhenProviderIsCuda_StillRejects()
    {
        var config = Default with { Asr = Default.Asr with { Provider = "cuda" } };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cuda"));
    }

    [Theory]
    [InlineData(49)]
    [InlineData(3001)]
    public void Validate_WhenHoldThresholdOutOfRange_Rejects(int ms)
    {
        var config = Default with { Hotkey = Default.Hotkey with { HoldThresholdMs = ms } };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("holdThresholdMs"));
    }

    [Fact]
    public void Validate_WhenOutputModeUnknown_Rejects()
    {
        var config = Default with { Output = Default.Output with { Mode = "Telepathy" } };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WhenTrimMaxCharsNegative_Rejects()
    {
        var config = Default with { Output = Default.Output with { TrimPunctuationMaxChars = -1 } };

        Assert.False(ConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Validate_WhenVersionIsNewerThanSupported_Rejects()
    {
        var config = Default with { Version = LisscribeConfig.CurrentVersion + 1 };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("version"));
    }

    [Fact]
    public void Validate_NormalizesModifierCasingAndOrder()
    {
        var config = Default with { Hotkey = Default.Hotkey with { Modifiers = ["alt", "CTRL"] } };

        var result = ConfigValidator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Equal(["Ctrl", "Alt"], result.Normalized.Hotkey.Modifiers);
    }

    [Fact]
    public void Validate_WhenUnknownModifierAlone_Rejects()
    {
        var config = Default with { Hotkey = Default.Hotkey with { Modifiers = ["Meta"] } };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Meta"));
    }

    [Fact]
    public void Validate_WhenUnknownModifierMixedWithValid_Rejects()
    {
        var config = Default with { Hotkey = Default.Hotkey with { Modifiers = ["Ctrl", "Meta"] } };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Meta"));
    }

    [Fact]
    public void Validate_WhenModifiersNullArray_Rejects()
    {
        var config = Default with { Hotkey = Default.Hotkey with { Modifiers = null! } };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("modifiers"));
    }

    [Fact]
    public void Validate_WhenHotkeyNull_Rejects()
    {
        var config = Default with { Hotkey = null! };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("hotkey"));
    }

    [Fact]
    public void Validate_WhenAsrNull_Rejects()
    {
        var config = Default with { Asr = null! };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("asr"));
    }

    [Fact]
    public void Validate_WhenOutputNull_Rejects()
    {
        var config = Default with { Output = null! };

        var result = ConfigValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("output"));
    }
}
