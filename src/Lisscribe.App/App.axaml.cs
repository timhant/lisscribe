using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lisscribe.App.Composition;

namespace Lisscribe.App;

/// <summary>
/// Avalonia 应用实例：加载主题，装配组合根。
/// </summary>
/// <remarks>
/// 它同时是 XAML 预览器的第二条解析路径——预览器先在入口类型上找
/// <see cref="Program.BuildAvaloniaApp"/>，找不到才在程序集里找 <see cref="Application"/> 的子类，
/// 两条都落空时 VS 报「No Entry Point」。两条各有测试钉着，别删成「反正另一条还在」：预览器版本
/// 换了走哪条不由本仓说了算。
/// </remarks>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 组合根的入口。不在这里 await 任何东西：Avalonia 要本方法返回之后才跑消息循环，在这里等
    /// 模型加载（默认档 1–2s、高精度档 4–5s）会让托盘图标晚出现同样久，用户会以为程序没起来。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ShutdownMode 必须由 Program 那头传 OnExplicitShutdown：本程序是托盘型，默认的
            // OnLastWindowClose 会在配置窗关掉之后把整个进程关掉。
            // desktop.Shutdown 有默认参数，直接当 Action 传是 method group 转换失败，故包一层。
            _services = new AppServices(AppContext.BaseDirectory, requestExit: () => desktop.Shutdown());

            _services.Tray.Attach(this);

            desktop.ShutdownRequested += (_, _) =>
            {
                // 同步等收尾跑完：这里之后 Avalonia 就要拆平台层了，
                // 而状态机的收尾要停采集、等在途识别落地。等不到就是「退出时丢最后一句」。
                _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };

            // 启动是异步的，但不能让它把异常吞了：可预期的失败（模型缺失、热键非法）已由
            // AppServices.StartAsync 翻成悬浮条提示，逃到这里的都是意外。
            _ = _services.StartAsync().ContinueWith(
                t => Console.Error.WriteLine($"lisscribe 启动失败：{t.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private AppServices? _services;
}
