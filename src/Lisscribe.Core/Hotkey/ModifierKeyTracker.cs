namespace Lisscribe.Core.Hotkey;

/// <summary>
/// 修饰键按下状态表。
///
/// 为何需要它：MSDN LowLevelKeyboardProc 明载「the callback function is called before the
/// asynchronous state of the key is updated」，故钩子回调内调 GetAsyncKeyState 得到的是过期值；
/// GetKeyState 更不可用（线程局部输入状态，钩子线程无输入队列关联）。只能自行累积。
///
/// 该表必然会失同步（KEYUP 在锁屏 / UAC / secure desktop / RDP 断开时不会送达），
/// 故 <see cref="Resync"/> 是必备出口，由调用方在回调之外用 GetAsyncKeyState 取真实状态后调用。
/// </summary>
public sealed class ModifierKeyTracker
{
    private ModifierKeys _current = ModifierKeys.None;
    private readonly Lock _gate = new();

    public ModifierKeys Current
    {
        get { lock (_gate) { return _current; } }
    }

    public void OnKeyDown(ModifierKeys key)
    {
        lock (_gate) { _current |= key; }
    }

    public void OnKeyUp(ModifierKeys key)
    {
        lock (_gate) { _current &= ~key; }
    }

    /// <summary>用回调之外取得的物理状态整表覆盖。</summary>
    public void Resync(ModifierKeys physicalState)
    {
        lock (_gate) { _current = physicalState; }
    }

    public void Reset() => Resync(ModifierKeys.None);
}
