using Lisscribe.Core.Asr;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// Qwen3 真模型测试的前置条件。模型包 982MB 不入 Git（体积 + 权重许可另计），
/// 缺了就 Skip 而非 Fail —— 一次缓存失效不该把 PR 全打红。
/// </summary>
public static class Qwen3ModelFixture
{
    /// <summary>官方压缩包解包后的原始目录名，见 scripts/fetch-models.ps1。</summary>
    private const string OfficialArchiveDirName = "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25";

    /// <summary>
    /// 包里自带的长样本，50.96s 真实密集中文语音（歌词），16kHz 单声道。
    ///
    /// <para>
    /// **选它不选 sense-voice 那条 zh.wav，是因为长度上限的测试非真实长语音不可。**
    /// 把 5.59s 的短句拼十遍得到的是低熵重复音频，模型在上面撑到 33s 才垮，
    /// 而真实语音 27s 就开始丢内容——拿拼接音频定上限会定出一个偏大的数。
    /// </para>
    /// </summary>
    private const string ReferenceWavName = "qiqiu1.wav";

    private static readonly Lazy<string> ResolvedModelDir = new(ProbeModelDir);

    public static string ModelDir => ResolvedModelDir.Value;

    public static bool IsAvailable => IsUsableModelDir(ModelDir);

    public static bool IsUsableModelDir(string dir) =>
        Qwen3ModelPaths.Resolve(dir).IsComplete && File.Exists(WavPathIn(dir));

    public static string SkipReason
    {
        get
        {
            var paths = Qwen3ModelPaths.Resolve(ModelDir);
            var reason = paths.IsComplete
                ? $"缺少 test_wavs/{ReferenceWavName}"
                : paths.MissingDescription;

            return $"Qwen3 模型不可用（{ModelDir}）：{reason} 运行 scripts/fetch-models.ps1 -Engine qwen3 后重试。";
        }
    }

    public static string TestWavPath => WavPathIn(ModelDir);

    /// <summary>取参考样本的前 <paramref name="seconds"/> 秒。长度上限的测试全靠它裁。</summary>
    public static float[] ReadFirstSeconds(double seconds)
    {
        var (samples, rate) = ModelFixture.ReadPcm16Mono(TestWavPath);
        var wanted = (int)(seconds * rate);

        return wanted >= samples.Length ? samples : samples[..wanted];
    }

    private static string WavPathIn(string dir) =>
        Path.Combine(Path.GetFullPath(dir, AppContext.BaseDirectory), "test_wavs", ReferenceWavName);

    private static string ProbeModelDir()
    {
        var models = Path.Combine(FindRepoRoot(), "models");
        var canonical = Path.Combine(models, "qwen3-asr");
        var archive = Path.Combine(models, OfficialArchiveDirName);

        // 探测与可用性判定必须用同一把尺子，否则会挑中一个「有权重没 wav」的目录，
        // 然后在 IsAvailable 上判 false —— 明明隔壁那个官方解包目录是全的。
        return IsUsableModelDir(canonical) || !IsUsableModelDir(archive)
            ? canonical
            : archive;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "lisscribe.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
