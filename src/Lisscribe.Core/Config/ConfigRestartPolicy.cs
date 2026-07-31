using System.Globalization;

namespace Lisscribe.Core.Config;

/// <summary>配置里哪些改动是「重启才生效」的。</summary>
/// <remarks>
/// <c>CompositeTextInjector</c>、<c>SessionOptions</c>、<c>LoggerFactory</c> 三样只在组合根的构造
/// 函数里建一次，于是用户改上屏方式、长按阈值、日志级别，保存关窗后什么都不会变；本类负责把
/// 「一个字都没告诉他」那一半补上。
/// <para>
/// 三样不进这张单子，它们本来就即时生效：热键组合（关窗当场重装）、模型目录（引擎未加载时由
/// <see cref="Asr.EngineReloadPolicy"/> 判重载，它问引擎要 <c>ModelDir</c>，只比配置会报一个假的
/// 「须重启」）、开机自启（保存时直接写注册表）。算进来的症状是「改完热键立刻能用，却还弹一句
/// 要重启」。判断住在这里而不是组合根，是为了让测试盯得住。
/// </para>
/// </remarks>
public static class ConfigRestartPolicy
{
    /// <param name="running">
    /// 本进程正在跑的那份，不是上次关窗时那份。这一条承重：用户点了「稍后重启」、隔一会儿再开
    /// 配置窗什么都不改就关，若拿上次关窗那份比则毫无差异、不再提示，而跑着的仍是旧值。组合根里
    /// 那份 <c>_config</c> 启动时加载并规范化、全程不改写，正是这个参数该收到的东西。
    /// </param>
    /// <param name="saved">配置窗刚存下并规范化过的那份。</param>
    public static IReadOnlyList<ConfigChange> Diff(LisscribeConfig running, LisscribeConfig saved)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(saved);

        var changes = new List<ConfigChange>();

        Compare(changes, "上屏方式", running.Output.Mode, saved.Output.Mode);
        Compare(changes, "上屏后恢复剪贴板",
            running.Output.RestoreClipboard, saved.Output.RestoreClipboard);
        Compare(changes, "清理句末标点",
            running.Output.TrimTrailingPunctuation, saved.Output.TrimTrailingPunctuation);
        Compare(changes, "末尾标点最多清理字符数",
            running.Output.TrimPunctuationMaxChars, saved.Output.TrimPunctuationMaxChars);
        Compare(changes, "中文数字转换",
            running.Output.ChineseNumberItn, saved.Output.ChineseNumberItn);

        Compare(changes, "长按判定阈值",
            running.Hotkey.HoldThresholdMs, saved.Hotkey.HoldThresholdMs);

        Compare(changes, "日志级别", running.App.LogLevel, saved.App.LogLevel);

        // provider 眼下只放行 "cpu"，校验器把别的都挡了，这一条现在走不到。仍要比：CUDA 档一落地
        // 它就是须重启的（引擎构造时读它），而漏掉一个字段的症状正是本类要修的那个病。
        Compare(changes, "推理后端", running.Asr.Provider, saved.Asr.Provider);

        return changes;
    }

    private static void Compare<T>(List<ConfigChange> changes, string field, T running, T saved)
    {
        if (EqualityComparer<T>.Default.Equals(running, saved))
        {
            return;
        }

        changes.Add(new ConfigChange(field, Format(running), Format(saved)));
    }

    /// <summary>
    /// 布尔这一支必须排在最前：<c>bool</c> 也实现了 <c>IFormattable</c>，落到下一支会写出
    /// 「True 改为 False」，而日志的读者是人。
    /// </summary>
    private static string Format<T>(T value) => value switch
    {
        bool enabled => enabled ? "开" : "关",

        // 数字一律按不变文化格式化：日志要能跨机器对照，不该因为区域设置换个写法。
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),

        _ => value?.ToString() ?? string.Empty,
    };
}
