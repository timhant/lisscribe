using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lisscribe.Core.Config;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(LisscribeConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
