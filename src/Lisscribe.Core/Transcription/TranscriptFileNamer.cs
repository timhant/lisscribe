using System.Globalization;

namespace Lisscribe.Core.Transcription;

/// <summary>
/// 转写结果的输出目录与文件名。
/// </summary>
public static class TranscriptFileNamer
{
    /// <summary>未配置输出目录时，程序目录下的默认子目录名。</summary>
    public const string DefaultDirectoryName = "transcripts";

    /// <summary>空白落到 &lt;base&gt;\transcripts，相对路径按 baseDirectory 展开（不看当前工作目录）。</summary>
    public static string ResolveDirectory(string? configured, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(baseDirectory, DefaultDirectoryName);
        }

        return Path.GetFullPath(configured, baseDirectory);
    }

    /// <summary>
    /// 文件名为「源文件名-yyyyMMddHHmmss.txt」，已存在则依次试 -1、-2……，返回完整路径。
    /// </summary>
    public static string ChoosePath(string directory, string sourcePath, DateTime startedAt, Func<string, bool> exists)
    {
        // 不变文化，免得佛历等日历把年份改掉。
        var stem = Path.GetFileNameWithoutExtension(sourcePath)
            + "-" + startedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        var candidate = Path.Combine(directory, stem + ".txt");

        for (var n = 1; exists(candidate); n++)
        {
            candidate = Path.Combine(directory, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".txt");
        }

        return candidate;
    }
}