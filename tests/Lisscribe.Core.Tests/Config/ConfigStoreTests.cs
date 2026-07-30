using Lisscribe.Core.Config;
using Xunit;

namespace Lisscribe.Core.Tests.Config;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-cfg").FullName;

    private string Path => System.IO.Path.Combine(_dir, "config.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaultsAndReportsCreatedDefault()
    {
        var store = new ConfigStore(Path);

        var config = store.Load();

        Assert.Equal(ConfigLoadOutcome.CreatedDefault, store.LastOutcome);
        Assert.Equal(["Ctrl", "Alt"], config.Hotkey.Modifiers);
        Assert.Equal("Space", config.Hotkey.MainKey);
        Assert.Equal(300, config.Hotkey.HoldThresholdMs);
        Assert.Equal("SenseVoice", config.Asr.Engine);
        Assert.Equal("cpu", config.Asr.Provider);
        Assert.Equal("SendInput", config.Output.Mode);
        Assert.Equal(8, config.Output.TrimPunctuationMaxChars);
    }

    /// <summary>
    /// <b>2026-07-29 砍掉 <c>recording</c> 一节时补的回归。</b>那一节曾经是 schema 的一部分，
    /// 每一份已经存在的 <c>config.json</c> 里都写着它。若被判成坏文件，用户的热键、模型目录、
    /// 上屏方式会**一次性全部回落默认值**，而原文件只留个 <c>.bad</c> 后缀——
    /// 那是这次删字段最坏的一种收场。
    ///
    /// <para>
    /// 断言的是「<see cref="ConfigLoadOutcome.Ok"/> 且其余字段原样读回」，**不是**「解析没抛异常」
    /// ——后者在回落路径上同样成立，等于空断言。
    /// </para>
    /// </summary>
    [Fact]
    public void Load_WhenTheFileStillCarriesTheRetiredRecordingSection_KeepsEverythingElse()
    {
        File.WriteAllText(Path, """
            {
              "version": 1,
              "hotkey": { "modifiers": ["Alt"], "mainKey": "/", "holdThresholdMs": 500 },
              "asr": { "engine": "Qwen3Asr", "modelDir": "./models/qwen3-asr", "provider": "cpu" },
              "output": { "mode": "Clipboard", "restoreClipboard": false,
                          "trimTrailingPunctuation": false, "trimPunctuationMaxChars": 3 },
              "recording": { "saveAudio": true, "saveDir": "./records" },
              "app": { "autoStart": true, "logLevel": "Debug" }
            }
            """);

        var store = new ConfigStore(Path);

        var config = store.Load();

        Assert.Equal(ConfigLoadOutcome.Ok, store.LastOutcome);
        Assert.False(File.Exists(Path + ".bad"));

        Assert.Equal(["Alt"], config.Hotkey.Modifiers);
        Assert.Equal("/", config.Hotkey.MainKey);
        Assert.Equal(500, config.Hotkey.HoldThresholdMs);
        Assert.Equal("Qwen3Asr", config.Asr.Engine);
        Assert.Equal("./models/qwen3-asr", config.Asr.ModelDir);
        Assert.Equal("Clipboard", config.Output.Mode);
        Assert.False(config.Output.RestoreClipboard);
        Assert.Equal(3, config.Output.TrimPunctuationMaxChars);
        Assert.True(config.App.AutoStart);
        Assert.Equal("Debug", config.App.LogLevel);
    }

    [Fact]
    public void Save_WhenTargetDoesNotExist_CreatesFile()
    {
        // 回归测试：File.Replace 要求目标已存在，首次写入必须走 File.Move 分支
        var store = new ConfigStore(Path);
        var config = LisscribeConfig.CreateDefault();

        store.Save(config);

        Assert.True(File.Exists(Path));
        Assert.False(File.Exists(Path + ".tmp"));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var store = new ConfigStore(Path);
        var original = LisscribeConfig.CreateDefault() with
        {
            Hotkey = new HotkeySection(["Ctrl", "Shift"], "F2", 450),
            Asr = new AsrSection("SenseVoice", "./models/sv", "cpu"),
        };

        store.Save(original);
        var loaded = new ConfigStore(Path).Load();

        Assert.Equal(["Ctrl", "Shift"], loaded.Hotkey.Modifiers);
        Assert.Equal("F2", loaded.Hotkey.MainKey);
        Assert.Equal(450, loaded.Hotkey.HoldThresholdMs);
        Assert.Equal("./models/sv", loaded.Asr.ModelDir);
    }

    [Fact]
    public void Save_OverExistingFile_ReplacesAtomicallyAndLeavesNoTempFile()
    {
        var store = new ConfigStore(Path);
        store.Save(LisscribeConfig.CreateDefault());

        store.Save(LisscribeConfig.CreateDefault() with
        {
            Hotkey = new HotkeySection(["Alt"], "Q", 300),
        });

        Assert.Equal("Q", new ConfigStore(Path).Load().Hotkey.MainKey);
        Assert.False(File.Exists(Path + ".tmp"));
    }

    [Fact]
    public void Load_WhenJsonIsCorrupt_FallsBackToDefaultsAndBacksUpBadFile()
    {
        File.WriteAllText(Path, "{ this is not json");
        var store = new ConfigStore(Path);

        var config = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, store.LastOutcome);
        Assert.Equal("Space", config.Hotkey.MainKey);
        Assert.True(File.Exists(Path + ".bad"));
        Assert.Contains("not json", File.ReadAllText(Path + ".bad"));
    }

    [Fact]
    public void Load_WhenCorruptTwice_OverwritesPreviousBadFile()
    {
        File.WriteAllText(Path + ".bad", "old bad content");
        File.WriteAllText(Path, "{ broken again");

        _ = new ConfigStore(Path).Load();

        Assert.Contains("broken again", File.ReadAllText(Path + ".bad"));
    }

    [Fact]
    public void Load_WhenJsonIsEmptyObject_FallsBackToDefaultsAndBacksUpBadFile()
    {
        // {} 能被反序列化——构造器式源生成器给缺失的引用类型参数填 null，
        // 不抛 JsonException，config 本身非 null，若不逐段校验会被误判为 Ok。
        File.WriteAllText(Path, "{}");
        var store = new ConfigStore(Path);

        var config = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, store.LastOutcome);
        Assert.Equal("Space", config.Hotkey.MainKey);
        Assert.True(File.Exists(Path + ".bad"));
        Assert.Contains("{}", File.ReadAllText(Path + ".bad"));
    }

    [Fact]
    public void Load_WhenSomeSectionsMissing_FallsBackToDefaultsAndBacksUpBadFile()
    {
        // 结构完整性不足：仅有 hotkey 一段，其余三段（asr / output / app）缺失，
        // 反序列化本身成功但结果携带 null 子段——同样必须按损坏处理，而非 Ok。
        File.WriteAllText(Path,
            """{"version":1,"hotkey":{"modifiers":["Ctrl"],"mainKey":"A","holdThresholdMs":100}}""");
        var store = new ConfigStore(Path);

        var config = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, store.LastOutcome);
        Assert.Equal("Space", config.Hotkey.MainKey);
        Assert.True(File.Exists(Path + ".bad"));
    }

    [Fact]
    public void Save_WhenSwapFails_LeavesNoTempFileAndRethrows()
    {
        // 目标路径本身是一个目录：File.Exists 对目录恒为 false，故走 Move 分支，
        // 而 Move 到一个已存在的目录会抛 IOException——以此确定性地逼出交换失败。
        Directory.CreateDirectory(Path);
        var store = new ConfigStore(Path);

        Assert.ThrowsAny<IOException>(() => store.Save(LisscribeConfig.CreateDefault()));
        Assert.False(File.Exists(Path + ".tmp"));
    }
}
