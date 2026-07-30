using System.Reflection;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Lisscribe.App.Views;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 钉住 <b>Avalonia XAML 预览器解析本程序集的那两条路径</b>。
///
/// <para>
/// 预览器（<c>Avalonia.DesignerSupport</c>）拿到 <c>Lisscribe.App.dll</c> 之后只做两次查找：
/// 先看入口方法所在的类型上有没有静态的 <c>BuildAvaloniaApp()</c>，没有则退而在整个程序集里
/// 找一个 <see cref="Application"/> 的子类。两条都落空时它报
/// <c>Unable to resolve AppBuilder from Lisscribe.App assembly.</c> 随即自尽，
/// VS 侧看到的是「No Entry Point」加一条读 socket 失败的 <c>IOException</c>——
/// <b>那条 IOException 是果不是因</b>，照着它查会一路查到网络层去。
/// </para>
///
/// <para>
/// 症状是「设计器打不开 <c>OverlayWindow.axaml</c>」：<c>Program.Main</c> 止于 <c>return 0</c>、
/// 程序集里既无 <c>BuildAvaloniaApp</c> 也无 <see cref="Application"/> 子类时两条路径同时落空。
/// 故这两条测试**必须都用反射走**，与预览器同形；直接 <c>typeof(App)</c> 只能证明类型编得出来，
/// 证明不了预览器找得到它。
/// </para>
///
/// <para>
/// 不进 <see cref="DesktopCollection"/>：全程只做反射与一次 <c>AppBuilder</c> 构造
/// （<c>AppBuilder.Configure&lt;T&gt;()</c> 只存一个工厂委托，既不实例化应用也不装配平台），
/// 不碰真桌面，也不会与 <c>AvaloniaTestHost</c> 那条 UI 线程抢什么。
/// </para>
/// </summary>
public sealed class AppEntryPointTests
{
    /// <summary>
    /// 从被测程序集里的一个**与入口无关**的类型取程序集句柄。取样范围独立于被测对象：
    /// 若从 <c>Program</c> 取，这条测试就成了「Program 所在的程序集里有 Program」。
    /// </summary>
    private static readonly Assembly AppAssembly = typeof(OverlayWindow).Assembly;

    [Fact]
    public void TheEntryPointType_ExposesABuildAvaloniaAppMethod_ThatReallyReturnsAnAppBuilder()
    {
        var entryPoint = AppAssembly.EntryPoint;

        // 阴性对照兼前提：本项目禁用顶级语句，入口必是手写的 Main，其 DeclaringType 才是
        // 预览器要翻的那个类型。哪天有人改成顶级语句，编译器生成的 <Main>$ 会挂在别的类上，
        // 这一行会先炸，而不是让下面那次查找莫名其妙地落空。
        Assert.NotNull(entryPoint);

        var method = entryPoint!.DeclaringType!.GetMethod(
            "BuildAvaloniaApp",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(AppBuilder), method!.ReturnType);

        // 光有签名不够：预览器是真去调它的，调出 null 或抛异常一样打不开设计器。
        var builder = method.Invoke(null, parameters: null);
        Assert.IsType<AppBuilder>(builder);
    }

    [Fact]
    public void TheAssembly_ContainsExactlyOneConcreteApplicationSubtype_WithAPublicParameterlessConstructor()
    {
        var applications = AppAssembly.GetTypes()
            .Where(type => typeof(Application).IsAssignableFrom(type) && !type.IsAbstract)
            .ToArray();

        // 恰好一个：多一个，预览器的第二条路径就得靠 GetTypes() 的返回顺序去猜该用哪个，
        // 那是个没有保证的顺序。
        var applicationType = Assert.Single(applications);

        // 预览器 new 出来的是它，无参构造缺了就只有一个 MissingMethodException。
        Assert.NotNull(applicationType.GetConstructor(Type.EmptyTypes));
    }
}

/// <summary>
/// <see cref="App"/> 的 XAML 那一半：预览器找得到类型只是第一关，<c>Initialize()</c> 里
/// 加载不出东西的话，设计器照样只画得出一个没有控件模板的灰框。
///
/// <para>
/// 归 <see cref="DesktopCollection"/>：要在 <see cref="AvaloniaTestHost"/> 那条已装配好的 UI 线程上
/// 建 <see cref="Application"/> 实例，服务定位器与 XAML 加载器都得先在位。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class AppXamlTests
{
    [Fact]
    public void Initialize_LoadsTheCompiledXaml_BringingInFluentThemeAndTheDarkVariant()
    {
        // **取值必须在 UI 线程上取完再搬出来**：RequestedThemeVariant 是 StyledProperty，
        // 它的 getter 走 AvaloniaObject.VerifyAccess，别的线程上读一下就抛「invalid thread」；
        // 而 Styles 是普通集合，跨线程读不抛——两者不一致，故一律在里面读完。
        var (hasFluentTheme, variant) = AvaloniaTestHost.Run(() =>
        {
            var instance = new App();

            // 阴性对照：主题是 XAML 带进来的，不是 Application 天生就有的。
            // 少了这一句，下面那条断言也可能只是在验 Avalonia 的默认值。
            Assert.Empty(instance.Styles);

            instance.Initialize();

            return (instance.Styles.Any(style => style is FluentTheme), instance.RequestedThemeVariant);
        });

        Assert.True(hasFluentTheme, "App.axaml 没把 FluentTheme 带进来，控件将没有模板");

        // 悬浮条底色 #CC1E1E1E、文字白色是写死的，浅色变体下控件默认色会跟着翻，
        // 于是白字撞白底。这一条钉的是 App.axaml 里的 RequestedThemeVariant="Dark"。
        Assert.Equal(ThemeVariant.Dark, variant);
    }
}
