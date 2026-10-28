using CommunityToolkit.Mvvm.ComponentModel;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Lisscribe.Core.Hotkey;

namespace Lisscribe.App.ViewModels;

/// <summary>
/// 配置窗背后的逻辑：读盘、校验、写盘，外加开机自启那个开关。
/// </summary>
/// <remarks>
/// 写盘是一件事，不是两件：校验没过就什么都不动——既不写文件，也不碰注册表。半做的后果是
/// 「配置没存下来，开机自启却已经改了」，而用户从界面上看不出来。
/// 自启动的现状以注册表为准，不以配置文件为准：配置里的 <c>app.autoStart</c> 记的是「上次保存时
/// 的意图」，注册表 Run 键才是「现在开机会不会真启动」，两者可能不一致（用户自己删了那个键、
/// 或者装了个清理工具替他删）。显示意图而不显示现状，用户会以为自启还开着。
/// </remarks>
public sealed partial class SettingsViewModel(
    ConfigStore store,
    IAutoStart autoStart,
    Func<string> deviceNameProvider) : ObservableObject
{
    /// <summary>
    /// 界面上没有对应控件、但保存时必须原样带回去的部分（版本号、provider 等）。
    /// 少了它，一次保存就会把用户手改的隐藏字段抹回默认值。
    /// </summary>
    private LisscribeConfig _config = LisscribeConfig.CreateDefault();

    /// <summary>
    /// 上屏方式下拉框的选项。给的是字符串，不是 <c>ComboBoxItem</c>——选中项要双向绑到
    /// <see cref="OutputMode"/> 这个 <c>string</c> 上，拿控件对象当选项则两头都对不上：进来时
    /// 匹配不到（框里空白），出去时写回属性的是 <c>"Avalonia.Controls.ComboBoxItem"</c>，
    /// 保存必被校验器拒掉。值须与 <see cref="ConfigValidator"/> 认的逐字一致。
    /// </summary>
    public IReadOnlyList<string> OutputModes { get; } = ["SendInput", "Clipboard"];

    /// <summary>
    /// 日志级别下拉框的选项。理由同 <see cref="OutputModes"/>，但这一项更该防：
    /// <c>app.logLevel</c> 没有任何校验，写坏了不报错，只会在下次启动时被悄悄当成 Info。
    /// </summary>
    public IReadOnlyList<string> LogLevels { get; } = ["Debug", "Info", "Warning", "Error"];

    /// <summary>
    /// 识别引擎下拉框的选项。取值来自 <see cref="AsrEngines.All"/>，与校验器认的是同一份常量。
    /// </summary>
    public IReadOnlyList<string> Engines { get; } = AsrEngines.All;

    /// <summary>
    /// 模型权重的下载地址。权重不随发行包走——SenseVoiceSmall 适用阿里 FunASR 的
    /// <c>MODEL_LICENSE</c>（自定义条款，不是 Apache/MIT），程序里能给的就只有一个地址。
    /// 两档的包都在这个 release 下，与取模型脚本拉的是同一处。
    /// </summary>
    public string ModelDownloadUrl => "https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models";

    /// <summary>
    /// 当前选中那一档能录多久。随下拉框当场变，不等保存：用户挑档时就该知道这一档能录多久，
    /// 而不是撞上限之后才发现。未知引擎返回空串而不是抛——这里是配置窗，抛出去就是窗口崩在
    /// 用户脸上。
    /// </summary>
    public string EngineCeilingHint => AsrEngines.IsSupported(Engine)
        ? $"该档单次最长可录 {AsrEngines.MaxRecordingSeconds(Engine)} 秒，到点自动结束并识别"
        : string.Empty;

    /// <summary>
    /// <see cref="Load"/> 期间把字段填进控件不算「用户换了档」。少了这个闸，一次载入就会顺手
    /// 改写用户的模型目录——而他什么也没点。
    /// </summary>
    private bool _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineCeilingHint))]
    private string _engine = AsrEngines.SenseVoice;

    [ObservableProperty]
    private string _hotkeyDisplay = string.Empty;

    [ObservableProperty]
    private int _holdThresholdMs;

    [ObservableProperty]
    private string _modelDir = string.Empty;

    [ObservableProperty]
    private string _outputMode = "SendInput";

    [ObservableProperty]
    private bool _restoreClipboard;

    [ObservableProperty]
    private bool _trimTrailingPunctuation;

    [ObservableProperty]
    private int _trimPunctuationMaxChars;

    /// <summary>中文数字转阿拉伯数字。两档都显示，但只对 Qwen3 档有效——默认档由模型自带。</summary>
    [ObservableProperty]
    private bool _chineseNumberItn;

    /// <summary>转写结果的保存目录。空串表示程序目录下的 <c>transcripts\</c>。</summary>
    [ObservableProperty]
    private string _transcriptDir = string.Empty;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private string _logLevel = "Info";

    [ObservableProperty]
    private string _currentDeviceName = string.Empty;

    /// <summary>最近一次操作的错误。热键录制被拒、保存校验不过，都落在这里。</summary>
    [ObservableProperty]
    private string? _lastError;

    /// <summary>
    /// 载入时发现配置文件坏掉的告警。<c>null</c> 表示这次载入一切正常。
    /// <see cref="ConfigStore"/> 把这件事记在 <c>LastOutcome</c> 上就不管了，不接出来就没有任何人
    /// 看得见。报警的去处是悬浮条——托盘没有气泡通知 API。
    /// </summary>
    [ObservableProperty]
    private string? _loadWarning;

    public void Load()
    {
        _config = ConfigValidator.Validate(store.Load()).Normalized;

        LoadWarning = store.LastOutcome == ConfigLoadOutcome.RecoveredFromCorrupt
            ? "配置文件无法解析，已回落到默认值；原文件已备份为 config.json.bad。"
            : null;

        _loading = true;

        try
        {
            // 解析不出来时退而显示原始字段：显示「Ctrl + ? + Space」也比显示空白强，至少用户看得出
            // 是哪一项出了问题。校验器已经规范化过，这条几乎走不到。
            HotkeyDisplay = HotkeyCombination.TryParse(_config.Hotkey, out var combo, out _)
                ? combo.ToDisplayString()
                : string.Join(" + ", _config.Hotkey.Modifiers.Append(_config.Hotkey.MainKey));

            HoldThresholdMs = _config.Hotkey.HoldThresholdMs;

            // 模型目录先填，引擎后填，且中间隔着 _loading 那道闸。反过来写的话 OnEngineChanged 会在
            // ModelDir 还是空串时触发，跟随逻辑对着空串空转一趟，随后被真值覆盖——看着没事，
            // 实则那道闸变成了摆设，谁把这两行换个位置都察觉不到。
            ModelDir = _config.Asr.ModelDir;
            Engine = _config.Asr.Engine;
            OutputMode = _config.Output.Mode;
            RestoreClipboard = _config.Output.RestoreClipboard;
            TrimTrailingPunctuation = _config.Output.TrimTrailingPunctuation;
            TrimPunctuationMaxChars = _config.Output.TrimPunctuationMaxChars;
            ChineseNumberItn = _config.Output.ChineseNumberItn;

            // 配置有校验错误时规范化器原样返回，Transcription 可能是 null，故须兜底。
            TranscriptDir = _config.Transcription?.OutputDir ?? string.Empty;
            AutoStart = autoStart.IsEnabled;
            LogLevel = _config.App.LogLevel;
            CurrentDeviceName = deviceNameProvider();
            LastError = null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 用户在下拉框里换了一档：模型目录若还是原档的默认值就跟着换，自定义过的一概不动
    /// （规矩见 <see cref="ModelDirFollowPolicy"/>）。
    /// 就地改而不是等到保存时再改，是为了让用户当场看见目录变成了什么——存下去才悄悄改，
    /// 等于程序背着他动了一个他填过的字段。
    /// </summary>
    partial void OnEngineChanged(string? oldValue, string newValue)
    {
        // oldValue 可空是生成器给的签名（首次赋值时它是 null）。null 在跟随规矩里就是「不认识的
        // 引擎」，原样返回目录，正是想要的行为。
        if (_loading)
        {
            return;
        }

        ModelDir = ModelDirFollowPolicy.Follow(oldValue ?? string.Empty, newValue, ModelDir);
    }

    /// <summary>
    /// 录制控件录完一次的回调。校验不过就保留原值并把理由记进 <see cref="LastError"/>。
    /// 热键不挂在任何一个可观察属性上——它存进 <see cref="_config"/>，显示用
    /// <see cref="HotkeyDisplay"/>。故 <see cref="TrySave"/> 重新拼配置时必须从 <c>_config</c> 里
    /// 取它；照「每个字段都从属性拿」的写法会把录好的热键悄悄丢掉，症状是「配置窗里显示改好了，
    /// 重启回到旧热键」。
    /// </summary>
    public void ApplyRecordedHotkey(string[] modifiers, string mainKey)
    {
        var section = new HotkeySection(modifiers, mainKey, HoldThresholdMs);

        if (!HotkeyCombination.TryParse(section, out var combo, out var error))
        {
            LastError = error;

            return;
        }

        LastError = null;
        _config = _config with { Hotkey = section };
        HotkeyDisplay = combo.ToDisplayString();
    }

    /// <summary>
    /// 「浏览…」按钮的逻辑：让用户选一个文件夹写进 <see cref="TranscriptDir"/>，取消则不动。
    /// 选择器是委托（入参为起始目录，当前值不是存在的目录时为 <c>null</c>；返回本地路径，取消为
    /// <c>null</c>），因为 Avalonia 的 <c>IStorageProvider</c> 带内部成员、用户代码无法实现，换成
    /// 委托才测得到。窗口里的点击处理是 <c>async void</c>，异常冒出去会让整个托盘程序退出，
    /// 故这里全部接住，记进 <see cref="LastError"/>。成功选中时清掉旧错误，做法同
    /// <see cref="ApplyRecordedHotkey"/>。
    /// </summary>
    public async Task BrowseTranscriptDirAsync(Func<string?, Task<string?>> pickFolder)
    {
        try
        {
            var current = TranscriptDir.Trim();

            var picked = await pickFolder(Directory.Exists(current) ? current : null);

            if (picked is null)
            {
                return;
            }

            LastError = null;
            TranscriptDir = picked;
        }
        catch (Exception ex)
        {
            LastError = $"无法打开文件夹选择框：{ex.Message}";
        }
    }

    public bool TrySave(out string? error)
    {
        var candidate = _config with
        {
            Hotkey = _config.Hotkey with { HoldThresholdMs = HoldThresholdMs },
            Asr = _config.Asr with { Engine = Engine, ModelDir = ModelDir },
            Output = new OutputSection(
                OutputMode, RestoreClipboard, TrimTrailingPunctuation, TrimPunctuationMaxChars,
                ChineseNumberItn),
            App = new AppSection(AutoStart, LogLevel),

            // 目录存不存在、能不能写，转写开始时才查，保存不拦。
            Transcription = new TranscriptionSection(TranscriptDir.Trim()),
        };

        var result = ConfigValidator.Validate(candidate);

        if (!result.IsValid)
        {
            error = string.Join("；", result.Errors);
            LastError = error;

            return false;
        }

        // 顺序有意如此：先落盘，再动注册表。反过来的话，写盘失败就留下一个「开机自启已改、
        // 配置没改」的不一致状态，而那一半没有任何界面显示得出来。
        store.Save(result.Normalized);
        _config = result.Normalized;

        if (AutoStart)
        {
            autoStart.Enable(Environment.ProcessPath ?? AppContext.BaseDirectory);
        }
        else
        {
            autoStart.Disable();
        }

        error = null;
        LastError = null;

        return true;
    }
}
