using Lisscribe.Core.Abstractions;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Core.Asr;

/// <summary>
/// 按 <c>asr.engine</c> 选一档引擎 new 出来。
///
/// <para>
/// 判断住在这里而不是组合根里：<c>AppServices</c> 刻意不含任何判断逻辑，它只负责「谁在谁之前」。
/// 规矩放这儿才有测试盯得住，也才不必为了验一条 switch 去把整个组合根立起来。
/// </para>
/// </summary>
public static class AsrEngineFactory
{
    /// <param name="config">已过 <see cref="ConfigValidator"/> 的 asr 配置段。</param>
    public static IAsrEngine Create(AsrSection config, ILoggerFactory loggerFactory) => config.Engine switch
    {
        AsrEngines.SenseVoice =>
            new SenseVoiceEngine(config, loggerFactory.CreateLogger<SenseVoiceEngine>()),

        AsrEngines.Qwen3Asr =>
            new Qwen3AsrEngine(config, loggerFactory.CreateLogger<Qwen3AsrEngine>()),

        // 校验器已经拦过一道，走到这里说明两处的规则漂移了。**不许静默回落到默认档**：
        // 那会让用户以为自己在跑高精度档，而实际上跑的是 SenseVoice，
        // 唯一的线索是识别质量「好像没变好」——查无可查。
        _ => throw new ArgumentOutOfRangeException(
            nameof(config), config.Engine,
            $"未知的识别引擎，只支持 {string.Join(" / ", AsrEngines.All)}"),
    };
}
