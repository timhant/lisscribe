using Lisscribe.Core.Audio;
using Xunit;

namespace Lisscribe.Core.Tests.Audio;

/// <summary>
/// 音量条的刻度映射。
///
/// <para>
/// **起因是一次实机故障**：用户大声说话，悬浮条的音量条也只涨到两成，「根本占不满」。
/// 病根不在麦克风，在量纲——<see cref="RmsMeter.Compute"/> 返回的是**线性 RMS**，
/// 而进度条的 <c>Maximum</c> 是 1。语音的 RMS 本来就落在 0.02–0.2 这一段，
/// 要把线性刻度顶满得是削波级别的信号。人耳听的是对数，条也该按对数画。
/// </para>
///
/// <para>
/// 本组每条都带阳性对照（先断言原始 RMS 真的很小），否则「映射后过半」这句话
/// 无法与「样本本来就响」区分开——那样这组测试在修复前也会绿。
/// </para>
/// </summary>
public sealed class VolumeScaleTests
{
    [Fact]
    public void LoudSpeech_FillsMostOfTheBar_WhereasTheRawRmsBarelyMoves()
    {
        var rms = RmsMeter.Compute(Sine(amplitude: 0.28f));

        // 阳性对照：这确实是「大声说话」的量级，而它的原始 RMS 连三成都不到。
        // 直接拿它当进度值，就是用户看到的那根几乎不动的条。
        Assert.InRange(rms, 0.15f, 0.25f);

        var level = VolumeScale.ToBarLevel(rms);

        Assert.True(level >= 0.6, $"大声说话该占掉大半根条，实际只有 {level:F3}（原始 RMS {rms:F3}）");
    }

    [Fact]
    public void OrdinarySpeech_LandsAroundTheMiddle()
    {
        var rms = RmsMeter.Compute(Sine(amplitude: 0.1f));

        Assert.InRange(rms, 0.05f, 0.09f);

        var level = VolumeScale.ToBarLevel(rms);

        // 上下界都要卡：只卡下界的话，把映射写成「恒等于 1」也照样绿。
        Assert.InRange(level, 0.4, 0.75);
    }

    [Fact]
    public void Silence_LeavesTheBarEmpty()
    {
        Assert.Equal(0d, VolumeScale.ToBarLevel(0f));
    }

    [Fact]
    public void RoomToneBelowTheNoiseFloor_LeavesTheBarEmpty()
    {
        // 底噪把条顶起一小截，看起来就像「没说话也在收音」。门限以下一律归零。
        var rms = RmsMeter.Compute(Sine(amplitude: 0.002f));

        Assert.InRange(rms, 0f, 0.003f);
        Assert.Equal(0d, VolumeScale.ToBarLevel(rms));
    }

    [Fact]
    public void FullScale_FillsTheBar()
    {
        Assert.Equal(1d, VolumeScale.ToBarLevel(1f));
    }

    [Fact]
    public void AnythingLouderThanFullScale_IsClampedInsteadOfOverflowing()
    {
        // RmsMeter 已经夹过一道，但本函数是公开的，别指望调用方。
        Assert.Equal(1d, VolumeScale.ToBarLevel(4f));
    }

    [Fact]
    public void NegativeInput_IsClampedToEmpty()
    {
        // log10(负数) 是 NaN，而 NaN 一路绑到 ProgressBar.Value 上不抛也不报，
        // 只会让条停住不动——正是最难查的那种症状。
        Assert.Equal(0d, VolumeScale.ToBarLevel(-1f));
    }

    [Fact]
    public void TheMappingIsMonotonic_SoALouderVoiceNeverDrawsAShorterBar()
    {
        double[] samples = [0f, 0.001f, 0.005f, 0.01f, 0.03f, 0.05f, 0.1f, 0.2f, 0.5f, 1f];

        for (var i = 1; i < samples.Length; i++)
        {
            var previous = VolumeScale.ToBarLevel(samples[i - 1]);
            var current = VolumeScale.ToBarLevel(samples[i]);

            Assert.True(current >= previous, $"RMS {samples[i]} 的刻度 {current} 反而低于 {samples[i - 1]} 的 {previous}");
        }
    }

    [Fact]
    public void EveryOutput_StaysInsideTheProgressBarsRange()
    {
        for (var rms = 0d; rms <= 1d; rms += 0.01d)
        {
            Assert.InRange(VolumeScale.ToBarLevel(rms), 0d, 1d);
        }
    }

    /// <summary>一段固定振幅的正弦，RMS 恒为 <c>振幅 / √2</c>——好让阳性对照的数值可手算。</summary>
    private static float[] Sine(float amplitude)
    {
        var samples = new float[1600];

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * MathF.Sin(2f * MathF.PI * i / 16f);
        }

        return samples;
    }
}
