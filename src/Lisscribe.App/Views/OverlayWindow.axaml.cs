using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using Lisscribe.App.ViewModels;
using Lisscribe.Core.Abstractions;
using Lisscribe.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lisscribe.App.Views;

/// <summary>
/// 悬浮状态指示器：主屏工作区右下角、无边框、置顶、点击穿透、不抢焦点、不进 Alt+Tab。
/// </summary>
/// <remarks>
/// 三条实测事实定了本类的形状。
/// 一、HWND 在构造函数返回时就有了，不必等 <c>Show()</c>——但在 <c>Show()</c> 之前设扩展样式
/// 没用，<c>Show()</c> 会把 <c>GWL_EXSTYLE</c> 整个写回 Avalonia 自己那一份，四个位一个不剩。
/// 故样式只能设在 <see cref="OnOpened"/> 里。
/// 二、<c>Opened</c> 每次 <c>Show()</c> 都引发（含经由 <c>IsVisible = true</c> 那条路），这正好把
/// 上一条变成自愈；反过来，把 <c>Screens.Changed += ...</c> 写在 <c>Opened</c> 里就是一条无上界的
/// 重复订阅，故订阅挪到构造函数、并在 <see cref="OnClosed"/> 里退掉。
/// 三、跨线程驱动本类的后果分两半：单纯写 <c>Volume</c> / <c>Message</c> 这类属性，经绑定打到控件
/// 上一声不响地成功（那是一个没人拦、也不会报的数据竞争）；而一旦 <c>IsVisible</c> 翻转，
/// <see cref="OnViewModelPropertyChanged"/> 会在写入的那条线程上同步调 <c>Show()</c> / <c>Hide()</c>，
/// 两个方向都会抛 <c>Call from invalid thread</c>。组合根因此必须在
/// <c>Dispatcher.UIThread.Post</c> 里驱动 ViewModel；本类刻意不替它 Post——那会把一条每次会话
/// 都必现的线程错误，换成一个只在渲染时偶发的数据竞争。
/// <para>
/// 两条事件边界包了 catch，第三条刻意不包：<see cref="OnOpened"/> 与
/// <see cref="OnScreensChanged"/> 里逃逸的异常会顺着 <c>Show()</c> ← <c>ApplyState</c> 一路抛回
/// 状态机的线程，一次瞬时的 Win32 失败不该演变成整条会话崩掉。
/// <see cref="OnViewModelPropertyChanged"/> 不包：那里唯一会抛的就是上面那条线程错误，
/// 包起来等于把「接线错了」变成「悬浮条有时候不消失」。
/// </para>
/// </remarks>
public sealed partial class OverlayWindow : Window
{
    /// <summary>距工作区边缘 16px。这是 DIP，高 DPI 屏上要乘 <c>Screen.Scaling</c>。</summary>
    public const int MarginDip = 16;

    /// <summary>
    /// 窗口宽（DIP）。这是画布，不是用户看得见的东西——真正显示出来的是里面那颗胶囊，
    /// 窗口其余部分全透明。取 320 是为了给失败态那句话留地方。
    /// </summary>
    public const double WidthDip = 320d;

    /// <summary>
    /// 窗口高（DIP）。不能按胶囊的高度来定：Windows 有一条最小窗口尺寸
    /// （<c>SM_CXMIN</c>=136、<c>SM_CYMIN</c>=39），顶层窗口矮于它就会被系统夹上去，
    /// 而 Avalonia 随后把夹之后的真尺寸写回 <c>Width</c>/<c>Height</c>——照 30 DIP 声明的后果是
    /// 150% 屏上读回 39.33 DIP、落点跟着差 14 DIP，且这条写回还会把随后设的宽度一并盖掉。
    /// 所以窗口尺寸固定不变、且都在最小值之上，胶囊的大小改在里层的 <c>Border</c> 上。
    /// </summary>
    public const double HeightDip = 48d;

    /// <summary>
    /// 胶囊高（DIP）——这才是用户看得见的那个尺寸。贴在窗口右下角，故它的右下角就是窗口的
    /// 右下角，<see cref="Reposition"/> 算出来的边距对胶囊仍然精确。
    /// </summary>
    public const double PillHeightDip = 30d;

    private readonly IOverlayWindowStyler _styler;
    private readonly OverlayViewModel _viewModel;

    /// <summary>
    /// 事件边界上咽掉的异常总得留个痕迹，否则「悬浮条不穿透了」就成了无从查起的怪事。
    /// 可为 null，此时退化成 <c>NullLogger</c>。
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// 构造时取一次并存住。<c>Close()</c> 之后 <c>TopLevel.Screens</c> 未必还拿得到
    /// （平台实现已经拆了），而退订恰恰发生在那之后。
    /// </summary>
    private readonly Screens _screens;

    /// <summary>
    /// 只为 XAML 预览器与运行期加载器存在：没有一个公开的无参构造，编译期报 AVLN3001，
    /// 而本仓 <c>TreatWarningsAsErrors</c>。
    /// 生产一律走带参构造。这个重载建出来的悬浮条挂着一个没人驱动的 ViewModel，
    /// 于是永远不会显示——若哪天有人在组合根里误用了它，症状就是「指示器不出现」。
    /// </summary>
    public OverlayWindow()
        : this(new OverlayViewModel(), new OverlayWindowStyler())
    {
    }

