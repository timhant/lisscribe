using Lisscribe.Core.Abstractions;

namespace Lisscribe.Core.Tests.Fakes;

/// <summary>
/// 假解码器：按给定样本数组吐数据，每次最多吐多少可配，可在第 N 次 Read 抛异常，并记下 Open、Read、Dispose 所在的线程。
/// </summary>
public sealed class FakeAudioFileDecoder : IAudioFileDecoder
{
    private readonly float[] _samples;

    private readonly Lock _gate = new();

    private readonly List<int> _threadIds = [];

    public FakeAudioFileDecoder(float[] samples)
    {
        _samples = samples;

        // 16kHz 下一个样本恰为 625 个刻度，默认时长与样本数严格相符。
        Duration = TimeSpan.FromTicks(samples.LongLength * (TimeSpan.TicksPerSecond / 16000));
    }

    /// <summary>报给调用方的总时长，默认恰等于样本时长，可改成与样本数不符来模拟文件头不准。</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>每次 Read 最多吐多少个样本。</summary>
    public int MaxSamplesPerRead { get; set; } = int.MaxValue;

    /// <summary>置非 null 则 Open 抛出它。</summary>
    public Exception? OpenException { get; set; }

    /// <summary>Open 一进门就调，供测试查看彼时的外部状态。</summary>
    public Action? OnOpen { get; set; }

    /// <summary>第几次 Read（从 1 起）抛 <see cref="ReadException"/>，0 表示不抛。</summary>
    public int ThrowOnReadNumber { get; set; }

    /// <summary>真实实现在读到坏数据时可能抛英文的 COM 或 InvalidOperation 异常。</summary>
    public Exception ReadException { get; set; } = new InvalidOperationException("Unspecified error");

    public string? OpenedPath { get; private set; }

    public int OpenCount { get; private set; }

    public int ReadCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>Open、每次 Read、Dispose 各自所在的托管线程号，按发生顺序。</summary>
    public IReadOnlyList<int> ThreadIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _threadIds];
            }
        }
    }

    public IDecodedAudio Open(string path)
    {
        RecordThread();
        OnOpen?.Invoke();

        OpenCount++;
        OpenedPath = path;

        if (OpenException is not null)
        {
            throw OpenException;
        }

        return new DecodedAudio(this);
    }

    private void RecordThread()
    {
        lock (_gate)
        {
            _threadIds.Add(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class DecodedAudio(FakeAudioFileDecoder owner) : IDecodedAudio
    {
        private int _position;

        private bool _disposed;

        public TimeSpan Duration => owner.Duration;

        public int Read(Span<float> buffer)
        {
            owner.RecordThread();

            ObjectDisposedException.ThrowIf(_disposed, this);

            // 与真实实现同一契约：空缓冲读回的 0 会被当成文件结束，故直接拒绝。
            if (buffer.IsEmpty)
            {
                throw new ArgumentException("缓冲区不能为空。", nameof(buffer));
            }

            owner.ReadCount++;

            if (owner.ReadCount == owner.ThrowOnReadNumber)
            {
                throw owner.ReadException;
            }

            var count = Math.Min(Math.Min(buffer.Length, owner.MaxSamplesPerRead), owner._samples.Length - _position);

            owner._samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;

            return count;
        }

        public void Dispose()
        {
            owner.RecordThread();

            owner.DisposeCount++;
            _disposed = true;
        }
    }
}
