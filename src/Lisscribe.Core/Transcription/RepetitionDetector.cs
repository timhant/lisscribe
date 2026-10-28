namespace Lisscribe.Core.Transcription;

/// <summary>
/// 识别结果是否掉进了复读循环（模型卡在同一个词上不停输出）。
/// </summary>
public static class RepetitionDetector
{
    /// <summary>
    /// 连续重复多少次算循环。依据实测：正常说话最长连续重复 8 次，模型循环时在 100 次以上。
    /// </summary>
    public const int LoopThreshold = 20;

    /// <summary>被检查的重复单元最长字数。</summary>
    private const int MaxUnitLength = 4;

    /// <summary>去掉标点与空白后，是否存在 1–4 字的单元连续出现至少 LoopThreshold 次。</summary>
    public static bool IsLooping(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var core = Strip(text);

        for (var unit = 1; unit <= MaxUnitLength; unit++)
        {
            // 连续 run 个位置满足 core[i] == core[i + unit]，就是单元重复了 run / unit + 1 次。
            var run = 0;
            for (var i = 0; i + unit < core.Length; i++)
            {
                run = core[i] == core[i + unit] ? run + 1 : 0;

                if (run / unit + 1 >= LoopThreshold)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>去掉所有 Unicode 标点与空白。</summary>
    private static string Strip(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (!char.IsPunctuation(c) && !char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}