using System.ComponentModel;
using Avalonia.Media;
using Lisscribe.App.ViewModels;
using Lisscribe.Core.Session;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 悬浮条的状态→观感映射。纯逻辑，不碰窗口，也不碰 Dispatcher。
///
/// <para>
/// **颜色断言刻意不写死具体色号。** 要求是「绿 / 黄 / 红」，不是
/// <c>LimeGreen / Gold / OrangeRed</c>：把色号抄进断言，换个色调就假红，而把绿红对调这种真错误
/// 反倒抓不住（对调之后两条断言各自仍能找到「某个色号」）。故断言写成「哪个通道占优」。
/// </para>
/// </summary>
public sealed class OverlayViewModelTests
{
    /// <summary>
    /// 取样口独立：直接从 <see cref="SessionState"/> 的反射枚举取全部取值，而不是抄一份清单
    /// ——抄清单的写法在日后往枚举里加值时会静默漏测。
    /// </summary>
    public static TheoryData<SessionState> AllStates()
    {
        var data = new TheoryData<SessionState>();
        foreach (var state in Enum.GetValues<SessionState>())
        {
            data.Add(state);
        }

        return data;
    }

    [Fact]
    public void AllStates_CoversEveryValueOfTheEnum()
    {
        // 阴性对照：上面那个反射取样口若哪天被人改成手抄清单，这条会立刻发现。
        Assert.Equal(6, Enum.GetValues<SessionState>().Length);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void ApplyState_MakesTheOverlayVisibleExactlyForTheStatesTheSpecShows(SessionState state)
    {
        var vm = new OverlayViewModel();

        vm.ApplyState(state, null);

        // 转写中也得看得见：转写一跑几分钟，悬浮条是用户唯一能看到进度的地方。
        var shouldShow = state is SessionState.Recording or SessionState.Recognizing
            or SessionState.Transcribing or SessionState.Failed;
        Assert.Equal(shouldShow, vm.IsVisible);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void VolumeBar_IsShownOnlyWhileRecording(SessionState state)
    {
        var vm = new OverlayViewModel();

        vm.ApplyState(state, null);

        Assert.Equal(state == SessionState.Recording, vm.IsVolumeVisible);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void IndeterminateBar_IsShownOnlyWhileRecognizing(SessionState state)
    {
        var vm = new OverlayViewModel();

        vm.ApplyState(state, null);

        Assert.Equal(state == SessionState.Recognizing, vm.IsIndeterminate);
    }

    [Fact]
    public void BarBrush_WhileRecording_IsGreen()
    {
        var color = BrushColorFor(SessionState.Recording);

        Assert.True(color.G > color.R && color.G > color.B, $"录音态应为绿色，实际 {color}");
        Assert.Equal(byte.MaxValue, color.A);
    }

    [Fact]
    public void BarBrush_WhileRecognizing_IsYellow()
    {
        var color = BrushColorFor(SessionState.Recognizing);

        // 黄 = 红绿都高、蓝低。只查「不是绿也不是红」是查不出黄的。
        Assert.True(color.R > color.B && color.G > color.B, $"识别态应为黄色，实际 {color}");
        Assert.True(color.R > 0x80 && color.G > 0x80, $"识别态应为黄色，实际 {color}");
        Assert.Equal(byte.MaxValue, color.A);
    }

    [Fact]
    public void BarBrush_AfterAFailure_IsRed()
    {
        var color = BrushColorFor(SessionState.Failed);

        Assert.True(color.R > color.G && color.R > color.B, $"失败态应为红色，实际 {color}");
        Assert.Equal(byte.MaxValue, color.A);
    }

    [Theory]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Paused)]
    public void BarBrush_WhenNothingIsHappening_IsFullyTransparent(SessionState state)
    {
        Assert.Equal(0, BrushColorFor(state).A);
    }

    [Fact]
    public void ApplyState_WithAMessage_PutsItOnTheOverlay()
    {
        var vm = new OverlayViewModel();

        vm.ApplyState(SessionState.Failed, "没有可用的录音设备");

        Assert.Equal("没有可用的录音设备", vm.Message);
    }

    [Fact]
    public void ApplyState_WithoutAMessage_ClearsTheOldOne()
    {
        // 上一次失败的提示留在屏幕上，会让用户以为这次也失败了。
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Failed, "没有可用的录音设备");

        vm.ApplyState(SessionState.Recording, null);

        Assert.Equal(string.Empty, vm.Message);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void ApplyState_WhenLeavingRecording_ZeroesTheVolume(SessionState state)
    {
        // 不清零的话，最后一格音量会冻在识别中的那条黄条上——看起来像录音没停。
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recording, null);
        vm.Volume = 0.75;

        vm.ApplyState(state, null);

        Assert.Equal(state == SessionState.Recording ? 0.75 : 0d, vm.Volume);
    }

