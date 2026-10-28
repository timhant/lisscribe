namespace Lisscribe.Core.Config;

/// <summary>
/// 弹窗里那句话。
///
/// <para>
/// <b>「全部」这两个字是承重的。</b>关窗时热键组合已经当场重装、模型目录也可能已经重载了，
/// 说成「重启之后才会生效」是假话——用户会以为刚改的热键也没生效，回头再改一遍。
/// </para>
///
/// <para>
/// <b>刻意不带参数。</b>上一版这里按「换档 / 换目录」分两句说，理由是用户改的是引擎却只看到
/// 「模型目录已改」会去把目录再改一遍。文案统一之后那个分辨力整个搬到了日志上
/// （<see cref="ConfigChange"/>）：<b>用户看弹窗，排障的人看日志</b>，UI 上不多一分复杂度。
/// </para>
///
/// <para>
/// <see cref="ConfirmMessageFor"/> 的参数不是「改了什么」，而是「重启会不会打断用户手上的事」，与上一条不冲突。
/// </para>
/// </summary>
public static class RestartPrompt
{
    public const string Message = "配置已修改，重启 lisscribe 之后才会全部生效。";

    /// <summary>重启确认窗的文案。有转写在跑时说明它会被取消（设计 §4.6），点「稍后」退回的红条仍用 <see cref="Message"/>。</summary>
    public static string ConfirmMessageFor(bool isTranscribing) =>
        isTranscribing ? Message + "正在进行的转写会被取消。" : Message;
}
