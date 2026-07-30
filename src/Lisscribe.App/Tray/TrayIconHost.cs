using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;

namespace Lisscribe.App.Tray;

/// <summary>
/// 托盘图标与它的右键菜单。本类的全部复杂度都来自绕行 Avalonia 在 Windows 上的已知缺陷，
/// 菜单本身的内容与文案在 <see cref="TrayMenuModel"/> 里，那一层是纯逻辑。
/// </summary>
/// <remarks>
/// 构造参数取的是 <c>Action</c> 而不是 <c>SessionController</c>：本类只用得上 <c>Pause</c> /
/// <c>Resume</c> 两个方法，收 <c>Action</c> 让「点暂停到底有没有真的暂停」这条路在没有状态机的
/// 情况下也测得了。
/// <para>
/// 暂停与否记在本类自己身上，不读控制器的 <c>State</c>：用户点「暂停热键」时若正在录音，控制器是
/// 把暂停记下来、等这一轮落地才进 <c>Paused</c>，于是那段窗口期里 <c>State</c> 仍是
/// <c>Recording</c>。菜单要反映的是「用户要的是什么」，不是「状态机现在走到哪」——照 <c>State</c>
/// 渲染会让菜单在那段时间里显示成没暂停。
/// </para>
/// </remarks>
public sealed class TrayIconHost(
    Action pause,
    Action resume,
    Action openSettings,
    Action openModelFolder,
    Action openLogFolder,
    Action requestExit,
    ILogger<TrayIconHost> logger)
{
    /// <summary>
    /// 图标资源的 avares 地址。公开是给测试当靶子用的：图标丢了或路径改了，症状是启动时抛
    /// <c>FileNotFoundException</c>，而那时人多半在查托盘代码而不是查资源清单。
    /// </summary>
    public const string IconAssetUri = "avares://Lisscribe.App/Assets/lisscribe.ico";

    private Application? _application;

    private TrayIcon? _trayIcon;

    private bool _isPaused;

    /// <summary>用户当前要的是「停」还是「跑」。见类注释：这不等于控制器的状态。</summary>
    public bool IsPaused => _isPaused;

    public void Attach(Application application)
    {
        _application = application;

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri(IconAssetUri))),
            ToolTipText = TrayMenuModel.TooltipFor(_isPaused),
            IsVisible = true,
        };

        RebuildMenu();

        // 左键单击开配置窗。Avalonia 只给了这一个点击事件，双击、中键都没有。
        _trayIcon.Clicked += (_, _) => Invoke(TrayCommand.OpenSettings);

        TrayIcon.SetIcons(application, [_trayIcon]);

        Log(() => logger.LogInformation(
            "托盘图标已装配。已知限制：explorer.exe 重启后图标不会自动回来（Avalonia 未处理 " +
            "TaskbarCreated 广播），届时需重启本程序。"));
    }

    /// <summary>
    /// 执行一条菜单命令。生产与测试走的是同一个入口：菜单项的 Click 处理器只是转调它。
    /// 整个方法体包在 try 里，是因为它跑在 UI 线程上：从这里逃出去的异常没有第二层接住，
    /// 用户看到的是「点了一下菜单，整个程序没了」。
    /// </summary>
    public void Invoke(TrayCommand command)
    {
        try
        {
            switch (command)
            {
                case TrayCommand.OpenSettings:
                case TrayCommand.About:
                    // 「关于」暂时也开配置窗——单开一个只显示版本号的窗口不值当。
                    openSettings();
                    break;

                case TrayCommand.TogglePause:
                    TogglePause();
                    break;

                case TrayCommand.OpenModelFolder:
                    openModelFolder();
                    break;

                case TrayCommand.OpenLogFolder:
                    openLogFolder();
                    break;

                case TrayCommand.Exit:
                    requestExit();
                    break;

                default:
                    Log(() => logger.LogWarning("托盘菜单收到未登记的命令 {Command}，已忽略。", command));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log(() => logger.LogError(ex, "托盘菜单命令 {Command} 执行失败。", command));
        }
    }

    /// <summary>
    /// 退出前必须先隐藏再释放：有 TrayIcon 时直接退出会崩（avalonia#7588）。
    /// 顺序在进程内观察不到，别写一条自称验了顺序的测试——实测 <c>Dispose()</c> 自己不动
    /// <c>IsVisible</c>（dispose 后读回仍是 True，且不发属性变更通知），dispose 之后再设
    /// <c>IsVisible</c> 也不抛，二次 dispose 也不抛。于是「先隐藏」这一步测得出来，
    /// 而「在 dispose 之前」测不出来——那一条的回报只在进程退出时现形，归手工清单。
    /// </summary>
    public void Detach()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();

        // 把附加属性上的引用一并摘掉：留着它，Application 会一直握着一个已释放的图标，
        // 而下一次 Attach 又会 SetIcons 覆盖它——两个都在时谁先谁后没有定论。
        if (_application is not null)
        {
            TrayIcon.SetIcons(_application, []);
        }

        _trayIcon = null;
        _application = null;
    }

    /// <summary>
    /// 整个菜单重建，不做增量更新。<c>NativeMenu.Opening</c> / <c>NeedsUpdate</c> 在 Windows 上
    /// 从不触发（avalonia#8076），所以「弹出前刷新」这条路是死的；每次状态变化重建一份是唯一能
    /// 保证看到的就是当前状态的做法。菜单只有七项，重建的代价可以忽略。
    /// </summary>
    private void RebuildMenu()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var menu = new NativeMenu();

        foreach (var entry in TrayMenuModel.Build(_isPaused))
        {
            if (entry.IsSeparator)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            // 刻意不设 ToggleType / IsChecked：它们在 Windows 上不渲染（avalonia#8751），
            // 状态由文案自己带。TrayIconHostTests 有一条专门盯着这里别被人「改良」回去。
            var item = new NativeMenuItem(entry.Header!);
            var command = entry.Command!.Value;
            item.Click += (_, _) => Invoke(command);
            menu.Add(item);
        }

        _trayIcon.Menu = menu;
    }

    private void TogglePause()
    {
        // 先动作、后翻状态：动作抛了就当这次没发生，否则菜单会显示成「已暂停」而热键还活着，
        // 那是比报错更坏的一种失败。
        if (_isPaused)
        {
            resume();
        }
        else
        {
            pause();
        }

        _isPaused = !_isPaused;

        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = TrayMenuModel.TooltipFor(_isPaused);
        }

        RebuildMenu();
    }

    /// <summary>
    /// 记日志这件事本身不许把 UI 线程带走。
    /// Lisscribe.Platform 那边是用 <c>SafeLogger</c> 装饰器解决的，但那个类是 internal，跨程序集
    /// 用不了；本类只有三处日志，就地包一层比把装饰器提成公共 API 划算。若日后 App 层的日志点
    /// 变多，该做的是把 <c>SafeLogger</c> 提到 Core 里，而不是继续复制这个方法。
    /// </summary>
    private static void Log(Action write)
    {
        try
        {
            write();
        }
        catch
        {
            // 故意吞掉：sink 坏了不该让托盘跟着坏。Platform 那边有 LoggingHealth 兜底，
            // App 这边暂无——日后该把这两处并到同一个健康计数上。
        }
    }
}
