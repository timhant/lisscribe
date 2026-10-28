using Xunit;
using Lisscribe.Core.Transcription;

namespace Lisscribe.Core.Tests.Transcription;

public sealed class RepetitionDetectorTests
{
    private static string Repeat(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));

    [Fact]
    public void NormalEmphaticSpeech_IsNotLooping()
    {
        Assert.False(RepetitionDetector.IsLooping("对，对，对，对。对，对，对。对。"));
    }

    [Fact]
    public void SingleCharUnit_NineteenIsFine_TwentyIsLooping()
    {
        Assert.False(RepetitionDetector.IsLooping(Repeat("好", 19)));
        Assert.True(RepetitionDetector.IsLooping(Repeat("好", 20)));
    }

    [Fact]
    public void TwoCharUnitWithPunctuation_RepeatedOneHundredTimes_IsLooping()
    {
        Assert.True(RepetitionDetector.IsLooping(Repeat("没有，", 100)));
    }

    [Fact]
    public void FourCharUnit_RepeatedTwentyTimes_IsLooping()
    {
        Assert.True(RepetitionDetector.IsLooping(Repeat("我们开会", 20)));
        Assert.False(RepetitionDetector.IsLooping(Repeat("我们开会", 19)));
    }

    [Fact]
    public void FiveCharUnit_RepeatedTwentyTimes_IsNotLooping()
    {
        // 规格上限是 4 字单元，5 字单元不判。
        Assert.False(RepetitionDetector.IsLooping(Repeat("我们开会吧", 20)));
    }

    [Fact]
    public void PunctuationAndSpacesInsideTheUnit_AreStrippedBeforeCounting()
    {
        // 带标点后单元长 5，不剥标点就超出 4 字上限而漏判。
        Assert.True(RepetitionDetector.IsLooping(Repeat("我们开会，", 20)));
        Assert.True(RepetitionDetector.IsLooping(Repeat("我们 开会 ", 20)));
    }

    [Fact]
    public void WhitespaceBetweenRepeats_IsIgnored()
    {
        Assert.True(RepetitionDetector.IsLooping(Repeat("嗯 ", 20)));
    }

    [Fact]
    public void RepeatsInterruptedByOtherText_DoNotAddUp()
    {
        // 两段各 12 次，中间隔了别的字，连续重复只有 12。
        Assert.False(RepetitionDetector.IsLooping(Repeat("好", 12) + "不是" + Repeat("好", 12)));
    }

    [Fact]
    public void LoopInTheMiddleOfNormalText_IsFound()
    {
        Assert.True(RepetitionDetector.IsLooping("今天的会议先讲预算。" + Repeat("没有", 30) + "然后继续。"));
    }

    [Fact]
    public void LongNormalSentence_IsNotLooping()
    {
        Assert.False(RepetitionDetector.IsLooping(
            "下个季度的预算需要重新评估，各部门先把现有的开支整理出来，再讨论哪些项目可以延后。"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("，。！")]
    public void NullEmptyOrPunctuationOnly_IsNotLooping(string? text)
    {
        Assert.False(RepetitionDetector.IsLooping(text!));
    }
}