using NAudio.Wave;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 一个照着真 <c>WasapiCapture</c> 实测时序做的假采集器。真设备做不到确定性：要验的那条缺陷
/// （收尾时最后一包被丢掉）只在「<c>StopRecording()</c> 已返回、采集线程还有一包在途」这个窗口里
/// 出现，真麦克风上出不出现全看相位。
///
/// <para>照搬的三条时序：</para>
/// <list type="number">
/// <item><c>StopRecording()</c> 只置一个标志就返回（零点几毫秒），采集线程此刻可能正卡在一次
/// <c>DataAvailable</c> 里。<b>这个窗口真实存在，但命不命中取决于相位</b>——扫过一批相位命中约
/// 七成，换个相位可以整轮不中。别把它当必现。</item>
/// <item><c>Dispose()</c> 会 Join 采集线程，<b>但只在采集线程还活着时才 Join</b>：NAudio 在
/// <c>RaiseRecordingStopped</c> 之前就把 <c>captureThread</c> 置 null，故自停路径（订阅者在
/// DataAvailable 抛异常、拔设备、驱动出错）上一次 Join 也不做——自停后即使 RecordingStopped
/// 睡 800ms，<c>Dispose()</c> 也是二十毫秒就返回。</item>
/// <item><c>RecordingStopped</c> 在采集线程上引发，且 NAudio 在那一句上<b>没有</b> try/catch。</item>
/// </list>
///
/// <para>
/// <b>本假件不能用来证明这些时序本身</b>——那是真设备探针的活。它证明的是：<b>给定</b>这些时序，
/// 被测代码的收尾次序是对的。
/// </para>
/// </summary>
internal sealed class FakeWaveIn : IWaveIn
{
    private readonly ManualResetEventSlim _releaseCaptureThread = new();
    private volatile bool _capturing;
    private Thread? _captureThread;
    private int _deliveredPackets;

    public FakeWaveIn(WaveFormat format) => WaveFormat = format;

    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>
    /// 采集线程一共送出去多少包。**这是纯输入侧的计数，与被测管道无关**，
    /// 故可以拿来当样本记账的预言机（读它要在 <see cref="Dispose"/> 之后，那时线程已收尾）。
    /// </summary>
    public int DeliveredPacketCount => _deliveredPackets;

    /// <summary>
    /// 采集线程在「<c>StopRecording()</c> 已返回、<c>Dispose()</c> 尚未返回」这个窗口里
    /// 补送的最后一包。置 null 表示不补送。
    /// </summary>
    public byte[]? PacketStillInFlightAtStop { get; set; }

    /// <summary>
    /// 非 null 时采集线程在录音期间**持续**送这一包，制造真正的跨线程并发。
    /// 不加它的话，所有用例的 <see cref="Deliver"/> 都跑在测试线程上，于是
    /// <c>_muted</c> 的 volatile 强度、Join 的 happens-before、订阅者重入
    /// **一条也执行不到**——整套用例其实是单线程的。
    /// </summary>
    public byte[]? ContinuousPacket { get; set; }

    /// <summary>采集线程收尾时随 <see cref="RecordingStopped"/> 交出的异常（模拟采集中途出错）。</summary>
    public Exception? StoppedWith { get; set; }

    /// <summary>
    /// 从 <see cref="RecordingStopped"/> 处理器里逃出来的异常。
    ///
    /// <b>真 NAudio 在这一句上没有 catch，逃逸即进程终结</b>（<c>IsTerminating=True</c>，
    /// 退出码 <c>-532462766 / 0xE0434352</c>）。假件把它接住只为让回归变成一条红测试，
    /// 而不是把整个测试宿主带走——这里是刻意偏离真实行为，偏离的方向就写在这儿。
    /// </summary>
    public Exception? EscapedFromRecordingStopped { get; private set; }

    public void StartRecording()
    {
        StartCount++;
        _releaseCaptureThread.Reset();
        _capturing = true;
        _captureThread = new Thread(CaptureThreadBody) { IsBackground = true, Name = "fake-wasapi-capture" };
        _captureThread.Start();
    }

    /// <summary>只置标志，**不等采集线程**——与真 WasapiCapture 一致。</summary>
    public void StopRecording()
    {
        StopCount++;
        _capturing = false;
    }

    /// <summary>Join 采集线程——与真 WasapiCapture 一致。</summary>
    public void Dispose()
    {
        DisposeCount++;
        _capturing = false;
        _releaseCaptureThread.Set();
        _captureThread?.Join();
        _captureThread = null;
    }

    /// <summary>在调用方自己的线程上送一包。</summary>
    public void Deliver(byte[] bytes) =>
        DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));

    private void CaptureThreadBody()
    {
        while (_capturing)
        {
            if (ContinuousPacket is { } packet)
            {
                Interlocked.Increment(ref _deliveredPackets);
                Deliver(packet);
            }

            Thread.Sleep(1);
        }

        // 等 Dispose() 放行，好让「这一包严格晚于 StopRecording() 返回」成为确定事实，
        // 而不是靠抢时序碰运气。
        _releaseCaptureThread.Wait();

        if (PacketStillInFlightAtStop is { } late)
        {
            PacketStillInFlightAtStop = null;
            Deliver(late);
        }

        try
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(StoppedWith));
        }
        catch (Exception ex)
        {
            EscapedFromRecordingStopped = ex;
        }
    }
}
