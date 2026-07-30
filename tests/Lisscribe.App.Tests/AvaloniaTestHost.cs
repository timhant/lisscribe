using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 桌面级测试用的 Avalonia 宿主：在一条 STA 线程上把 Avalonia 装配起来并跑它自己的消息循环，
/// 于是测试可以建**真的** Avalonia 窗口，再拿 Win32 API 去量它。
///
/// <para>
/// **为什么不是 <c>Avalonia.Headless</c>**：要验的三件事全是 headless 下不存在的东西
/// —— HWND 的扩展样式、<c>GetWindowRect</c> 量到的物理像素、DPI 变化。headless 后端连窗口
/// 句柄都没有，验出来的只会是它自己那套假屏幕。
/// </para>
///
/// <para>
/// **Avalonia 一个进程只能装配一次**，故这里是进程级单例，且线程置为 background：
/// 测试宿主退出时它跟着走，不会留下孤儿线程。窗口则由各条测试自己关——留在屏幕上就是脏状态。
/// </para>
///
/// <para>
/// 与 <c>Lisscribe.Platform.Tests.TestWindow</c> 同一条理由，这里也自己起线程而不复用
/// <c>MessagePumpThread</c>：预言机的取样范围必须独立于被测对象。
/// </para>
/// </summary>
internal static class AvaloniaTestHost
{
    private static readonly Lazy<IClassicDesktopStyleApplicationLifetime> Host =
        new(Boot, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>在 Avalonia 的 UI 线程上跑一段代码，并把那边抛的异常原样搬回来。</summary>
    public static T Run<T>(Func<T> work)
    {
        _ = Host.Value;

        return Dispatcher.UIThread.InvokeAsync(work).GetTask().GetAwaiter().GetResult();
    }

    public static void Run(Action work) => Run<object?>(() =>
    {
        work();

        return null;
    });

    /// <summary>UI 线程本身（用来构造「不是 UI 线程」这个条件的反面对照）。</summary>
    public static bool IsUiThread => Dispatcher.UIThread.CheckAccess();

    private static IClassicDesktopStyleApplicationLifetime Boot()
    {
        var ready = new TaskCompletionSource<IClassicDesktopStyleApplicationLifetime>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<TestApp>()
                    .UsePlatformDetect()
                    .AfterSetup(builder =>
                        ready.TrySetResult((IClassicDesktopStyleApplicationLifetime)
                            builder.Instance!.ApplicationLifetime!))
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception ex)
            {
                // 装配失败时若不把异常搬出去，下面那个 Wait 会白等满 60 秒再报一个指不到真因的超时。
                ready.TrySetException(ex);
            }
        })
        {
            Name = "avalonia-test-ui",
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Task.Wait(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("Avalonia 未在期限内装配起来");
        }

        return ready.Task.GetAwaiter().GetResult();
    }

    private sealed class TestApp : Application
    {
        public override void Initialize()
        {
            // 没有主题，ProgressBar 之类的控件找不到模板，窗口测得出句柄却画不出内容——
            // 而「画不画得出来」正是疑点 5 要看的事，不能让它坏在缺主题上。
            Styles.Add(new FluentTheme());
        }
    }
}

/// <summary>
/// 所有会碰到真桌面的测试都归这一组，禁并行。
///
/// 理由有二：Avalonia 的 UI 线程只有一条，并行建窗口只是把它们排到同一条队上；
/// 更要紧的是这些测试会抢前台、量鼠标命中，两条同时跑就会互相看到对方的窗口。
/// </summary>
[CollectionDefinition(DesktopCollection.Name, DisableParallelization = true)]
public sealed class DesktopCollection
{
    public const string Name = "desktop";
}
