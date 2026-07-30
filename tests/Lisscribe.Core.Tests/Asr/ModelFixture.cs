using Lisscribe.Core.Asr;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 真模型测试的前置条件。模型 229MB 不入 Git（许可 + 体积），
/// CI 缓存未命中时这些测试应 Skip 而非 Fail —— 否则一次缓存失效就把 PR 全打红。
/// </summary>
public static class ModelFixture
{
    /// <summary>官方压缩包解包后的原始目录名，见 scripts/fetch-models.ps1。</summary>
    private const string OfficialArchiveDirName = "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17";

    private static readonly Lazy<string> ResolvedModelDir = new(ProbeModelDir);

    public static string ModelDir => ResolvedModelDir.Value;

    public static bool IsAvailable => IsUsableModelDir(ModelDir);

    /// <summary>
    /// 「这个目录够不够跑真模型测试」——注意不等于 <see cref="AsrModelPaths.IsComplete"/>：
    /// 测试除了权重和词表，还要读 test_wavs/zh.wav。官方包里 test_wavs 是独立子目录，
    /// 手工拷贝时最容易漏；只查那两个文件的话，这种半全目录会把「模型缺失就 Skip」
    /// 变成「在 ReadPcm16Mono 里硬失败」，报错还指向一个和模型无关的地方。
    /// </summary>
    public static bool IsUsableModelDir(string dir) =>
        AsrModelPaths.Resolve(dir).IsComplete && File.Exists(WavPathIn(dir));

    public static string SkipReason
    {
        get
        {
            var paths = AsrModelPaths.Resolve(ModelDir);
            var reason = paths.IsComplete
                ? $"缺少 test_wavs/{ReferenceWavName}"
                : paths.MissingDescription;

            return $"模型不可用（{ModelDir}）：{reason} " +
                   $"运行 scripts/fetch-models.ps1 或设 {AsrModelPaths.ModelDirEnvironmentVariable} 后重试。";
        }
    }

    public static string TestWavPath => WavPathIn(ModelDir);

    private const string ReferenceWavName = "zh.wav";

    private static string WavPathIn(string dir) =>
        Path.Combine(Path.GetFullPath(dir, AppContext.BaseDirectory), "test_wavs", ReferenceWavName);

    /// <summary>
    /// 读 16-bit PCM 单声道 wav。sherpa-onnx 的 .NET API 没有 WaveReader 类，只能自己解 RIFF。
    ///
    /// 必须逐块遍历而非按固定偏移取 data：真实 wav 常在 fmt 与 data 之间夹 LIST/fact/bext 等块，
    /// 且 fmt 块本身有 16/18/40 三种长度（PCM / cbSize / WAVE_FORMAT_EXTENSIBLE）。
    /// </summary>
    public static (float[] Samples, int SampleRate) ReadPcm16Mono(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));

        if (ReadFourCc(reader) != "RIFF")
        {
            throw new InvalidDataException($"{path} 不是 RIFF 文件");
        }

        reader.ReadUInt32(); // RIFF 块长度，不可信（流式录音常写 0 或 0xFFFFFFFF），不用。
        if (ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException($"{path} 不是 WAVE 文件");
        }

        var sampleRate = 0;
        var channels = 0;
        var bitsPerSample = 0;

        // 每块头 8 字节（4 字节 id + 4 字节长度），剩不足 8 字节即文件结束。
        while (reader.BaseStream.Length - reader.BaseStream.Position >= 8)
        {
            var id = ReadFourCc(reader);
            var size = reader.ReadInt32();
            if (size < 0)
            {
                throw new InvalidDataException($"{path} 的 {id} 块长度非法：{size}");
            }

            // 块长度按字对齐：奇数长度后面跟一个填充字节，不算进 size。
            var padded = size + (size & 1);

            if (id == "fmt ")
            {
                reader.ReadInt16(); // audio format
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();
                Skip(reader, padded - 16);
            }
            else if (id == "data")
            {
                if (channels != 1 || bitsPerSample != 16)
                {
                    throw new NotSupportedException(
                        $"{path} 是 {channels} 声道 / {bitsPerSample} bit，本读取器只支持 16-bit 单声道");
                }

                // 截断文件（录制中途崩溃）里 size 可能超过实际剩余字节，按实际长度收。
                var bytes = reader.ReadBytes(size);
                var samples = new float[bytes.Length / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                }

                return (samples, sampleRate);
            }
            else
            {
                Skip(reader, padded);
            }
        }

        throw new InvalidDataException($"{path} 中没有 data chunk");
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        // 不用 ReadChars：它按 BinaryReader 的编码（默认 UTF-8）解码，
        // 遇到非 ASCII 字节的块 id 会多吞字节，把后续偏移全带偏。
        var bytes = reader.ReadBytes(4);
        return bytes.Length < 4
            ? string.Empty
            : System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static void Skip(BinaryReader reader, int count)
    {
        if (count > 0)
        {
            reader.BaseStream.Seek(count, SeekOrigin.Current);
        }
    }

    /// <summary>
    /// 环境变量优先且不再回退——显式指定却悄悄用了别处的模型，
    /// 比直接 Skip 更难排查。其余按 fetch-models.ps1 的产物、再按官方压缩包原名找。
    ///
    /// <para>
    /// 变量名取自 <see cref="AsrModelPaths.ModelDirEnvironmentVariable"/>，与生产端
    /// 同一个常量。**曾经这里是硬编码的字符串，而生产端根本不认这个变量**：
    /// 开发者设了它，测试跑真模型，双击起来的程序却报模型缺失。
    /// </para>
    /// </summary>
    private static string ProbeModelDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable(AsrModelPaths.ModelDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var models = Path.Combine(FindRepoRoot(), "models");
        var canonical = Path.Combine(models, "sense-voice");
        var archive = Path.Combine(models, OfficialArchiveDirName);

        // 探测与可用性判定必须用同一把尺子，否则会挑中一个「有权重没 wav」的目录，
        // 然后在 IsAvailable 上判 false —— 明明隔壁那个官方解包目录是全的。
        return IsUsableModelDir(canonical) || !IsUsableModelDir(archive)
            ? canonical
            : archive;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "lisscribe.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
