namespace Lisscribe.Core.Audio;

/// <summary>
/// 把 <see cref="RmsMeter"/> 算出的线性 RMS 换成音量条的刻度（0..1）。必须换：语音的线性 RMS 落在
/// 0.02–0.2 这一段，直接喂给 <c>Maximum = 1</c> 的进度条，症状是「大声说话也只涨到两成」。
/// 与 <see cref="RmsMeter"/> 分开，是因为 RMS 是对信号的测量（静音检测、增益提示都要它的真值），
/// 而本函数只回答「这一格柱子画多高」；合在一处，哪天为了让条好看去调 RMS 就会调坏所有测量。
/// </summary>
public static class VolumeScale
{
    /// <summary>
    /// 门限：低于这个响度一律画成空条。取 -50 dBFS 而不更低，是因为再往下就是房间底噪的地盘——
    /// 门限压得太深，没人说话时条也会顶起一小截。
    /// </summary>
    public const double NoiseFloorDb = -50d;

    /// <returns>0..1 的刻度值，可直接当进度条的 <c>Value</c>。</returns>
    public static double ToBarLevel(double rms)
    {
        // 负数与 0 都得在取对数之前拦掉：log10(0) 是 -∞、log10(负数) 是 NaN，
        // 而 NaN 一路绑到 ProgressBar.Value 上不抛也不报，只会让条停住不动。
        if (rms <= 0d)
        {
            return 0d;
        }

        if (rms >= 1d)
        {
            return 1d;
        }

        var decibels = 20d * Math.Log10(rms);

        // 满刻度是 0 dBFS，故分母就是门限的绝对值。
        var level = (decibels - NoiseFloorDb) / -NoiseFloorDb;

        return Math.Clamp(level, 0d, 1d);
    }
}
