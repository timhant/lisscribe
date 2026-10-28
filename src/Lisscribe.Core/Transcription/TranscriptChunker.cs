namespace Lisscribe.Core.Transcription;

/// <summary>
/// 长录音切段：在搜索区间里挑最安静的一小段当切点，避免把词从中间劈开。
/// 设计见 docs/plans/2026-10-28-file-transcription-design.md §3.2、§3.3。
/// </summary>
public static class TranscriptChunker
{
    /// <summary>识别引擎的输入采样率。</summary>
    public const int SampleRate = 16000;

    /// <summary>每段最长秒数，Qwen3 与 SenseVoice 在此长度内都稳定。</summary>
    public const int MaxChunkSeconds = 20;

    /// <summary>能量窗口长度：200ms。</summary>
    public const int WindowSamples = SampleRate / 5;

    /// <summary>窗口步长：10ms。</summary>
    public const int HopSamples = SampleRate / 100;

    /// <summary>
    /// 在 [searchStart, searchEnd) 内找平均能量最低的 200ms 窗口，返回窗口中心的绝对下标，并列取最早。
    /// </summary>
    public static int FindCut(ReadOnlySpan<float> samples, int searchStart, int searchEnd)
    {
        if (searchStart < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(searchStart), "搜索起点不能为负。");
        }

        if (searchEnd > samples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(searchEnd), "搜索终点超出样本长度。");
        }

        if (searchEnd - searchStart < WindowSamples)
        {
            throw new ArgumentException("搜索区间不足一个能量窗口。", nameof(searchEnd));
        }

        var bestStart = searchStart;
        var bestEnergy = double.MaxValue;

        // 窗口必须整个落在区间内，故起点上限是 searchEnd - WindowSamples。
        for (var start = searchStart; start + WindowSamples <= searchEnd; start += HopSamples)
        {
            double sum = 0;
            for (var i = start; i < start + WindowSamples; i++)
            {
                sum += (double)samples[i] * samples[i];
            }

            var energy = sum / WindowSamples;

            // 严格小于才替换，并列时保留最早的。
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestStart = start;
            }
        }

        return bestStart + WindowSamples / 2;
    }
}