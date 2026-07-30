using System.Diagnostics;
using System.Runtime.InteropServices;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Audio;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Lisscribe.Core.Asr;

/// <summary>
/// 两档引擎共用的骨架：句柄的生命周期、闸门、取消的能力边界。
/// 两档的差别只有三处——模型文件长什么样、<c>OfflineRecognizerConfig</c> 怎么填、叫什么名字；
/// 剩下的全是「原生句柄不能在推理半途被抽走」这类一旦写错就是进程级 AccessViolation 的东西，
/// 复制一份等于把那些坑复制一份，而第二份不会有人再去踩一遍。
/// 派生类只需实现 <see cref="EngineName"/>、<see cref="ValidateModelFiles"/> 与
/// <see cref="BuildRecognizerConfig"/> 三个成员。
/// </summary>
public abstract class OfflineRecognizerEngine : IAsrEngine
{
    /// <summary>实测拐点。8 线程相对 4 线程只快约 11%，不值得多占的核。</summary>
    protected const int NumThreads = 4;

    /// <summary>目前唯一受支持的 provider。见 <see cref="LoadAsync"/> 里为何不接受别的值。</summary>
    private const string SupportedProvider = "cpu";

    /// <summary>
    /// 原生句柄的唯一守门人：LoadAsync / RecognizeAsync / DisposeAsync 三者互斥。
    /// 刻意不 Dispose 这个 SemaphoreSlim——从不取它的 AvailableWaitHandle，故它没有需要释放的
    /// 非托管资源；而释放它会让「正好排在 Dispose 之后的那次 RecognizeAsync」在 WaitAsync 上
    /// 撞见未定义行为。留着它，迟到的调用就能老实地拿到闸门、看见 _disposed、抛一个干净的
    /// ObjectDisposedException。
    /// </summary>
    private readonly SemaphoreSlim _nativeGate = new(1, 1);

    private readonly AsrSection _config;

    private readonly ILogger _logger;

    private OfflineRecognizer? _recognizer;

    /// <summary>
    /// 当前的模型目录。不直接读 <c>config.ModelDir</c>，因为它得能改：引擎加载失败之后，
    /// 用户在配置窗里把目录改对，那条自救路径要落在这儿（见 <see cref="RepointModelDir"/>）。
    /// </summary>
    private string _modelDir;

    // 0 = 存活，1 = 已释放。Dispose 与推理可能来自不同线程（队列 worker 与关机路径），
    // 故用 Interlocked/Volatile 而非普通 bool 字段。
    private int _disposed;

    protected OfflineRecognizerEngine(AsrSection config, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _modelDir = config.ModelDir;
    }

    public bool IsLoaded => Volatile.Read(ref _disposed) == 0 && _recognizer is not null;

    public abstract string EngineName { get; }

    /// <summary>下一次 <see cref="LoadAsync"/> 会去找模型的目录。</summary>
    public string ModelDir => _modelDir;

    /// <summary>
    /// 换模型目录，只对尚未加载的引擎有效。用户改完配置窗的「模型目录」再保存，走的就是这条路。
    /// 已加载就拒绝，不是保守是必须：此时换址只能换出「配置说的是 A、跑着的是 B」这种查无可查的
    /// 状态；真要换得换一个引擎实例，而它已经被状态机攥着了——就地热换要么泄掉旧的原生句柄，
    /// 要么在识别半途把它抽走，那是 catch 不到的 AccessViolation。
    /// </summary>
    public void RepointModelDir(string modelDir)
    {
        ThrowIfDisposed();

        if (IsLoaded)
        {
            throw new InvalidOperationException(
                "引擎已加载，不能换模型目录；换目录须重建引擎（现实做法是重启程序）。");
        }

        _modelDir = modelDir;
    }

