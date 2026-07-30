namespace Lisscribe.Core.Diagnostics;

/// <summary>启动被拦下的原因。<see cref="None"/> 表示可以继续启动。</summary>
public enum StartupBlockReason
{
    None,

    /// <summary>同一登录会话内已有一个实例在跑。</summary>
    AlreadyRunning,

    /// <summary>单实例检查本身没能做成——不等于「已有实例」，见 <see cref="StartupPreflight"/>。</summary>
    SingleInstanceCheckFailed,

    /// <summary>程序目录写不进去，config.json 与日志都将无处安放。</summary>
    ProgramDirectoryNotWritable,
}

/// <summary>
/// 一次启动前置检查的结论。构造函数私有、只留两个工厂：位置构造函数若是 public，
/// <c>new(StartupBlockReason.None, null, null)</c> 这种「可以启动、却没有守卫」的组合就构造得出来，
/// 落进调用方的 <c>using</c> 里安静无害，而单实例保护已经整个失效且毫无征兆。
/// </summary>
public sealed record StartupPreflightResult
{
    private StartupPreflightResult(StartupBlockReason reason, string? message, SingleInstanceGuard? guard)
    {
        Reason = reason;
        Message = message;
        Guard = guard;
    }

    public StartupBlockReason Reason { get; }

    /// <summary>可以直接弹给用户看的一句话；<see cref="CanStart"/> 为真时是 null。</summary>
    public string? Message { get; }

    /// <summary>
    /// 取到的单实例守卫，**调用方负责 Dispose**，须持有到进程退出。
    /// 凡 <see cref="CanStart"/> 为假，此处必为 null 且内部已释放。
    /// </summary>
    public SingleInstanceGuard? Guard { get; }

    public bool CanStart => Reason == StartupBlockReason.None;

    public static StartupPreflightResult Allowed(SingleInstanceGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        return new StartupPreflightResult(StartupBlockReason.None, null, guard);
    }

    public static StartupPreflightResult Blocked(StartupBlockReason reason, string message)
    {
        if (reason == StartupBlockReason.None)
        {
            throw new ArgumentException("被拦下的结论不能拿 None 当原因。", nameof(reason));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return new StartupPreflightResult(reason, message, null);
    }
}

/// <summary>
/// 启动前置检查：单实例 + 程序目录可写性。只判断与描述，不负责显示——弹窗是 exe 那一层的事，
/// 而本项目的核心库要留在 net10.0 上不依赖任何 Windows 类型。
/// </summary>
public static class StartupPreflight
{
    public static StartupPreflightResult Run(string instanceName, string programDirectory)
    {
        // 次序是有讲究的：先互斥、后可写性。第二个实例应当尽快退出，不必再去碰文件系统
        // ——那次探测要在程序目录里真建一个文件再删掉，而此刻另一个实例正跑在同一个目录上。
        SingleInstanceGuard? guard;

        try
        {
            if (!SingleInstanceGuard.TryAcquire(instanceName, out guard))
            {
                return StartupPreflightResult.Blocked(
                    StartupBlockReason.AlreadyRunning,
                    "lisscribe 已在运行。请查看托盘图标；若托盘里找不到它（explorer 重启会吞掉图标），" +
                    "请在任务管理器中结束 Lisscribe.App 后重试。");
            }
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            // 同名的非互斥内核对象占了这个名字。这不是「已有实例在跑」而是「这道检查做不成了」，
            // 两者处置不同；更要紧的是别让它从启动路径上冒出去——用户看到的会是进程凭空消失，
            // 无窗口、无提示、无退出信息，于是再点一次、再死一次。
            return StartupPreflightResult.Blocked(
                StartupBlockReason.SingleInstanceCheckFailed,
                $"单实例检查无法完成：名字 \"{instanceName}\" 已被系统中另一个同名的非互斥内核对象占用" +
                $"（{ex.Message}）。lisscribe 无法确认自己是否已在运行，故拒绝启动——" +
                "两个实例同时抢热键会让一次说话录两遍、上屏两遍。");
        }
        catch (UnauthorizedAccessException ex)
        {
            // 名字被同类型对象占着而 DACL 不让本进程碰。必须与上一支分开说，成因完全不同：最可能是
            // 本程序的另一份正以管理员身份运行——高完整性进程建的内核对象，中完整性进程改不动
            // （NO_WRITE_UP），而 .NET 的 Mutex 构造请求里含 MUTEX_MODIFY_STATE。manifest 写的是
            // asInvoker，但那拦不住用户自己右键「以管理员身份运行」。
            return StartupPreflightResult.Blocked(
                StartupBlockReason.SingleInstanceCheckFailed,
                $"单实例检查无法完成：名字 \"{instanceName}\" 已被占用，且系统拒绝本进程访问它" +
                $"（{ex.Message}）。最可能的成因是 lisscribe 的另一份正以管理员身份运行；" +
                "请在任务管理器中结束那一份后重试。" +
                "（请勿改用管理员身份启动 lisscribe：提权后它反而无法向普通窗口上屏。）");
        }

        var report = DirectoryWritabilityProbe.Probe(programDirectory);

        if (!report.IsWritable)
        {
            // 先取到互斥体、再发现目录不可写。忘了释放的话，本进程退出前那段窗口里第三个实例会被
            // 误判成「已在运行」——用户看到的是「明明什么都没开着，它却说已在运行」。
            guard!.Dispose();

            // 提示原样用探测报告：它已带齐目录、原因与补救建议，另编一句等于把那些信息扔掉。
            return StartupPreflightResult.Blocked(
                StartupBlockReason.ProgramDirectoryNotWritable, report.FailureMessage!);
        }

        return StartupPreflightResult.Allowed(guard!);
    }
}
