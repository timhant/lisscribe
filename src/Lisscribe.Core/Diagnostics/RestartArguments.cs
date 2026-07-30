using System.Globalization;

namespace Lisscribe.Core.Diagnostics;

/// <summary>
/// 命令行里那个 <c>--wait-for &lt;pid&gt;</c>：换档重启时旧进程拼给新进程，
/// 告诉它「先等我死透，再去抢单实例互斥体」。
///
/// <para>
/// <b>认不出来时一律当没传</b>，不报错、不拒绝启动。认不出的后果最多是新进程不等旧的、
/// 撞上单实例检查报一句「已在运行」——那是句真话；而为一个畸形参数拒绝启动，
/// 等于让用户手打错一个字就开不了程序。
/// </para>
/// </summary>
public static class RestartArguments
{
    /// <summary>拼参数与解析参数两头必须用同一个字面量，故此处公开。</summary>
    public const string WaitForOption = "--wait-for";

    /// <summary>
    /// 从命令行里找出要等的那个 pid。
    /// </summary>
    /// <returns>找到且合法时为 true；其余一律 false，<paramref name="pid"/> 为 0。</returns>
    public static bool TryParseWaitForPid(string[] args, out int pid)
    {
        pid = 0;

        if (args is null)
        {
            return false;
        }

        // 位置无关地找。写死「第 0 个元素是选项」的话，日后前面多加任何一个参数就整个失灵，
        // 而失灵的样子是「重启偶尔不生效」——最难查的那种。
        // 末位的选项没有值可取，故上界是 Length - 1。
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], WaitForOption, StringComparison.Ordinal))
            {
                continue;
            }

            return TryParsePid(args[i + 1], out pid);
        }

        return false;
    }

    /// <summary>
    /// pid 必为正。0 与负数不是「某个已经退出的进程」，是垃圾输入——放行的话
    /// <c>Process.GetProcessById</c> 会拿它去抛，于是走进「旧进程已经没了」那条路，
    /// 把一次错误伪装成一切正常。
    /// </summary>
    private static bool TryParsePid(string value, out int pid)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0)
        {
            pid = 0;

            return false;
        }

        return true;
    }
}