    /// <summary>
    /// 加载模型。
    /// </summary>
    /// <remarks>
    /// 取消的能力边界（调用方必读）：原生 <c>OfflineRecognizer</c> 构造是一次长达一两秒的阻塞
    /// 调用，sherpa-onnx 没有给出任何中断入口，没法真的打断它。本方法只在三个能观察的位置观察
    /// token：进入时、抢闸门时、以及原生加载返回之后（此时若已取消，就把刚建好的识别器销毁并抛
    /// <see cref="OperationCanceledException"/>，绝不返回一个「取消了却仍然加载成功」的引擎）。
    /// 后果：最坏情况下调用方仍要等满整个加载耗时才看到取消异常。不要在此之上做「立即生效」的
    /// 取消按钮——UI 会在按下后继续僵住那么久。
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // 调用方已经撤单就别开工了。放在最前面：连配置和文件都不必看。
        cancellationToken.ThrowIfCancellationRequested();

        if (_config.Provider != SupportedProvider)
        {
            // sherpa-onnx 官方 NuGet 只编入 CPU EP。传别的值它不报错，只打条日志然后静默用 CPU
            // ——用户会以为自己在跑 GPU。这道检查必须排在文件检查之前：配置错了就该直说配置错，
            // 而不是先拿「模型找不到」把人引到错误的方向。
            throw new NotSupportedException(
                $"目前只支持 asr.provider = \"{SupportedProvider}\"。收到 \"{_config.Provider}\"。" +
                "sherpa-onnx 官方 NuGet 为纯 CPU 构建，指定其他 provider 只会静默回落。");
        }

        // 缺失与损坏都必须赶在文件路径递给原生加载器之前查。过了那道门，损坏的 onnx 会让原生
        // Ort::Exception 直接逃逸成进程级崩溃，进程已经没了，谈不上「处理错误」。
        ValidateModelFiles(_modelDir);

        await _nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();

