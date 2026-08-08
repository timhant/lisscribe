using System.Runtime.InteropServices;
using Lisscribe.Core.Abstractions;
using Lisscribe.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Platform;

/// <summary>
/// 剪贴板 + Ctrl+V 注入。整段原子写入，长文本快得多；代价是污染剪贴板。
/// <c>targetWindow</c> 为 <c>nint.Zero</c> 时只写剪贴板不发 Ctrl+V——状态机用这个约定表示
/// 「焦点已切换 / 目标不可注入 / 结果过期，只复制给用户」。
/// </summary>
/// <remarks>
/// 已知限制一：只保存与恢复 CF_UNICODETEXT。用户剪贴板里若是图片、文件列表、带格式的 HTML，
/// <c>restoreClipboard = true</c> 也恢复不回来，那份内容会被本程序的文本永久顶掉且没有提示。
/// 全格式保存要处理句柄型格式与延迟渲染，代价远超收益。故 <c>restoreClipboard</c> 的承诺只有
/// 一句「你剪贴板里原来的那段文字会还给你」，不是「你的剪贴板不受影响」。
/// <para>
/// 已知限制二：返回 Ok 不等于目标应用真的粘上了。实测约 1% 的 Ctrl+V 会「按键到了、控件也发了
/// WM_PASTE，可一个字符都没进去」，隔 200ms 补发一次立刻成功——目标控件在 WM_PASTE 里自己调的
/// OpenClipboard 那一刻失败了（剪贴板历史服务在每次变化后都要抓一次剪贴板去快照）。
/// 这一层没有任何办法察觉：粘贴是目标应用做的，收不到完成信号，<c>SendInput</c> 只报「按键已
/// 发出」。这是「剪贴板 + Ctrl+V」这条路子的固有代价，也是 <c>output.mode</c> 默认取
/// <c>"SendInput"</c> 的一条实打实的理由。
/// </para>
/// <para>
/// 并发前提：调用方须自行串行化。本类无锁，两次并发的 <see cref="InjectAsync"/> 会互相踩掉
/// 对方存下的 <c>previous</c>，把用户的旧内容永久换成另一次识别的结果。
/// </para>
/// </remarks>
public sealed class ClipboardTextInjector : ITextInjector
{
    /// <summary>
    /// 剪贴板同一时刻只能被一个进程打开。浏览器、Office 等会频繁短暂持有它，首次失败是常态，
    /// 必须重试。10 × 20ms ≈ 200ms 上限：再长就压过了整条「松开热键到文本上屏」的预算。
    /// </summary>
    private const int OpenRetryCount = 10;

    private const int OpenRetryDelayMs = 20;

    /// <summary>owner 句柄已失效。重试对它毫无意义，见 <see cref="TryOpenClipboard"/>。</summary>
    private const int ERROR_INVALID_WINDOW_HANDLE = 1400;

    /// <summary>剪贴板正被别的进程（拿真 HWND）打开着。只有这一个值得重试。</summary>
    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>
    /// 等目标应用读完剪贴板再恢复旧内容。这是个竞态，没有可靠的完成信号。
    /// </summary>
    /// <remarks>
    /// 这个值没有实测支撑，别把它当成量出来的：改成 0 之后自建 EDIT 靶子上连跑全绿。
    /// 复现不出的原因也说得通——EDIT 在 WM_PASTE 里同步读完剪贴板才返回，而真正会中招的是
    /// 异步取剪贴板的应用（浏览器、Electron 把粘贴丢到别的线程或进程），那类靶子这一层测不到。
    /// 代价是实打实的：它吃掉端到端延迟预算里的 150ms，且只在 <c>restoreClipboard = true</c>
    /// 时付。留着是保守选择，不是结论——要么在真实应用上量出一条曲线来定，要么把恢复改成
    /// 事件驱动，不要再靠这句话。
    /// </remarks>
    private const int PasteSettleDelayMs = 150;

