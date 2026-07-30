using System.Diagnostics;
using System.Globalization;
using Lisscribe.Core.Diagnostics;

namespace Lisscribe.App.Composition;

/// <summary>
/// 拿 <see cref="Process.Start(ProcessStartInfo)"/> 起一份新的 lisscribe。
/// </summary>
public sealed class ProcessAppRestarter : IAppRestarter
{
    public void LaunchSuccessor()
    {
        // 单文件发行下这里返回的是 apphost 的路径，正是要重新跑的那个东西；
        // SettingsViewModel 给开机自启写注册表时用的也是它，两处一致。
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("取不到当前进程的可执行文件路径，无法重启。");

        var info = BuildSuccessorStartInfo(executable, Environment.ProcessId);

        _ = Process.Start(info)
            ?? throw new InvalidOperationException($"未能拉起新的 lisscribe 进程（{executable}）。");
    }

    /// <summary>
    /// 拼后继进程的启动参数。**单独拎出来是为了测得住**——真去 Start 一次要么起一个真程序，
    /// 要么就得靠缝把执行整个换掉，而参数写错恰恰是这里最可能发生的错。
    /// </summary>
    public static ProcessStartInfo BuildSuccessorStartInfo(string executablePath, int currentPid)
    {
        var info = new ProcessStartInfo
        {
            FileName = executablePath,

            // 必须 false：true 会走 ShellExecuteEx，那条路不认 ArgumentList，
            // 参数被整个丢掉之后一切照常，只是新进程不再等旧的了。
            UseShellExecute = false,

            // 显式给，别继承本进程的。模型目录那头不受影响（ModelDirectory.Canonical
            // 按 AppContext.BaseDirectory 展开），但日后有谁用了相对路径，这里就是防线。
            WorkingDirectory = AppContext.BaseDirectory,
        };

        info.ArgumentList.Add(RestartArguments.WaitForOption);
        info.ArgumentList.Add(currentPid.ToString(CultureInfo.InvariantCulture));

        return info;
    }
}
