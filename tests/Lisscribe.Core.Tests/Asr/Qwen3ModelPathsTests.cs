using Lisscribe.Core.Asr;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// Qwen3-ASR 的四件套定位：三个 onnx + 一个 tokenizer 目录。
///
/// <para>
/// 与 SenseVoice 那两个文件的差别不只是数量：<b>tokenizer 是目录不是文件</b>，
/// 而 <c>File.Exists</c> 对目录返回 false。照抄 SenseVoice 那套写法，
/// 会得到一个「模型明明齐了却永远报缺失」的程序。
/// </para>
/// </summary>
public sealed class Qwen3ModelPathsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-qwen3").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Resolve_ProducesOfficialSherpaOnnxNames()
    {
        var paths = Qwen3ModelPaths.Resolve(_dir);

        Assert.Equal(Path.Combine(_dir, "conv_frontend.onnx"), paths.ConvFrontendFile);
        Assert.Equal(Path.Combine(_dir, "encoder.int8.onnx"), paths.EncoderFile);
        Assert.Equal(Path.Combine(_dir, "decoder.int8.onnx"), paths.DecoderFile);
        Assert.Equal(Path.Combine(_dir, "tokenizer"), paths.TokenizerDir);
    }

    [Fact]
    public void Resolve_ExpandsRelativePathAgainstTheExecutableDirectory_NotTheWorkingDirectory()
    {
        // 与 SenseVoice 同一条理由：开机自启时工作目录是 system32。
        // 阴性对照见 AsrModelPathsTests 里那条同名测试的注释。
        var originalWorkingDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(_dir);

            Assert.NotEqual(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory()));

            Assert.Equal(
                Path.Combine(AppContext.BaseDirectory, "models", "qwen3-asr", "encoder.int8.onnx"),
                Qwen3ModelPaths.Resolve("./models/qwen3-asr").EncoderFile);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalWorkingDirectory);
        }
    }

    [Fact]
    public void IsComplete_WhenAllFourAreThere_IsTrue()
    {
        WriteWholeModel();

        Assert.True(Qwen3ModelPaths.Resolve(_dir).IsComplete);
        Assert.Equal(string.Empty, Qwen3ModelPaths.Resolve(_dir).MissingDescription);
    }

    [Theory]
    [InlineData("conv_frontend.onnx")]
    [InlineData("encoder.int8.onnx")]
    [InlineData("decoder.int8.onnx")]
    public void IsComplete_WhenOneOnnxIsMissing_IsFalseAndNamesOnlyThatOne(string missing)
    {
        WriteWholeModel();
        File.Delete(Path.Combine(_dir, missing));

        var paths = Qwen3ModelPaths.Resolve(_dir);

        Assert.False(paths.IsComplete);
        Assert.Contains(missing, paths.MissingDescription);

        // 只点名真缺的那个。三个名字一起报会让用户以为整包都没了，
        // 而实际上多半只是某个 756MB 的文件没拷完。
        foreach (var present in new[] { "conv_frontend.onnx", "encoder.int8.onnx", "decoder.int8.onnx" })
        {
            if (present != missing)
            {
                Assert.DoesNotContain(present, paths.MissingDescription);
            }
        }
    }

    /// <summary>
    /// tokenizer 是目录。<c>File.Exists</c> 对它恒为 false，
    /// 照抄 SenseVoice 的判法会让一个完好的模型永远报缺失。
    /// </summary>
    [Fact]
    public void IsComplete_TreatsTheTokenizerAsADirectory_NotAFile()
    {
        WriteWholeModel();

        Assert.True(Qwen3ModelPaths.Resolve(_dir).IsComplete);

        Directory.Delete(Path.Combine(_dir, "tokenizer"), recursive: true);

        var paths = Qwen3ModelPaths.Resolve(_dir);

        Assert.False(paths.IsComplete);
        Assert.Contains("tokenizer", paths.MissingDescription);
    }

    /// <summary>
    /// 空的 tokenizer 目录也算缺失。解包中断、拷贝只拷了目录结构时就是这个样子，
    /// 而原生层拿着一个没有 vocab.json 的目录会当场崩掉。
    /// </summary>
    [Fact]
    public void IsComplete_WhenTheTokenizerDirectoryHasNoVocab_IsFalse()
    {
        WriteWholeModel();
        File.Delete(Path.Combine(_dir, "tokenizer", "vocab.json"));

        var paths = Qwen3ModelPaths.Resolve(_dir);

        Assert.False(paths.IsComplete);
        Assert.Contains("vocab.json", paths.MissingDescription);
    }

    [Fact]
    public void MissingDescription_NamesTheDirectoryThatWasSearched()
    {
        var missingDir = Path.Combine(_dir, "nope");

        Assert.Contains(missingDir, Qwen3ModelPaths.Resolve(missingDir).MissingDescription);
    }

    // ---- 损坏检测 ----------------------------------------------------------
    //
    // 与 SenseVoice 同一条命：损坏的 onnx 递给原生加载器就是进程级崩溃，没有窗口没有日志。
    // Qwen3 那包 982MB、单个 decoder 就 756MB，下载中断的概率只高不低。

    [Fact]
    public void DescribeCorruption_OnWellFormedFiles_IsNull()
    {
        WriteWholeModel();

        Assert.Null(Qwen3ModelPaths.Resolve(_dir).DescribeCorruption());
    }

    /// <summary>
    /// **三个 onnx 每个都要查。** 只查其中一个的话，另外两个被截断时照样一路走到原生层。
    /// 这条用 Theory 逐个截断，正是为了让「漏查一个」这种改动必然翻红。
    /// </summary>
    [Theory]
    [InlineData("conv_frontend.onnx")]
    [InlineData("encoder.int8.onnx")]
    [InlineData("decoder.int8.onnx")]
    public void DescribeCorruption_CatchesATruncatedTail_InEveryOneOfTheThreeOnnxFiles(string victim)
    {
        WriteWholeModel();

        // 声明 100 字节的变长字段，文件里只剩 3 字节——正是下载中断截掉尾巴的样子。
        File.WriteAllBytes(
            Path.Combine(_dir, victim),
            [0x08, 0x07, 0x12, 0x64, (byte)'a', (byte)'b', (byte)'c']);

        var description = Qwen3ModelPaths.Resolve(_dir).DescribeCorruption();

        Assert.NotNull(description);
        Assert.Contains(victim, description);
        Assert.Contains("fetch-models.ps1", description);
    }

    [Fact]
    public void DescribeCorruption_WhenFilesAbsent_IsNull()
    {
        // 文件不存在归 MissingDescription 报，两条错误路径别抢话。
        Assert.Null(Qwen3ModelPaths.Resolve(_dir).DescribeCorruption());
    }

    /// <summary>造一个「合法但极小」的 ONNX 式 protobuf：字段 1 varint + 字段 2 变长。</summary>
    private static byte[] MinimalValidOnnx() =>
        [0x08, 0x07, 0x12, 0x03, (byte)'a', (byte)'b', (byte)'c'];

    private void WriteWholeModel()
    {
        foreach (var name in new[] { "conv_frontend.onnx", "encoder.int8.onnx", "decoder.int8.onnx" })
        {
            File.WriteAllBytes(Path.Combine(_dir, name), MinimalValidOnnx());
        }

        var tokenizer = Directory.CreateDirectory(Path.Combine(_dir, "tokenizer"));

        File.WriteAllText(Path.Combine(tokenizer.FullName, "vocab.json"), "{}");
        File.WriteAllText(Path.Combine(tokenizer.FullName, "merges.txt"), "#version: 0.2\n");
        File.WriteAllText(Path.Combine(tokenizer.FullName, "tokenizer_config.json"), "{}");
    }
}
