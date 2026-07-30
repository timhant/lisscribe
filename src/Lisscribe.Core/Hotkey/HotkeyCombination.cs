using System.Diagnostics.CodeAnalysis;
using Lisscribe.Core.Config;

namespace Lisscribe.Core.Hotkey;

[Flags]
public enum ModifierKeys
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 热键组合。只接受「≥1 修饰键 + 恰好 1 个非修饰主键」。
/// </summary>
public readonly record struct HotkeyCombination(ModifierKeys Modifiers, string MainKey)
{
    private static readonly (string Name, ModifierKeys Flag)[] KnownModifiers =
    [
        ("Ctrl", ModifierKeys.Ctrl),
        ("Alt", ModifierKeys.Alt),
        ("Shift", ModifierKeys.Shift),
        ("Win", ModifierKeys.Win),
    ];

    public static bool TryParse(
        HotkeySection section,
        out HotkeyCombination combination,
        [NotNullWhen(false)] out string? error)
    {
        combination = default;

        // JSON 反序列化可绕过 NRT 产出 null 数组（ConfigValidator.Validate 已因此设防）；
        // TryParse 的 out error 契约是「报告失败」而非「抛异常」，此处须显式判空。
        if (section.Modifiers is null || section.Modifiers.Length == 0)
        {
            error = "热键须包含至少一个修饰键（Ctrl / Alt / Shift / Win）";

            return false;
        }

        var modifiers = ModifierKeys.None;

        foreach (var name in section.Modifiers)
        {
            var match = KnownModifiers.FirstOrDefault(
                m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

            if (match.Name is null)
            {
                error = $"无法识别的修饰键 \"{name}\"";

                return false;
            }

            modifiers |= match.Flag;
        }

        if (string.IsNullOrWhiteSpace(section.MainKey))
        {
            error = "热键须指定一个主键";

            return false;
        }

        if (KnownModifiers.Any(m => string.Equals(m.Name, section.MainKey, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"主键不得是修饰键（\"{section.MainKey}\"）——纯修饰键组合无法作为热键";

            return false;
        }

        combination = new HotkeyCombination(modifiers, section.MainKey);
        error = null;

        return true;
    }

    public string ToDisplayString()
    {
        // 局部变量捕获 this.Modifiers：readonly record struct 内的 lambda
        // 不能直接捕获实例成员（CS1673），须先拷贝到局部变量。
        var modifiers = Modifiers;
        var parts = KnownModifiers
            .Where(m => modifiers.HasFlag(m.Flag))
            .Select(m => m.Name)
            .Append(MainKey);

        return string.Join(" + ", parts);
    }
}
