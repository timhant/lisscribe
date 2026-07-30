using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Lisscribe.Core.Diagnostics;

namespace Lisscribe.App;

internal static partial class Program
{
    /// <summary>
    /// 改名会让新旧两版在同一台机器上互不相认，于是「升级时旧版还开着」变成两个实例同抢热键。
    /// </summary>
    internal const string InstanceName = "lisscribe-single-instance";

    /// <summary>
    /// 置为 "1" 时不弹窗，只返回退出码。
    /// 这是有意留的测试接缝，不是调试开关：MessageBox 是模态的，没有它，任何「连开两次看第二次
    /// 是不是被挡住」的自动化验证都会挂死在对话框上，直到超时被杀。
    /// </summary>
    internal const string HeadlessVariable = "LISSCRIBE_HEADLESS";

    /// <summary>
    /// 没能归类的意外异常。不并进 3：3 是「单实例检查做不成」，有确定的成因与说辞，排障时会
    /// 照着那条线索去查；意外异常并进去只会把人引向一个不存在的互斥体问题。
    /// </summary>
    private const int UnexpectedFailureExitCode = 4;

    /// <summary>
    /// <see cref="StartupBlockReason"/> 加了新值却忘了在 <see cref="ExitCodeFor"/> 登记时走这里。
    /// 同样不并进 3，理由同上。
    /// </summary>
    private const int UnmappedBlockReasonExitCode = 99;

    /// <summary>
    /// 换档重启时，等上一个实例退出等到超时。同样不并进 1（已有实例在跑）或 3：
    /// 那两个各有确定成因，混进去只会把人引到别处。这一条的成因唯一——旧进程卡住了。
    /// </summary>
    private const int RestartWaitTimedOutExitCode = 5;

    /// <summary>
    /// 等上一个实例退出的时限。旧进程正常收尾很快，但 <c>SessionController.ShutdownAsync</c>
    /// 光等工作线程就给了 10s，后面还要 Dispose 引擎——Qwen3 档若有推理在途，那一步要等它跑完。
    /// 故 30s 是「正常收尾绝对够、真卡住又不至于让人干等」的那个数。
    /// </summary>
    private static readonly TimeSpan RestartWaitTimeout = TimeSpan.FromSeconds(30);

    private const uint MessageBoxOk = 0x00000000;

    private const uint MessageBoxIconError = 0x00000010;

    /// <summary>
    /// 让对话框不被别的窗口盖住。开机自启、脚本拉起、升级器拉起全是非前台调用方，
    /// 而那恰恰是「启动失败」最需要被看见的场景。
    /// </summary>
    /// <remarks>
    /// 用 MB_SYSTEMMODAL 而不是 MB_TOPMOST，这是实测定的：MSDN 说 MB_TOPMOST「以 WS_EX_TOPMOST
    /// 样式创建消息框」，而实测 exStyle 里根本没有那一位、对话框照样被遮挡；换 MB_SYSTEMMODAL
    /// 则 exStyle 带上 0x08、不被遮挡。此结论经两人各自写探针独立复现，勿据文档改回。
    /// MB_SYSTEMMODAL 在现代 Windows 上不再「冻住系统」，文档明写它只是给对话框加上
    /// WS_EX_TOPMOST，且不影响用户操作其它窗口——正是这里要的效果。
    /// </remarks>
    private const uint MessageBoxSystemModal = 0x00001000;

    /// <summary>
    /// 只在调用方本就握有前台权时才生效（双击启动即是），被前台锁挡住时是空操作。
    /// 留着它是因为双击那条路值这一行，但「能被看见」这件事靠的是上面那个 SYSTEMMODAL，不是它。
    /// </summary>
    private const uint MessageBoxSetForeground = 0x00010000;

    [STAThread]
    public static int Main(string[] args)
    {
        // 顶层兜底。只堵已知异常名单是不够的——本程序是 WinExe，没有控制台，未捕获异常的那段
        // stderr 用户根本看不到，用户看到的只是「双击一闪即没」，退出码 0xE0434352。
        try
        {
            return RunStartup(args);
        }
        catch (Exception ex)
        {
            ReportFailure(
                $"lisscribe 启动时遇到未预料的错误，已停止启动。\n\n{ex.GetType().Name}: {ex.Message}");

            return UnexpectedFailureExitCode;
        }
    }

