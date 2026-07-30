using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.Core.Tests.Config;

/// <summary>
/// 弹窗里那句话。
///
/// <para>
/// <b>本类薄是有意的。</b>文案统一成一句之后，「换档」与「换目录」的分辨力整个搬到了日志上，
/// 由 <see cref="SettingsClosePolicyTests"/> 里那两条盯着。这里只剩一件事可钉：
/// <b>这句话本身的语义</b>。
/// </para>
/// </summary>
public sealed class RestartPromptTests
{
    /// <summary>
    /// 关窗时热键组合已经当场重装、模型目录也可能已经重载，说「重启之后才会生效」是假话
    /// ——用户会以为刚改的热键也没生效，回头再改一遍。
    /// </summary>
    [Fact]
    public void TheMessage_SaysOnlyTheRestOfItNeedsARestart()
    {
        Assert.Contains("重启", RestartPrompt.Message, StringComparison.Ordinal);
        Assert.Contains("全部", RestartPrompt.Message, StringComparison.Ordinal);
    }
}
