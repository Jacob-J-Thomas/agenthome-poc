namespace EmbodySense.Core.Application.Loops;

public sealed class UnsupportedCustomLoopRunDiscoveryIndexSchemaException : FormatException
{
    public UnsupportedCustomLoopRunDiscoveryIndexSchemaException(int schemaVersion)
        : this(schemaVersion, null, null)
    {
    }

    public UnsupportedCustomLoopRunDiscoveryIndexSchemaException(int schemaVersion, string? context, Exception? innerException = null)
        : base(FormatMessage(schemaVersion, context), innerException)
    {
        SchemaVersion = schemaVersion;
    }

    public int SchemaVersion { get; }

    private static string FormatMessage(int schemaVersion, string? context)
    {
        var guidance = $"The custom loop run discovery index schema version {schemaVersion} is unsupported in this pre-1.0 build. Delete `.custom-loop-run-index.json` and retry the operation.";
        if (string.IsNullOrWhiteSpace(context))
        {
            return guidance;
        }

        var trimmedContext = context.Trim();
        return trimmedContext.Contains(guidance, StringComparison.Ordinal) ? trimmedContext : $"{trimmedContext} {guidance}";
    }
}