    public OverlayWindow(
        OverlayViewModel viewModel,
        IOverlayWindowStyler styler,
        ILogger<OverlayWindow>? logger = null)
    {
        _viewModel = viewModel;
        _styler = styler;
        _logger = logger ?? NullLogger<OverlayWindow>.Instance;

        DataContext = viewModel;

        InitializeComponent();

        _screens = Screens;

        Opened += OnOpened;
        Closed += OnClosed;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 笔记本热插外屏、改缩放比例都会触发。只订一次——见类注释第二条。
        _screens.Changed += OnScreensChanged;
    }

    /// <summary>
    /// 按主屏工作区重算落点。公开是因为它有一个外部触发源（显示器变化），
    /// 而不是因为调用方需要挑时机。
    /// </summary>
    public void Reposition()
    {
        var screen = _screens.Primary ?? _screens.All.FirstOrDefault();

        if (screen is null)
        {
            // Screens.Primary 可能为 null。一块屏都没有的时候（远程会话断开、显示器全拔），
            // 摆在哪里都没意义，保持原位即可。
            _logger.LogWarning("一块屏幕也拿不到，悬浮条保持原位");

            return;
        }

        RepositionOn(screen);
    }

    /// <summary>
    /// 把落点算在指定的这块屏上。从 <see cref="Reposition"/> 里分出来，是为了让
    /// 「拿 <c>screen.Scaling</c> 去喂 <see cref="OverlayPlacement.Compute"/>」这一步在任何 DPI 下
    /// 都有测试盖着——那几条既有测试只在非 100% 屏上盖得住这一步，本方法这条是唯一与 DPI 无关的
    /// （它传一块自造的 <see cref="Screen"/>，缝只决定输入、执行仍走这条真路径）。
    /// 取 <c>screen.Scaling</c> 而不是窗口自己的 <c>RenderScaling</c>：前者描述「要摆过去的那块屏」，
    /// 后者描述「它此刻在哪块屏上」，摆过去之前未必相同。
    /// </summary>
    public void RepositionOn(Screen screen)
    {
        var position = OverlayPlacement.Compute(
            screen.WorkingArea, Width, Height, screen.Scaling, MarginDip);

        Position = position;

        // 诊断（开机自启后悬浮条不可见）：Avalonia 的工作区是缓存值，与系统实时值不符时落点会掉进
        // 任务栏底下。两份一起记，日志自己就能分出「摆错了」还是「压根没画出来」。
        _logger.LogInformation(
            "悬浮条落点 {Position}，缩放 {Scaling}，Avalonia 工作区 {Cached}，系统实时工作区 {Live}",
            position, screen.Scaling, screen.WorkingArea, PrimaryWorkArea.Current());
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? nint.Zero;

            // 每次 Show 都要重设：Show() 刚刚把四个位抹掉了。
            if (handle != nint.Zero)
            {
                _styler.MakeClickThrough(handle);
            }
            else
            {
                // 没有句柄就没法设样式。这条本机没复现过（构造完就有句柄），但静默跳过等于
                // 「悬浮条挡住了托盘图标而程序端毫不知情」。
                _logger.LogError("悬浮条没有平台句柄，点击穿透样式这一次没设上");
            }

            Reposition();

            // 诊断（开机自启后悬浮条不可见）：坐标算对了却看不见时，现场只能从系统侧取。
            // 分层属性没设上的窗口整个不可见，而设它那一步刻意丢掉了返回值。
            _logger.LogInformation("悬浮条现状 {State}", OverlayWindowProbe.Describe(handle));
        }
        catch (Exception ex)
        {
            // 这里逃逸的异常会顺着 Show() ← ApplyState 抛回状态机的线程。
            // 一次瞬时的 Win32 失败该退化成「这一次没设上」，不是把整条会话带走。
            SafeLog(ex, "悬浮条显示时设置样式或重排失败");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 刻意不退 Opened：同实例的处理器本就不泄漏，留着它只为一件事——Close() 之后若有人再
        // Show()，四个扩展样式至少还会被重设一次。
        // 别把这当成「支持重开」：下面三行退掉的东西不会回来，重开得到的是个半死窗口。
        // 本类的契约是 Close 即终态；真要支持重开，该做的是把这三行一起挪到别处。
        Closed -= OnClosed;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _screens.Changed -= OnScreensChanged;
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        try
        {
            Reposition();
        }
        catch (Exception ex)
        {
            // 这条是系统在热插外屏 / 改缩放时引发的，抛出去就是「插一次外屏崩一次」。
            SafeLog(ex, "显示器变化后重排悬浮条失败");
        }
    }

    /// <summary>记日志这件事本身也会抛，而这里正是「不许再抛」的地方。</summary>
    private void SafeLog(Exception error, string message)
    {
        try
        {
            _logger.LogError(error, "{Message}", message);
        }
        catch
        {
            // 无处可去。吞掉总好过把异常送回状态机的线程——那正是本 catch 要防的事。
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OverlayViewModel.IsVisible))
        {
            return;
        }

        if (_viewModel.IsVisible)
        {
            Show();
        }
        else
        {
            Hide();
        }
    }

}
