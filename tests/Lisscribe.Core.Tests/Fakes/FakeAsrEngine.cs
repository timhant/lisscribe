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

    public int RecognizeCallCount { get; private set; }
    public List<int> ReceivedSampleCounts { get; } = [];
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

        cancellationToken.ThrowIfCancellationRequested();

        if (NextException is not null)
        {
            throw NextException;
        }

        return NextResult;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
