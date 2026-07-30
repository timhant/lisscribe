using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 测试基础设施自身的测试。ModelFixture 决定「真模型测试是 Skip 还是真跑」，
/// 它判错的代价是整类测试静默失效或整类测试莫名爆红，值得单独守住。
/// </summary>
public sealed class ModelFixtureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-fixture").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteRequiredModelFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "model.int8.onnx"), "x");
        File.WriteAllText(Path.Combine(_dir, "tokens.txt"), "x");
    }

    [Fact]
    public void IsUsableModelDir_WithModelTokensAndWavs_IsTrue()
    {
        WriteRequiredModelFiles();
        Directory.CreateDirectory(Path.Combine(_dir, "test_wavs"));
        File.WriteAllText(Path.Combine(_dir, "test_wavs", "zh.wav"), "x");

        Assert.True(ModelFixture.IsUsableModelDir(_dir));
    }

    /// <summary>
    /// 手工拷来的模型目录常只有权重和词表，没有 test_wavs —— 官方包里它是独立子目录，
    /// 拷贝时最容易漏。若可用性只看那两个文件，这种目录会让「模型缺失就 Skip」
    /// 变成「模型半全就在 ReadPcm16Mono 里硬失败」，报错还指向一个和模型无关的地方。
    /// </summary>
    [Fact]
    public void IsUsableModelDir_WithoutTestWavs_IsFalse()
    {
        WriteRequiredModelFiles();

        Assert.False(ModelFixture.IsUsableModelDir(_dir));
    }

    [Fact]
    public void IsUsableModelDir_WithoutModel_IsFalse()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "test_wavs"));
        File.WriteAllText(Path.Combine(_dir, "test_wavs", "zh.wav"), "x");

        Assert.False(ModelFixture.IsUsableModelDir(_dir));
    }

    /// <summary>
    /// RIFF 解析必须逐块走：真实 wav 常在 fmt 与 data 之间夹 LIST 等块，
    /// 且奇数长度的块后面跟一个不计入 size 的填充字节 —— 漏掉它，后续偏移全歪。
    /// </summary>
    [Fact]
    public void ReadPcm16Mono_SkipsOddSizedListChunkBeforeData()
    {
        var path = Path.Combine(_dir, "with-list.wav");
        short[] pcm = [0, 16384, -16384, 32767];
        File.WriteAllBytes(path, BuildWav(16000, pcm, oddSizedListChunk: "abc"));

        var (samples, rate) = ModelFixture.ReadPcm16Mono(path);

        Assert.Equal(16000, rate);
        Assert.Equal(4, samples.Length);
        Assert.Equal(0.5f, samples[1], 0.001);
        Assert.Equal(-0.5f, samples[2], 0.001);
    }

    [Fact]
    public void ReadPcm16Mono_OnNonRiffFile_ThrowsInvalidData()
    {
        var path = Path.Combine(_dir, "not-a-wav.bin");
        File.WriteAllText(path, "this is not a wav file at all");

        Assert.Throws<InvalidDataException>(() => ModelFixture.ReadPcm16Mono(path));
    }

    /// <summary>构造一个 16-bit 单声道 wav，可选地在 data 之前插入一个奇数长度的 LIST 块。</summary>
    private static byte[] BuildWav(int sampleRate, short[] pcm, string? oddSizedListChunk)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        void FourCc(string id) => writer.Write(System.Text.Encoding.ASCII.GetBytes(id));

        FourCc("RIFF");
        writer.Write(0); // 长度回填留空——解析器不该依赖它
        FourCc("WAVE");

        FourCc("fmt ");
        writer.Write(16);
        writer.Write((short)1);            // PCM
        writer.Write((short)1);            // 单声道
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);      // byte rate
        writer.Write((short)2);            // block align
        writer.Write((short)16);           // bits per sample

        if (oddSizedListChunk is not null)
        {
            var payload = System.Text.Encoding.ASCII.GetBytes(oddSizedListChunk);
            FourCc("LIST");
            writer.Write(payload.Length);  // 奇数
            writer.Write(payload);
            writer.Write((byte)0);         // 字对齐填充字节，不计入 size
        }

        FourCc("data");
        writer.Write(pcm.Length * 2);
        foreach (var sample in pcm)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return buffer.ToArray();
    }
}
