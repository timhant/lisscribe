using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 不依赖 982MB 权重的守卫测试。
///
/// <para>
/// <b>为什么要单独写一遍，而不是「基类测过了就算」。</b>这几条守的都是「坏输入必须在递给原生
/// 加载器之前被拦下」——拦不住就是进程级崩溃，无窗口无日志。而拦截逻辑一半在基类
/// （<c>LoadAsync</c> 的调用顺序），一半在本档自己的 <c>ValidateModelFiles</c> 里。
/// 只测 SenseVoice 那份，等于假设新档也把它实现对了。
/// </para>
/// </summary>
public sealed class Qwen3AsrEngineGuardTests
{
    /// <summary>
    /// 必须是「本平台意义上的绝对路径」，理由见 <c>SenseVoiceEngineGuardTests</c> 里同名字段的注释：
    /// POSIX 风格的 "/nope" 在 Windows 上会被补上驱动器改写，于是「错误消息里带着用户给的目录」
    /// 这条断言凭空落空。
    /// </summary>
    private static readonly string MissingModelDir =
        Path.Combine(Path.GetTempPath(), $"lisscribe-missing-qwen3-{Guid.NewGuid():N}");

    private static Qwen3AsrEngine CreateEngine(string modelDir, string provider = "cpu") =>
        new(new AsrSection(AsrEngines.Qwen3Asr, modelDir, provider),
            NullLogger<Qwen3AsrEngine>.Instance);

    [Fact]
    public async Task LoadAsync_WhenModelFilesMissing_ThrowsWithActionableMessage()
    {
        await using var engine = CreateEngine(MissingModelDir);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => engine.LoadAsync(CancellationToken.None));

        // 三个 onnx 都得点名——只报一个的话，用户补上它再跑，又撞同一堵墙。
        Assert.Contains("conv_frontend.onnx", ex.Message);
        Assert.Contains("encoder.int8.onnx", ex.Message);
        Assert.Contains("decoder.int8.onnx", ex.Message);
        Assert.Contains("tokenizer", ex.Message);
        Assert.Contains(MissingModelDir, ex.Message);
        Assert.False(engine.IsLoaded);
    }

    /// <summary>
    /// 损坏的模型必须抛一个**能 catch 的**异常。放过去的话 sherpa-onnx 会让原生
    /// <c>Ort::Exception</c> 直接逃逸，进程当场没了，try/catch 一样拦不住。
    /// 走合成 protobuf，故不依赖那 982MB 权重。
    /// </summary>
    [Fact]
    public async Task LoadAsync_WithCorruptModel_ThrowsCatchableExceptionInsteadOfKillingProcess()
    {
        var dir = Directory.CreateTempSubdirectory("lisscribe-qwen3-corrupt").FullName;

        try
        {
            WriteWholeModel(dir);

            // 变长字段声明 100 字节，文件里只剩 3 —— 与下载中断截掉尾巴的形状一致。
            File.WriteAllBytes(
                Path.Combine(dir, Qwen3ModelPaths.DecoderFileName),
                [0x08, 0x07, 0x12, 0x64, (byte)'a', (byte)'b', (byte)'c']);

            await using var engine = CreateEngine(dir);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => engine.LoadAsync(CancellationToken.None));

            Assert.Contains(Qwen3ModelPaths.DecoderFileName, ex.Message);
            Assert.Contains("fetch-models.ps1", ex.Message);
            Assert.False(engine.IsLoaded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithUnsupportedProvider_ThrowsInsteadOfSilentlyFallingBack()
    {
        // 绝不静默回落：sherpa-onnx 收到无法满足的 provider 只会打条日志然后用 CPU，
        // 用户会以为自己在跑 GPU。
        //
        // **模型目录故意给一个不存在的**：这样断言才能同时证明 provider 检查排在文件检查之前。
        // 顺序反了的话用户改了 provider 却收到「模型缺失」，被引到完全错误的方向。
        await using var engine = CreateEngine(MissingModelDir, "cuda");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => engine.LoadAsync(CancellationToken.None));

        Assert.Contains("cuda", ex.Message);
        Assert.False(engine.IsLoaded);
    }

    [Fact]
    public async Task RecognizeAsync_BeforeLoad_Throws()
    {
        await using var engine = CreateEngine(MissingModelDir);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RecognizeAsync(new float[16000], CancellationToken.None));
    }

    [Fact]
    public async Task RepointModelDir_BeforeLoad_MakesTheNextLoadLookInTheNewPlace()
    {
        await using var engine = CreateEngine(MissingModelDir);
        var elsewhere = Path.Combine(Path.GetTempPath(), $"lisscribe-qwen3-repointed-{Guid.NewGuid():N}");

        engine.RepointModelDir(elsewhere);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => engine.LoadAsync(CancellationToken.None));

        Assert.Contains(elsewhere, ex.Message);

        // 阴性对照：旧目录不许再出现。少了这一句，「换址其实没生效」也能让上一句绿
        // ——只要新旧两个目录都不存在。
        Assert.DoesNotContain(MissingModelDir, ex.Message);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        var engine = CreateEngine(MissingModelDir);

        await engine.DisposeAsync();
        await engine.DisposeAsync();

        Assert.False(engine.IsLoaded);
    }

    [Fact]
    public async Task LoadAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var engine = CreateEngine(MissingModelDir);
        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.LoadAsync(CancellationToken.None));
    }

    /// <summary>造一个「合法但极小」的 ONNX 式 protobuf：字段 1 varint + 字段 2 变长。</summary>
    private static void WriteWholeModel(string dir)
    {
        byte[] minimalOnnx = [0x08, 0x07, 0x12, 0x03, (byte)'a', (byte)'b', (byte)'c'];

        foreach (var name in new[]
                 {
                     Qwen3ModelPaths.ConvFrontendFileName,
                     Qwen3ModelPaths.EncoderFileName,
                     Qwen3ModelPaths.DecoderFileName,
                 })
        {
            File.WriteAllBytes(Path.Combine(dir, name), minimalOnnx);
        }

        var tokenizer = Directory.CreateDirectory(
            Path.Combine(dir, Qwen3ModelPaths.TokenizerDirName));

        File.WriteAllText(Path.Combine(tokenizer.FullName, Qwen3ModelPaths.VocabFileName), "{}");
    }
}
