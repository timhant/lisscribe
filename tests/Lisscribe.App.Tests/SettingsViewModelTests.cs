using Lisscribe.App.ViewModels;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.App.Tests;

/// <summary>
/// 配置窗的**纯逻辑**：读进来、改、校验、写回去，外加自启动开关。不需要真窗口。
///
/// <para>
/// 每条测试一个独立的临时目录，用完删掉——配置是写真文件的，共用目录会让相邻测试互相看见。
/// </para>
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-settings").FullName;

    private readonly FakeAutoStart _autoStart = new();

    private string ConfigPath => Path.Combine(_dir, "config.json");

    /// <summary>
    /// **同一个实例**贯穿一条测试：每次访问都 new 一个的话，
    /// <see cref="ConfigStore.LastOutcome"/> 这类跨调用的状态永远读不到。
    /// </summary>
    private ConfigStore Store => field ??= new ConfigStore(ConfigPath);

    private SettingsViewModel CreateViewModel() => new(Store, _autoStart, () => "测试麦克风");

    /// <summary>
    /// 换档最扎眼的差别是「能录多久」——60 秒掉到 25 秒。界面上不说这句，用户撞上去看到的是
    /// 「话说了一半没了」，是最难自诊的症状之一。
    /// </summary>
    [Fact]
    public void EngineCeilingHint_NamesTheRecordingCeilingOfTheSelectedTier()
    {
        var vm = CreateViewModel();
        vm.Load();

        vm.Engine = "SenseVoice";

        Assert.Contains("60", vm.EngineCeilingHint);

        vm.Engine = "Qwen3Asr";

        Assert.Contains("25", vm.EngineCeilingHint);

        // 阴性对照：换了档那句话必须真的跟着变，否则上一句对一条写死的文案也成立。
        Assert.DoesNotContain("60", vm.EngineCeilingHint);
    }

    [Fact]
    public void ChangingTheEngine_NotifiesTheCeilingHint()
    {
        // 派生属性靠 [NotifyPropertyChangedFor] 挂上去。漏掉的话下拉框换了、那行字不动，
        // 于是它说的是上一档的秒数——比不写还坏。
        var vm = CreateViewModel();
        vm.Load();

        var seen = new List<string>();
        ((System.ComponentModel.INotifyPropertyChanged)vm).PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        vm.Engine = "Qwen3Asr";

        Assert.Contains(nameof(SettingsViewModel.EngineCeilingHint), seen);
    }

    [Fact]
    public void ModelDownloadUrl_IsTheOneThingTheProgramCanGiveInsteadOfTheWeights()
    {
        // 权重不随发行包走（SenseVoiceSmall 适用阿里 FunASR MODEL_LICENSE，不是 Apache/MIT），
        // 故程序能给用户的只有这一条下载地址。
        var vm = CreateViewModel();

        Assert.StartsWith("https://", vm.ModelDownloadUrl);
        Assert.Contains("sherpa-onnx", vm.ModelDownloadUrl);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_PopulatesFromDefaults()
    {
        var vm = CreateViewModel();

        vm.Load();

        Assert.Equal("Ctrl + Alt + Space", vm.HotkeyDisplay);
        Assert.Equal(300, vm.HoldThresholdMs);
        Assert.Equal("SendInput", vm.OutputMode);
        Assert.Equal("测试麦克风", vm.CurrentDeviceName);
    }

    // ---- 引擎档位 ----------------------------------------------------------

    [Fact]
    public void Load_ShowsTheConfiguredEngine_AndOffersBothTiers()
    {
        var vm = CreateViewModel();

        vm.Load();

        Assert.Equal("SenseVoice", vm.Engine);
        Assert.Equal(["SenseVoice", "Qwen3Asr"], vm.Engines);
    }

    [Fact]
    public void SwitchingTheEngine_MovesTheModelDirectory_WhenItWasStillTheDefault()
    {
        // 目录不跟着走的话，用户切完档必然撞一次「缺少模型文件」——而他什么也没做错。
        var vm = CreateViewModel();
        vm.Load();

        vm.Engine = "Qwen3Asr";

        Assert.Equal("./models/qwen3-asr", vm.ModelDir);
    }

    [Fact]
    public void SwitchingTheEngine_LeavesACustomModelDirectoryAlone()
    {
        // 用户指到别处的目录是他自己的安排，程序无从知道另一档的权重该在哪。
        var vm = CreateViewModel();
        vm.Load();
        vm.ModelDir = @"D:\my-models\sv";

        vm.Engine = "Qwen3Asr";

        Assert.Equal(@"D:\my-models\sv", vm.ModelDir);
    }

    /// <summary>
    /// <b>载入时把字段填进控件不算「用户换了档」——只是打开设置窗，配置不许被改。</b>
    ///
    /// <para>
    /// 配置刻意选一个**不自洽**的组合：引擎是 Qwen3Asr，目录却还停在 SenseVoice 的默认值
    /// （手改 config.json 时漏改一行就是这样）。此时 <c>Load</c> 把 Engine 填进去会触发
    /// <c>OnEngineChanged</c>，而目录恰好等于旧档默认值，跟随逻辑于是「顺手帮他改好」——
    /// 用户只是**看了一眼**设置窗，存着的值就变了，再点保存便固化下来。
    /// </para>
    ///
    /// <para>
    /// <b><c>Load</c> 里的赋值顺序必须是 ModelDir 先、Engine 后。</b>反过来的话，跟随逻辑对着
    /// 空串空转一趟就被真值覆盖，把那道闸整个删掉本条也照样绿——那时真正兜住的是赋值顺序而非闸。
    /// </para>
    /// </summary>
    [Fact]
    public void Load_DoesNotRewriteTheModelDirectory_WhileMerelyFillingInTheFields()
    {
        File.WriteAllText(ConfigPath, """
            {"version":1,
             "hotkey":{"modifiers":["Ctrl","Alt"],"mainKey":"Space","holdThresholdMs":300},
             "asr":{"engine":"Qwen3Asr","modelDir":"./models/sense-voice","provider":"cpu"},
             "output":{"mode":"SendInput","restoreClipboard":true,
                       "trimTrailingPunctuation":true,"trimPunctuationMaxChars":8},
             "app":{"autoStart":false,"logLevel":"Info"}}
            """);

        var vm = CreateViewModel();
        vm.Load();

        Assert.Equal("Qwen3Asr", vm.Engine);
        Assert.Equal("./models/sense-voice", vm.ModelDir);
    }

    [Fact]
    public void MerelyOpeningTheSettingsWindow_LeavesTheFileOnDiskByteForByte()
    {
        // 上一条盯的是内存里的属性，这一条盯盘上的文件——跟随逻辑若改的是别的路径，
        // 属性那条可能仍绿，而用户的 config.json 已经被动过了。
        const string original = """
            {"version":1,
             "hotkey":{"modifiers":["Ctrl","Alt"],"mainKey":"Space","holdThresholdMs":300},
             "asr":{"engine":"Qwen3Asr","modelDir":"./models/sense-voice","provider":"cpu"},
             "output":{"mode":"SendInput","restoreClipboard":true,
                       "trimTrailingPunctuation":true,"trimPunctuationMaxChars":8},
             "app":{"autoStart":false,"logLevel":"Info"}}
            """;

        File.WriteAllText(ConfigPath, original);

        CreateViewModel().Load();

        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void TrySave_PersistsTheChosenEngine()
    {
        var vm = CreateViewModel();
        vm.Load();
        vm.Engine = "Qwen3Asr";

        Assert.True(vm.TrySave(out var error), error);

        // 重开一个 ViewModel 读盘，才算真的落到了文件里。
        var reopened = new SettingsViewModel(new ConfigStore(ConfigPath), _autoStart, () => "测试麦克风");
        reopened.Load();

        Assert.Equal("Qwen3Asr", reopened.Engine);
        Assert.Equal("./models/qwen3-asr", reopened.ModelDir);
    }

    // ---- 转写结果保存位置 --------------------------------------------------

    [Fact]
    public void Load_ShowsTheConfiguredTranscriptDir()
    {
        // 带中文与空格的路径：原样进、原样出，不许被截断或转义。
        var configured = LisscribeConfig.CreateDefault() with
        {
            Transcription = new TranscriptionSection(@"D:\我的 录音\转写"),
        };

        Store.Save(configured);

        var vm = CreateViewModel();
        vm.Load();

        Assert.Equal(@"D:\我的 录音\转写", vm.TranscriptDir);
    }

    [Fact]
    public void Load_WithoutATranscriptionSection_ShowsAnEmptyDir()
    {
        // 旧配置没有这一节：校验通过，规范化补成空串。
        File.WriteAllText(ConfigPath, """
            {"version":1,
             "hotkey":{"modifiers":["Ctrl","Alt"],"mainKey":"Space","holdThresholdMs":300},
             "asr":{"engine":"SenseVoice","modelDir":"./models/sense-voice","provider":"cpu"},
             "output":{"mode":"SendInput","restoreClipboard":true,
                       "trimTrailingPunctuation":true,"trimPunctuationMaxChars":8},
             "app":{"autoStart":false,"logLevel":"Info"}}
            """);

        var vm = CreateViewModel();

        vm.Load();

        Assert.Equal("", vm.TranscriptDir);
    }

    [Fact]
    public void Load_WhenTheConfigIsInvalidAndHasNoTranscriptionSection_ShowsAnEmptyDirWithoutThrowing()
    {
        // 校验没过时规范化器原样返回配置，Transcription 仍是 null；打开设置窗不许因此崩。
        File.WriteAllText(ConfigPath, """
            {"version":1,
             "hotkey":{"modifiers":["Ctrl","Alt"],"mainKey":"Space","holdThresholdMs":10000},
             "asr":{"engine":"SenseVoice","modelDir":"./models/sense-voice","provider":"cpu"},
             "output":{"mode":"SendInput","restoreClipboard":true,
                       "trimTrailingPunctuation":true,"trimPunctuationMaxChars":8},
             "app":{"autoStart":false,"logLevel":"Info"}}
            """);

        var vm = CreateViewModel();

        vm.Load();

        Assert.Equal("", vm.TranscriptDir);
    }

    [Fact]
    public void TrySave_PersistsTheTranscriptDir_WithSurroundingWhitespaceTrimmed()
    {
        var vm = CreateViewModel();
        vm.Load();
        vm.TranscriptDir = "  E:\\转写 结果\\新目录  ";

        Assert.True(vm.TrySave(out var error), error);

        var saved = new ConfigStore(ConfigPath).Load().Transcription;
        Assert.NotNull(saved);
        Assert.Equal(@"E:\转写 结果\新目录", saved.OutputDir);
    }

    [Fact]
    public void TrySave_AcceptsADirectoryThatDoesNotExist()
    {
        // 目录存不存在、能不能写，转写开始时才查；保存不拦。
        var vm = CreateViewModel();
        vm.Load();
        vm.TranscriptDir = Path.Combine(_dir, "不存在的", "目录");

        Assert.True(vm.TrySave(out var error), error);
        Assert.False(Directory.Exists(vm.TranscriptDir));
    }

    [Fact]
    public void Load_TakesTheAutoStartStateFromTheRegistry_NotFromTheConfigFile()
    {
        // **两个真相来源，必须钉死谁说了算。** 配置文件里的 app.autoStart 是「上次保存时的意图」，
        // 注册表 Run 键才是「现在开机会不会真启动」。用户可能在别处删了那个键，
        // 也可能装了个清理工具替他删。显示意图而不显示现状，用户会以为自启还开着。
        File.WriteAllText(ConfigPath, """
            {"version":1,
             "hotkey":{"modifiers":["Ctrl","Alt"],"mainKey":"Space","holdThresholdMs":300},
             "asr":{"engine":"SenseVoice","modelDir":"./models/sense-voice","provider":"cpu"},
             "output":{"mode":"SendInput","restoreClipboard":true,
                       "trimTrailingPunctuation":true,"trimPunctuationMaxChars":8},
             "app":{"autoStart":true,"logLevel":"Info"}}
            """);

        var vm = CreateViewModel();
        vm.Load();

        Assert.False(vm.AutoStart);
    }

    [Fact]
    public void Load_WhenTheFileWasCorrupt_SaysSo()
    {
        // 坏文件备份为 config.json.bad、回落默认值，并报警。报警的路子是悬浮条（托盘没有气泡
        // API），但**先得有人把这件事说出来**——ConfigStore 只把它记在 LastOutcome 上，
        // 不接出来就没人看得见。
        File.WriteAllText(ConfigPath, "{ 这不是 JSON");

        var vm = CreateViewModel();
        vm.Load();

        Assert.NotNull(vm.LoadWarning);
        Assert.Contains("config.json.bad", vm.LoadWarning);

        // 阴性对照：正常载入不许报警，否则上一句对任何实现都成立。
        var clean = new SettingsViewModel(new ConfigStore(Path.Combine(_dir, "other.json")), _autoStart, () => "x");
        clean.Load();
        Assert.Null(clean.LoadWarning);
    }

    [Fact]
    public void TrySave_WithValidValues_PersistsToDisk()
    {
        var vm = CreateViewModel();
        vm.Load();
        vm.HoldThresholdMs = 500;

        var ok = vm.TrySave(out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(500, new ConfigStore(ConfigPath).Load().Hotkey.HoldThresholdMs);
    }

    /// <summary>
    /// <b>中文数字转换的开关要真的存下去。</b><c>OutputSection.ChineseNumberItn</c> 带默认值
    /// （为的是旧 config.json 缺字段时仍算「开」），于是 <c>TrySave</c> 重新 <c>new</c> 这一节时
    /// 漏掉它**不会编译错**，只会把用户刚关掉的开关无声地重置回开——重启一次它自己就回来了。
    /// </summary>
    [Fact]
    public void TrySave_PersistsChineseNumberItnBothWays()
    {
        var vm = CreateViewModel();
        vm.Load();
        vm.ChineseNumberItn = false;

        Assert.True(vm.TrySave(out var error), error);
        Assert.False(new ConfigStore(ConfigPath).Load().Output.ChineseNumberItn);

        // 阴性对照：少了它，实现里写死 false 也照样绿。
        vm.ChineseNumberItn = true;

        Assert.True(vm.TrySave(out error), error);
        Assert.True(new ConfigStore(ConfigPath).Load().Output.ChineseNumberItn);
    }

    [Fact]
    public void TrySave_WithInvalidThreshold_FailsWithMessageAndDoesNotWrite()
    {
        var vm = CreateViewModel();
        vm.Load();
        vm.HoldThresholdMs = 10_000;

        var ok = vm.TrySave(out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("holdThresholdMs", error);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void TrySave_FirstTimeIntoAnEmptyDirectory_Succeeds()
    {
        // File.Replace 在目标不存在时抛，故首次保存这条路必须单独有测试。
        var vm = CreateViewModel();
        vm.Load();

        var ok = vm.TrySave(out _);

        Assert.True(ok);
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void TrySave_TakesTheRecordedHotkeyAllTheWayToDisk()
    {
        // 整条链上最容易断的一环：ApplyRecordedHotkey 改的是 ViewModel 私有的那份配置，
        // 而 TrySave 是从各个 ObservableProperty 重新拼一份出来的——热键不在任何一个可观察属性上。
        // 少接一根线，症状是「配置窗里显示改好了，重启回到旧热键」。
        var vm = CreateViewModel();
        vm.Load();

        vm.ApplyRecordedHotkey(["Ctrl", "Shift"], "F2");
        Assert.True(vm.TrySave(out _));

        var saved = new ConfigStore(ConfigPath).Load().Hotkey;
        Assert.Equal(["Ctrl", "Shift"], saved.Modifiers);
        Assert.Equal("F2", saved.MainKey);
    }

    [Fact]
    public void TrySave_WithAutoStartOn_RegistersIt_AndWithItOff_UnregistersIt()
    {
        var vm = CreateViewModel();
        vm.Load();

        vm.AutoStart = true;
        vm.TrySave(out _);
        Assert.True(_autoStart.IsEnabled);
        Assert.Equal(Environment.ProcessPath ?? AppContext.BaseDirectory, _autoStart.LastPath);

        vm.AutoStart = false;
        vm.TrySave(out _);
        Assert.False(_autoStart.IsEnabled);
    }

    [Fact]
    public void TrySave_WhenValidationFails_DoesNotTouchAutoStart()
    {
        // 校验没过就整件事都不算数。半做的后果是：配置没存下来，开机自启却已经改了。
        var vm = CreateViewModel();
        vm.Load();
        vm.AutoStart = true;
        vm.HoldThresholdMs = 10_000;

        Assert.False(vm.TrySave(out _));
        Assert.False(_autoStart.IsEnabled);
    }

    [Fact]
    public void ApplyRecordedHotkey_UpdatesDisplay()
    {
        var vm = CreateViewModel();
        vm.Load();

        vm.ApplyRecordedHotkey(["Ctrl", "Shift"], "F2");

        Assert.Equal("Ctrl + Shift + F2", vm.HotkeyDisplay);
        Assert.Null(vm.LastError);
    }

    [Theory]
    [InlineData(new string[0], "F2", "修饰键")]
    [InlineData(new[] { "Ctrl" }, "Alt", "修饰键")]
    [InlineData(new[] { "Meta" }, "F2", "修饰键")]
    public void ApplyRecordedHotkey_WhenTheComboIsNotUsable_KeepsTheOldOneAndExplains(
        string[] modifiers, string mainKey, string expectedInError)
    {
        // 三种拒绝理由各一条：没有修饰键、主键本身是修饰键、修饰键名不认识。
        // 只测第一种的话，「凡是解析失败都照单全收」这种实现照样绿。
        var vm = CreateViewModel();
        vm.Load();

        vm.ApplyRecordedHotkey(modifiers, mainKey);

        Assert.Equal("Ctrl + Alt + Space", vm.HotkeyDisplay);
        Assert.Contains(expectedInError, vm.LastError ?? string.Empty);
    }

    private sealed class FakeAutoStart : IAutoStart
    {
        public bool IsEnabled { get; private set; }

        public string? LastPath { get; private set; }

        public void Enable(string executablePath)
        {
            IsEnabled = true;
            LastPath = executablePath;
        }

        public void Disable() => IsEnabled = false;
    }
}
