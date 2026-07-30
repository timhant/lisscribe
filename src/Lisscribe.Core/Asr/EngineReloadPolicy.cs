namespace Lisscribe.Core.Asr;

/// <summary>配置窗关掉之后该拿识别引擎怎么办。</summary>
public enum EngineReloadAction
{
    /// <summary>什么都不用做。</summary>
    Nothing,

    /// <summary>用最新的模型目录再加载一次。</summary>
    Reload,

    /// <summary>改动要重启程序才生效，须明确告知用户。</summary>
    RestartRequired,
}

/// <summary>
/// 配置窗关掉之后，识别引擎该怎么办。少了这一层的症状是：模型目录写错、启动时引擎加载失败、
/// 用户进配置窗把目录改对、保存——什么也没发生，最自然的自救手段完全无效且没有一句提示。
/// 判断住在这里而不是组合根，是为了让测试盯得住。
/// </summary>
public static class EngineReloadPolicy
{
    /// <param name="engineLoaded">引擎当前是否已经加载成功。</param>
    /// <param name="currentEngine">引擎实例的档位（<see cref="AsrEngines"/> 里那两个名字之一）。</param>
    /// <param name="currentModelDir">引擎正在用的模型目录。</param>
    /// <param name="newEngine">配置窗保存后的档位。</param>
    /// <param name="newModelDir">配置窗保存后的模型目录（已算过环境变量覆盖）。</param>
    public static EngineReloadAction Decide(
        bool engineLoaded,
        string currentEngine, string currentModelDir,
        string newEngine, string newModelDir)
    {
        // 这一条必须排在「没加载过就重试」前面：两档是两个类，重新 LoadAsync 只会把同一个实例再加载
        // 一遍——用户明明选了另一档，程序却继续跑原来那档，日志还写着「加载完成」，那是假话。而
        // 「加载失败之后去配置窗换一档」恰恰是最可能发生的那次换档，不是边角情形。
        if (!string.Equals(currentEngine, newEngine, StringComparison.Ordinal))
        {
            return EngineReloadAction.RestartRequired;
        }

        // 没加载成功过就无条件再试一次，不看目录变没变：用户很可能压根没改路径，而是照着错误提示把
        // 权重下下来了，只在「目录变了」时重试会把那条最常见的自救路径挡在门外。
        if (!engineLoaded)
        {
            return EngineReloadAction.Reload;
        }

        // 已加载而目录变了：换目录得换一个引擎实例，可它已被状态机攥着。就地热换要么泄掉旧的原生
        // 句柄，要么在识别半途把它抽走（catch 不到的 AccessViolation）。说实话比这两样都强。
        return ModelDirectory.SamePlace(currentModelDir, newModelDir)
            ? EngineReloadAction.Nothing
            : EngineReloadAction.RestartRequired;
    }
}
