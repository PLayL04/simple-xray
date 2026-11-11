using System.Text.Json.Serialization;
using SimpleXray.Core.Models;

namespace SimpleXray.Core.Storage;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProfilesDocument))]
[JsonSerializable(typeof(RuntimeState))]
[JsonSerializable(typeof(SimpleXray.Core.Routing.RoutingPreset))]
[JsonSerializable(typeof(SimpleXray.Core.Geo.GeoState))]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}
