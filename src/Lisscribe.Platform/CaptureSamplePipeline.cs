using System.Runtime.InteropServices;
using Lisscribe.Core.Audio;
using NAudio.Wave;

namespace Lisscribe.Platform;

/// <summary>
/// 「WASAPI 给的原生字节 + 设备格式 → 16kHz 单声道 float」这一整条链。
/// </summary>
/// <remarks>
/// 单独成类的理由：录音里最容易错、错了又最没有现场线索的三件事——字节怎么解释、两个声道怎么合、
/// 重采样比例对不对——全是纯数据变换。剥出来之后它们可以用构造出来的已知波形精确单测，而
/// <see cref="WasapiAudioCapture"/> 退化成一层只管开设备与收尾次序的薄壳。
/// <para>
/// 格式必须校验，不能假定。按 float32 硬解凭的是「共享模式给的是 IEEE float32」这句没验过的话，
/// 猜错的后果不是崩，是静默产出垃圾波形：识别结果全是乱码或空，日志里一个字都没有。
/// 本机实测混音格式确为 <c>IeeeFloat / 32bit / 48000Hz / 2ch</c>，但「那就恒是 float32」这个结论
/// 没有拿到——设备属性 <c>PKEY_AudioEngine_DeviceFormat</c> 写的是 16bit 而混音格式仍是 float32，
/// 说明这一层由音频引擎说了算；且 NAudio 自己的 <c>ToStandardWaveFormat()</c> 留了一条「既不是
/// float32 也不是 PCM 就原样返回」的分支，说明它预期得到别的 SubFormat。
/// </para>
/// <para>
/// 于是取「支持两种 + 其余当场失败」：float32（实测拿到过）与 16bit PCM（未在真设备上取到过样本，
/// 正确性完全靠逐位对拍的纯函数测试）。24bit / 32bit 整数 / 8bit / 认不出的 SubFormat 一律抛
/// <see cref="NotSupportedException"/> 并把格式写进消息——宁可让用户看见「不支持的采集格式：
/// Pcm / 24bit / …」来报障，也不要让他对着一屏乱码猜。
/// </para>
/// </remarks>
internal sealed class CaptureSamplePipeline
{
    private readonly AudioResampler _resampler;
    private readonly int _bytesPerFrame;
    private readonly bool _isFloat32;

    // PCM16 那条支路要把 short 展成 float 才能喂给重采样器。这段是采集回调的热路径，
    // 复用同一块缓冲而非每包 new。float32 那条支路不经过它——MemoryMarshal.Cast 是零拷贝重解释。
    private float[] _scratch = [];

    public CaptureSamplePipeline(WaveFormat sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);

        _isFloat32 = sourceFormat is { Encoding: WaveFormatEncoding.IeeeFloat, BitsPerSample: 32 };

        var isPcm16 = sourceFormat is { Encoding: WaveFormatEncoding.Pcm, BitsPerSample: 16 };

        if (!_isFloat32 && !isPcm16)
        {
            throw new NotSupportedException(
                $"不支持的采集格式：{Describe(sourceFormat)}。本实现只解 IEEE float32 与 16bit PCM；"
                + "其余格式一律当场失败，因为按 float32 硬解只会静默产出垃圾波形——"
                + "不崩、不报错，而识别结果全是乱码。");
        }

        SourceSampleRate = sourceFormat.SampleRate;
        SourceChannels = sourceFormat.Channels;

        // 不用 WaveFormat.BlockAlign：那是格式自己报的数，与 Encoding/BitsPerSample 不一致时
        // （驱动填错、或测试里造的格式）会把整条帧对齐悄悄挪歪。这里按已校验过的位深自己算。
        _bytesPerFrame = sourceFormat.Channels * (sourceFormat.BitsPerSample / 8);

        _resampler = new AudioResampler(sourceFormat.SampleRate, sourceFormat.Channels);
    }

    public int SourceSampleRate { get; }

    public int SourceChannels { get; }

    /// <param name="nativeBytes">
    /// WASAPI 交出来的原始字节，长度须为 <see cref="WaveInEventArgs.BytesRecorded"/> 那一段
    /// （缓冲区本身通常更长）。
    /// </param>
    /// <returns>已重采样为 16kHz 单声道的样本；这一包还产不出样本时为空数组。</returns>
    public float[] Process(ReadOnlySpan<byte> nativeBytes)
    {
        // 只把整数个帧交给重采样器——那是 AudioResampler.Process 写明的入参契约。
        // 这条守卫在当前 NAudio 实现下没有可观测效果（Cast 本就丢掉不足一个样本的尾字节，
        // 落单那个样本也不跨包留存，何况左右对调对取平均是恒等变换），没有测试杀得掉移除它的
        // 变异。留着，因为「契约由别人的实现顺手兜住」是运气不是设计。
        var usable = nativeBytes.Length - (nativeBytes.Length % _bytesPerFrame);

        if (usable <= 0)
        {
            return [];
        }

        var source = nativeBytes[..usable];

        if (_isFloat32)
        {
            return _resampler.Process(MemoryMarshal.Cast<byte, float>(source));
        }

        var pcm = MemoryMarshal.Cast<byte, short>(source);

        if (_scratch.Length < pcm.Length)
        {
            _scratch = new float[pcm.Length];
        }

        // 1/32768 是 2 的幂，浮点里精确可表示，故这里的乘法与除以 32768 逐位等价。
        // 用 32768 而不是 32767 是为了让 short.MinValue 正好落在 -1.0：换成 32767 会让满程负样本
        // 变成 -1.00003，越界的那一点点在后续处理里会被削顶。
        for (var i = 0; i < pcm.Length; i++)
        {
            _scratch[i] = pcm[i] * (1f / 32768f);
        }

        return _resampler.Process(_scratch.AsSpan(0, pcm.Length));
    }

    /// <summary>录音结束时调一次，取出重采样器内部的残留。</summary>
    public float[] Flush() => _resampler.Flush();

    internal static string Describe(WaveFormat format) =>
        $"{format.Encoding} / {format.BitsPerSample}bit / {format.SampleRate}Hz / {format.Channels}ch";
}
