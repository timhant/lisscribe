using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lisscribe.Core.Audio;

/// <summary>
/// 把 WASAPI 共享模式给出的设备混音格式（通常 48kHz / 2ch / float32）转成 ASR 要的 16kHz 单声道。
/// 用带插值滤波的 <see cref="WdlResamplingSampleProvider"/> 而非每 3 点取一个的朴素抽取：后者无
/// 抗混叠低通，8kHz 以上能量会折叠回可听带，噪声环境下显著劣化识别率。
/// </summary>
public sealed class AudioResampler
{
    public const int TargetSampleRate = 16000;

    private readonly PushSampleProvider _source;
    private readonly ISampleProvider _pipeline;
    private readonly bool _isPassThrough;

    // 复用同一块缓冲区，省掉采集热路径上的逐帧分配。本类按设计一次只服务一路采集回调
    // （非线程安全），故共享可变字段安全。
    private readonly float[] _drainBuffer = new float[4096];

    public AudioResampler(int sourceSampleRate, int sourceChannels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);

        _isPassThrough = sourceSampleRate == TargetSampleRate && sourceChannels == 1;
        _source = new PushSampleProvider(sourceSampleRate, sourceChannels);

        // 立体声转单声道取左右平均。不用 MultiplexingSampleProvider——
        // 它默认只取第 0 声道，会整个丢掉另一声道的信息。
        ISampleProvider stage = sourceChannels > 1
            ? new StereoToMonoSampleProvider(_source) { LeftVolume = 0.5f, RightVolume = 0.5f }
            : _source;

        _pipeline = sourceSampleRate == TargetSampleRate
            ? stage
            : new WdlResamplingSampleProvider(stage, TargetSampleRate);
    }

    /// <param name="interleaved">交错排列的 float32 样本（左右左右…）。</param>
    public float[] Process(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.IsEmpty)
        {
            return [];
        }

        if (_isPassThrough)
        {
            return interleaved.ToArray();
        }

        _source.Enqueue(interleaved);

        return Drain();
    }

    /// <summary>
    /// 取出重采样器内部残留的样本。录音结束时调一次。不要为「推出内部延迟」而补零：每次
    /// <see cref="Drain"/> 已把当下能产出的样本读空，补进去的零会被当成真实静音按比例重采样，
    /// 凭空多出一截输出（1 秒 48k→16k 补 4096 个零多出约 800 个样本）。
    /// </summary>
    public float[] Flush()
    {
        return _isPassThrough ? [] : Drain();
    }

    private float[] Drain()
    {
        var output = new List<float>();
        int read;
        // 终止性依赖上游每个 ISampleProvider 在其源耗尽后老实返回 0。这是对第三方实现行为的假设、
        // 不是语言层面的保证：换一个不守此约定的 provider 就会在此死循环。
        while ((read = _pipeline.Read(_drainBuffer, 0, _drainBuffer.Length)) > 0)
        {
            output.AddRange(_drainBuffer.AsSpan(0, read));
        }

        return [.. output];
    }

    /// <summary>
    /// 把外部推入的样本适配成 NAudio 的拉取式 ISampleProvider。用 <see cref="List{T}"/> + 读游标
    /// 而非 <see cref="Queue{T}"/>，好让两头都能整段搬运——这段在 WASAPI 回调线程的热路径上。
    /// </summary>
    private sealed class PushSampleProvider(int sampleRate, int channels) : ISampleProvider
    {
        private readonly List<float> _buffer = new();
        private int _readOffset;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public void Enqueue(ReadOnlySpan<float> samples)
        {
            // 防御性紧缩：正常用法下每次 Read 循环都会读空、_readOffset 已归零，此处只防「有一批
            // 没读完就继续追加」导致已消费的前缀无限堆积。
            if (_readOffset > 0)
            {
                _buffer.RemoveRange(0, _readOffset);
                _readOffset = 0;
            }

            _buffer.AddRange(samples);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var available = _buffer.Count - _readOffset;
            var written = Math.Min(available, count);

            if (written > 0)
            {
                CollectionsMarshal.AsSpan(_buffer)
                    .Slice(_readOffset, written)
                    .CopyTo(buffer.AsSpan(offset, written));

                _readOffset += written;
            }

            // 读空即清空重置：让下一次 Enqueue 从空列表开始，并让上游在这一批耗尽时收到 0。
            if (_readOffset == _buffer.Count)
            {
                _buffer.Clear();

                _readOffset = 0;
            }

            return written;
        }
    }
}