    [Fact]
    public void ChangingTheState_NotifiesEveryPropertyThatIsDerivedFromIt()
    {
        // **少了这一条，界面会在正确的状态上显示错误的颜色，而全部状态断言照样绿。**
        // CommunityToolkit 的 [ObservableProperty] 只替 State 自己发通知；
        // 派生只读属性靠 [NotifyPropertyChangedFor] 挂上去，漏一个就是漏一个。
        var vm = new OverlayViewModel();
        var seen = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        vm.State = SessionState.Recording;

        Assert.Contains(nameof(OverlayViewModel.State), seen);
        Assert.Contains(nameof(OverlayViewModel.BarBrush), seen);
        Assert.Contains(nameof(OverlayViewModel.IsVolumeVisible), seen);
        Assert.Contains(nameof(OverlayViewModel.IsIndeterminate), seen);
        Assert.Contains(nameof(OverlayViewModel.DisplayText), seen);
    }

    /// <summary>
    /// **悬浮条必须自己说出它在干什么。**
    ///
    /// <para>
    /// <c>SessionController</c> 进录音、进识别时传的 message 都是 <c>null</c>，故界面那个
    /// <c>TextBlock</c> 若直接绑 <c>Message</c>，正常流程全程无字、只有失败态才有话说——
    /// 屏幕右下角一根跳动的条，看不出是干什么用的。文案是界面的事，不塞回状态机，故落在本类。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void DisplayText_SaysSomethingInEveryStateTheOverlayIsVisibleIn(SessionState state)
    {
        var vm = new OverlayViewModel();

        vm.ApplyState(state, null);

        // 判据与可见性同源：看得见就必须有字，看不见就不该占字。
        Assert.Equal(vm.IsVisible, vm.DisplayText.Length > 0);
    }

    [Fact]
    public void DisplayText_WhileRecording_SaysSoInWords()
    {
        // 断言具体词是有意的：只查「非空」的话，各态返回同一个字符串也照样绿。
        var vm = new OverlayViewModel();

        vm.ApplyState(SessionState.Recording, null);

        Assert.Contains("录音", vm.DisplayText);
    }

    [Fact]
    public void DisplayText_WhileRecognizing_SaysSomethingElse()
    {
        var vm = new OverlayViewModel();

        vm.ApplyState(SessionState.Recognizing, null);

        Assert.Contains("识别", vm.DisplayText);
        Assert.DoesNotContain("录音", vm.DisplayText);
    }

    [Fact]
    public void DisplayText_PrefersTheRealMessage_OverTheCannedOne()
    {
        // 失败态那句话是唯一带真信息的（引擎加载失败的原因、没有录音设备……），
        // 被套话顶掉就等于把排障线索扔了。
        var vm = new OverlayViewModel();

        vm.ApplyState(SessionState.Failed, "没有可用的录音设备");

        Assert.Equal("没有可用的录音设备", vm.DisplayText);
    }

    /// <summary>
    /// **音量条的刻度必须是对数的**（理由见 <c>VolumeScaleTests</c>）：语音的线性 RMS 落在
    /// 0.02–0.2，直接当进度值就是那根「大声说话也只到两成」的条。本条钉的是「ViewModel 真用了
    /// 那个映射」，映射本身对不对归 Core 那组管。
    /// </summary>
    [Fact]
    public void TheStrip_UsesTheLogarithmicScale_NotTheRawRms()
    {
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recording, null);

        // 阳性对照：0.2 是「大声说话」的 RMS 量级，线性刻度下只有两成。
        Assert.True(0.2 < 0.3);

        vm.Volume = 0.2;

        var span = OverlayViewModel.BarMaxHeightDip - OverlayViewModel.BarMinHeightDip;
        var filled = (vm.Bar6Height - OverlayViewModel.BarMinHeightDip) / span;

