using Lisscribe.Core.Asr;

namespace Lisscribe.Core.Config;

/// <summary>
/// <b>没有 <c>recording</c> 一节，是砍掉的，不是漏了。</b>参考项目那两个字段
/// （<c>saveAudio</c> / <c>saveDir</c>）服务的是它的「语音日记」，本项目不做这个产品；
/// 而唯一说得通的另一个用途——留原始音频供缺陷复现——一个要提前勾上的开关恰恰赶不上，
/// bug 总是事后才发现的。真要做该是「留最近 N 次的环形缓冲、只在诊断模式下开」，那是另一件事。
///
/// <para>
/// 旧 <c>config.json</c> 里残留的那一节会被源生成器按「跳过未映射成员」直接忽略，不报错。
/// </para>
/// </summary>
public sealed record LisscribeConfig(
    int Version,
    HotkeySection Hotkey,
    AsrSection Asr,
    OutputSection Output,
    AppSection App)
{
    public const int CurrentVersion = 1;

    public static LisscribeConfig CreateDefault() => new(
        Version: CurrentVersion,
        Hotkey: new HotkeySection(["Ctrl", "Alt"], "Space", 300),
        // 默认档不因为多了一档就改：Qwen3 那 982MB 权重是用户自己去下的，
        // 默认指过去只会让所有人开箱即见「模型缺失」。
        Asr: new AsrSection(
            AsrEngines.SenseVoice, AsrEngines.DefaultModelDir(AsrEngines.SenseVoice), "cpu"),
        Output: new OutputSection("SendInput", RestoreClipboard: true,
                                  TrimTrailingPunctuation: true, TrimPunctuationMaxChars: 8),
        App: new AppSection(AutoStart: false, LogLevel: "Info"));
}

public sealed record HotkeySection(string[] Modifiers, string MainKey, int HoldThresholdMs);

/// <param name="Engine">"SenseVoice"（默认档）或 "Qwen3Asr"（高精度档），见 <see cref="AsrEngines"/>。</param>
/// <param name="Provider">只接受 "cpu"。"cuda" 须运行时探测通过才可选，尚未实现。</param>
public sealed record AsrSection(string Engine, string ModelDir, string Provider);

public sealed record OutputSection(
    string Mode, bool RestoreClipboard, bool TrimTrailingPunctuation, int TrimPunctuationMaxChars);

public sealed record AppSection(bool AutoStart, string LogLevel);
