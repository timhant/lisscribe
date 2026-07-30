using Lisscribe.Core.Abstractions;
using Microsoft.Win32;

namespace Lisscribe.Platform;

/// <summary>
/// HKCU 的 Run 项。用 HKCU 而非 HKLM——后者要管理员权限，而本程序刻意不提权。
/// </summary>
public sealed class RegistryAutoStart(string valueName = "lisscribe") : IAutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 每次都真去读注册表，不缓存。用户在任务管理器或 msconfig 里关掉自启动之后，
    /// 我们的设置界面必须跟着变——缓存一个内存标志就会让界面一直显示「已开启」。
    /// </summary>
    public bool IsEnabled => ReadRawValue() is not null;

    public void Enable(string executablePath)
    {
        // 空串会写出一条谁也起不来的 Run 项，而用户在设置界面只看到「已开启」。
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        // 显式 RegistryValueKind.String（REG_SZ）。写成 REG_EXPAND_SZ 的话，安装目录里
        // 若含 %AppData% 这样的字样会在开机时被当环境变量展开掉。
        key.SetValue(valueName, Quote(executablePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        // 键可能压根不存在（全新用户配置），OpenSubKey 返回 null 是正常情形。
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 供测试断言写进去的原始值。<c>internal</c> 而非 public：测试的需要不该撑大生产表面。
    /// </summary>
    internal string? ReadRawValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);

        return key?.GetValue(valueName) as string;
    }

    /// <summary>
    /// 带空格的路径不加引号，会被 Windows 解析成 <c>C:\Program</c> 加两个参数，开机静默失败。
    ///
    /// 但也不能无条件加：路径若已经带着引号，再包一层就成了 <c>""C:\...\x.exe""</c>，
    /// 解析出来是一个空程序名，同样开机什么也不启动——而且更难看出来。
    /// </summary>
    private static string Quote(string executablePath)
    {
        var trimmed = executablePath.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed
            : $"\"{trimmed}\"";
    }
}
