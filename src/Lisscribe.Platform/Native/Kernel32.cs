using System.Runtime.InteropServices;

namespace Lisscribe.Platform.Native;

internal static partial class Kernel32
{
    /// <summary>
    /// 必须是原生线程 id：<see cref="User32.PostThreadMessage"/> 认的是它，不是
    /// <c>Thread.ManagedThreadId</c>。两者取值范围重叠，传错不报错，只是把消息投进一个不相干的
    /// 线程（或谁也收不到）。
    /// </summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    /// <summary>
    /// 判 UIPI 只需要「问得出目标进程的令牌」这一点点权限，故不要 PROCESS_QUERY_INFORMATION。
    /// 后者对更高完整性级别的进程会直接失败，而那恰恰是我们最想问清楚的一类目标。
    /// </summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>返回伪句柄（恒为 -1），不占内核对象，不许 CloseHandle。</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    /// <summary>
    /// <c>SetClipboardData</c> 收的句柄，MSDN 明载须由 <c>GlobalAlloc</c> 带这个标志分配。
    /// 别图省事换成 <c>Marshal.StringToHGlobalUni</c>：它在本机跑得通，但那同时依赖 Windows
    /// 堆函数的实现细节（Global / Local / 进程默认堆三者合一）与 .NET 的
    /// <c>Marshal.AllocHGlobal</c> 落在哪个堆上，两者都不在契约里，任一边变了症状就是「某天堆
    /// 损坏」——现场与成因隔得极远。
    /// </summary>
    public const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalFree(nint memory);

    /// <summary>
    /// 对 GMEM_MOVEABLE 的块，返回值与句柄不是同一个值：写入必须用这里返回的地址，而交给
    /// <c>SetClipboardData</c> 的必须是句柄。搞反了写进去的是一段谁也没申请过的内存。
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalLock(nint memory);

    /// <summary>
    /// 返回值不可当成败判据：锁计数减到 0 时它返回 FALSE，而那正是正常收尾的样子
    /// （此时 <c>GetLastError()</c> 为 NO_ERROR；ERROR_NOT_LOCKED(158) 是「本来就没锁」另一支）。
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);

    /// <summary>
    /// 一块的字节数。用来给「从别的进程写进来的剪贴板块里读字符串」定上限：「以空字符结尾」是
    /// 各家应用的约定、不是系统校验的东西，写入方少写一个终止符，<c>Marshal.PtrToStringUni</c>
    /// 就会扫过分配边界读野内存，撞不上 0 即 AccessViolation——catch 不到的进程终结。
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(nint memory);
}