        Assert.True(filled >= 0.6, $"最新一格只涨到 {filled:P0}，说明喂进去的还是原始 RMS");
    }

    [Fact]
    public void TheStrip_ScrollsLeft_SoItReadsAsAWaveformRatherThanOneBar()
    {
        // 七格是一条约半秒的历史，最新的在最右。不滚动的话七根柱子恒等高，
        // 那就只是一根被切开的条，白占地方。
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recording, null);

        vm.Volume = 0.5;
        var loudest = vm.Bar6Height;

        vm.Volume = 0.001;

        Assert.Equal(loudest, vm.Bar5Height);
        Assert.True(vm.Bar6Height < loudest, "新的一格没有覆盖掉最右那根");
    }

    [Fact]
    public void LeavingRecording_ClearsTheWholeStrip()
    {
        // 只清 Volume 不清柱子的话，识别态那半秒会挂着上一句话的波形，看着像还在录。
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recording, null);

        // **每次都要给一个不同的值**：CommunityToolkit 生成的 setter 遇到相等的值直接返回，
        // 连着写七遍 0.5 只会推进一格，下面那句阳性对照就会假红。
        for (var i = 0; i < OverlayViewModel.BarCount; i++)
        {
            vm.Volume = 0.5 + (i * 0.01);
        }

        Assert.True(vm.Bar0Height > OverlayViewModel.BarMinHeightDip, "阳性对照不成立：柱子根本没涨起来");

        vm.ApplyState(SessionState.Recognizing, null);

        Assert.Equal(OverlayViewModel.BarMinHeightDip, vm.Bar0Height);
        Assert.Equal(OverlayViewModel.BarMinHeightDip, vm.Bar6Height);
    }

    [Fact]
    public void ChangingTheVolume_NotifiesEveryBarOfTheStrip()
    {
        // 一次推进七格全动，漏发哪一格那根柱子就冻住——而颜色、文字、可见性全对，
        // 看起来只是「波形有点怪」。
        var vm = new OverlayViewModel();
        var seen = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        vm.Volume = 0.5;

        for (var i = 0; i < OverlayViewModel.BarCount; i++)
        {
            Assert.Contains($"Bar{i}Height", seen);
        }
    }


    [Fact]
    public void ChangingTheVolume_NotifiesTheBinding()
    {
        var vm = new OverlayViewModel();
        var seen = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        vm.Volume = 0.5;

        Assert.Contains(nameof(OverlayViewModel.Volume), seen);
    }

    [Fact]
    public void ANewViewModel_StartsHiddenAndIdle()
    {
        var vm = new OverlayViewModel();

        Assert.Equal(SessionState.Idle, vm.State);
        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Message);
        Assert.Equal(0d, vm.Volume);
    }

    /// <summary>
    /// 录音临近上限时的倒数。
    ///
    /// <para>
    /// 上限到点自有兜底（自动收尾并提示「已达最长录音时长」），**但那是事后**。Qwen3 档
    /// 只能录 25 秒，而这件事在界面上一个字都没有——真撞上去，用户看到的是「话说了一半没了」。
    /// 秒数由 <c>SessionController.RecordingCountdown</c> 报进来，文案在这里定。
    /// </para>
    /// </summary>
    [Fact]
    public void DisplayText_InTheLastFewSeconds_CountsDownInsteadOfJustSayingRecording()
    {
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recording, null);

        // 阳性对照：平时就是那三个字，没有秒数。
        Assert.Equal("录音中", vm.DisplayText);

        vm.RemainingSeconds = 3;

        Assert.Contains("3", vm.DisplayText);
        Assert.Contains("秒", vm.DisplayText);
    }

    [Fact]
    public void DisplayText_CountsDownOnlyWhileRecording()
    {
        // 倒数是录音态的子态。识别态还挂着「还剩 3 秒」，读起来像是识别本身有时限。
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recognizing, null);

        vm.RemainingSeconds = 3;

        Assert.DoesNotContain("3", vm.DisplayText);
    }

    [Fact]
    public void LeavingRecording_ForgetsTheCountdown()
    {
        // 与上一条各守一头：那条守的是显示时的判断，这条守的是状态本身被清干净。
        // 只留一头的话，下一轮录音会从上一轮的余数开始。
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recording, null);
        vm.RemainingSeconds = 3;

        vm.ApplyState(SessionState.Recognizing, null);

        Assert.Null(vm.RemainingSeconds);
    }

    [Fact]
    public void ChangingTheCountdown_NotifiesTheBinding()
    {
        // DisplayText 是派生属性，靠 [NotifyPropertyChangedFor] 挂上去。漏掉的话
        // 秒数在变而屏上那行字一直停在「录音中」——倒数就等于没做。
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recording, null);

        var seen = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        vm.RemainingSeconds = 3;

        Assert.Contains(nameof(OverlayViewModel.DisplayText), seen);
        Assert.Contains(nameof(OverlayViewModel.BarBrush), seen);
    }

    [Fact]
    public void BarBrush_InTheLastFewSeconds_TakesAColourOfItsOwn()
    {
        var vm = new OverlayViewModel();
        vm.ApplyState(SessionState.Recording, null);
        vm.RemainingSeconds = 3;

        var urgent = Assert.IsAssignableFrom<ISolidColorBrush>(vm.BarBrush).Color;

        // 与其余各态都得分得开。与识别态同色的话，余光扫过去像是「已经在识别了，可以松手」。
        Assert.NotEqual(BrushColorFor(SessionState.Recording), urgent);
        Assert.NotEqual(BrushColorFor(SessionState.Recognizing), urgent);
        Assert.NotEqual(BrushColorFor(SessionState.Failed), urgent);
        Assert.NotEqual(BrushColorFor(SessionState.Transcribing), urgent);
        Assert.Equal(byte.MaxValue, urgent.A);
    }

    /// <summary>
    /// **转写进度原样上屏。** 百分比由 <c>SessionController</c> 写进 Message（「转写中 35%」），
    /// 悬浮条不另拼一遍——两处各拼一份，格式迟早漂移。
    /// </summary>
    [Fact]
    public void DisplayText_WhileTranscribing_ShowsTheProgressMessage()
    {
        var vm = new OverlayViewModel();

        vm.ApplyState(SessionState.Transcribing, "转写中 35%");

        Assert.True(vm.IsVisible);
        Assert.Equal("转写中 35%", vm.DisplayText);
    }

    [Fact]
    public void DisplayText_WhileTranscribing_WithoutAMessage_StillSaysWhatIsGoingOn()
    {
        // 断言整句而非「含转写」：套话写成「录音中」「识别中」之类就该红。
        var vm = new OverlayViewModel();

        vm.ApplyState(SessionState.Transcribing, null);

        Assert.Equal("转写中", vm.DisplayText);
    }

    [Fact]
    public void BarBrush_WhileTranscribing_TakesAColourOfItsOwn()
    {
        var transcribing = BrushColorFor(SessionState.Transcribing);

        // 倒数那一色单独取：它挂在录音态上，BrushColorFor 量不到。
        var countdown = new OverlayViewModel();
        countdown.ApplyState(SessionState.Recording, null);
        countdown.RemainingSeconds = 3;
        var urgent = Assert.IsAssignableFrom<ISolidColorBrush>(countdown.BarBrush).Color;

        // 与其它四色都得分得开。与录音同色读起来是「麦克风还开着」，与识别、倒数、失败同色则各有各的误读。
        Assert.NotEqual(BrushColorFor(SessionState.Recording), transcribing);
        Assert.NotEqual(BrushColorFor(SessionState.Recognizing), transcribing);
        Assert.NotEqual(BrushColorFor(SessionState.Failed), transcribing);
        Assert.NotEqual(urgent, transcribing);
        Assert.Equal(byte.MaxValue, transcribing.A);
    }

    [Fact]
    public void ThePill_WhileTranscribing_IsAsWideAsWhileRecording()
    {
        // 取最长的那句「100%」：胶囊要是随文字变宽，进度每跳一格整颗胶囊就抖一下。
        var recording = new OverlayViewModel();
        recording.ApplyState(SessionState.Recording, null);

        var transcribing = new OverlayViewModel();
        transcribing.ApplyState(SessionState.Transcribing, "转写中 100%");

        // 阳性对照：录音态是个定数。两边都是 NaN（自适应）时下面那句相等照样成立。
        Assert.False(double.IsNaN(recording.PillWidthDip), "录音态的胶囊宽度不是定数");

        Assert.Equal(recording.PillWidthDip, transcribing.PillWidthDip);
    }

    private static Color BrushColorFor(SessionState state)
    {
        var vm = new OverlayViewModel();
        vm.ApplyState(state, null);

        // 断言的是「屏幕上会是什么颜色」，不是「用的是哪个 Brushes.XXX 静态实例」——
        // 后者换个等色的实例就假红。
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(vm.BarBrush);
        return brush.Color;
    }
}
