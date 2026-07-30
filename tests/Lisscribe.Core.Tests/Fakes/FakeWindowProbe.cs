using Lisscribe.Core.Abstractions;

namespace Lisscribe.Core.Tests.Fakes;

public sealed class FakeWindowProbe : IWindowProbe
{
    public nint ForegroundWindow { get; set; } = 1000;

    /// <summary>加入此集合的窗口句柄会被判为不可注入(模拟提权窗口)。</summary>
    public HashSet<nint> ElevatedWindows { get; } = [];

    /// <summary>置非 null 则 GetForegroundWindow 抛出它,用于验证调用方对探针异常的容错——
    /// 生产环境里这是原生 GetForegroundWindow 的 P/Invoke 包装,可能因句柄失效等原因抛出。</summary>
    public Exception? ThrowOnGetForegroundWindow { get; set; }

    public nint GetForegroundWindow()
    {
        if (ThrowOnGetForegroundWindow is not null)
        {
            throw ThrowOnGetForegroundWindow;
        }

        return ForegroundWindow;
    }

    public bool CanInjectInto(nint window) => !ElevatedWindows.Contains(window);
}
