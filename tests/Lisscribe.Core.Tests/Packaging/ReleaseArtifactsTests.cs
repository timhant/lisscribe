using System.IO.Compression;
using System.Text.RegularExpressions;
using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
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
    /// CI 命令行上的 trx 报告参数，每个测试项目都得有那个扩展包接得住。
    /// </summary>
    /// <remarks>
    /// 那个参数<b>不是</b>测试主机自带的（xunit.v3 + Microsoft.Testing.Platform），要单独引
    /// <c>Microsoft.Testing.Extensions.TrxReport</c>。少了它，测试主机在参数解析阶段就退出、
    /// 一条测试也不跑，而 <c>dotnet test</c> 报「Tests failed」且<b>不打印任何汇总行</b>
    /// ——看着像测试红了。2026-07-30 到 08-02 那六次 CI 全红就是这么来的，六次一条测试没跑过。
    /// <para>
    /// 受检范围取自 <c>tests\</c> 目录下的 csproj，判据取自 CI 的 yml，两侧互不取用：
    /// 让 yml 自己划定该查谁，新增一个测试项目忘了引包它照样绿。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryTestProject))]
    public void EveryTestProject_CarriesTheReporterThatTheCiCommandLineAsksFor(string csproj)
    {
        var yml = File.ReadAllText(InRepo(".github/workflows/ci.yml"));

        // 前提。CI 哪天不再要 trx 了，这一条会先红，讯息也说得清该改哪儿。
        Assert.Contains("report-trx", yml);

        Assert.Contains(
            """<PackageReference Include="Microsoft.Testing.Extensions.TrxReport" """,
            File.ReadAllText(csproj));
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
    /// **真开包验**：条目名一律正斜杠，许可齐全，而权重<b>不多不少，正好是包名声称的那些</b>。
    ///
    /// <para>
    /// 预言机是打好的那个 zip 本身，与生成它的脚本无关——只验脚本文本的话，
    /// 「脚本看着对、包却是错的」这一格永远查不出来（本条就是这么发现反斜杠那个坑的）。
    /// 没打过那一档时跳过：CI 不跑发布，本机也可能只打了其中一个。
    /// </para>
    /// <para>
    /// <b>权重那一格断的是「集合相等」，不是「不检查 .onnx」。</b> 0.3.0 起带模型的包要发，
    /// 于是原先那道「任何 .onnx 都不许进包」的硬闸不能再用；但换成不看，等于把拦住误打包
    /// 整份权重的闸门整个拆掉——精简包混进 756MB 的 decoder、或 sense-voice 包里混进 Qwen3
    /// 那三个，都将无声通过。故改成**逐档点名**：期望集合来自
    /// <see cref="AsrModelPaths"/> / <see cref="Qwen3ModelPaths"/> 的常量，多一个少一个都红。
    /// </para>
    /// <para>
    /// 挑包按「当前版本 + 该档后缀」精确命名。升过版本号之后 <c>dist\</c> 下常有新旧多个 zip
    /// 并排躺着，模糊匹配抓到的是旧的——它当然全绿，新包却一格都没查到。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryArchiveFlavour))]
    public void TheProducedArchive_UsesForwardSlashes_AndCarriesExactlyTheWeightsItsNameClaims(
        string flavour)
    {
        var root = $"lisscribe-{DeclaredVersion()}-win-x64{flavour}";
        var archive = InRepo($"dist/{root}.zip");

        Assert.SkipWhen(!File.Exists(archive), $"dist 下没有 {root}.zip，先跑 scripts\\publish.ps1");

        using var zip = ZipFile.OpenRead(archive);
        var names = zip.Entries.Select(entry => entry.FullName).ToArray();

        Assert.DoesNotContain(names, name => name.Contains('\\'));

        foreach (var required in CommonArchiveEntries.Concat(ExtraEntriesFor(flavour)))
        {
            Assert.Contains($"{root}/{required}", names);
        }

        var weights = names
            .Where(name => name.EndsWith(".onnx", StringComparison.Ordinal))
            .Select(name => name[(root.Length + 1)..])
            .Order()
            .ToArray();

        Assert.Equal(ExpectedWeightsFor(flavour), weights);
    }

    /// <summary>
    /// **带 Qwen3 的包必须预置 config.json，否则开箱即报「模型缺失」。**
    /// </summary>
    /// <remarks>
    /// 默认配置指的是 <c>./models/sense-voice</c>（见 <see cref="LisscribeConfig.CreateDefault"/>），
    /// 而那个包里只有 <c>qwen3-asr</c>。2026-08-09 真机实测：下 871MB、解压、双击，日志里是
    /// <c>缺少模型文件：model.int8.onnx、tokens.txt</c>——比不带模型的包还糟，因为用户以为
    /// 自己下的就是开箱即用的那个。
    /// <para>
    /// 期望值取自 <see cref="LisscribeConfig.CreateDefault"/>，不在这里手抄一份：脚本里那份
    /// 预置配置是**第二处默认值**，两处各写各的，改了默认热键之后包里那份还是旧的。
    /// 读法走真的 <see cref="ConfigStore"/>，顺带把「这份 JSON 结构完不完整」一起验了——
    /// 缺一整节的话它会静默回落默认值并把原文存成 .bad，而那正是最难发现的一种坏包。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheQwen3Archive_ShipsAConfigThatPointsAtTheModelItActuallyCarries()
    {
        var root = $"lisscribe-{DeclaredVersion()}-win-x64{Qwen3Flavour}";
        var archive = InRepo($"dist/{root}.zip");

        Assert.SkipWhen(!File.Exists(archive), $"dist 下没有 {root}.zip，先跑 scripts\\publish.ps1");

        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.GetEntry($"{root}/config.json");

        Assert.NotNull(entry);

        var scratch = Directory.CreateTempSubdirectory("lisscribe-config-probe");

        try
        {
            var path = Path.Combine(scratch.FullName, "config.json");

            entry!.ExtractToFile(path);

            var store = new ConfigStore(path);
            var config = store.Load();

            // 不是 Ok 就说明这份 JSON 缺段或解析不了，Load 已经悄悄换成默认值了。
            Assert.Equal(ConfigLoadOutcome.Ok, store.LastOutcome);

            Assert.Equal(AsrEngines.Qwen3Asr, config.Asr.Engine);
            Assert.Equal(AsrEngines.DefaultModelDir(AsrEngines.Qwen3Asr), config.Asr.ModelDir);

            // 除了引擎那一节，其余必须原样是默认值——预置配置的职责只有「指对模型」这一件事。
            // 逐字段比而不是比整个 record：HotkeySection 里那个 string[] 走的是引用相等。
            var expected = LisscribeConfig.CreateDefault();

            Assert.Equal(expected.Version, config.Version);
            Assert.Equal(expected.Hotkey.Modifiers, config.Hotkey.Modifiers);
            Assert.Equal(expected.Hotkey.MainKey, config.Hotkey.MainKey);
            Assert.Equal(expected.Hotkey.HoldThresholdMs, config.Hotkey.HoldThresholdMs);
            Assert.Equal(expected.Asr.Provider, config.Asr.Provider);
            Assert.Equal(expected.Output, config.Output);
            Assert.Equal(expected.App, config.App);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    /// <summary>
    /// 另两个包<b>不</b>预置配置：默认值本就指着 sense-voice，精简包则由用户自己决定用哪档。
    /// 多塞一份只会把用户后来改过的配置在下次覆盖安装时替换掉。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(SenseVoiceFlavour)]
    public void TheOtherArchives_DoNotShipAConfig(string flavour)
    {
        var root = $"lisscribe-{DeclaredVersion()}-win-x64{flavour}";
        var archive = InRepo($"dist/{root}.zip");

        Assert.SkipWhen(!File.Exists(archive), $"dist 下没有 {root}.zip，先跑 scripts\\publish.ps1");

        using var zip = ZipFile.OpenRead(archive);

        Assert.Null(zip.GetEntry($"{root}/config.json"));
    }

    /// <summary>
    /// 三种发行包形态的后缀。<b>列在这里而不是数 <c>dist\</c> 里有什么</b>——
    /// 拿实际产出划定受检范围，publish 少打了一档它照样全绿。
    /// </summary>
    public static TheoryData<string> EveryArchiveFlavour() => ["", SenseVoiceFlavour, Qwen3Flavour];

    private const string SenseVoiceFlavour = "-sense-voice";
    private const string Qwen3Flavour = "-qwen3";

    /// <summary>三种形态都必须有的。</summary>
    private static readonly string[] CommonArchiveEntries = [
        "LICENSE", "THIRD-PARTY-NOTICES.md",
        "licenses/Apache-2.0.txt", "licenses/ANGLE-LICENSE.txt",
        "models/README.txt", "Lisscribe.App.exe", "onnxruntime.dll", "sherpa-onnx-c-api.dll"];

    /// <summary>该档权重的**完整**清单，路径取自引擎常量，不手抄字面量。</summary>
    private static string[] ExpectedWeightsFor(string flavour) => flavour switch
    {
        SenseVoiceFlavour => [$"{SenseVoiceDir}/{AsrModelPaths.ModelFileName}"],
        Qwen3Flavour =>
        [
            // Order() 之后的字母序，不是官方包里那个顺序。
            $"{Qwen3Dir}/{Qwen3ModelPaths.ConvFrontendFileName}",
            $"{Qwen3Dir}/{Qwen3ModelPaths.DecoderFileName}",
            $"{Qwen3Dir}/{Qwen3ModelPaths.EncoderFileName}",
        ],
        _ => [],
    };

    /// <summary>该档除权重之外还必须带的：词表，以及那一档特有的许可全文。</summary>
    private static string[] ExtraEntriesFor(string flavour) => flavour switch
    {
        // SenseVoice 的权重适用 FunASR 自定义协议，全文必须随包——这是分发义务，不是客套。
        SenseVoiceFlavour =>
        [
            $"{SenseVoiceDir}/{AsrModelPaths.TokensFileName}",
            "licenses/FunASR-MODEL_LICENSE.txt",
        ],
        // Qwen3 是 Apache-2.0，全文即上面那份 licenses/Apache-2.0.txt，不必另放一份。
        Qwen3Flavour =>
        [
            $"{Qwen3Dir}/{Qwen3ModelPaths.TokenizerDirName}/{Qwen3ModelPaths.VocabFileName}",
        ],
        _ => [],
    };

    private static string SenseVoiceDir => ArchiveModelDir(AsrEngines.SenseVoice);

    private static string Qwen3Dir => ArchiveModelDir(AsrEngines.Qwen3Asr);

    /// <summary>把 <c>./models/sense-voice</c> 这种默认目录换算成 zip 里的条目前缀。</summary>
    private static string ArchiveModelDir(string engine) =>
        AsrEngines.DefaultModelDir(engine).Replace("./", string.Empty).Replace('\\', '/');

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
    /// 断言挑的是包名那句里 <c>-win-x64</c> 后面紧跟着的东西，不是裸的 <c>win-x64</c>——后者在
    /// <c>--runtime win-x64</c> 那一行里早就有了，拿它当判据这条测试从写下来就是绿的。
    /// 0.3.0 起包名尾部多了一个 <c>$suffix</c>（带哪档权重），故这里连它一起认。
    /// </para>
    /// </summary>
    [Fact]
    public void TheArchiveName_SaysWhichPlatformItIsFor()
    {
        var script = File.ReadAllText(InRepo("scripts/publish.ps1"));

        Assert.Contains("-win-x64$suffix\"", script);
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

    /// <summary>
    /// <c>tests\</c> 下的每个测试项目。CI 是对整个解决方案跑的，漏掉任何一个都会让那一步整个失败。
    /// </summary>
    public static TheoryData<string> EveryTestProject()
    {
        var projects = Directory
            .EnumerateFiles(InRepo("tests"), "*.csproj", SearchOption.AllDirectories)
            .Order()
            .ToArray();

        // 一个都没读到时下面那条会「零用例通过」——静默的空覆盖，与上面同一条理由。
        Assert.NotEmpty(projects);

        return [.. projects];
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
