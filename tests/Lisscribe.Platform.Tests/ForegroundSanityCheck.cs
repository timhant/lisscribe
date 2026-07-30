using System.Diagnostics;
using Lisscribe.Platform.Native;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 注入型测试跑起来之前的一次 fail-fast 自检：**当前前台窗口的进程，完整性级别不能高于我们。**
///
/// 为什么值得单开一个 fixture：前台一旦落在 SYSTEM 完整性级别的窗口上
/// （<c>GameInputServiceWindow</c> 是一例），**未改一行代码**也会有几十条测试转红、
/// 耗时涨几倍，而失败讯息是「只收到 []」「系统拒绝了前台抢占」——**一个字都指不到真因**。
/// 根因是我们既抢不到前台，也没法把输入送进那个窗口，于是每条测试各自以自己的方式超时。
///
/// 与其让每条测试各报一句误导的话，不如在这里一次性说清楚，并给出恢复办法。
///
/// 说明取样的边界：这里借了 <see cref="WindowProbe.GetIntegrityLevel"/>——那是被测代码。
/// 它在这里只当**诊断**用，不当预言机：本类从不判「产品对不对」，只判「这台机器现在能不能跑」。
/// 真出问题时它顶多报错原因不准，不会把一条坏实现说成好实现。
/// </summary>
public sealed class ForegroundSanityCheck
{
    public ForegroundSanityCheck()
    {
        var foreground = User32.GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return;
        }

        User32.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0)
        {
            return;
        }

        var ourLevel = WindowProbe.GetIntegrityLevel(Kernel32.GetCurrentProcess());
        if (ourLevel is null)
        {
            return;
        }

        var process = Kernel32.OpenProcess(
            Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, inheritHandle: false, processId);

        if (process == nint.Zero)
        {
            throw new InvalidOperationException(Explain(processId, "打不开它的进程（多半是 SYSTEM 或受保护进程）"));
        }

        try
        {
            var theirLevel = WindowProbe.GetIntegrityLevel(process);
            if (theirLevel > ourLevel)
            {
                throw new InvalidOperationException(
                    Explain(processId, $"它的完整性级别 0x{theirLevel:X} 高于我们的 0x{ourLevel:X}"));
            }
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    private static string Explain(uint processId, string why)
    {
        string name;
        try
        {
            name = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            name = "?";
        }

        return $"""
            注入型测试无法在当前桌面状态下运行，已提前中止（这不是产品缺陷）。

            当前前台窗口属于进程 {name}（pid {processId}），{why}。
            这种状态下我们既抢不到前台，也没法把合成输入送进去，几十条测试会各自超时，
            而它们报出来的是「只收到 []」「系统拒绝了前台抢占」之类**指不到真因**的讯息。

            恢复办法（把前台还给一个普通窗口即可）：
                (New-Object -ComObject Shell.Application).MinimizeAll()
                (New-Object -ComObject Shell.Application).UndoMinimizeALL()

            另见 tests/Lisscribe.Platform.Tests/PlatformIntegrationManualChecklist.md 的 E0。
            """;
    }
}
