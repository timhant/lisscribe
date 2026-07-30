using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lisscribe.App.ViewModels;

namespace Lisscribe.App.Views;

/// <summary>
/// 配置窗。逻辑都在 <see cref="SettingsViewModel"/> 里，这里只做三件事：接线录制控件、
/// 把保存的成败反馈给用户、关窗。
/// </summary>
/// <remarks>
/// 录制期间必须暂停热键，否则用户把热键改成 <c>Ctrl+Alt+Space</c> 时一按就开始录音；本窗口不认识
/// 状态机，故由构造时传进来的两个回调负责。保存失败不关窗——关掉再弹错误，用户改好的值全没了。
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    private readonly Action _pauseHotkey;

    private readonly Action _resumeHotkey;

    /// <summary>
    /// XAML 预览器要一个无参构造。预览器碰得到的每一样真东西都要换掉：配置文件必须指到临时目录，
    /// 不能用相对路径——相对路径按当前工作目录展开，在预览器里点一下「保存」就往那儿写一份全是
    /// 空值的 config.json（此路径的 ViewModel 从没 <c>Load()</c> 过），而工作目录哪天恰好是输出
    /// 目录，写坏的就是用户正在用的那份。注册表那侧同理，见 <see cref="PreviewAutoStart"/>。
    /// </summary>
    public SettingsWindow()
        : this(
            new SettingsViewModel(
                new Lisscribe.Core.Config.ConfigStore(
                    Path.Combine(Path.GetTempPath(), "lisscribe-xaml-preview.json")),
                new PreviewAutoStart(),
                () => "（预览）"),
            () => { },
            () => { })
    {
    }

    public SettingsWindow(SettingsViewModel viewModel, Action pauseHotkey, Action resumeHotkey)
    {
        _viewModel = viewModel;
        _pauseHotkey = pauseHotkey;
        _resumeHotkey = resumeHotkey;

        InitializeComponent();

        DataContext = _viewModel;

        Recorder.RecordingStarted += (_, _) => _pauseHotkey();
        Recorder.RecordingFinished += (_, _) => _resumeHotkey();
        Recorder.RecordingCompleted += (_, e) => _viewModel.ApplyRecordedHotkey(e.Modifiers, e.MainKey);
    }

    /// <summary>
    /// 录到一半关窗，热键也必须还回去：<c>RecordingFinished</c> 只在录到或按 Esc 时发，点「取消」、
    /// 按右上角 X 都不发，<see cref="_resumeHotkey"/> 于是永不调用、状态机永久停在 Paused，症状是
    /// 「进过一次配置窗之后热键就死了」而日志上一切正常。必须放在 <c>base.OnClosed</c> 之前：
    /// <c>Closed</c> 事件在它里面引发，宿主就在那个事件里重装钩子，顺序反了重装发生在恢复之前。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        Recorder.CancelRecording();

        base.OnClosed(e);
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave(out _))
        {
            Close();
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    /// <summary>预览器专用：什么也不做的自启动实现，绝不去碰真注册表。</summary>
    private sealed class PreviewAutoStart : Lisscribe.Core.Abstractions.IAutoStart
    {
        public bool IsEnabled => false;

        public void Enable(string executablePath)
        {
        }

        public void Disable()
        {
        }
    }
}