    /// <summary>V 键。左 Ctrl 用 <see cref="VirtualKey.VK_LCONTROL"/>，好让
    /// <see cref="InputBuilder.CreateVirtualKeyInput"/> 从扫描码表里取到 0x1D，与硬件同形。</summary>
    private const ushort VK_V = 0x56;

    private readonly IWindowProbe _windowProbe;
    private readonly bool _restoreClipboard;
    private readonly Func<nint> _clipboardOwner;

    /// <summary>构造时已过 <see cref="SafeLoggerExtensions.Safe"/>。</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// 带上 <paramref name="windowProbe"/> 是必要的，不是镀金：Ctrl+V 同样走 SendInput，一样会被
    /// UIPI 静默丢掉，也一样会打进焦点已经切走的那个窗口。少了它，对着提权窗口注入的结果是
    /// 用户的剪贴板被顶掉、粘贴没发生、返回 Ok——三件坏事凑齐。
    /// <see cref="ITextInjector"/> 的三条不变量写的是「实现方须保证」。
    /// </summary>
    /// <param name="clipboardOwner">
    /// 交给 <see cref="User32.OpenClipboard"/> 当 owner 的窗口句柄，每次取用时求值
    /// （窗口可能比本对象晚建，也可能被重建）。
    /// <para>
    /// 不写死 0 的理由见 <see cref="User32.OpenClipboard"/>：MSDN 明写 owner 为 NULL 会导致
    /// <c>SetClipboardData</c> 失败（实测本机不失败，但那正是最不该赌的一类差异），
    /// 而传真 HWND 还顺带买到写入期间的独占。
    /// </para>
    /// <para>
    /// 委托不得抛，也不得阻塞：抛了按 <c>nint.Zero</c> 处理并记一条 Warning
    /// （见 <see cref="ResolveClipboardOwner"/>）。返回已销毁的句柄也是允许的——
    /// <c>OpenClipboard</c> 会给 err 1400，当场改用 NULL owner 继续，不空等那一整圈重试。
    /// 返回 <c>nint.Zero</c> 同样允许，行为回落到「与文档相反但实测可用」那一套。
    /// </para>
    /// </param>
    public ClipboardTextInjector(
        IWindowProbe windowProbe,
        bool restoreClipboard,
        Func<nint> clipboardOwner,
        ILogger<ClipboardTextInjector> logger)
    {
        _windowProbe = windowProbe;
        _restoreClipboard = restoreClipboard;
        _clipboardOwner = clipboardOwner;
        _logger = logger.Safe();
    }

    public async Task<InjectResult> InjectAsync(
        string text, nint targetWindow, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return InjectResult.Ok;
        }

        // 取消语义与 SendInputTextInjector 一致：只在动手之前认一次。在收尾那段响应取消的话，
        // 取消一次剪贴板就永远停在本程序的文本上。
        if (cancellationToken.IsCancellationRequested)
        {
            return InjectResult.Failed;
        }

        if (targetWindow != nint.Zero)
        {
            // 两道守卫都在写剪贴板之前：注入注定发不出去时，连用户的剪贴板都不该动。
            if (!_windowProbe.CanInjectInto(targetWindow))
            {
                _logger.LogWarning(
                    "目标窗口 0x{Window:X} 完整性级别高于自身，Ctrl+V 会被 UIPI 丢掉，放弃注入", targetWindow);

                return InjectResult.IntegrityLevelTooLow;
            }

            var foreground = _windowProbe.GetForegroundWindow();

            if (foreground != targetWindow)
            {
                _logger.LogInformation(
                    "临粘贴前焦点已从 0x{Target:X} 切至 0x{Current:X}，放弃注入", targetWindow, foreground);

                return InjectResult.FocusChanged;
            }
        }

        // targetWindow == 0 时不留旧内容：那条路上用户要的就是新内容留在剪贴板里，
        // 恢复旧内容等于把识别结果扔了。
        var previous = _restoreClipboard && targetWindow != nint.Zero ? TryReadClipboardText() : null;

