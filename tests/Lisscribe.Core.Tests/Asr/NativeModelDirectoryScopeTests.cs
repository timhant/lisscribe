using Lisscribe.Core.Asr;
using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 工作目录变通本身的守卫。整机效果由 <see cref="NonAsciiModelPathTests"/> 钉（那条要真模型），
/// 本文件钉的是它什么时候动手、动完还不还原。
/// </summary>
public sealed class NativeModelDirectoryScopeTests
{
    /// <summary>
    /// <b>绝大多数装机走的是这条。</b>纯 ASCII 路径下必须一动不动——为少数人的路径
    /// 给所有人切一次进程级状态，是拿全体的稳定去换。
    /// </summary>
    [Fact]
    public void Enter_WithAsciiPath_TouchesNothingAndHandsBackTheFullPath()
    {
        var dir = CreateDirectory("lisscribe-ascii");
        var before = Directory.GetCurrentDirectory();

        using (var scope = NativeModelDirectoryScope.Enter(dir))
        {
            Assert.False(scope.ChangedWorkingDirectory);
            Assert.Equal(dir, scope.RootForNative);
            Assert.Equal(before, Directory.GetCurrentDirectory());
        }

        Assert.Equal(before, Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// 非 ASCII 路径下：工作目录切过去，交出去的根是**空串**——按它拼出来就是纯文件名，
    /// 而纯文件名是原生唯一认得的形状。
    /// </summary>
    [Fact]
    public void Enter_WithNonAsciiPath_SwitchesTheWorkingDirectoryAndHandsBackAnEmptyRoot()
    {
        var dir = CreateDirectory("lisscribe-中文");
        var before = Directory.GetCurrentDirectory();

        using (var scope = NativeModelDirectoryScope.Enter(dir))
        {
            Assert.True(scope.ChangedWorkingDirectory);
            Assert.Equal(string.Empty, scope.RootForNative);
            Assert.Equal(dir, Directory.GetCurrentDirectory());
        }

        Assert.Equal(before, Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// 空根拼出来必须是纯文件名。这一条是两档 <c>BuildRecognizerConfig</c> 赖以成立的前提，
    /// 而它只是 <see cref="Path.Combine(string, string)"/> 的一个行为——写下来才不会被人
    /// 「顺手改成 Path.GetFullPath」冲掉。
    /// </summary>
    [Fact]
    public void UnderAnEmptyRoot_BothEnginesGetBareFileNames()
    {
        Assert.Equal(
            Qwen3ModelPaths.ConvFrontendFileName,
            Qwen3ModelPaths.Under(string.Empty).ConvFrontendFile);

        Assert.Equal(
            AsrModelPaths.ModelFileName,
            AsrModelPaths.Under(string.Empty).ModelFile);
    }

    /// <summary>
    /// 相对路径按 <see cref="AppContext.BaseDirectory"/> 展开，**不是**按当前工作目录——
    /// 开机自启时工作目录是 system32，按它展开必然找不到模型。
    /// </summary>
    [Fact]
    public void Enter_WithRelativePath_ExpandsAgainstTheBaseDirectoryNotTheWorkingDirectory()
    {
        using var scope = NativeModelDirectoryScope.Enter("./models/sense-voice");

        Assert.Equal(
            Path.GetFullPath("./models/sense-voice", AppContext.BaseDirectory),
            scope.RootForNative);
    }

    /// <summary>
    /// <b>切换失败必须把闸门放掉。</b>工作目录只有一格，进出由一个进程级信号量守着；
    /// 失败那条路上不放，下一次加载就永远挂在那儿——而挂住比报错难查得多。
    /// 连做两次：第二次若挂住，测试会超时而不是红，同样能发现，但第一时间看到的是「卡住」。
    /// </summary>
    [Fact]
    public void Enter_WhenTheDirectoryIsMissing_ReleasesTheGateSoTheNextAttemptStillRuns()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"lisscribe-缺失-{Guid.NewGuid():N}");

        Assert.ThrowsAny<IOException>(() => NativeModelDirectoryScope.Enter(missing));
        Assert.ThrowsAny<IOException>(() => NativeModelDirectoryScope.Enter(missing));

        // 阴性对照：闸门真放开了，正常路径还能进得去。少了这句，
        // 「两次都抛」在闸门泄漏时同样成立——第二次抛的是别的。
        var usable = CreateDirectory("lisscribe-中文-放闸");

        using var scope = NativeModelDirectoryScope.Enter(usable);

        Assert.True(scope.ChangedWorkingDirectory);
    }

    private static string CreateDirectory(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(dir);

        return dir;
    }
}
