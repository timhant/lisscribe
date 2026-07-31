using Lisscribe.Core.Asr;

namespace Lisscribe.Core.Session;

/// <summary>
/// 状态机的全部阈值。默认值来自规格的写死常量，不入 config.json。
///
/// <para>
/// <c>CountdownThreshold</c> 是「离上限还剩多久开始倒数」（见
/// <c>SessionController.RecordingCountdown</c>）。**两档共用一个 5 秒，不按比例给**：
/// Qwen3 只能录 25s，五秒已占两成、够显眼；SenseVoice 那档 60s 若按比例算要十几秒，
/// 那就从「快到了」变成了「一直在催」。
/// </para>
/// </summary>
public sealed record SessionOptions(
    TimeSpan HoldThreshold,
    int MaxRecordingSeconds,
    TimeSpan MinAudioDuration,
    TimeSpan StaleResultThreshold,
    TimeSpan FailedDisplayDuration,
    TimeSpan WatchdogInterval,
    TimeSpan CountdownThreshold,
    bool TrimTrailingPunctuation,
    int TrimPunctuationMaxChars,
    bool ApplyChineseNumberItn)
{
    public static SessionOptions CreateDefault() => new(
        HoldThreshold: TimeSpan.FromMilliseconds(300),
        MaxRecordingSeconds: AsrEngines.MaxRecordingSeconds(AsrEngines.SenseVoice),
        MinAudioDuration: TimeSpan.FromSeconds(0.5),
        StaleResultThreshold: TimeSpan.FromSeconds(5),
        FailedDisplayDuration: TimeSpan.FromSeconds(3),
        WatchdogInterval: TimeSpan.FromMilliseconds(200),
        CountdownThreshold: TimeSpan.FromSeconds(5),
        TrimTrailingPunctuation: true,
        TrimPunctuationMaxChars: 8,
        ApplyChineseNumberItn: AsrEngines.NeedsChineseNumberItn(AsrEngines.SenseVoice));

    /// <summary>
    /// 按引擎档位取阈值。**随档位变的只有录音上限与中文数字转换**：前者两档差得很远
    /// （SenseVoice 60s / Qwen3 25s，见 <see cref="AsrEngines.MaxRecordingSeconds"/>），
    /// 后者只补给没有内置 ITN 的那档（见 <see cref="AsrEngines.NeedsChineseNumberItn"/>）。
    ///
    /// <para>
    /// 判断放在这儿而不是组合根里：<c>AppServices</c> 刻意不含判断逻辑。
    /// </para>
    /// </summary>
    /// <param name="chineseNumberItnEnabled">
    /// 用户在配置里开没开。**与档位取合取**：这一档得确实需要软件来做，用户也没关掉，才生效。
    /// 写成直接采信配置值的话，默认档会在 sherpa-onnx 自带的 ITN 上再叠一层。
    /// </param>
    public static SessionOptions CreateFor(string engine, bool chineseNumberItnEnabled = true) =>
        CreateDefault() with
        {
            MaxRecordingSeconds = AsrEngines.MaxRecordingSeconds(engine),
            ApplyChineseNumberItn =
                AsrEngines.NeedsChineseNumberItn(engine) && chineseNumberItnEnabled,
        };
}
