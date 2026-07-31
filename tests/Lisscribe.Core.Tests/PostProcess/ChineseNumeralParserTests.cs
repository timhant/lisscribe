using Xunit;
using Lisscribe.Core.PostProcess;

namespace Lisscribe.Core.Tests.PostProcess;

/// <summary>
/// 数词解析器的两套读法。表全部手写在这里，不从生产代码取任何常量——
/// 拿被测代码的字符表去生成用例，它漏一个字测试就跟着漏。
/// </summary>
public sealed class ChineseNumeralParserTests
{
    // ---------- 整数读法：有位权字（十百千万亿）组织的读法 ----------

    [Theory]
    [InlineData("一", 1)]
    [InlineData("二", 2)]
    [InlineData("两", 2)]
    [InlineData("五", 5)]
    [InlineData("九", 9)]
    [InlineData("零", 0)]
    [InlineData("十", 10)]
    [InlineData("一十", 10)]
    [InlineData("十一", 11)]
    [InlineData("十五", 15)]
    [InlineData("二十", 20)]
    [InlineData("二十五", 25)]
    [InlineData("九十九", 99)]
    [InlineData("一百", 100)]
    [InlineData("一百零五", 105)]
    [InlineData("一百二十三", 123)]
    [InlineData("两百", 200)]
    [InlineData("一千", 1000)]
    [InlineData("一千零一", 1001)]
    [InlineData("一千二百三十四", 1234)]
    [InlineData("一万", 10000)]
    [InlineData("两万三千", 23000)]
    [InlineData("一万零五百", 10500)]
    [InlineData("一亿", 100000000)]
    [InlineData("五十六亿", 5600000000)]
    public void TryParseInteger_Recognized(string text, long expected)
    {
        Assert.True(ChineseNumeralParser.TryParseInteger(text, out var value));
        Assert.Equal(expected, value);
    }

    /// <summary>末位省略单位：「三千五」的「五」按前一档单位算，是 500 不是 5。</summary>
    [Theory]
    [InlineData("三十五", 35)]
    [InlineData("一百二", 120)]
    [InlineData("三百五", 350)]
    [InlineData("三千五", 3500)]
    [InlineData("两万五", 25000)]
    public void TryParseInteger_TrailingUnitOmitted(string text, long expected)
    {
        Assert.True(ChineseNumeralParser.TryParseInteger(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("你好")]
    [InlineData("一二三")]          // 多字却没有位权字组织，这是逐位读法，不是整数
    [InlineData("零五")]            // 「零」不占 pending，故只有「多字须有位权字」这条能挡住它
    [InlineData("二零二六")]
    [InlineData("两三")]
    [InlineData("幺")]              // 「幺」是报号读法，整数读法里不认
    [InlineData("幺十")]
    [InlineData("十十")]
    [InlineData("百")]              // 位权字不能单独成数
    [InlineData("千")]
    [InlineData("一二百")]
    [InlineData("一百零")]          // 半截
    [InlineData("一万亿")]          // 超出「亿」这个上限
    [InlineData("三点一四")]
    [InlineData("一 二")]
    public void TryParseInteger_NotRecognized(string text)
    {
        Assert.False(ChineseNumeralParser.TryParseInteger(text, out var value));
        Assert.Equal(0, value);
    }

    // ---------- 逐位读法：没有位权字，一个字一位 ----------

    [Theory]
    [InlineData("一", "1")]
    [InlineData("幺", "1")]
    [InlineData("零", "0")]
    [InlineData("二", "2")]
    [InlineData("一二三", "123")]
    [InlineData("二零二六", "2026")]
    [InlineData("幺三五八", "1358")]
    [InlineData("零幺零", "010")]   // 前导零必须留住，所以这套读法返回字符串而不是数值
    [InlineData("幺三五八幺八八七五", "135818875")]
    public void TryParseDigits_Recognized(string text, string expected)
    {
        Assert.True(ChineseNumeralParser.TryParseDigits(text, out var digits));
        Assert.Equal(expected, digits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("你好")]
    [InlineData("一百二十三")]      // 含位权字，那是整数读法
    [InlineData("十")]
    [InlineData("三十五")]
    [InlineData("两")]              // 报号没人说「两」
    [InlineData("两三")]
    [InlineData("一二三点四")]
    [InlineData("一 二")]
    public void TryParseDigits_NotRecognized(string text)
    {
        Assert.False(ChineseNumeralParser.TryParseDigits(text, out var digits));
        Assert.Equal(string.Empty, digits);
    }
}