    /// <summary>
    /// Avalonia 的装配描述。除了启动，它还有第二个调用方：VS / Rider 的 XAML 预览器。
    /// </summary>
    /// <remarks>
    /// 预览器加载程序集之后按名反射找入口类型上的这个方法，找不到就退而在程序集里找
    /// <see cref="Avalonia.Application"/> 的子类，两条都落空时报
    /// <c>Unable to resolve AppBuilder from Lisscribe.App assembly</c>，进程当场退出，
    /// 而 VS 那头只显示「No Entry Point」外加一条读 socket 失败的 IOException（那条是果不是因）。
    /// 故本方法有三条不可改的地方，各由 <c>AppEntryPointTests</c> 钉住：名字必须是
    /// <c>BuildAvaloniaApp</c>、必须静态无参、必须真的返回一个非 null 的 <see cref="AppBuilder"/>。
    /// 不加 <c>.WithInterFont()</c>：那要 <c>Avalonia.Fonts.Inter</c> 包，本仓没引，
    /// 且悬浮条只显示中文提示，跟系统字体反而少一份 4MB 的字体资源。
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static int RunStartup(string[] args)
    {
        // 必须排在 StartupPreflight.Run 之前。换档重启时旧进程还攥着单实例互斥体，先做检查就会
        // 被判「已有实例在跑」当场退出——用户看到的是「点了立即重启，程序直接没了」。
        if (RestartArguments.TryParseWaitForPid(args, out var previousPid)
            && PreviousInstanceWaiter.Wait(previousPid, RestartWaitTimeout)
                == PreviousInstanceWaitOutcome.TimedOut)
        {
            // 不强杀旧进程：那会跳过摘钩子、停采集、Dispose 引擎那一串，还可能杀掉一次正在落盘
            // 的识别。真等满 30 秒只有一种可能——它卡住了，那正是不该硬闯的时候。
            ReportFailure(
                $"lisscribe 未能重启：上一个实例（进程 {previousPid}）在 " +
                $"{RestartWaitTimeout.TotalSeconds:0} 秒内没有退出。" +
                "请在任务管理器中结束 Lisscribe.App 之后重新打开。");

            return RestartWaitTimedOutExitCode;
        }

        var result = StartupPreflight.Run(InstanceName, AppContext.BaseDirectory);

        // using 提到分支之前：所有返回路径确实都释放了守卫，但那靠的是「每条 return 都记得」这个
        // 约定。摆在这里，契约在结构上就违背不了——被拦下时 Guard 为 null，using null 是空操作。
        using (result.Guard)
        {
            if (!result.CanStart)
            {
                // 本程序是 WinExe，没有控制台，写 Console.Error 等于写进虚空；用户双击运行看到的
                // 是「闪一下就没了」，正是最难诊断的失败形态。
                ReportFailure(result.Message!);

                return ExitCodeFor(result.Reason);
            }

            // guard 必须活到 Avalonia 的消息循环返回为止，别把这个 using 缩掉——提前释放就等于
            // 本进程还跑着、互斥体已经空了，第二个实例进得来。
            //
            // ShutdownMode 必须显式给 OnExplicitShutdown：本程序是托盘型，默认的
            // OnLastWindowClose 会在配置窗关掉之后把整个进程关掉，而那时托盘图标还在，
            // 用户看到的是「点了个叉，程序没了」。
            //
            // args 原样交给 Avalonia（它只存进 lifetime.Args，不解析）。本程序自己认的只有
            // --wait-for，且已在上面处理完。
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
    }

    private static int ExitCodeFor(StartupBlockReason reason) => reason switch
    {
        StartupBlockReason.AlreadyRunning => 1,
        StartupBlockReason.ProgramDirectoryNotWritable => 2,
        StartupBlockReason.SingleInstanceCheckFailed => 3,

        // None 到不了这里（只在 CanStart 为假时调用）。这个分支真正防的是「往
        // StartupBlockReason 加了新值、却忘了在这里登记」。
        _ => UnmappedBlockReasonExitCode,
    };

    private static void ReportFailure(string message)
    {
        if (Environment.GetEnvironmentVariable(HeadlessVariable) == "1")
        {
            return;
        }

        var shown = MessageBoxW(
            nint.Zero,
            message,
            "lisscribe",
            MessageBoxOk | MessageBoxIconError | MessageBoxSystemModal | MessageBoxSetForeground);

        if (shown == 0)
        {
            // 返回 0 即没弹出来（无交互桌面、session 0、句柄耗尽）。此时退出码是用户唯一的线索，
            // 但若有人是从控制台或脚本里重定向着跑的，这一行还能捞到。
            Console.Error.WriteLine($"lisscribe: 无法显示错误对话框，原文如下：{message}");
        }
    }

    /// <summary>
    /// 私有声明，不进 <c>Lisscribe.Platform/Native/User32.cs</c>：启动失败提示是这一层唯一需要的
    /// Win32 调用，多这一处重复远比多一个跨层的共享依赖划算。
    /// </summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
