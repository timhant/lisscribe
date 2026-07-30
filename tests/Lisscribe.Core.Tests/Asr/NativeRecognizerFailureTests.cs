using Lisscribe.Core.Asr;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 「原生创建失败」那句话本身的守卫。整机行为由 <see cref="NativeRecognizerHandleTests"/> 钉，
/// 本文件只钉措辞里的判断。
/// </summary>
public sealed class NativeRecognizerFailureTests
{
    /// <summary>
    /// 配置里写的常是相对路径 <c>./models/qwen3-asr</c>，对用户毫无信息量；
    /// 真出问题的往往是 exe 所在的那一段。不展开就报，等于什么都没说。
    /// </summary>
    [Fact]
    public void Describe_WithRelativeModelDir_ReportsTheExpandedPath()
    {
        var message = NativeRecognizerFailure.Describe("./models/qwen3-asr");

        Assert.Contains(ModelDirectory.Canonical("./models/qwen3-asr"), message);

        // 阴性对照：原样回显那个相对路径就等于没展开。少了这句，
        // 「消息里同时带着原文和全路径」也能让上一句绿。
        Assert.DoesNotContain("./models/qwen3-asr", message);
    }

    /// <summary>
    /// 真实原因只在原生的 stderr 里，而 GUI 程序没有控制台——不告诉用户去哪看，
    /// 他手上就只剩一句「失败了」。
    /// </summary>
    [Fact]
    public void Describe_PointsAtWhereTheRealReasonIs()
    {
        var message = NativeRecognizerFailure.Describe(@"C:\lisscribe\models\qwen3-asr");

        Assert.Contains("stderr", message);
        Assert.Contains("命令行", message);
    }
}
