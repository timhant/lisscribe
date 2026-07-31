namespace Lisscribe.Core.PostProcess;

/// <summary>
/// 中文数词解析，ITN 规则的公共底座。
///
/// 中文有两套读法，字面上互斥，故分成两个方法而不是一个入口内部分派：
/// 整数读法靠位权字（十百千万亿）组织（一百二十三 = 123），逐位读法一个字一位
/// （二零二六 = 2026）。调用方本就知道自己要哪一套——时间、月日要整数，年份、
/// 长号码要逐位——各方法内部的合法性检查顺带把另一套挡在门外。
///
/// 认不出一律返回 false，绝不猜：白名单口径下漏一条只是没生效，猜错才是真的坏事。
/// </summary>
public static class ChineseNumeralParser
{
    private const string DigitChars = "零一二三四五六七八九";

    /// <summary>「两」只在整数读法里认——报手机号没人说「两」。</summary>
    private const char Two = '两';

    /// <summary>「幺」只在逐位读法里认，是报号专用读法，出现即强信号。</summary>
    private const char Yao = '幺';

    private const char Zero = '零';

    /// <summary>
    /// 整数读法：一百二十三 → 123。上限是「亿」，更长的数语音输入里说不出来。
    /// </summary>
    public static bool TryParseInteger(string text, out long value)
    {
        value = 0;

        if (text.Length == 0)
        {
            return false;
        }

        // 末字是「零」说明话只说了半截（「一百零」），整串就是「零」除外。
        if (text.Length > 1 && text[^1] == Zero)
        {
            return false;
        }

        long result = 0;      // 「万」「亿」结算掉的部分
        long section = 0;     // 当前段内由十百千累计的部分
        long number = 0;      // 刚读到、尚未配上单位的数字
        long lastUnit = 0;    // 最后用过的单位，供末位省略单位时定档
        var pending = false;  // number 是否有效
        var sawUnit = false;
        var sawZero = false;
        var sawWan = false;
        var sawYi = false;

        foreach (var c in text)
        {
            if (c == Zero)
            {
                // 零是显式占位符，不占 pending；它一出现就说明这是精确读法，末位不再补单位。
                sawZero = true;
                continue;
            }

            var digit = c == Two ? 2 : DigitChars.IndexOf(c);
            if (digit > 0)
            {
                // 两个数字连读（一二三）是逐位读法，不该走到这儿。
                if (pending)
                {
                    return false;
                }

                number = digit;
                pending = true;

                continue;
            }

            var unit = UnitOf(c);
            if (unit == 0)
            {
                return false;
            }

            sawUnit = true;

            if (unit is 10 or 100 or 1000)
            {
                // 「十一」这种以十开头的省略了前面的「一」，其余情况单位前必须有数字。
                if (!pending)
                {
                    if (unit != 10 || section != 0 || result != 0)
                    {
                        return false;
                    }

                    number = 1;
                }

                section += number * unit;
                number = 0;
                pending = false;
                lastUnit = unit;

                continue;
            }

            // 「万」「亿」各自只能出现一次；「一万亿」超出上限，由「亿」前不得有万来挡。
            if (unit == 10000)
            {
                if (sawWan)
                {
                    return false;
                }

                sawWan = true;
                section += number;
                result += section * unit;
            }
            else
            {
                if (sawWan || sawYi)
                {
                    return false;
                }

                sawYi = true;
                sawWan = false;
                section += number;
                result = (result + section) * unit;
            }

            section = 0;
            number = 0;
            pending = false;
            lastUnit = unit;
        }

        // 没有位权字撑着的多字串是逐位读法（零五），不是整数。
        if (!sawUnit && text.Length > 1)
        {
            return false;
        }

        // 末位省略单位：「三千五」的五按前一档算，是 500。见过「零」则是精确读法，不补。
        if (pending && !sawZero && lastUnit > 10)
        {
            number *= lastUnit / 10;
        }

        value = result + section + number;

        return true;
    }

    /// <summary>
    /// 逐位读法：二零二六 → "2026"。返回字符串而不是数值，因为前导零必须留住（零幺零 = 010）。
    /// </summary>
    public static bool TryParseDigits(string text, out string digits)
    {
        digits = string.Empty;

        if (text.Length == 0)
        {
            return false;
        }

        var buffer = new char[text.Length];

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var digit = c == Yao ? 1 : DigitChars.IndexOf(c);

            // 位权字、「两」、以及任何非数词字都不属于这套读法。
            if (digit < 0)
            {
                return false;
            }

            buffer[i] = (char)('0' + digit);
        }

        digits = new string(buffer);

        return true;
    }

    private static long UnitOf(char c) => c switch
    {
        '十' => 10,
        '百' => 100,
        '千' => 1000,
        '万' => 10000,
        '亿' => 100000000,
        _ => 0,
    };
}
