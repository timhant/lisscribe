using System.Diagnostics;
using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 真模型测试。用 Trait 归入 RequiresModel 分类，CI 上与每次 PR 都跑的确定性测试分开。
/// </summary>
[Trait("Category", "RequiresModel")]
public sealed class SenseVoiceEngineTests
{
    private static AsrSection Config => new("SenseVoice", ModelFixture.ModelDir, "cpu");

    /// <summary>去标点、去空白后的字符错误率。</summary>
    private static double CharacterErrorRate(string expected, string actual)
    {
        static string Normalize(string s) =>
            new(s.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToArray());

        var e = Normalize(expected);
        var a = Normalize(actual);

        if (e.Length == 0)
        {
            return a.Length == 0 ? 0 : 1;
        }

        // Levenshtein 距离。
        var prev = new int[a.Length + 1];
        var curr = new int[a.Length + 1];
        for (var j = 0; j <= a.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= e.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= a.Length; j++)
            {
                var cost = e[i - 1] == a[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[a.Length] / (double)e.Length;
    }

    [Fact]
    public async Task RepointModelDir_AfterLoad_IsRefusedInsteadOfLyingAboutWhatIsRunning()
    {
        // 已加载还允许换址的话，引擎会进入「配置说的是 A、跑着的是 B」这种查无可查的状态。
        // 组合根据此把「已加载 + 目录变了」判成「须重启」，而不是就地热换。
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        await using var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);
        await engine.LoadAsync(CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => engine.RepointModelDir("D:/somewhere/else"));
    }

    [Fact]
    public async Task LoadAsync_WithRealModel_Succeeds()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        await using var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);

        await engine.LoadAsync(CancellationToken.None);

