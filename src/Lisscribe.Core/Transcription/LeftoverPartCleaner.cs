using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Core.Transcription;

/// <summary>
/// 启动时清理上次残留的 .part：退出时转写超过 10 秒停不下来，进程退出后「正式名.part」留在保存目录里。
/// </summary>
/// <remarks>
/// 只删本次启动之前写过的：截止时刻由组合根在构造开始时取，早于任何转写开工，故新写的 .part 都晚于它。
/// 正在被写的 .part 不许删（<see cref="TranscriptWriter"/> 开它时不给删除共享），删时抛共享冲突，记下跳过。
/// </remarks>
public sealed class LeftoverPartCleaner
{
    /// <summary>残留文件名：主名-yyyyMMddHHmmss[-序号].txt 再加 .part，即 <see cref="TranscriptFileNamer"/> 取的正式名加后缀。</summary>
    private static readonly Regex LeftoverPartName = new(
        @"^.*-[0-9]{14}(-[0-9]+)?\.txt" + Regex.Escape(TranscriptWriter.PartSuffix) + @"\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ILogger<LeftoverPartCleaner> _logger;

    public LeftoverPartCleaner(ILogger<LeftoverPartCleaner> logger)
    {
        _logger = logger;
    }

    /// <summary>文件名是否形如本程序转写留下的 .part，不区分大小写。</summary>
    public static bool IsLeftoverPartName(string fileName) => LeftoverPartName.IsMatch(fileName);

    /// <summary>
    /// 要扫的目录：默认目录在前，设定的保存目录在后。两者是同一目录（不计大小写与末尾分隔符）或设定值不是合法路径时只扫默认目录。
    /// </summary>
    public static IReadOnlyList<string> DirectoriesToScan(string? configuredOutputDir, string baseDirectory)
    {
        var defaultDirectory = TranscriptFileNamer.ResolveDirectory(null, baseDirectory);

        string configuredDirectory;

        try
        {
            configuredDirectory = TranscriptFileNamer.ResolveDirectory(configuredOutputDir, baseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 设定值不合法时转写回落到默认目录，.part 也只会在那里。
            return [defaultDirectory];
        }

        var same = string.Equals(
            Path.TrimEndingDirectorySeparator(defaultDirectory),
            Path.TrimEndingDirectorySeparator(configuredDirectory),
            StringComparison.OrdinalIgnoreCase);

        return same ? [defaultDirectory] : [defaultDirectory, configuredDirectory];
    }

    /// <summary>在线程池上清理，立即返回，不必等它：保存目录可能在断开的网络盘上，列目录会卡很久。任务不会失败。</summary>
    public Task CleanInBackground(string? configuredOutputDir, string baseDirectory, DateTimeOffset cutoff) =>
        Task.Run(() =>
        {
            try
            {
                Clean(configuredOutputDir, baseDirectory, cutoff);
            }
            catch (Exception ex)
            {
                // 没人等这个任务，漏出去的异常无人看见，故在此记下。
                _logger.LogError(ex, "清理上次残留的临时文件时出错");
            }
        });

    /// <summary>
    /// 删掉 cutoff 之前写过的残留 .part，只看这几个目录的顶层，返回删掉的个数。IO 与权限异常按目录、按文件接住，记下后接着做，不向外抛。
    /// </summary>
    public int Clean(string? configuredOutputDir, string baseDirectory, DateTimeOffset cutoff)
    {
        var cutoffUtc = cutoff.UtcDateTime;
        var total = 0;
        var cleanedDirectories = new List<string>();

        foreach (var directory in DirectoriesToScan(configuredOutputDir, baseDirectory))
        {
            var deleted = CleanDirectory(directory, cutoffUtc);

            if (deleted > 0)
            {
                total += deleted;
                cleanedDirectories.Add(directory);
            }
        }

        // 没有残留就不写：每次启动都记一句「没东西可删」只是噪声。
        if (total > 0)
        {
            _logger.LogInformation(
                "清理了上次残留的 {Count} 个临时文件，所在目录 {Directories}",
                total, string.Join("、", cleanedDirectories));
        }

        return total;
    }

    /// <summary>清理一个目录的顶层，返回删掉的个数。目录不存在就跳过，不建。</summary>
    private int CleanDirectory(string directory, DateTime cutoffUtc)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        FileInfo[] leftovers;

        try
        {
            // 只列文件、只列这一层：同名的目录与子目录里的文件都不碰。
            leftovers = [.. new DirectoryInfo(directory).EnumerateFiles().Where(f => IsLeftoverPartName(f.Name))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "列不出目录 {Directory} 里的文件，跳过清理", directory);

            return 0;
        }

        var deleted = 0;

        foreach (var file in leftovers)
        {
            if (TryDelete(file, cutoffUtc))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>早于截止时刻才删。删不掉就记下跳过，返回是否删了。</summary>
    private bool TryDelete(FileInfo file, DateTime cutoffUtc)
    {
        try
        {
            // 截止时刻及之后写过的不碰：那是本次启动后新开的转写正在写的。
            if (file.LastWriteTimeUtc >= cutoffUtc)
            {
                return false;
            }

            file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 最常见的是共享冲突：这个 .part 正被写着。
            _logger.LogWarning(ex, "上次残留的临时文件删不掉，跳过：{Path}", file.FullName);

            return false;
        }

        _logger.LogInformation("已删除上次残留的临时文件 {Path}", file.FullName);

        return true;
    }
}
