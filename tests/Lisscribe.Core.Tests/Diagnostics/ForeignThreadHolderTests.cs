using Xunit;

namespace Lisscribe.Core.Tests.Diagnostics;

/// <summary>
/// 测试基础设施自身的测试（同 ModelFixtureTests 的用意）。
///
/// 为什么值得单独测：本类的失败模式不是「一条测试变红」，而是**整个测试宿主进程消失**
/// ——.NET 上后台线程的未捕获异常直接终结进程，连是哪条测试炸的都看不到。
/// 这种缺陷一旦回归，排查成本远高于它自身的复杂度。
/// </summary>
public sealed class ForeignThreadHolderTests
{
    [Fact]
    public void Constructor_WhenAcquireThrows_SurfacesTheExceptionInsteadOfKillingTheHost()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ForeignThreadHolder(
            () => throw new InvalidOperationException("acquire 抛了"),
            () => { }));

        // 原始异常必须留着：只报一句「持有线程失败」等于把死因扔了。
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("acquire 抛了", inner.Message);
    }

    [Fact]
    public void Constructor_WhenAcquireReturnsFalse_DoesNotCallRelease()
    {
        // 没取到就别释放——对互斥体而言，非持有线程调 ReleaseMutex 会抛 ApplicationException，
        // 而那一抛正好又落在后台线程上。
        var released = false;

        using (new ForeignThreadHolder(() => false, () => released = true))
        {
        }

        Assert.False(released);
    }

    [Fact]
    public void Dispose_RunsReleaseOnTheSameThreadThatAcquired()
    {
        // 本类存在的全部理由就是「取和放都发生在另一条线程上」。若 release 跑回了测试线程，
        // 互斥体的持有线程语义就散了，用它摆出来的局面也就不作数了。
        int acquireThread = 0;
        int releaseThread = 0;

        using (new ForeignThreadHolder(
            () => { acquireThread = Environment.CurrentManagedThreadId; return true; },
            () => releaseThread = Environment.CurrentManagedThreadId))
        {
        }

        Assert.NotEqual(Environment.CurrentManagedThreadId, acquireThread);
        Assert.Equal(acquireThread, releaseThread);
    }
}
