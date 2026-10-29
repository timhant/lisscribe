using Lisscribe.Core.Asr;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

[Collection(ProcessWorkingDirectoryCollection.Name)]
public sealed class AsrModelPathsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-model").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Resolve_ProducesOfficialSherpaOnnxFileNames()
    {
        var paths = AsrModelPaths.Resolve(_dir);

        Assert.Equal(Path.Combine(_dir, "model.int8.onnx"), paths.ModelFile);
        Assert.Equal(Path.Combine(_dir, "tokens.txt"), paths.TokensFile);
    }

    [Fact]
    public void IsComplete_WhenBothFilesPresent_IsTrue()
    {
        File.WriteAllText(Path.Combine(_dir, "model.int8.onnx"), "x");
        File.WriteAllText(Path.Combine(_dir, "tokens.txt"), "x");

        Assert.True(AsrModelPaths.Resolve(_dir).IsComplete);
    }

    [Fact]
    public void MissingDescription_WhenComplete_IsEmpty()
    {
        File.WriteAllText(Path.Combine(_dir, "model.int8.onnx"), "x");
        File.WriteAllText(Path.Combine(_dir, "tokens.txt"), "x");

        Assert.Equal(string.Empty, AsrModelPaths.Resolve(_dir).MissingDescription);
    }

    [Fact]
    public void IsComplete_WhenModelMissing_IsFalseAndNamesTheMissingFile()
    {
        File.WriteAllText(Path.Combine(_dir, "tokens.txt"), "x");

        var paths = AsrModelPaths.Resolve(_dir);

        Assert.False(paths.IsComplete);
        Assert.Contains("model.int8.onnx", paths.MissingDescription);
        Assert.DoesNotContain("tokens.txt", paths.MissingDescription);
    }

    [Fact]
    public void IsComplete_WhenDirectoryDoesNotExist_IsFalse()
    {
        var paths = AsrModelPaths.Resolve(Path.Combine(_dir, "nope"));

        Assert.False(paths.IsComplete);
        Assert.Contains("model.int8.onnx", paths.MissingDescription);
        Assert.Contains("tokens.txt", paths.MissingDescription);
    }

    /// <summary>
    /// MissingDescription 会原样落进配置校验的错误提示里。只说「模型没找到」的文案
    /// 会让用户对着一个他其实没看过的目录发呆——必须把「找过哪里」一并说出来。
    /// </summary>
    [Fact]
    public void MissingDescription_NamesTheDirectoryThatWasSearched()
    {
        var missingDir = Path.Combine(_dir, "nope");

        var description = AsrModelPaths.Resolve(missingDir).MissingDescription;

        Assert.Contains(missingDir, description);
    }

    /// <summary>
    /// **整套发行布局压在这一条上**：exe 与 <c>models\</c> 同级，默认值 "./models/sense-voice"
    /// 必须按**程序所在目录**展开，而不是当前工作目录——开机自启时后者是 system32。
    ///
    /// <para>
    /// 旧断言只查 <c>Path.IsPathRooted</c>：按 CWD 展开出来的同样是绝对路径，照样通过，
    /// 等于什么也没守住。
    /// </para>
    /// </summary>
    [Fact]
    public void Resolve_ExpandsRelativePathAgainstTheExecutableDirectory_NotTheWorkingDirectory()
    {
        // **必须先把工作目录挪开。** 实测：MTP 拉起测试宿主时把工作目录设成了输出目录，
        // 也就是恰好等于 AppContext.BaseDirectory —— 不挪的话，「按 CWD 展开」的实现
        // 一样能让下面那条断言通过，整条测试等于没写。
        //
        // 改进程级的工作目录本是并行测试里的经典雷，此处敢改是因为本类已归入不并行的
        // ProcessWorkingDirectoryCollection。**新增改工作目录的测试类也必须标它**，有守卫测试把关。
        var originalWorkingDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(_dir);

            // 阴性对照：挪过之后两者必须真的不同，否则后面那条断言仍是空的。
            Assert.NotEqual(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory()));

            var paths = AsrModelPaths.Resolve("./models/sense-voice");

            Assert.Equal(
                Path.Combine(AppContext.BaseDirectory, "models", "sense-voice", "model.int8.onnx"),
                paths.ModelFile);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalWorkingDirectory);
        }
    }

    // ---- 模型目录约定 ------------------------------------------------------
    //
    // 布局只有一套：exe 与 models/ 同级，配置里的默认相对路径按 BaseDirectory 展开恰好命中。
    // 开发也走这一套——构建输出统一在 build\<项目名>\<配置>\（见 Directory.Build.props），
    // 把 models/ 拷进去即可，故环境变量不再是开发的必经之路。
    //
    // 环境变量留作逃生口（模型放别处、多份副本共用一份权重），且**与 ModelFixture 用的是
    // 同一个名字**：名字若各写各的，开发者设了变量，测试认、程序不认（或反过来），
    // 症状是「测试能跑真模型，双击起来却说模型缺失」。

    [Fact]
    public void EffectiveModelDir_PrefersTheEnvironmentOverride()
    {
        Assert.Equal(
            @"D:\elsewhere\sense-voice",
            AsrModelPaths.EffectiveModelDir("./models/sense-voice", @"D:\elsewhere\sense-voice"));
    }

    [Fact]
    public void EffectiveModelDir_FallsBackToConfig_WhenTheOverrideIsAbsent()
    {
        Assert.Equal("./models/sense-voice", AsrModelPaths.EffectiveModelDir("./models/sense-voice", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EffectiveModelDir_FallsBackToConfig_WhenTheOverrideIsBlank(string blank)
    {
        // 空白也算没设。Windows 上 `set LISSCRIBE_MODEL_DIR=` 留下的正是空串，
        // 而拿空串去 Path.GetFullPath 会抛 ArgumentException —— 症状离病因很远。
        Assert.Equal("./models/sense-voice", AsrModelPaths.EffectiveModelDir("./models/sense-voice", blank));
    }

    [Fact]
    public void TheEnvironmentVariableName_IsTheOneModelFixtureAlreadyUses()
    {
        // ModelFixture 现在引用同一个常量，这条断言只是把「两处必须同名」写成机器读得懂的形式。
        Assert.Equal("LISSCRIBE_MODEL_DIR", AsrModelPaths.ModelDirEnvironmentVariable);
    }

    // ---- 损坏检测 ----------------------------------------------------------
    //
    // 动机：把损坏的 model.int8.onnx 递给 sherpa-onnx，原生层的 Ort::Exception 不会被
    // 转成托管异常，进程直接 SIGABRT（exit 134）—— 没有窗口、没有日志、没有退出信息，
    // 用户唯一的线索是「点了没反应」，于是再点一次，再死一次。229MB 的模型要用户自己下，
    // 下载中断 / 磁盘写满 / 拷贝到一半 / 掐掉 fetch-models.ps1 都会产出这种文件，
    // 一点也不罕见。故必须在文件递到原生加载器之前就拦下。

    /// <summary>造一个「合法但极小」的 ONNX 式 protobuf：字段 1 varint + 字段 2 变长。</summary>
    private static byte[] MinimalValidOnnx() =>
        [0x08, 0x07, 0x12, 0x03, (byte)'a', (byte)'b', (byte)'c'];

    private void WriteModel(byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(_dir, "model.int8.onnx"), bytes);

    private void WriteTokens(string text) =>
        File.WriteAllText(Path.Combine(_dir, "tokens.txt"), text);

    [Fact]
    public void DescribeCorruption_OnWellFormedFiles_IsNull()
    {
        WriteModel(MinimalValidOnnx());
        WriteTokens("<blk> 0\n");

        Assert.Null(AsrModelPaths.Resolve(_dir).DescribeCorruption());
    }

    /// <summary>
    /// 下载中断截的是**尾巴**，文件头完好无损 —— 所以「魔数/文件头检查」对最常见的
    /// 故障恰恰无效。这里模拟：某个变长字段声明了 100 字节，文件里只剩 3 字节。
    /// </summary>
    [Fact]
    public void DescribeCorruption_OnTruncatedModel_SaysTruncatedAndPointsAtFetchScript()
    {
        WriteModel([0x08, 0x07, 0x12, 0x64, (byte)'a', (byte)'b', (byte)'c']);
        WriteTokens("<blk> 0\n");

        var description = AsrModelPaths.Resolve(_dir).DescribeCorruption();

        Assert.NotNull(description);
        Assert.Contains("model.int8.onnx", description);
        Assert.Contains("fetch-models.ps1", description);
    }

    /// <summary>下载到的其实是一张 404 页面时（镜像挂了、代理插了个门户认证页）。</summary>
    [Fact]
    public void DescribeCorruption_OnHtmlErrorPage_IsNotNull()
    {
        WriteModel(System.Text.Encoding.ASCII.GetBytes("<!DOCTYPE html><html>404</html>"));
        WriteTokens("<blk> 0\n");

        Assert.NotNull(AsrModelPaths.Resolve(_dir).DescribeCorruption());
    }

    [Fact]
    public void DescribeCorruption_OnEmptyModel_IsNotNull()
    {
        WriteModel([]);
        WriteTokens("<blk> 0\n");

        Assert.NotNull(AsrModelPaths.Resolve(_dir).DescribeCorruption());
    }

    /// <summary>磁盘写满时最典型的产物就是 0 字节文件，tokens.txt 也不例外。</summary>
    [Fact]
    public void DescribeCorruption_OnEmptyTokens_NamesTokensFile()
    {
        WriteModel(MinimalValidOnnx());
        WriteTokens(string.Empty);

        var description = AsrModelPaths.Resolve(_dir).DescribeCorruption();

        Assert.NotNull(description);
        Assert.Contains("tokens.txt", description);
    }

    /// <summary>文件压根不存在是 MissingDescription 的活，别让两条错误路径互相抢话。</summary>
    [Fact]
    public void DescribeCorruption_WhenFilesAbsent_IsNull()
    {
        Assert.Null(AsrModelPaths.Resolve(_dir).DescribeCorruption());
    }
}