            if (_recognizer is not null)
            {
                // 重复加载直接返回。若每次都新建，旧的原生句柄就此失联，每存一次配置泄掉一整份
                // 常驻内存。
                _logger.LogDebug("{Engine} 已加载，跳过重复加载", EngineName);

                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();

            // 原生构造是纯阻塞调用且不认 CancellationToken，丢到线程池跑，免得启动时冻住 UI 线程。
            // 作用域必须整个待在这个委托里：它可能改进程级的当前工作目录（模型落在中文路径下时的
            // 唯一活路，见 NativeModelDirectoryScope），跨 await 持有等于把那格全局状态交给调度器
            // 摆布。配置也得在域内建——路径要按域给的根拼。
            var recognizer = await Task.Run(
                () =>
                {
                    using var scope = NativeModelDirectoryScope.Enter(_modelDir);

                    return new OfflineRecognizer(BuildRecognizerConfig(scope.RootForNative));
                },
                CancellationToken.None)
                .ConfigureAwait(false);

            stopwatch.Stop();

            // 原生没建起来时，这里是唯一还能说清原因的地方，见 EnsureNativeHandleUsable。
            EnsureNativeHandleUsable(recognizer);

            if (cancellationToken.IsCancellationRequested)
            {
                // 这段时间已经花掉了，退不回来。但「取消了却仍然 IsLoaded == true」是更坏的结果：
                // 上层状态机会以为引擎可用，而调用方以为自己撤销了。宁可丢掉刚建好的识别器。
                recognizer.Dispose();

                _logger.LogInformation(
                    "{Engine} 加载完成（{Elapsed}ms）后发现调用方已取消，丢弃刚建好的识别器",
                    EngineName, stopwatch.ElapsedMilliseconds);

                cancellationToken.ThrowIfCancellationRequested();
            }

            _recognizer = recognizer;

            _logger.LogInformation(
                "{Engine} 加载完成，耗时 {Elapsed}ms，线程数 {Threads}，模型目录 {Dir}",
                EngineName, stopwatch.ElapsedMilliseconds, NumThreads, _modelDir);
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    public async Task<string> RecognizeAsync(
        ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // OfflineRecognizer 的原生句柄非线程安全。上层是串行队列，此闸门是第二道保险，
        // 同时保证 DisposeAsync 不会在解码半途抽走句柄（那是 catch 不到的 AccessViolation）。
        await _nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 进闸门前的检查只是快速失败；真正作数的是这里——闸门内看到的状态才稳定。
            ThrowIfDisposed();

            var recognizer = _recognizer
                ?? throw new InvalidOperationException("引擎尚未加载，请先调用 LoadAsync");

            cancellationToken.ThrowIfCancellationRequested();

            using var stream = recognizer.CreateStream();

            // AcceptWaveform 只收 float[]；ReadOnlyMemory 若本就由数组撑着就直接借用，
            // 免掉一次几百 KB 的复制。
            stream.AcceptWaveform(AudioResampler.TargetSampleRate, AsArray(samples));
            recognizer.Decode(stream);

            return stream.Result.Text;
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            // 重复 Dispose 是空操作：`await using` 之外常还有一处显式释放，第二次不能炸。
            return;
        }

        // 等在途推理结束再释放原生句柄。抢在前面释放会触发进程级 AccessViolation。
        await _nativeGate.WaitAsync().ConfigureAwait(false);

        try
        {
            _recognizer?.Dispose();

            _recognizer = null;
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    /// <summary>
    /// 模型文件齐不齐、坏没坏。缺失抛 <see cref="FileNotFoundException"/>，
    /// 损坏抛 <see cref="InvalidDataException"/>，两条错误路径不许互相抢话。
    /// </summary>
    protected abstract void ValidateModelFiles(string modelDir);

    /// <summary>
    /// 填好该档的 <c>OfflineRecognizerConfig</c>。线程数与 provider 也在这里落。
    /// 参数是「递给原生的根」，不是配置里那个模型目录——两者只在纯 ASCII 路径下相等。
    /// 故这里必须用 <c>Under(nativeRoot)</c> 拼路径，不能用 <c>Resolve</c>：后者会按
    /// <see cref="AppContext.BaseDirectory"/> 再展开一次，把切好工作目录的那条路径打回原形。
    /// </summary>
    protected abstract OfflineRecognizerConfig BuildRecognizerConfig(string nativeRoot);

    /// <summary>派生类填 provider 时用它，免得两档各写各的字符串。</summary>
    protected string Provider => _config.Provider;

    /// <summary>
    /// 确认原生识别器真的建起来了；没有就抛，绝不放一个空句柄出去。
    /// </summary>
    /// <remarks>
    /// 非补不可的一道门：<c>SherpaOnnxCreateOfflineRecognizer</c> 在配置校验失败时把原因打到
    /// stderr 然后返回 <c>NULL</c>，而 sherpa-onnx 的 C# 包装不检查这个返回值，照样把空句柄裹成
    /// 对象交出来——一次异常也没有。少了这道门，上面那条「加载完成，耗时 N 毫秒」就是假的
    /// （实测 N=1，而真加载 3180ms），<see cref="IsLoaded"/> 也是真的，错误一路推迟到每次识别，
    /// 从 <c>OfflineRecognizerResult..ctor</c> 里炸出一个不含任何原因的
    /// <see cref="NullReferenceException"/>。
    /// 怎么问出句柄：包装没把识别器句柄公开，但流句柄是公开的，而空识别器只会造出空流。
    /// 建一个流再扔掉，代价可以忽略。
    /// </remarks>
    private void EnsureNativeHandleUsable(OfflineRecognizer recognizer)
    {
        using (var probe = recognizer.CreateStream())
        {
            if (probe.Handle != IntPtr.Zero)
            {
                return;
            }
        }

        recognizer.Dispose();

        throw new InvalidOperationException(NativeRecognizerFailure.Describe(_modelDir));
    }

    private static float[] AsArray(ReadOnlyMemory<float> samples) =>
        MemoryMarshal.TryGetArray(samples, out var segment)
        && segment.Array is { } array
        && segment.Offset == 0
        && segment.Count == array.Length
            ? array
            : samples.ToArray();

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
