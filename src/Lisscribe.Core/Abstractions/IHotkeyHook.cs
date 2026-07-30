using Lisscribe.Core.Hotkey;

namespace Lisscribe.Core.Abstractions;

public interface IHotkeyHook : IDisposable
{
    /// <summary>组合成立的瞬间(主键按下且修饰键精确匹配)。</summary>
    event EventHandler? Pressed;

    /// <summary>主键松开,携带按住时长。与修饰键是否仍按下无关。</summary>
    event EventHandler<TimeSpan>? Released;

    /// <summary>存活自检发现钩子已被系统静默摘除(回调超时所致)。</summary>
    event EventHandler? HookLost;

    /// <summary>供看门狗轮询：主键是否仍被按住。</summary>
    /// <remarks>
    /// 实现方须自行跟踪按住态，不能拿 <c>GetAsyncKeyState</c> 当真相来源：低层钩子吞掉的事件
    /// 不更新 win32k 的异步键状态表，而热键成立时主键的 KEYDOWN 正是被吞掉的那一个。照它实现，
    /// 本属性整个按住期间恒为 false，看门狗第一次巡检就判「KEYUP 丢失」强行收尾，而那时
    /// held &lt; HoldThreshold——每一次录音都被当成误触丢弃。该函数只对被放行的键（修饰键就是）
    /// 有意义，可作佐证不可作依据。
    /// <para>
    /// 调用方须知道：看门狗的「KEYUP 丢失」分支对被吞的热键已经失效。标志只在我们自己看见
    /// KEYUP 时才清位，而 KEYUP 真丢失的场景（锁屏 / UAC 切 secure desktop / 钩子被摘除）恰恰
    /// 是看不见它，本属性在那种场景下会一直返回 true。兜底只剩
    /// <c>SessionOptions.MaxRecordingSeconds</c>，锁屏与休眠另由 <see cref="ISessionMonitor"/> 覆盖。
    /// </para>
    /// <para>
    /// 实现方须在「检出钩子曾被摘除」与「会话被打断」（锁屏 / 休眠 / 会话切换）时复位该标志，
    /// 否则用户下一次热键被静默吞掉；但<b>不要在前台窗口变化时复位</b>——后者不蕴含「这次按住
    /// 已经结束」，按住期间复位会让 Released 根本不来、本属性立刻翻假、看门狗下一跳强行收尾，
    /// 而前台窗口变化随时可能由悬浮条、托盘气泡、通知、IME 候选窗引起。
    /// </para>
    /// </remarks>
    bool IsMainKeyPhysicallyDown { get; }

    /// <summary>放行态。置 true 后钩子仍装着，但热键原样交给别的窗口。</summary>
    /// <remarks>
    /// 暂停必须置它，只让状态机不理会 <see cref="Pressed"/> 是不够的：钩子吞掉的键不到任何进程，
    /// 于是暂停期间别的窗口一样收不到——配置窗录新热键时按当前热键，症状正是「按了毫无反应」。
    /// 未 <see cref="Start"/> 时也可置位，值留到下次 Start 生效，免得调用方自己记「现在装没装」。
    /// </remarks>
    bool PassThrough { get; set; }

    void Start(HotkeyCombination combination);

    void Stop();
}
