using System.Globalization;

namespace Lisscribe.Core.Transcription;

/// <summary>
/// 转写文本里的一行：「[hh:mm:ss] 文字」。
/// </summary>
public static class TranscriptLine
{
    /// <summary>文字为空白返回 null（调用方据此不写行）。小时取总小时数，秒向下取整。</summary>
    public static string? Format(TimeSpan start, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var hours = (int)start.TotalHours;

        return string.Create(CultureInfo.InvariantCulture,
            $"[{hours:00}:{start.Minutes:00}:{start.Seconds:00}] {text.Trim()}");
    }
}