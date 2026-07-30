namespace Lisscribe.Core.Abstractions;

public interface IAudioCapture : IDisposable
{
    /// <summary>
    /// 已重采样为 16kHz 单声道 float32 的样本。
    /// 实现方负责 48k/2ch → 16k/1ch 的转换(WASAPI 共享模式给不了 16kHz)。
    /// </summary>
    event EventHandler<ReadOnlyMemory<float>>? DataAvailable;

    /// <summary>系统默认录音设备变更(用户插拔耳机)。</summary>
    event EventHandler? DefaultDeviceChanged;

    string CurrentDeviceName { get; }

    void Start();

    void Stop();
}
