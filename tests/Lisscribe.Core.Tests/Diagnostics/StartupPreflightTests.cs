using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Lisscribe.Core.Diagnostics;
using Xunit;

namespace Lisscribe.Core.Tests.Diagnostics;

public sealed class StartupPreflightTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-preflight").FullName;

    private static string UniqueName() => $"lisscribe-preflight-{Guid.NewGuid():N}";

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Run_OnCleanEnvironment_AllowsStartAndYieldsGuard()
    {
        var result = StartupPreflight.Run(UniqueName(), _dir);

        using (result.Guard)
        {
            Assert.True(result.CanStart);
            Assert.Equal(StartupBlockReason.None, result.Reason);
            Assert.NotNull(result.Guard);
            Assert.Null(result.Message);
        }
    }

    [Fact]
    public void Run_WhenAnotherInstanceHoldsMutex_ReportsAlreadyRunning()
    {
        // 互斥体必须由**另一条线程**持有：同线程持有时它可重入，Run 照样能取到，
        // 于是这条测试会以错误的理由通过，且把整个单实例检查删掉它仍然绿。
        var name = UniqueName();
        SingleInstanceGuard? owner = null;
        using var holder = new ForeignThreadHolder(
            () => SingleInstanceGuard.TryAcquire(name, out owner),
            () => owner!.Dispose());
        Assert.True(holder.Acquired, "前提不成立：占位线程没能取到互斥体");

        var result = StartupPreflight.Run(name, _dir);

        Assert.False(result.CanStart);
        Assert.Equal(StartupBlockReason.AlreadyRunning, result.Reason);
        Assert.Null(result.Guard);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Run_WhenNameCollidesWithNonMutexObject_ReportsCheckFailedInsteadOfThrowing()
    {
        // 交接文档 §5.1 第 2 笔账：名字撞上同名的**非互斥**内核对象时，Mutex 构造函数抛
        // WaitHandleCannotBeOpenedException。启动路径上的未捕获异常是最坏的失败形态——
        // 进程凭空消失，无窗口、无提示，用户再点一次、再死一次。
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "此路径仅 Windows 可验：Linux 上具名 Semaphore 抛 PlatformNotSupportedException，" +
            "根本摆不出「同名非互斥内核对象」这个局面。");

        // CA1416 只认 OperatingSystem.* 形式的平台守卫，认不出上面那句 Assert.SkipUnless。
        if (OperatingSystem.IsWindows())
        {
            AssertNameCollisionIsReported();
        }
    }

    [SupportedOSPlatform("windows")]
    private void AssertNameCollisionIsReported()
    {
        var name = UniqueName();
        // 先用一个信号量占住实现会用到的那个完整名字（含 Local\ 前缀）。
        using var squatter = new Semaphore(1, 1, $"Local\\{name}");

        var result = StartupPreflight.Run(name, _dir);

        Assert.False(result.CanStart);
        Assert.Equal(StartupBlockReason.SingleInstanceCheckFailed, result.Reason);
        Assert.Null(result.Guard);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Run_WhenNameIsSquattedWithDenyAcl_ReportsCheckFailedInsteadOfThrowing()
    {
        // 账 ② 的另一半。名字被同一个类型（互斥体）占住、但 DACL 拒绝访问时，
        // Mutex 构造函数抛的是 UnauthorizedAccessException ——
        // 它既不是 IOException（SingleInstanceGuard.CreateMutex 的 catch 接不到），
        // 也不是 WaitHandleCannotBeOpenedException（Run 的 catch 接不到），于是原样冲出启动路径，
        // 与账 ② 本要消灭的「双击一闪即没」是同一种失败形态，只是换了个异常类型。
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "此路径仅 Windows 可验：内核对象 DACL 是 Windows 概念，Linux 上摆不出这个局面。");

        // CA1416 只认 OperatingSystem.* 形式的平台守卫，认不出上面那句 Assert.SkipUnless。
        if (OperatingSystem.IsWindows())
        {
            AssertDenyAclSquatIsReported();
        }
    }

    [SupportedOSPlatform("windows")]
    private void AssertDenyAclSquatIsReported()
    {
        var name = UniqueName();

        // 只拒 Modify，不拒 FullControl：这正是强制完整性等级的 NO_WRITE_UP 对
        // 「中完整性进程去开高完整性进程所建对象」所做的事，而 .NET 的 Mutex 构造请求里含
        // MUTEX_MODIFY_STATE。翻成生产场景就是：用户曾右键「以管理员身份运行」开过一份
        // （asInvoker 拦不住这个动作），那份还跑着，此后普通双击启动就走到这里。
        // 三种拒绝形状（FullControl / Modify / Synchronize）实测都抛 UnauthorizedAccessException，
        // 这里挑最贴近真实场景的那个。
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            WindowsIdentity.GetCurrent().User!, MutexRights.Modify, AccessControlType.Deny));
        using var squatter = MutexAcl.Create(false, $"Local\\{name}", out _, security);

        var result = StartupPreflight.Run(name, _dir);

        Assert.False(result.CanStart);
        Assert.Equal(StartupBlockReason.SingleInstanceCheckFailed, result.Reason);
        Assert.Null(result.Guard);
        Assert.NotNull(result.Message);
        // 与「非互斥同名对象」那一支必须分岔：这一支最可能的成因是本程序的另一份以管理员身份在跑，
        // 而那条提示对用户的可操作建议完全不同（去结束那一份，而不是琢磨谁占了这个名字）。
        //
        // 断言挑的这两个词都经过变异验证。**不要**改回只断言「管理员」三个字：
        // 消息末尾那句「请勿改用管理员身份启动」本身就含这三个字，于是把成因整句删掉测试照样绿
        // ——实测过，那是一条空断言。
        Assert.Contains("拒绝", result.Message);              // 只有这一支是「访问被拒」
        Assert.Contains("管理员身份运行", result.Message);      // 成因诊断（末尾那句警告写的是「启动」）
    }

    [Fact]
    public void Run_WhenDirectoryDoesNotExist_ReportsNotWritableWithPath()
    {
        var missing = Path.Combine(_dir, "nope");

        var result = StartupPreflight.Run(UniqueName(), missing);

        Assert.False(result.CanStart);
        Assert.Equal(StartupBlockReason.ProgramDirectoryNotWritable, result.Reason);
        Assert.Null(result.Guard);
        // 锁死「原样转发探测报告」：自己另编一句提示，等于把探测器查到的真因扔掉。
        Assert.Equal(DirectoryWritabilityProbe.Probe(missing).FailureMessage, result.Message);
        Assert.Contains(missing, result.Message);
        // **提示里不许出现「以管理员身份运行」**，那与 app.manifest 自相矛盾：提权会让本进程
        // 变成高完整性级别，UIPI 是双向的，届时反而注入不进普通窗口——教用户提权即教他把上屏弄坏。
        Assert.DoesNotContain("管理员", result.Message);
    }

    [Fact]
    public void Run_WhenBlockedByUnwritableDirectory_ReleasesMutex()
    {
        // 关键不变量：这条路径是「先取到互斥体、再发现目录不可写」，忘了释放的话，
        // 进程退出前那段窗口里第三个实例会被误判成「已在运行」。
        var name = UniqueName();

        var result = StartupPreflight.Run(name, Path.Combine(_dir, "nope"));
        Assert.False(result.CanStart);

        // 必须在另一条线程上复查：互斥体对持有线程可重入，在测试线程上再取一次必然成功，
        // 那样即便实现根本没释放，这条测试也照样绿——正是它要防的那种空测。
        SingleInstanceGuard? reacquired = null;
        using var holder = new ForeignThreadHolder(
            () => SingleInstanceGuard.TryAcquire(name, out reacquired),
            () => reacquired!.Dispose());

        Assert.True(holder.Acquired, "被阻断后互斥体仍被占着：第三个实例会被误判成「已在运行」");
    }

    [Fact]
    public void Run_WithInvalidInstanceName_Throws()
    {
        // 实例名是调用方写死的编译期常量，传错就是程序员错误。吞掉它只会让缺陷藏进
        // 一句「启动检查失败」里，而单实例保护实际上已经失效。
        Assert.Throws<ArgumentException>(() => StartupPreflight.Run("lisscribe\\bad", _dir));
    }
}