        Assert.True(engine.IsLoaded);
        Assert.Equal("SenseVoice", engine.EngineName);
    }

    /// <summary>
    /// 重复 LoadAsync 必须是空操作。若每次都 new 一个 OfflineRecognizer，旧的原生句柄就此失联，
    /// 配置窗每保存一次泄掉几百 MB 常驻内存。
    /// </summary>
    [Fact]
    public async Task LoadAsync_CalledTwice_KeepsWorkingAndDoesNotReplaceRecognizer()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        await using var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);
        await engine.LoadAsync(CancellationToken.None);
        await engine.LoadAsync(CancellationToken.None);

        var (samples, _) = ModelFixture.ReadPcm16Mono(ModelFixture.TestWavPath);

        Assert.True(engine.IsLoaded);
        Assert.NotEmpty(await engine.RecognizeAsync(samples, CancellationToken.None));
    }

    [Fact]
    public async Task RecognizeAsync_OnReferenceWav_StaysUnderCerThreshold()
    {
        // 断言用归一化 CER 阈值而非精确字符串：精确断言会因模型版本、ITN 开关、
        // 标点策略变化而红，一次模型升级把测试全打红，团队随即开始 skip。
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        await using var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);
        await engine.LoadAsync(CancellationToken.None);
        var (samples, _) = ModelFixture.ReadPcm16Mono(ModelFixture.TestWavPath);

        var text = await engine.RecognizeAsync(samples, CancellationToken.None);

        var cer = CharacterErrorRate("开放时间早上9点至下午5点", text);
        Assert.True(cer < 0.15, $"CER {cer:F3} 超阈值。识别结果：\"{text}\"");
    }

    [Fact]
    public async Task RecognizeAsync_ProducesArabicNumeralsViaItn()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        await using var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);
        await engine.LoadAsync(CancellationToken.None);
        var (samples, _) = ModelFixture.ReadPcm16Mono(ModelFixture.TestWavPath);

        var text = await engine.RecognizeAsync(samples, CancellationToken.None);

        // UseInverseTextNormalization = 1 应让「九点」变成「9点」。
        Assert.Contains('9', text);
    }

    /// <summary>
    /// 识别结果不得残留 SenseVoice 的元信息标记（&lt;|zh|&gt;&lt;|NEUTRAL|&gt;… 之类）。
    /// 这些标记若漏出去会被 SendInput 原样敲进用户的编辑器。
    /// </summary>
    [Fact]
    public async Task RecognizeAsync_StripsSenseVoiceMetaTags()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        await using var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);
        await engine.LoadAsync(CancellationToken.None);
        var (samples, _) = ModelFixture.ReadPcm16Mono(ModelFixture.TestWavPath);

        var text = await engine.RecognizeAsync(samples, CancellationToken.None);

        Assert.DoesNotContain("<|", text);
    }

    /// <summary>
    /// 回归护栏，<b>不是验收</b>（验收口径与取数用具是 <see cref="EndToEndLatencyBenchmark"/>）。
    /// 0.3 这条线只拦「不小心跑成单线程」「误用 fp32」这类量级退化。
    ///
    /// <para>
    /// <b>取三次里最快的那次，不取单次</b>：单次采样配 0.3 的阈值会偶发越线——多个测试项目并行
    /// 抢 CPU 时实测撞到过 0.3041，而同机同样本的 P95 只有 0.110。最小值对这种抖动免疫，
    /// 真退化时它照样过不去。
    /// </para>
    /// </summary>
    [Fact]
    public async Task RecognizeAsync_MeetsRealTimeFactorBudget()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        await using var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);
        await engine.LoadAsync(CancellationToken.None);
        var (samples, rate) = ModelFixture.ReadPcm16Mono(ModelFixture.TestWavPath);

        await engine.RecognizeAsync(samples, CancellationToken.None); // 预热

        var seconds = samples.Length / (double)rate;
        var best = double.MaxValue;

        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            await engine.RecognizeAsync(samples, CancellationToken.None);
            sw.Stop();

            best = Math.Min(best, sw.Elapsed.TotalSeconds / seconds);
        }

        Assert.True(best < 0.3, $"三次里最快的 RTF {best:F4} 仍超预算");
    }

    /// <summary>
    /// 推理在途时释放原生句柄 = 进程级 AccessViolation，try/catch 拦不住，测试宿主直接死。
    /// 状态机那侧靠「队列 worker 退出后才 dispose」避开，这里验证引擎自身也扛得住：
    /// DisposeAsync 必须等在途推理走完，且在途那次要么正常返回、要么抛 ObjectDisposedException。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_DuringInFlightRecognize_WaitsInsteadOfCrashing()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);
        await engine.LoadAsync(CancellationToken.None);

        // 单条样本百来毫秒就解完，重复拼接拉长解码窗口，好让 Dispose 真撞上在途推理。
        var (single, _) = ModelFixture.ReadPcm16Mono(ModelFixture.TestWavPath);
        var samples = Enumerable.Repeat(single, 4).SelectMany(s => s).ToArray();

        var inFlight = Task.Run(() => engine.RecognizeAsync(samples, CancellationToken.None));
        await Task.Delay(20, TestContext.Current.CancellationToken); // 让推理先进闸门

        await engine.DisposeAsync();

        var outcome = await Record.ExceptionAsync(() => inFlight);
        Assert.True(
            outcome is null or ObjectDisposedException,
            $"在途识别抛了意料之外的 {outcome?.GetType().Name}：{outcome?.Message}");
        Assert.False(engine.IsLoaded);
    }

    /// <summary>
    /// 损坏检测最要命的失败方向不是漏报而是<b>误报</b>：真模型若被判成损坏，所有人都用不了。
    /// 故必须拿真权重文件验一次「不报警」。
    /// </summary>
    [Fact]
    public void DescribeCorruption_OnIntactRealModel_IsNull()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        Assert.Null(AsrModelPaths.Resolve(ModelFixture.ModelDir).DescribeCorruption());
    }

    /// <summary>
    /// 用真模型的前 2.4MB 做「下载到 1% 就断线」的复现：文件头完好、producer 字段齐全，
    /// 唯独 graph 那个巨型变长字段的声明长度远超剩余字节。合成用例覆盖不到这个角度——
    /// 它证明检测不是只认得我们自己造的假 protobuf。只拷前缀，不拷整份权重。
    /// </summary>
    [Fact]
    public async Task LoadAsync_WithTruncatedRealModel_ThrowsInsteadOfKillingProcess()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        var dir = Directory.CreateTempSubdirectory("lisscribe-trunc").FullName;
        try
        {
            var real = AsrModelPaths.Resolve(ModelFixture.ModelDir);
            await using (var source = File.OpenRead(real.ModelFile))
            await using (var sink = File.Create(Path.Combine(dir, AsrModelPaths.ModelFileName)))
            {
                var prefix = new byte[2_400_000];
                var read = await source.ReadAsync(prefix, TestContext.Current.CancellationToken);
                await sink.WriteAsync(prefix.AsMemory(0, read), TestContext.Current.CancellationToken);
            }

            File.Copy(real.TokensFile, Path.Combine(dir, AsrModelPaths.TokensFileName));

            await using var engine = new SenseVoiceEngine(
                new AsrSection("SenseVoice", dir, "cpu"), NullLogger<SenseVoiceEngine>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => engine.LoadAsync(CancellationToken.None));

            Assert.Contains("fetch-models.ps1", ex.Message);
            Assert.False(engine.IsLoaded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 取消在原生加载启动后到达时的诚实行为：原生构造无法中断，但<b>不能假装成功</b>。
    /// 调用方撤了单，就该收到 OperationCanceledException 且引擎保持未加载 ——
    /// 否则一个「取消了却仍然加载完成」的引擎会让上层的状态机彻底对不上账。
    /// </summary>
    [Fact]
    public async Task LoadAsync_CancelledMidFlight_ThrowsAndLeavesEngineUnloaded()
    {
        Assert.SkipUnless(ModelFixture.IsAvailable, ModelFixture.SkipReason);

        await using var engine = new SenseVoiceEngine(Config, NullLogger<SenseVoiceEngine>.Instance);
        using var cts = new CancellationTokenSource();

        var loading = engine.LoadAsync(cts.Token);
        await Task.Delay(5, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.False(engine.IsLoaded);
    }
}

/// <summary>
/// 不依赖模型的守卫测试。这些是每次 PR 都要跑的确定性用例，故不打 RequiresModel Trait ——
/// 若与真模型测试同处一类，`--filter-not-trait Category=RequiresModel` 会把它们一并滤掉，
/// 而「provider 静默回落」这类回归恰恰最需要在无模型的 CI 上守住。
/// </summary>
public sealed class SenseVoiceEngineGuardTests
{
    /// <summary>
    /// 必须是「本平台意义上的绝对路径」：<see cref="AsrModelPaths.Resolve"/> 走的是
    /// <c>Path.GetFullPath(dir, AppContext.BaseDirectory)</c>，而 "/definitely/not/here" 这种
    /// POSIX 绝对路径在 Windows 上会被补上当前驱动器改写成 "E:\definitely\not\here" ——
    /// 于是「错误消息里必须带着用户给的那个目录」这条断言在 Windows 上凭空落空，
    /// 而它守的恰恰是本用例唯一的价值。临时目录在两个平台上都会原样穿过 GetFullPath；
    /// GUID 保证它必不存在（本组用例有若干条正是靠「目录不存在」立论的）。
    /// </summary>
    private static readonly string MissingModelDir =
        Path.Combine(Path.GetTempPath(), $"lisscribe-missing-model-{Guid.NewGuid():N}");

    private static SenseVoiceEngine CreateEngine(string modelDir, string provider = "cpu") =>
        new(new AsrSection("SenseVoice", modelDir, provider), NullLogger<SenseVoiceEngine>.Instance);

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
        // 引擎加载失败之后，用户在配置窗里把目录改对——这是那条自救路径的落点。
        await using var engine = CreateEngine(MissingModelDir);
        var elsewhere = Path.Combine(Path.GetTempPath(), $"lisscribe-repointed-{Guid.NewGuid():N}");

        engine.RepointModelDir(elsewhere);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => engine.LoadAsync(CancellationToken.None));

        Assert.Contains(elsewhere, ex.Message);

        // 阴性对照：旧目录不许再出现。少了这一句，「换址其实没生效、报的还是老地方」
        // 也能让上一句绿——只要新旧两个目录都不存在。
        Assert.DoesNotContain(MissingModelDir, ex.Message);
    }

    [Fact]
    public async Task LoadAsync_WhenModelFilesMissing_ThrowsWithActionableMessage()
    {
        await using var engine = CreateEngine(MissingModelDir);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => engine.LoadAsync(CancellationToken.None));

        Assert.Contains("model.int8.onnx", ex.Message);
        Assert.Contains(MissingModelDir, ex.Message);
        Assert.False(engine.IsLoaded);
    }

    [Fact]
    public async Task LoadAsync_WithUnsupportedProvider_ThrowsInsteadOfSilentlyFallingBack()
    {
        // 绝不静默回落：sherpa-onnx 收到无法满足的 provider 只打条日志然后用 CPU，用户会以为
        // 自己在跑 GPU。当前只支持 cpu，别的一律显式拒绝。拿 directml 举例是因为官方 NuGet
        // 根本没编入该 EP，正是「配了等于没配」的典型。
        await using var engine = CreateEngine(ModelFixture.ModelDir, "directml");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => engine.LoadAsync(CancellationToken.None));

        Assert.Contains("directml", ex.Message);
        Assert.False(engine.IsLoaded);
    }

    [Fact]
    public async Task DisposeAsync_WithoutLoad_DoesNotThrow()
    {
        var engine = CreateEngine(MissingModelDir);

        await engine.DisposeAsync();

        Assert.False(engine.IsLoaded);
    }

    /// <summary>
    /// 重复 Dispose 必须是空操作：`await using` 之外还有关停路径会显式 DisposeAsync，
    /// 第二次不能炸。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        var engine = CreateEngine(MissingModelDir);

        await engine.DisposeAsync();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task RecognizeAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var engine = CreateEngine(MissingModelDir);
        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.RecognizeAsync(new float[16000], CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var engine = CreateEngine(MissingModelDir);
        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.LoadAsync(CancellationToken.None));
    }

    /// <summary>
    /// 损坏的模型必须在进原生加载器之前被拦下，抛一个**能 catch 的**异常。
    /// 放过去的话 sherpa-onnx 会让原生 Ort::Exception 直接逃逸，进程 SIGABRT，
    /// try/catch 一样拦不住 —— 和 dispose 竞态是同一种「catch 不到的进程死亡」，只是换了个门。
    /// 本用例走合成 protobuf，不依赖真权重，CI 上每次 PR 都能跑。
    /// </summary>
    [Fact]
    public async Task LoadAsync_WithCorruptModel_ThrowsCatchableExceptionInsteadOfKillingProcess()
    {
        var dir = Directory.CreateTempSubdirectory("lisscribe-corrupt").FullName;
        try
        {
            // 变长字段声明 100 字节，文件里只剩 3 —— 与下载中断截断尾巴的形状一致。
            File.WriteAllBytes(
                Path.Combine(dir, "model.int8.onnx"),
                [0x08, 0x07, 0x12, 0x64, (byte)'a', (byte)'b', (byte)'c']);
            File.WriteAllText(Path.Combine(dir, "tokens.txt"), "<blk> 0\n");

            await using var engine = CreateEngine(dir);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => engine.LoadAsync(CancellationToken.None));

            Assert.Contains("fetch-models.ps1", ex.Message);
            Assert.False(engine.IsLoaded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 已取消的 token 应当在做任何事之前就被观察到 —— 这里连模型目录都不存在，
    /// 若先抛 FileNotFoundException 就说明 token 没被看在眼里。
    /// </summary>
    [Fact]
    public async Task LoadAsync_WithAlreadyCancelledToken_ThrowsOperationCanceled()
    {
        await using var engine = CreateEngine(MissingModelDir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // 用 ThrowsAnyAsync：取消可能来自 ThrowIfCancellationRequested（OperationCanceledException）
        // 也可能来自信号量的取消路径（TaskCanceledException），二者都是合格的「已取消」。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAsync(cts.Token));
        Assert.False(engine.IsLoaded);
    }
}
