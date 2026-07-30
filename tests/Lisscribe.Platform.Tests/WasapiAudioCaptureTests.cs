using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// WASAPI 共享模式采集这层薄壳：开设备、收尾次序、事件转发、异常安全。
/// 「字节 → 16kHz 单声道」那条链在 <see cref="CaptureSamplePipelineTests"/> 里精确验，本类只管壳；
/// 收尾次序真麦克风上没法确定性复现，用 <see cref="FakeWaveIn"/>（照真 WasapiCapture 的时序做的
/// 假件）验。<c>[Collection]</c> 不是摆设：本类三条用例会开真麦克风，靠「同属一个测试类所以串行」
/// 是偶然成立的，日后另开一个测试类碰同一个设备，串行就静默失效。
/// </summary>
[Collection(RecordingDeviceCollection.Name)]
public sealed class WasapiAudioCaptureTests
{
    private static readonly WaveFormat DeviceFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private static bool HasRecordingDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch
        {
            return false;
        }
    }

    // ---------- 设备名 ----------

    [Fact]
    public void CurrentDeviceName_WhenDeviceExists_IsNotEmpty()
    {
        Assert.SkipUnless(HasRecordingDevice(), "无默认录音设备");

        using var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);

        Assert.False(string.IsNullOrWhiteSpace(capture.CurrentDeviceName));
        Assert.NotEqual(WasapiAudioCapture.NoDeviceName, capture.CurrentDeviceName);
    }

    /// <summary>
    /// 查不到设备时给占位串而不是抛：这个属性是 UI 直接读的（托盘提示、配置窗），从它里面抛出去
    /// 等于「拔了麦克风就打不开设置界面」。用一个只决定「查询交出什么」的缝把这条降级路径真跑起来
    /// （跳过有麦克风的机器等于零覆盖）；异常类型挑 <c>COMException 0xE000020B</c>——读一个
    /// NotPresent 端点的 <c>FriendlyName</c> 真会得到它。
    /// </summary>
    [Fact]
    public void CurrentDeviceName_WhenTheLookupThrows_GivesThePlaceholderAndLogsIt()
    {
        var logger = new RecordingLogger<WasapiAudioCapture>();
        using var capture = new WasapiAudioCapture(logger)
        {
            OpenDefaultDeviceForTest = () => throw new COMException("设备没了", unchecked((int)0xE000020B)),
        };

        Assert.Equal(WasapiAudioCapture.NoDeviceName, capture.CurrentDeviceName);
        Assert.Equal(1, logger.CountOf(LogLevel.Warning));
    }

    /// <summary>
    /// <c>Dispose()</c> 之后设备名照样读得出来——被测类刻意不 Dispose <c>MMDeviceEnumerator</c>：
    /// 那会与不持锁的 <see cref="WasapiAudioCapture.CurrentDeviceName"/> 抢同一个 RCW，而它本就是
    /// 进程生命期的对象。反过来断言「Dispose 之后给占位串」是拿一个 NullReference 当契约。
    /// </summary>
    [Fact]
    public void CurrentDeviceName_AfterDispose_StillWorks()
    {
        Assert.SkipUnless(HasRecordingDevice(), "无默认录音设备");

        var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);
        capture.Dispose();

        Assert.NotEqual(WasapiAudioCapture.NoDeviceName, capture.CurrentDeviceName);
    }

    // ---------- 设备变更通知 ----------

    /// <summary>
    /// <c>IMMNotificationClient</c> 是个接口，直接调那个方法就能验事件转发，不必去插拔耳机。派发是
    /// 异步的，故这里等条件而不是等挂钟。
    /// </summary>
    [Fact]
    public void OnDefaultDeviceChanged_ForTheCaptureConsoleRole_RaisesTheEvent()
    {
        using var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);
        var raised = 0;
        capture.DefaultDeviceChanged += (_, _) => Interlocked.Increment(ref raised);

        capture.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, "{0.0.1.00000000}.{deadbeef}");
        Settle(capture);

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// 换一次默认设备，Windows 会把各个 Flow / Role 挨个通知一遍（切一次默认播放设备实测 7 次），
    /// 不过滤就等于用户插一次耳机、状态机被连着捅七下。这条负向断言不是「等一会儿看看没来」：
    /// <see cref="Settle"/> 等到所有已派发的通知执行完才返回，过滤器一拆，那次派发必在断言之前落地。
    /// </summary>
    [Theory]
    [InlineData(DataFlow.Render, Role.Console)]
    [InlineData(DataFlow.Render, Role.Multimedia)]
    [InlineData(DataFlow.Capture, Role.Communications)]
    [InlineData(DataFlow.Capture, Role.Multimedia)]
    public void OnDefaultDeviceChanged_ForAnyOtherFlowOrRole_IsIgnored(DataFlow flow, Role role)
    {
        using var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);
        var raised = 0;
        capture.DefaultDeviceChanged += (_, _) => Interlocked.Increment(ref raised);

        capture.OnDefaultDeviceChanged(flow, role, "{0.0.0.00000000}.{deadbeef}");
        Settle(capture);

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// <c>IMMNotificationClient</c> 的方法跑在 COM 通知线程上，MSDN 明令其 must be nonblocking、
    /// never wait on a synchronization object。同步派发的后果实测是挂死 15 秒不返回、连主线程的
    /// <c>SetDefaultEndpoint</c> 也一起卡在对 audiosrv 的 LPC 等待上，只能杀进程——因为订阅者会
    /// 一路走到 <c>Stop()</c> → <c>Dispose()</c>（Join）→ <c>MMDevice.Dispose()</c>。用一个一直
    /// 不返回的订阅者钉住它：谁把派发挪回同步，本条转红而不是把测试宿主挂住（等待带 10 秒上限）。
    /// </summary>
    [Fact]
    public void OnDefaultDeviceChanged_ReturnsWithoutWaitingForTheSubscriber()
    {
        using var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var finished = false;
        capture.DefaultDeviceChanged += (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            Volatile.Write(ref finished, true);
        };

        capture.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, "a");

        Assert.True(
            entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "5 秒内订阅者根本没被调到——事件压根没派发出去");
        Assert.False(
            Volatile.Read(ref finished),
            "OnDefaultDeviceChanged 一直等到订阅者跑完才返回：COM 通知线程被阻塞了");

        release.Set();
        Settle(capture);
    }

    /// <summary>
    /// 通知订阅者抛异常：事件流必须继续，且必须留下一条 Error 日志。异常逃出
    /// <c>IMMNotificationClient</c> 的方法不会终结进程（CCW 把它翻成失败 HRESULT 还给音频服务，
    /// 后续通知照常到达），故这层 catch 买的是日志不是存活——没有它，订阅者悄悄炸了而我们既没
    /// 日志也没异常。
    /// </summary>
    [Fact]
    public void OnDefaultDeviceChanged_WhenTheSubscriberThrows_KeepsDeliveringAndLogsIt()
    {
        var logger = new RecordingLogger<WasapiAudioCapture>();
        using var capture = new WasapiAudioCapture(logger);
        var boom = new InvalidOperationException("订阅者炸了");
        var seen = 0;
        capture.DefaultDeviceChanged += (_, _) =>
        {
            Interlocked.Increment(ref seen);
            throw boom;
        };

        capture.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, "a");
        capture.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, "b");
        Settle(capture);

        Assert.Equal(2, seen);
        Assert.Equal(2, logger.TimesRecorded(boom));
    }

    /// <summary>
    /// 「默认录音设备已变更」那句日志挡在 <c>Invoke</c> 前面，它抛出就等于把事件静默吃掉——用户拔了
    /// 耳机而状态机毫不知情，傻等 KEYUP，前半句被「录音过短」吞掉。安全性来自构造函数里那道
    /// <c>.Safe()</c>，这条是它在本类里唯一的钉子（没有它，去掉 <c>.Safe()</c> 全套测试都不会红）。
    /// </summary>
    [Fact]
    public void OnDefaultDeviceChanged_WhenLoggingItselfThrows_TheEventStillReachesSubscribers()
    {
        var logger = new RecordingLogger<WasapiAudioCapture> { ThrowOnMessageContaining = "默认录音设备已变更" };
        using var capture = new WasapiAudioCapture(logger);
        var raised = 0;
        capture.DefaultDeviceChanged += (_, _) => Interlocked.Increment(ref raised);

        capture.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, "a");
        Settle(capture);

        Assert.Equal(1, raised);
    }

    // ---------- 生命周期 ----------

    [Fact]
    public void Stop_WithoutStart_DoesNotThrow()
    {
        using var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);

        capture.Stop();
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);

        capture.Dispose();
        capture.Dispose();
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);
        capture.Dispose();

        Assert.Throws<ObjectDisposedException>(capture.Start);
    }

    // ---------- 收尾次序（用假件，见 FakeWaveIn 的说明） ----------

    [Fact]
    public void Start_ThenNativePackets_RaisesResampled16kMonoSamples()
    {
        var fake = new FakeWaveIn(DeviceFormat);
        using var capture = NewCaptureBackedBy(fake);
        var total = 0;
        capture.DataAvailable += (_, data) => Interlocked.Add(ref total, data.Length);

        capture.Start();
        fake.Deliver(Tone48kStereoFrames(48000)); // 1 秒
        capture.Stop();

        Assert.Equal(1, fake.StartCount);
        Assert.InRange(total, (int)(16000 * 0.99), (int)(16000 * 1.01));
    }

    /// <summary>
    /// <c>StopRecording()</c> 只置个标志就返回（0.0–0.2ms），采集线程此刻可能还有一包在途。谁在
    /// Stop 里先把管道置空，那一包就被静默丢掉——订阅者跟得上时是 60ms 的音频，跟不上则到 WASAPI
    /// 缓冲上限 100ms。救回它的不是 <c>Flush()</c>（这条链上它恒空），而是「管道归会话自己持有、
    /// 不被 Stop 置空」。
    /// </summary>
    [Fact]
    public void Stop_WithAPacketStillInFlight_DeliversItInsteadOfDroppingTheTail()
    {
        var fake = new FakeWaveIn(DeviceFormat) { PacketStillInFlightAtStop = Tone48kStereoFrames(4800) };
        using var capture = NewCaptureBackedBy(fake);
        var total = 0;
        capture.DataAvailable += (_, data) => Interlocked.Add(ref total, data.Length);

        capture.Start();
        fake.Deliver(Tone48kStereoFrames(43200)); // 0.9 秒正常送达
        capture.Stop();

        // 0.9s + 0.1s = 1s 的音频，一包都不许少。
        Assert.InRange(total, (int)(16000 * 0.99), (int)(16000 * 1.01));
    }

    /// <summary>
    /// <c>Stop()</c> 返回之后一个样本也不许再来。这是订阅者唯一能倚靠的次序保证：状态机在锁里调
    /// <c>Stop()</c>，紧接着就取走缓冲，迟到的样本会落进刚清空的缓冲、随下一次会话送出。保证的来源
    /// 不是加锁，而是 <c>WasapiCapture.Dispose()</c> 会 Join 采集线程。
    /// <para>
    /// 强度要说清楚：<c>arrivedAfterStopReturned == 0</c> 由假件的构造恒真（<c>Dispose()</c> 先
    /// <c>Set()</c> 再 <c>Join()</c>），承重的是下面两句次序断言——把 <c>Dispose()</c> 排到
    /// <c>StopRecording()</c> 之前、或干脆不 <c>Dispose()</c>，本条都会红。
    /// </para>
    /// </summary>
    [Fact]
    public void Stop_AfterItReturns_NoMoreSamplesArrive()
    {
        var fake = new FakeWaveIn(DeviceFormat) { PacketStillInFlightAtStop = Tone48kStereoFrames(4800) };
        using var capture = NewCaptureBackedBy(fake);
        var stopReturned = false;
        var arrivedAfterStopReturned = 0;
        capture.DataAvailable += (_, _) =>
        {
            if (Volatile.Read(ref stopReturned))
            {
                Interlocked.Increment(ref arrivedAfterStopReturned);
            }
        };

        capture.Start();
        fake.Deliver(Tone48kStereoFrames(4800));
        capture.Stop();
        Volatile.Write(ref stopReturned, true);

        Assert.Equal(0, arrivedAfterStopReturned);
        Assert.Equal(1, fake.StopCount);
        // Dispose 是那条保证的唯一来源：只 StopRecording 不 Dispose，采集线程根本不会收尾。
        Assert.Equal(1, fake.DisposeCount);
    }

    /// <summary>
    /// 采集线程上订阅者抛的异常必须就地咽下并记账。它不崩进程（NAudio 自己 catch 了），但后果比崩
    /// 更隐蔽：录音当场静默停止，而用户只看到悬浮条还绿着、说完却什么也没上屏。与
    /// <see cref="RecordingStopped_WithAnException_IsLoggedAndDoesNotEscape"/> 那条不同——
    /// <c>RecordingStopped</c> 里逃逸的异常是进程终结，NAudio 在引发它那一句上没有 catch。
    /// </summary>
    [Fact]
    public void DataAvailable_WhenTheSubscriberThrows_IsSwallowedSoRecordingKeepsRunning()
    {
        var logger = new RecordingLogger<WasapiAudioCapture>();
        var fake = new FakeWaveIn(DeviceFormat);
        using var capture = NewCaptureBackedBy(fake, logger);
        var boom = new InvalidOperationException("订阅者炸了");
        var deliveries = 0;
        capture.DataAvailable += (_, _) =>
        {
            Interlocked.Increment(ref deliveries);
            throw boom;
        };

        capture.Start();
        fake.Deliver(Tone48kStereoFrames(4800));
        fake.Deliver(Tone48kStereoFrames(4800));
        capture.Stop();

        Assert.Equal(2, deliveries);
        Assert.Equal(2, logger.TimesRecorded(boom));
    }

    /// <summary>
    /// 采集中途出错时 <c>RecordingStopped</c> 会带着异常回来，而这个处理器里逃逸的异常是进程终结
    /// （<c>IsTerminating=True</c>，退出码 -532462766 / 0xE0434352）。死得并不快——从抛出到进程真的
    /// 结束要 3.6–4.0 秒，主线程若在这窗口里自己跑完，同一个崩溃会被记成「还活着、退出码 0」。
    /// </summary>
    [Fact]
    public void RecordingStopped_WithAnException_IsLoggedAndDoesNotEscape()
    {
        var logger = new RecordingLogger<WasapiAudioCapture>();
        var died = new InvalidOperationException("采集中途死了");
        var fake = new FakeWaveIn(DeviceFormat) { StoppedWith = died };
        using var capture = NewCaptureBackedBy(fake, logger);

        capture.Start();
        capture.Stop();

        Assert.Equal(1, logger.TimesRecorded(died));
        Assert.Null(fake.EscapedFromRecordingStopped);
    }

    /// <summary>
    /// <c>OnStopped</c> 是全类唯一「逃逸即进程终结」的位置，而挡在它里面的那句 <c>LogError</c> 自己
    /// 也会抛（磁盘满、句柄没了、sink 自己炸）。安全性来自构造函数里那道 <c>.Safe()</c>。
    /// </summary>
    [Fact]
    public void RecordingStopped_WhenLoggingTheFailureItselfThrows_NothingEscapesTheCaptureThread()
    {
        var logger = new RecordingLogger<WasapiAudioCapture> { ThrowOnMessageContaining = "录音异常终止" };
        var died = new InvalidOperationException("采集中途死了");
        var fake = new FakeWaveIn(DeviceFormat) { StoppedWith = died };
        using var capture = NewCaptureBackedBy(fake, logger);

        capture.Start();
        capture.Stop();

        Assert.Equal(1, logger.TimesRecorded(died));
        Assert.Null(fake.EscapedFromRecordingStopped);
    }

    /// <summary>
    /// 跨线程并发 + 样本记账：采集线程持续送包的同时，收尾在另一条线程上跑。判据是记账——预言机取自
    /// 假件自己数的「送出去多少包」（纯输入侧，与被测管道无关），期望值按 48kHz→16kHz 的比例算。
    /// <para>
    /// 强度到此为止，别按名字多读一分：本条钉住的是「丢样本 / 重放样本」（隔一包丢一包实测转红）。
    /// 删掉 Join 它仍绿（那件事由 <see cref="Stop_AfterItReturns_NoMoreSamplesArrive"/> 一族守着）；
    /// 把 <c>Flush()+emit</c> 提到收尾之前制造真并发，连跑 4 轮也全绿——那是单发竞争，一次运行只有
    /// 一次撞上的机会且 <c>Flush()</c> 恒空，没有哪种判据能可靠地把它转红，只能靠收尾次序守着。
    /// </para>
    /// </summary>
    [Fact]
    public void Stop_WhileTheCaptureThreadIsStillPushingPackets_AccountsForEverySample()
    {
        const int framesPerPacket = 480; // 48k 立体声一包 10ms → 16k 单声道 160 个样本
        var logger = new RecordingLogger<WasapiAudioCapture>();
        var fake = new FakeWaveIn(DeviceFormat) { ContinuousPacket = Ramp48kStereoFrames(framesPerPacket) };
        using var capture = NewCaptureBackedBy(fake, logger);
        using var flowing = new ManualResetEventSlim();
        var stopReturned = false;
        var arrivedAfterStopReturned = 0;
        var total = 0;
        var outOfRange = 0;

        capture.DataAvailable += (_, data) =>
        {
            if (Volatile.Read(ref stopReturned))
            {
                Interlocked.Increment(ref arrivedAfterStopReturned);
            }

            foreach (var sample in data.Span)
            {
                // 输入是 0→1 的斜坡，重采样滤波器在包与包的跳变处会有振铃，故留宽边界；
                // 这一条只拦「值整个变成垃圾」，细的靠下面的记账。
                if (sample is < -0.6f or > 1.6f)
                {
                    Interlocked.Increment(ref outOfRange);
                }
            }

            if (Interlocked.Add(ref total, data.Length) >= 8 * framesPerPacket / 3)
            {
                flowing.Set();
            }
        };

        capture.Start();
        Assert.True(
            flowing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "10 秒内假采集线程送出的样本不够");
        capture.Stop();
        Volatile.Write(ref stopReturned, true);

        // 预言机取自假件的输入侧计数，与被测管道无关。
        var expected = fake.DeliveredPacketCount * framesPerPacket / 3.0;
        Assert.InRange(total, expected * 0.98, expected * 1.02);
        Assert.Equal(0, outOfRange);
        Assert.Equal(0, arrivedAfterStopReturned);
        Assert.Equal(0, logger.CountOf(LogLevel.Error));
    }

    /// <summary>
    /// <c>Dispose()</c> 期间不许再往订阅者身上发音频：那时订阅者（状态机）可能已经半关停，
    /// 而这段尾巴属于一次没人会再消费的会话。
    /// </summary>
    [Fact]
    public void Dispose_WhileRecording_StopsTheDeviceWithoutRaisingMoreSamples()
    {
        var fake = new FakeWaveIn(DeviceFormat) { PacketStillInFlightAtStop = Tone48kStereoFrames(4800) };
        var capture = NewCaptureBackedBy(fake);
        var afterDisposeStarted = 0;
        var disposing = false;
        capture.DataAvailable += (_, _) =>
        {
            if (Volatile.Read(ref disposing))
            {
                Interlocked.Increment(ref afterDisposeStarted);
            }
        };

        capture.Start();
        fake.Deliver(Tone48kStereoFrames(4800));
        Volatile.Write(ref disposing, true);
        capture.Dispose();

        Assert.Equal(0, afterDisposeStarted);
        Assert.Equal(1, fake.DisposeCount);
    }

    /// <summary>
    /// 认不出的采集格式必须让 <c>Start()</c> 明确失败，而且**不许留下一个开着的设备**——
    /// 否则麦克风被一个谁也够不着的对象占着，直到进程退出。
    /// </summary>
    [Fact]
    public void Start_WithAFormatWeCannotDecode_ThrowsAndLeavesNothingOpen()
    {
        var fake = new FakeWaveIn(new WaveFormat(48000, 24, 2));
        using var capture = NewCaptureBackedBy(fake);

        Assert.Throws<NotSupportedException>(capture.Start);

        Assert.Equal(0, fake.StartCount);
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public void Start_CalledTwice_OnlyOpensOneDevice()
    {
        var opened = 0;
        var fake = new FakeWaveIn(DeviceFormat);
        using var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance)
        {
            OpenCaptureForTest = () =>
            {
                opened++;
                return (fake, null, "假设备");
            },
        };

        capture.Start();
        capture.Start();
        capture.Stop();

        Assert.Equal(1, opened);
        Assert.Equal(1, fake.StartCount);
    }

    // ---------- 真设备：只验管道通了、出来的确实是 16kHz ----------

    /// <summary>
    /// 不靠挂钟定采样数，而是等条件（攒够 1 秒的量或超时）再用实际耗时反算速率，判据是「每秒 16000
    /// 个样本」这个契约本身。速率只用第一批之后的样本与第一批到最后一批之间的耗时算，把 WASAPI 的
    /// 启动延迟整个排除在外。
    /// <para>
    /// 容差不对称，两边各有道理：上界 +2%——48kHz 源经 3:1 重采样不可能多于每秒 16000 个样本，超出
    /// 即比例算错；下界 -40%——这个算法量到的分母是回调之间的调度延迟而非采样率，20 线程满载时实测
    /// 偏差到 -31%。比例正确性已由 <c>CaptureSamplePipelineTests</c> 以 ±1% 钉死，这里只守「不是别的
    /// 数量级」和「不可能超上界」。
    /// </para>
    /// </summary>
    [Fact]
    public void Start_WithTheRealDefaultDevice_DeliversRoughly16000SamplesPerSecond()
    {
        Assert.SkipUnless(HasRecordingDevice(), "无默认录音设备");

        using var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);
        var enough = new ManualResetEventSlim();
        var gate = new Lock();
        long firstTicks = 0, lastTicks = 0;
        var samplesAtFirst = 0;
        var samplesAtLast = 0;
        var total = 0;

        capture.DataAvailable += (_, data) =>
        {
            lock (gate)
            {
                total += data.Length;
                if (firstTicks == 0)
                {
                    firstTicks = Stopwatch.GetTimestamp();
                    samplesAtFirst = total;
                    return;
                }

                lastTicks = Stopwatch.GetTimestamp();
                samplesAtLast = total;
                if (samplesAtLast - samplesAtFirst >= 16000)
                {
                    enough.Set();
                }
            }
        };

        capture.Start();
        try
        {
            Assert.True(
                enough.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                "10 秒内没能从真设备收到 1 秒的音频");
        }
        finally
        {
            capture.Stop();
        }

        double rate;
        lock (gate)
        {
            var seconds = Stopwatch.GetElapsedTime(firstTicks, lastTicks).TotalSeconds;
            rate = (samplesAtLast - samplesAtFirst) / seconds;
        }

        Assert.InRange(rate, 16000 * 0.60, 16000 * 1.02);
    }

    [Fact]
    public void StartStop_RepeatedlyOnTheRealDevice_DoesNotThrow()
    {
        Assert.SkipUnless(HasRecordingDevice(), "无默认录音设备");

        using var capture = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance);

        for (var i = 0; i < 3; i++)
        {
            capture.Start();
            capture.Stop();
        }
    }

    // ---------- 助手 ----------

    /// <summary>
    /// 等到本实例已派发的设备变更通知全部执行完。它只是同步手段、不是预言机：这里读的是被测代码自己
    /// 的在途计数，拿它当判据就是让被测方自己出题，拿它来等则等错了只会超时。
    /// </summary>
    private static void Settle(WasapiAudioCapture capture)
    {
        var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10);
        while (capture.NotificationDispatchesInFlight > 0)
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                Assert.Fail($"10 秒后仍有 {capture.NotificationDispatchesInFlight} 条设备变更通知没派发完");
            }

            Thread.Sleep(1);
        }
    }

    private static WasapiAudioCapture NewCaptureBackedBy(
        FakeWaveIn fake, ILogger<WasapiAudioCapture>? logger = null) =>
        new(logger ?? NullLogger<WasapiAudioCapture>.Instance)
        {
            OpenCaptureForTest = () => (fake, null, "假设备"),
        };

    /// <summary>0→1 的斜坡，两声道同值。常数信号不行——缓冲复用出错时抄到的旧内容也是那个常数。</summary>
    private static byte[] Ramp48kStereoFrames(int frames)
    {
        var bytes = new byte[frames * 2 * sizeof(float)];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)i / frames;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8), value);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i * 8) + 4), value);
        }

        return bytes;
    }

    private static byte[] Tone48kStereoFrames(int frames)
    {
        var bytes = new byte[frames * 2 * sizeof(float)];
        for (var i = 0; i < frames * 2; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), 0.05f);
        }

        return bytes;
    }
}

/// <summary>
/// 会去开真录音设备的测试都归这一组，串行执行。共享模式允许多客户端同开一个设备，所以这不是
/// 「开不了」而是速率测量的问题：按耗时反算采样率的用例一旦与别的开设备用例并行，量到的就是两者
/// 叠加后的调度抖动。
/// </summary>
[CollectionDefinition(Name)]
public sealed class RecordingDeviceCollection
{
    public const string Name = "真录音设备（速率测量不可与同类用例并行）";
}
