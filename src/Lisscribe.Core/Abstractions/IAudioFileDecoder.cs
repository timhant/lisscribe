namespace Lisscribe.Core.Abstractions;

/// <summary>把音频文件解成识别引擎要的 16kHz 单声道 float 样本。</summary>
public interface IAudioFileDecoder
{
    /// <summary>打开文件准备流式读取，打不开时抛异常，异常消息直接给用户看。</summary>
    IDecodedAudio Open(string path);
}

/// <summary>一个已打开的音频文件，按顺序吐出 16kHz 单声道样本。</summary>
public interface IDecodedAudio : IDisposable
{
    /// <summary>文件头报的总时长，只用来算进度，实际解出的样本可能略长或略短。</summary>
    TimeSpan Duration { get; }

    /// <summary>读一批样本进缓冲区，返回读到的个数，返回 0 即到了文件末尾。</summary>
    int Read(Span<float> buffer);
}
