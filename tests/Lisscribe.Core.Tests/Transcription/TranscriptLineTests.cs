using Xunit;
using Lisscribe.Core.Transcription;

namespace Lisscribe.Core.Tests.Transcription;

public sealed class TranscriptLineTests
{
    [Theory]
    [InlineData(0.0, "[00:00:00] 你好")]
    [InlineData(59.999, "[00:00:59] 你好")]                 // 秒向下取整，不进位
    [InlineData(6549.0, "[01:49:09] 你好")]                 // 1*3600 + 49*60 + 9
    [InlineData(90000.0, "[25:00:00] 你好")]                // 25 小时，小时不回绕
    public void Format_RendersStartAsBracketedClock(double seconds, string expected)
    {
        Assert.Equal(expected, TranscriptLine.Format(TimeSpan.FromSeconds(seconds), "你好"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t\r\n ")]
    public void Format_BlankText_ReturnsNull(string? text)
    {
        Assert.Null(TranscriptLine.Format(TimeSpan.FromSeconds(5), text));
    }

    [Fact]
    public void Format_TrimsTextOnBothEnds()
    {
        Assert.Equal("[00:00:07] 你好 世界", TranscriptLine.Format(TimeSpan.FromSeconds(7), "  你好 世界\r\n"));
    }
}