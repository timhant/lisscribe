using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Lisscribe.Core.Asr;

/// <summary>
/// Qwen3-ASR 0.6B int8 引擎（高精度档）：自带标点，粤语 / 中英混说 / 嘈杂环境数字串明显强于
/// SenseVoice，代价是模型包 982MB、内存与耗时都高一个量级，且延迟远达不到默认档的水平。
/// </summary>
/// <remarks>
/// 两条硬伤。其一，<see cref="MaxTotalLen"/> 的上限就是模型上限 512，调不上去：audio token
/// 每秒 13 个加 15 个 prompt token，约 38.2s 撞满，原生层打印
/// <c>context_len (N) exceeds max_total_len (512)</c>。其二，<b>它在撞到 512 之前很久就开始丢
/// 内容且毫无征兆</b>——不报错、不抛异常，只是上屏的字变少。故本档录音上限由
/// <see cref="AsrEngines.MaxRecordingSeconds"/> 单独给 25s（曲线见那里），宁可少录不可静默丢字。
/// </remarks>
public sealed class Qwen3AsrEngine(AsrSection config, ILogger<Qwen3AsrEngine> logger)
    : OfflineRecognizerEngine(config, logger)
{
    /// <summary>模型自身的上限，不是挑的数：填更大的值 sherpa-onnx 会夹回来。</summary>
    public const int MaxTotalLen = 512;

    public override string EngineName => AsrEngines.Qwen3Asr;

    protected override void ValidateModelFiles(string modelDir)
    {
        var paths = Qwen3ModelPaths.Resolve(modelDir);

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
        var paths = Qwen3ModelPaths.Under(nativeRoot);
        var recognizerConfig = new OfflineRecognizerConfig();

        recognizerConfig.ModelConfig.Qwen3Asr.ConvFrontend = paths.ConvFrontendFile;
        recognizerConfig.ModelConfig.Qwen3Asr.Encoder = paths.EncoderFile;
        recognizerConfig.ModelConfig.Qwen3Asr.Decoder = paths.DecoderFile;
        recognizerConfig.ModelConfig.Qwen3Asr.Tokenizer = paths.TokenizerDir;
        recognizerConfig.ModelConfig.Qwen3Asr.MaxTotalLen = MaxTotalLen;

        // 输出长度上限，默认 128。给一档余量：撞上限的样子是句子被拦腰砍掉，没有任何报错。
        recognizerConfig.ModelConfig.Qwen3Asr.MaxNewTokens = MaxTotalLen / 2;

        // 本档没有、也接不上逆文本正则化：SenseVoice 靠 UseInverseTextNormalization 出「早上9点」，
        // Qwen3 无此开关，出「早上九点」。recognizer 层的 RuleFsts 补不上——同一份官方
        // itn_zh_number.fst 挂到 SenseVoice 立刻生效、挂到本档输出逐字节不变，是 sherpa-onnx
        // 1.13.4 的 Qwen3 实现根本不调它。别再试这条路，真要补只能自写中文 ITN。

        // Qwen3 的 tokenizer 目录里自带词表，不走 ModelConfig.Tokens 那条路。
        recognizerConfig.ModelConfig.NumThreads = NumThreads;
        recognizerConfig.ModelConfig.Provider = Provider;
        recognizerConfig.ModelConfig.Debug = 0;

        return recognizerConfig;
    }
}
