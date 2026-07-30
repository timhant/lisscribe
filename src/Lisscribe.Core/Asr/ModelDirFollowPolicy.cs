namespace Lisscribe.Core.Asr;

/// <summary>
/// 换引擎时，<c>asr.modelDir</c> 跟不跟着换。
///
/// <para>
/// 两档模型在两个目录，而配置里的模型目录只有一个。切档时目录一动不动的话，
/// 用户切完必然撞一次「缺少模型文件」——而他什么也没做错。
/// </para>
///
/// <para>
/// <b>只在用户没自定义过时才跟随。</b>目录若已被指到 <c>D:\my-models\sv</c>，那是他自己的安排，
/// 程序无从知道另一档的权重该在哪；此时擅自改写等于把用户填的东西悄悄抹掉。那种情形交给
/// 「模型缺失」提示去引导，那条提示里带着目录全路径。
/// </para>
/// </summary>
public static class ModelDirFollowPolicy
{
    /// <returns>保存后应当写进配置的模型目录。</returns>
    public static string Follow(string oldEngine, string newEngine, string currentModelDir)
    {
        if (string.Equals(oldEngine, newEngine, StringComparison.Ordinal))
        {
            return currentModelDir;
        }

        // 引擎名非法归 ConfigValidator 报，这里别抢话，更不能拿一个猜出来的目录盖掉用户的值。
        if (!AsrEngines.IsSupported(oldEngine) || !AsrEngines.IsSupported(newEngine))
        {
            return currentModelDir;
        }

        return ModelDirectory.SamePlace(currentModelDir, AsrEngines.DefaultModelDir(oldEngine))
            ? AsrEngines.DefaultModelDir(newEngine)
            : currentModelDir;
    }
}
