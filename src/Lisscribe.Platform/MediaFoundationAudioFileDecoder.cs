using System.Runtime.InteropServices;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lisscribe.Platform;

/// <summary>
/// 用 Windows 自带的 Media Foundation 解音频文件（m4a / mp3 / wav 等），产出 16kHz 单声道。
/// </summary>
/// <remarks>
/// 链路：<see cref="MediaFoundationReader"/> → <c>ToSampleProvider()</c> → <see cref="ChannelAverager"/>
/// → <see cref="WdlResamplingSampleProvider"/>。源已是 16kHz 时 WDL 实测逐位不变，故不另设旁路。
/// <para>
/// 线程：<see cref="MediaFoundationReader"/> 在 <c>SingleReaderObject = false</c> 时，构造期建的 COM reader
/// 读完格式与时长即释放，真正读文件的那个在第一次 Read 所在的线程上另建。故可在 UI 线程（STA）上 Open、
/// 在线程池（MTA）上读完；改成 true 则读的一侧实测抛 InvalidCastException（E_NOINTERFACE）。
/// </para>
/// <para>
/// 不手动调 <c>MediaFoundationApi.Startup()</c>，Reader 构造时自己会调。也不调 Shutdown：它是进程级的。
/// </para>
/// </remarks>
public sealed class MediaFoundationAudioFileDecoder : IAudioFileDecoder
{
    /// <summary>MF_E_UNSUPPORTED_BYTESTREAM_TYPE：系统不认这种文件，文本改名、空文件都落在这里。</summary>
    private const int UnsupportedByteStreamType = unchecked((int)0xC00D36C4);

    /// <summary>
    /// 打开文件。文件不存在抛 <see cref="FileNotFoundException"/>，其余打不开的情形抛 <see cref="IOException"/>，
    /// 消息直接给用户看，原异常作 InnerException。
    /// </summary>
    public IDecodedAudio Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var name = Path.GetFileName(path);

        // 缺文件时直说，不提 Media Feature Pack，免得把人引去装系统组件。
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到该文件：{name}。", path);
        }

        MediaFoundationReader? reader = null;

        try
        {
            // SingleReaderObject 必须为 false，跨线程读全靠它（见类注释），故显式写出而不依赖默认值。
            reader = new MediaFoundationReader(
                path, new MediaFoundationReader.MediaFoundationReaderSettings { SingleReaderObject = false });

            return new DecodedAudio(reader);
        }
        catch (Exception ex)
        {
            reader?.Dispose();

            throw new IOException(
                $"无法解码该文件：{name}（{Describe(ex)}）。若是 Windows N 版本，请安装 Media Feature Pack。", ex);
        }
    }

    /// <summary>最常见的「选错了文件」换成人话，其余原样给出系统消息。</summary>
    private static string Describe(Exception ex) =>
        ex is COMException { HResult: UnsupportedByteStreamType } ? "不是系统能识别的音频格式" : ex.Message;

    /// <summary>多声道等权平均成单声道，凑满整帧再平均，不会半帧错位。</summary>
    internal sealed class ChannelAverager : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;

        // 上游交错样本的暂存，只增不减。
        private float[] _interleaved = [];

        public ChannelAverager(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var wanted = count * _channels;

            if (_interleaved.Length < wanted)
            {
                _interleaved = new float[wanted];
            }

            // 上游一次可能给不满，读到够数或见底为止。
            var got = 0;

            while (got < wanted)
            {
                var read = _source.Read(_interleaved, got, wanted - got);

                if (read == 0)
                {
                    break;
                }

                got += read;
            }

            var frames = got / _channels;

            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;

                for (var c = 0; c < _channels; c++)
                {
                    sum += _interleaved[f * _channels + c];
                }

                buffer[offset + f] = sum / _channels;
            }

            return frames;
        }
    }

    /// <summary>一个已打开的文件。Read 不是线程安全的，同一时刻只许一个线程读。</summary>
    private sealed class DecodedAudio : IDecodedAudio
    {
        private readonly MediaFoundationReader _reader;
        private readonly ISampleProvider _output;

        // WDL 只收 float[]，借这块缓冲转一手，只增不减。
        private float[] _scratch = [];

        private bool _disposed;

        public DecodedAudio(MediaFoundationReader reader)
        {
            _reader = reader;

            // 文件头没写时长时 NAudio 给 0，此处即为 Zero，调用方算进度须防除零。
            Duration = reader.TotalTime;

            _output = new WdlResamplingSampleProvider(
                new ChannelAverager(reader.ToSampleProvider()), AudioResampler.TargetSampleRate);
        }

        public TimeSpan Duration { get; }

        public int Read(Span<float> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 空缓冲读回的 0 会被调用方当成文件结束。
            if (buffer.IsEmpty)
            {
                throw new ArgumentException("缓冲区不能为空。", nameof(buffer));
            }

            if (_scratch.Length < buffer.Length)
            {
                _scratch = new float[buffer.Length];
            }

            // WDL 按输出驱动，上游未尽时总能交满，故首个 0 即文件结束（实测缓冲 1 / 7 / 4096 均无提前的 0）。
            var read = _output.Read(_scratch, 0, buffer.Length);

            _scratch.AsSpan(0, read).CopyTo(buffer);

            return read;
        }

        /// <summary>释放读线程上建的那个 COM reader，重复调用无害。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _reader.Dispose();
        }
    }
}
