using System.Reflection;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 看住 <c>Category=RequiresDesktop</c> 这个分类不漂。
///
/// <para>
/// 分类的用处是日常跑 <c>-- --filter-not-trait "Category=RequiresDesktop"</c> 把桌面留给人用：
/// 这一批会建真窗口、抢前台、往整个桌面发合成输入。**漏标一个类，日常那一跑就会毫无预兆地
/// 又开始抢前台**，而且看不出是哪来的——正是最难往回查的那种。
/// </para>
///
/// <para>
/// 判据取自 <see cref="DesktopCollection"/> 的成员资格而非一张手写名单：名单会过期，
/// 成员资格是新增测试类时本来就得写的那一行。
/// </para>
/// </summary>
public sealed class RequiresDesktopTraitTests
{
    [Fact]
    public void EveryClassInTheDesktopCollection_CarriesTheRequiresDesktopTrait()
    {
        var bound = DesktopBoundClasses();

        // 阳性对照：扫描本身得真扫到东西。少了这一句，反射一旦扫空，
        // 下面那条断言对任何实现都成立——包括「谁都没标」。
        Assert.Contains(typeof(OverlayWindowStylingTests), bound);

        var missing = bound
            .Where(type => !HasRequiresDesktopTrait(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void NothingOutsideTheDesktopCollection_ClaimsToNeedADesktop()
    {
        // 反向也得看住：多标一个类，它就被日常那一跑无声跳过，
        // 而跳过的东西不会有任何人来提醒你。
        var overclaiming = typeof(RequiresDesktopTraitTests).Assembly.GetTypes()
            .Where(HasRequiresDesktopTrait)
            .Except(DesktopBoundClasses())
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(overclaiming);
    }

    private static Type[] DesktopBoundClasses() =>
        [.. typeof(RequiresDesktopTraitTests).Assembly.GetTypes().Where(InDesktopCollection)];

    private static bool InDesktopCollection(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(CollectionAttribute)
            && attribute.ConstructorArguments.Count == 1
            && (attribute.ConstructorArguments[0].Value as string) == DesktopCollection.Name);

    /// <summary>
    /// 读 <see cref="CustomAttributeData"/> 而不是取属性：xunit 各版本对 Trait 的属性名
    /// 改过，构造参数的位置没改过，这样读跨版本都成立。
    /// </summary>
    private static bool HasRequiresDesktopTrait(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments.Count == 2
            && (attribute.ConstructorArguments[0].Value as string) == "Category"
            && (attribute.ConstructorArguments[1].Value as string) == "RequiresDesktop");
}
