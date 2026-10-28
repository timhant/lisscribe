using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lisscribe.Core.Audio;
using Lisscribe.Core.Session;

namespace Lisscribe.App.ViewModels;

/// <summary>
/// 悬浮条四态：录音中绿色音量条、识别中黄色不确定进度、转写中蓝色进度文字加不确定进度、失败红色。
/// </summary>
/// <remarks>
/// 本类没有线程亲和性，也刻意不去获取 Dispatcher——但这不等于随便哪条线程都能写它，
/// 契约见 <see cref="ApplyState"/>。
/// 它也不负责显示/隐藏窗口，也不负责失败态几秒后自动隐藏：<see cref="IsVisible"/> 只是
/// 「该不该显示」这个判断的结果，真正去 <c>Show()</c> / <c>Hide()</c> 的是 <c>OverlayWindow</c>，
/// 它自己订本类的 <c>PropertyChanged</c>；而失败态的自动隐藏需要一个计时器，
/// 计时器意味着「谁来 Dispose」，那笔账归组合根。
/// </remarks>
public sealed partial class OverlayViewModel : ObservableObject
{
    /// <summary>电平柱的根数。七根约合半秒历史，看起来是一小段波形而不是一根被切开的条。</summary>
    public const int BarCount = 7;

    /// <summary>静音时柱子仍留这么高（DIP），否则条会整段消失，看着像程序死了。</summary>
    public const double BarMinHeightDip = 2d;

    /// <summary>满刻度时的柱高（DIP）。</summary>
    public const double BarMaxHeightDip = 14d;

    /// <summary>
    /// 录音中与识别中那颗胶囊的宽（DIP），两态必须一模一样大。
    /// 写死一个宽度，不靠「两边内容碰巧一样宽」：文字实测两态差 2 DIP（汉字并非等宽，
    /// 字体回退一变就再差一点）。现在由构造保证：这一个数管住这两态，余量落进中间那一列。
    /// 转写态文字更长、右侧又挂着进度条，这个宽度放不下，另用 <see cref="TranscribingPillWidthDip"/>。
    /// </summary>
    public const double NormalPillWidthDip = 132d;

