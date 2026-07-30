namespace Lisscribe.Core.Asr;

/// <summary>
/// SenseVoice 模型文件定位。
/// 文件名来自 sherpa-onnx 官方包 sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17：
/// model.int8.onnx (229MB) + tokens.txt (312KB)。
/// 该包内还有 model.onnx (895MB, fp32)，不用它：int8 实测 RTF 0.021 已绰绰有余，
/// 且 ONNX Runtime CPU EP 上 fp32 只多占内存不提精度。
/// </summary>
public sealed record AsrModelPaths(string ModelFile, string TokensFile)
{
    public const string ModelFileName = "model.int8.onnx";
    public const string TokensFileName = "tokens.txt";

    /// <summary>
    /// 覆盖模型目录的环境变量名。测试与生产共用这一个常量，见 <see cref="EffectiveModelDir"/>。
    /// </summary>
    public const string ModelDirEnvironmentVariable = "LISSCRIBE_MODEL_DIR";

    /// <summary>
    /// 定下本项目的模型目录约定，一句话：模型放在 exe 旁边，开发与发行同一套布局。
    /// </summary>
    /// <remarks>
    /// 唯一的布局：<c>lisscribe.exe</c> 与 <c>models/sense-voice/</c> 同级，配置里的默认值
    /// <c>./models/sense-voice</c> 按 <see cref="AppContext.BaseDirectory"/> 展开恰好命中
    /// （见 <see cref="Resolve"/>；按当前工作目录展开则会在开机自启时落到 system32）。
    /// 权重许可不许打进发行包，故那个目录由用户跑取模型脚本自己填。开发时同理：构建输出统一在
    /// 仓库根的 <c>build\&lt;项目名&gt;\&lt;配置&gt;\</c>，把 <c>models/</c> 拷进去即可。
    /// <para>
    /// 环境变量留着，但降级为逃生口：模型放在别处、几个副本共用一份权重时用它，不再是开发的
    /// 必经之路。覆盖优先于配置——反过来的话，用户显式设了变量却因为配置里有值而被静默忽略，
    /// 排查时看不出任何线索。同理，覆盖生效后不再回落：指着一个不存在的目录就该报「模型缺失」，
    /// 而不是悄悄用了别处那份。
    /// </para>
    /// </remarks>
    /// <param name="configuredDir">配置文件里的 <c>asr.modelDir</c>。</param>
    /// <param name="environmentOverride">
    /// <see cref="ModelDirEnvironmentVariable"/> 的当前值。取值由调用方做，本方法保持纯函数——
    /// 否则这几条测试就得改进程环境变量，而那是跨测试互相干扰的经典来源。
    /// </param>
    public static string EffectiveModelDir(string configuredDir, string? environmentOverride) =>
        string.IsNullOrWhiteSpace(environmentOverride) ? configuredDir : environmentOverride;

    /// <summary>
    /// 相对路径按 <see cref="AppContext.BaseDirectory"/>（而非当前工作目录）展开：配置里的默认值是
    /// "./models/sense-voice"，而托盘程序被开机自启拉起时工作目录是 system32，按 CWD 解析必然
    /// 找不到模型。
    /// </summary>
    public static AsrModelPaths Resolve(string modelDir) =>
        Under(Path.GetFullPath(modelDir, AppContext.BaseDirectory));

    /// <summary>
    /// 按给定根拼两个路径，不做任何展开。根为空串时拼出来就是纯文件名——
    /// <see cref="NativeModelDirectoryScope"/> 切了工作目录时要的正是这个。
    /// </summary>
    public static AsrModelPaths Under(string root) =>
        new(Path.Combine(root, ModelFileName), Path.Combine(root, TokensFileName));

    public bool IsComplete => File.Exists(ModelFile) && File.Exists(TokensFile);

    /// <summary>
    /// 缺失文件的人话描述，直接显示给用户，也会原样进入配置校验的错误信息。
    /// 必须同时点名「少了哪个文件」与「在哪找的」——只说「模型未找到」会让用户对着一个自己
    /// 从未见过的目录猜路径。
    /// </summary>
    public string MissingDescription
    {
        get
        {
            var missing = new List<string>();

            if (!File.Exists(ModelFile))
            {
                missing.Add(ModelFileName);
            }

            if (!File.Exists(TokensFile))
            {
                missing.Add(TokensFileName);
            }

            return missing.Count == 0
                ? string.Empty
                : $"缺少模型文件：{string.Join("、", missing)}（目录 {Path.GetDirectoryName(ModelFile)}）。" +
                  "运行 scripts/fetch-models.ps1 下载，或把 asr.modelDir 指向已有的模型目录。";
        }
    }

    /// <summary>
    /// 廉价的完整性自检，返回 null 表示「看起来没问题」。文件不存在时也返回 null——那是
    /// <see cref="MissingDescription"/> 的活，两条错误路径别抢话。
    /// 做法与理由见 <see cref="OnnxIntegrity"/>（两档引擎共用那一份，别在这儿再写一遍）。
    /// </summary>
    public string? DescribeCorruption()
    {
        if (!IsComplete)
        {
            return null;
        }

        var damage = OnnxIntegrity.DescribeDamage(ModelFile, ModelFileName)
            ?? DescribeTokensDamage(TokensFile);

        return damage is null
            ? null
            : $"模型文件疑似损坏：{damage}。" +
              "多半是下载中断或拷贝未完成。请删除后重跑 scripts/fetch-models.ps1 重新获取。";
    }

    private static string? DescribeTokensDamage(string path)
    {
        // 词表是纯文本，没有结构可校验；但磁盘写满、拷贝中断最典型的产物就是 0 字节文件，
        // 而空词表会让原生层在建解码器时崩掉。能廉价查的只有这一条，就查这一条。
        var info = new FileInfo(path);

        return info.Length == 0 ? $"{TokensFileName} 是空文件" : null;
    }

}
