using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Hotkey;

namespace Lisscribe.Core.Tests.Fakes;

public sealed class FakeHotkeyHook : IHotkeyHook
{
    public event EventHandler? Pressed;
    public event EventHandler<TimeSpan>? Released;
    public event EventHandler? HookLost;

    public bool IsMainKeyPhysicallyDown { get; set; }
    public bool IsStarted { get; private set; }
    public HotkeyCombination? StartedWith { get; private set; }

    /// <summary>
    /// 放行态。真钩子把它转给 <c>HotkeyMatcher</c>，本假件只记着——「吞还是放」那一段
    /// 由 <c>HotkeyMatcherTests</c> 直接测，这里只回答「状态机有没有去拨这个开关」。
    /// </summary>
    public bool PassThrough { get; set; }

    public void Start(HotkeyCombination combination)
    {
        IsStarted = true;
        StartedWith = combination;
    }

    public void Stop() => IsStarted = false;

    public void RaisePressed()
    {
        IsMainKeyPhysicallyDown = true;
        Pressed?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseReleased(TimeSpan heldFor)
    {
        IsMainKeyPhysicallyDown = false;
        Released?.Invoke(this, heldFor);
    }

    /// <summary>模拟「KEYUP 丢失」:物理已松开,但 Released 事件永不到达。</summary>
    public void SimulateLostKeyUp() => IsMainKeyPhysicallyDown = false;

    public void RaiseHookLost() => HookLost?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Stop();
}
