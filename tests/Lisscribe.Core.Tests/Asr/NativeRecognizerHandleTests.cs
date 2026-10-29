using Lisscribe.Core.Asr;
using Lisscribe.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 两档共用骨架上的一道防线：**原生识别器没建起来时必须当场报错，不许假装加载成功。**
///
/// <para>
/// <b>起因是一次真实事故</b>（2026-07-29，把 Debug 目录复制到另一台机器）：日志上写着
/// 「"Qwen3Asr" 加载完成，耗时 49ms」——而同档本机实测 4.2–4.7s，49ms 连把那 982MB 读一遍
/// 都不够。随后每次按热键说话都在 sherpa 内部炸 <c>NullReferenceException</c>，
/// 堆栈止于 <c>OfflineRecognizerResult..ctor</c>，不含任何原因。
/// </para>
///
/// <para>
/// <b>机理</b>：<c>SherpaOnnxCreateOfflineRecognizer</c> 在配置校验失败时把原因打到 stderr，
/// 然后返回 <c>NULL</c>；而 C# 包装的 <c>OfflineRecognizer</c> 构造器**不检查返回值**，
/// 照样把空句柄裹成对象交出来，一次异常也没有。GUI 程序没有控制台，那两行 stderr
/// 用户和日志都看不见，于是「加载完成」是假的、真错误推迟到每次识别。
/// </para>
///
/// <para>
/// <b>触发器为什么是「空配置」。</b>当初这条测试拿中文路径触发——那时中文路径必然让原生
/// 判文件不存在。<see cref="NativeModelDirectoryScope"/> 把那条路修好之后，中文路径反而能
/// 正常加载了，再拿它当触发器只会让原生真去解析假 onnx，<c>Ort::Exception</c> 会带走整个
/// 测试进程。空配置则是干净的：sherpa 在**校验**那一步就返回 NULL（实测流句柄 = 0，
/// 进程照常活着），ORT 一次也碰不到。
/// </para>
/// </summary>
[Collection(ProcessWorkingDirectoryCollection.Name)]
public sealed class NativeRecognizerHandleTests
{
    private static readonly string ModelDir =
        Path.Combine(Path.GetTempPath(), $"lisscribe-empty-config-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_WhenTheNativeRecognizerCannotBeCreated_ThrowsInsteadOfReportingSuccess()
    {
        await using var engine = new EmptyConfigEngine(
            new AsrSection("EmptyConfig", ModelDir, "cpu"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.LoadAsync(CancellationToken.None));

        // 消息得点名「在哪找的」。只说一句「原生创建失败」等于把用户丢回本次事故的起点
        // ——那正是他当时面对的东西。
        Assert.Contains(ModelDir, ex.Message);

        // 假成功的另一半：状态机只问 IsLoaded，它为 true 就会照常派活。
        Assert.False(engine.IsLoaded);
    }

    /// <summary>
    /// 测试专用档：把模型文件检查换成空实现，好让坏配置能走到原生那一步。
    ///
    /// <para>
    /// <b>缝只决定输入，不代替执行</b>：跳过的是「文件齐不齐」这个前置判断，
    /// 被测的 <c>EnsureNativeHandleUsable</c> 与它上下的每一句照常跑。
    /// </para>
    /// </summary>
    private sealed class EmptyConfigEngine(AsrSection config)
        : OfflineRecognizerEngine(config, NullLogger.Instance)
    {
        public override string EngineName => "EmptyConfig";

        protected override void ValidateModelFiles(string modelDir)
        {
        }

        /// <summary>全空的配置，sherpa 必判「Errors in config」并返回 NULL。</summary>
        protected override OfflineRecognizerConfig BuildRecognizerConfig(string nativeRoot) => new();
    }
}
