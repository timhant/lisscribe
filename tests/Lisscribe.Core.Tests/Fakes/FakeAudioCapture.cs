using Lisscribe.Core.Abstractions;

namespace Lisscribe.Core.Tests.Fakes;

public sealed class FakeAudioCapture : IAudioCapture
{
    public event EventHandler<ReadOnlyMemory<float>>? DataAvailable;
    public event EventHandler? DefaultDeviceChanged;

    public string CurrentDeviceName { get; set; } = "Fake Microphone";
    public bool IsCapturing { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    /// <summary>置非 null 则 Stop 抛出它。生产环境这里是 WASAPI 的原生包装,会抛;
    /// 用于验证调用方在采集器停不下来时既不崩溃、也不把状态机卡死在 Recording。</summary>
    public Exception? ThrowOnStop { get; set; }

    public void Start()
    {
        IsCapturing = true;
        StartCount++;
    }

    public void Stop()
    {
        StopCount++;

        if (ThrowOnStop is not null)
        {
            throw ThrowOnStop;
        }

        IsCapturing = false;
    }

    public void PushSamples(float[] samples) => DataAvailable?.Invoke(this, samples);

    /// <summary>推入指定秒数的静音,用于把缓冲喂到某个时长。</summary>
    public void PushSeconds(double seconds) =>
        PushSamples(new float[(int)(seconds * Lisscribe.Core.Audio.AudioResampler.TargetSampleRate)]);

    public void RaiseDefaultDeviceChanged(string newName)
    {
        CurrentDeviceName = newName;
        DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