        var written = TryWriteClipboardText(text);

        if (written != ClipboardWriteOutcome.Ok)
        {
            // 按失败原因分流。没碰到剪贴板的那一类什么也不用做——不分流的话，会在最常见的
            // 「打不开剪贴板」现场白白再花一轮 10×20ms 去写 previous（触发前提正是刚 Open 失败过，
            // 几乎必然再失败），还打一句在那条路上是错的「剪贴板可能已被清空」。
            // FailedAfterEmptying 这一支要 SetClipboardData 失败才走得到，从外部造不出来，
            // 故无自动化覆盖——明写在此，不当它被钉住了。
            if (
                written == ClipboardWriteOutcome.FailedAfterEmptying
                && previous is not null
                && TryWriteClipboardText(previous) != ClipboardWriteOutcome.Ok
                )
            {
                _logger.LogError("剪贴板已被清空、新内容没写进去，且用户的旧内容也没能放回去");
            }

            return InjectResult.Failed;
        }

        if (targetWindow == nint.Zero)
        {
            return InjectResult.Ok;
        }

        // 先释放物理按下的修饰键，否则发出去的是 Ctrl+Alt+V——按住 Alt 时 Ctrl+V 一个字符也
        // 粘不进去。
        if (!InputSender.TryReleasePhysicalModifiers(_logger))
        {
            return InjectResult.Failed;
        }

        // 菜单模式下 Ctrl+V 同样一个字符也粘不进去，而这条路一样返回 Ok。查在释放修饰键之后：
        // 上面那一串补发的 Alt↑ 本身就可能把目标推进菜单模式。
        InputSender.TryLeaveMenuMode(_windowProbe, targetWindow, _logger);

        if (!TrySendCtrlV())
        {
            // 粘贴没发出去时不恢复旧内容：文本还留在剪贴板上，用户至少能自己按一次 Ctrl+V。
            return InjectResult.Failed;
        }

        if (previous is not null)
        {
            // 刻意不把 cancellationToken 传进去：这一段是收尾义务，上限 150ms，关停多等这一会儿
            // 远好过把用户的剪贴板永久留在本程序的文本上。
            await Task.Delay(PasteSettleDelayMs, CancellationToken.None).ConfigureAwait(false);

            if (TryWriteClipboardText(previous) != ClipboardWriteOutcome.Ok)
            {
                // 恢复失败不算注入失败，故不改返回值：用户要的那件事（文本上屏）已经做成了。
                // 这里返回 Failed 会让状态机报「上屏失败」并把会话置为 Failed——那是拿一句假话
                // 去纠正另一句假话。
                _logger.LogWarning("粘贴已完成，但恢复剪贴板旧内容失败，用户的剪贴板停在识别结果上");
            }
        }

