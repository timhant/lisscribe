using Xunit;
using Lisscribe.Core.PostProcess;

namespace Lisscribe.Core.Tests.PostProcess;

public sealed class TrailingPunctuationTrimmerTests
{
    private const int MaxChars = 8;

    private static string Trim(string text) =>
        TrailingPunctuationTrimmer.Trim(text, enabled: true, MaxChars);

    [Fact]
    public void Trim_ShortSentenceWithFullWidthPeriod_RemovesIt()
    {
        Assert.Equal("打开设置", Trim("打开设置。"));
    }

    [Fact]
    public void Trim_ShortSentenceWithFullWidthComma_RemovesIt()
    {
        Assert.Equal("打开设置", Trim("打开设置，"));
    }

    [Fact]
    public void Trim_ShortSentenceWithHalfWidthPeriod_RemovesIt()
    {
        Assert.Equal("open", Trim("open."));
    }

    [Fact]
    public void Trim_ShortSentenceWithHalfWidthComma_RemovesIt()
    {
        Assert.Equal("open", Trim("open,"));
    }

    [Fact]
    public void Trim_AtExactlyMaxChars_StillTrims()
    {
        // 8 个字 + 1 个标点 = 9 字符。判定长度用的是去掉标点后的长度。
        var text = "一二三四五六七八。";

        Assert.Equal("一二三四五六七八", Trim(text));
    }

    [Fact]
    public void Trim_OneCharOverMaxChars_KeepsPunctuation()
    {
        var text = "一二三四五六七八九。";

        Assert.Equal("一二三四五六七八九。", Trim(text));
    }

    [Fact]
    public void Trim_QuestionMark_IsKept()
    {
        // 只清 trash_punc（，。,.）四种。问号叹号是用户语气的一部分，不能删。
        Assert.Equal("好吗？", Trim("好吗？"));
    }

    [Fact]
    public void Trim_OnlyRemovesOnePunctuation()
    {
        Assert.Equal("好。", Trim("好。。"));
    }

    [Fact]
    public void Trim_WhenDisabled_ReturnsInputUnchanged()
    {
        Assert.Equal("打开设置。",
            TrailingPunctuationTrimmer.Trim("打开设置。", enabled: false, MaxChars));
    }

    [Fact]
    public void Trim_WithNoTrailingPunctuation_ReturnsInputUnchanged()
    {
        Assert.Equal("打开设置", Trim("打开设置"));
    }

    [Fact]
    public void Trim_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Trim(string.Empty));
    }

    [Fact]
    public void Trim_PunctuationOnly_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Trim("。"));
    }

    [Fact]
    public void Trim_WithMaxCharsZero_NeverTrims()
    {
        Assert.Equal("好。", TrailingPunctuationTrimmer.Trim("好。", enabled: true, maxChars: 0));
    }
}
