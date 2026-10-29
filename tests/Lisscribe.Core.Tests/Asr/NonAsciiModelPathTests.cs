using System.Diagnostics;
using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// **模型目录落在中文路径下时，两档都必须照常加载。**
///
/// <para>
/// 起因：sherpa-onnx 1.13.4 在 Windows 上按窄字符查文件，中文路径一律判
/// <c>does not exist</c>，于是原生识别器根本没建起来（见 <see cref="NativeRecognizerHandleTests"/>
/// 记的那次事故）。而「程序装在中文路径下」在国内是常态，不能靠一句「请移到英文目录」了事。
/// </para>
///
/// <para>
/// <b>为什么两档各测一条。</b>变通的落点在基类，但两个派生类各自决定怎么把路径填进
/// <c>OfflineRecognizerConfig</c>；派生类若把相对根当绝对路径拼，变通当场失效，
/// 而基类的测试一条也照不到。
/// </para>
///
/// <para>
/// <b>用 junction 而不是复制</b>：真模型 982MB + 229MB，复制一份只为了改个目录名不划算。
/// junction 由 <c>mklink /J</c> 造——**不是符号链接**：<see cref="Directory.CreateSymbolicLink"/>
/// 要 <c>SeCreateSymbolicLinkPrivilege</c>（须开发者模式或管理员），CI 上未必有；
/// junction 普通用户就能建。
/// </para>
/// </summary>
[Trait("Category", "RequiresModel")]
[Collection(ProcessWorkingDirectoryCollection.Name)]
public sealed class NonAsciiModelPathTests : IDisposable
{
    /// <summary>目录名必须含非 ASCII，否则整条测试测的是别的东西。</summary>
    private const string NonAsciiDirName = "中文目录";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"lisscribe-nonascii-{Guid.NewGuid():N}");

    /// <summary>
    /// <b>junction 必须单独、非递归地删。</b><c>Directory.Delete(recursive: true)</c> 会去枚举
    /// 联接里的内容——那头是真模型（Qwen3 954MB），碰不得。非递归的删只摘掉重解析点本身。
    ///
    /// <para>清理失败一律吞掉：临时目录里的残留不值得把一条已经跑过的测试判红。</para>
    /// </summary>
    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var link in Directory.GetDirectories(_root))
        {
            TryDelete(() => Directory.Delete(link));
        }

        TryDelete(() => Directory.Delete(_root, recursive: true));
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Qwen3_LoadAsync_WhenTheModelSitsUnderANonAsciiPath_LoadsAndRecognizes()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        var link = LinkTo(Qwen3ModelFixture.ModelDir);

        await using var engine = new Qwen3AsrEngine(
            new AsrSection(AsrEngines.Qwen3Asr, link, "cpu"),
            NullLogger<Qwen3AsrEngine>.Instance);

        await engine.LoadAsync(CancellationToken.None);

        Assert.True(engine.IsLoaded);

        // **只判 IsLoaded 不够**：句柄建起来了而模型没读对，照样能绿。
        // 让它真解一段出来，这条断言才落在实处。
        var text = await engine.RecognizeAsync(
            Qwen3ModelFixture.ReadFirstSeconds(3), CancellationToken.None);

        Assert.NotEmpty(text);
    }

    [Fact]
    public async Task SenseVoice_LoadAsync_WhenTheModelSitsUnderANonAsciiPath_LoadsAndRecognizes()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        var link = LinkTo(ModelFixture.ModelDir);

        await using var engine = new SenseVoiceEngine(
            new AsrSection(AsrEngines.SenseVoice, link, "cpu"),
            NullLogger<SenseVoiceEngine>.Instance);

        await engine.LoadAsync(CancellationToken.None);

        Assert.True(engine.IsLoaded);

        var (samples, _) = ModelFixture.ReadPcm16Mono(ModelFixture.TestWavPath);
        var text = await engine.RecognizeAsync(samples, CancellationToken.None);

        Assert.NotEmpty(text);
    }

    /// <summary>
    /// 变通改的是**进程级**的当前工作目录，那是全进程共享的一格状态。
    /// 加载完不还原，后面所有相对路径就都落在模型目录里，而且没有任何征兆。
    /// </summary>
    [Fact]
    public async Task LoadAsync_UnderANonAsciiPath_LeavesTheWorkingDirectoryWhereItFoundIt()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        var link = LinkTo(Qwen3ModelFixture.ModelDir);
        var before = Directory.GetCurrentDirectory();

        await using var engine = new Qwen3AsrEngine(
            new AsrSection(AsrEngines.Qwen3Asr, link, "cpu"),
            NullLogger<Qwen3AsrEngine>.Instance);

        await engine.LoadAsync(CancellationToken.None);

        Assert.Equal(before, Directory.GetCurrentDirectory());
    }

    /// <summary>在临时目录下造一个名字含中文的 junction，指向真模型目录。</summary>
    private string LinkTo(string modelDir)
    {
        Directory.CreateDirectory(_root);

        var link = Path.Combine(_root, NonAsciiDirName);
        var target = Path.GetFullPath(modelDir, AppContext.BaseDirectory);

        // /J 是目录联接，不需要任何特权；/D 才是需要特权的符号链接，别写错。
        var process = Process.Start(new ProcessStartInfo(
            "cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        Assert.NotNull(process);

        process.WaitForExit();

        Assert.SkipUnless(Directory.Exists(link),
            $"junction 建不起来（mklink 退出码 {process.ExitCode}）：{process.StandardError.ReadToEnd()}");

        return link;
    }
}
