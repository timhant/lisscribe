using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// Qwen3 真模型测试。归入 RequiresModel 分类，与每次 PR 都跑的确定性测试分开。
/// </summary>
[Trait("Category", "RequiresModel")]
[Collection(ProcessWorkingDirectoryCollection.Name)]
public sealed class Qwen3AsrEngineTests
{
    private static AsrSection Config =>
        new(AsrEngines.Qwen3Asr, Qwen3ModelFixture.ModelDir, "cpu");

    private static Qwen3AsrEngine CreateEngine() =>
        new(Config, NullLogger<Qwen3AsrEngine>.Instance);

    [Fact]
    public async Task LoadAsync_WithRealModel_Succeeds()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        await using var engine = CreateEngine();

        await engine.LoadAsync(CancellationToken.None);

        Assert.True(engine.IsLoaded);
        Assert.Equal("Qwen3Asr", engine.EngineName);
    }

    /// <summary>
    /// 损坏检测最要命的失败方向不是漏报而是**误报**：真模型若被判成损坏，所有人都用不了。
    /// 故必须拿真的那三个 onnx 验一次「不报警」。
    /// </summary>
    [Fact]
    public void DescribeCorruption_OnIntactRealModel_IsNull()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        Assert.Null(Qwen3ModelPaths.Resolve(Qwen3ModelFixture.ModelDir).DescribeCorruption());
    }

    [Fact]
    public async Task RecognizeAsync_OnRealSpeech_ProducesTextWithItsOwnPunctuation()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        await using var engine = CreateEngine();
        await engine.LoadAsync(CancellationToken.None);

        var text = await engine.RecognizeAsync(
            Qwen3ModelFixture.ReadFirstSeconds(10), CancellationToken.None);

        Assert.NotEmpty(text);

        // 自带标点是本档相对 SenseVoice 的卖点之一，也是上层不给它套
        // TrailingPunctuationTrimmer 之外那套补标点逻辑的前提。
        Assert.Contains(text, c => c is '，' or '。' or '、');
    }

    /// <summary>
    /// <b>换到高精度档会丢掉逆文本正则化（ITN）——同一句话，数字的写法变了。</b>
    ///
    /// <para>
    /// SenseVoice 显式开了 <c>UseInverseTextNormalization = 1</c>，「早上九点」上屏成「早上9点」；
    /// Qwen3 的配置里**没有这个开关**，输出保持汉字数字。语音输入要的是能直接用的文本，
    /// 故这是一处真实的行为差异，不是审美问题。
    /// </para>
    ///
    /// <para>
    /// <b>且那条明摆着的补救路子是死的：<c>RuleFsts</c> 对 Qwen3 完全不生效。</b>
    /// 实测官方 <c>itn_zh_number.fst</c>：同一条 wav，Qwen3 挂不挂这个 FST 输出<b>逐字节相同</b>；
    /// 而同一份 FST 挂到关掉自带 ITN 的 SenseVoice 上，「幺三五八幺八八七五」当场变成
    /// 「幺358幺8875」——FST 是好的，是 Qwen3 那条实现根本不调它。
    /// <b>谁想靠 RuleFsts 给本档补 ITN，先看这段，别再花一天。</b> 真要补只能自己写中文 ITN。
    /// </para>
    ///
    /// <para>
    /// 本条是特征化测试：哪天上游给 Qwen3 补上 ITN 或让 RuleFsts 生效，它会转红，那时再回头改。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Qwen3_KeepsChineseNumerals_WhereSenseVoiceWouldNormaliseThem()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        // 参考句「开放时间早上九点至下午五点」出自 SenseVoice 那包的 test_wavs。
        // **另一半在 SenseVoiceEngineTests.RecognizeAsync_ProducesArabicNumeralsViaItn**：
        // 同一条 wav 上它断言输出含 '9'。两条合起来才说明差异真在引擎，而不是这条样本没数字。
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        var (samples, _) = ModelFixture.ReadPcm16Mono(ModelFixture.TestWavPath);

        await using var engine = CreateEngine();
        await engine.LoadAsync(CancellationToken.None);

        var text = await engine.RecognizeAsync(samples, CancellationToken.None);

        Assert.Contains('九', text);
        Assert.DoesNotContain('9', text);
    }

    /// <summary>
    /// <b>录音上限那个 25 秒是有依据的，这条就是依据。</b>
    ///
    /// <para>
    /// 喂满 <see cref="AsrEngines.MaxRecordingSeconds"/> 那么长的真实语音，输出必须仍是完整的一大段。
    /// 本机实测该点输出 157 个汉字；阈值取 100 留足余量，只拦「垮掉」不拦「差几个字」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AtTheRecordingCeiling_TheOutputIsStillWhole()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        await using var engine = CreateEngine();
        await engine.LoadAsync(CancellationToken.None);

        var ceiling = AsrEngines.MaxRecordingSeconds(AsrEngines.Qwen3Asr);
        var text = await engine.RecognizeAsync(
            Qwen3ModelFixture.ReadFirstSeconds(ceiling), CancellationToken.None);

        Assert.True(
            text.Length >= 100,
            $"{ceiling}s 真实语音只识别出 {text.Length} 字，上限该往下调了。结果：\"{text}\"");
    }

    /// <summary>
    /// <b>特征化测试：记录「超了上限会怎样」这件当前为真的坏事。</b>
    ///
    /// <para>
    /// 38s 时输出会塌成个位数汉字——不报错、不抛异常，只是字没了。这条把那个事实钉住：
    /// 哪天 sherpa-onnx 或模型把它修好了，本条转红，那时才该回头把 25s 往上调。
    /// **在此之前，谁想调大 <see cref="AsrEngines.MaxRecordingSeconds"/> 都得先让这条红给他看。**
    /// </para>
    ///
    /// <para>
    /// 38s 这个点在 512 token 的硬上限（约 38.2s）之内，故它塌的原因不是那个上限。
    /// </para>
    /// </summary>
    [Fact]
    public async Task PastTheCeiling_TheOutputCollapsesSilently_WhichIsWhyTheCeilingExists()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        await using var engine = CreateEngine();
        await engine.LoadAsync(CancellationToken.None);

        var text = await engine.RecognizeAsync(
            Qwen3ModelFixture.ReadFirstSeconds(38), CancellationToken.None);

        Assert.True(
            text.Length <= 20,
            $"38s 现在能识别出 {text.Length} 字了——若稳定复现，说明上限可以放宽，" +
            $"请重测 27/30/33/35/38s 后再动那个常量。结果：\"{text}\"");
    }

    /// <summary>
    /// 推理在途时释放原生句柄 = 进程级 AccessViolation，try/catch 拦不住。
    /// 骨架在基类，但本档的解码窗口长得多（RTF 约 0.4–0.6），正好是撞得最准的一档。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_DuringInFlightRecognize_WaitsInsteadOfCrashing()
    {
        Assert.SkipUnless(Qwen3ModelFixture.IsAvailable, Qwen3ModelFixture.SkipReason);

        var engine = CreateEngine();
        await engine.LoadAsync(CancellationToken.None);

        var samples = Qwen3ModelFixture.ReadFirstSeconds(20);
        var inFlight = Task.Run(() => engine.RecognizeAsync(samples, CancellationToken.None));
        await Task.Delay(200, TestContext.Current.CancellationToken); // 让推理先进闸门

        await engine.DisposeAsync();

        var outcome = await Record.ExceptionAsync(() => inFlight);

        Assert.True(
            outcome is null or ObjectDisposedException,
            $"在途识别抛了意料之外的 {outcome?.GetType().Name}：{outcome?.Message}");
        Assert.False(engine.IsLoaded);
    }
}
