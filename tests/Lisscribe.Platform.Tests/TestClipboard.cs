using System.Runtime.InteropServices;

namespace Lisscribe.Platform.Tests;

/// <summary>
/// 测试自己的剪贴板读写，**刻意与 <see cref="ClipboardTextInjector"/> 零共用**。
///
/// 预言机的取样范围必须独立于被测对象。
/// 拿注入器自己的 <c>TryReadClipboardText</c> 去验它自己写进去的东西，等于让被测方出题：
/// 句柄分配、锁定、编码任何一处两边一起错，读回来照样对得上，测试全绿而剪贴板里是垃圾。
/// 这里的每一条 P/Invoke 都是照 MSDN 单独写的。
/// </summary>
internal static unsafe partial class TestClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// 读不到文本时返回 null——「剪贴板是空的」与「剪贴板里是图片」在这里不作区分，
    /// 因为用它的断言只关心文本。
    /// </summary>
    public static string? ReadText()
    {
        if (!TryOpen())
        {
            throw new InvalidOperationException("测试打不开剪贴板，无法取样");
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == nint.Zero)
            {
                return null;
            }

            // 实测本机上 GetClipboardData 返回的一律是固定内存（handle == GlobalLock(handle)，
            // 无论写入方用的是 GMEM_MOVEABLE 还是固定内存），直接当指针用也读得对。
            // 仍照 MSDN 走 GlobalLock：那个相等是这一版 Windows 的实现细节，不是契约。
            //
            // 上限同样取自 GlobalSize，理由与生产那侧一字不差：这块是别的进程写进来的，
            // 终止符是约定而非系统校验，无上限地扫过去撞不上 0 就是 AccessViolation。
            var size = GlobalSize(handle);
            if (size < sizeof(char))
            {
                return null;
            }

            var address = GlobalLock(handle);
            if (address == nint.Zero)
            {
                return null;
            }

            try
            {
                var span = new ReadOnlySpan<char>(
                    (void*)address, (int)Math.Min(size / sizeof(char), int.MaxValue));
                var end = span.IndexOf('\0');
                return new string(end >= 0 ? span[..end] : span);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static void WriteText(string text)
    {
        if (!TryOpen())
        {
            throw new InvalidOperationException("测试打不开剪贴板，无法布置前提");
        }

        try
        {
            EmptyClipboard();

            var bytes = (nuint)((text.Length + 1) * 2);
            var handle = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (handle == nint.Zero)
            {
                throw new InvalidOperationException("GlobalAlloc 失败");
            }

            var address = GlobalLock(handle);
            if (address == nint.Zero)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("GlobalLock 失败");
            }

            fixed (char* source = text)
            {
                Buffer.MemoryCopy(source, (void*)address, (long)bytes, text.Length * 2L);
            }

            ((char*)address)[text.Length] = '\0';
            GlobalUnlock(handle);

            if (SetClipboardData(CF_UNICODETEXT, handle) == nint.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                GlobalFree(handle);
                throw new InvalidOperationException($"SetClipboardData 失败，Win32 错误码 {error}");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>剪贴板此刻的 owner 窗口。系统自己记的账，与注入器零共用，故可当预言机。</summary>
    public static nint GetOwner() => GetClipboardOwner();

    /// <summary>
    /// 写一块**没有终止符**的 CF_UNICODETEXT：块正好 <paramref name="text"/> 那么大，
    /// 一个 U+0000 也没有。
    ///
    /// 这是别的应用真会写出来的东西——「以空字符结尾」是各家的约定，不是系统校验的。
    /// 拿它当输入，就能确定性地验「读的一侧有没有按 GlobalSize 定界」：
    /// 定了界读回恰好 N 个字符；没定界就一路扫过分配边界，读到的是块后面的堆内容
    /// （撞不上 0 就是不可 catch 的 AccessViolation）。
    /// </summary>
    public static void WriteTextWithoutTerminator(string text)
    {
        if (!TryOpen())
        {
            throw new InvalidOperationException("测试打不开剪贴板，无法布置前提");
        }

        try
        {
            EmptyClipboard();

            var bytes = (nuint)(text.Length * 2);
            var handle = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (handle == nint.Zero)
            {
                throw new InvalidOperationException("GlobalAlloc 失败");
            }

            var address = GlobalLock(handle);
            if (address == nint.Zero)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("GlobalLock 失败");
            }

            fixed (char* source = text)
            {
                Buffer.MemoryCopy(source, (void*)address, (long)bytes, text.Length * 2L);
            }

            GlobalUnlock(handle);

            if (SetClipboardData(CF_UNICODETEXT, handle) == nint.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                GlobalFree(handle);
                throw new InvalidOperationException($"SetClipboardData 失败，Win32 错误码 {error}");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static void Clear()
    {
        if (!TryOpen())
        {
            return;
        }

        try
        {
            EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpen()
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (OpenClipboard(nint.Zero))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint GlobalSize(nint memory);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardOwner();
}

/// <summary>
/// 把用户的剪贴板在整个注入型测试集合前后各存取一次。
///
/// **剪贴板是全机共享资源**：跑测试期间用户正复制着的东西会被我们顶掉。这个 fixture
/// 让损失止于「一整轮测试」而不是「永久」。
///
/// **说清楚它救不了什么**：只存得回 CF_UNICODETEXT。用户剪贴板里若是图片、文件列表、
/// 带格式的 HTML，跑完就没了——这与 <see cref="ClipboardTextInjector"/> 的
/// <c>restoreClipboard</c> 是同一条已知限制，代价一致，理由也一致（全格式保存要处理
/// 位图/HDROP 这类句柄型格式与延迟渲染，代价远超收益）。
/// </summary>
public sealed class ClipboardGuard : IDisposable
{
    private readonly string? _saved;

    public ClipboardGuard() => _saved = TestClipboard.ReadText();

    public void Dispose()
    {
        if (_saved is not null)
        {
            TestClipboard.WriteText(_saved);
        }
        else
        {
            // 本来就没有文本内容时清空，免得把测试用的字符串留在用户剪贴板里。
            TestClipboard.Clear();
        }
    }
}
