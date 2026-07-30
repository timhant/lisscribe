using Lisscribe.Core.Asr;

namespace Lisscribe.Core.Config;

/// <summary>配置窗关掉之后要做的两件事。两个都为假就是什么也不做。</summary>
/// <param name="Changes">
/// 所有「重启才生效」的改动，含引擎那一条。<b>只进日志，不进弹窗</b>——
/// 弹窗给的是 <see cref="RestartPrompt.Message"/> 那句笼统话。
/// </param>
public sealed record SettingsCloseAction(
    bool ReloadEngine, bool AskRestart, IReadOnlyList<ConfigChange> Changes);

/// <summary>
/// 把「引擎那一路」（<see cref="EngineReloadPolicy"/>）与「其余字段那一路」
/// （<see cref="ConfigRestartPolicy"/>）合成一个决定。合流规矩住在这里而不是组合根，是因为
/// 「须重启时不做 Reload」这种规矩埋在组合根的一个 <c>if</c> 里就没人测得到。
/// </summary>
public static class SettingsClosePolicy
{
    /// <param name="engineAction"><see cref="EngineReloadPolicy.Decide"/> 的结论。</param>
    /// <param name="currentEngine">引擎实例的档位。</param>
    /// <param name="currentModelDir">引擎正在用的模型目录。</param>
    /// <param name="newEngine">配置窗保存后的档位。</param>
    /// <param name="newModelDir">配置窗保存后的模型目录（已算过环境变量覆盖）。</param>
    /// <param name="otherChanges"><see cref="ConfigRestartPolicy.Diff"/> 的结论。</param>
    public static SettingsCloseAction Decide(
        EngineReloadAction engineAction,
        string currentEngine, string currentModelDir,
        string newEngine, string newModelDir,
        IReadOnlyList<ConfigChange> otherChanges)
    {
        ArgumentNullException.ThrowIfNull(otherChanges);

        var changes = new List<ConfigChange>();

        if (engineAction == EngineReloadAction.RestartRequired)
        {
            // 换档与换目录记成两条不同的记录：弹窗文案已统一，日志是这个分辨力唯一的落脚处。
            changes.Add(string.Equals(currentEngine, newEngine, StringComparison.Ordinal)
                ? new ConfigChange("模型目录", currentModelDir, newModelDir)
                : new ConfigChange("识别引擎", currentEngine, newEngine));
        }

        changes.AddRange(otherChanges);

        var askRestart = changes.Count > 0;

        return new SettingsCloseAction(
            // 须重启时不做 Reload：重启本来就会按新配置重建引擎，先加载一次是白加载（高精度档
            // 982MB / 4 秒），何况那次加载会跟「立即重启」的收尾撞在一起——LoadAsync 不在状态机的
            // 在途任务账里，ShutdownAsync 等不到它。
            ReloadEngine: engineAction == EngineReloadAction.Reload && !askRestart,
            AskRestart: askRestart,
            Changes: changes);
    }
}
