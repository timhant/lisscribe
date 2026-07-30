using System.Runtime.InteropServices;

namespace Lisscribe.Platform.Native;

/// <summary>
/// 访问令牌相关声明，只为一件事：读进程的**强制完整性级别**，用来判 UIPI 会不会把
/// 我们的 SendInput 悄悄丢掉。
///
/// 四条都仍由 advapi32.dll 导出（实际转发到 kernelbase.dll），照 MSDN 的库名写。
/// </summary>
internal static partial class Advapi32
{
    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenIntegrityLevel。</summary>
    public const int TokenIntegrityLevel = 25;

    // 完整性级别的 RID。数值大小即高低，可直接比较——UIPI 正是按这个序判「低的不许向高的发输入」。
    public const uint SECURITY_MANDATORY_UNTRUSTED_RID = 0x0000;
    public const uint SECURITY_MANDATORY_LOW_RID = 0x1000;
    public const uint SECURITY_MANDATORY_MEDIUM_RID = 0x2000;
    public const uint SECURITY_MANDATORY_HIGH_RID = 0x3000;
    public const uint SECURITY_MANDATORY_SYSTEM_RID = 0x4000;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    /// <summary>
    /// 头一次调用**故意**传 <c>tokenInformation = 0, tokenInformationLength = 0</c> 来问所需缓冲区
    /// 大小：那是 MSDN 给的惯用法，此时函数返回 FALSE 且 GetLastError 为
    /// ERROR_INSUFFICIENT_BUFFER(122)，而 <paramref name="returnLength"/> 已被填好。
    /// 调用方据此分配缓冲区再问一次。
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    /// <summary>
    /// 返回的是**指向 SID 内部的指针**，不是值，且失败时返回的内容未定义。
    /// 拿它去 <c>Marshal.ReadInt32</c> 之前必须确认 SID 有效且下标在范围内——
    /// 越界读到的是一个不可 catch 的 AccessViolation。
    /// </summary>
    [LibraryImport("advapi32.dll")]
    public static partial nint GetSidSubAuthority(nint sid, uint subAuthority);

    /// <summary>同上，返回的是指向那一个字节计数的指针。</summary>
    [LibraryImport("advapi32.dll")]
    public static partial nint GetSidSubAuthorityCount(nint sid);
}

/// <summary>
/// <c>TOKEN_MANDATORY_LABEL</c>。<see cref="Label"/> 里的 <c>Sid</c> 指向的正是
/// GetTokenInformation 那同一个缓冲区的内部——**缓冲区一释放这个指针就悬空**。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_MANDATORY_LABEL
{
    public SID_AND_ATTRIBUTES Label;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SID_AND_ATTRIBUTES
{
    public nint Sid;
    public uint Attributes;
}
