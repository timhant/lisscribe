using Lisscribe.Core.Asr;
using Xunit;

namespace Lisscribe.Core.Tests.Packaging;

/// <summary>
/// 发布产物（CI、发布脚本、取模型脚本、许可、README、干净机清单）里<b>会随代码漂移的那几处</b>。
///
/// <para>
/// 不测「脚本能不能跑」——那要真下载上 GB、真发布一遍，归干净机验收清单。这里只测一件事：
/// <b>脚本与文档里写死的东西，跟代码里的常量还对得上吗。</b>
/// </para>
///
/// <para>
/// <b>期望值一律取自代码常量，不取自被测的那个文件</b>——拿被测对象自己划定受检范围就是空测试。
/// </para>
/// </summary>
public sealed class ReleaseArtifactsTests
{
    [Theory]
    [InlineData("LICENSE")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("scripts/publish.ps1")]
    [InlineData("scripts/fetch-models.ps1")]
    [InlineData("docs/clean-machine-checklist.md")]
    public void TheReleaseArtifactsExist(string relativePath) =>
        Assert.True(File.Exists(InRepo(relativePath)), $"缺少 {relativePath}");

    /// <summary>
    /// **CI 必须同时排除 <c>RequiresDesktop</c> 与 <c>Benchmark</c>。**
    ///
    /// <para>
    /// 只排除 <c>RequiresModel</c> 的话开局就红：<c>RequiresDesktop</c> 那批会在 runner 上抢前台、
    /// 往桌面发合成输入，前台被别人占着时固定红同样几条；<c>Benchmark</c> 那几条则让每次 PR
    /// 白等三分多钟，且 runner 的性能波动大到那组数没有意义。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("RequiresDesktop")]
    [InlineData("Benchmark")]
    public void TheCiWorkflow_ExcludesTheTraitsThatWouldMakeItUseless(string trait)
    {
        var yml = File.ReadAllText(InRepo(".github/workflows/ci.yml"));

        Assert.Contains($"--filter-not-trait \"Category={trait}\"", yml);
    }

