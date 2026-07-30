using Lisscribe.Core.Diagnostics;
using Xunit;

namespace Lisscribe.Core.Tests.Diagnostics;

public sealed class DirectoryWritabilityProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-probe").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void IsWritable_ForTempDirectory_IsTrue()
    {
        Assert.True(DirectoryWritabilityProbe.IsWritable(_dir));
    }

    [Fact]
    public void IsWritable_LeavesNoProbeFileBehind()
    {
        DirectoryWritabilityProbe.IsWritable(_dir);

        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void IsWritable_ForNonexistentDirectory_IsFalse()
    {
        Assert.False(DirectoryWritabilityProbe.IsWritable(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void IsWritable_ForFilePath_IsFalse()
    {
        var file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, "{}");

        Assert.False(DirectoryWritabilityProbe.IsWritable(file));
    }

    [Fact]
    public void IsWritable_ForBlankPath_IsFalse()
    {
        Assert.False(DirectoryWritabilityProbe.IsWritable(string.Empty));
        Assert.False(DirectoryWritabilityProbe.IsWritable("   "));
    }

    [Fact]
    public void IsWritable_CanBeCalledRepeatedly()
    {
        Assert.True(DirectoryWritabilityProbe.IsWritable(_dir));
        Assert.True(DirectoryWritabilityProbe.IsWritable(_dir));
    }

    [Fact]
    public void Probe_WhenWritable_HasNoFailureMessage()
    {
        var report = DirectoryWritabilityProbe.Probe(_dir);

        Assert.True(report.IsWritable);
        Assert.Null(report.FailureMessage);
    }

    [Fact]
    public void Probe_WhenDirectoryMissing_ReportsPathAndReason()
    {
        var missing = Path.Combine(_dir, "nope");

        var report = DirectoryWritabilityProbe.Probe(missing);

        Assert.False(report.IsWritable);
        // 「配置存不下」是启动期就该说清的事：说是哪个目录、为什么不行。
        // 只回一个 false，用户看到的就是「保存没反应」，无从下手。
        Assert.NotNull(report.FailureMessage);
        Assert.Contains(missing, report.FailureMessage);
        Assert.Contains("不存在", report.FailureMessage);
    }

    [Fact]
    public void Probe_WhenPermissionDenied_ReportsPathAndDistinctReason()
    {
        // 这正是把程序装进 Program Files 的下场：目录在，就是写不了。
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "Windows 上造只读目录要改 ACL，非本单元测试的职责；此路径由 Windows 端手工验收。");
        Assert.SkipWhen(
            Environment.IsPrivilegedProcess,
            "以 root 运行时文件权限位形同虚设，无法构造不可写目录。");

        // CA1416 只认 OperatingSystem.* 形式的平台守卫，认不出上面那句 Assert.SkipWhen，
        // 故再包一层；走到这里时它恒为真。
        if (!OperatingSystem.IsWindows())
        {
            AssertReadOnlyDirectoryIsReported();
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private void AssertReadOnlyDirectoryIsReported()
    {
        var locked = Directory.CreateDirectory(Path.Combine(_dir, "locked"));
        File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var report = DirectoryWritabilityProbe.Probe(locked.FullName);

            Assert.False(report.IsWritable);
            Assert.NotNull(report.FailureMessage);
            Assert.Contains(locked.FullName, report.FailureMessage);
            Assert.Contains("权限", report.FailureMessage);
        }
        finally
        {
            // 不还原权限，测试类的 Dispose 删不掉这棵目录树。
            File.SetUnixFileMode(
                locked.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
