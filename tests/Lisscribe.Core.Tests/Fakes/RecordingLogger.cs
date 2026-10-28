using Microsoft.Extensions.Logging;

namespace Lisscribe.Core.Tests.Fakes;

/// <summary>
/// 记下写过哪些日志，供「原异常进了日志」「日志里没有识别出的文字」这类断言用。
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message, Exception? Error)> _entries = [];

    private readonly Lock _gate = new();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Error)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    public int CountOf(LogLevel level) => Entries.Count(e => e.Level == level);

    /// <summary>某个异常对象被记下过几次。按对象比对，不按消息文本，免得让被测方自己出题。</summary>
    public int TimesRecorded(Exception error) => Entries.Count(e => ReferenceEquals(e.Error, error));
}
