using Lisscribe.Core.Audio;
using Xunit;

namespace Lisscribe.Core.Tests.Audio;

public sealed class RmsMeterTests
{
    [Fact]
    public void Compute_ForSilence_ReturnsZero()
    {
        Assert.Equal(0f, RmsMeter.Compute(new float[1000]));
    }

    [Fact]
    public void Compute_ForFullScaleSquareWave_ReturnsOne()
    {
        var samples = new float[1000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 2 == 0 ? 1f : -1f;
        }

        Assert.Equal(1f, RmsMeter.Compute(samples), tolerance: 1e-4f);
    }

    [Fact]
    public void Compute_ForFullScaleSine_ReturnsAboutSqrtHalf()
    {
        var samples = new float[16000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 16000);
        }

        Assert.Equal(0.707f, RmsMeter.Compute(samples), tolerance: 0.01f);
    }

    [Fact]
    public void Compute_ForEmptyInput_ReturnsZero()
    {
        Assert.Equal(0f, RmsMeter.Compute([]));
    }

    [Fact]
    public void Compute_ClampsToOne_WhenSamplesExceedFullScale()
    {
        var samples = Enumerable.Repeat(3f, 100).ToArray();

        Assert.Equal(1f, RmsMeter.Compute(samples));
    }
}
