using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Lisscribe.Core.Asr;

/// <summary>
/// SenseVoice int8 引擎（默认档）。
///
/// 实测（i7-13xxx / Win11 22631，5.59s 中文 wav，4 线程）：加载约 1.1–1.7s，
/// 单次解码 266–278ms（RTF 0.048），常驻 RSS 326MB。
/// 8 线程仅快约一成，故 <see cref="OfflineRecognizerEngine.NumThreads"/> 写死 4。
///
/// <para>句柄生命周期、闸门、取消边界全在基类，见 <see cref="OfflineRecognizerEngine"/>。</para>
/// </summary>
public sealed class SenseVoiceEngine(AsrSection config, ILogger<SenseVoiceEngine> logger)
    : OfflineRecognizerEngine(config, logger)
{
    public override string EngineName => AsrEngines.SenseVoice;

    protected override void ValidateModelFiles(string modelDir)
    {
        var paths = AsrModelPaths.Resolve(modelDir);

        if (!paths.IsComplete)
        {
            throw new FileNotFoundException(paths.MissingDescription);
        }

        if (paths.DescribeCorruption() is { } corruption)
        {
            throw new InvalidDataException(corruption);
        }
    }

    protected override OfflineRecognizerConfig BuildRecognizerConfig(string nativeRoot)
    {
        // Under 而非 Resolve：根由作用域给，已经是它要的形状，再展开一次就废了那次变通。
        var paths = AsrModelPaths.Under(nativeRoot);
        var recognizerConfig = new OfflineRecognizerConfig();

        recognizerConfig.ModelConfig.SenseVoice.Model = paths.ModelFile;

        // 逆文本正则化：把「早上九点」输出成「早上9点」。语音输入场景下用户要的是
        // 能直接用的文本，不是转写稿。
        recognizerConfig.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        recognizerConfig.ModelConfig.Tokens = paths.TokensFile;
        recognizerConfig.ModelConfig.NumThreads = NumThreads;
        recognizerConfig.ModelConfig.Provider = Provider;
        recognizerConfig.ModelConfig.Debug = 0;

        // SenseVoice 是 CTC 模型，没有可用的外部语言模型，greedy 与 beam 结果一致而更快。
        recognizerConfig.DecodingMethod = "greedy_search";

        return recognizerConfig;
    }
}
