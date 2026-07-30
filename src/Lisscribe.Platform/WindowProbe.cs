using System.Runtime.InteropServices;
using Lisscribe.Core.Abstractions;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Platform;

/// <summary>
/// 前台窗口获取与 UIPI 完整性级别比对。
/// 为何必须比对：非提权进程向更高完整性级别的窗口 SendInput，输入会被 UIPI 悄悄丢掉，
/// 而 SendInput 的返回值与 GetLastError 都不指示这件事——用户会报「时好时坏的玄学 bug」。
/// 焦点在任务管理器 / 管理员 PowerShell / regedit 上时就是这个场景。判出「不可注入」之后
/// 上层改走剪贴板兜底。
/// </summary>
public sealed class WindowProbe : IWindowProbe
{
    /// <summary>
    /// 令牌信息缓冲区的上限。TOKEN_MANDATORY_LABEL 加一个只有一位子颁发机构的 SID 不过几十字节。
    /// 设这道闸不是为了省内存，是因为 <c>size</c> 来自一次预期会失败的调用：万一它带回一个荒唐
    /// 的值，<c>AllocHGlobal((int)size)</c> 要么吃掉一大块内存，要么在强转时溢出成负数。
    /// </summary>
    private const uint MaxTokenInformationBytes = 4096;

    /// <summary>构造时已过 <see cref="SafeLoggerExtensions.Safe"/>：本类由 SessionController 的
    /// 队列 worker 调用，那里一次逃逸的异常就把 worker 永久 fault 掉。</summary>
    private readonly ILogger _logger;
    private readonly Lazy<uint> _ownIntegrityLevel;

    public WindowProbe(ILogger<WindowProbe> logger)
        : this(logger, ownIntegrityLevelForTest: null)
    {
    }

    /// <summary>
    /// <paramref name="ownIntegrityLevelForTest"/> 只为测试而开，生产恒为 null。
    /// </summary>
    /// <remarks>
    /// 不开这条缝，本类存在的全部理由就是无覆盖的：把 <c>targetLevel &lt;= ownLevel</c> 改成
    /// <c>&gt;=</c>、<c>==</c> 或干脆 <c>return true</c>，整套测试一条都不红——因为几条
    /// CanInjectInto 测试大多走早退分支（0 句柄／无效句柄／已销毁），剩下一条是同进程的
    /// <c>x &lt;= x</c>，对比较方向完全不敏感。
    /// 为什么用「注入自身级别」而不是「真起一个提权进程」：真提权要么弹 UAC 框把测试挂住，
    /// 要么依赖本机一项非默认的注册表设置——两条路都让这条测试的存亡取决于跑它的机器。
    /// 注入之后目标那侧仍是真的：拿本进程的真窗口、走真的 <see cref="GetIntegrityLevel"/> 解析，
    /// 只有「我们自己是什么级别」是给定的。UIPI 的真实行为另由手工清单 B 组守着。
    /// </remarks>
    internal WindowProbe(ILogger<WindowProbe> logger, uint? ownIntegrityLevelForTest)
    {
        _logger = logger.Safe();

        // 这一处没有回归钉子，且造不出来：触发它需要 ReadOwnIntegrityLevel 真的抛
        // （AllocHGlobal 撞 OOM 之类），从外部制造不了。记在明处，别当它被钉住了。
        //
        // PublicationOnly，不是默认的 ExecutionAndPublication——默认模式会缓存异常：
        // 一旦 ReadOwnIntegrityLevel 抛过一次，此后每次访问都重抛同一个陈年异常 →
        // 被 CanInjectInto 的 catch 接住 → 恒返回 false → 进程余生只走剪贴板，而现场早就没了。
        _ownIntegrityLevel = ownIntegrityLevelForTest is { } given
            ? new Lazy<uint>(given)
            : new Lazy<uint>(ReadOwnIntegrityLevel, LazyThreadSafetyMode.PublicationOnly);
    }

    public nint GetForegroundWindow() => User32.GetForegroundWindow();

    public bool CanInjectInto(nint window)
    {
        // 整段包住：本方法几乎全是 P/Invoke 包装，而调用它的是 SessionController 的队列 worker
        // ——那里一次逃逸的异常就把 worker 永久 fault 掉，此后每次识别都挂起且无任何日志。
        // 判不出来时答「不可注入」是安全的那一侧：上层退到剪贴板兜底，用户至少拿得到文字。
        try
        {
            return CanInjectIntoCore(window);
        }
        catch (Exception ex)
        {
            // 这一句自己也不许抛：它一抛，异常照样逃到 worker 上，上面那段注释描述的后果原样发生
            // ——防线看着在，其实没修。保障来自构造函数里那道 Safe()。
            _logger.LogWarning(
                ex, "判定窗口 0x{Window:X} 能否注入时发生未预期异常，按不可注入处理", window);

            return false;
        }
    }

