using System.Diagnostics;
using Lisscribe.Core.Diagnostics;
using Xunit;

namespace Lisscribe.Core.Tests.Diagnostics;

public sealed class SingleInstanceGuardTests
{
    private static string UniqueName() => $"lisscribe-test-{Guid.NewGuid():N}";

    [Fact]
    public void TryAcquire_WhenNoOtherInstance_Succeeds()
    {
        var name = UniqueName();

        var acquired = SingleInstanceGuard.TryAcquire(name, out var guard);

        using (guard)
        {
            Assert.True(acquired);
            Assert.NotNull(guard);
        }
    }

    [Fact]
    public void TryAcquire_WhenHeldByAnotherThread_Fails()
    {
        var name = UniqueName();
        SingleInstanceGuard? owner = null;
        using var holder = new ForeignThreadHolder(
            () => SingleInstanceGuard.TryAcquire(name, out owner),
            () => owner!.Dispose());
        Assert.True(holder.Acquired);

        var acquired = SingleInstanceGuard.TryAcquire(name, out var second);

        using (second)
        {
            Assert.False(acquired);
            Assert.Null(second);
        }
    }

    [Fact]
    public void TryAcquire_WhenHeldByAnotherProcess_Fails()
    {
        // 这才是本类真正要证的事：热键钩子是进程级的，两个进程各装一份，
        // 一次按键就录两遍、上屏两遍。同进程内的句柄争抢证明不了这一点。
        var name = UniqueName();
        Assert.True(SingleInstanceGuard.TryAcquire(name, out var guard));

        using (guard)
        {
            Assert.False(CrossProcessGuardHost.TryAcquireInChildProcess(name));
        }
    }

    [Fact]
    public void TryAcquire_AfterOwnerDisposes_SucceedsInAnotherProcess()
    {
        // 正常退出后必须能再启动，否则用户重启程序会被自己上一次的残留挡住。
        var name = UniqueName();
        Assert.True(SingleInstanceGuard.TryAcquire(name, out var guard));
        guard!.Dispose();

        Assert.True(CrossProcessGuardHost.TryAcquireInChildProcess(name));
    }

    [Fact]
    public void TryAcquire_AfterOwnerProcessIsKilled_Succeeds()
    {
        // 崩溃/被任务管理器结束/断电后重来一次。持有者没释放就没了，互斥体处于 abandoned 状态——
        // 若实现把 AbandonedMutexException 当成「已有实例在跑」，程序从此再也起不来，
        // 用户唯一的出路是重启电脑，而他不会想到这一层。
        var name = UniqueName();
        using var child = CrossProcessGuardHost.HoldInChildProcess(name);

        // 趁子进程还活着先开一个同名句柄，好让内核对象在它被强杀之后依然存在 ——
        // 这正是「第二个实例刚要启动、第一个实例此刻崩掉」的真实时序，
        // 也是 AbandonedMutexException 唯一会浮现的时序：最后一个句柄一关，
        // 对象连同它的 abandoned 状态就一起没了，下一个进程只会新建一个干净的。
        // 不带前缀的名字与实现用的 Local\ 指向同一个对象（Windows 与 Linux 皆然）。
        using var keepAlive = new Mutex(initiallyOwned: false, name);
        child.Crash();

        var acquired = SingleInstanceGuard.TryAcquire(name, out var guard);

        using (guard)
        {
            Assert.True(acquired);
        }
    }

    [Fact]
    public void TryAcquire_AfterOwnerThreadDiesWithoutReleasing_Succeeds()
    {
        // 同一场景的进程内版本：持有线程退出即令互斥体 abandoned，
        // 下一次 WaitOne 必抛 AbandonedMutexException（Windows/Linux 皆然）。
        //
        // 有个时序窗口：Thread.Join 返回的是「托管线程跑完了」，而互斥体被标记为 abandoned
        // 发生在内核真正销毁那条 OS 线程时。窗口内 WaitOne(0) 照样返回 false，
        // 概率不高（千分之一量级），CPU 满载时高一个数量级。
        //
        // **不能靠「在同一个名字上重试取锁」来消化这个窗口。** 首次调用即便把 abandoned 判反，
        // 那个异常本身也已经把 abandoned 状态消费掉了（AbandonedMutexException 意味着等待成功、
        // 所有权已转移），于是重试会干净地取到锁，把「判反」这种缺陷正好吞掉，测试照样绿。
        //
        // 故撞上窗口时换个名字整幕重演。判反时每一幕的首次调用都会失败，十幕全败即红。
        const int attempts = 10;
        var acquired = false;

        for (var attempt = 0; attempt < attempts && !acquired; attempt++)
        {
            var name = UniqueName();
            SingleInstanceGuard? abandoned = null;
            var owner = new Thread(() => SingleInstanceGuard.TryAcquire(name, out abandoned))
            {
                IsBackground = true,
            };
            owner.Start();
            Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
            // 句柄仍被引用着（没被 GC 关掉），复现的正是「进程还在、持有者线程没了」。
            Assert.NotNull(abandoned);

            acquired = SingleInstanceGuard.TryAcquire(name, out var guard);
            guard?.Dispose();

            GC.KeepAlive(abandoned);
        }

        Assert.True(acquired, $"{attempts} 幕重演都没能取到被遗弃的互斥体");
    }

