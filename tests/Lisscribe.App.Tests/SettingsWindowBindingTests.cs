using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Lisscribe.App.ViewModels;
using Lisscribe.App.Views;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 配置窗**真控件与 ViewModel 之间那根线**。<see cref="SettingsViewModelTests"/> 只测纯逻辑，
/// 它对 XAML 里绑错了什么一无所知——本组补的正是那一段。
///
/// <para>
/// 要防的形状：<c>SelectedItem</c> 绑在 <c>string</c> 属性上而选项是 <see cref="ComboBoxItem"/> 对象，
/// 两者永不相等——进来时匹配不上（下拉框显示空白），出去时把控件对象的 <c>ToString()</c> 写回属性
/// （保存时报 <c>实为 "Avalonia.Controls.ComboBoxItem"</c>）。<b>两个方向都坏，且都不抛异常</b>，
/// 所以不碰真控件的测试一条也发现不了。
/// </para>
/// </summary>
[Collection(DesktopCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class SettingsWindowBindingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-settings-window").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void OpeningTheWindow_ShowsTheCurrentOutputMode_AndDoesNotClobberIt()
    {
        // 进来的方向。下拉框显示不出当前值只是难看，**把属性写坏才是真伤**——
        // 用户什么都没碰，一开一关就足以把 output.mode 改掉。
        WithWindow((vm, window) =>
        {
            var box = ComboBoxNamed(window, "OutputModeBox");

            Assert.Equal("SendInput", vm.OutputMode);
            Assert.Equal(0, box.SelectedIndex);
        });
    }

    [Fact]
    public void PickingAnOutputMode_WritesTheStringBack_NotTheComboBoxItem()
    {
        // 出去的方向。
        WithWindow((vm, window) =>
        {
            var box = ComboBoxNamed(window, "OutputModeBox");

            box.SelectedIndex = 1;

            Assert.Equal("Clipboard", vm.OutputMode);
            Assert.True(vm.TrySave(out var error), error);
        });
    }

    [Fact]
    public void OpeningTheWindow_ShowsTheCurrentLogLevel_AndDoesNotClobberIt()
    {
        // 日志级别这条**更隐蔽**：ConfigValidator 根本不校验 app.logLevel，
        // 写坏了也不报错，只会在下次启动时被 ParseLevel 悄悄当成 Info。
        WithWindow((vm, window) =>
        {
            var box = ComboBoxNamed(window, "LogLevelBox");

            Assert.Equal("Info", vm.LogLevel);
            Assert.Equal(1, box.SelectedIndex);
        });
    }

    [Fact]
    public void PickingALogLevel_WritesTheStringBack_AndSurvivesARoundTripToDisk()
    {
        WithWindow((vm, window) =>
        {
            var box = ComboBoxNamed(window, "LogLevelBox");

            box.SelectedIndex = 0;

            Assert.Equal("Debug", vm.LogLevel);
            Assert.True(vm.TrySave(out var error), error);
            Assert.Equal("Debug", new ConfigStore(ConfigPath).Load().App.LogLevel);
        });
    }

    [Fact]
    public void OpeningTheWindow_ShowsTheCurrentEngine_AndDoesNotClobberIt()
    {
        WithWindow((vm, window) =>
        {
            var box = ComboBoxNamed(window, "EngineBox");

            Assert.Equal("SenseVoice", vm.Engine);
            Assert.Equal(0, box.SelectedIndex);
        });
    }

    /// <summary>
    /// 换档这条**必须走真控件**：模型目录跟随挂在 <c>OnEngineChanged</c> 上，
    /// 而那个回调只有在 <c>SelectedItem</c> 真把新值写回属性时才触发。
    /// 绑定若又写成了 <see cref="ComboBoxItem"/>，纯逻辑测试照样全绿，
    /// 实机上却是「选了 Qwen3、目录没动、保存后报模型缺失」。
    /// </summary>
    [Fact]
    public void PickingAnEngine_WritesTheStringBack_AndTakesTheModelDirectoryWithIt()
    {
        WithWindow((vm, window) =>
        {
            var box = ComboBoxNamed(window, "EngineBox");

            box.SelectedIndex = 1;

            Assert.Equal("Qwen3Asr", vm.Engine);
            Assert.Equal("./models/qwen3-asr", vm.ModelDir);
            Assert.True(vm.TrySave(out var error), error);
            Assert.Equal("Qwen3Asr", new ConfigStore(ConfigPath).Load().Asr.Engine);
        });
    }

    /// <summary>
    /// **录到一半关窗，热键必须还回去。** 录制开始时宿主暂停了状态机；<c>RecordingFinished</c>
    /// 若只在录到或按 Esc 时发，点「取消」与按 X 关窗都不发，<c>Resume()</c> 便永不调用、
    /// 状态机永久停在 Paused ——「进过一次配置窗热键就死了」，而关窗后宿主还会重装一次钩子，
    /// 日志上一切正常，更看不出来。
    ///
    /// <para>
    /// 顺序一并钉住：恢复必须排在宿主重装钩子之前，反过来则重装完还是 Paused。
    /// </para>
    /// </summary>
    [Fact]
    public void ClosingTheWindowMidRecording_ResumesTheHotkey_BeforeTheHostRestartsIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            var viewModel = new SettingsViewModel(
                new ConfigStore(ConfigPath), new NoopAutoStart(), () => "测试麦克风");

            viewModel.Load();

            var log = new List<string>();

            var window = new SettingsWindow(
                viewModel, () => log.Add("pause"), () => log.Add("resume"))
            {
                ShowActivated = false,
            };

            // 宿主（AppServices）在 Closed 里重装钩子，这里如实照搬那根线。
            window.Closed += (_, _) => log.Add("restart-hotkey");

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

                var recorder = Assert.Single(
                    window.GetLogicalDescendants().OfType<HotkeyRecorderControl>());

                recorder.FindControl<Button>("RecordButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                // 阳性对照：不先真进到录制态，下面那条断言就是空的。
                Assert.Equal(["pause"], log);
                Assert.True(recorder.IsRecording);
            }
            finally
            {
                window.Close();
            }

            Assert.Equal(["pause", "resume", "restart-hotkey"], log);
        });
    }

    /// <summary>
    /// **给 XAML 预览器用的那个无参构造，绝不许碰工作目录。**
    ///
    /// <para>
    /// 相对路径 <c>"config.json"</c> 按<b>当前工作目录</b>展开，而预览器的工作目录是仓库根：
    /// 在预览器里点一下保存，就往那里写一份全是 ViewModel 字段初始值的配置（<c>modelDir</c> 与
    /// <c>saveDir</c> 空串、三项上屏开关 false/0）——工作目录哪天恰好是输出目录，
    /// 写坏的就是用户正在用的那份。自启动已经换成空实现「不碰真注册表」，
    /// <c>ConfigStore</c> 也必须同样不碰真文件。
    /// </para>
    /// </summary>
    [Fact]
    public void ThePreviewConstructor_NeverWritesIntoTheWorkingDirectory()
    {
        AvaloniaTestHost.Run(() =>
        {
            // 改进程级工作目录本是并行测试里的经典雷，此处敢改是因为查过：
            // Lisscribe.App 与本测试程序集里再没有第二处读工作目录或用相对路径的代码。
            // **日后谁加了这类代码，先来看这条注释。**
            var originalWorkingDirectory = Directory.GetCurrentDirectory();

            try
            {
                Directory.SetCurrentDirectory(_dir);

                var window = new SettingsWindow();

                var viewModel = Assert.IsType<SettingsViewModel>(window.DataContext);

                // 预览构造不调 Load()，holdThresholdMs 停在 0，而校验器要 50–3000。
                // **不先给它一个合法值，TrySave 会在校验那关就被拦下、什么也不写，
                // 下面那条断言便是空的**——连坏的实现也能让它绿。
                viewModel.HoldThresholdMs = 300;

                Assert.True(viewModel.TrySave(out var error), error);

                Assert.Empty(Directory.GetFiles(_dir));
            }
            finally
            {
                Directory.SetCurrentDirectory(originalWorkingDirectory);
            }
        });
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    /// <summary>
    /// 按 <c>x:Name</c> 认下拉框。**不按控件顺序取**（顺序随布局调整而变），
    /// **也不按选项条数取**：条数是碰巧唯一、不是身份，窗口一多出个同样两项的下拉框，
    /// 按条数认的测试就报「匹配到 2 个」。
    /// </summary>
    private static ComboBox ComboBoxNamed(Window window, string name) =>
        Assert.Single(
            window.GetLogicalDescendants().OfType<ComboBox>(),
            box => box.Name == name);

    private void WithWindow(Action<SettingsViewModel, SettingsWindow> assert) => AvaloniaTestHost.Run(() =>
    {
        var viewModel = new SettingsViewModel(
            new ConfigStore(ConfigPath), new NoopAutoStart(), () => "测试麦克风");

        viewModel.Load();

        var window = new SettingsWindow(viewModel, () => { }, () => { })
        {
            ShowActivated = false,
        };

        try
        {
            window.Show();

            // 绑定是排队生效的，不抽干队列就会量到「还没绑上」的中间态。
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

            assert(viewModel, window);
        }
        finally
        {
            window.Close();
        }
    });

    private sealed class NoopAutoStart : IAutoStart
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
