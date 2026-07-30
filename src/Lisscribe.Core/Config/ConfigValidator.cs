using Lisscribe.Core.Asr;

namespace Lisscribe.Core.Config;

public sealed record ValidationResult(
    bool IsValid, IReadOnlyList<string> Errors, LisscribeConfig Normalized);

/// <summary>
/// 配置校验的唯一实现。启动加载与配置窗保存共用，避免两处规则漂移。
/// </summary>
public static class ConfigValidator
{
    private const int MinHoldThresholdMs = 50;
    private const int MaxHoldThresholdMs = 3000;

    // 修饰键的规范顺序。Ctrl、Alt、Shift、Win —— 与 Windows 惯例一致。
    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    public static ValidationResult Validate(LisscribeConfig config)
    {
        var errors = new List<string>();

        // Null section checks
        if (config.Hotkey == null)
        {
            errors.Add("hotkey 配置段缺失");
        }

        if (config.Asr == null)
        {
            errors.Add("asr 配置段缺失");
        }

        if (config.Output == null)
        {
            errors.Add("output 配置段缺失");
        }

        if (config.Version > LisscribeConfig.CurrentVersion)
        {
            errors.Add($"不支持的配置 version {config.Version}，本程序最高支持 {LisscribeConfig.CurrentVersion}");
        }

        if (config.Hotkey != null)
        {
            // Null modifiers array check
            if (config.Hotkey.Modifiers == null)
            {
                errors.Add("hotkey.modifiers 数组缺失");
            }
            else
            {
                // Validate unknown modifiers before normalization
                var validModifiers = new HashSet<string>(ModifierOrder, StringComparer.OrdinalIgnoreCase);

                foreach (var modifier in config.Hotkey.Modifiers)
                {
                    if (!validModifiers.Contains(modifier))
                    {
                        errors.Add($"未知的修饰键 \"{modifier}\"，只支持 Ctrl、Alt、Shift、Win");
                    }
                }
            }

            if (config.Hotkey.HoldThresholdMs is < MinHoldThresholdMs or > MaxHoldThresholdMs)
            {
                errors.Add($"holdThresholdMs 须在 {MinHoldThresholdMs}–{MaxHoldThresholdMs} 之间");
            }
        }

        if (config.Asr != null)
        {
            // 未实现的配置值必须在此拦截，否则用户填了会得到一个静默不生效的设置
            // ——正是规格明令禁止的。cuda 仍未实现（官方 NuGet 是纯 CPU 构建，
            // 传别的 provider 只会静默回落，用户会以为自己在跑 GPU）。
            if (!AsrEngines.IsSupported(config.Asr.Engine))
            {
                errors.Add(
                    $"asr.engine 只支持 {string.Join(" / ", AsrEngines.All.Select(e => $"\"{e}\""))}，" +
                    $"\"{config.Asr.Engine}\" 尚未实现");
            }

            if (config.Asr.Provider != "cpu")
            {
                errors.Add($"目前只支持 asr.provider = \"cpu\"，\"{config.Asr.Provider}\" 尚未实现");
            }
        }

        if (config.Output != null)
        {
            if (config.Output.Mode is not ("SendInput" or "Clipboard"))
            {
                errors.Add($"output.mode 须为 \"SendInput\" 或 \"Clipboard\"，实为 \"{config.Output.Mode}\"");
            }

            if (config.Output.TrimPunctuationMaxChars < 0)
            {
                errors.Add("trimPunctuationMaxChars 不得为负");
            }
        }

        // Only normalize if there are no section errors and modifiers are valid
        LisscribeConfig normalized;

        if (errors.Count == 0 && config.Hotkey?.Modifiers != null)
        {
            var normalizedModifiers = NormalizeModifiers(config.Hotkey.Modifiers);

            normalized = config with
            {
                Hotkey = config.Hotkey with { Modifiers = normalizedModifiers },
            };
        }
        else
        {
            // Return config as-is if there are errors or if sections are null
            normalized = config;
        }

        return new ValidationResult(errors.Count == 0, errors, normalized);
    }

    private static string[] NormalizeModifiers(string[] raw) =>
        ModifierOrder
            .Where(canonical => raw.Any(r => string.Equals(r, canonical, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
}
