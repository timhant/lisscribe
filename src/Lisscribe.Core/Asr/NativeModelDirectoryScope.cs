namespace Lisscribe.Core.Asr;

/// <summary>
/// 让 sherpa-onnx 认得落在非 ASCII 路径下的模型。只在原生构造那一瞬间生效。
/// </summary>
/// <remarks>
/// 病根：sherpa-onnx 1.13.4 在 Windows 上按窄字符查模型文件，路径含中文一律判
/// <c>does not exist</c>；而 .NET 走 UTF-16，说文件就在那儿。于是完整性校验全过、原生却建不出
/// 识别器——且它失败时不抛，只返回空句柄（见 <c>OfflineRecognizerEngine.EnsureNativeHandleUsable</c>）。
/// 解法：把进程的当前工作目录切到模型目录，只把纯 ASCII 的文件名递给原生。工作目录由内核持有，
/// 不经那层窄字符转换，于是中文目录照样打得开。
/// <para>
/// 四条被否掉的路，别再试（均实测）：① 8.3 短路径——中文目录的短名仍含中文（<c>中文目~1</c>）；
/// ② <c>setlocale(LC_ALL, "")</c> 到 936——locale 确实改了，sherpa 照样不认，它的原生 CRT 不与
/// 我们共享；③ 在纯 ASCII 处建目录联接——可行，但 .NET 的
/// <see cref="Directory.CreateSymbolicLink"/> 要开发者模式，改用 junction 就得自己 P/Invoke，
/// 还得管链接的失效与清理；④ 要求用户装到英文目录——中文路径在国内是常态，不能这么糊弄。
/// </para>
/// <para>
/// 代价说清楚：当前工作目录是进程级的一格状态，切换期间（Qwen3 约 3.2 秒）全进程可见。
/// 本项目所有路径都走 <see cref="AppContext.BaseDirectory"/>、不读工作目录；第三方库理论上可能读，
/// 故切换范围压到最小：只有路径真含非 ASCII 时才切，且必须与原生构造同处一个同步块内，
/// 切完立刻还原。
/// </para>
/// </remarks>
public sealed class NativeModelDirectoryScope : IDisposable
{
    /// <summary>
    /// 工作目录只有一格，同一时刻只能有一个 scope 攥着它。
    /// 用信号量而非 <c>lock</c>：进出可能不在同一线程上。
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly string? _previousWorkingDirectory;

    private int _disposed;

    private NativeModelDirectoryScope(string rootForNative, string? previousWorkingDirectory)
    {
        RootForNative = rootForNative;
        _previousWorkingDirectory = previousWorkingDirectory;
    }

    /// <summary>
    /// 递给原生加载器的模型根。纯 ASCII 路径下是模型目录的完整路径（与变通前逐字节一致）；
    /// 切了工作目录时是空串，此时按它拼出来的就是纯文件名。
    /// </summary>
    public string RootForNative { get; }

    /// <summary>本次是否真的动了工作目录。给日志与测试看。</summary>
    public bool ChangedWorkingDirectory => _previousWorkingDirectory is not null;

    /// <summary>
    /// 进入作用域。必须 <c>using</c>，且必须与原生构造同处一个同步块——跨 <c>await</c> 持有它，
    /// 等于把进程的工作目录交给调度器摆布。
    /// </summary>
    /// <param name="modelDir">配置里的模型目录，可以是相对路径。</param>
    public static NativeModelDirectoryScope Enter(string modelDir)
    {
        var full = ModelDirectory.Canonical(modelDir);

        // 纯 ASCII 就什么也不做。绝大多数装机走的是这条，不该为少数人的路径给所有人加一次
        // 全局状态切换。
        if (!ModelDirectory.ContainsNonAscii(full))
        {
            return new NativeModelDirectoryScope(full, previousWorkingDirectory: null);
        }

        Gate.Wait();

        try
        {
            var previous = Directory.GetCurrentDirectory();

            Directory.SetCurrentDirectory(full);

            return new NativeModelDirectoryScope(string.Empty, previous);
        }
        catch
        {
            // 切失败就别攥着闸门不放——下一次加载会永远等在这儿。
            // 变异验过：删掉这一句，测试不是变红而是当场挂死。
            Gate.Release();

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_previousWorkingDirectory is null)
        {
            return;
        }

        try
        {
            Directory.SetCurrentDirectory(_previousWorkingDirectory);
        }
        finally
        {
            // 还原失败也得放闸门：工作目录留在了别处已经够糟，再把后续所有加载堵死只会让排查更难。
            Gate.Release();
        }
    }
}
