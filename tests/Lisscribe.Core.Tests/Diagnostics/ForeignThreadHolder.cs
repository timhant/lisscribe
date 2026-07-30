using Xunit;

namespace Lisscribe.Core.Tests.Diagnostics;

/// <summary>
/// 在一条独立线程上持有某个锁，直到 Dispose。
///
/// 跨线程是硬要求，不是讲究：互斥体对持有它的线程可重入，同线程再取一次照样成功。
/// 凡「某某已被占用」的测试若在测试线程上摆局面，摆出来的是空局面——把被测实现整个删掉
/// 它也照样绿。
/// </summary>
internal sealed class ForeignThreadHolder : IDisposable
{
    private readonly ManualResetEventSlim _acquired = new();
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _thread;
    private Exception? _failure;

    public ForeignThreadHolder(Func<bool> acquire, Action release)
    {
        _thread = new Thread(() =>
        {
            // 后台线程上的未捕获异常会直接终结整个测试宿主进程——那不是一条测试变红，
            // 是整个 run 凭空消失，连是哪条测试炸的都看不到。而本类最顺手的下一个用法
            // 恰恰是「拿一个已被占用的名字去复查」，那条路上 TryAcquire 正会抛
            // （名字非法、名字撞上非互斥对象、DACL 拒绝访问，三种都会）。
            try
            {
                Acquired = acquire();
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            finally
            {
                // 必须在 finally 里放行：acquire 抛了却不置位，构造函数就干等满 5 秒，
                // 然后报一句「持有线程未在 5 秒内完成获取」——把真正的死因盖掉。
                // 实测（去掉本 try/catch 后）看到的正是这句，而真正的
                // InvalidOperationException 只以 [FATAL ERROR] 形式另行出现在别处。
                _acquired.Set();
            }

            _release.Wait();
            if (Acquired)
            {
                release();
            }
        })
        {
            IsBackground = true,
            Name = "lisscribe-test-holder",
        };
        _thread.Start();

        var signalled = _acquired.Wait(TimeSpan.FromSeconds(5));
        if (_failure is not null)
        {
            // 构造函数抛出时调用方拿不到实例，也就永远调不到 Dispose —— 不在这里放行，
            // 持有线程就永久停在 _release.Wait() 上。IsBackground 保证它不挡进程退出，
            // 但每抛一次漏一条线程，而本类正要被后续任务反复复用。
            _release.Set();
            throw new InvalidOperationException("持有线程上的 acquire 抛了异常，见 InnerException。", _failure);
        }

        Assert.True(signalled, "持有线程未在 5 秒内完成获取");
    }

    public bool Acquired { get; private set; }

    public void Dispose()
    {
        _release.Set();

        // Join 超时后若照旧 Dispose 这两个事件，还没走完的持有线程下一次碰它们就吃
        // ObjectDisposedException——又是一次「后台线程未捕获异常 = 整个宿主进程没了」。
        // 宁可漏掉两个事件对象（进程马上就要结束了），也不能拿整个 run 去赌。
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            Assert.Fail("持有线程未在 5 秒内退出；故意不释放同步对象，以免它稍后触发 ObjectDisposedException");
        }

        _acquired.Dispose();
        _release.Dispose();
    }
}
