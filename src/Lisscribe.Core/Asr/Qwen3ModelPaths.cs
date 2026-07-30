namespace Lisscribe.Core.Asr;

/// <summary>
/// Qwen3-ASR 0.6B int8 的四件套定位。
/// 文件名来自 sherpa-onnx 官方包 <c>sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25</c>（解包后 982MB）：
/// <c>conv_frontend.onnx</c> 44MB + <c>encoder.int8.onnx</c> 182MB + <c>decoder.int8.onnx</c> 756MB
/// + <c>tokenizer/</c>（vocab.json + merges.txt + tokenizer_config.json）。
/// 与 SenseVoice 那套的差别不只是数量：tokenizer 是目录不是文件，而 <see cref="File.Exists"/>
/// 对目录恒返回 false，照抄过去会得到一个「模型明明齐了却永远报缺失」的程序。
/// </summary>
public sealed record Qwen3ModelPaths(
    string ConvFrontendFile, string EncoderFile, string DecoderFile, string TokenizerDir)
{
    public const string ConvFrontendFileName = "conv_frontend.onnx";
    public const string EncoderFileName = "encoder.int8.onnx";
    public const string DecoderFileName = "decoder.int8.onnx";
    public const string TokenizerDirName = "tokenizer";

    /// <summary>
    /// tokenizer 目录里唯一非有不可的那个。空目录也算缺失——解包中断、只拷了目录结构时就是这个
    /// 样子，而原生层拿着一个没有词表的目录会当场崩掉。
    /// </summary>
    public const string VocabFileName = "vocab.json";

    /// <summary>相对路径按 <see cref="AppContext.BaseDirectory"/> 展开，理由同 <see cref="AsrModelPaths.Resolve"/>。</summary>
    public static Qwen3ModelPaths Resolve(string modelDir) =>
        Under(Path.GetFullPath(modelDir, AppContext.BaseDirectory));

    /// <summary>
    /// 按给定根拼四个路径，不做任何展开。根为空串时拼出来就是纯文件名——
    /// <see cref="NativeModelDirectoryScope"/> 切了工作目录时要的正是这个。
    /// </summary>
    public static Qwen3ModelPaths Under(string root) =>
        new(Path.Combine(root, ConvFrontendFileName),
            Path.Combine(root, EncoderFileName),
            Path.Combine(root, DecoderFileName),
            Path.Combine(root, TokenizerDirName));

    /// <summary>三个 onnx，按官方包里的顺序。损坏自检要逐个走一遍。</summary>
    private IEnumerable<(string Path, string Name)> OnnxFiles =>
    [
        (ConvFrontendFile, ConvFrontendFileName),
        (EncoderFile, EncoderFileName),
        (DecoderFile, DecoderFileName),
    ];

    private string VocabFile => Path.Combine(TokenizerDir, VocabFileName);

    public bool IsComplete =>
        OnnxFiles.All(f => File.Exists(f.Path))
        && Directory.Exists(TokenizerDir)
        && File.Exists(VocabFile);

    /// <summary>
    /// 缺失文件的人话描述，直接显示给用户，也会原样进入配置校验的错误信息。
    /// 必须同时点名「少了哪个」与「在哪找的」——只说「模型未找到」会让用户对着一个自己从未
    /// 见过的目录猜路径。
    /// </summary>
    public string MissingDescription
    {
        get
        {
            var missing = OnnxFiles
                .Where(f => !File.Exists(f.Path))
                .Select(f => f.Name)
                .ToList();

            if (!Directory.Exists(TokenizerDir))
            {
                missing.Add($"{TokenizerDirName}/");
            }
            else if (!File.Exists(VocabFile))
            {
                missing.Add($"{TokenizerDirName}/{VocabFileName}");
            }

            return missing.Count == 0
                ? string.Empty
                : $"缺少模型文件：{string.Join("、", missing)}（目录 {Path.GetDirectoryName(ConvFrontendFile)}）。" +
                  "运行 scripts/fetch-models.ps1 -Engine qwen3 下载，或把 asr.modelDir 指向已有的模型目录。";
        }
    }

    /// <summary>
    /// 廉价的完整性自检，返回 null 表示「看起来没问题」。文件不存在时也返回 null——那是
    /// <see cref="MissingDescription"/> 的活，两条错误路径别抢话。
    /// 三个 onnx 每个都要查：只查其中一个，另外两个被截断时照样一路走到原生层。
    /// </summary>
    public string? DescribeCorruption()
    {
        if (!IsComplete)
        {
            return null;
        }

        var damage = OnnxFiles
            .Select(f => OnnxIntegrity.DescribeDamage(f.Path, f.Name))
            .FirstOrDefault(d => d is not null);

        return damage is null
            ? null
            : $"模型文件疑似损坏：{damage}。" +
              "多半是下载中断或拷贝未完成。请删除后重跑 scripts/fetch-models.ps1 -Engine qwen3 重新获取。";
    }
}