    /// <summary>
    /// 取模型脚本里写死的文件名，必须跟 <see cref="AsrModelPaths"/> / <see cref="Qwen3ModelPaths"/>
    /// 的常量对得上。对不上时的症状是「脚本说下完了，程序说模型缺失」。
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRequiredModelFileName))]
    public void BothFetchScripts_LookForTheFileNamesTheCodeActuallyOpens(string fileName)
    {
        foreach (var script in (string[])["scripts/fetch-models.ps1", "scripts/fetch-models.sh"])
        {
            Assert.Contains(fileName, File.ReadAllText(InRepo(script)));
        }
    }

    /// <summary>
    /// 两档的默认模型目录也来自代码（<see cref="AsrEngines.DefaultModelDir"/>），
    /// 脚本把包解到别处的话，用户照做一遍仍然启动不了。
    /// </summary>
    [Theory]
    [InlineData(AsrEngines.SenseVoice)]
    [InlineData(AsrEngines.Qwen3Asr)]
    public void TheFetchScript_PutsModelsWhereTheDefaultConfigLooks(string engine)
    {
        // "./models/sense-voice" 里真正要对上的是最后那一段目录名。
        var directoryName = AsrEngines.DefaultModelDir(engine).Split('/')[^1];

        Assert.Contains(directoryName, File.ReadAllText(InRepo("scripts/fetch-models.ps1")));
    }

    /// <summary>
    /// **发布产物必须带上这三个**：`onnxruntime.dll` 是 `sherpa-onnx-c-api.dll` 的传递依赖，
    /// 由 Windows OS loader 按标准搜索顺序加载、不经 .NET 的解析器——开发机的输出目录里
    /// 恰好有它，所以少带了在本地永远测不出来，换台干净机器就是 `0xC000007B`。
    /// </summary>
    [Theory]
    [InlineData("onnxruntime.dll")]
    [InlineData("sherpa-onnx-c-api.dll")]
    [InlineData("Lisscribe.App.exe")]
    public void ThePublishScript_VerifiesTheNativeDependenciesCameOut(string fileName) =>
        Assert.Contains(fileName, File.ReadAllText(InRepo("scripts/publish.ps1")));

    /// <summary>
    /// 发布产物目录必须被 git 忽略：self-contained 的 `dist\` 有上百 MB、几百个文件，
    /// 一次 `git add .` 就进库了。这是「一律 `git add <具体路径>`」之外的第二道闸。
    /// </summary>
    [Fact]
    public void ThePublishOutput_IsGitIgnored()
    {
        var ignore = File.ReadAllLines(InRepo(".gitignore"));

        Assert.Contains(ignore, line => line.Trim() is "dist/" or "dist");
    }

    /// <summary>
    /// 许可那节是发布前不能省的一节，且**两种许可必须分开陈述**：代码 MIT，
    /// 而 SenseVoiceSmall 权重适用阿里 FunASR 的 `MODEL_LICENSE`（自定义条款，非 Apache/MIT）。
    /// 混作一谈就是许可事故。
    /// </summary>
    [Theory]
    [InlineData("MIT")]
    [InlineData("MODEL_LICENSE")]
    [InlineData("Apache-2.0")]
    public void TheReadme_StatesTheCodeAndWeightLicencesSeparately(string token) =>
        Assert.Contains(token, File.ReadAllText(InRepo("README.md")));

    /// <summary>
    /// 干净机清单必须验中文安装目录：sherpa-onnx 按窄字符查模型文件，中文路径一律判「不存在」，
    /// 而它建不出识别器时既不抛也不报——而中文路径在国内是常态。
    /// </summary>
    [Fact]
    public void TheCleanMachineChecklist_CoversTheNonAsciiInstallPath()
    {
        var checklist = File.ReadAllText(InRepo("docs/clean-machine-checklist.md"));

        Assert.Contains("中文", checklist);

        // 阴性对照：不能只是在别处捎带提一句「中文」，得真有一条可执行的验收项。
        Assert.Contains("- [ ]", checklist);
    }

    /// <summary>
    /// **两个 .ps1 必须带 UTF-8 BOM。**
    ///
    /// <para>
    /// Windows PowerShell 5.1 读脚本文件时，没有 BOM 的 UTF-8 会被按系统 ANSI 代码页解码，
    /// 中文注释与中文字符串当场变成乱码，随后是一串 <c>Unexpected token</c> ——**脚本一行也
    /// 执行不到**。而干净的 Windows 机器上默认只有 5.1（PowerShell 7 要另装），
    /// 这两个脚本恰恰就是给那种机器准备的。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("scripts/publish.ps1")]
    [InlineData("scripts/fetch-models.ps1")]
    public void ThePowerShellScripts_CarryAUtf8Bom_OrPowerShell51CannotEvenParseThem(string relativePath)
    {
        var head = new byte[3];

        using (var stream = File.OpenRead(InRepo(relativePath)))
        {
            stream.ReadExactly(head);
        }

        Assert.Equal<byte[]>([0xEF, 0xBB, 0xBF], head);
    }

    public static TheoryData<string> EveryRequiredModelFileName() =>
    [
        AsrModelPaths.ModelFileName,
        AsrModelPaths.TokensFileName,
        Qwen3ModelPaths.ConvFrontendFileName,
        Qwen3ModelPaths.EncoderFileName,
        Qwen3ModelPaths.DecoderFileName,
        Qwen3ModelPaths.VocabFileName,
    ];

    /// <summary>
    /// 仓库根按 <c>lisscribe.sln</c> 定位——测试的工作目录是构建输出目录，
    /// 而这一组测的全是仓库里的文件。
    /// </summary>
    private static string InRepo(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "lisscribe.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
