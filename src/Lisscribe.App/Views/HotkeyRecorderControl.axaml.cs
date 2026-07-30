using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Lisscribe.App.Views;

/// <summary>热键录制按钮。点一下进入录制，按一组键录完，Esc 取消。</summary>
/// <remarks>
/// 录制期间绝不能让热键本身生效——用户想把热键改成 <c>Ctrl+Alt+Space</c>，一按下去就开始录音。
/// 本控件不动状态机，只在进出录制时各发一个事件由宿主暂停 / 恢复，且
/// <see cref="RecordingStarted"/> 必须在开始吃按键之前发出去，顺序反了就留一个「热键还活着而我们
/// 已经在录」的窗口。
/// <para>
/// 按键走 Tunnel 而不是冒泡：Space 与 Enter 在冒泡阶段会被 Button 当成「按下按钮」吃掉，那两个
/// 最常见的主键于是永远录不到。
/// </para>
/// </remarks>
public sealed partial class HotkeyRecorderControl : UserControl
{
    public static readonly StyledProperty<string> DisplayTextProperty =
        AvaloniaProperty.Register<HotkeyRecorderControl, string>(
            nameof(DisplayText), defaultValue: string.Empty);

    private const string RecordingPrompt = "请按下组合键（Esc 取消）";

    /// <summary>
    /// 按到本项目装不上钩子的键时显示它：不出声就是「按了没反应」，用户分不出「这个键不支持」与
    /// 「这控件坏了」。仍留在录制态，换一个键即可继续。
    /// </summary>
    private const string UnsupportedKeyPrompt = "这个键不能当热键，换一个（Esc 取消）";

    private bool _isRecording;

    public HotkeyRecorderControl()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>录制开始。宿主应在此暂停热键。</summary>
    public event EventHandler? RecordingStarted;

    /// <summary>录制结束（录到了、或用户按 Esc 取消了）。宿主应在此恢复热键。</summary>
    public event EventHandler? RecordingFinished;

    /// <summary>录到了一组键。参数是配置文件里的写法，够不够格当热键由上层判。</summary>
    public event EventHandler<HotkeyRecordedEventArgs>? RecordingCompleted;

    /// <summary>按钮上显示的文字。非录制状态下由宿主绑成当前热键。</summary>
    public string DisplayText
    {
        get => GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    public bool IsRecording => _isRecording;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DisplayTextProperty && !_isRecording)
        {
            RecordButton.Content = DisplayText;
        }
    }

    private void OnRecordClicked(object? sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            return;
        }

        // 先喊停再开录。反过来就留下一个「热键还活着而我们已经在吃按键」的窗口。
        RecordingStarted?.Invoke(this, EventArgs.Empty);

        _isRecording = true;
        RecordButton.Content = RecordingPrompt;
        RecordButton.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecording)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            StopRecording();

            return;
        }

        if (!HotkeyRecording.TryCapture(e.KeyModifiers, e.Key, out var modifiers, out var mainKey))
        {
            // 两种 false 得分开对待：只按着修饰键是「等下一下」该沉默，而这个键装不上钩子
            // （小键盘、多媒体键等）必须出声，都沉默的话后者在用户眼里就是「按了没反应」。
            RecordButton.Content = HotkeyRecording.IsModifierKey(e.Key)
                ? RecordingPrompt
                : UnsupportedKeyPrompt;

            e.Handled = true;

            return;
        }

        e.Handled = true;
        StopRecording();
        RecordingCompleted?.Invoke(this, new HotkeyRecordedEventArgs(modifiers, mainKey));
    }

    /// <summary>
    /// 中止录制并让宿主恢复热键，不在录制态时什么也不做。给「录到一半窗口没了」用：
    /// <see cref="RecordingFinished"/> 只在录到或按 Esc 时发，用户点「取消」或直接关窗一次也不发，
    /// 宿主的恢复便永不调用，关窗之后热键彻底失灵而界面上没有任何迹象。
    /// </summary>
    public void CancelRecording()
    {
        if (!_isRecording)
        {
            return;
        }

        StopRecording();
    }

    private void StopRecording()
    {
        _isRecording = false;
        RecordButton.Content = DisplayText;
        RecordingFinished?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>录到的一组键，用配置文件的写法表示。</summary>
public sealed class HotkeyRecordedEventArgs(string[] modifiers, string mainKey) : EventArgs
{
    public string[] Modifiers { get; } = modifiers;

    public string MainKey { get; } = mainKey;
}
