namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Represents an unsupported custom loop run discovery index schema exception.
/// </summary>
public sealed class UnsupportedCustomLoopRunDiscoveryIndexSchemaException : FormatException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedCustomLoopRunDiscoveryIndexSchemaException"/> type.
    /// </summary>
    /// <param name="schemaVersion">The schema version.</param>
    public UnsupportedCustomLoopRunDiscoveryIndexSchemaException(int schemaVersion)
        : this(schemaVersion, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedCustomLoopRunDiscoveryIndexSchemaException"/> type.
    /// </summary>
    /// <param name="schemaVersion">The schema version.</param>
    /// <param name="context">The context.</param>
    /// <param name="innerException">The inner exception.</param>
    public UnsupportedCustomLoopRunDiscoveryIndexSchemaException(int schemaVersion, string? context, Exception? innerException = null)
        : base(FormatMessage(schemaVersion, context), innerException)
    {
        SchemaVersion = schemaVersion;
    }

    /// <summary>
    /// Gets the schema version.
    /// </summary>
    /// <value>The schema version.</value>
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
