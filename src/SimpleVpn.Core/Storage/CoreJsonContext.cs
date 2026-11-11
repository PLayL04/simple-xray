using System.Text.Json.Serialization;
using SimpleVpn.Core.Models;

namespace SimpleVpn.Core.Storage;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProfilesDocument))]
[JsonSerializable(typeof(RuntimeState))]
[JsonSerializable(typeof(SimpleVpn.Core.Routing.RoutingPreset))]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}
