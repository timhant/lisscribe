using System.IO.Compression;
using System.Text.RegularExpressions;
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
    [InlineData("THIRD-PARTY-NOTICES.md")]
    [InlineData("licenses/Apache-2.0.txt")]
    [InlineData("licenses/ANGLE-LICENSE.txt")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("scripts/publish.ps1")]
    [InlineData("scripts/fetch-models.ps1")]
    [InlineData("docs/clean-machine-checklist.md")]
    public void TheReleaseArtifactsExist(string relativePath) =>
        Assert.True(File.Exists(InRepo(relativePath)), $"缺少 {relativePath}");

    /// <summary>
    /// 每个直接依赖都必须在第三方声明里被点名。
    ///
    /// <para>
    /// <b>受检范围取自各 <c>.csproj</c> 的 <c>PackageReference</c>，不取自被测的那份声明</b>——
    /// 让声明自己划定该声明谁，加一个依赖忘了写进去它照样绿。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryPackageTheAppShips))]
    public void TheThirdPartyNotices_NameEveryDirectDependency(string package) =>
        Assert.Contains(package, File.ReadAllText(InRepo("THIRD-PARTY-NOTICES.md")));

    /// <summary>
    /// 声明里必须三种许可都在场。少写一种就是把一批组件的义务漏掉：
    /// sherpa-onnx 与 Serilog 系是 Apache-2.0，Avalonia 带的 ANGLE 原生包是 BSD-3-Clause，
    /// 其余是 MIT。
    /// </summary>
    [Theory]
    [InlineData("MIT")]
    [InlineData("Apache-2.0")]
    [InlineData("BSD-3-Clause")]
    public void TheThirdPartyNotices_CoverEveryLicenceInTheBundle(string licence) =>
        Assert.Contains(licence, File.ReadAllText(InRepo("THIRD-PARTY-NOTICES.md")));

    /// <summary>
    /// **Apache-2.0 与 BSD 两份全文必须真的随包走，不能只给个链接。**
    /// Apache-2.0 第 4 条明写分发时须附许可副本，BSD 亦要求在分发材料中重述条款与免责声明。
    /// 用长度加正文关键词判：只写一行链接的「许可文件」过不了这一关。
    /// </summary>
    [Theory]
    [InlineData("licenses/Apache-2.0.txt", "TERMS AND CONDITIONS", 8000)]
    [InlineData("licenses/ANGLE-LICENSE.txt", "Redistribution and use", 1000)]
    public void TheBundledLicenceTexts_AreTheWholeThing(string relativePath, string marker, int minimumLength)
    {
        var text = File.ReadAllText(InRepo(relativePath));

        Assert.Contains(marker, text);
        Assert.True(text.Length >= minimumLength, $"{relativePath} 只有 {text.Length} 字符，不像是许可全文");
    }

    /// <summary>
    /// 发布脚本得真把这几份拷进产物里——留在仓库里不随包走，等于没做。
    /// </summary>
    [Theory]
    [InlineData("THIRD-PARTY-NOTICES.md")]
    [InlineData("licenses")]
    public void ThePublishScript_ShipsTheLicenceMaterial(string token) =>
        Assert.Contains(token, File.ReadAllText(InRepo("scripts/publish.ps1")));

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
    /// 第三方声明里点到的原生库文件名，得是**包里真有的那个名字**。
    /// ANGLE 在 Avalonia 的包里叫 <c>av_libglesv2.dll</c>，不叫上游的 <c>libGLESv2.dll</c>——
    /// 写错了，拿着声明去核对的人会以为这条根本不适用。
    /// </summary>
    [Fact]
    public void TheThirdPartyNotices_CallTheAngleLibraryByTheNameItShipsUnder()
    {
        var notices = File.ReadAllText(InRepo("THIRD-PARTY-NOTICES.md"));

        Assert.Contains("av_libglesv2.dll", notices);
        Assert.DoesNotContain("libGLESv2.dll", notices);
    }

    /// <summary>
    /// Apache-2.0 那一栏得点出**包里哪两个文件**属于它。只写包名的话，
    /// 拿着一堆 DLL 的人对不上号——而这两个恰恰是发行包里体积最大、最引人问的东西。
    /// </summary>
    [Theory]
    [InlineData("onnxruntime.dll")]
    [InlineData("sherpa-onnx-c-api.dll")]
    public void TheThirdPartyNotices_NameTheNativeBinariesThatCarryTheApacheLicence(string fileName) =>
        Assert.Contains(fileName, File.ReadAllText(InRepo("THIRD-PARTY-NOTICES.md")));

    /// <summary>
    /// **面向用户的文档里不许教 <c>pwsh</c>。**
    ///
    /// <para>
    /// PowerShell 7 要另装，干净的 Windows 机器上只有 5.1，敲 <c>pwsh</c> 得到的是
    /// 「无法将“pwsh”项识别为 cmdlet」——用户卡在第二步，且那句报错跟语音输入毫无关系。
    /// 而 5.1 默认的执行策略是 Restricted，直接跑 <c>.ps1</c> 一样被拦，故须带
    /// <c>-ExecutionPolicy Bypass -File</c>。CI 不在此列：runner 上 pwsh 是有的。
    /// </para>
    /// <para>
    /// 判的是「<b>拿 pwsh 去跑某个 .ps1</b>」这个动作，不是「文中出现过 pwsh 四个字母」——
    /// 后者会把「别写成 pwsh」这类警示语本身也判红。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/clean-machine-checklist.md")]
    public void TheUserFacingDocs_InvokeScriptsInAWayStockWindowsCanRun(string relativePath)
    {
        var text = File.ReadAllText(InRepo(relativePath));

        var teachesPwsh = Regex.Match(text, @"pwsh[^\r\n`]*\.ps1");
        Assert.False(teachesPwsh.Success, $"这句会让干净 Windows 上的用户卡住：{teachesPwsh.Value}");

        Assert.Contains("-ExecutionPolicy Bypass -File", text);
    }

    /// <summary>
    /// 发布产物目录必须被 git 忽略：self-contained 的 `dist\` 有上百 MB、几百个文件，
    /// 一次 `git add .` 就进库了。这是「一律 `git add <具体路径>`」之外的第二道闸。
    /// </summary>
    [Fact]
    public void ThePublishOutput_IsGitIgnored()
    {
        var ignore = File.ReadAllLines(InRepo(".gitignore"));

        Assert.Contains(ignore, line => line.Trim() is "dist/" or "dist");

        // 发行 zip 与 dist 同处一地、同样上百 MB，一并挡住。
        Assert.Contains(ignore, line => line.Trim() is "*.zip");
    }

    /// <summary>
    /// 发布脚本要把整份产物压成**一个**归档，那才是能挂到 Release 上、能交给别人的东西。
    ///
    /// <para>
    /// **不许用 <c>Compress-Archive</c>**：Windows PowerShell 5.1 的实现把条目名写成反斜杠
    /// （实测 337 个条目 337 个反斜杠、0 个正斜杠），而 ZIP 规范要求正斜杠。Windows 资源管理器
    /// 与 7-Zip 容忍这种包，换到 macOS / Linux 解压就摊成一堆名字里带 <c>\</c> 的文件，目录结构没了。
    /// <c>ZipFile.CreateFromDirectory</c> 也一样——Windows PowerShell 5.1 跑在 .NET Framework 上，
    /// 那一版按 <c>Path.DirectorySeparatorChar</c> 拼条目名。故两个都不能用，只能自己
    /// 逐个建条目并把分隔符换成 <c>/</c>。
    /// </para>
    /// <para>
    /// 判的是「有没有**调用**它们」——注释行里提名字不算，
    /// 否则脚本里那句「不用 Compress-Archive」的说明会把自己判红。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Compress-Archive")]
    [InlineData("CreateFromDirectory")]
    public void ThePublishScript_DoesNotUseTheApisThatWriteBackslashEntryNames(string api)
    {
        var script = File.ReadAllText(InRepo("scripts/publish.ps1"));

        var call = Regex.Match(script, $@"(?m)^(?!\s*#).*{Regex.Escape(api)}");

        Assert.False(call.Success, $"这一行会写出反斜杠条目名：{call.Value.Trim()}");
    }

    /// <summary>
    /// **真开包验**：条目名一律正斜杠，四份许可在里面，模型权重不在里面。
    ///
    /// <para>
    /// 预言机是打好的那个 zip 本身，与生成它的脚本无关——只验脚本文本的话，
    /// 「脚本看着对、包却是错的」这一格永远查不出来（本条就是这么发现反斜杠那个坑的）。
    /// 没打过包时跳过：CI 不跑发布。
    /// </para>
    /// </summary>
    [Fact]
    public void TheProducedArchive_UsesForwardSlashes_AndCarriesTheLicencesButNoWeights()
    {
        // **挑的必须是「当前版本」那个包。** 升过版本号之后，`dist\` 下常常新旧两个 zip 并排
        // 躺着，而 `FirstOrDefault()` 抓到的是旧的——它当然全绿，新包却一格都没查到。
        // 判据取自 props 的版本号，与 dist 里恰好有什么无关。
        var version = DeclaredVersion();
        var archive = Directory.Exists(InRepo("dist"))
            ? Directory.EnumerateFiles(InRepo("dist"), "*.zip")
                .FirstOrDefault(path => Path.GetFileName(path).Contains(version, StringComparison.Ordinal))
            : null;

        Assert.SkipWhen(archive is null, $"dist 下没有 {version} 的发行包，先跑 scripts\\publish.ps1");

        using var zip = ZipFile.OpenRead(archive!);
        var names = zip.Entries.Select(entry => entry.FullName).ToArray();

        Assert.DoesNotContain(names, name => name.Contains('\\'));

        var root = Path.GetFileNameWithoutExtension(archive!);

        foreach (var required in (string[])[
            "LICENSE", "THIRD-PARTY-NOTICES.md",
            "licenses/Apache-2.0.txt", "licenses/ANGLE-LICENSE.txt",
            "models/README.txt", "Lisscribe.App.exe", "onnxruntime.dll", "sherpa-onnx-c-api.dll"])
        {
            Assert.Contains($"{root}/{required}", names);
        }

        // 权重许可不允许随包分发，且包会因此涨到 1GB 以上。
        Assert.DoesNotContain(names, name => name.EndsWith(".onnx", StringComparison.Ordinal));
    }

    /// <summary>
    /// **包名里的版本号必须取自 <c>Directory.Build.props</c>，不许在脚本里另写一份。**
    ///
    /// <para>
    /// 两处各写各的，早晚出现「zip 叫 0.1.0、里面的 exe 报 0.2.0」——而拿到包的人只信文件名，
    /// 报错时会照着错的版本去查。判据是脚本里**不出现**那个版本字面量：
    /// 期望值取自 props，与被测的脚本无关。
    /// </para>
    /// </summary>
    [Fact]
    public void TheArchiveName_TakesItsVersionFromTheBuildProps_NotAHardcodedCopy()
    {
        var script = File.ReadAllText(InRepo("scripts/publish.ps1"));

        Assert.Contains("Directory.Build.props", script);
        Assert.DoesNotContain(DeclaredVersion(), script);
    }

    /// <summary>
    /// 包名要说清这是给哪个平台的：本项目只出 win-x64，而下载页上光有版本号看不出这一点。
    ///
    /// <para>
    /// 断言挑的是 <c>-win-x64"</c>（包名字符串的结尾），不是裸的 <c>win-x64</c>——后者在
    /// <c>--runtime win-x64</c> 那一行里早就有了，拿它当判据这条测试从写下来就是绿的。
    /// </para>
    /// </summary>
    [Fact]
    public void TheArchiveName_SaysWhichPlatformItIsFor()
    {
        var script = File.ReadAllText(InRepo("scripts/publish.ps1"));

        Assert.Contains("-win-x64\"", script);
        Assert.Contains(".zip\"", script);
    }

    /// <summary>
    /// <c>Directory.Build.props</c> 里声明的版本号。发行包的名字得跟它一致。
    /// </summary>
    private static string DeclaredVersion()
    {
        var props = File.ReadAllText(InRepo("Directory.Build.props"));
        var version = Regex.Match(props, @"<Version>([^<]+)</Version>");

        Assert.True(version.Success, "Directory.Build.props 里没有 <Version>，发行包无从命名");

        return version.Groups[1].Value;
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

    /// <summary>
    /// 应用真正带出去的那些包：从 <c>src\</c> 下每个 <c>.csproj</c> 里读 <c>PackageReference</c>。
    /// 测试项目的依赖不算——它们不进发行包。
    /// </summary>
    public static TheoryData<string> EveryPackageTheAppShips()
    {
        var names = Directory
            .EnumerateFiles(InRepo("src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(csproj => Regex.Matches(
                File.ReadAllText(csproj), """<PackageReference\s+Include="([^"]+)"""))
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .Order()
            .ToArray();

        // 一个都没读到时，下面那条测试会「零用例通过」——静默的空覆盖，正是这里要防的。
        Assert.NotEmpty(names);

        return [.. names];
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
