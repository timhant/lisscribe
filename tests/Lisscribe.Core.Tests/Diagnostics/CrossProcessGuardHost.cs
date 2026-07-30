using System.Diagnostics;
using Lisscribe.Core.Diagnostics;
using Xunit;

namespace Lisscribe.Core.Tests.Diagnostics;

/// <summary>
/// 跨进程互斥测试的「另一个进程」。
///
/// 单实例守卫要防的正是第二个进程，而同进程内再取一次句柄根本测不出东西：
/// 互斥体对持有它的线程是可重入的，同线程第二次 WaitOne 必然返回 true。
/// 一个「开第二个句柄期望 false」的用例今天就会以错误的理由通过，
/// 且把实现整个删掉它还是通过——所以真话只能由一个独立进程来说。
///
/// 手法：测试宿主本身就是可执行文件，用 -method 过滤器把它再拉起一次，只跑本类这个
/// Explicit 用例（Explicit 用例默认不参与常规运行，不污染统计，也不会递归拉起自己）。
/// 参数只能走环境变量——xunit 命令行不接受自定义参数。
/// </summary>
public sealed class CrossProcessGuardActor
{
    internal const string MutexNameVariable = "LISSCRIBE_XPROC_MUTEX";
    internal const string ResultPathVariable = "LISSCRIBE_XPROC_RESULT";
    internal const string ModeVariable = "LISSCRIBE_XPROC_MODE";

    /// <summary>取到即长睡等父进程 Kill，用于模拟「崩溃时仍持有互斥体」。</summary>
    internal const string HoldMode = "hold";

    /// <summary>取一次即退出，用于测「另一个进程占着时我能不能起来」。</summary>
    internal const string TryMode = "try";

    /// <summary>父进程失联时的兜底：不能把子进程永远留在系统里。</summary>
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(60);

    [Fact(Explicit = true)]
    public void Act()
    {
        var name = Environment.GetEnvironmentVariable(MutexNameVariable);
        var resultPath = Environment.GetEnvironmentVariable(ResultPathVariable);

        Assert.SkipWhen(
            string.IsNullOrEmpty(name) || string.IsNullOrEmpty(resultPath),
            $"本用例只在跨进程测试拉起的子进程中运行（未设 {MutexNameVariable}/{ResultPathVariable}）。");

        var acquired = SingleInstanceGuard.TryAcquire(name!, out var guard);
        WriteResultAtomically(resultPath!, acquired);

        if (Environment.GetEnvironmentVariable(ModeVariable) == HoldMode)
        {
            // 故意不 Dispose：父进程会强杀本进程，好让互斥体变成 abandoned 状态。
            Thread.Sleep(HoldTimeout);
            return;
        }

        guard?.Dispose();
    }

    /// <summary>
    /// 先写临时文件再改名。父进程是靠这个文件轮询判断「子进程已经走完 TryAcquire」的，
    /// 直接写目标路径会让它读到长度为 0 的半成品。
    /// </summary>
    private static void WriteResultAtomically(string resultPath, bool acquired)
    {
        var temp = resultPath + ".tmp";
        File.WriteAllText(temp, acquired ? "true" : "false");
        File.Move(temp, resultPath, overwrite: true);
    }
}

/// <summary>拉起 <see cref="CrossProcessGuardActor"/> 子进程的工具。</summary>
internal static class CrossProcessGuardHost
{
    private const string ActorMethodFilter =
        "Lisscribe.Core.Tests.Diagnostics.CrossProcessGuardActor.Act";

    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(60);

    /// <summary>在独立进程里跑一次 TryAcquire，返回它是否拿到。</summary>
    public static bool TryAcquireInChildProcess(string mutexName)
    {
        using var child = new ChildProcess(mutexName, CrossProcessGuardActor.TryMode);
        return child.WaitForResult();
    }

    /// <summary>起一个持有互斥体不放的子进程，直到 <see cref="ChildProcess.Dispose"/> 或 Crash。</summary>
    public static ChildProcess HoldInChildProcess(string mutexName)
    {
        var child = new ChildProcess(mutexName, CrossProcessGuardActor.HoldMode);
        Assert.True(child.WaitForResult(), "子进程未能取得互斥体，测试前提不成立");
        return child;
    }

