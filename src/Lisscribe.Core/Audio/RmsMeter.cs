namespace Lisscribe.Core.Audio;

/// <summary>
/// 悬浮条实时音量条用的 RMS 计算。音量条不只是装饰——它让用户一眼判断麦克风到底有没有收到声音。
/// </summary>
public static class RmsMeter
{
    /// <returns>0..1 之间的均方根值。</returns>
    public static float Compute(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        double sumOfSquares = 0;

        foreach (var sample in samples)
        {
            sumOfSquares += (double)sample * sample;
        }

        var rms = Math.Sqrt(sumOfSquares / samples.Length);

        return (float)Math.Clamp(rms, 0d, 1d);
    }
}
