using Microsoft.Extensions.Logging;
using Xunit;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 装饰器本身的契约。
///
/// 它现在是本层**唯一**的「记日志不许把线程带走」机制——上一轮那个靠调用点自觉的
/// <c>SafeLog.Attempt</c> 助手实测漏了 7 处，且漏的全在 catch 块之外（<c>?.Invoke</c> 之前、
/// 消息泵线程上、Timer 回调上）。机制既然收敛成一处，这一处就得单独钉住。
/// </summary>
public sealed class SafeLoggerTests
{
    [Fact]
    public void Log_WhenTheSinkThrows_DoesNotPropagate()
    {
        var sink = new RecordingLogger<SafeLoggerTests> { ThrowOnEveryEntry = true };
        var safe = ((ILogger)sink).Safe();

        safe.LogInformation("一条会让 sink 炸掉的日志");

        // 阳性对照：sink 确实被调用过。否则「没抛」也可能只是因为压根没往下传。
        Assert.Equal(1, sink.CountOf(LogLevel.Information));
    }

    [Fact]
    public void IsEnabled_WhenTheSinkThrows_AnswersTrueRatherThanSilencingEverything()
    {
        // 答 false 会把日志整个静默关掉——那是比抛出更难查的故障。
        var safe = ((ILogger)new ThrowingLogger()).Safe();

        Assert.True(safe.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void BeginScope_WhenTheSinkThrows_HandsBackNullRatherThanThrowing()
    {
        var safe = ((ILogger)new ThrowingLogger()).Safe();

        using var scope = safe.BeginScope("scope");

        Assert.Null(scope);
    }

    [Fact]
    public void ASwallowedEntry_LeavesATraceForDiagnosticsToFind()
    {
        // 装饰器把失败咽得很干净——它覆盖本程序集**每一次**日志调用，一个配错的文件 sink
        // 就等于「零日志 + 零提示 + 零异常」。这个读数是唯一的逃生口。
        var before = LoggingHealth.SwallowedEntryCount;
        var safe = ((ILogger)new RecordingLogger<SafeLoggerTests> { ThrowOnEveryEntry = true }).Safe();

        safe.LogWarning("会被咽掉的一条");

        Assert.True(LoggingHealth.SwallowedEntryCount > before,
            "日志被咽掉了却没留下任何痕迹，链路坏掉时谁也发现不了");
        Assert.NotNull(LoggingHealth.FirstFailure);
    }

    [Fact]
    public void Safe_IsIdempotent()
    {
        // 宿主传给 MessagePumpThread 的通常已经包过。包两层不算错，但每条日志多一层 try
        // 且调试时多一层栈——更要紧的是，「包没包过」这件事不该需要被追踪。
        var once = ((ILogger)new RecordingLogger<SafeLoggerTests>()).Safe();

        Assert.Same(once, once.Safe());
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => throw new InvalidOperationException("BeginScope 炸了");

        public bool IsEnabled(LogLevel logLevel) => throw new InvalidOperationException("IsEnabled 炸了");

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("Log 炸了");
    }
}