        return InjectResult.Ok;
    }

    private bool TrySendCtrlV()
    {
        INPUT[] inputs =
        [
            InputBuilder.CreateVirtualKeyInput(VirtualKey.VK_LCONTROL, keyUp: false),
            InputBuilder.CreateVirtualKeyInput(VK_V, keyUp: false),
            InputBuilder.CreateVirtualKeyInput(VK_V, keyUp: true),
            InputBuilder.CreateVirtualKeyInput(VirtualKey.VK_LCONTROL, keyUp: true),
        ];

        return InputSender.TrySend(inputs, _logger, "发送 Ctrl+V");
    }

    /// <summary>
    /// 分配与填充全在打开剪贴板之前，这个顺序是承重的。
    /// </summary>
    /// <remarks>
    /// 一、写成「Open → Empty → Alloc → Fill → Set」的话，<c>GlobalAlloc</c> 或 <c>GlobalLock</c>
    /// 一旦失败，<c>EmptyClipboard</c> 已经跑过了——本程序的文本没进去、用户的旧内容也没了，
    /// 而调用方那句「注定发不出去时连用户的剪贴板都不动」当场失真。
    /// 二、顺带把持有剪贴板的时长砍到只剩 Empty + Set 两次调用。剪贴板是全机独占资源，
    /// 占着的每一毫秒都是别人 <c>OpenClipboard</c> 拿 err 5 的窗口——那 1% 的静默粘贴失败
    /// 正是这一族。
    /// </remarks>
    private unsafe ClipboardWriteOutcome TryWriteClipboardText(string text)
    {
        // 末尾要留一个 U+0000：CF_UNICODETEXT 是以空字符结尾的。
        var bytes = (nuint)((text.Length + 1) * sizeof(char));
        var handle = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, bytes);

        if (handle == nint.Zero)
        {
            _logger.LogError("GlobalAlloc {Bytes} 字节失败，Win32 错误码 {Error}",
                (ulong)bytes, Marshal.GetLastWin32Error());

            return ClipboardWriteOutcome.FailedBeforeTouchingClipboard;
        }

        // 所有权用 try/finally 兜，而不是每条失败路径各写一次 GlobalFree：漏一处就是每次失败
        // 泄漏一块，而这中间还夹着一次托管调用（求值 owner 委托），它一抛就绕过所有那些
        // GlobalFree。移交给系统之后把 published 置真即可。
        var published = false;
        try
        {
            var address = Kernel32.GlobalLock(handle);

            if (address == nint.Zero)
            {
                _logger.LogError("GlobalLock 失败，Win32 错误码 {Error}", Marshal.GetLastWin32Error());

                return ClipboardWriteOutcome.FailedBeforeTouchingClipboard;
            }

            try
            {
                var destination = new Span<char>((void*)address, text.Length + 1);

                text.AsSpan().CopyTo(destination);

                destination[text.Length] = '\0';
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }

            if (!TryOpenClipboard())
            {
                return ClipboardWriteOutcome.FailedBeforeTouchingClipboard;
            }

            try
            {
                if (!User32.EmptyClipboard())
                {
                    _logger.LogError("清空剪贴板失败，Win32 错误码 {Error}", Marshal.GetLastWin32Error());

                    // EmptyClipboard 失败即剪贴板没被清空，用户的旧内容还在原处。
                    return ClipboardWriteOutcome.FailedBeforeTouchingClipboard;
                }

                if (User32.SetClipboardData(User32.CF_UNICODETEXT, handle) == nint.Zero)
                {
                    _logger.LogError("SetClipboardData 失败，Win32 错误码 {Error}", Marshal.GetLastWin32Error());

                    // 只有这一条路会把用户的旧内容连坐掉：Empty 已经跑过，我们的又没进去。
                    return ClipboardWriteOutcome.FailedAfterEmptying;
                }

                published = true;

                return ClipboardWriteOutcome.Ok;
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
        finally
        {
            // 成功后所有权归系统，不能 Free；其余每一条路（含托管异常）都由这里收。
            if (!published)
            {
                Kernel32.GlobalFree(handle);
            }
        }
    }

    /// <summary>
    /// 写剪贴板失败时剪贴板处于什么状态——这决定了要不要尽力恢复用户的旧内容。
    /// 只回 bool 的话，「没碰剪贴板」与「已清空但没写进去」两种失败会被一视同仁。
    /// </summary>
    private enum ClipboardWriteOutcome
    {
        Ok,

        /// <summary>分配、锁定、打开、清空任一步失败。剪贴板内容原封未动。</summary>
        FailedBeforeTouchingClipboard,

        /// <summary><c>EmptyClipboard</c> 已执行而 <c>SetClipboardData</c> 失败：剪贴板此刻是空的。</summary>
        FailedAfterEmptying,
    }

    private unsafe string? TryReadClipboardText()
    {
        if (!TryOpenClipboard())
        {
            // 读不到旧内容不是失败：大不了事后不恢复。真正要报的是写失败。
            _logger.LogDebug("读旧剪贴板内容时打不开剪贴板，本次不做恢复");

            return null;
        }

        try
        {
            var handle = User32.GetClipboardData(User32.CF_UNICODETEXT);

            if (handle == nint.Zero)
            {
                // 剪贴板是空的，或者里面是图片/文件之类没有文本形态的东西。
                return null;
            }

            // 上限来自 GlobalSize，不靠终止符。这块是别的进程写进来的，无上限的 PtrToStringUni
            // 一旦碰上没有终止符的块，就会一路扫过分配边界读野内存，撞不上 0 就是
            // AccessViolation——catch 不到，进程当场没。
            // 说准它今天防住了什么：实测系统不允许这种块存在（会把最后一个码元改写成 U+0000），
            // 故这个上限一次也用不上、去掉结果一模一样。留着的理由是那个保证属于系统的实现细节、
            // 没写进契约，而代价只是一次 GlobalSize。
            var size = Kernel32.GlobalSize(handle);

            if (size < sizeof(char))
            {
                return null;
            }

            var address = Kernel32.GlobalLock(handle);

            if (address == nint.Zero)
            {
                return null;
            }

            try
            {
                var maxChars = (int)Math.Min(size / sizeof(char), int.MaxValue);
                var span = new ReadOnlySpan<char>((void*)address, maxChars);

                // 块内有终止符就截到那里；没有就整块当内容——两种都不越界。
                var end = span.IndexOf('\0');

                return new string(end >= 0 ? span[..end] : span);
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    private bool TryOpenClipboard()
    {
        var owner = ResolveClipboardOwner();

        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (User32.OpenClipboard(owner))
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();

            // 1400 是重试一万次也不会变的错，而重试圈整整值 10×20ms，读一圈写一圈就是 400ms。
            // 拿一个已销毁的 HWND 去开，整次注入白扔 618ms 才报失败，日志还说「被其他进程占用」，
            // 排障的人会去找一个根本不存在的占用者。
            // 这不是理论路径：owner 由 Func 每次求值，理由正是「窗口可能被重建」，那就必然存在
            // 拿到旧句柄的一瞬。回落到 NULL owner 是本来就允许的形态，故当场换过去继续。
            if (error == ERROR_INVALID_WINDOW_HANDLE && owner != nint.Zero)
            {
                _logger.LogWarning(
                    "owner 窗口 0x{Owner:X} 已失效（Win32 错误码 {Error}），改用 NULL owner 继续",
                    owner, error);

                owner = nint.Zero;

                continue;
            }

            if (attempt == OpenRetryCount - 1)
            {
                // 措辞按错误码来，别把什么错都说成「被占用」。
                _logger.LogError(
                    error == ERROR_ACCESS_DENIED
                        ? "打开剪贴板失败：被其他进程占用（Win32 错误码 {Error}），已重试 {Count} 次"
                        : "打开剪贴板失败，Win32 错误码 {Error}，已重试 {Count} 次",
                    error, OpenRetryCount);
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    /// <summary>
    /// 求值 owner 委托，本方法绝不抛。
    /// 委托是组合根交进来的外部代码，它一抛异常会从 <see cref="InjectAsync"/> 逃到
    /// <c>SessionController</c> 那道全局 catch 上——队列不会死，但用户只看到「处理识别结果时
    /// 出错，详见日志」，而真因不过是一个窗口句柄取不到。按 <c>nint.Zero</c> 处理即可：
    /// 那是本来就支持的回落形态。
    /// </summary>
    private nint ResolveClipboardOwner()
    {
        nint owner;

        try
        {
            owner = _clipboardOwner();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "取剪贴板 owner 窗口时抛出异常，本次按 NULL owner 处理");
            return nint.Zero;
        }

        if (owner == nint.Zero)
        {
            _logger.LogDebug(
                "没有可用的 owner 窗口，以 NULL owner 打开剪贴板；此形态与 MSDN 明文相反（见 User32.OpenClipboard）");
        }

        return owner;
    }
}
