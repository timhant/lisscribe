namespace Lisscribe.Core.Hotkey;

/// <summary>钩子回调对单个按键事件的处置。</summary>
public enum KeyAction
{
    /// <summary>放行给系统（钩子回调返回 CallNextHookEx 的结果）。</summary>
    PassThrough,

    /// <summary>吞掉，且这是组合成立的瞬间——触发 Pressed。</summary>
    SuppressAndPress,

    /// <summary>吞掉，且这是主键松开——触发 Released。</summary>
    SuppressAndRelease,

    /// <summary>吞掉，但不产生事件（按住不放时的重复 KEYDOWN）。</summary>
    Suppress,
}

/// <summary>
/// 「这次按键是否构成热键」的判定。纯逻辑，无平台依赖，故可完整单测。
/// 规矩两条：拦截主键、放行修饰键，短按不补发；Released 在主键松开时触发，与修饰键是否仍按下无关。
/// </summary>
public sealed class HotkeyMatcher(HotkeyCombination combination)
{
    private static readonly string[] ModifierNames = ["Ctrl", "Alt", "Shift", "Win"];

    private readonly HotkeyCombination _combination = combination;
    private bool _mainKeyHeld;

    // Reset() 发生在「已吞下 KEYDOWN、尚未收到 KEYUP」之后时须记住这一点：那次 KEYDOWN 从未放行
    // 给系统，之后真正到来的 KEYUP 也必须被吞掉，否则目标应用会收到一个没有配对 KEYDOWN 的 KEYUP。
    // 干净状态下的 Reset() 不得凭空置位此标志——那样会让「从未匹配」的 KEYUP 也被误吞。
    //
    // 该标志只能被置位、不能被 Reset() 清空：它记录的是「还有一次待吞的 release 尚未兑现」，
    // 只有真正消费掉它的 KEYUP 分支、或开启全新会话的按下分支才有资格清除。若 Reset() 在
    // _mainKeyHeld 已为 false 时也去覆写它，背靠背的两次 Reset()（前台窗口变化与会话变化在锁屏时
    // 几乎总是同时发生）就会把第一次置位的标志冲回 false。
    private bool _releaseSuppressedByReset;

    private volatile bool _passThrough;

    /// <summary>
    /// 放行态：热键暂停期间置位，主键一律交还给别的窗口。
    /// </summary>
    /// <remarks>
    /// 为什么暂停不能只改状态机：钩子吞掉的键不到任何进程，所以「状态机不理会 Pressed」跟
    /// 「键还给系统」是两回事——前者只是自己不动作，键仍在钩子里被吞掉。配置窗录新热键时按下
    /// 当前热键，症状就是界面毫无反应：不是录错，是一个按键事件都收不到。
    /// 为什么落在这一层而不是去摘钩子：<c>SessionController.Pause</c> 持着 <c>_gate</c> 调用本路径，
    /// 而 <c>KeyboardHook.Stop</c> 要 Join 事件派发线程，那条线程正是回过头来抢同一把 <c>_gate</c>
    /// 的人。改一个标志则无锁、无 Join、也没有「重装钩子可能失败」这个新的失败模式。
    /// 写在这一层还有一个好处：判定是纯逻辑，可完整单测；若写进钩子回调，就只能靠装真钩子的
    /// 桌面用例去碰。
    /// <c>volatile</c>：置位的是调用方那条线程，读它的是钩子回调所在的泵线程。
    /// </remarks>
    public bool PassThrough
    {
        get => _passThrough;
        set => _passThrough = value;
    }

    public void Reset()
    {
        if (_mainKeyHeld)
        {
            _releaseSuppressedByReset = true;
        }

        _mainKeyHeld = false;
    }

    public KeyAction Evaluate(string keyName, bool isKeyDown, ModifierKeys activeModifiers)
    {
        // 修饰键一律放行。吞掉它们会让用户按 Ctrl 时所有应用失灵。
        if (ModifierNames.Contains(keyName, StringComparer.OrdinalIgnoreCase))
        {
            return KeyAction.PassThrough;
        }

        if (!string.Equals(keyName, _combination.MainKey, StringComparison.OrdinalIgnoreCase))
        {
            return KeyAction.PassThrough;
        }

        if (_passThrough)
        {
            // 暂停期间一律放行，唯独「KEYDOWN 已吞下、KEYUP 还没来」那一次必须补吞完：那次按下
            // 从未交给系统，此刻放行它的松开就是给目标应用一个没有配对 KEYDOWN 的 KEYUP。
            // 这一条与下面 _releaseSuppressedByReset 守的是同一件事，只是触发时机不同。
            if (!isKeyDown && (_mainKeyHeld || _releaseSuppressedByReset))
            {
                _mainKeyHeld = false;
                _releaseSuppressedByReset = false;

                return KeyAction.Suppress;
            }

            // 放行的按下不置 _mainKeyHeld：它记的是「这次按下被我们吞了」。置了的话，解除暂停后
            // 那次配对的 KEYUP 会被上面这段当成欠账再吞一次。
            return KeyAction.PassThrough;
        }

        if (isKeyDown)
        {
            if (_mainKeyHeld)
            {
                // 按住不放的重复投递：继续吞，但不重复触发 Pressed。
                return KeyAction.Suppress;
            }

            // 修饰键须精确相等：多按或少按都不算这个热键。
            if (activeModifiers != _combination.Modifiers)
            {
                return KeyAction.PassThrough;
            }

            // 新一轮按下开始了合法会话，此前遗留的「待吞 release」标志不再适用。
            _releaseSuppressedByReset = false;
            _mainKeyHeld = true;

            return KeyAction.SuppressAndPress;
        }

        if (_releaseSuppressedByReset)
        {
            _releaseSuppressedByReset = false;

            return KeyAction.Suppress;
        }

        if (!_mainKeyHeld)
        {
            // 这次按下当初已放行，其松开也必须放行，否则目标应用收到孤立的 KEYDOWN。
            return KeyAction.PassThrough;
        }

        _mainKeyHeld = false;

        return KeyAction.SuppressAndRelease;
    }
}
