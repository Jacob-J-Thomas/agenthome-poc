namespace EmbodySense.Core.Persistence.Loops;

public sealed class UnsupportedCustomLoopRunDiscoveryIndexSchemaException : FormatException
{
    public UnsupportedCustomLoopRunDiscoveryIndexSchemaException(int schemaVersion)
        : base($"The custom loop run discovery index schema version {schemaVersion} is unsupported in this pre-1.0 build. Delete `.custom-loop-run-index.json` and retry the operation.")
    {
    }
}
