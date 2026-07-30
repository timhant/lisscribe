namespace Lisscribe.Core.PostProcess;

/// <summary>
/// 短句末尾标点清理。
///
/// 规则源自参考项目 config_client.py 的 trash_punc = '，。,.' 与 trash_punc_thresh = 8：
/// 用户说「打开设置」时不想要识别器补的那个句号，但长句的标点要保留。
/// 只处理这四种标点——问号、叹号承载语气，删掉会改变语义。
/// </summary>
public static class TrailingPunctuationTrimmer
{
    private const string TrashPunctuation = "，。,.";

    public static string Trim(string text, bool enabled, int maxChars)
    {
        if (!enabled || text.Length == 0 || maxChars <= 0)
        {
            return text;
        }

        var last = text[^1];
        if (!TrashPunctuation.Contains(last))
        {
            return text;
        }

        // 长度判定基于去掉该标点后的正文长度。
        var body = text[..^1];

        return body.Length <= maxChars ? body : text;
    }
}
