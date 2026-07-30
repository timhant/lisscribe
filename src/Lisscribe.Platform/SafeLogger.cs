using Microsoft.Extensions.Logging;

namespace Lisscribe.Platform;

/// <summary>
/// 把一个 <see cref="ILogger"/> 包成「记日志这件事本身绝不会抛」的样子。
/// </summary>
/// <remarks>
/// 为什么是装饰器，不是在每个 catch 里包一层：后者要求每个调用点都有人记得调，而危险的那几行
/// 恰恰不在 catch 块里——两处写在 <c>?.Invoke</c> 之前（sink 一抛，事件被静默吃掉，锁屏后
/// <c>ForgetHeldMainKey</c> 不跑，用户下一次热键被吞），三处在消息泵线程与 <c>Timer</c> 回调上
/// （那里逃逸的异常是进程当场终结）。装饰器把纪律换成了类型：构造时包一次，本程序集里所有
/// <c>_logger.*</c> 调用点一次性变安全。
/// <para>
/// 三个成员都要包，不只是 <see cref="Log"/>：<see cref="IsEnabled"/> 与 <see cref="BeginScope"/>
/// 同样是外部代码，同样会抛。<see cref="IsEnabled"/> 出错时答 <c>true</c>（宁可试着记、让
/// <see cref="Log"/> 那层去兜），答 <c>false</c> 会把日志整个静默关掉。
/// 代价是每条日志多一次虚调用与一个 try；本层的日志都在冷路径上（启动、停止、出错），
/// 钩子回调那条热路径正常情况下一条日志也不写。
/// </para>
/// </remarks>
internal sealed class SafeLogger(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        try
        {
            return inner.BeginScope(state);
        }
        catch
        {
            // 拿不到 scope 不是取消这次调用的理由：调用方写的是 using，null 它处理得了。
            return null;
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        try
        {
            return inner.IsEnabled(logLevel);
        }
        catch
        {
            return true;
        }
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        try
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
        catch (Exception failure)
        {
            // 连日志都写不出去了（磁盘满、句柄没了、sink 自己抛）。不允许再抛：调用点可能在钩子
            // 回调、消息泵线程或 Timer 回调上，那些地方逃逸的异常是进程终结，而代价本该只是少记
            // 一条日志。
            // 但也不能就这么彻底静默——这一层覆盖本程序集的每一次日志调用，一个配错的文件 sink
            // 就等于「零日志 + 零提示 + 零异常」，谁也发现不了。故留一个逃生口：只记数与第一个原因。
            LoggingHealth.NoteSwallowed(failure);
        }
    }
}

/// <summary>
/// 「记日志这件事失败过多少次」的一个进程级读数。
/// 存在的唯一理由是 <see cref="SafeLogger"/> 把失败咽得太干净：它覆盖本程序集每一次日志调用，
/// 没有这个读数，日志链路整个坏掉时用户和我们都拿不到任何信号。组合根应当在启动自检里读一次。
/// 本身绝不抛：只有 <see cref="Interlocked"/> 操作，没有格式化、没有 I/O。
/// </summary>
public static class LoggingHealth
{
    private static long s_swallowed;
    private static Exception? s_firstFailure;

    /// <summary>被咽掉的日志条数。非零即表示日志链路有问题。</summary>
    public static long SwallowedEntryCount => Interlocked.Read(ref s_swallowed);

    /// <summary>第一次失败的原因。留第一个而不是最后一个：根因通常在最早那次。</summary>
    public static Exception? FirstFailure => Volatile.Read(ref s_firstFailure);

    internal static void NoteSwallowed(Exception failure)
    {
        Interlocked.Increment(ref s_swallowed);
        Interlocked.CompareExchange(ref s_firstFailure, failure, null);
    }
}

internal static class SafeLoggerExtensions
{
    /// <summary>
    /// 包一层，已经包过就原样返回。本程序集里凡是要长期持有的 <see cref="ILogger"/>，都在构造函数
    /// 里过一道这个。幂等是为了让「谁传进来的已经安全了」这件事不必被追踪。
    /// </summary>
    public static ILogger Safe(this ILogger inner) => inner as SafeLogger ?? new SafeLogger(inner);
}
