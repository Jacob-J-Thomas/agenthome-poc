using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Governance.Permissions;

internal static class PermissionsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