    private bool CanInjectIntoCore(nint window)
    {
        if (window == nint.Zero)
        {
            return false;
        }

        // 句柄无效（含「窗口刚刚被用户关掉」这条真实时序）时返回 0，顺带把 processId 留成 0。
        var threadId = User32.GetWindowThreadProcessId(window, out var processId);

        if (threadId == 0 || processId == 0)
        {
            return false;
        }

        var process = Kernel32.OpenProcess(
            Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, inheritHandle: false, processId);

        if (process == nint.Zero)
        {
            // 连 PROCESS_QUERY_LIMITED_INFORMATION 都打不开，说明对方的保护级别高于我们
            // （受保护进程、别的用户会话），注入同样不会成功。
            // 对同进程的窗口这一支不会走到：进程对自己恒有这个权限。
            _logger.LogDebug(
                "无法打开进程 {ProcessId}（错误码 {Error}），判定为不可注入",
                processId, Marshal.GetLastWin32Error());

            return false;
        }

        try
        {
            var targetLevel = GetIntegrityLevel(process);

            if (targetLevel is null)
            {
                return false;
            }

            // UIPI 的判据是「低的不许向高的发」，同级放行。故 <= 而不是 <。
            return targetLevel.Value <= _ownIntegrityLevel.Value;
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    private uint ReadOwnIntegrityLevel()
    {
        // GetCurrentProcess 给的是伪句柄，不必也不许关。
        var level = GetIntegrityLevel(Kernel32.GetCurrentProcess());

        if (level is not null)
        {
            return level.Value;
        }

        // 这里是「猜一个值」还是「诚实报错」的取舍，选前者，理由须写明。
        // 一、这条路基本走不到：进程对自己的令牌恒有 TOKEN_QUERY 权限。
        // 二、猜错的方向是安全的那一侧——但这句只在「本进程实际不低于 Medium」时成立：真实级别
        //     若高于 Medium（提权运行），把 High 的目标误判成不可注入 → 退剪贴板，用户看得见；
        //     真实级别若低于 Medium（Low / AppContainer 沙箱）则反过来——把 Medium 的目标误判成
        //     可注入，SendInput 静默失败，用户说了半天一个字也没出来。严格保守的回落值是
        //     SECURITY_MANDATORY_UNTRUSTED_RID。仍取 Medium：本程序是普通桌面 exe，装
        //     WH_KEYBOARD_LL 与发 SendInput 在 AppContainer 里本来就做不成，没有任何受支持的配置
        //     能让它跑在 Medium 以下；而取 Untrusted 万一这条路真被走到，产品就永久退化成只会
        //     用剪贴板。
        // 三、诚实报错（抛异常）等于让上层每次注入前都可能崩，换来的信息量还不如这一条日志。
        _logger.LogWarning(
            "读不出本进程的完整性级别，按 Medium 处理；对提权窗口会保守地判为不可注入并退剪贴板");

        return Advapi32.SECURITY_MANDATORY_MEDIUM_RID;
    }

    /// <summary>
    /// 读一个进程令牌的强制完整性级别 RID。取不到返回 null。
    /// <c>internal</c> 而非 private 是为了让测试直接钉住这段解析：<see cref="CanInjectInto"/>
    /// 对同进程窗口做的是 <c>x &lt;= x</c>，只要本方法返回的是个稳定的值、返回什么垃圾都成立，
    /// 于是不单独验它就等于完全不验。
    /// </summary>
    internal static uint? GetIntegrityLevel(nint process)
    {
        if (process == nint.Zero
            || !Advapi32.OpenProcessToken(process, Advapi32.TOKEN_QUERY, out var token)
            || token == nint.Zero)
        {
            return null;
        }

        try
        {
            // 头一次调用故意不给缓冲区，只为问大小。它预期返回 FALSE（ERROR_INSUFFICIENT_BUFFER），
            // 故返回值不判——判的是它有没有填出大小来。真正失败（句柄无效之类）时原生代码一个
            // 字节也不写，而 LibraryImport 生成的桩把 out 参数零初始化过，于是 size 仍为 0，
            // 一样落到下面这道守卫上。
            Advapi32.GetTokenInformation(
                token, Advapi32.TokenIntegrityLevel, nint.Zero, 0, out var size);

            if (size == 0 || size > MaxTokenInformationBytes)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                if (!Advapi32.GetTokenInformation(
                        token, Advapi32.TokenIntegrityLevel, buffer, size, out _))
                {
                    return null;
                }

                var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);

                if (label.Label.Sid == nint.Zero)
                {
                    return null;
                }

                // 这几道判断里只有两道是承重的，别把另两道也当成防线：GetSidSubAuthorityCount 与
                // GetSidSubAuthority 不校验入参、只做加法（传 0 分别返回 sid+1 与 sid+8），
                // 故下面两处 `== nint.Zero` 永不成立。真正挡住 AccessViolation 的是：
                //   一、上面那道 `label.Label.Sid == nint.Zero`——否则这里就成了
                //       Marshal.ReadByte(1)，当场 AV；
                //   二、下面那道 `count == 0`——否则 (uint)(count - 1) 下溢成 0xFFFFFFFF，
                //       算出的地址在 sid 往后约 16GB 处。
                // 两处空指针判留着不要钱，但别把它们说成承重的。
                var countPointer = Advapi32.GetSidSubAuthorityCount(label.Label.Sid);

                if (countPointer == nint.Zero)
                {
                    return null;
                }

                var count = Marshal.ReadByte(countPointer);

                if (count == 0)
                {
                    return null;
                }

                // 完整性级别是 SID 的最后一位子颁发机构（S-1-16-&lt;RID&gt;）。
                var ridPointer = Advapi32.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));

                return ridPointer == nint.Zero ? null : (uint)Marshal.ReadInt32(ridPointer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }
}
