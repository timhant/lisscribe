namespace Lisscribe.Core.Asr;

/// <summary>
/// 两档识别引擎的公共事实：名字、默认模型目录、录音上限。
/// 名字就是 <c>config.json</c> 里 <c>asr.engine</c> 写的那个字符串，大小写敏感——放宽成不敏感的话，
/// 配置校验、引擎工厂、目录跟随三处各自比较，规则一漂移就出「校验通过了但工厂不认识」。
/// 两档的模型目录必须分开：文件名毫无交集，堆在一个目录里虽也能跑，但用户换档时看到的将是
/// 一堆分不清归属的文件。
/// </summary>
public static class AsrEngines
{
    public const string SenseVoice = "SenseVoice";

    public const string Qwen3Asr = "Qwen3Asr";

    /// <summary>配置窗那个下拉框的全部选项，顺序即显示顺序（默认档在前）。</summary>
    public static IReadOnlyList<string> All { get; } = [SenseVoice, Qwen3Asr];

    public static bool IsSupported(string? engine) => engine is SenseVoice or Qwen3Asr;

    /// <summary>
    /// 该档权重的默认落脚处，与取模型脚本的产出目录一一对应。
    /// 未知引擎直接抛：静默回落到别档的目录，会让「引擎名拼错」表现成「模型目录莫名其妙对了
    /// 一半」，那是最难查的一类症状。
    /// </summary>
    public static string DefaultModelDir(string engine) => engine switch
    {
        SenseVoice => "./models/sense-voice",
        Qwen3Asr => "./models/qwen3-asr",
        _ => throw new ArgumentOutOfRangeException(
            nameof(engine), engine, $"未知的识别引擎，只支持 {string.Join(" / ", All)}"),
    };

    /// <summary>
    /// 该档一次录音的硬上限。两个数都是实测出来的，改之前先重测。
    /// </summary>
    /// <remarks>
    /// SenseVoice 60s：实测 61.5s 音频不截断，11 段内容一段不少（只掉个别字）。
    /// <para>
    /// Qwen3 25s，远短于它那个 512 token 的硬上限。算术上的上限是 audio token 每秒 13 个、
    /// 加 15 个 prompt token 撞满 512，即约 38.2s；但拿真实密集中文语音逐段实测，输出字数
    /// 10s→59、15s→125、20s→141、22s→154、<b>25s→157</b>、27s→151、30s→129、33s→77、
    /// 35s→53、36s→29、38s→1，整条 50.96s 则是空串——它在撞到 512 之前很久就开始丢内容，
    /// 25s 是最后一个输出还在涨的点。（别拿拼接音频定这个数：低熵重复音频能撑到 33s。）
    /// </para>
    /// <para>
    /// 而丢内容这件事没有任何征兆：不报错、不抛异常，只是上屏的字变少。宁可少录——到点自动收尾
    /// 用户看得见（悬浮条上那句「已达最长录音时长」），静默丢字看不见。
    /// </para>
    /// </remarks>
    public static int MaxRecordingSeconds(string engine) => engine switch
    {
        SenseVoice => 60,
        Qwen3Asr => 25,
        _ => throw new ArgumentOutOfRangeException(
            nameof(engine), engine, $"未知的识别引擎，只支持 {string.Join(" / ", All)}"),
    };
}