    internal sealed class ChildProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly string _resultPath;

        public ChildProcess(string mutexName, string mode)
        {
            _resultPath = Path.Combine(Path.GetTempPath(), $"lisscribe-xproc-{Guid.NewGuid():N}");
            _process = Process.Start(BuildStartInfo(mutexName, mode, _resultPath))
                       ?? throw new InvalidOperationException("无法拉起测试宿主子进程");
            _stdout = _process.StandardOutput.ReadToEndAsync();
            _stderr = _process.StandardError.ReadToEndAsync();
        }

        /// <summary>
        /// 子进程的 pid。给 <c>PreviousInstanceWaiter</c> 那组测试用——它要一个「真的活着、
        /// 且什么时候死由测试说了算」的进程，本类恰好就是。
        /// </summary>
        public int Pid => _process.Id;

        /// <summary>轮询等子进程把 TryAcquire 的结果落盘。</summary>
        public bool WaitForResult()
        {
            var deadline = DateTime.UtcNow + ChildTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(_resultPath))
                {
                    return bool.Parse(File.ReadAllText(_resultPath));
                }

                // 子进程先退了却没留结果，说明它压根没跑到用例里（过滤器写错、宿主启动失败……），
                // 此时必须报出子进程的输出，否则只会看到一句没头没脑的超时。
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(DescribeFailure("子进程未写出结果即退出"));
                }

                Thread.Sleep(20);
            }

            throw new TimeoutException(DescribeFailure($"等待子进程结果超过 {ChildTimeout.TotalSeconds} 秒"));
        }

        /// <summary>强杀子进程，模拟崩溃——持有者没释放就没了，互斥体应变为 abandoned。</summary>
        public void Crash()
        {
            _process.Kill(entireProcessTree: true);
            Assert.True(_process.WaitForExit((int)ChildTimeout.TotalMilliseconds), "子进程未在超时内退出");
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit((int)ChildTimeout.TotalMilliseconds);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已回收，无事可做。
            }
            finally
            {
                _process.Dispose();
                File.Delete(_resultPath);
            }
        }

        private string DescribeFailure(string headline) =>
            $"{headline}。ExitCode={(_process.HasExited ? _process.ExitCode.ToString() : "(仍在运行)")}\n" +
            $"stdout:\n{SafeRead(_stdout)}\nstderr:\n{SafeRead(_stderr)}";

        private static string SafeRead(Task<string> output) =>
            output.Wait(TimeSpan.FromSeconds(5)) ? output.Result : "(未能读取)";
    }

    private static ProcessStartInfo BuildStartInfo(string mutexName, string mode, string resultPath)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        // 宿主可能是 apphost 可执行文件，也可能是 `dotnet exec xxx.dll`——后者要把 dll 补回去。
        var host = Environment.ProcessPath
                   ?? throw new InvalidOperationException("取不到当前进程路径，无法拉起子进程");
        info.FileName = host;
        if (IsDotnetMuxer(host))
        {
            info.ArgumentList.Add(typeof(CrossProcessGuardActor).Assembly.Location);
        }

        info.ArgumentList.Add("-method");
        info.ArgumentList.Add(ActorMethodFilter);
        info.ArgumentList.Add("-explicit");
        info.ArgumentList.Add("only");
        info.ArgumentList.Add("-noLogo");
        info.ArgumentList.Add("-noColor");

        info.Environment[CrossProcessGuardActor.MutexNameVariable] = mutexName;
        info.Environment[CrossProcessGuardActor.ResultPathVariable] = resultPath;
        info.Environment[CrossProcessGuardActor.ModeVariable] = mode;

        // dotnet test 的新测试平台（MTP）用环境变量告诉宿主「你是被平台拉起来的」。
        // 子进程继承后会改走 MTP 命令行模式，于是不认识上面这套 xunit 原生参数。必须摘掉。
        foreach (var key in info.Environment.Keys
                     .Where(k => k.StartsWith("TESTINGPLATFORM", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            info.Environment.Remove(key);
        }

        return info;
    }

    private static bool IsDotnetMuxer(string host) =>
        string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase);
}
