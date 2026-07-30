using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Governance.Permissions;

/// <summary>
/// Defines JSON serialization behavior for permissions.
/// </summary>
internal static class PermissionsJson
{
    /// <summary>
    /// Identifies the options permissions JSON.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
