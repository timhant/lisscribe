using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.Core.Tests.Config;

/// <summary>
/// 配置窗关掉之后，哪些改动是「重启才生效」的。
///
/// <para>
/// <b>这条规矩是查账查出来的，不是猜的。</b>配置窗关闭的回调只重装热键、只处置引擎，
/// 而 <c>CompositeTextInjector</c> / <c>SessionOptions</c> / <c>LoggerFactory</c> 三样都只在
/// 组合根的构造函数里建一次。于是用户在配置窗里把上屏方式从 SendInput 改成剪贴板、
/// 把长按阈值从 300 改成 500、把日志级别调到 Debug——保存、关窗，**什么都不会变，
/// 也没有一个字告诉他**。
/// </para>
///
/// <para>
/// 判断住在这里而不是组合根里，与 <c>EngineReloadPolicy</c> 同一个理由：
/// <c>AppServices</c> 刻意不含判断逻辑，规矩住这儿才有测试盯得住。
/// </para>
///
/// <para>
/// <b>本类盖不住的那一条，明写在这里</b>：真正的风险不是这个函数算错，是组合根把调用点写成
/// 「拿刚存下的那份跟它自己比」（永远为空，于是永不提示）。那是接线正确性，
/// 没有缝够得着——**不要写一条自称验了它的测试**。它的去处是调用点注释与手工清单。
/// </para>
/// </summary>
public sealed class ConfigRestartPolicyTests
{
    /// <summary>逐字段各来一条。用 <see cref="LisscribeConfig.CreateDefault"/> 当基线，只改一处。</summary>
    public static TheoryData<string, LisscribeConfig> ChangedOneField()
    {
        var baseline = LisscribeConfig.CreateDefault();

        return new TheoryData<string, LisscribeConfig>
        {
            { "上屏方式", baseline with { Output = baseline.Output with { Mode = "Clipboard" } } },
            {
                "上屏后恢复剪贴板",
                baseline with { Output = baseline.Output with { RestoreClipboard = false } }
            },
            {
                "清理句末标点",
                baseline with { Output = baseline.Output with { TrimTrailingPunctuation = false } }
            },
            {
                "末尾标点最多清理字符数",
                baseline with { Output = baseline.Output with { TrimPunctuationMaxChars = 3 } }
            },
            {
                "中文数字转换",
                baseline with { Output = baseline.Output with { ChineseNumberItn = false } }
            },
            {
                "长按判定阈值",
                baseline with { Hotkey = baseline.Hotkey with { HoldThresholdMs = 500 } }
            },
            { "日志级别", baseline with { App = baseline.App with { LogLevel = "Debug" } } },

            // provider 眼下只放行 "cpu"，改不成别的。仍要比：CUDA 档一落地它就是须重启的
            // （引擎在构造时读它），而漏掉一个字段的症状是「改了没反应」，跟本类要修的病一模一样。
            { "推理后端", baseline with { Asr = baseline.Asr with { Provider = "cuda" } } },
        };
    }

    [Theory]
    [MemberData(nameof(ChangedOneField))]
    public void Diff_ReportsTheFieldThatChanged(string expectedField, LisscribeConfig saved)
    {
        var changes = ConfigRestartPolicy.Diff(LisscribeConfig.CreateDefault(), saved);

        var change = Assert.Single(changes);
        Assert.Equal(expectedField, change.Field);
    }

    [Fact]
    public void Diff_WhenNothingChanged_IsEmpty()
    {
        // 阴性对照。少了它，实现里无条件返回「全都变了」也照样让上面七条全绿——
        // 而那样每关一次配置窗都弹一句「须重启」，喊到用户不再看那个窗口为止。
        Assert.Empty(
            ConfigRestartPolicy.Diff(LisscribeConfig.CreateDefault(), LisscribeConfig.CreateDefault()));
    }

    /// <summary>
    /// 热键组合与模型目录**不许**进这张单子：前者由 <c>RestartHotkey()</c> 当场重装，
    /// 后者由 <c>EngineReloadPolicy</c> 单独判
    /// （它要问引擎自己要 <c>ModelDir</c>，只比配置会报一个假的「须重启」）。
    /// 把它们一并算进来，症状是「改完热键立刻就能用，却还弹一句要重启」。
    /// </summary>
    [Fact]
    public void Diff_IgnoresTheThingsThatTakeEffectRightAway()
    {
        var baseline = LisscribeConfig.CreateDefault();
        var saved = baseline with
        {
            Hotkey = baseline.Hotkey with { Modifiers = ["Ctrl"], MainKey = "F9" },
            Asr = baseline.Asr with { ModelDir = "D:/models/elsewhere" },
            App = baseline.App with { AutoStart = true },
            Transcription = new TranscriptionSection(@"D:\我的 录音\转写"),
        };

        Assert.Empty(ConfigRestartPolicy.Diff(baseline, saved));
    }

    [Fact]
    public void Diff_ReportsEveryFieldThatChanged_NotJustTheFirst()
    {
        // 一次改好几处是常态（进一次配置窗顺手都调了）。只报第一处的话，用户重启之后
        // 会发现只生效了一样，而日志里另几样连提都没提。
        var baseline = LisscribeConfig.CreateDefault();
        var saved = baseline with
        {
            Output = baseline.Output with { Mode = "Clipboard", TrimPunctuationMaxChars = 3 },
            App = baseline.App with { LogLevel = "Debug" },
        };

        var fields = ConfigRestartPolicy.Diff(baseline, saved).Select(c => c.Field).ToList();

        Assert.Equal(3, fields.Count);
        Assert.Contains("上屏方式", fields);
        Assert.Contains("末尾标点最多清理字符数", fields);
        Assert.Contains("日志级别", fields);
    }

    /// <summary>
    /// 每条改动都要带上新旧值——**这是弹窗那句笼统话唯一的补偿**。用户看到的是
    /// 「配置已修改，重启之后才会全部生效」，排障的人要能从日志里看出到底是哪一项、从什么改成了什么。
    /// 只记字段名的话，「我明明没改过日志级别」这种报障永远查不下去。
    /// </summary>
    [Fact]
    public void EachChange_CarriesBothTheOldAndTheNewValue()
    {
        var baseline = LisscribeConfig.CreateDefault();
        var saved = baseline with { Hotkey = baseline.Hotkey with { HoldThresholdMs = 500 } };

        var change = Assert.Single(ConfigRestartPolicy.Diff(baseline, saved));

        Assert.Equal("300", change.Before);
        Assert.Equal("500", change.After);

        // 落进日志的是这一句，故它必须同时含得下两个值。
        Assert.Contains("300", change.ToString(), StringComparison.Ordinal);
        Assert.Contains("500", change.ToString(), StringComparison.Ordinal);
        Assert.Contains("长按判定阈值", change.ToString(), StringComparison.Ordinal);
    }
}
