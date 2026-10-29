using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 静态守卫：本程序集里凡直接改当前目录的测试文件，必须归入 <see cref="ProcessWorkingDirectoryCollection"/>。
/// 当前目录是进程级全局状态，漏标的类会与读它的测试并行互踩，表现为约 1/15 的偶发红。
/// </summary>
public sealed class WorkingDirectoryCollectionGuardTests
{
    // 模式拼接而成，使本文件源码里不出现连续的调用形态，守卫不会判到自己。
    private static readonly string[] CallPatterns =
    [
        "Directory." + "SetCurrentDirectory(",
        "NativeModelDirectoryScope." + "Enter(",
    ];

    [Fact]
    public void EveryTestFileThatChangesTheWorkingDirectory_IsInTheNonParallelCollection()
    {
        var testsDir = Path.Combine(FindRepositoryRoot(), "tests", "Lisscribe.Core.Tests");

        // 取样范围由守卫自己遍历文件系统得出，不依赖任何手维护的名单。
        var sources = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToList();

        // 阴性对照：取样非空，且本文件自己在其中（否则遍历范围有误，下面的断言是空的）。
        Assert.Contains(sources, p => Path.GetFileName(p) == nameof(WorkingDirectoryCollectionGuardTests) + ".cs");

        var collectionTag = "[Collection(" + nameof(ProcessWorkingDirectoryCollection) + "." + nameof(ProcessWorkingDirectoryCollection.Name) + ")]";

        var violations = sources
            .Select(path => (path, text: File.ReadAllText(path)))
            .Where(f => CallPatterns.Any(f.text.Contains))
            .Where(f => !f.text.Contains(collectionTag))
            .Select(f => Path.GetRelativePath(testsDir, f.path))
            .Order()
            .ToList();

        Assert.True(
            violations.Count == 0,
            "以下测试文件会改当前目录，却没有标 " + collectionTag + "：" + string.Join("、", violations));
    }

    /// <summary>obj、bin 下可能有生成的 .cs，不算源码。</summary>
    private static bool IsBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return parts.Contains("obj") || parts.Contains("bin");
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "lisscribe.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("从 " + AppContext.BaseDirectory + " 往上没找到 lisscribe.sln。");
    }
}
