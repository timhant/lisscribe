using Xunit;
using Lisscribe.Core.Transcription;

namespace Lisscribe.Core.Tests.Transcription;

/// <summary>
/// 切点查找。合成信号与期望下标全部手算，不拿 FindCut 的输出当判据。
/// </summary>
public sealed class TranscriptChunkerTests
{
    private const int Rate = 16000;

    /// <summary>整段 0.5 幅度正弦（能量约 0.125），再把 [from, from+length) 置为 level 幅度的常量。</summary>
    private static float[] Tone(int totalSamples)
    {
        var s = new float[totalSamples];
        for (var i = 0; i < s.Length; i++)
        {
            s[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / Rate);
        }

        return s;
    }

    private static void Fill(float[] s, int from, int length, float level)
    {
        for (var i = from; i < from + length; i++)
        {
            s[i] = level;
        }
    }

    [Fact]
    public void Constants_MatchTheDesign()
    {
        Assert.Equal(16000, TranscriptChunker.SampleRate);
        Assert.Equal(20, TranscriptChunker.MaxChunkSeconds);
        Assert.Equal(3200, TranscriptChunker.WindowSamples);
        Assert.Equal(160, TranscriptChunker.HopSamples);
    }

    [Fact]
    public void FindCut_LandsOnTheCenterOfASilentStretch()
    {
        // 20 秒信号，静音 [10.0s, 10.3s)，即 [160000, 164800)，中心 162400。
        var s = Tone(20 * Rate);
        Fill(s, 160000, 4800, 0f);

        var cut = TranscriptChunker.FindCut(s, 10 * Rate, 20 * Rate);

        // 300ms 静音里 200ms 窗口有 100ms 的滑动余量，全静音窗口起点在 [160000, 161600]，
        // 并列取最早，故起点 160000、中心 161600。
        Assert.Equal(161600, cut);
    }

    [Fact]
    public void FindCut_PicksTheEarlierOfTwoEquallySilentStretches()
    {
        var s = Tone(20 * Rate);
        Fill(s, 250080, 3200, 0f);   // 后者
        Fill(s, 180000, 3200, 0f);   // 前者

        var cut = TranscriptChunker.FindCut(s, 10 * Rate, 20 * Rate);

        Assert.Equal(180000 + 1600, cut);
    }

    [Fact]
    public void FindCut_IgnoresADeeperSilenceOutsideTheSearchRange()
    {
        var s = Tone(20 * Rate);
        Fill(s, 40000, 6400, 0f);              // 区间外，绝对静音，更诱人
        Fill(s, 200000, 3200, 0.1f);           // 区间内较安静的一段（能量 0.01）

        var cut = TranscriptChunker.FindCut(s, 10 * Rate, 20 * Rate);

        Assert.Equal(200000 + 1600, cut);
    }

    [Fact]
    public void FindCut_DoesNotSelectASilenceThatDoesNotFitAWindowBeforeTheEnd()
    {
        // 区间 [160000, 320000)。静音贴着末端，只有最后 1600 样本（半个窗口）落在区间内。
        // 窗口放不下，不得选它，也不得越界。区间内另有一段能量 0.01 的较安静处作为正确答案。
        var s = Tone(20 * Rate + 4000);
        Fill(s, 320000 - 1600, 5600, 0f);
        Fill(s, 200000, 3200, 0.1f);

        var cut = TranscriptChunker.FindCut(s, 160000, 320000);

        Assert.Equal(200000 + 1600, cut);
    }

    [Fact]
    public void FindCut_AcceptsARangeExactlyOneWindowWide()
    {
        var s = Tone(10000);

        var cut = TranscriptChunker.FindCut(s, 1000, 1000 + 3200);

        Assert.Equal(1000 + 1600, cut);
    }

    [Theory]
    [InlineData(0, 3199)]       // 不足一个窗口
    [InlineData(-1, 5000)]      // 起点为负
    [InlineData(0, 20001)]      // 终点越过样本末端
    public void FindCut_RejectsBadRanges(int start, int end)
    {
        var s = new float[20000];

        Assert.ThrowsAny<ArgumentException>(() => TranscriptChunker.FindCut(s, start, end));
    }
}