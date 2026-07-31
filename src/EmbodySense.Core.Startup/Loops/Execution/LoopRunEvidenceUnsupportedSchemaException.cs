namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>
/// Reports that persisted loop-run discovery evidence uses an unsupported schema and requires
/// explicit cleanup or reinitialization rather than automatic compatibility handling.
/// </summary>
public sealed class LoopRunEvidenceUnsupportedSchemaException : InvalidOperationException
{
    /// <summary>
    /// Creates an interface-facing unsupported-schema failure with cleanup guidance.
    /// </summary>
    /// <param name="message">The non-null diagnostic supplied to the hosting interface.</param>
    public LoopRunEvidenceUnsupportedSchemaException(string message)
        : base(message ?? throw new ArgumentNullException(nameof(message)))
    {
    }

    internal LoopRunEvidenceUnsupportedSchemaException(Exception innerException)
        : base(innerException?.Message ?? throw new ArgumentNullException(nameof(innerException)), innerException)
    {
    }
}
