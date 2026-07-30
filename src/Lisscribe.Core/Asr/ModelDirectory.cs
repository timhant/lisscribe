namespace Lisscribe.Core.Asr;

/// <summary>
/// 「这两个模型目录是不是同一处」的唯一判法。
///
/// <para>
/// 相对路径按 <see cref="AppContext.BaseDirectory"/> 展开，与两个 ModelPaths 的
/// <c>Resolve</c> 用的是同一套规则——开机自启时当前工作目录是 system32，按 CWD 展开必错。
/// </para>
///
/// <para>
/// 自己写字符串比较的后果：<c>"./x"</c> 与 <c>"x"</c> 会被判成改过，于是每次保存配置都弹一句
/// 「重启后生效」，喊到用户不再看那行提示为止。
/// </para>
/// </summary>
public static class ModelDirectory
{
    public static string Canonical(string dir) => Path.GetFullPath(dir, AppContext.BaseDirectory);

    public static bool SamePlace(string a, string b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 路径里有没有 ASCII 之外的字符。**这一问决定原生加载器认不认这条路**，
    /// 见 <see cref="NativeModelDirectoryScope"/>。
    /// </summary>
    public static bool ContainsNonAscii(string path) => path.Any(c => c > 0x7F);
}
