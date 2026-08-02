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

    /// <summary>
    /// 「点」既是钟点也是量词（「三点建议」），光靠左边一个数字分不出来，**必须有第二个时间信号**：
    /// 左侧的时段提示词，或右侧的 半/刻/整/钟/分/秒。
    /// </summary>
    [Theory]
    [InlineData("早上九点半", "早上9点半")]
    [InlineData("九点半", "9点半")]                    // 右侧信号
    [InlineData("九点三十分", "9点30分")]
    [InlineData("九点三十分二十秒", "9点30分20秒")]
    [InlineData("下午两点", "下午2点")]                // 左侧信号
    [InlineData("晚上十一点", "晚上11点")]
    [InlineData("上午九点开会", "上午9点开会")]        // 有提示词，右侧是普通汉字也放行
    [InlineData("零点整", "0点整")]
    [InlineData("二十四点整", "24点整")]
    [InlineData("两点钟", "2点钟")]
    [InlineData("下午三点一刻", "下午3点一刻")]        // 「刻」那一侧保留汉字
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

    // ---------- 小数 ----------

    /// <summary>
    /// 时间规则落空后才轮到小数：右侧走逐位读法（说小数不带位权字），且不接分/秒/刻。
    /// 「一点」那条例外**不套用在这里**——右侧一旦有数词，「有一点累」那个歧义就不存在了。
    /// </summary>
    [Theory]
    [InlineData("三点一四一五", "3.1415")]
    [InlineData("三点一四", "3.14")]
    [InlineData("零点五", "0.5")]
    [InlineData("一点五", "1.5")]
    [InlineData("二十五点三", "25.3")]      // 小数不受时间那个 0–24 值域的约束
    [InlineData("二十五点一二三四", "25.1234")]
    public void Convert_Decimal(string input, string expected)
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
    [InlineData("三个月")]
    [InlineData("我有三点建议")]   // 「点」在这儿是量词不是钟点
    [InlineData("两点说明")]
    [InlineData("九点开会")]       // 裸钟点没有第二个信号，宁可漏
    [InlineData("十七八")]
    [InlineData("一两个")]
    [InlineData("三年")]
    [InlineData("三元一次方程")]
    [InlineData("二元结构")]
    [InlineData("三角形")]        // 「角」跟「元」同族：钱单位也是常用字，右侧接名词就不是钱
    [InlineData("三角函数")]
    [InlineData("六角螺丝")]
    [InlineData("三毛流浪记")]
    [InlineData("九点零五分")]   // 已知漏项：整数读法不认「零五」，而右侧跟着「分」也轮不到小数
    [InlineData("三点二十")]     // 右侧含位权字，逐位读法拒收，故既不是时间也不是小数
    [InlineData("一二三四五六七八九十")]   // 末尾那个「十」是位权字，整串被逐位读法拒之门外
    public void Convert_LeavesUntouched(string input)
    {
        Assert.Equal(input, Convert(input));
    }

    // ---------- 变异抗性：每条约束配一个只有它能挡住的用例 ----------

    [Fact]
    public void Convert_HourOutOfRange_NotConverted()
    {
        // 带上「半」才试得出值域：不带的话「没有第二个信号」那道防线会独自兜住，
        // 删掉值域检查照样绿。
        Assert.Equal("二十五点半", Convert("二十五点半"));
        Assert.Equal("24点半", Convert("二十四点半"));
    }

    [Fact]
    public void Convert_OneOClockNeedsTheSameSignalAsAnyOther()
    {
        // 「有一点累」靠的是「没有第二个信号」，不再给「一」开专门的例外——那条例外会把
        // 「一点半」一起挡掉，而这三个字几乎只能是 1:30。
        Assert.Equal("有一点累", Convert("有一点累"));
        Assert.Equal("1点半", Convert("一点半"));
    }

    [Fact]
    public void Convert_MoneyExceptionStillBitesSingleOne()
    {
        // 金额那条例外还在：「一块」没有第二道防线兜着。
        Assert.Equal("一块儿去", Convert("一块儿去"));
        Assert.Equal("3块", Convert("三块"));
    }

    [Fact]
    public void Convert_TimeNeedsASecondSignal()
    {
        // 「点」既是钟点也是量词。去掉这道要求，「我有三点建议」会变成「我有3点建议」。
        Assert.Equal("九点开会", Convert("九点开会"));
        Assert.Equal("9点半", Convert("九点半"));
        Assert.Equal("上午9点开会", Convert("上午九点开会"));
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
    public void Convert_TimeUnitAfterFraction_BeatsDecimal()
    {
        // 去掉「右侧不接分/秒/刻」这个条件，「九点零五分」会变成「9.05分」。
        Assert.Equal("九点零五分", Convert("九点零五分"));
        Assert.Equal("3.05", Convert("三点零五"));
    }

    [Fact]
    public void Convert_FailedTimeDoesNotLeakItsRightHandSide()
    {
        // 时间与小数双双落空时，右侧那段必须跟着一起留下。不然它会被当成独立的长数字串
        // 转掉，出「九点053分」这种半转——2026-07-31 真机上「三点一四一五」出的就是
        // 「三点1415」。这句本身不像人话，但它是眼下唯一还走得到那条路的形状。
        Assert.Equal("九点零五三分", Convert("九点零五三分"));
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
