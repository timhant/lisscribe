using Microsoft.Extensions.Logging;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 记下写过哪些日志。只给「回调里的异常必须被咽下并记账」那两条测试用。
///
/// **它是被测代码自己记的账，不能当作「进程没崩」的证据**，理由写在用它的测试里：
/// 回调里逃逸的异常在普通宿主上会终结进程，而在 xunit 的测试宿主上进程活了下来——
/// 于是「进程还在」这个本该最有力的预言机，在测试里恰好取不到样。
/// 退而求其次，钉住 catch 块必须留下的那条 Error 日志。
///
/// **退出码分两族，别混**：Win32 反向调进来的回调（<c>WH_KEYBOARD_LL</c>、WinEvent）那条路是
/// <c>0xC000041D</c>（STATUS_FATAL_USER_CALLBACK_EXCEPTION）；纯托管线程上逃逸的异常
/// （NAudio 采集线程的 <c>RecordingStopped</c>、线程池）是 <c>-532462766 / 0xE0434352</c>
/// （CLR 未处理异常）。
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message, Exception? Error)> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// 置真之后每次记日志都抛。用来验「记日志本身失败时，这条线程不会被它带走」——
    /// 那正是 <c>Lisscribe.Platform.SafeLogger</c> 存在的理由：磁盘满、文件句柄没了、
    /// sink 自己抛，都是真会发生的事。
    /// </summary>
    public bool ThrowOnEveryEntry { get; init; }

    /// <summary>
    /// 只在**某一句**日志上抛。
    ///
    /// **验「记日志失败会怎样」必须用这种 sink，不能用无条件抛的**：
    /// 无条件抛会在更早的地方先炸，把要验的那条链当场打断，看到的失败便不是要验的那个。
    /// 例如要验「挡在 Invoke 前面的那句 LogInformation 抛出会吃掉事件」，
    /// 就只能让它在 <c>会话中断</c> 那一句上抛，别的日志照常。
    /// </summary>
    public string? ThrowOnMessageContaining { get; init; }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);

        lock (_gate)
        {
            _entries.Add((logLevel, message, exception));
        }

        // 先记下再抛：调用方要能证明「它确实试过记这一条」，而不只是「它抛了」。
        if (ThrowOnEveryEntry
            || (ThrowOnMessageContaining is { } needle && message.Contains(needle, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("日志 sink 自己炸了");
        }
    }

    public int CountOf(LogLevel level)
    {
        lock (_gate)
        {
            return _entries.Count(e => e.Level == level);
        }
    }

    /// <summary>某个具体异常被记下过几次。按异常对象比对，不按消息文本——
    /// 消息文本是生产代码写的，拿它当判据等于让被测方自己出题。</summary>
    public int TimesRecorded(Exception error)
    {
        lock (_gate)
        {
            return _entries.Count(e => ReferenceEquals(e.Error, error));
        }
    }

    public string Describe()
    {
        lock (_gate)
        {
            return $"[{string.Join("; ", _entries.Select(e => $"{e.Level}:{e.Message}"))}]";
        }
    }
}
