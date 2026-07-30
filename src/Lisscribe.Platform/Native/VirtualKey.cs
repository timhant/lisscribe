using Lisscribe.Core.Hotkey;

namespace Lisscribe.Platform.Native;

/// <summary>配置中的按键名与 Windows 虚拟键码之间的映射。</summary>
public static class VirtualKey
{
    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_RSHIFT = 0xA1;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_RCONTROL = 0xA3;
    public const ushort VK_LMENU = 0xA4;
    public const ushort VK_RMENU = 0xA5;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;

    /// <summary>
    /// KEYEVENTF_UNICODE 形态的注入事件，在低层钩子里 <c>vkCode</c> 是这个值而不是字符本身。
    /// 按 vkCode 分派再查注入标记的话，须确保它不会误撞用户配的热键码。
    /// </summary>
    public const ushort VK_PACKET = 0xE7;

    /// <summary>
    /// 一个官方未分配的虚拟键码，只用来当「中间键」把 Win 键的按放拆开。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：用户按住 Win 说话时，补发的 <c>Win↑</c> 在 shell 眼里就是一次无中间键的
    /// 完整 Win 按放，于是开始菜单弹出、抢走前台，识别出的文本逐字进了它的搜索框（实测 3/3，
    /// 与「真按一放 Win」的阳性对照不可区分）。补发前插一个未分配码的按放即 0/3。
    /// 只有「Win 是唯一按住的修饰键」时才中招——别的修饰键的 KEYUP 自己就是中间键——而配置校验
    /// 放行 Win 作修饰键，故这不是理论路径。
    /// 选 0xE8 而不是某个 F 键：它在 MSDN 的虚拟键表里是未分配码，应用不会绑它；实测按放一次
    /// EDIT 里不留任何字符。按下与抬起必须成对——只按不抬同样能抑制，但那会把一个键永久留在
    /// 按下态，而卡住的键跨进程存活。
    /// </remarks>
    public const ushort VK_NEUTRAL = 0xE8;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;

    private static readonly Dictionary<string, ushort> NameToCode;
    private static readonly Dictionary<ushort, string> CodeToName;

    /// <summary>
    /// 正反两张表都从 <see cref="BuildKeyTable"/> 这一份数据派生：各补一遍也能跑，但往正向表加
    /// 一类新键时必须记得同时改反向表，改漏了只有恰好覆盖到那类键的往返测试才会转红。
    /// 合成一处，把「漏改」从「靠测试发现」变成「写不出来」。
    /// 用静态构造器而非字段初始化器，是因为后者只靠声明顺序保证被读的字段已初始化，而编译器一个
    /// 字都不提示：把 CodeToName 那行挪到数据源之前仍是「0 Warning」，运行时才炸
    /// TypeInitializationException。这里两张表同读一个局部变量，那条线索根本不存在。
    /// </summary>
    static VirtualKey()
    {
        var table = BuildKeyTable();

        NameToCode = table.ToDictionary(
            entry => entry.Name, entry => entry.Code, StringComparer.OrdinalIgnoreCase);

        // 表内条目顺序在此有意义：若日后为同一个码加别名，反向表取表中第一个作规范显示名。
        var reverse = new Dictionary<ushort, string>();
        foreach (var (name, code) in table)
        {
            reverse.TryAdd(code, name);
        }

        CodeToName = reverse;
    }

    public static bool TryFromName(string name, out ushort code) =>
        NameToCode.TryGetValue(name, out code);

    /// <summary>
    /// 无名键码回落成十六进制而非抛异常：这个值的去处是日志与界面提示，一个认不出的键不该把
    /// 调用方带崩。
    /// </summary>
    public static string GetName(ushort code) =>
        CodeToName.TryGetValue(code, out var name) ? name : $"0x{code:X2}";

    /// <summary>
    /// 虚拟键码 → 修饰键标志。左右两侧的分离码与「不分左右」的聚合码都要认：低层钩子给分离码，
    /// 而 GetAsyncKeyState 重同步时用聚合码更省事。Win 键没有聚合码，故那一行只有左右两个。
    /// </summary>
    public static ModifierKeys? ToModifier(ushort code) => code switch
    {
        VK_LCONTROL or VK_RCONTROL or VK_CONTROL => ModifierKeys.Ctrl,
        VK_LMENU or VK_RMENU or VK_MENU => ModifierKeys.Alt,
        VK_LSHIFT or VK_RSHIFT or VK_SHIFT => ModifierKeys.Shift,
        VK_LWIN or VK_RWIN => ModifierKeys.Win,
        _ => null,
    };