    /// <summary>
    /// 转写中那颗胶囊的宽（DIP），定值，进度跳格时胶囊不抖。
    /// 实测「转写中 100%」12 号字宽 68.23 DIP，加固定的 81 DIP（内边距 24、状态点 8、文字边距 16、进度条 33）约 150，再留 10 DIP 给字体回退。
    /// </summary>
    public const double TranscribingPillWidthDip = 160d;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarBrush))]
    [NotifyPropertyChangedFor(nameof(IsVolumeVisible))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(PillWidthDip))]
    [NotifyPropertyChangedFor(nameof(IsCountingDown))]
    private SessionState _state = SessionState.Idle;

    /// <summary>
    /// 七根柱子的高度（DIP），下标越大越新，最新的一格在最右。
    /// 用定长数组 + 七个属性而不是集合：绑定 <c>ObservableCollection</c> 的话，从非 UI 线程写
    /// <see cref="Volume"/> 就会变成一次跨线程的集合变更，而「单写属性不抛」这条契约不能因为
    /// 换了个容器就悄悄作废。
    /// </summary>
    private readonly double[] _barHeights = CreateEmptyStrip();

    /// <summary>
    /// 0..1 的 RMS 音量（<c>RmsMeter.Compute</c> 已经夹好，本类不再夹一遍）。
    /// 单独写这个属性不抛：绑在一个已加载的控件上、从线程池线程连写 500 次都不抛。
    /// 会抛的是翻转 <see cref="IsVisible"/> 那一下，见 <see cref="ApplyState"/>；
    /// <c>SessionController.VolumeChanged</c>（跑在采集线程上）只写本属性、不动
    /// <see cref="IsVisible"/>，故它撞到的是一个没人拦、也不会报的数据竞争。
    /// </summary>
    [ObservableProperty]
    private double _volume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _message = string.Empty;

    /// <summary>
    /// 离录音上限还剩几秒，<c>null</c> 即还早。由 <c>SessionController.RecordingCountdown</c>
    /// 报进来，只在最后几秒里有值。上限到点自有兜底，但那是事后；Qwen3 档只能录 25 秒，
    /// 真撞上去而界面上一个字都没有的话，用户看到的是「话说了一半没了」。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(BarBrush))]
    [NotifyPropertyChangedFor(nameof(IsCountingDown))]
    private int? _remainingSeconds;

    [ObservableProperty]
    private bool _isVisible;

    public double Bar0Height => _barHeights[0];

    public double Bar1Height => _barHeights[1];

    public double Bar2Height => _barHeights[2];

    public double Bar3Height => _barHeights[3];

    public double Bar4Height => _barHeights[4];

    public double Bar5Height => _barHeights[5];

    public double Bar6Height => _barHeights[6];

    /// <summary>
    /// 胶囊该有多宽（DIP）。转写中是 <see cref="TranscribingPillWidthDip"/>，其余可见态是
    /// <see cref="NormalPillWidthDip"/>，都是定值；失败态返回
    /// <c>NaN</c>——那是 Avalonia 里「自适应」的写法，让那句提示自己撑开。
    /// 改的是窗口里面那个 <c>Border</c>，不是窗口本身：窗口尺寸一动就要跟系统的最小窗口尺寸
    /// 打交道，还要重排落点。
    /// </summary>
    public double PillWidthDip => State switch
    {
        SessionState.Failed => double.NaN,
        SessionState.Transcribing => TranscribingPillWidthDip,
        _ => NormalPillWidthDip,
    };

    /// <summary>
    /// 胶囊上显示的那行字。有真消息就用真消息，没有才用套话——失败态那句是唯一带排障线索的
    /// （引擎加载失败的原因、没有录音设备……），被套话顶掉就等于把线索扔了。
    /// 套话落在这里而不是 <c>SessionController</c>：状态机是 Core 的东西，界面文案塞进去
    /// 就等于让它认识「用户看到什么」。
    /// </summary>
    public string DisplayText => Message.Length > 0
        ? Message
        : IsCountingDown
        ? $"还剩 {RemainingSeconds} 秒"
        : State switch
        {
            // 「识别中」不带省略号：与「录音中」同为三个汉字，加一个省略号就多一个字符的宽度。
            SessionState.Recording => "录音中",
            SessionState.Recognizing => "识别中",

            // 百分比由状态机写在 Message 里，这句只在它缺席时兜底。
            SessionState.Transcribing => "转写中",
            SessionState.Failed => "识别失败",
            _ => string.Empty,
        };

    /// <summary>
    /// 正在倒数：录音态、且剩余秒数有值。
    /// 在 <see cref="DisplayText"/> 里排在真消息之后、套话之前：真消息带的是别处拿不到的线索，
    /// 倒数则每秒都会再说一遍，晚一秒不损失什么。
    /// </summary>
    public bool IsCountingDown => State == SessionState.Recording && RemainingSeconds is not null;

    /// <summary>
    /// 四色。挑的是「绿 / 黄 / 蓝 / 红」这件事，具体色号可换。
    /// 倒数时用第五色（深橙）：它不是又一个状态，是录音态的子态；挑色的唯一约束是与另外几色都
    /// 分得开——与识别态的金色撞色的话，余光扫过去像是「已经在识别了，可以松手」。
    /// <para>
    /// 转写用蓝：绿的意思是「麦克风开着」，而转写不碰麦克风；冷色离金、深橙、橙红那一簇暖色最远，
    /// 读起来是「后台在干活」而不是报警，红绿色弱也分得开蓝与另外几色。
    /// DodgerBlue 在胶囊的 #1E1E1E 底上对比度约 5:1，8 DIP 的状态点看得清。
    /// </para>
    /// </summary>
    public IBrush BarBrush => IsCountingDown
        ? Brushes.DarkOrange
        : State switch
    {
        SessionState.Recording => Brushes.LimeGreen,
        SessionState.Recognizing => Brushes.Gold,
        SessionState.Transcribing => Brushes.DodgerBlue,
        SessionState.Failed => Brushes.OrangeRed,
        _ => Brushes.Transparent,
    };

    public bool IsVolumeVisible => State == SessionState.Recording;

    /// <summary>识别中与转写中显示。转写的百分比要识别完一段才跳一格，可能十来秒不动，靠这条表明程序还在工作。</summary>
    public bool IsIndeterminate => State is SessionState.Recognizing or SessionState.Transcribing;

    /// <summary>
    /// Idle 与 Paused 时隐藏；其余四态显示。必须在 UI 线程上调。
    /// </summary>
    /// <remarks>
    /// 不守这条的后果，只有一条在两种接线下都成立：每一轮会话的收尾都抛。
    /// 机理是 <c>OverlayWindow</c> 订本类的 <c>PropertyChanged</c>、在处理器里同步调
    /// <c>Show()</c> / <c>Hide()</c>，而生成的 setter 是在写入的那条线程上同步发通知的。
    /// 分界是「这一次调用有没有越过它自己的提前返回哨兵」，而哨兵盯的是窗口内部的已显示状态
    /// ——「有没有真的改变可见性」这条只在一个未曾抛过的窗口上与它等价，半途抛出会让那个内部
    /// 状态与实际可见性脱钩。抛出点也不是同一道检查（<c>Visual.set_IsVisible</c>、
    /// <c>Compositor.RegisterForSerialization</c>、<c>WindowBase.set_Owner</c> 都见过）。
    /// <para>
    /// 用户看见的症状不像「线程用错了」：窗口启动时从未显示过的那条序列是「指示器从来不出现」，
    /// 启动时已显示的那条是「一直亮着不消失」。抛出之后的现场两条都要记：本类与窗口的可见性
    /// 从此不一致，而本方法末尾那句 <c>Volume = 0</c> 一行也没跑到（异常在
    /// <see cref="IsVisible"/> 的 setter 处抛出，早于它）；异常还会原样抛回引发状态变更的那条线程。
    /// </para>
    /// <para>
    /// 故组合根必须 <c>Dispatcher.UIThread.Post</c>，且不许用 <c>Invoke</c>——<c>Invoke</c>
    /// 同步等 UI 线程，而收尾路径要 Join 采集线程，那就是死锁。
    /// 本类不自己 Post，是因为「哪条线程、要不要节流、UI 关掉之后还要不要投递」全是组合根才
    /// 知道的事；把 Dispatcher 埋进 ViewModel 只会让它离了 Avalonia 就测不了。
    /// </para>
    /// </remarks>
    public void ApplyState(SessionState state, string? message)
    {
        State = state;
        Message = message ?? string.Empty;
        IsVisible = state is SessionState.Recording or SessionState.Recognizing
            or SessionState.Transcribing or SessionState.Failed;

        // 不清零的话，最后一格音量会冻在识别中那条黄条上，看起来像录音没停。
        if (state != SessionState.Recording)
        {
            Volume = 0;

            // 倒数同理，且还多一层：不清的话下一轮录音会从上一轮的余数接着显示。
            RemainingSeconds = null;

            // 柱子要整条抹平，光把 Volume 归零不够：Volume 只推进最右那一格，
            // 前六格仍挂着上一句话的波形，识别态那半秒看着像还在录。
            ClearStrip();
        }
    }

    /// <summary>
    /// 每来一包音量就把整条向左推一格，最新的落在最右。
    /// 喂进来的是线性 RMS，画出去的必须是对数刻度（见 <see cref="VolumeScale.ToBarLevel"/>）：
    /// 语音的 RMS 落在 0.02–0.2，直接当高度用就是一根「大声说话也只涨到两成」的条。
    /// </summary>
    partial void OnVolumeChanged(double value)
    {
        var level = VolumeScale.ToBarLevel(value);

        Array.Copy(_barHeights, 1, _barHeights, 0, BarCount - 1);
        _barHeights[BarCount - 1] = BarMinHeightDip + (level * (BarMaxHeightDip - BarMinHeightDip));

        NotifyStripChanged();
    }

    private void ClearStrip()
    {
        Array.Fill(_barHeights, BarMinHeightDip);

        NotifyStripChanged();
    }

    /// <summary>
    /// 七格逐个发通知。漏发哪一格那根柱子就冻住，而颜色、文字、可见性全对，看起来只是
    /// 「波形有点怪」——所以这里宁可写死循环也不手抄七行。
    /// </summary>
    private void NotifyStripChanged()
    {
        for (var i = 0; i < BarCount; i++)
        {
            OnPropertyChanged($"Bar{i}Height");
        }
    }

    private static double[] CreateEmptyStrip()
    {
        var heights = new double[BarCount];
        Array.Fill(heights, BarMinHeightDip);

        return heights;
    }
}
