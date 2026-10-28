using System.Runtime.CompilerServices;
using Lisscribe.Core.Abstractions;

namespace Lisscribe.Core.Tests.Fakes;

public sealed class FakeAsrEngine : IAsrEngine
{
    private readonly TaskCompletionSource _disposed = new();

    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsLoaded { get; private set; }
    public string EngineName => "Fake";

    public string ModelDir { get; private set; } = "./models/fake";

    /// <summary>真引擎已加载后会拒绝换址；假的这里不拦，状态机根本不调它。</summary>
    public void RepointModelDir(string modelDir) => ModelDir = modelDir;

    /// <summary>下一次 RecognizeAsync 的返回值。</summary>
    public string NextResult { get; set; } = "识别结果";

    /// <summary>置非 null 则 RecognizeAsync 抛出它。</summary>
    public Exception? NextException { get; set; }

    /// <summary>置非 null 则 RecognizeAsync 阻塞直到该源被 SetResult,用于测试并发与关停。</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>
    /// 识别真的挂在 <see cref="Gate"/> 上之后才完成。
    ///
    /// <para>
    /// <b>「关停必须打断在途识别」这类测试非等它不可。</b>只等状态机变成 Recognizing 是不够的
    /// ——那个状态在**入队**时就置上了，而工作线程可能还没把请求取出来。此时关停，
    /// 取消令牌打在一个还没注册取消回调的地方，<see cref="WasCancelledMidFlight"/> 恒为 false，
    /// 测试红得毫无道理（实测撞到过两次，整条用例 2ms 就失败）。
    /// </para>
    /// </summary>
    public Task Parked => _parked.Task;

    /// <summary>置非 null 则按调用序号（从 1 起）取返回值，代替 <see cref="NextResult"/>；可在其中抛异常或触发取消。</summary>
    public Func<int, string>? Script { get; set; }

    /// <summary>置真则记下每次收到的样本副本。调用方的缓冲会被复用，只存引用的话断言时早已被覆盖。</summary>
    public bool RecordSamples { get; set; }

    /// <summary>
    /// 置真则在一条新建的线程上完成识别，await 它的续体随之同步跑到那条线程上。
    /// Task.Delay 与 Task.Yield 实测会落回刚空出来的原线程，没有区分力。
    /// </summary>
    public bool CompleteOnNewThread { get; set; }

    /// <summary>每次识别走到出结果那一步时所在的托管线程号。</summary>
    public List<int> CompletionThreadIds { get; } = [];

    /// <summary>置真则完全不看取消令牌，模拟原生解码中途打断不了的情形。</summary>
    public bool IgnoreCancellation { get; set; }

    public int RecognizeCallCount { get; private set; }
    public List<int> ReceivedSampleCounts { get; } = [];
    public List<float[]> ReceivedSamples { get; } = [];
    public bool IsDisposed { get; private set; }
    public bool WasCancelledMidFlight { get; private set; }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoaded = true;
        return Task.CompletedTask;
    }

    public async Task<string> RecognizeAsync(
        ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        RecognizeCallCount++;
        ReceivedSampleCounts.Add(samples.Length);

        var call = RecognizeCallCount;

        if (RecordSamples)
        {
            ReceivedSamples.Add(samples.ToArray());
        }

        if (Gate is not null)
        {
            using var registration = cancellationToken.Register(() =>
            {
                WasCancelledMidFlight = true;
                Gate.TrySetCanceled(cancellationToken);
            });

            // 取消回调已注册好，此刻才算「真的挂住了」。顺序反了就等于没等。
            _parked.TrySetResult();

            await Gate.Task.ConfigureAwait(false);
        }

        if (CompleteOnNewThread)
        {
            await new ResumeOnNewThread();
        }

        CompletionThreadIds.Add(Environment.CurrentManagedThreadId);

        // 脚本先跑：它在「识别途中」触发的取消，要能被下面这道检查看见。
        var text = Script is null ? NextResult : Script(call);

        if (!IgnoreCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (NextException is not null)
        {
            throw NextException;
        }

        return text;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// await 它的续体一律在一条新建的线程上跑。永远报「未完成」，故不存在「先完成、续体落回原线程」的竞态。
    /// 新线程先停 20ms 再跑续体，让调用方来得及在本方法返回的任务上挂好它自己的续体。
    /// </summary>
    private readonly struct ResumeOnNewThread : ICriticalNotifyCompletion
    {
        public ResumeOnNewThread GetAwaiter() => this;

        public bool IsCompleted => false;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation) => Start(continuation);

        public void UnsafeOnCompleted(Action continuation) => Start(continuation);

        private static void Start(Action continuation) =>
            new Thread(() =>
            {
                Thread.Sleep(20);
                continuation();
            })
            { IsBackground = true }.Start();
    }
}
