using Microsoft.Win32;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 开机自启动的 HKCU Run 项。
///
/// **本类写的是真实的注册表**（HKCU\...\Run），不是内存里的假货。值名带 GUID，
/// <see cref="Dispose"/> 里删掉；但测试进程被硬杀时清理不会跑，故手工清单里有一条
/// 「查 Run 项有无 LisscribeTest_ 残留」。
/// </summary>
public sealed class RegistryAutoStartTests : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName = $"LisscribeTest_{Guid.NewGuid():N}";
    private readonly RegistryAutoStart _autoStart;

    public RegistryAutoStartTests() => _autoStart = new RegistryAutoStart(_valueName);

    /// <summary>
    /// **收尾故意绕开被测对象**，直接删注册表值，不调 <c>_autoStart.Disable()</c>。
    ///
    /// 这不是洁癖：本轮 TDD 的红灯阶段（<c>Disable</c> 还是个空壳）跑完之后，HKCU 的 Run 项里
    /// 真的留下了两条 <c>LisscribeTest_*</c>——收尾一旦依赖被测代码，被测代码坏掉的那几轮
    /// 就正好不收尾，而那几轮恰恰是最容易被硬杀的。
    ///
    /// 仍然收不干净的情形只剩「测试进程被硬杀」，那条写在 PlatformIntegrationManualChecklist.md E1。
    /// </summary>
    public void Dispose()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    [Fact]
    public void IsEnabled_Initially_IsFalse()
    {
        Assert.False(_autoStart.IsEnabled);
    }

    [Fact]
    public void Enable_ThenIsEnabled_IsTrue()
    {
        _autoStart.Enable(@"C:\Program Files\lisscribe\lisscribe.exe");

        Assert.True(_autoStart.IsEnabled);
    }

    [Fact]
    public void Disable_AfterEnable_ClearsIt()
    {
        _autoStart.Enable(@"C:\lisscribe.exe");

        _autoStart.Disable();

        Assert.False(_autoStart.IsEnabled);
    }

    [Fact]
    public void Disable_WhenNotEnabled_DoesNotThrow()
    {
        _autoStart.Disable();
        _autoStart.Disable();
    }

    [Fact]
    public void Enable_WritesThePathWrappedInQuotes()
    {
        // **整串值都要对得上**：只断言 StartsWith("\"") 的话漏掉尾引号照样绿，
        // 而少一个尾引号同样开不了机。
        _autoStart.Enable(@"C:\Program Files\lisscribe\lisscribe.exe");

        Assert.Equal("\"C:\\Program Files\\lisscribe\\lisscribe.exe\"", _autoStart.ReadRawValue());
    }

    [Fact]
    public void Enable_DoesNotWrapAnAlreadyQuotedPathTwice()
    {
        // 无条件加引号时这里会写出 ""C:\...\lisscribe.exe""，Windows 解析成一个空程序名，
        // 开机静默什么也不启动——与不加引号一样坏，只是更难看出来。
        _autoStart.Enable("\"C:\\Program Files\\lisscribe\\lisscribe.exe\"");

        Assert.Equal("\"C:\\Program Files\\lisscribe\\lisscribe.exe\"", _autoStart.ReadRawValue());
    }

    [Fact]
    public void Enable_Twice_LeavesExactlyOneValue()
    {
        _autoStart.Enable(@"C:\old\lisscribe.exe");
        _autoStart.Enable(@"C:\new\lisscribe.exe");

        Assert.Equal("\"C:\\new\\lisscribe.exe\"", _autoStart.ReadRawValue());
    }

    [Fact]
    public void Enable_WritesAPlainStringNotAnExpandableOne()
    {
        // REG_EXPAND_SZ 会让路径里的 %AppData% 之类被展开。安装目录若含百分号，
        // 写成可展开串就把它吃掉了。预言机是注册表自己报的类型，不是我们写进去的参数。
        _autoStart.Enable(@"C:\lisscribe.exe");

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        Assert.Equal(RegistryValueKind.String, key!.GetValueKind(_valueName));
    }

    [Fact]
    public void Enable_WithABlankPath_Throws()
    {
        // 空串会写进一条谁也起不来的 Run 项，而用户在设置界面只看到「已开启」。
        Assert.Throws<ArgumentException>(() => _autoStart.Enable("   "));
        Assert.False(_autoStart.IsEnabled);
    }

    [Fact]
    public void IsEnabled_SeesWhatSomebodyElseWroteToTheRegistry()
    {
        // IsEnabled 若读的是自己的内存标志而不是注册表，用户在 msconfig 里关掉自启动之后
        // 我们的设置界面还显示「已开启」。这里绕过被测对象直接写注册表。
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
        {
            key.SetValue(_valueName, @"""C:\somewhere\else.exe""", RegistryValueKind.String);
        }

        Assert.True(_autoStart.IsEnabled);
    }
}
