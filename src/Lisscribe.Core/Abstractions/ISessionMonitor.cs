namespace Lisscribe.Core.Abstractions;

/// <summary>
/// 那些会让 KEYUP 永远送不到的系统事件。没有它,状态机会永久卡死在 Recording:
/// 麦克风一直录、悬浮条一直绿、内存一直涨,且用户按任何键都恢复不了。
/// </summary>
public interface ISessionMonitor : IDisposable
{
    /// <summary>锁屏 / 切 secure desktop(Win+L、UAC 提权框、Ctrl+Alt+Del)。</summary>
    event EventHandler? SessionLocked;

    event EventHandler? SystemSuspending;

    /// <summary>前台窗口变化——触发修饰键状态表重同步。</summary>
    event EventHandler? ForegroundWindowChanged;

    void Start();
}
