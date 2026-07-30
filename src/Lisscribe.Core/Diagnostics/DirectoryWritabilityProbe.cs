namespace Lisscribe.Core.Diagnostics;

/// <summary>
/// 一次可写性探测的结果。不可写时带上「哪个目录、为什么」，供启动阶段直接示人。
/// </summary>
/// <param name="Directory">被探测的目录。</param>
/// <param name="FailureReason">不可写的原因；可写时为 null。</param>
public sealed record DirectoryWritabilityReport(string Directory, string? FailureReason)
{
    public bool IsWritable => FailureReason is null;

    /// <summary>可写时为 null；否则是一句可以直接弹给用户看的话。</summary>
    public string? FailureMessage => FailureReason is null
        ? null
        : $"目录不可写：{Directory}（{FailureReason}）。" +
          "config.json 与日志都存在程序目录旁，请把程序装到可写的位置（勿装在 Program Files），" +
          "或为该目录授予写权限。";
}

/// <summary>
/// 程序目录可写性探测。config.json 一律放程序目录旁，不做 %APPDATA% 回退；
/// 不可写时须在启动阶段就说清楚，而不是等用户改完配置点保存、然后什么也没发生。
/// </summary>
public static class DirectoryWritabilityProbe
{
    public static bool IsWritable(string directory) => Probe(directory).IsWritable;

    public static DirectoryWritabilityReport Probe(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new DirectoryWritabilityReport(directory ?? string.Empty, "路径为空");
        }

        if (!Directory.Exists(directory))
        {
            // 传进来的是文件路径时也走这里 —— 对调用方而言同样是「存不下配置」。
            return new DirectoryWritabilityReport(directory, "目录不存在，或该路径其实是个文件");
        }

        // 只有真去写一次才算数：目录 ACL、只读介质、容器挂载各有各的失败方式，
        // 靠属性判断会漏。
        var probePath = Path.Combine(directory, $".lisscribe-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (var stream = File.Create(probePath, bufferSize: 1, FileOptions.DeleteOnClose))
            {
                // 只创建文件不写字节的话，配额耗尽 / 只读挂载这类要到落盘才暴露的失败会漏掉。
                stream.WriteByte(0);
            }

            return new DirectoryWritabilityReport(directory, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DirectoryWritabilityReport(directory, $"权限不足：{ex.Message}");
        }
        catch (IOException ex)
        {
            return new DirectoryWritabilityReport(directory, $"写入失败：{ex.Message}");
        }
        finally
        {
            TryDeleteProbeFile(probePath);
        }
    }

    /// <summary>
    /// DeleteOnClose 常态下已经删掉了；但网络盘和部分容器挂载并不认这个标志。
    /// 探测文件残留在程序目录旁边既难看，也会被用户当成垃圾文件来问。
    /// </summary>
    private static void TryDeleteProbeFile(string probePath)
    {
        try
        {
            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 探测本身不该因为清理失败而改判 —— 能写进去就是能写。
        }
    }
}
