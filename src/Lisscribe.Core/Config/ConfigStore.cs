using System.Text.Json;

namespace Lisscribe.Core.Config;

public enum ConfigLoadOutcome { Ok, CreatedDefault, RecoveredFromCorrupt }

/// <summary>
/// config.json 的读写。写入走「临时文件 + 原子替换」。
/// </summary>
public sealed class ConfigStore(string path)
{
    private readonly string _path = path;
    private string TempPath => _path + ".tmp";
    private string BadPath => _path + ".bad";

    public ConfigLoadOutcome LastOutcome { get; private set; } = ConfigLoadOutcome.Ok;

    public LisscribeConfig Load()
    {
        if (!File.Exists(_path))
        {
            LastOutcome = ConfigLoadOutcome.CreatedDefault;

            return LisscribeConfig.CreateDefault();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.LisscribeConfig);

            // 构造器式源生成器对缺失的引用类型参数一律填 null，不抛 JsonException——
            // 「{}」「只填一段」这类结构不完整的 JSON 会被反序列化「成功」，
            // 但携带 null 子段。此处必须逐段校验，否则调用方首次取字段即 NRE，且无告警。
            if (config is null || HasNullSection(config))
            {
                return RecoverFromCorrupt(json);
            }

            LastOutcome = ConfigLoadOutcome.Ok;

            return config;
        }
        catch (JsonException)
        {
            return RecoverFromCorrupt(File.ReadAllText(_path));
        }
    }

    public void Save(LisscribeConfig config)
    {
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.LisscribeConfig);

        File.WriteAllText(TempPath, json);

        try
        {
            // File.Replace 要求目标已存在，否则抛 FileNotFoundException。
            // 全新安装的首次保存必须走 Move 分支——漏掉此分支是隐蔽的必崩缺陷。
            if (!File.Exists(_path))
            {
                File.Move(TempPath, _path);
            }
            else
            {
                File.Replace(TempPath, _path, destinationBackupFileName: null);
            }
        }
        catch
        {
            // 交换失败（目标被锁定/权限变更/目标路径实为目录……）必须原样上抛让调用方知晓，
            // 但不得把 .tmp 留在磁盘——否则「原子写入」的承诺名存实亡。
            if (File.Exists(TempPath))
            {
                File.Delete(TempPath);
            }

            throw;
        }
    }

    private static bool HasNullSection(LisscribeConfig config) =>
        config.Hotkey is null
        || config.Asr is null
        || config.Output is null
        || config.App is null;

    private LisscribeConfig RecoverFromCorrupt(string rawContent)
    {
        File.WriteAllText(BadPath, rawContent);

        LastOutcome = ConfigLoadOutcome.RecoveredFromCorrupt;

        return LisscribeConfig.CreateDefault();
    }
}
