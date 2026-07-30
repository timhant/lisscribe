using System.Reflection;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 看住 <c>Category=RequiresDesktop</c> 这个分类不漂。理由与
/// <c>Lisscribe.App.Tests</c> 里的同名类一致：漏标一个类，日常那一跑就会毫无预兆地
/// 又开始抢前台；多标一个类，它就被无声跳过。
///
/// <para>
/// 本项目的判据是 <see cref="InjectedInputCollection"/> 的成员资格——那个集合的定义
/// 本身就写着「共享整个桌面的输入队列」。
/// </para>
///
/// <para>
/// <b>不含 <see cref="RecordingDeviceCollection"/></b>：那一批要的是真麦克风，不是桌面，
/// 且不抢前台。要单独跳过它得另立分类，别混进这一个。
/// </para>
/// </summary>
public sealed class RequiresDesktopTraitTests
{
    [Fact]
    public void EveryClassInTheInjectedInputCollection_CarriesTheRequiresDesktopTrait()
    {
        var bound = DesktopBoundClasses();

        // 阳性对照：扫描本身得真扫到东西，否则下面那条对「谁都没标」也成立。
        Assert.Contains(typeof(KeyboardHookTests), bound);

        var missing = bound
            .Where(type => !HasRequiresDesktopTrait(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void NothingOutsideTheInjectedInputCollection_ClaimsToNeedADesktop()
    {
        var overclaiming = typeof(RequiresDesktopTraitTests).Assembly.GetTypes()
            .Where(HasRequiresDesktopTrait)
            .Except(DesktopBoundClasses())
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(overclaiming);
    }

    private static Type[] DesktopBoundClasses() =>
        [.. typeof(RequiresDesktopTraitTests).Assembly.GetTypes().Where(InInjectedInputCollection)];

    private static bool InInjectedInputCollection(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(CollectionAttribute)
            && attribute.ConstructorArguments.Count == 1
            && (attribute.ConstructorArguments[0].Value as string) == InjectedInputCollection.Name);

    /// <summary>
    /// 读 <see cref="CustomAttributeData"/> 而不是取属性：xunit 各版本对 Trait 的属性名
    /// 改过，构造参数的位置没改过。
    /// </summary>
    private static bool HasRequiresDesktopTrait(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments.Count == 2
            && (attribute.ConstructorArguments[0].Value as string) == "Category"
            && (attribute.ConstructorArguments[1].Value as string) == "RequiresDesktop");
}
