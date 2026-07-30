using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 「另一个进程正占着剪贴板」这个现场的制造者。
///
/// **两件事都是实测定的，缺一条这个现场就造不出来**（Win11 22631）：
///
/// 一、**必须是另一个进程**：同一进程的另一条线程握着剪贴板时，本线程 <c>OpenClipboard</c>
///    照样返回 TRUE、lastError = 0。
///
/// 二、**持有方必须传一个真 HWND**：持有方用 <c>OpenClipboard(NULL)</c> 时，别的进程不但
///    开得成，连 <c>EmptyClipboard</c> + <c>SetClipboardData</c> 都成，读回来是闯入者的内容 ——
///    也就是说传 0 根本没锁住任何东西。改成 <c>OpenClipboard(hwnd)</c> 之后，
///    另一进程立刻拿到 **err = 5（ERROR_ACCESS_DENIED）**。
///    第一版本类写的是传 0，于是这条测试拿着一个「持有中」的假前提跑，被测方一路成功返回 Ok。
///
/// 这条分支正是 <see cref="ClipboardTextInjector"/> 那圈重试与「写失败要报 Failed 而不是 Ok」
/// 唯一的触发点。不起子进程、不给真窗口，那段代码就是零覆盖。
///
/// 手法：测试宿主自己就是可执行文件，用 <c>-method</c> 过滤器把它再拉起一次，
/// 只跑本类这个 Explicit 用例。
///
/// **「持有剪贴板的进程被 Kill 会不会把整台机器的剪贴板挂到重启」——已实测，不会。**
/// 这是本设施最不该留的脏状态，故单独验过：子进程用真 HWND 打开剪贴板并持有，
/// 此时父进程 <c>OpenClipboard</c> 得 <c>False / err 5</c>；<c>Kill(entireProcessTree)</c>
/// 之后 200ms 再试，得 <c>True / err 0</c>。win32k 在持有线程终止时会自行释放。
/// </summary>
public sealed class ClipboardHolderActor
{
    internal const string HoldMillisecondsVariable = "LISSCRIBE_CLIPBOARD_HOLD_MS";
    internal const string ReadyPathVariable = "LISSCRIBE_CLIPBOARD_READY";

    [Fact(Explicit = true)]
    public void Act()
    {
        var holdMilliseconds = Environment.GetEnvironmentVariable(HoldMillisecondsVariable);
        var readyPath = Environment.GetEnvironmentVariable(ReadyPathVariable);

        Assert.SkipWhen(
            string.IsNullOrEmpty(holdMilliseconds) || string.IsNullOrEmpty(readyPath),
            $"本用例只在剪贴板占用测试拉起的子进程中运行（未设 {HoldMillisecondsVariable}/{ReadyPathVariable}）。");

        // 窗口不必显示，也不必抢前台——只是 OpenClipboard 的 owner。
        using var owner = new TestWindow("clipboard-holder");
        Assert.True(OpenClipboard(owner.Handle), "子进程打不开剪贴板，测试前提不成立");

        try
        {
            // 先写临时文件再改名：父进程靠这个文件判断「剪贴板已被占住」，
            // 直接写目标路径会让它读到长度为 0 的半成品，从而抢跑。
            var temp = readyPath + ".tmp";
            File.WriteAllText(temp, "held");
            File.Move(temp, readyPath!, overwrite: true);

            Thread.Sleep(int.Parse(holdMilliseconds!));
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
}

/// <summary>拉起 <see cref="ClipboardHolderActor"/> 子进程，并在它真正占住剪贴板之后才返回。</summary>
internal sealed class ClipboardHolderProcess : IDisposable
{
    private const string ActorMethodFilter =
        "Lisscribe.Platform.Tests.ClipboardHolderActor.Act";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    private readonly string _readyPath;

    public ClipboardHolderProcess(TimeSpan holdFor)
    {
        _readyPath = Path.Combine(Path.GetTempPath(), $"lisscribe-clipboard-{Guid.NewGuid():N}");
        _process = Process.Start(BuildStartInfo(holdFor, _readyPath))
                   ?? throw new InvalidOperationException("无法拉起测试宿主子进程");
        _stdout = _process.StandardOutput.ReadToEndAsync();
        _stderr = _process.StandardError.ReadToEndAsync();

        WaitUntilHeld();
    }

    private void WaitUntilHeld()
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(_readyPath))
            {
                return;
            }

            // 子进程先退了却没占住，说明它压根没跑到用例里（过滤器写错、宿主启动失败……）。
            // 此时必须报出子进程的输出，否则只会看到一句没头没脑的超时。
            if (_process.HasExited)
            {
                throw new InvalidOperationException(Describe("子进程未占住剪贴板即退出"));
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException(Describe($"等待子进程占住剪贴板超过 {StartupTimeout.TotalSeconds} 秒"));
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit((int)StartupTimeout.TotalMilliseconds);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已回收，无事可做。
        }
        finally
        {
            _process.Dispose();
            File.Delete(_readyPath);

            // 子进程恰好在「写 .tmp」与「改名」之间被 Kill 时，那个 .tmp 会永远留在 %TEMP% 里。
            // 它是我们让子进程写的，就由我们收。
            File.Delete(_readyPath + ".tmp");
        }
    }

    private string Describe(string headline) =>
        $"{headline}。ExitCode={(_process.HasExited ? _process.ExitCode.ToString() : "(仍在运行)")}\n" +
        $"stdout:\n{SafeRead(_stdout)}\nstderr:\n{SafeRead(_stderr)}";

    private static string SafeRead(Task<string> output) =>
        output.Wait(TimeSpan.FromSeconds(5)) ? output.Result : "(未能读取)";

    private static ProcessStartInfo BuildStartInfo(TimeSpan holdFor, string readyPath)
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
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(typeof(ClipboardHolderActor).Assembly.Location);
        }

        info.ArgumentList.Add("-method");
        info.ArgumentList.Add(ActorMethodFilter);
        info.ArgumentList.Add("-explicit");
        info.ArgumentList.Add("only");
        info.ArgumentList.Add("-noLogo");
        info.ArgumentList.Add("-noColor");

        info.Environment[ClipboardHolderActor.HoldMillisecondsVariable] =
            ((int)holdFor.TotalMilliseconds).ToString();
        info.Environment[ClipboardHolderActor.ReadyPathVariable] = readyPath;

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
}
