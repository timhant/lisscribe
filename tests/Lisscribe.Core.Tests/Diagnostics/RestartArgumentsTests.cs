using Lisscribe.Core.Diagnostics;
using Xunit;

namespace Lisscribe.Core.Tests.Diagnostics;

/// <summary>
/// <c>--wait-for &lt;pid&gt;</c> 的解析。
///
/// <para>
/// 这个参数只由 lisscribe 自己拼给自己（换档重启时旧进程传给新进程），故**认不出来时一律当没传**，
/// 不报错、不拒绝启动。理由：认不出的后果最多是新进程不等旧的、撞上单实例检查报一句
/// 「已在运行」——那是句真话；而为一个畸形参数拒绝启动，等于让用户手打错一个字就开不了程序。
/// </para>
/// </summary>
public sealed class RestartArgumentsTests
{
    [Fact]
    public void TryParseWaitForPid_WithTheOptionAndAValue_YieldsThatPid()
    {
        Assert.True(RestartArguments.TryParseWaitForPid(["--wait-for", "1234"], out var pid));
        Assert.Equal(1234, pid);
    }

    /// <summary>
    /// 位置无关。写死「第 0 个元素是选项」的实现今天能过，日后前面加任何一个参数就整个失灵，
    /// 而失灵的样子是「重启偶尔不生效」——最难查的那种。
    /// </summary>
    [Fact]
    public void TryParseWaitForPid_WhenTheOptionIsNotFirst_StillFindsIt()
    {
        Assert.True(RestartArguments.TryParseWaitForPid(["--something", "x", "--wait-for", "77"], out var pid));
        Assert.Equal(77, pid);
    }

    [Fact]
    public void TryParseWaitForPid_WithNoArguments_YieldsNothing()
    {
        Assert.False(RestartArguments.TryParseWaitForPid([], out var pid));
        Assert.Equal(0, pid);
    }

    /// <summary>选项在最后一个位置上，后面没有值可取——不能越界，也不能当成有。</summary>
    [Fact]
    public void TryParseWaitForPid_WhenTheOptionHasNoValue_YieldsNothing()
    {
        Assert.False(RestartArguments.TryParseWaitForPid(["--wait-for"], out _));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("99999999999999999999")]
    public void TryParseWaitForPid_WhenTheValueIsNotAWholeNumber_YieldsNothing(string value)
    {
        Assert.False(RestartArguments.TryParseWaitForPid(["--wait-for", value], out _));
    }

    /// <summary>
    /// pid 必为正。0 与负数不是「某个已经退出的进程」，是垃圾输入；
    /// 放行的话 <c>Process.GetProcessById</c> 会拿它去抛 ArgumentException，
    /// 于是走进「旧进程已经没了」那条路——把一次配置错误伪装成一切正常。
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void TryParseWaitForPid_WhenThePidIsNotPositive_YieldsNothing(string value)
    {
        Assert.False(RestartArguments.TryParseWaitForPid(["--wait-for", value], out _));
    }

    /// <summary>选项名是拼给自己的常量，两头必须用同一个，故它得公开且被这条钉住。</summary>
    [Fact]
    public void TheOptionName_IsTheOneBothSidesAgreeOn()
    {
        Assert.Equal("--wait-for", RestartArguments.WaitForOption);
    }
}
