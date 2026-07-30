namespace Lisscribe.Core.Audio;

/// <summary>
/// 一次录音会话的样本缓冲，带硬性时长上限。上限不是「优化」而是安全阀：键盘被压住时，无上限缓冲
/// 在 8 小时后约 1.8GB，喂进 encoder 即 OOM 或永久 hang。
///
/// 线程模型：采集回调线程 <see cref="Append"/>，看门狗线程读 <see cref="Duration"/> /
/// <see cref="IsFull"/>，会话线程 <see cref="Take"/>。每个公开成员各自持锁，故单次调用
/// 内部不会撕裂；但跨调用的组合（例如调用方先读 <see cref="IsFull"/> 再决定是否
/// <see cref="Append"/>）不是原子的——两次加锁之间可能有别的线程插入。这里刻意不提供
/// 「check-then-act」的原子接口，因为 <see cref="Append"/> 本身已经在容量不足时自行
/// 截断，调用方即便在过期的 IsFull 读数上多调用一次 Append，也只会看到追加了 0
/// 个样本，不会破坏上限——安全阀不依赖调用方的判断时序。
/// </summary>
public sealed class AudioBuffer
{
    private readonly List<float> _samples = [];
    private readonly int _maxSamples;
    private readonly Lock _gate = new();

    public AudioBuffer(int maxSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSeconds, 1);

        _maxSamples = maxSeconds * AudioResampler.TargetSampleRate;
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(_samples.Count / (double)AudioResampler.TargetSampleRate);
            }
        }
    }

    public bool IsFull
    {
        get { lock (_gate) { return _samples.Count >= _maxSamples; } }
    }

    /// <summary>追加样本；若已达上限或追加会超出上限，则静默截断而非抛出或无限增长。</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            var room = _maxSamples - _samples.Count;

            if (room <= 0)
            {
                return;
            }

            var toCopy = Math.Min(room, samples.Length);

            _samples.AddRange(samples[..toCopy]);
        }
    }

    /// <summary>取走全部样本并清空。</summary>
    public float[] Take()
    {
        lock (_gate)
        {
            var result = _samples.ToArray();

            _samples.Clear();

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate) { _samples.Clear(); }
    }
}
