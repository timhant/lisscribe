namespace Lisscribe.Core.Config;

/// <summary>
/// 一处「重启才生效」的配置改动。
///
/// <para>
/// <b>它存在的理由全在日志里</b>：弹窗那句话是笼统的（<see cref="RestartPrompt.Message"/>），
/// 用户不需要知道细节，但排障的人需要。只记「配置已修改」的话，
/// 「我明明没改过日志级别」这类报障永远查不下去，故新旧值两个都要带上。
/// </para>
/// </summary>
/// <param name="Field">用户在配置窗里看到的那个名字，不是属性名——报障的人只认得前者。</param>
public sealed record ConfigChange(string Field, string Before, string After)
{
    /// <summary>落进日志的形状。</summary>
    public override string ToString() => $"{Field}：{Before} 改为 {After}";
}
