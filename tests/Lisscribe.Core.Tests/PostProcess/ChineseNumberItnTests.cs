using Xunit;
using Lisscribe.Core.PostProcess;

namespace Lisscribe.Core.Tests.PostProcess;

/// <summary>
/// 白名单式中文数字转换。表全部手写在这里，不从生产代码取锚点表——
/// 拿被测代码的字符表去生成用例，它漏一个锚点测试就跟着漏。
/// 断言一律整串相等：断言「输出含 9」在「九点九分」上是空的，输入本来就会产出别处的 9。
/// </summary>
public sealed class ChineseNumberItnTests
{
    private static string Convert(string text) => ChineseNumberItn.Convert(text, enabled: true);

    // ---------- 时间 ----------

    [Theory]
    [InlineData("早上九点半", "早上9点半")]
    [InlineData("九点三十分", "9点30分")]
    [InlineData("九点三十分二十秒", "9点30分20秒")]
    [InlineData("两点", "2点")]
    [InlineData("十一点", "11点")]
    [InlineData("零点", "0点")]
    [InlineData("二十四点", "24点")]
    [InlineData("下午三点一刻", "下午3点一刻")]  // 「刻」那一侧保留汉字
    [InlineData("九点开会", "9点开会")]
    public void Convert_Time(string input, string expected)
    {
        Assert.Equal(expected, Convert(input));
    }

    // ---------- 金额 ----------

    [Theory]
    [InlineData("五十块钱", "50块钱")]
    [InlineData("三块五", "3块5")]              // 不合并成 3.5，保留口语形态
    [InlineData("三块五毛", "3块5毛")]
    [InlineData("两毛", "2毛")]
    [InlineData("五角", "5角")]
    [InlineData("一千二百三十四块钱", "1234块钱")]
    [InlineData("三十元。", "30元。")]
    [InlineData("三十元整", "30元整")]
    [InlineData("三十元钱", "30元钱")]
    public void Convert_Money(string input, string expected)
    {
        Assert.Equal(expected, Convert(input));
    }

    // ---------- 百分比 ----------

    [Theory]
    [InlineData("百分之三十", "30%")]
    [InlineData("百分之一百", "100%")]
    [InlineData("涨了百分之五", "涨了5%")]
    [InlineData("百分之九十九点九", "99.9%")]   // 只有这一类认小数点
    public void Convert_Percent(string input, string expected)
    {
        Assert.Equal(expected, Convert(input));
    }

    // ---------- 长数字串 ----------

    [Theory]
    [InlineData("幺三五八幺八八七五", "135818875")]
    [InlineData("一二三四", "1234")]
    [InlineData("幺三五", "135")]               // 含「幺」，阈值降到 3
    [InlineData("零幺零", "010")]               // 前导零留住
    [InlineData("我的房间是二零一八", "我的房间是2018")]
    public void Convert_DigitRun(string input, string expected)
    {
        Assert.Equal(expected, Convert(input));
    }

    // ---------- 日期与年份 ----------

    [Theory]
    [InlineData("二零二六年", "2026年")]
    [InlineData("三月八号", "3月8号")]
    [InlineData("十二月三十一日", "12月31日")]
    [InlineData("一月", "1月")]
    [InlineData("二零二六年三月八号", "2026年3月8号")]
    public void Convert_Date(string input, string expected)
    {
        Assert.Equal(expected, Convert(input));
    }

    // ---------- 阴性：白名单口径的主防线 ----------

    [Theory]
    [InlineData("我一个人去")]
    [InlineData("乱七八糟")]
    [InlineData("星期三")]
    [InlineData("不三不四")]
    [InlineData("买了三个")]
    [InlineData("有一点累")]
    [InlineData("一块儿去")]
    [InlineData("三点一四")]
    [InlineData("三个月")]
    [InlineData("十七八")]
    [InlineData("一两个")]
    [InlineData("三年")]
    [InlineData("三元一次方程")]
    [InlineData("二元结构")]
    [InlineData("九点零五分")]   // 已知漏项：「零五」不是合法整数读法，故整处不转
    public void Convert_LeavesUntouched(string input)
    {
        Assert.Equal(input, Convert(input));
    }

    // ---------- 变异抗性：每条约束配一个只有它能挡住的用例 ----------

    [Fact]
    public void Convert_HourOutOfRange_NotConverted()
    {
        // 删掉值域检查这里会变成「25点」。
        Assert.Equal("二十五点", Convert("二十五点"));
    }

    [Fact]
    public void Convert_ExceptionListOnlyBitesSingleOne()
    {
        // 例外表只对单字「一」生效，删掉它「一点」会变成「1点」。
        Assert.Equal("有一点累", Convert("有一点累"));
        Assert.Equal("11点", Convert("十一点"));
    }

    [Fact]
    public void Convert_YuanNeedsTerminator()
    {
        // 放宽「元」的右侧要求，「三元一次方程」会变成「3元一次方程」。
        Assert.Equal("三元一次方程", Convert("三元一次方程"));
        Assert.Equal("30元整", Convert("三十元整"));
    }

    [Fact]
    public void Convert_PlainDigitRunThreshold_IsFour()
    {
        // 阈值两侧各一条，是唯一能钉死这个数字的形状：只写一侧则改成 3 或 5 都能绿。
        Assert.Equal("一二三", Convert("一二三"));
        Assert.Equal("1234", Convert("一二三四"));
    }

    [Fact]
    public void Convert_YaoOrZeroLowersThresholdToThree()
    {
        // 删掉降阈，「幺三五」就不转了。
        Assert.Equal("135", Convert("幺三五"));
        Assert.Equal("幺三", Convert("幺三"));
    }

    [Fact]
    public void Convert_YearNeedsExactlyFourDigits()
    {
        // 放宽到 2 位，「一九年」会变成「19年」。
        Assert.Equal("一九年", Convert("一九年"));
        Assert.Equal("2026年", Convert("二零二六年"));
    }

    [Fact]
    public void Convert_DecimalPointOnlyInsidePercent()
    {
        Assert.Equal("99.9%", Convert("百分之九十九点九"));
        Assert.Equal("三点一四", Convert("三点一四"));
    }

    // ---------- 不变量 ----------

    [Theory]
    [InlineData("早上九点半")]
    [InlineData("五十块钱")]
    [InlineData("百分之三十")]
    [InlineData("幺三五八幺八八七五")]
    [InlineData("二零二六年三月八号")]
    [InlineData("一千二百三十四块钱")]
    public void Convert_IsIdempotent(string input)
    {
        var once = Convert(input);

        Assert.Equal(once, Convert(once));
    }

    [Fact]
    public void Convert_WhenDisabled_ReturnsInputUnchanged()
    {
        Assert.Equal("早上九点半", ChineseNumberItn.Convert("早上九点半", enabled: false));
    }

    // ---------- 模糊输入：测的是「不该抛」那一侧 ----------

    [Theory]
    [InlineData("")]
    [InlineData("。。。，，，")]
    [InlineData("🙂🙂🙂")]                      // 代理对：逐 char 扫描会把它切开
    [InlineData("九点🙂三十分")]
    [InlineData("一")]
    [InlineData("点")]
    [InlineData("块")]
    [InlineData("百分之")]
    [InlineData("百分之。")]
    [InlineData("年月日号")]
    public void Convert_FuzzyInput_DoesNotThrow(string input)
    {
        var result = Convert(input);

        Assert.NotNull(result);
    }

    [Fact]
    public void Convert_VeryLongInput_DoesNotThrow()
    {
        var input = string.Concat(Enumerable.Repeat("九点三十分，", 2000));

        Assert.NotNull(Convert(input));
    }
}
