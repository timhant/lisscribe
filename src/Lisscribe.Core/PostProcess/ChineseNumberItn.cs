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

    /// <summary>钟点的左侧信号。只收时段词，「大约」「差不多」这类不算——它们跟量词用法同样搭得上。</summary>
    private static readonly string[] TimeOfDayPrefixes =
    [
        "早上", "早晨", "上午", "中午", "下午", "傍晚", "晚上", "夜里", "夜间", "半夜", "凌晨",
        "今天", "明天", "昨天", "前天", "后天", "今早", "明早",
    ];

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

                // 规则没接住。「点」是唯一会把两段数词分开的锚点，它一旦落空，右侧那段必须
                // 连同一起原样留下——否则下一轮会把它当成独立的长数字串转掉，出「三点1415」
                // 这种半转，比一个字不转难看得多。
                var skipTo = end < text.Length && text[end] == '点'
                    ? ScanNumerals(text, end + 1)
                    : end;

                // 整段原样留下，不再从段内某处重试——重试只会拼出更古怪的结果。
                builder.Append(text, i, skipTo - i);
                i = skipTo;

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
            // 时间先试，落空了才当小数：「九点三十分」是时间，「三点一四一五」是小数。
            '点' => TryTime(text, start, numerals, end, builder, out next)
                    || TryDecimal(text, numerals, end, builder, out next),
            '块' or '元' or '毛' or '角' => TryMoney(text, numerals, end, anchor, builder, out next),
            '月' => TryMonthOrDay(numerals, end, anchor, 1, 12, builder, out next),
            '日' or '号' => TryMonthOrDay(numerals, end, anchor, 1, 31, builder, out next),
            '年' => TryYear(numerals, end, builder, out next),
            _ => TryDigitRun(numerals, end, builder, out next),
        };
    }

    /// <summary>
    /// 钟点。**必须有第二个时间信号**：「点」既是钟点也是量词，「我有三点建议」跟「九点开会」
    /// 在左侧长得一模一样，光靠一个数字分不出来。信号是左侧的时段提示词，或右侧的
    /// 半/刻/整/钟/分/秒。裸的「九点开会」因此不转——白名单口径下宁可漏。
    /// </summary>
    private static bool TryTime(
        string text, int numeralStart, string numerals, int anchorIndex, StringBuilder builder, out int next)
    {
        next = anchorIndex;

        if (!ChineseNumeralParser.TryParseInteger(numerals, out var hour) || hour is < 0 or > 24)
        {
            return false;
        }

        var hasSignal = HasTimeOfDayPrefix(text, numeralStart);
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
                hasSignal = true;

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
            hasSignal = true;
        }

        // 「半」「整」「钟」原样交给主循环输出，这里只把它们算作信号。
        if (cursor < text.Length && text[cursor] is '半' or '整' or '钟')
        {
            hasSignal = true;
        }

        if (!hasSignal)
        {
            return false;
        }

        builder.Append(hour).Append('点').Append(tail);
        next = cursor;

        return true;
    }

    /// <summary>数词串紧挨着的左边是不是时段提示词。</summary>
    private static bool HasTimeOfDayPrefix(string text, int numeralStart)
    {
        foreach (var prefix in TimeOfDayPrefixes)
        {
            if (numeralStart >= prefix.Length
                && text.AsSpan(numeralStart - prefix.Length, prefix.Length).SequenceEqual(prefix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 小数：「三点一四一五」→ 3.1415。只在时间规则落空之后才轮到它。
    ///
    /// <para>
    /// 小数部分走逐位读法——说小数不带位权字（「三点一四」不是「三点十四」）。左侧不设值域：
    /// 时间那个 0–24 是钟点的事，「二十五点三」是个正常的数。
    /// </para>
    /// <para>
    /// <b>「一点」那条例外不套用在这里。</b>它是为「有一点累」设的，而右侧一旦跟着数词，
    /// 那个歧义就不存在了——「一点五」就是 1.5。
    /// </para>
    /// </summary>
    private static bool TryDecimal(
        string text, string numerals, int anchorIndex, StringBuilder builder, out int next)
    {
        next = anchorIndex;

        if (!ChineseNumeralParser.TryParseInteger(numerals, out var whole))
        {
            return false;
        }

        var cursor = anchorIndex + 1;
        var runEnd = ScanNumerals(text, cursor);

        if (runEnd == cursor
            || !ChineseNumeralParser.TryParseDigits(text[cursor..runEnd], out var fraction))
        {
            return false;
        }

        // 后面跟着时间单位就不是小数，是「九点零五分」那种时间——那一处宁可不转。
        var following = runEnd < text.Length ? text[runEnd] : '\0';

        if (following is '分' or '秒' or '刻')
        {
            return false;
        }

        builder.Append(whole).Append('.').Append(fraction);
        next = runEnd;

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