    [Fact]
    public void TryAcquire_IsNotBlockedByGlobalNamespaceMutexOfSameName()
    {
        // Local\ 前缀不是装饰：多用户（远程桌面 / 快速用户切换）时各会话各持一份，
        // 换成 Global\ 就变成「甲用户开着程序，乙用户根本起不来」。
        // 拿一个同名的 Global\ 互斥体当探针，守卫必须完全无视它。
        // 探针必须由别的线程持有——同线程持有时互斥体可重入，Global\ 实现也会「成功」，测不出来。
        var name = UniqueName();
        using var globalMutex = new Mutex(initiallyOwned: false, $"Global\\{name}");
        using var holder = new ForeignThreadHolder(
            () => globalMutex.WaitOne(TimeSpan.Zero, exitContext: false),
            globalMutex.ReleaseMutex);
        Assert.True(holder.Acquired);

        var acquired = SingleInstanceGuard.TryAcquire(name, out var guard);

        using (guard)
        {
            Assert.True(acquired);
        }
    }

    [Fact]
    public void TryAcquire_AfterFirstIsDisposed_SucceedsAgain()
    {
        var name = UniqueName();
        SingleInstanceGuard.TryAcquire(name, out var first);
        first!.Dispose();

        var acquired = SingleInstanceGuard.TryAcquire(name, out var second);

        using (second)
        {
            Assert.True(acquired);
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        SingleInstanceGuard.TryAcquire(UniqueName(), out var guard);

        guard!.Dispose();
        guard.Dispose();
    }

    [Fact]
    public void Dispose_FromAnotherThread_DoesNotThrow()
    {
        // 组装根多半在关机路径上释放它，而那条路径未必是启动时取锁的那条线程。
        // Mutex.ReleaseMutex 在非持有线程上会抛 ApplicationException——
        // 让它冒出去就等于「退出时崩一下」。
        var name = UniqueName();
        Assert.True(SingleInstanceGuard.TryAcquire(name, out var guard));

        Exception? captured = null;
        var disposer = new Thread(() =>
        {
            try
            {
                guard!.Dispose();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
        };
        disposer.Start();

        Assert.True(disposer.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(captured);
    }

    [Fact]
    public void TryAcquire_WithNullName_Throws() =>
        Assert.Throws<ArgumentNullException>(() => SingleInstanceGuard.TryAcquire(null!, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryAcquire_WithBlankName_Throws(string name) =>
        // 空名字会退化成匿名互斥体：次次都能取到，守卫形同虚设却毫无征兆。
        Assert.Throws<ArgumentException>(() => SingleInstanceGuard.TryAcquire(name, out _));

    [Theory]
    [InlineData("lisscribe\\sub")]
    [InlineData("\\leading")]
    [InlineData("trailing\\")]
    public void TryAcquire_WithBackslashInName_ThrowsArgumentException(string name)
    {
        // 实现会拼上 Local\ 前缀，名字里再带反斜杠就成了内核对象命名空间路径 Local\a\b，
        // Windows 会去找一个叫 a 的对象目录。实测（Win11 / .NET 10）抛的是
        // DirectoryNotFoundException: "Could not find a part of the path 'Local\a\b'."
        // ——一句谈论「路径」的话，与「你传的名字不合法」毫无关系，调用方无从据此定位问题。
        var ex = Assert.Throws<ArgumentException>(() => SingleInstanceGuard.TryAcquire(name, out _));

        Assert.Contains("反斜杠", ex.Message);
    }

    [Fact]
    public void TryAcquire_WithOverlongName_ThrowsArgumentException()
    {
        // 调用方名字至多 32731 字符，再长 Mutex 构造函数就抛 PathTooLongException。
        // 它有两处不友好：
        // 一是仍旧管这叫「path」（这里根本没有路径，只有内核对象名）；
        // 二是它把整个名字抄进 Message，40000 字符的名字换来 40080 字符的异常消息 ——
        // 原样冒泡会把日志整段淹掉。故此处换成一句短话，细节留在 InnerException 里。
        var name = new string('a', 40000);

        var ex = Assert.Throws<ArgumentException>(() => SingleInstanceGuard.TryAcquire(name, out _));

        Assert.IsType<PathTooLongException>(ex.InnerException);
        Assert.Contains("40000", ex.Message);
        // 决定性的一条：证明没把那 40080 字符的原始消息原样转发出来。
        Assert.True(ex.Message.Length < 200, $"异常消息不该夹带整个名字，实际长 {ex.Message.Length}");
    }

    [Fact]
    public void TryAcquire_WithNameLongerThanLegacy260Limit_Succeeds()
    {
        // .NET Framework 上内核对象名确有 260 字符上限（含 Local\ 前缀），**但 .NET 10 上没有**
        // ——实测 300、1000、5000 字符一律成功。留这条测试是为了挡住「照着老经验补一个 254/260
        // 常量」的修法：那会把一批完全合法的名字拒之门外，而且失败只在真拿长名字时才现形。
        // 名字仍走 UniqueName()：内核对象名是全机可见的，硬编码会让同机并发跑的两个
        // dotnet test 互撞，撞出来的还是一条看不懂的偶发失败。
        var name = $"{UniqueName()}-{new string('a', 300)}";

        var acquired = SingleInstanceGuard.TryAcquire(name, out var guard);

        using (guard)
        {
            Assert.True(acquired);
        }
    }
}
