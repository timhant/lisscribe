using Lisscribe.Core.Asr;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 配置窗关掉之后，该拿识别引擎怎么办。
///
/// <para>
/// <b>这条规矩是真机故障逼出来的。</b>模型目录不对 → 启动时引擎加载失败 → 用户打开配置窗
/// 把目录改对 → 保存 → <b>什么也没发生</b>。组合根关窗时只重装热键，引擎那头一动不动，
/// 于是用户最自然的自救手段完全无效，且没有任何提示说「得重启」。
/// </para>
///
/// <para>
/// 判断放在这里而不是组合根里，是因为组合根**刻意不含判断逻辑**（见 <c>AppServices</c> 类注释）：
/// 它只会把 new 出来的东西按顺序接上。规矩住在这儿才有测试盯得住。
/// </para>
/// </summary>
public sealed class EngineReloadPolicyTests
{
    private const string SenseVoiceDir = "./models/sense-voice";

    [Fact]
    public void WhenTheEngineNeverLoaded_ItRetries_EvenIfTheDirectoryDidNotChange()
    {
        // 目录没改也要重试：用户很可能是去把模型下下来了，而不是改路径。
        // 只在「目录变了」时重试，那种最常见的自救方式照样无效。
        Assert.Equal(
            EngineReloadAction.Reload,
            Decide(engineLoaded: false, SenseVoiceDir, SenseVoiceDir));

        Assert.Equal(
            EngineReloadAction.Reload,
            Decide(engineLoaded: false, SenseVoiceDir, "D:/models"));
    }

    [Fact]
    public void WhenTheEngineIsLoadedAndTheDirectoryDidNotChange_ItDoesNothing()
    {
        // 每关一次配置窗就重新加载一次 229MB 的模型，那是把「保存」变成两秒的卡顿。
        Assert.Equal(
            EngineReloadAction.Nothing,
            Decide(engineLoaded: true, SenseVoiceDir, SenseVoiceDir));
    }

    [Fact]
    public void TheSamePlaceWrittenTwoWays_StillCountsAsUnchanged()
    {
        // "./x" 与 "x" 是同一处。按原样比字符串会把它判成改过，于是每次保存都弹一句
        // 「重启后生效」——喊狼来了喊到用户不再看那行提示为止。
        Assert.Equal(
            EngineReloadAction.Nothing,
            Decide(engineLoaded: true, SenseVoiceDir, "models/sense-voice"));
    }

    [Fact]
    public void WhenTheEngineIsLoadedAndTheDirectoryChanged_ItAsksForARestart()
    {
        // 换目录得换一个引擎实例，而它已经被状态机攥在手里了。就地热换要么泄掉旧的原生句柄
        // （324MB 常驻），要么在识别半途抽走它（catch 不到的 AccessViolation）。
        // **说实话比两样都强。**
        Assert.Equal(
            EngineReloadAction.RestartRequired,
            Decide(engineLoaded: true, SenseVoiceDir, "D:/models/other"));
    }

    // ---- 换引擎 ------------------------------------------------------------

    [Fact]
    public void ChangingTheEngine_AlwaysAsksForARestart()
    {
        Assert.Equal(
            EngineReloadAction.RestartRequired,
            EngineReloadPolicy.Decide(
                engineLoaded: true,
                AsrEngines.SenseVoice, SenseVoiceDir,
                AsrEngines.Qwen3Asr, "./models/qwen3-asr"));
    }

    /// <summary>
    /// **换引擎这条必须排在「没加载过就重试」前面。**
    ///
    /// <para>
    /// 两档是两个类。引擎实例已经 new 出来交给状态机了，重新 <c>LoadAsync</c> 一次
    /// 只会把**同一个** SenseVoiceEngine 再加载一遍——用户明明选了 Qwen3，程序却继续跑
    /// SenseVoice，日志还写着「加载完成」。这比报一句「须重启」坏得多：它是假话。
    /// </para>
    ///
    /// <para>
    /// 而「加载失败之后去配置窗换一档」恰恰是最可能发生的一次换档：SenseVoice 模型没下好，
    /// 用户想着「那我用另一个」。故这一条不是边角情形。
    /// </para>
    /// </summary>
    [Fact]
    public void ChangingTheEngine_AsksForARestart_EvenWhenNothingEverLoaded()
    {
        Assert.Equal(
            EngineReloadAction.RestartRequired,
            EngineReloadPolicy.Decide(
                engineLoaded: false,
                AsrEngines.SenseVoice, SenseVoiceDir,
                AsrEngines.Qwen3Asr, "./models/qwen3-asr"));
    }

    [Fact]
    public void ChangingTheEngine_AsksForARestart_EvenIfTheDirectoryHappensToBeTheSame()
    {
        // 有人把两份权重堆在同一个目录里。目录没变不代表引擎没变，
        // 只看目录的话这次换档会被判成 Nothing，静默无效。
        Assert.Equal(
            EngineReloadAction.RestartRequired,
            EngineReloadPolicy.Decide(
                engineLoaded: true,
                AsrEngines.SenseVoice, "D:/models/all",
                AsrEngines.Qwen3Asr, "D:/models/all"));
    }

    /// <summary>引擎没换时，判断与从前一字不差——上面那四条走的就是这条路。</summary>
    private static EngineReloadAction Decide(bool engineLoaded, string currentDir, string newDir) =>
        EngineReloadPolicy.Decide(
            engineLoaded,
            AsrEngines.SenseVoice, currentDir,
            AsrEngines.SenseVoice, newDir);
}