    /// <summary>
    /// 某个修饰键对应的左右两个虚拟键码。只认单个标志位——传入组合值会落到 <c>_</c> 得空数组，
    /// 因为「Ctrl|Alt 的左右两侧」不是两个码而是四个，调用方须自己逐位拆。
    /// </summary>
    public static ushort[] GetBothSides(ModifierKeys modifier) => modifier switch
    {
        ModifierKeys.Ctrl => [VK_LCONTROL, VK_RCONTROL],
        ModifierKeys.Alt => [VK_LMENU, VK_RMENU],
        ModifierKeys.Shift => [VK_LSHIFT, VK_RSHIFT],
        ModifierKeys.Win => [VK_LWIN, VK_RWIN],
        _ => [],
    };

    /// <summary>
    /// 修饰键的 Set 1 扫描码（make code）与扩展位。与 <see cref="GetBothSides"/> 同处，因为
    /// 「哪些是扩展键」和「左右两侧各是哪个码」是同一类知识，分开放必然有一天只改一处。
    /// </summary>
    /// <remarks>
    /// 写死而不调 MapVirtualKeyW，是为了让 <see cref="InputBuilder"/> 保持纯函数——换成运行时
    /// 查询后断言只剩得下 <c>wScan != 0</c>。代价（某布局的修饰键扫描码不同则失真）由
    /// <c>VirtualKeyTests.ScanCodeTable_AgreesWithWhatTheOperatingSystemItselfReports</c> 挪到看得见
    /// 的地方：跑测试的机器上表若与系统不符便转红。
    /// 表值取自 <c>MapVirtualKeyW(vk, MAPVK_VK_TO_VSC_EX)</c> 实测。右 Shift 不是扩展键
    /// （0x0036，高字节为 0），左右 Ctrl/Alt 却是一侧扩展一侧不扩展——看着对称就照抄正是栽在这里。
    /// Win 键左右皆扩展。
    /// </remarks>
    public static bool TryGetScanCode(ushort virtualKey, out ushort scanCode, out bool isExtended)
    {
        // 命中与否由 switch 直接给出，不拿 scanCode != 0 反推：0x00 眼下不是任何修饰键的合法扫描码，
        // 但那是这张表当下的形状、不是不变量——哪天写进一个合法的 0 码，反推法会静默返回 false，
        // 而调用方只会看到「这个键没在表里」。
        (scanCode, isExtended, var found) = virtualKey switch
        {
            VK_LSHIFT => ((ushort)0x2A, false, true),
            VK_RSHIFT => ((ushort)0x36, false, true),
            VK_LCONTROL => ((ushort)0x1D, false, true),
            VK_RCONTROL => ((ushort)0x1D, true, true),
            VK_LMENU => ((ushort)0x38, false, true),
            VK_RMENU => ((ushort)0x38, true, true),
            VK_LWIN => ((ushort)0x5B, true, true),
            VK_RWIN => ((ushort)0x5C, true, true),
            _ => ((ushort)0, false, false),
        };

        return found;
    }

    private static (string Name, ushort Code)[] BuildKeyTable()
    {
        var table = new List<(string Name, ushort Code)>
        {
            ("Space", 0x20),
            ("Enter", 0x0D),
            ("Tab", 0x09),
            ("Escape", 0x1B),
            ("Backspace", 0x08),
            ("Insert", 0x2D),
            ("Delete", 0x2E),
            ("Home", 0x24),
            ("End", 0x23),
            ("PageUp", 0x21),
            ("PageDown", 0x22),
            ("Left", 0x25),
            ("Up", 0x26),
            ("Right", 0x27),
            ("Down", 0x28),
            ("CapsLock", 0x14),
            ("`", 0xC0),
            ("-", 0xBD),
            ("=", 0xBB),
            ("[", 0xDB),
            ("]", 0xDD),
            ("\\", 0xDC),
            (";", 0xBA),
            ("'", 0xDE),
            (",", 0xBC),
            (".", 0xBE),
            ("/", 0xBF),
        };

        // 字母与数字的 VK 码就等于其 ASCII 码，F 键从 VK_F1 起连续排布。
        for (var c = 'A'; c <= 'Z'; c++)
        {
            table.Add((c.ToString(), c));
        }

        for (var d = '0'; d <= '9'; d++)
        {
            table.Add((d.ToString(), d));
        }

        for (var f = 1; f <= 24; f++)
        {
            table.Add(($"F{f}", (ushort)(0x70 + f - 1)));
        }

        return [.. table];
    }
}
