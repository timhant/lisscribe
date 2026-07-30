using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lisscribe.App.Views;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 录制控件的壳：真的 <see cref="UserControl"/>，真的路由事件。
///
/// <para>
/// **按键从按钮那头发起**，不是直接发给控件。两者的区别正是本组要验的东西：
/// Space 与 Enter 在冒泡阶段会被 <see cref="Button"/> 当成「按下按钮」吃掉，
/// 只有隧道阶段先截住才录得到——而 Space 恰好是本项目的默认主键。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class HotkeyRecorderControlTests
{
    [Fact]
    public void ClickingRecord_AnnouncesItBeforeItStartsEatingKeys()
    {
        // 顺序有实际后果：宿主要在这个事件里暂停热键。若控件先进入录制再喊，
        // 中间那段窗口里按下的键既被录制吃着、又还在触发真热键。
        WithRecorder(recorder =>
        {
            var seenWhileAnnouncing = (bool?)null;
            recorder.RecordingStarted += (_, _) => seenWhileAnnouncing = recorder.IsRecording;

            ClickRecord(recorder);

            Assert.False(seenWhileAnnouncing);
            Assert.True(recorder.IsRecording);
            Assert.Equal("请按下组合键（Esc 取消）", ButtonOf(recorder).Content);
        });
    }

    [Fact]
    public void Recording_CapturesSpace_WhichTheButtonWouldOtherwiseSwallow()
    {
        // **这一条是隧道那个决定的全部理由。** 改回冒泡的话，Button 自己的 Space/Enter
        // 处理会先把它当成「点了这个按钮」，录制永远等不到默认主键。
        WithRecorder(recorder =>
        {
            HotkeyRecordedEventArgs? recorded = null;
            var finished = 0;
            recorder.RecordingCompleted += (_, e) => recorded = e;
            recorder.RecordingFinished += (_, _) => finished++;

            ClickRecord(recorder);
            PressOnButton(recorder, Key.Space, KeyModifiers.Control | KeyModifiers.Alt);

            Assert.NotNull(recorded);
            Assert.Equal(["Ctrl", "Alt"], recorded.Modifiers);
            Assert.Equal("Space", recorded.MainKey);
            Assert.Equal(1, finished);
            Assert.False(recorder.IsRecording);
        });
    }

    [Fact]
    public void Recording_WhileOnlyModifiersAreHeld_KeepsWaiting()
    {
        WithRecorder(recorder =>
        {
            var completed = 0;
            recorder.RecordingCompleted += (_, _) => completed++;

            ClickRecord(recorder);
            PressOnButton(recorder, Key.LeftCtrl, KeyModifiers.Control);

            Assert.Equal(0, completed);
            Assert.True(recorder.IsRecording);
        });
    }

    [Fact]
    public void Escape_CancelsWithoutRecordingAnything_ButStillLetsTheHostResume()
    {
        // 取消也必须发 RecordingFinished，否则热键停在暂停态永不恢复——
        // 症状是「进过一次配置窗之后热键就不灵了」。
        WithRecorder(recorder =>
        {
            var completed = 0;
            var finished = 0;
            recorder.RecordingCompleted += (_, _) => completed++;
            recorder.RecordingFinished += (_, _) => finished++;

            recorder.DisplayText = "Ctrl + Alt + Space";
            ClickRecord(recorder);
            PressOnButton(recorder, Key.Escape, KeyModifiers.None);

            Assert.Equal(0, completed);
            Assert.Equal(1, finished);
            Assert.False(recorder.IsRecording);
            Assert.Equal("Ctrl + Alt + Space", ButtonOf(recorder).Content);
        });
    }

    /// <summary>
    /// 录到一半窗口没了（点「取消」、按 X、Esc 关窗）时，宿主必须照样收到
    /// <c>RecordingFinished</c>，否则它的 <c>Resume()</c> 永不调用，状态机永久停在 Paused
    /// ——症状是「进过一次配置窗之后热键就死了」。
    /// </summary>
    [Fact]
    public void CancelRecording_LetsTheHostResume_EvenThoughNothingWasRecorded()
    {
        WithRecorder(recorder =>
        {
            var completed = 0;
            var finished = 0;
            recorder.RecordingCompleted += (_, _) => completed++;
            recorder.RecordingFinished += (_, _) => finished++;

            recorder.DisplayText = "Ctrl + Alt + Space";
            ClickRecord(recorder);
            recorder.CancelRecording();

            Assert.Equal(0, completed);
            Assert.Equal(1, finished);
            Assert.False(recorder.IsRecording);
            Assert.Equal("Ctrl + Alt + Space", ButtonOf(recorder).Content);
        });
    }

    [Fact]
    public void CancelRecording_WhenNotRecording_SaysNothing()
    {
        // 阴性对照：关窗是常态，绝大多数关窗时根本没在录制。此时若照发 RecordingFinished，
        // 宿主就会对着一个从未暂停过的状态机调 Resume() —— 恰好把用户在托盘上点的「暂停」撤掉。
        WithRecorder(recorder =>
        {
            var finished = 0;
            recorder.RecordingFinished += (_, _) => finished++;

            recorder.CancelRecording();

            Assert.Equal(0, finished);
        });
    }

    [Fact]
    public void UnsupportedKey_SaysSo_InsteadOfLookingDead()
    {
        // 小键盘、多媒体键之类装不上钩子的键，原先与「还按着修饰键」共用一条静默路径，
        // 用户看到的就是「按了没反应」，于是再按一次同样的键。
        WithRecorder(recorder =>
        {
            var completed = 0;
            recorder.RecordingCompleted += (_, _) => completed++;

            ClickRecord(recorder);
            PressOnButton(recorder, Key.NumPad5, KeyModifiers.Control | KeyModifiers.Alt);

            Assert.Equal(0, completed);
            Assert.True(recorder.IsRecording, "换一个键就能接着录，不该把录制态也一起丢了");
            Assert.Equal("这个键不能当热键，换一个（Esc 取消）", ButtonOf(recorder).Content);
        });
    }

    [Fact]
    public void HoldingOnlyModifiers_StaysQuiet()
    {
        // 上一条的阴性对照：修饰键还按着是「等下一下」，出声就成了满屏乱跳的提示。
        WithRecorder(recorder =>
        {
            ClickRecord(recorder);
            PressOnButton(recorder, Key.LeftCtrl, KeyModifiers.Control);

            Assert.Equal("请按下组合键（Esc 取消）", ButtonOf(recorder).Content);
        });
    }

    [Fact]
    public void KeysPressedWhenNotRecording_AreLeftAlone()
    {
        // 阴性对照：不在录制态时控件不该截任何按键，否则配置窗里别的控件全哑了。
        WithRecorder(recorder =>
        {
            var completed = 0;
            recorder.RecordingCompleted += (_, _) => completed++;

            var args = PressOnButton(recorder, Key.Space, KeyModifiers.Control);

            Assert.Equal(0, completed);
            Assert.False(args.Handled);
        });
    }

    private static Button ButtonOf(HotkeyRecorderControl recorder) =>
        recorder.FindControl<Button>("RecordButton")!;

    private static void ClickRecord(HotkeyRecorderControl recorder) =>
        ButtonOf(recorder).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// 从**按钮**上发起一次按键。路由会先隧道穿过 UserControl 再冒泡回按钮，
    /// 这正是生产里键盘焦点落在按钮上时的走法。
    /// </summary>
    private static KeyEventArgs PressOnButton(
        HotkeyRecorderControl recorder, Key key, KeyModifiers modifiers)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        };

        ButtonOf(recorder).RaiseEvent(args);

        return args;
    }

    private static void WithRecorder(Action<HotkeyRecorderControl> assert) => AvaloniaTestHost.Run(() =>
    {
        var recorder = new HotkeyRecorderControl();
        var window = new Window { Width = 300, Height = 80, Content = recorder, ShowActivated = false };

        try
        {
            window.Show();
            assert(recorder);
        }
        finally
        {
            window.Close();
        }
    });
}
