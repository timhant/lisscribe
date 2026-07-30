namespace Lisscribe.Core.Asr;

/// <summary>
/// 「原生识别器没建起来」这句错误怎么说。抽成纯函数，是因为它承载的判断有区分力
/// 而组合根里立不起测试。
/// </summary>
public static class NativeRecognizerFailure
{
    /// <summary>
    /// <b>不猜原因。</b>非 ASCII 路径曾经是头号嫌疑，<see cref="NativeModelDirectoryScope"/>
    /// 把那条路修好之后就不再是了；剩下的可能性没有一条能从托管侧分辨出来，
    /// 硬塞一句猜测只会把人往错方向带。能给的实话只有两样：在哪找的，以及真实原因在哪儿看。
    /// </summary>
    /// <param name="modelDir">配置里的模型目录，可以是相对路径。</param>
    public static string Describe(string modelDir)
    {
        // **必须展开再报**：配置里那个 "./models/qwen3-asr" 对用户毫无信息量，
        // 而真正出问题的往往是 exe 所在的那一段。
        var full = ModelDirectory.Canonical(modelDir);

        return $"sherpa-onnx 未能创建识别器，模型目录 {full}。" +
               "原生层把具体原因打在 stderr，而本程序没有控制台；" +
               "从命令行启动一次即可看到那几行。";
    }
}
