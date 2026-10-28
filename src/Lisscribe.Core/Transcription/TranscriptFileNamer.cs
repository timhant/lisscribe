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

        return ChooseFreePath(Path.Combine(directory, stem + ".txt"), exists);
    }

    /// <summary>
    /// preferredPath 已被占时，依次试「主名-1.扩展名」「主名-2.扩展名」……，返回第一个空着的完整路径。
    /// </summary>
    public static string ChooseFreePath(string preferredPath, Func<string, bool> exists)
    {
        var directory = Path.GetDirectoryName(preferredPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);

        var candidate = preferredPath;

        for (var n = 1; exists(candidate); n++)
        {
            candidate = Path.Combine(directory, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + extension);
        }

        return candidate;
    }
}