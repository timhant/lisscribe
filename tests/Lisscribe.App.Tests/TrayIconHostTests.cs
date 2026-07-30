using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Lisscribe.App.Tray;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 托盘壳层：真的 <see cref="TrayIcon"/>、真的 <see cref="NativeMenu"/>。
///
/// <para>
/// 真需要人眼的（图标长什么样、菜单弹出来的版式、退出时不弹崩溃框）留在
/// <c>TrayIconHostManualChecklist.md</c>，其余都在这里。
/// </para>
///
/// <para>
/// **取样口用的是 Avalonia 自己的附加属性 <c>TrayIcon.GetIcons</c>，不是被测类的字段。**
/// 让被测代码自报「我装了什么」，等于拿它自己的账当判据。
/// </para>
///
/// <para>
/// 本组会在系统托盘里短暂放一个真图标，每条测试用完必 <c>Detach</c>。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class TrayIconHostTests
{
    [Fact]
    public void TheIconAsset_IsThereAndLoads()
    {
        // 资源丢了或路径写错，症状是启动时抛，而人多半会先去翻托盘代码。
        // 这一条让它在最便宜的地方现形。
        using var stream = AssetLoader.Open(new Uri(TrayIconHost.IconAssetUri));

        Assert.True(stream.Length > 0);

        // 阴性对照：换个不存在的名字必须抛——否则上一句可能只是 AssetLoader 对什么都点头。
        Assert.ThrowsAny<Exception>(
            () => AssetLoader.Open(new Uri("avares://Lisscribe.App/Assets/no-such-icon.ico")));
    }

    [Fact]
    public void Attach_PutsAVisibleIconOnTheApplication_WithTheMenuTheModelDescribes()
    {
        WithHost((host, app) =>
        {
            Assert.Single(IconsOf(app));

            var icon = IconsOf(app)[0];
            Assert.True(icon.IsVisible);
            Assert.Equal("lisscribe", icon.ToolTipText);
            Assert.NotNull(icon.Icon);

            Assert.Equal(
                TrayMenuModel.Build(isPaused: false).Select(e => e.Header).ToArray(),
                HeadersOf(icon));
        });
    }

    [Fact]
    public void Attach_SetsNoCheckMarksAnywhere()
    {
        // **这一条盯的是 avalonia#8751 的绕行别被人「改良」掉。** 勾选标记在 Windows 上不渲染，
        // 谁把状态改成靠 IsChecked 表达，用户看到的就是「点了没反应」——而代码读起来更漂亮。
        WithHost((host, app) =>
        {
            foreach (var item in ItemsOf(IconsOf(app)[0]))
            {
                Assert.Equal(NativeMenuItemToggleType.None, item.ToggleType);
                Assert.False(item.IsChecked);
            }
        });
    }

    [Fact]
    public void TogglePause_CallsPauseThenResume_AndRebuildsTheWholeMenu()
    {
        var calls = new List<string>();

        WithHost(
            (host, app) =>
            {
                var icon = IconsOf(app)[0];
                var firstMenu = icon.Menu;

                host.Invoke(TrayCommand.TogglePause);

                Assert.True(host.IsPaused);
                Assert.Equal(["pause"], calls);
                Assert.Contains("暂停", icon.ToolTipText);
                Assert.Contains("恢复热键", HeadersOf(icon));

                // **菜单必须是新的一份**：avalonia#8076 让「弹出前刷新」这条路是死的，
                // 所以状态一变就得整份重建。比对引用而不是比对文案——文案那半上面已经断言过，
                // 而「原地改了第二项的 Header」在真机上同样不会更新已建好的原生菜单。
                Assert.NotSame(firstMenu, icon.Menu);

                host.Invoke(TrayCommand.TogglePause);

                Assert.False(host.IsPaused);
                Assert.Equal(["pause", "resume"], calls);
                Assert.Equal("lisscribe", icon.ToolTipText);
                Assert.Contains("暂停热键", HeadersOf(icon));
            },
            pause: () => calls.Add("pause"),
            resume: () => calls.Add("resume"));
    }

    [Fact]
    public void Invoke_WhenTheActionThrows_KeepsTheMenuHonest_AndDoesNotEscape()
    {
        // 点一下菜单就把程序带走是不可接受的。更要紧的是第二条断言：动作失败时状态**不许**翻
        // ——菜单显示「已暂停」而热键还活着，比直接报错坏得多。
        WithHost(
            (host, app) =>
            {
                host.Invoke(TrayCommand.TogglePause);

                Assert.False(host.IsPaused);
                Assert.Contains("暂停热键", HeadersOf(IconsOf(app)[0]));
            },
            pause: () => throw new InvalidOperationException("控制器拒绝了暂停"));
    }

    [Fact]
    public void Detach_HidesTheIcon_AndTakesItOffTheApplication_AndCanBeCalledTwice()
    {
        // 隐藏这一步测得出来：实测 Dispose() 自己不动 IsVisible、也不发通知，
        // 所以下面这个通知只可能是 Detach 里那一行引发的（见 TrayIconHost.Detach 的注释）。
        // 「隐藏在 dispose 之前」这个顺序在进程内观察不到，那一条归手工清单。
        var hidden = false;

        var host = NewHost();
        var app = AvaloniaTestHost.Run(() =>
        {
            var application = Application.Current!;
            host.Attach(application);

            var icon = IconsOf(application)[0];
            icon.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty || e.Property.Name == nameof(TrayIcon.IsVisible))
                {
                    hidden = e.NewValue is false;
                }
            };

            host.Detach();
            host.Detach();

            return application;
        });

        Assert.True(hidden, "Detach 没有把托盘图标隐藏，avalonia#7588 的绕行就是空的");
        Assert.Empty(AvaloniaTestHost.Run(() => IconsOf(app)));
    }

    /// <summary>
    /// Application 上挂着的托盘图标集合。<c>null</c> 与空是同一件事（都表示没有图标），
    /// 但断言的话要说得出「有几个」，故在这里统一成集合。
    /// </summary>
    private static IReadOnlyList<TrayIcon> IconsOf(Application app) =>
        TrayIcon.GetIcons(app) ?? (IReadOnlyList<TrayIcon>)[];

    private static TrayIconHost NewHost(Action? pause = null, Action? resume = null) =>
        new(
            pause ?? (() => { }),
            resume ?? (() => { }),
            openSettings: () => { },
            openModelFolder: () => { },
            openLogFolder: () => { },
            requestExit: () => { },
            NullLogger<TrayIconHost>.Instance);

    /// <summary>
    /// 装配、跑断言、无论如何都拆掉。留一个真图标在托盘里就是脏状态，
    /// 而下一条测试会因为 <c>GetIcons</c> 里多出一个而以指不到真因的方式失败。
    /// </summary>
    private static void WithHost(Action<TrayIconHost, Application> assert, Action? pause = null, Action? resume = null)
    {
        var host = NewHost(pause, resume);

        AvaloniaTestHost.Run(() =>
        {
            var app = Application.Current!;

            try
            {
                host.Attach(app);
                assert(host, app);
            }
            finally
            {
                host.Detach();
            }
        });
    }

    /// <summary>
    /// 菜单文案，分隔符记为 <c>null</c>，好跟 <see cref="TrayMenuModel"/> 直接比对。
    ///
    /// <para>
    /// **实测踩到的一处**：<c>NativeMenuItemSeparator</c> 是 <c>NativeMenuItem</c> 的子类，
    /// 且 <c>Header</c> 是字符串 <c>"-"</c>，不是 <c>null</c>。照「is NativeMenuItem 就取 Header」
    /// 写出来，分隔符会当成一个叫「-」的菜单项混进比对里。
    /// </para>
    /// </summary>
    private static string?[] HeadersOf(TrayIcon icon) =>
        icon.Menu!.Items
            .Select(i => i is NativeMenuItemSeparator ? null : (i as NativeMenuItem)?.Header)
            .ToArray();

    private static IEnumerable<NativeMenuItem> ItemsOf(TrayIcon icon) =>
        icon.Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator);
}
