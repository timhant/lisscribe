using Avalonia.Threading;
using Lisscribe.App.Tray;
using Lisscribe.App.ViewModels;
using Lisscribe.App.Views;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Lisscribe.Core.Hotkey;
using Lisscribe.Core.Session;
using Lisscribe.Platform;
using Microsoft.Extensions.Logging;

namespace Lisscribe.App.Composition;

/// <summary>
/// 组合根：把真正的平台实现 new 出来，接上 <see cref="AppWiring"/>，再按顺序启停。
/// 本类刻意不含任何判断逻辑——凡是「什么情况下该怎样」的规矩都住在别处（状态机、
/// <see cref="AppWiring"/>、<see cref="TrayMenuModel"/>）。这里只剩「谁在谁之前」，
/// 而那件事有测试盯不住的部分，故顺序上的每一条都写明了理由。
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;

    private readonly ILogger<AppServices> _logger;

    private readonly ConfigStore _configStore;

    private readonly LisscribeConfig _config;

    private readonly KeyboardHook _hook;

    private readonly WasapiAudioCapture _capture;

    /// <summary>档位由 <see cref="AsrEngineFactory"/> 按配置挑，这里只拿接口。</summary>
    private readonly IAsrEngine _engine;

    private readonly SessionMonitor _monitor;

    private readonly WindowProbe _probe;

    private readonly RegistryAutoStart _autoStart;

    private readonly SessionController _controller;

    private readonly OverlayViewModel _overlayViewModel;

    private readonly OverlayWindow _overlay;

    private readonly TrayIconHost _tray;

    private readonly AppWiring _wiring;

    private readonly RestartSequence _restartSequence;

    private SettingsWindow? _settingsWindow;

    private RestartConfirmWindow? _restartWindow;

    private int _shutdownStarted;

    public AppServices(string baseDirectory, Action requestExit)
    {
        LogDirectory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(LogDirectory);

        _configStore = new ConfigStore(Path.Combine(baseDirectory, "config.json"));

        // 校验器规范化过的那份才是全程用的那份：它把大小写、修饰键顺序、越界值都收拾干净了。
        var loaded = ConfigValidator.Validate(_configStore.Load());
        _config = loaded.Normalized;

        var level = ParseLevel(_config.App.LogLevel);

        // 两处都要给：SetMinimumLevel 管的是工厂这一层，而文件 sink 自己还有一个默认
        // Information 的下限，只调前者的话 logLevel=Debug 写出来的文件里一条 Debug 也没有
        // ——而那正是排障时唯一有用的那一档。
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(level)
            .AddFile(Path.Combine(LogDirectory, "lisscribe-{Date}.log"), level));

        _logger = _loggerFactory.CreateLogger<AppServices>();

        if (_configStore.LastOutcome == ConfigLoadOutcome.RecoveredFromCorrupt)
        {
            // 这件事必须报警。托盘没有气泡 API，故报警的去处只能是悬浮条，
            // 见下面 StartAsync 里那句 ShowNotice。
            _logger.LogWarning("配置文件无法解析，已回落默认值，原文件备份为 config.json.bad。");
        }

        if (!loaded.IsValid)
        {
            _logger.LogWarning("配置有 {Count} 处不合规，已按默认值规范化：{Errors}",
                loaded.Errors.Count, string.Join("；", loaded.Errors));
        }

        _probe = new WindowProbe(_loggerFactory.CreateLogger<WindowProbe>());
        _hook = new KeyboardHook(_loggerFactory.CreateLogger<KeyboardHook>());
        _capture = new WasapiAudioCapture(_loggerFactory.CreateLogger<WasapiAudioCapture>());

        // 模型目录约定见 AsrModelPaths.EffectiveModelDir：发行看目录，开发看环境变量。
        var configuredModelDir = _config.Asr.ModelDir;
        var modelDirOverride = Environment.GetEnvironmentVariable(AsrModelPaths.ModelDirEnvironmentVariable);
        var effectiveModelDir = AsrModelPaths.EffectiveModelDir(configuredModelDir, modelDirOverride);

        if (!string.Equals(effectiveModelDir, configuredModelDir, StringComparison.Ordinal))
        {
            // 必须留痕：覆盖是隐形的，用户设过一次就会忘。模型报缺失时，「在哪找的」这条线索
            // 若不在日志里，人会对着 config.json 那个路径反复确认。
            _logger.LogInformation(
                "模型目录被环境变量 {Variable} 覆盖为 {Dir}（配置里写的是 {Configured}）。",
                AsrModelPaths.ModelDirEnvironmentVariable, effectiveModelDir, configuredModelDir);
        }

        _engine = AsrEngineFactory.Create(
            _config.Asr with { ModelDir = effectiveModelDir }, _loggerFactory);
        _monitor = new SessionMonitor(_loggerFactory.CreateLogger<SessionMonitor>());
        _autoStart = new RegistryAutoStart();

        _overlayViewModel = new OverlayViewModel();
        _overlay = new OverlayWindow(
            _overlayViewModel,
            new OverlayWindowStyler(),
            _loggerFactory.CreateLogger<OverlayWindow>());

        // 剪贴板路要一个真窗口当 owner：传 NULL 的话 OpenClipboard 根本不锁任何东西。
        // 悬浮条是这里唯一一直活着的窗口。
        var injector = new CompositeTextInjector(
            _config.Output,
            new SendInputTextInjector(_probe, _loggerFactory.CreateLogger<SendInputTextInjector>()),
            new ClipboardTextInjector(
                _probe,
                _config.Output.RestoreClipboard,
                () => _overlay.TryGetPlatformHandle()?.Handle ?? nint.Zero,
                _loggerFactory.CreateLogger<ClipboardTextInjector>()));

        _controller = new SessionController(
            _hook,
            _capture,
            _engine,
            injector,
            _probe,
            _monitor,
            SessionOptionsFrom(_config),
            TimeProvider.System,
            _loggerFactory.CreateLogger<SessionController>());

        _tray = new TrayIconHost(
            pause: _controller.Pause,
            resume: _controller.Resume,
            openSettings: OpenSettings,

            // 转写协调器尚未接入，先放一个什么都不做的回调，任务 13 接上。
            toggleTranscription: () => { },
            openModelFolder: OpenModelFolder,
            openLogFolder: OpenLogFolder,
            requestExit: requestExit,
            _loggerFactory.CreateLogger<TrayIconHost>());

        // Post 不是 Invoke：Invoke 同步等 UI 线程，而收尾路径要 Join 采集线程，那就是死锁。
        // 通知时长取状态机那份，两条路（组合根报警 / 状态机失败）在屏上待的时间才一样长。
        _wiring = new AppWiring(
            _controller,
            _overlayViewModel,
            _tray,
            action => Dispatcher.UIThread.Post(action),
            TimeProvider.System,
            SessionOptionsFrom(_config).FailedDisplayDuration);

        // 换档要重启才生效，而重启就是「拉起后继进程 + 请求本进程退出」这两步，顺序不可换。
        // requestExit 与托盘「退出」走的是同一个，故收尾路径也是同一条。
        _restartSequence = new RestartSequence(new ProcessAppRestarter(), requestExit);
    }

    public string LogDirectory { get; }

    public OverlayWindow Overlay => _overlay;

    public TrayIconHost Tray => _tray;

    public SessionController Controller => _controller;

    /// <summary>
    /// 起来。顺序是有讲究的：先接线再显示，先显示再装钩子，模型放到最后且不挡着。
    /// </summary>
    public async Task StartAsync()
    {
        _wiring.Connect();

        // 悬浮条先 Show 再 Hide：点击穿透那四个扩展位是在 Opened 里设的，而 Show() 会把
        // GWL_EXSTYLE 整个写回它自己那份。先亮一下再藏起来，等于把「窗口真的建出来了、
        // 样式也上了」这件事提前到用户按热键之前完成。
        _overlay.Show();
        _overlay.Hide();

        // 必须走 SessionController.Start，不能自己去调 _hook.Start / _monitor.Start：订阅、
        // 识别工作线程、看门狗计时器全在那个方法里，它最后才去启动钩子与监听。
        // 自己调 _hook.Start() 的后果是日志一路报「键盘钩子已启动」「热键已装」，而按热键什么也
        // 不会发生——钩子确实装上了，只是没有任何人订阅它，且单测一条也测不到。
        if (HotkeyCombination.TryParse(_config.Hotkey, out var combination, out var hotkeyError))
        {
            _controller.Start(combination);
            _logger.LogInformation("热键已装：{Hotkey}", combination.ToDisplayString());
        }
        else
        {
            // 装不上钩子不等于程序没用：托盘还在，用户能进配置窗改回一个合法组合。
            _logger.LogError("热键配置无法使用，已跳过装钩子：{Error}", hotkeyError);
            ShowNotice($"热键配置有误：{hotkeyError}");
        }

        if (_configStore.LastOutcome == ConfigLoadOutcome.RecoveredFromCorrupt)
        {
            ShowNotice("配置文件损坏，已回落默认值（原文件存为 config.json.bad）");
        }

        // 加载 SenseVoice 约 1.1–1.7s、Qwen3 约 4.2–4.7s。绝不能挡着 UI 起来——托盘图标晚出现
        // 四秒，用户会以为程序没起。
        try
        {
            await _engine.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("识别引擎已就绪：{Engine}", _engine.EngineName);
        }
        catch (Exception ex)
        {
            // 模型缺失或损坏在这里是可预期的（权重要用户自己下）。LoadAsync 已经把「文件不全」
            // 「protobuf 截断」翻成了带补救建议的托管异常，原样报给用户即可——它比任何我们能编的
            // 话都准。
            _logger.LogError(ex, "识别引擎加载失败。");
            ShowNotice($"识别引擎加载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 收尾。只跑一次：托盘的「退出」与窗口关闭都可能走到这里。
    /// 顺序：先摘托盘（<c>IsVisible = false</c> 再 Dispose），再停状态机（它自己会按定好的次序
    /// 停采集、等在途识别、最后 Dispose 引擎），最后才拆事件与日志。反过来做会让收尾过程中的
    /// 状态变更打到已经关掉的窗口上。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        _tray.Detach();

        try
        {
            await _controller.ShutdownAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "状态机收尾失败，继续拆其余部件。");
        }

        // Dispose 而不是 Disconnect：还得掐掉那个在途的通知计时器，否则它会在 Avalonia 已经开始
        // 拆平台层之后 Post 一次 Window.Hide()。
        _wiring.Dispose();

        _hook.Dispose();
        _monitor.Dispose();
        _capture.Dispose();
        _loggerFactory.Dispose();
    }

    /// <summary>
    /// 录音上限随引擎档位走（<see cref="SessionOptions.CreateFor"/>），别改回 CreateDefault：
    /// Qwen3 档说满 60s 的后果不是慢，是上屏一个字。
    ///
    /// <para>
    /// 中文数字转换那个开关原样交给 <c>CreateFor</c>，判断留在它那里——本类刻意不含判断逻辑。
    /// </para>
    /// </summary>
    private static SessionOptions SessionOptionsFrom(LisscribeConfig config) =>
        SessionOptions.CreateFor(config.Asr.Engine, config.Output.ChineseNumberItn) with
        {
            HoldThreshold = TimeSpan.FromMilliseconds(config.Hotkey.HoldThresholdMs),
            TrimTrailingPunctuation = config.Output.TrimTrailingPunctuation,
            TrimPunctuationMaxChars = config.Output.TrimPunctuationMaxChars,
        };

    private static LogLevel ParseLevel(string level) => level switch
    {
        "Debug" => LogLevel.Debug,
        "Warning" => LogLevel.Warning,
        "Error" => LogLevel.Error,
        _ => LogLevel.Information,
    };

    /// <summary>
    /// 报警一律经 <see cref="AppWiring.ShowNotice"/>，别在这儿自己 Post——这条路绕开状态机，
    /// 也就绕开了它那个失败提示计时器，自己 Post 的红提示永不消失。
    /// </summary>
    private void ShowNotice(string message) => _wiring.ShowNotice(message);

    private void OpenSettings() => Dispatcher.UIThread.Post(() =>
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();

            return;
        }

        var viewModel = new SettingsViewModel(_configStore, _autoStart, () => _capture.CurrentDeviceName);
        viewModel.Load();

        // 录制期间必须暂停热键，否则用户想把热键改成 Ctrl+Alt+Space，一按就开始录音。
        _settingsWindow = new SettingsWindow(viewModel, _controller.Pause, _controller.Resume);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;

            // 配置可能改了热键。重装一次，让改动立刻生效而不是等下次启动——「改完要重启才生效」
            // 是用户最容易当成没保存成功的那种失败。
            RestartHotkey();

            // 配置也可能改了模型目录、换了档，或者动了那批「重启才生效」的字段。
            ApplySavedConfig();
        };

        _settingsWindow.Show();
    });

    private void RestartHotkey()
    {
        var config = ConfigValidator.Validate(_configStore.Load()).Normalized;

        if (!HotkeyCombination.TryParse(config.Hotkey, out var combination, out var error))
        {
            _logger.LogError("配置窗保存后的热键仍不可用：{Error}", error);

            return;
        }

        try
        {
            _hook.Stop();
            _hook.Start(combination);
            _logger.LogInformation("热键已重装：{Hotkey}", combination.ToDisplayString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重装热键失败。");
            ShowNotice("热键更新失败，请检查日志");
        }
    }

    /// <summary>
    /// 配置窗关掉之后，处置识别引擎与那批「重启才生效」的字段。怎么处置由
    /// <see cref="SettingsClosePolicy"/> 说了算——这里只负责执行，与本类「不含判断逻辑」的规矩一致。
    /// 只管引擎是不够的：上屏方式、长按阈值、日志级别那三样都只在本类的构造函数里建过一次，
    /// 漏了它们就是「保存关窗什么都不会变，也没有一个字告诉他」。
    /// </summary>
    private void ApplySavedConfig()
    {
        var saved = ConfigValidator.Validate(_configStore.Load()).Normalized;
        var newModelDir = AsrModelPaths.EffectiveModelDir(
            saved.Asr.ModelDir,
            Environment.GetEnvironmentVariable(AsrModelPaths.ModelDirEnvironmentVariable));

        var action = SettingsClosePolicy.Decide(
            EngineReloadPolicy.Decide(
                _engine.IsLoaded,
                _engine.EngineName, _engine.ModelDir,
                saved.Asr.Engine, newModelDir),
            _engine.EngineName, _engine.ModelDir,
            saved.Asr.Engine, newModelDir,

            // 第一个实参必须是 _config——本进程正在跑的那份，不是刚存下的 saved。拿 saved 跟它
            // 自己比永远为空，于是用户点过一次「稍后重启」之后就再也不会被提示，而跑着的仍是旧值。
            // _config 是启动时加载并规范化的那份，全程不改写，正是此处要的。
            // 这一条没有任何单测够得着（组合根立不起测试），故它只能靠这行注释与手工清单守住。
            ConfigRestartPolicy.Diff(_config, saved));

        if (action.ReloadEngine)
        {
            // 不 await：关窗回调跑在 UI 线程上，而加载是一两秒的阻塞活。
            // 异常全在 ReloadEngineAsync 内部收住，逃不出去。
            _ = ReloadEngineAsync(newModelDir);
        }

        if (action.AskRestart)
        {
            // 弹窗那句是笼统的，分辨力全在这一行日志里。用户不需要知道细节，排障的人需要——
            // 否则「我明明没改过日志级别」这类报障永远查不下去。
            _logger.LogInformation(
                "下列改动要重启才生效：{Changes}", string.Join("；", action.Changes));

            AskToRestart();
        }
    }

    /// <summary>
    /// 弹一句「现在重启吗」。必须 Post，不能就地弹：本方法是从配置窗的 <c>Closed</c> 回调里进来的，
    /// 就地新建并 <c>Show()</c> 一个窗口等于在关窗的中途再开一扇窗。
    /// 用户点「稍后」时退回原来那条红条提示，这条不能省——此刻配置文件里已是新值、跑着的还是
    /// 旧值（连录音上限都还是旧档那个），而这个不一致没有别的可见痕迹。
    /// </summary>
    private void AskToRestart() => Dispatcher.UIThread.Post(() =>
    {
        if (_restartWindow is not null)
        {
            _restartWindow.Activate();

            return;
        }

        var window = new RestartConfirmWindow(RestartPrompt.Message);

        _restartWindow = window;

        window.Closed += (_, _) =>
        {
            _restartWindow = null;

            if (!window.Confirmed)
            {
                ShowNotice(RestartPrompt.Message);

                return;
            }

            if (!_restartSequence.TryRestart(out var error))
            {
                // 拉不起来时本进程原样活着，故这里还能报得出话来。
                _logger.LogError("重启 lisscribe 失败：{Error}", error);
                ShowNotice($"重启失败：{error}");
            }
        };

        window.Show();
    });

    private async Task ReloadEngineAsync(string modelDir)
    {
        try
        {
            if (_engine.IsLoaded)
            {
                // 连关两次配置窗时，第二次可能撞上第一次刚加载成功。此刻换址会被引擎拒掉，
                // 报出来的却是一句「加载失败」——那是假话。
                return;
            }

            _engine.RepointModelDir(modelDir);

            await _engine.LoadAsync(CancellationToken.None).ConfigureAwait(false);

            // 成功只记日志，不弹提示：悬浮条唯一能显示文字的状态是 Failed（红条），拿它报喜就是
            // 用报错的样子说好消息。真正的反馈是这一刻起按热键真的能录了。
            _logger.LogInformation("识别引擎已就绪：{Engine}（模型目录 {Dir}）", _engine.EngineName, modelDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "识别引擎重新加载失败。");
            ShowNotice($"识别引擎加载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 打开模型目录。开的是 <see cref="IAsrEngine.ModelDir"/>——程序现在去找模型的那条路，
    /// 环境变量覆盖与配置窗改址都已经算进去了，与 <c>_config</c> 里那份启动时的原值可能不同。
    /// 目录不存在就先建出来：这一项正是给「模型没放对地方」的人准备的，那时目录多半还不存在；
    /// 建出来等于把目标位置摆到用户面前，他把下载好的文件拖进去即可。
    /// </summary>
    private void OpenModelFolder()
    {
        var dir = _engine.ModelDir;

        try
        {
            Directory.CreateDirectory(dir);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打不开模型目录 {Dir}。", dir);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打不开日志目录 {Dir}。", LogDirectory);
        }
    }
}
