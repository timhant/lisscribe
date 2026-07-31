using System.Text;

namespace Lisscribe.Core.PostProcess;

/// <summary>
/// 中文数字转阿拉伯数字（Qwen3 档专用）。
///
/// SenseVoice 档由 sherpa-onnx 的 UseInverseTextNormalization 免费带上，Qwen3 没有这个开关
/// ——sherpa-onnx 1.13.4 的 Qwen3 实现根本不调 RuleFsts，只能自己做。
///
/// **白名单口径**：只在明确的数字语境里转，没命中就一个字不动。语音输入里转错一处的代价
/// 远大于漏转一处，所以宁可漏。触发方式是从单位锚点（点 / 块元毛角 / 百分之 / 月日号年）
/// 反向找数词，长数字串是唯一没有锚点的一类，靠长度触发。
///
/// 设计见 docs/plans/2026-07-31-chinese-itn-design.md。
/// </summary>
public static class ChineseNumberItn
{
    private const string NumeralChars = "零一二三四五六七八九两幺十百千万亿";

    private const string PercentPrefix = "百分之";

    /// <summary>「元」右侧允许的收尾标点，用来挡「三元一次方程」这种「元 + 名词」构型。</summary>
    private const string Terminators = "，。！？；：、…—,.!?;:";

    public static string Convert(string text, bool enabled)
    {
        if (!enabled || text.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (TryPercent(text, i, builder, out var afterPercent))
            {
                i = afterPercent;

                continue;
            }

            var end = ScanNumerals(text, i);

            if (end > i)
            {
                if (TryConvertRun(text, i, end, builder, out var afterRule))
                {
                    i = afterRule;

                    continue;
                }

                // 整段数词原样留下，不再从段内某处重试——重试只会拼出更古怪的结果。
                builder.Append(text, i, end - i);
                i = end;

                continue;
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>数词段 text[start..end) 后面紧跟的锚点决定走哪一类；锚点命中后不再退回试别类。</summary>
    private static bool TryConvertRun(string text, int start, int end, StringBuilder builder, out int next)
    {
        var numerals = text[start..end];
        var anchor = end < text.Length ? text[end] : '\0';

        return anchor switch
        {
            '点' => TryTime(text, numerals, end, builder, out next),
            '块' or '元' or '毛' or '角' => TryMoney(text, numerals, end, anchor, builder, out next),
            '月' => TryMonthOrDay(numerals, end, anchor, 1, 12, builder, out next),
            '日' or '号' => TryMonthOrDay(numerals, end, anchor, 1, 31, builder, out next),
            '年' => TryYear(numerals, end, builder, out next),
            _ => TryDigitRun(numerals, end, builder, out next),
        };
    }

    private static bool TryTime(string text, string numerals, int anchorIndex, StringBuilder builder, out int next)
    {
        next = anchorIndex;

        // 例外表：「有一点累」比「1点」常见得多。只对单字「一」生效，「十一点」照转。
        if (numerals == "一")
        {
            return false;
        }

        if (!ChineseNumeralParser.TryParseInteger(numerals, out var hour) || hour is < 0 or > 24)
        {
            return false;
        }

        var cursor = anchorIndex + 1;
        var tail = new StringBuilder();

        // 右侧一旦以数词开头，就必须构成 数词+分/秒/刻，否则整处不匹配——挡的是「三点一四」。
        while (true)
        {
            var runEnd = ScanNumerals(text, cursor);

            if (runEnd == cursor)
            {
                break;
            }

            var unit = runEnd < text.Length ? text[runEnd] : '\0';

            if (unit == '刻')
            {
                // 「一刻」「三刻」保留汉字。
                tail.Append(text, cursor, runEnd - cursor + 1);
                cursor = runEnd + 1;

                break;
            }

            if (unit is not ('分' or '秒'))
            {
                return false;
            }

            if (!ChineseNumeralParser.TryParseInteger(text[cursor..runEnd], out var part) || part is < 0 or > 59)
            {
                return false;
            }

            tail.Append(part).Append(unit);
            cursor = runEnd + 1;
        }

        builder.Append(hour).Append('点').Append(tail);
        next = cursor;

        return true;
    }

    private static bool TryMoney(
        string text, string numerals, int anchorIndex, char anchor, StringBuilder builder, out int next)
    {
        next = anchorIndex;

        // 例外表：「一块儿去」。
        if (numerals == "一" && anchor == '块')
        {
            return false;
        }

        if (!ChineseNumeralParser.TryParseInteger(numerals, out var amount))
        {
            return false;
        }

        var cursor = anchorIndex + 1;
        var after = cursor < text.Length ? text[cursor] : '\0';

        // 「元」比别的钱单位多一道要求：右侧须是句末、标点、「钱」或「整」。挡的正是
        // 「三元一次方程」「二元结构」这种「元 + 名词」，而真金额恰好都留得下。
        if (anchor == '元' && after != '\0' && after != '钱' && after != '整' && !Terminators.Contains(after))
        {
            return false;
        }

        builder.Append(amount).Append(anchor);
        next = cursor;

        // 「三块五」的散数：转，但不合并成 3.5，保留口语形态。其后若跟着别的钱单位
        // （三块五毛），这一位交给那个锚点自己处理。
        if (anchor is '块' or '元')
        {
            var runEnd = ScanNumerals(text, cursor);
            var following = runEnd < text.Length ? text[runEnd] : '\0';

            if (runEnd > cursor
                && following is not ('毛' or '角' or '分' or '钱')
                && ChineseNumeralParser.TryParseInteger(text[cursor..runEnd], out var fraction)
                && fraction is >= 0 and <= 9)
            {
                builder.Append(fraction);
                next = runEnd;
            }
        }

        return true;
    }

    private static bool TryMonthOrDay(
        string numerals, int anchorIndex, char anchor, long min, long max, StringBuilder builder, out int next)
    {
        next = anchorIndex;

        if (!ChineseNumeralParser.TryParseInteger(numerals, out var value) || value < min || value > max)
        {
            return false;
        }

        builder.Append(value).Append(anchor);
        next = anchorIndex + 1;

        return true;
    }

    /// <summary>
    /// 年份只认 4 位逐位读法。两位的「五六年」多半是时长，为一个少见的写法冒这个险不划算；
    /// 这条要求还顺带把「三年」「两年」全挡在外面。
    /// </summary>
    private static bool TryYear(string numerals, int anchorIndex, StringBuilder builder, out int next)
    {
        next = anchorIndex;

        if (!ChineseNumeralParser.TryParseDigits(numerals, out var digits) || digits.Length != 4)
        {
            return false;
        }

        builder.Append(digits).Append('年');
        next = anchorIndex + 1;

        return true;
    }

    /// <summary>
    /// 唯一没有锚点的一类，靠长度触发。「幺」是报号专用读法、「零」在日常汉语里几乎不连读，
    /// 两者出现即强信号，故降一档阈值。
    /// </summary>
    private static bool TryDigitRun(string numerals, int end, StringBuilder builder, out int next)
    {
        next = end;

        if (!ChineseNumeralParser.TryParseDigits(numerals, out var digits))
        {
            return false;
        }

        var threshold = numerals.Contains('幺') || numerals.Contains('零') ? 3 : 4;

        if (digits.Length < threshold)
        {
            return false;
        }

        builder.Append(digits);

        return true;
    }

    private static bool TryPercent(string text, int index, StringBuilder builder, out int next)
    {
        next = index;

        if (index + PercentPrefix.Length > text.Length
            || !text.AsSpan(index, PercentPrefix.Length).SequenceEqual(PercentPrefix))
        {
            return false;
        }

        var cursor = index + PercentPrefix.Length;
        var runEnd = ScanNumerals(text, cursor);

        if (runEnd == cursor || !ChineseNumeralParser.TryParseInteger(text[cursor..runEnd], out var whole))
        {
            return false;
        }

        builder.Append(whole);
        cursor = runEnd;

        // 「百分之」已把语境锁死，所以只有这一类认小数点，别处一概不认。
        if (cursor < text.Length && text[cursor] == '点')
        {
            var fractionEnd = ScanNumerals(text, cursor + 1);

            if (fractionEnd > cursor + 1
                && ChineseNumeralParser.TryParseDigits(text[(cursor + 1)..fractionEnd], out var fraction))
            {
                builder.Append('.').Append(fraction);
                cursor = fractionEnd;
            }
        }

        builder.Append('%');
        next = cursor;

        return true;
    }

    private static int ScanNumerals(string text, int start)
    {
        var i = start;

        while (i < text.Length && NumeralChars.Contains(text[i]))
        {
            i++;
        }

        return i;
    }
}
